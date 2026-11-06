"""Test physically specified processing candidates against independent repeats.

The supplied screenshot is a qualitative reference, never a numeric target.
Production UI/Core and existing experiment outputs are not modified.
"""
from __future__ import annotations
import argparse
import importlib.util
import itertools
import json
from pathlib import Path
import sys
import time
import traceback

spec = importlib.util.spec_from_file_location("notice", Path(__file__).with_name("Analyze-Notice50M.py"))
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)
np, pd, plt, ROOT, write = base.np, base.pd, base.plt, base.ROOT, base.write
from scipy.signal import butter, sosfiltfilt, find_peaks


def split_blocks(indices):
    return np.split(indices, np.flatnonzero(np.diff(indices) != 1)+1)


def indices_for(a, c, research, method):
    indices, selection = base.edges_and_mask(a, c)
    if method == "continuous_lowpass_direct":
        first = selection["first_sweep_index"]+research["continuous_relative_start_sample"]
        indices = np.arange(first, first+research["continuous_sample_count"])
        original, _ = base.edges_and_mask(a, c)
        if not np.isin(indices, original).all():
            raise ValueError("Predeclared continuous window crosses an excluded sample")
    elif method == "lowpass_direct_lead100us":
        high = a[:, 2] > c["trigger_threshold_v"]
        change = np.diff(np.r_[False, high, False].astype(np.int8))
        starts, ends = np.flatnonzero(change == 1), np.flatnonzero(change == -1)
        keep = np.zeros(len(a), dtype=bool)
        keep[indices] = True
        lead = int(round(research["lead_exclusion_s"]/c["sample_interval_s"]))
        for lo, hi in zip(starts, ends):
            keep[max(0, lo-lead):lo] = False
        indices = np.flatnonzero(keep)
    return indices, selection


def prepare_candidate(a, c, research, method, fft):
    indices, selection = indices_for(a, c, research, method)
    blocks = split_blocks(indices)
    if method == "ui_baseline":
        aux = a[indices, 3].copy()
    else:
        sos = butter(research["lowpass_butterworth_order"], research["lowpass_cutoff_hz"],
                     fs=1/c["sample_interval_s"], output="sos")
        trim = research["filter_edge_trim_samples"]
        filtered, retained = [], []
        for block in blocks:
            if len(block) <= 2*trim+32:
                continue
            filtered.append(sosfiltfilt(sos, a[block, 3])[trim:-trim])
            retained.append(block[trim:-trim])
        aux, indices = np.concatenate(filtered), np.concatenate(retained)
    phase, anchors, qc = base.phase_map(aux, fft, c["max_nonmonotonic_ratio"], c["sample_interval_s"])
    signal = a[indices, 4].copy()
    if not np.isfinite(signal).all():
        raise ValueError("Nonfinite measurement survived the synchronous mask")
    own_grid = np.linspace(0, phase[-1], len(signal))
    linearized = np.interp(own_grid, phase, signal[anchors])
    qc.update(retained_samples=len(indices), joins=len(split_blocks(indices))-1,
              original_first_index=int(indices[0]), original_end_index_exclusive=int(indices[-1]+1),
              filter_cutoff_hz=0 if method == "ui_baseline" else research["lowpass_cutoff_hz"],
              direct_interpolation=method not in ["ui_baseline", "lowpass_two_stage"])
    return dict(values=linearized, phase_span=float(phase[-1]), metrics=qc,
                anchor_phase=phase, anchor_signal=signal[anchors])


def spectra_for(ref, sig, method, fft):
    if method in ["ui_baseline", "lowpass_two_stage"]:
        return base.pair_spectra(ref, sig, fft)
    span = min(ref["phase_span"], sig["phase_span"])
    count = min(len(ref["values"]), len(sig["values"]))
    target = np.linspace(0, span, count)
    return (fft.fft(np.interp(target, ref["anchor_phase"], ref["anchor_signal"])),
            fft.fft(np.interp(target, sig["anchor_phase"], sig["anchor_signal"])), span)


def alias_diagnostic(reference, signal, span, distance, fft, c):
    size = c["segment_size"]
    length = 1 << (size*c["zero_padding_factor"]-1).bit_length()
    dx = np.pi*c["auxiliary_path_difference_m"]/span
    start = int(np.clip(round(distance/dx-(size-1)/2), 0, len(reference)//2-size))
    traces = []
    for spectrum in [reference, signal]:
        y = np.abs(fft.fft(spectrum[start:start+size]*np.hanning(size), inverse=True, n=length))
        y -= y.mean()
        traces.append(y)
    energy = float(np.sqrt((traces[0]@traces[0])*(traces[1]@traces[1])))
    corr = fft.fft(fft.fft(traces[1])*fft.fft(traces[0]).conj(), inverse=True).real/max(energy, 1e-300)
    auto = fft.fft(np.abs(fft.fft(traces[0]))**2, inverse=True).real/max(float(traces[0]@traces[0]), 1e-300)
    corr, auto = np.fft.fftshift(corr), np.fft.fftshift(auto)
    lags = np.arange(length)-length//2
    positions, props = find_peaks(corr, distance=c["zero_padding_factor"])
    ranked = positions[np.argsort(corr[positions])[-8:][::-1]]
    bandwidth = 299792458*span/(2*np.pi*c["auxiliary_group_index"]*c["auxiliary_path_difference_m"])/1e9
    return pd.DataFrame(dict(lag=lags[::8], correlation=corr[::8], reference_autocorrelation=auto[::8])), dict(
        center_m=(start+(size-1)/2)*dx,
        candidates=[dict(lag=int(lags[k]), shift_ghz=float(lags[k]*bandwidth/length), correlation=float(corr[k])) for k in ranked])


def assess_pairs(pairs, sources, cfg):
    # All comparisons share the intersection of physical axes, never row indices.
    lo = max(float(p["frame"].distance_m.min()) for p in pairs)
    hi = min(float(p["frame"].distance_m.max()) for p in pairs)
    step = max(p["axis"]["hop_m"] for p in pairs)
    grid = np.arange(lo, hi+step*.01, step)
    arrays = {}
    for kind in ["within", "between"]:
        chosen = [p for p in pairs if p["kind"] == kind]
        arrays[kind] = {key: np.array([np.interp(grid, p["frame"].distance_m, p["frame"][key]) for p in chosen])
                        for key in ["shift_ghz", "peak_correlation"]}
    w, b = arrays["within"]["shift_ghz"], arrays["between"]["shift_ghz"]
    wc, bc = arrays["within"]["peak_correlation"], arrays["between"]["peak_correlation"]
    wcenter = np.median(w, axis=0)
    wnoise = np.median(np.abs(w-wcenter), axis=0)
    effect = np.median(b, axis=0)-wcenter
    direction = np.sign(np.median(b, axis=0))
    same_sign = np.mean(np.sign(b) == direction, axis=0)
    rules = cfg["exploratory_acceptance"]
    quality = (np.mean(bc >= rules["minimum_peak_correlation"], axis=0) >= rules["minimum_correlation_fraction_pairs"])
    quality &= np.mean(wc >= rules["minimum_peak_correlation"], axis=0) >= rules["minimum_correlation_fraction_pairs"]
    threshold = np.maximum(rules["minimum_effect_ghz"], rules["within_mad_multiplier"]*wnoise)
    gauge = max(p["axis"]["gauge_length_m"] for p in pairs)
    # Assess distribution outside every strong reflection's window support.
    distributed = (grid-gauge/2 >= 0) & (grid+gauge/2 <= 23)
    for source in sources:
        distributed &= np.abs(grid-source["peak_m"]) > gauge/2+.1
    phase_ok = all(source["prepared"]["metrics"]["phase_qc_passed"] for source in sources)
    candidate = quality & (np.abs(effect) > threshold) & (same_sign >= rules["minimum_same_sign_fraction"]) & distributed
    accepted = candidate & phase_ok
    frame = pd.DataFrame(dict(distance_m=grid, within_median_ghz=wcenter,
                 between_median_ghz=np.median(b, axis=0), within_mad_ghz=wnoise,
                 between_minus_within_ghz=effect, within_correlation=np.median(wc,axis=0),
                 between_correlation=np.median(bc,axis=0), between_same_sign_fraction=same_sign,
                 distributed=distributed, candidate=candidate, accepted=accepted))
    full_counts = dict(within_pairs=len(w), between_pairs=len(b), sources=len(sources),
                phase_qc_passed_sources=sum(s["prepared"]["metrics"]["phase_qc_passed"] for s in sources),
                gauge_m=gauge, distributed_grid_points=int(distributed.sum()),
                distributed_within_correlation=float(np.median(wc[:,distributed])) if distributed.any() else None,
                distributed_between_correlation=float(np.median(bc[:,distributed])) if distributed.any() else None,
                within_median_absolute_shift_ghz=float(np.median(np.abs(w[:,distributed]))) if distributed.any() else None,
                between_median_absolute_shift_ghz=float(np.median(np.abs(b[:,distributed]))) if distributed.any() else None,
                candidate_points=int(candidate.sum()), accepted_points=int(accepted.sum()),
                phase_qc_all_passed=phase_ok)
    return frame, full_counts


def plot_method(out, folder, method, pairs, aggregate, processing_config):
    # A fixed Ref->three Heating overlay; labels identify actual files.
    references = sorted({p["reference"] for p in pairs if p["kind"] == "between"})
    reference = references[0]
    chosen = sorted([p for p in pairs if p["kind"] == "between" and p["reference"] == reference], key=lambda p:p["signal"])
    fig, ax = plt.subplots(2, 2, figsize=(14, 8), constrained_layout=True)
    colors = ["#0072bd", "#d95f02", "#2ca02c"]
    for color, pair in zip(colors, chosen):
        p = pair["frame"]
        ax[0,0].plot(p.distance_m, p.shift_ghz, color=color, lw=1, label=pair["signal"])
        ax[0,1].plot(np.arange(len(p)), p.signed_padded_lag, color=color, lw=1, label=pair["signal"])
        ax[1,0].plot(p.distance_m, p.peak_correlation, color=color, lw=1)
    for pair in pairs:
        if pair["kind"] == "within":
            p = pair["frame"]
            ax[1,1].plot(p.distance_m, p.shift_ghz, lw=.8, alpha=.7, label=pair["reference"]+" / "+pair["signal"])
    ax[0,0].set(xlabel="Distance (m)", ylabel="Spectral shift (GHz)", title=f"Reference: {reference}")
    ax[0,1].set(xlabel="Processed segment index", ylabel="Signed padded correlation lag (bins)", title="Unscaled lag view; screenshot axes are unknown")
    ax[1,0].set(xlabel="Distance (m)", ylabel="Normalized peak correlation", ylim=(0,1.02))
    ax[1,0].axhline(.5, ls=":", color="black", lw=.8)
    ax[1,1].set(xlabel="Distance (m)", ylabel="Same-state shift (GHz)", title="Independent same-state controls")
    ax[0,0].legend(fontsize=8)
    ax[1,1].legend(fontsize=7)
    for a in ax.flat:
        a.grid(alpha=.2)
    fig.suptitle(f"{folder} | {method} | segment {processing_config['segment_size']} / hop {processing_config['segment_hop']} / OPD ~{processing_config['auxiliary_path_difference_m']:g} m\nActual files only; no temperature calibration or arbitrary curve offsets", fontsize=12)
    fig.savefig(out/"profile_overlay.png", dpi=150)
    plt.close(fig)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", required=True, type=Path)
    ap.add_argument("--output", required=True, type=Path)
    args = ap.parse_args()
    research = json.loads(args.config.read_text(encoding="utf-8-sig"))
    c = json.loads((ROOT/research["base_config"]).read_text(encoding="utf-8"))
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    write(out/"config.json", research)
    write(out/"processing_config.json", c)
    started = time.perf_counter()
    metrics = dict(status="RUNNING")
    write(out/"metrics.json", metrics)
    (out/"summary.md").write_text("# RUNNING\n", encoding="utf-8")
    fft = None
    try:
        fft = base.NativeMkl(ROOT/c["native_mkl_dll"])
        write(out/"controls.json", base.controls(fft,c))
        write(out/"environment.json", dict(python=sys.version, numpy=np.__version__, scipy=base.scipy.__version__,
              analyzer_sha256=base.digest(Path(__file__)), imported_base_sha256=base.digest(Path(base.__file__)),
              native_mkl_sha256=base.digest(fft.path), image_sha256=base.digest(Path(research["reference_image"]))))
        manifest, method_rows, pair_rows = [], [], []
        for group in research["groups"]:
            folder = group["folder"]
            print("GROUP", folder, flush=True)
            datasets = []
            for path in sorted((Path(c["data_root"])/folder).glob("*.csv")):
                if path.name in group["exclude_files"]:
                    continue
                a,m = base.read_source(path)
                manifest.append(m)
                datasets.append((path,a,m))
            write(out/"dataset_manifest.json", manifest)
            for method in research["methods"]:
                dest = out/folder/method
                dest.mkdir(parents=True)
                sources = []
                for path,a,meta in datasets:
                    prepared = prepare_candidate(a,c,research,method,fft)
                    rs = fft.fft(prepared["values"])
                    dm,_,_ = base.distance_metrics(rs,prepared["phase_span"],c)
                    write(dest/(path.stem+"_preparation.json"), dict(source_path=str(path),sha256=meta["sha256"],**prepared["metrics"],**dm))
                    sources.append(dict(name=path.name, state=base.state(path), prepared=prepared, peak_m=dm["dominant_peak_m"]))
                pairs = []
                # Orient cross-state pairs Ref -> Heating consistently.
                for i,j in itertools.combinations(range(len(sources)),2):
                    left,right = sources[i],sources[j]
                    kind = "within" if left["state"] == right["state"] else "between"
                    if kind == "between" and left["state"] != "Ref":
                        left,right = right,left
                    rs,ss,span = spectra_for(left["prepared"],right["prepared"],method,fft)
                    p,axis = base.profile(rs,ss,span,fft,c)
                    p["signed_padded_lag"] = p.shift_ghz/axis["ghz_per_padded_lag"]
                    p.to_csv(dest/f"pair_{i}_{j}_profile.csv",index=False)
                    row = dict(folder=folder,method=method,reference=left["name"],signal=right["name"],kind=kind,
                               **axis,**base.summary_row(p))
                    pair_rows.append(row)
                    pairs.append(dict(reference=left["name"],signal=right["name"],kind=kind,frame=p,axis=axis))
                    if kind == "between" and not (dest/"correlation_candidates.json").exists():
                        # Fixed positions for diagnosing competing peaks; never used to choose plotted lags.
                        diagnostics = []
                        for position in [6.,12.,18.]:
                            trace,d = alias_diagnostic(rs,ss,span,position,fft,c)
                            trace.to_csv(dest/f"correlation_{int(position)}m.csv",index=False)
                            diagnostics.append(d)
                        write(dest/"correlation_candidates.json",diagnostics)
                agg,assessment = assess_pairs(pairs,sources,research)
                agg.to_csv(dest/"repeatability_contrast.csv",index=False)
                assessment.update(folder=folder,method=method,group_role=group["role"])
                write(dest/"assessment.json",assessment)
                method_rows.append(assessment)
                plot_method(dest,folder,method,pairs,agg,c)
                pd.DataFrame(method_rows).to_csv(out/"method_metrics.csv",index=False)
                pd.DataFrame(pair_rows).to_csv(out/"pair_metrics.csv",index=False)
                print(method, json.dumps(assessment), flush=True)
        metrics.update(status="PASS_EXECUTION",numeric_rows=sum(m["numeric_rows"] for m in manifest),
                       source_files=len(manifest),method_groups=len(method_rows),pairs=len(pair_rows),
                       accepted_points=sum(m["accepted_points"] for m in method_rows),
                       elapsed_seconds=time.perf_counter()-started,native_fft_calls=fft.calls,
                       temperature_file_mapping_known=False,production_ui_changed=False)
        write(out/"metrics.json",metrics)
        (out/"summary.md").write_text("# PASS_EXECUTION\n\n"+json.dumps(metrics,indent=2)+
            "\n\nAcceptance requires independent repeatability and phase QC, not screenshot resemblance. See method_metrics.csv and report under docs/report.\n",encoding="utf-8")
        print(json.dumps(metrics),flush=True)
    except BaseException as exc:
        metrics.update(status="FAIL",error=repr(exc),elapsed_seconds=time.perf_counter()-started)
        write(out/"metrics.json",metrics)
        (out/"summary.md").write_text("# FAIL\n\n"+traceback.format_exc(),encoding="utf-8")
        raise
    finally:
        if fft is not None:
            fft.close()


if __name__ == "__main__":
    main()
