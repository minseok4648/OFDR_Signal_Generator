"""Reproducible, full-record NoticeData analysis; production UI is not modified.

Run with x64 Python. NumPy/SciPy/Matplotlib may be supplied in .analysis_deps.
Native Math.NET MKL FFT is required, with no processing fallback.
"""
from __future__ import annotations

import argparse
import ctypes as ct
import hashlib
import itertools
import json
import os
from pathlib import Path
import platform
import sys
import time
import traceback

ROOT = Path(__file__).resolve().parents[2]
if (ROOT / ".analysis_deps").exists():
    sys.path.insert(0, str(ROOT / ".analysis_deps"))
os.environ.setdefault("MPLCONFIGDIR", str(ROOT / ".analysis_cache/matplotlib"))
import numpy as np
import pandas as pd
import scipy
from scipy.signal import find_peaks
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt


def write(path, obj):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(obj, indent=2, ensure_ascii=False, allow_nan=False), encoding="utf-8")


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


class NativeMkl:
    """Cdecl ABI documented by Math.NET src/NativeProviders/MKL/fft.cpp."""
    def __init__(self, path):
        if ct.sizeof(ct.c_void_p) != 8:
            raise RuntimeError("x64 Python is required")
        self.path = path.resolve()
        self.dll = ct.CDLL(str(self.path))
        self.dll.z_fft_create.argtypes = [ct.POINTER(ct.c_void_p), ct.c_int, ct.c_double, ct.c_double]
        self.dll.z_fft_create.restype = ct.c_int
        self.dll.x_fft_free.argtypes = [ct.POINTER(ct.c_void_p)]
        self.dll.x_fft_free.restype = ct.c_int
        for name in ["z_fft_forward", "z_fft_backward"]:
            getattr(self.dll, name).argtypes = [ct.c_void_p, ct.c_void_p]
            getattr(self.dll, name).restype = ct.c_int
        self.dll.set_max_threads.argtypes = [ct.c_int]
        self.dll.set_max_threads(1)
        self.plans = {}
        self.calls = 0

    def fft(self, values, inverse=False, n=None):
        n = len(values) if n is None else n
        result = np.zeros(n, dtype=np.complex128)
        result[:min(n, len(values))] = values[:n]
        if n not in self.plans:
            handle = ct.c_void_p()
            status = self.dll.z_fft_create(ct.byref(handle), n, 1., 1. / n)
            if status:
                raise RuntimeError(f"MKL create({n}) failed: {status}")
            self.plans[n] = handle
        fn = self.dll.z_fft_backward if inverse else self.dll.z_fft_forward
        status = fn(self.plans[n], result.ctypes.data_as(ct.c_void_p))
        if status:
            raise RuntimeError(f"MKL transform({n}) failed: {status}")
        self.calls += 1
        return result

    def close(self):
        for handle in self.plans.values():
            self.dll.x_fft_free(ct.byref(handle))
        self.plans.clear()


def read_source(path):
    before = path.stat()
    header, fields = [], {}
    with path.open(encoding="utf-8-sig") as stream:
        for line in stream:
            header.append(line.rstrip("\r\n"))
            parts = line.rstrip("\r\n").split(",")
            if parts[0] == "TIME":
                if parts != ["TIME", "CH1", "CH2", "CH3", "CH4"]:
                    raise ValueError(f"Unexpected channels: {parts}")
                break
            if parts[0]:
                fields[parts[0]] = parts[1:]
        else:
            raise ValueError("TIME header missing")
    # An explicit CH4 converter also handles mixed finite/inf parser chunks.
    # Python float accepts only numeric tokens, so malformed strings still fail.
    a = pd.read_csv(path, skiprows=len(header), header=None,
                    converters={i: float for i in range(5)}).to_numpy()
    sha = digest(path)
    after = path.stat()
    if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        raise RuntimeError("Source changed during read")
    n = int(float(fields["Record Length"][0]))
    dt = float(fields["Sample Interval"][0])
    if a.shape != (n, 5) or not np.isclose(dt, 2e-8, rtol=0, atol=1e-16):
        raise ValueError(f"Header/data mismatch: {a.shape}, header={n}, dt={dt}")
    if not np.isfinite(a[:, :3]).all():
        raise ValueError("TIME/CH1/CH2 contains nonfinite values")
    if np.isnan(a[:, 3:]).any():
        raise ValueError("NaN is not a supported oscilloscope infinity token")
    time_error = float(np.max(np.abs(np.diff(a[:, 0]) - dt)))
    if time_error > dt * 1e-4:
        raise ValueError(f"Nonuniform TIME axis: {time_error}")
    metadata = dict(path=str(path.resolve()), folder=path.parent.name, filename=path.name,
                    sha256=sha, bytes=before.st_size, mtime_ns=before.st_mtime_ns,
                    header_lines=header, sample_interval_s=dt, sample_rate_hz=1/dt,
                    declared_samples=n, numeric_rows=len(a), duration_s=n*dt,
                    first_time_s=float(a[0, 0]), last_time_s=float(a[-1, 0]),
                    maximum_time_step_error_s=time_error,
                    invalid_measurement_samples=int(np.sum(~np.isfinite(a[:, 4]))),
                    invalid_auxiliary_samples=int(np.sum(~np.isfinite(a[:, 3]))))
    return a, metadata


def edges_and_mask(a, c):
    edges = np.flatnonzero((a[:-1, 1] <= c["trigger_threshold_v"]) &
                           (a[1:, 1] > c["trigger_threshold_v"])) + 1
    first = int(edges[0]) if len(edges) else 0
    # UI first-edge-to-record-end is explicit; missing edge is recorded below.
    trigger = a[:, 2] > c["trigger_threshold_v"]
    changes = np.diff(np.r_[False, trigger, False].astype(np.int8))
    starts, ends = np.flatnonzero(changes == 1), np.flatnonzero(changes == -1)
    tail = int(round(c["mode_hop_exclusion_tail_s"] / c["sample_interval_s"]))
    excluded = np.zeros(len(a), dtype=bool)
    for left, right in zip(starts, ends):
        excluded[left:min(len(a), right + tail)] = True
    excluded |= ~np.isfinite(a[:, 4])
    keep = ~excluded
    keep[:first] = False
    indices = np.flatnonzero(keep)
    if not np.isfinite(a[indices, 3]).all():
        raise ValueError("CH3 contains infinity outside the predeclared CH2/CH4 exclusion mask")
    breaks = np.flatnonzero(np.diff(indices) > 1) + 1
    spans = np.split(indices, breaks)
    longest = max(spans, key=len)
    metrics = dict(sweep_rising_edges=edges.tolist(), first_sweep_index=first,
                   sweep_edge_found=bool(len(edges)), selected_raw_samples=len(a)-first,
                   mode_hop_high_samples=int(trigger[first:].sum()),
                   excluded_samples=int(excluded[first:].sum()),
                   retained_samples=len(indices), stitch_joins=len(breaks),
                   longest_contiguous_start=int(longest[0]),
                   longest_contiguous_end_exclusive=int(longest[-1]+1),
                   longest_contiguous_samples=len(longest),
                   masked_auxiliary_infinity_samples=int(np.sum(~np.isfinite(a[:, 3]))),
                   raw_ui_reader_accepts_auxiliary=bool(np.isfinite(a[:, 3]).all()))
    return indices, metrics


def phase_map(aux, fft, limit, dt):
    z = fft.fft(aux - aux.mean())
    n = len(z)
    z[1:(n+1)//2] *= 2
    z[n//2+1:] = 0
    z = fft.fft(z, inverse=True)
    phase = np.unwrap(np.angle(z))
    direction = 1 if phase[-1] >= phase[0] else -1
    phase *= direction
    # Exactly the UI record-high anchor rule, not simply diff(phase)>0.
    previous_max = np.maximum.accumulate(phase)
    accepted = np.r_[True, phase[1:] > previous_max[:-1]]
    anchors = np.flatnonzero(accepted)
    relative = phase[anchors] - phase[0]
    rejected = n-len(anchors)
    ratio = rejected / (n-1)
    mag = np.abs(z)
    increments = np.diff(phase)
    m = dict(sample_count=n, accepted_anchors=len(anchors), rejected_anchors=rejected,
             nonmonotonic_ratio=ratio, phase_qc_passed=ratio <= limit,
             phase_span_rad=float(relative[-1]), sweep_direction=direction,
             negative_increment_fraction=float(np.mean(increments <= 0)),
             median_aux_frequency_mhz=float(np.median(increments)/(2*np.pi*dt)/1e6),
             mean_aux_frequency_mhz=float(relative[-1]/((n-1)*dt)/(2*np.pi)/1e6),
             analytic_magnitude_p01_v=float(np.quantile(mag, .01)),
             analytic_magnitude_median_v=float(np.median(mag)))
    if len(relative) < 2 or relative[-1] < 2*np.pi:
        raise ValueError("Insufficient auxiliary phase span")
    return relative, anchors, m


def prepare(a, indices, fft, c):
    aux, sig = a[indices, 3], a[indices, 4]
    phase, anchors, m = phase_map(aux, fft, c["max_nonmonotonic_ratio"], c["sample_interval_s"])
    grid = np.linspace(0, phase[-1], len(indices))
    linearized = np.interp(grid, phase, sig[anchors])
    m.update(measurement_ac_rms_v=float(np.std(sig)), auxiliary_ac_rms_v=float(np.std(aux)),
             measurement_unique_levels=int(len(np.unique(sig))),
             measurement_p01_v=float(np.quantile(sig, .01)), measurement_p99_v=float(np.quantile(sig, .99)))
    return dict(values=linearized, phase_span=m["phase_span_rad"], metrics=m)


def pair_spectra(ref, sig, fft):
    common_span = min(ref["phase_span"], sig["phase_span"])
    n = min(len(ref["values"]), len(sig["values"]))
    target = np.linspace(0, common_span, n)
    spectra = []
    for source in [ref, sig]:
        values = source["values"]
        if len(values) != n or abs(source["phase_span"]-common_span) > source["phase_span"]*1e-12:
            values = np.interp(target, np.linspace(0, source["phase_span"], len(values)), values)
        spectra.append(fft.fft(values))
    return spectra[0], spectra[1], common_span


def profile(reference, signal, span, fft, c):
    size, hop = c["segment_size"], c["segment_hop"]
    padded = 1 << (size*c["zero_padding_factor"]-1).bit_length()
    dx = np.pi*c["auxiliary_path_difference_m"]/span
    bandwidth = 299792458*span/(2*np.pi*c["auxiliary_group_index"]*c["auxiliary_path_difference_m"])/1e9
    per_lag = bandwidth/padded
    maxlag = max(1, min(padded//2-2, int(np.ceil(c["maximum_spectral_shift_ghz"]/per_lag))))
    last = len(reference)//2-size
    starts = np.arange(0, last+1, hop, dtype=int)
    if starts[-1] != last:
        starts = np.r_[starts, last]
    centers = (starts+(size-1)/2)*dx
    keep = (centers >= c.get("analysis_start_m", 0)) & (centers <= c["measurement_cable_m"])
    starts, centers = starts[keep], centers[keep]
    window = np.hanning(size)
    lags = np.arange(-maxlag, maxlag+1)
    corridx = lags % padded
    rows = []
    for start, center in zip(starts, centers):
        traces = []
        energies = []
        for spectrum in [reference, signal]:
            trace = np.abs(fft.fft(spectrum[start:start+size]*window, inverse=True, n=padded))
            trace -= trace.mean()
            energies.append(float(trace @ trace))
            traces.append(fft.fft(trace))
        corr = fft.fft(traces[1]*traces[0].conj(), inverse=True).real
        denom = np.sqrt(energies[0]*energies[1])
        corr = corr/denom if denom > 1e-20 else np.zeros_like(corr)
        search = corr[corridx]
        k = int(np.argmax(search))
        lag = int(lags[k])
        left, middle, right = corr[(lag-1)%padded], corr[lag%padded], corr[(lag+1)%padded]
        den = left-2*middle+right
        offset = float(np.clip(.5*(left-right)/den, -.5, .5)) if abs(den) > 1e-20 else 0.
        # A separate maximum at least one unpadded lag away measures ambiguity.
        competitors = search[np.abs(lags-lag) >= c["zero_padding_factor"]]
        runner_up = float(competitors.max()) if len(competitors) else float(middle)
        shift = (lag+offset)*per_lag
        strain = shift*1e9/(299792458/(c["center_wavelength_nm"]*1e-9)*c["strain_coefficient"])*1e6
        rows.append((float(center), int(start), shift, strain, float(middle),
                     float(corr[0]), float(middle-runner_up), bool(k in (0, len(search)-1)),
                     bool(center-size*dx/2 >= 0 and center+size*dx/2 <= c["measurement_cable_m"])))
    frame = pd.DataFrame(rows, columns=["distance_m", "segment_start_bin", "shift_ghz", "apparent_strain_ue",
                                      "peak_correlation", "zero_lag_correlation", "peak_margin", "search_boundary", "window_inside_cable"])
    axis = dict(common_phase_span_rad=span, common_samples=len(reference), distance_m_per_bin=dx,
                gauge_length_m=size*dx, hop_m=hop*dx, sweep_span_ghz=bandwidth,
                ghz_per_padded_lag=per_lag, local_ifft_samples=padded,
                search_halfwidth_ghz=maxlag*per_lag, profile_samples=len(frame))
    return frame, axis


def distance_metrics(spectrum, span, c):
    dx = np.pi*c["auxiliary_path_difference_m"]/span
    amp = np.abs(spectrum[:len(spectrum)//2])
    x = np.arange(len(amp))*dx
    mask = (x >= .2) & (x <= c["measurement_cable_m"])
    peak = int(np.flatnonzero(mask)[np.argmax(amp[mask])])
    energy = amp**2
    p = np.abs(x-x[peak]) <= .1
    denominator = energy[mask].sum()
    # Robust 0.5 m-bin median slope, excluding the narrow dominant reflection.
    centers, dbmed = [], []
    norm = max(float(amp[mask].max()), 1e-300)
    db = 20*np.log10(np.maximum(amp, 1e-300)/norm)
    for left in np.arange(.5, c["measurement_cable_m"]-.5, .5):
        ix = (x >= left) & (x < left+.5) & (np.abs(x-x[peak]) > .2)
        if ix.sum() >= 10:
            centers.append(left+.25)
            dbmed.append(float(np.median(db[ix])))
    slope = float(np.polyfit(centers, dbmed, 1)[0])
    return dict(dominant_peak_m=float(x[peak]), dominant_peak_pm_0p1m_energy_fraction=float(energy[p & mask].sum()/denominator),
                median_db_slope_per_m=slope, distance_m_per_bin=dx), x, db


def controls(fft, c):
    rng = np.random.default_rng(7421)
    errors = []
    for n in [31, 32, 1024, 1000000]:
        x = rng.normal(size=n)
        native = fft.fft(x)
        truth = np.fft.fft(x)  # Independent verification only; never processing fallback.
        errors.append(dict(n=n, forward_max_relative_error=float(np.max(np.abs(native-truth))/np.max(np.abs(truth))),
                           roundtrip_max_error=float(np.max(np.abs(fft.fft(native, inverse=True).real-x)))))
    # Known optical spectral translation, circular (matching the UI's algorithm).
    n = 131072
    ref = rng.normal(size=n)
    shift_samples = 37
    sig = np.roll(ref, shift_samples)
    spectrum = fft.fft(ref)
    shifted = fft.fft(sig)
    # Deliberately select phase span so the requested 4096-bin window is 2 m.
    span = np.pi*c["auxiliary_path_difference_m"]*c["segment_size"]/2
    p, axis = profile(spectrum, shifted, span, fft, {**c, "measurement_cable_m": 5})
    expected = shift_samples*(299792458*span/(2*np.pi*c["auxiliary_group_index"]*c["auxiliary_path_difference_m"])/1e9)/n
    shift_error = float(np.max(np.abs(p.shift_ghz-expected)))
    same, _ = profile(spectrum, spectrum, span, fft, {**c, "measurement_cable_m": 5})
    zero_error = float(np.max(np.abs(same.shift_ghz)))
    # Independent dual-phase resampling of a known range tone.
    u = np.linspace(0, 1, n)
    phases = [span*(u+.03*(1-np.cos(4*np.pi*u))/(4*np.pi)),
              span*1.001*(u+.04*(1-np.cos(6*np.pi*u))/(6*np.pi))]
    common = np.linspace(0, span, n)
    peaks = []
    for phase in phases:
        values = np.cos(2*6/c["auxiliary_path_difference_m"]*phase+.4)
        uniform = np.interp(common, phase, values)
        peaks.append(int(np.argmax(np.abs(fft.fft(uniform)[:n//2])))*np.pi*c["auxiliary_path_difference_m"]/span)
    result = dict(native_fft=errors, known_translation_expected_ghz=expected,
                  known_translation_max_error_ghz=shift_error, identical_input_max_shift_ghz=zero_error,
                  known_6m_dual_phase_tone_peaks_m=peaks,
                  pass_all=bool(max(e["forward_max_relative_error"] for e in errors) < 1e-12 and
                                max(e["roundtrip_max_error"] for e in errors) < 1e-10 and
                                shift_error < axis["ghz_per_padded_lag"]*.2 and zero_error < 1e-10 and
                                max(abs(x-6) for x in peaks) < .002))
    if not result["pass_all"]:
        raise RuntimeError(f"Control failed: {result}")
    return result


def state(path):
    name = path.stem.lower()
    if name.startswith("heat"):
        return "Heating"
    if name.startswith("cool"):
        return "Cooling"
    return "Ref"


def summary_row(frame):
    p = frame[frame.window_inside_cable]
    return dict(median_abs_shift_ghz=float(p.shift_ghz.abs().median()),
                p90_abs_shift_ghz=float(p.shift_ghz.abs().quantile(.9)),
                median_peak_correlation=float(p.peak_correlation.median()),
                fraction_peak_correlation_ge_0p8=float((p.peak_correlation >= .8).mean()),
                fraction_abs_shift_lt_1ghz=float((p.shift_ghz.abs() < 1).mean()),
                median_peak_margin=float(p.peak_margin.median()),
                fraction_search_boundary=float(p.search_boundary.mean()))


def plot_group(folder, sources, pairdata, out, c):
    fig, axes = plt.subplots(2, 2, figsize=(14, 9), constrained_layout=True)
    for source in sources:
        x, db = source["plot"]
        take = (x <= c["measurement_cable_m"])
        # Each display is at most 10,000 points; calculations retain full precision.
        ix = np.flatnonzero(take)[::max(1, int(np.ceil(take.sum()/10000)))]
        axes[0, 0].plot(x[ix], db[ix], lw=.7, alpha=.8, label=source["path"].stem)
    axes[0, 0].set(xlabel=f"Distance (m), OPD={c['auxiliary_path_difference_m']:g} m", ylabel="FFT amplitude (dB, peak=0)", ylim=(-85, 3))
    axes[0, 0].legend(fontsize=7, ncol=2)
    for j, kind in enumerate(["within", "between"]):
        for pair in pairdata:
            if pair["kind"] != kind:
                continue
            p = pair["frame"]
            axes[0, 1].plot(p.distance_m, p.shift_ghz, color=["#1d4ed8", "#dc2626"][j], lw=.7, alpha=.35)
            axes[1, 0].plot(p.distance_m, p.peak_correlation, color=["#1d4ed8", "#dc2626"][j], lw=.7, alpha=.35)
    axes[0, 1].set(xlabel="Segment center (m)", ylabel="Spectral shift (GHz)")
    axes[1, 0].set(xlabel="Segment center (m)", ylabel="Peak normalized correlation", ylim=(0, 1.03))
    axes[1, 0].axhline(.8, color="black", ls=":", lw=.8)
    axes[0, 1].text(.02, .95, "Blue: same state; red: different states\nQC-failing results are diagnostic only", transform=axes[0, 1].transAxes, va="top", fontsize=9)
    qcs = [s["prepared"]["metrics"]["nonmonotonic_ratio"]*100 for s in sources]
    axes[1, 1].bar(np.arange(len(sources)), qcs, color="#0f766e")
    axes[1, 1].axhline(c["max_nonmonotonic_ratio"]*100, color="#dc2626", ls="--")
    axes[1, 1].set(xticks=np.arange(len(sources)), xticklabels=[s["path"].stem for s in sources], ylabel="Rejected phase anchors (%)")
    axes[1, 1].tick_params(axis="x", rotation=35)
    for ax in axes.flat:
        ax.grid(alpha=.2)
    fig.suptitle(f"{folder}: full record, UI-equivalent stitching | segment {c['segment_size']} / hop {c['segment_hop']}", fontsize=13)
    fig.savefig(out/f"{folder}_overview.png", dpi=160)
    plt.close(fig)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", required=True, type=Path)
    ap.add_argument("--output", required=True, type=Path)
    args = ap.parse_args()
    c = json.loads(args.config.read_text(encoding="utf-8-sig"))
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    write(out/"config.json", c)
    started = time.perf_counter()
    metrics = dict(status="RUNNING", source_numeric_rows=0)
    write(out/"metrics.json", metrics)
    (out/"summary.md").write_text("# RUNNING\n\nFull-record Python analysis started.\n", encoding="utf-8")
    fft = None
    try:
        fft = NativeMkl(ROOT/c["native_mkl_dll"])
        write(out/"environment.json", dict(python=sys.version, executable=sys.executable,
              platform=platform.platform(), numpy=np.__version__, scipy=scipy.__version__, pandas=pd.__version__,
              matplotlib=matplotlib.__version__, native_mkl_path=str(fft.path), native_mkl_sha256=digest(fft.path),
              native_mkl_threads=1, script_sha256=digest(Path(__file__))))
        write(out/"controls.json", controls(fft, c))
        manifest, source_rows, pair_rows, region_rows = [], [], [], []
        global_hashes = {}
        for folder in c["folders"]:
            print(f"GROUP {folder}", flush=True)
            group = out/folder
            group.mkdir()
            sources = []
            for number, path in enumerate(sorted((Path(c["data_root"])/folder).glob("*.csv"))):
                a, m = read_source(path)
                metrics["source_numeric_rows"] += len(a)
                indices, selection = edges_and_mask(a, c)
                m.update(selection)
                m["state_by_filename"] = state(path)
                m["duplicate_of"] = global_hashes.get(m["sha256"])
                manifest.append(m)
                write(out/"dataset_manifest.json", manifest)
                if m["sha256"] in {s["metadata"]["sha256"] for s in sources}:
                    print(f"  DUPLICATE {path.name}", flush=True)
                    continue
                prepared = prepare(a, indices, fft, c)
                pmetrics = prepared["metrics"]
                lo, hi = selection["longest_contiguous_start"], selection["longest_contiguous_end_exclusive"]
                _, _, continuous_metrics = phase_map(a[lo:hi, 3], fft, c["max_nonmonotonic_ratio"], c["sample_interval_s"])
                pmetrics["longest_contiguous_nonmonotonic_ratio"] = continuous_metrics["nonmonotonic_ratio"]
                pmetrics["longest_contiguous_phase_qc_passed"] = continuous_metrics["phase_qc_passed"]
                spectrum = fft.fft(prepared["values"])
                dmetrics, x, db = distance_metrics(spectrum, prepared["phase_span"], c)
                raw_freq = np.arange(len(spectrum)//2)/(len(spectrum)*c["sample_interval_s"])
                raw_spectrum = fft.fft(a[indices, 4])[:len(spectrum)//2]
                np.savez_compressed(group/f"source{number}_preview.npz",
                                    distance_m=x[::max(1, int(np.ceil(len(x)/10000)))], post_k_db=db[::max(1, int(np.ceil(len(x)/10000)))],
                                    raw_frequency_hz=raw_freq[::max(1, int(np.ceil(len(raw_freq)/10000)))],
                                    raw_fft_abs=np.abs(raw_spectrum)[::max(1, int(np.ceil(len(raw_freq)/10000)))])
                row = dict(folder=folder, filename=path.name, state=state(path), sha256=m["sha256"],
                           duplicate_of=m["duplicate_of"], **selection, **pmetrics, **dmetrics)
                source_rows.append(row)
                write(group/f"source{number}_metrics.json", row)
                sources.append(dict(path=path, metadata=m, prepared=prepared, plot=(x, db)))
                global_hashes.setdefault(m["sha256"], str(path))
                print(f"  {path.name}: {len(a)} raw -> {len(indices)} retained, QC={pmetrics['nonmonotonic_ratio']:.6g}, peak={dmetrics['dominant_peak_m']:.4f}m", flush=True)
                del a, spectrum, raw_spectrum
            pairs = []
            for i, j in itertools.combinations(range(len(sources)), 2):
                ref, sig = sources[i], sources[j]
                kind = "within" if state(ref["path"]) == state(sig["path"]) else "between"
                name = f"pair_{i}_{j}"
                rs, ss, span = pair_spectra(ref["prepared"], sig["prepared"], fft)
                p, axis = profile(rs, ss, span, fft, c)
                p.to_csv(group/f"{name}_profile.csv", index=False, float_format="%.12g")
                row = dict(folder=folder, pair=name, ref=ref["path"].name, sig=sig["path"].name,
                           kind=kind, phase_qc_passed=bool(ref["prepared"]["metrics"]["phase_qc_passed"] and sig["prepared"]["metrics"]["phase_qc_passed"]),
                           **axis, **summary_row(p))
                pair_rows.append(row)
                pairs.append(dict(kind=kind, frame=p, row=row))
                write(group/f"{name}_metrics.json", row)
                for lo, hi in c["report_regions_m"]:
                    subset = p[(p.distance_m >= lo) & (p.distance_m < hi)]
                    if len(subset):
                        region_rows.append(dict(folder=folder, pair=name, kind=kind, start_m=lo, end_m=hi,
                                                n=len(subset), median_shift_ghz=float(subset.shift_ghz.median()),
                                                median_abs_shift_ghz=float(subset.shift_ghz.abs().median()),
                                                median_peak_correlation=float(subset.peak_correlation.median())))
                print(f"  {name} {kind} {len(p)} segments: median|shift|={row['median_abs_shift_ghz']:.3f} GHz, corr={row['median_peak_correlation']:.3f}", flush=True)
                del rs, ss
            # One real reverse-order check per group, independently recomputing correlation.
            ref, sig = sources[0], sources[-1]
            forward = next(p for p in pairs if p["row"]["pair"] == f"pair_0_{len(sources)-1}")
            rs, ss, span = pair_spectra(sig["prepared"], ref["prepared"], fft)
            reverse, _ = profile(rs, ss, span, fft, c)
            residual = float(np.max(np.abs(forward["frame"].shift_ghz.to_numpy()+reverse.shift_ghz.to_numpy())))
            write(group/"reverse_control.json", dict(pair=forward["row"]["pair"], maximum_antisymmetry_error_ghz=residual, pass_all=residual < 1e-7))
            if residual > 1e-7:
                raise RuntimeError(f"Reverse correlation failed in {folder}: {residual}")
            plot_group(folder, sources, pairs, out, c)
            pd.DataFrame(source_rows).to_csv(out/"source_metrics.csv", index=False)
            pd.DataFrame(pair_rows).to_csv(out/"pair_metrics.csv", index=False)
            pd.DataFrame(region_rows).to_csv(out/"region_metrics.csv", index=False)
            write(out/"progress.json", dict(completed_folder=folder, pairs=len(pair_rows), source_files=len(manifest)))
        metrics.update(status="PASS_EXECUTION", source_files=len(manifest), unique_source_hashes=len({m["sha256"] for m in manifest}),
                       analyzed_sources=len(source_rows), pairs=len(pair_rows),
                       phase_qc_passed_sources=sum(r["phase_qc_passed"] for r in source_rows),
                       phase_qc_passed_pairs=sum(r["phase_qc_passed"] for r in pair_rows),
                       native_fft_calls=fft.calls, elapsed_seconds=time.perf_counter()-started,
                       absolute_strain_calibrated=False, production_ui_modified=False)
        write(out/"metrics.json", metrics)
        (out/"summary.md").write_text("# PASS_EXECUTION\n\nFull numeric processing completed. Execution success is separate from phase QC and sensing validation.\n\n"
             +json.dumps(metrics, indent=2)+"\n\nSee source_metrics.csv, pair_metrics.csv, region_metrics.csv, per-pair profiles, and the report in docs/report.\n", encoding="utf-8")
        print(json.dumps(metrics, indent=2), flush=True)
    except BaseException as exc:
        metrics.update(status="FAIL", error=repr(exc), elapsed_seconds=time.perf_counter()-started)
        write(out/"metrics.json", metrics)
        (out/"summary.md").write_text("# FAIL\n\n"+traceback.format_exc(), encoding="utf-8")
        raise
    finally:
        if fft is not None:
            fft.close()


if __name__ == "__main__":
    main()
