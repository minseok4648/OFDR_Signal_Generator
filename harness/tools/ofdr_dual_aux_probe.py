"""Minimal dual-auxiliary OFDR experiment.

This is a harness probe, not the GUI implementation.  It compares the current
single-auxiliary assumption with independently k-linearized reference and signal
sweeps and records reproducible metrics for one real 5M pair.
"""

import argparse
import json
import math
import re
import time
from pathlib import Path

import numpy as np


def read_isf(path):
    payload = Path(path).read_bytes()
    marker = payload.find(b":CURVE #")
    if marker < 0:
        raise ValueError(f"SCPI curve block not found: {path}")
    digit_index = marker + len(b":CURVE #")
    digit_count = int(chr(payload[digit_index]))
    length_start = digit_index + 1
    length_end = length_start + digit_count
    byte_count = int(payload[length_start:length_end].decode("ascii"))
    data_start = length_end
    header = payload[:marker].decode("ascii", errors="strict")
    ymult = float(re.search(r"(?:^|;)YMULT\s+([^;]+)", header).group(1))
    yoff = float(re.search(r"(?:^|;)YOFF\s+([^;]+)", header).group(1))
    yzero = float(re.search(r"(?:^|;)YZERO\s+([^;]+)", header).group(1))
    raw = np.frombuffer(payload, dtype=">i2", count=byte_count // 2, offset=data_start)
    return (raw.astype(np.float64) - yoff) * ymult + yzero


def phase_map(aux):
    centered = aux - np.mean(aux)
    analytic = np.fft.fft(centered)
    n = analytic.size
    analytic[1:n // 2] *= 2.0
    analytic[n // 2 + 1:] = 0.0
    analytic = np.fft.ifft(analytic)
    magnitude = np.abs(analytic)
    phase = np.unwrap(np.angle(analytic))
    direction = 1 if phase[-1] >= phase[0] else -1
    phase *= direction
    relative = phase - phase[0]
    increments = np.diff(relative)
    nonmonotonic_ratio = float(np.count_nonzero(increments <= 0.0) / increments.size)
    if nonmonotonic_ratio > 1e-5:
        raise ValueError(f"phase nonmonotonic ratio {nonmonotonic_ratio:g}")
    return relative, {
        "phase_span_rad": float(relative[-1]),
        "median_phase_increment_rad": float(np.median(increments)),
        "nonmonotonic_ratio": nonmonotonic_ratio,
        "analytic_magnitude_p01": float(np.quantile(magnitude, 0.01)),
        "analytic_magnitude_p50": float(np.quantile(magnitude, 0.50)),
        "analytic_magnitude_p99": float(np.quantile(magnitude, 0.99)),
        "sweep_direction": direction,
    }


def interpolate_sweep(phase, signal, common_span):
    target = np.linspace(0.0, common_span, signal.size)
    return np.interp(target, phase, signal)


def parabolic_peak(values, index):
    if index <= 0 or index >= values.size - 1:
        return float(index)
    left, center, right = values[index - 1:index + 2]
    denominator = left - 2.0 * center + right
    if abs(denominator) < 1e-20:
        return float(index)
    return float(index + np.clip(0.5 * (left - right) / denominator, -0.5, 0.5))


def local_shift_profile(reference_spectrum, signal_spectrum, phase_span, args):
    distance_per_bin = math.pi * args.opd / phase_span
    sweep_hz = 299792458.0 * phase_span / (2.0 * math.pi * args.group_index * args.opd)
    padded = 1
    while padded < args.segment * args.pad:
        padded *= 2
    # The UI uses the tensile-strain convention from the supplied reference plot:
    # a positive correlation lag is displayed as a positive Rayleigh spectral shift.
    shift_ghz_per_lag = sweep_hz / padded / 1e9
    maximum_lag = min(padded // 2 - 2, int(math.ceil(args.max_shift_ghz / abs(shift_ghz_per_lag))))
    first = max(0, int(math.floor(args.start_m / distance_per_bin - args.segment / 2.0)))
    last = min(reference_spectrum.size - args.segment, int(math.ceil(args.end_m / distance_per_bin - args.segment / 2.0)))
    starts = np.arange(first, last + 1, args.hop, dtype=np.int64)
    window = np.hanning(args.segment)
    distances = []
    shifts = []
    peaks = []
    zero = padded // 2
    for start in starts:
        ref_local = np.fft.ifft(reference_spectrum[start:start + args.segment] * window, n=padded)
        sig_local = np.fft.ifft(signal_spectrum[start:start + args.segment] * window, n=padded)
        ref_trace = np.abs(ref_local)
        sig_trace = np.abs(sig_local)
        ref_trace -= np.mean(ref_trace)
        sig_trace -= np.mean(sig_trace)
        corr = np.fft.ifft(np.fft.fft(sig_trace) * np.conj(np.fft.fft(ref_trace))).real
        energy = math.sqrt(float(np.sum(ref_trace * ref_trace) * np.sum(sig_trace * sig_trace)))
        if energy > 1e-20:
            corr /= energy
        corr = np.fft.fftshift(corr)
        search = corr[zero - maximum_lag:zero + maximum_lag + 1]
        local_index = int(np.argmax(search))
        peak_index = zero - maximum_lag + local_index
        fractional = parabolic_peak(corr, peak_index)
        lag = fractional - zero
        distances.append((start + (args.segment - 1) / 2.0) * distance_per_bin)
        shifts.append(lag * shift_ghz_per_lag)
        peaks.append(float(corr[peak_index]))
    return np.asarray(distances), np.asarray(shifts), np.asarray(peaks), {
        "distance_m_per_fft_bin": distance_per_bin,
        "padded_length": padded,
        "spectral_shift_ghz_per_lag": shift_ghz_per_lag,
        "maximum_search_lag": maximum_lag,
        "segment_count": int(starts.size),
    }


def region_stats(distances, shifts, start, end):
    selected = shifts[(distances >= start) & (distances <= end)]
    if selected.size == 0:
        return {"count": 0}
    return {
        "count": int(selected.size),
        "median_ghz": float(np.median(selected)),
        "p10_ghz": float(np.quantile(selected, 0.10)),
        "p90_ghz": float(np.quantile(selected, 0.90)),
        "mad_ghz": float(np.median(np.abs(selected - np.median(selected)))),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--ref-aux", required=True)
    parser.add_argument("--ref", required=True)
    parser.add_argument("--sig-aux", required=True)
    parser.add_argument("--sig", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--segment", type=int, default=1024)
    parser.add_argument("--hop", type=int, default=256)
    parser.add_argument("--pad", type=int, default=16)
    parser.add_argument("--start-m", type=float, default=3.5)
    parser.add_argument("--end-m", type=float, default=8.7)
    parser.add_argument("--deformation-start-m", type=float, default=5.5)
    parser.add_argument("--deformation-end-m", type=float, default=6.5)
    parser.add_argument("--opd", type=float, default=18.2703)
    parser.add_argument("--group-index", type=float, default=1.4682)
    parser.add_argument("--max-shift-ghz", type=float, default=180.0)
    args = parser.parse_args()
    started = time.time()
    ref_aux = read_isf(args.ref_aux)
    reference = read_isf(args.ref)
    sig_aux = read_isf(args.sig_aux)
    signal = read_isf(args.sig)
    ref_phase, ref_qc = phase_map(ref_aux)
    sig_phase, sig_qc = phase_map(sig_aux)
    common_span = min(ref_qc["phase_span_rad"], sig_qc["phase_span_rad"])
    reference_k = interpolate_sweep(ref_phase, reference, common_span)
    signal_k = interpolate_sweep(sig_phase, signal, common_span)
    reference_spectrum = np.fft.rfft(reference_k)
    signal_spectrum = np.fft.rfft(signal_k)
    distances, shifts, peaks, processing = local_shift_profile(reference_spectrum, signal_spectrum, common_span, args)
    inside = region_stats(distances, shifts, args.deformation_start_m, args.deformation_end_m)
    outside_values = shifts[(distances < args.deformation_start_m) | (distances > args.deformation_end_m)]
    outside = {
        "count": int(outside_values.size),
        "median_ghz": float(np.median(outside_values)),
        "mad_ghz": float(np.median(np.abs(outside_values - np.median(outside_values)))),
        "p10_ghz": float(np.quantile(outside_values, 0.10)),
        "p90_ghz": float(np.quantile(outside_values, 0.90)),
    }
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    config = vars(args).copy()
    config["output"] = str(output.resolve())
    metrics = {
        "status": "COMPLETED",
        "ref_aux_qc": ref_qc,
        "sig_aux_qc": sig_qc,
        "common_phase_span_rad": common_span,
        "processing": processing,
        "deformation_region": inside,
        "outside_region": outside,
        "peak_correlation_median": float(np.median(peaks)),
        "wall_seconds": time.time() - started,
        "distance_m": distances.tolist(),
        "spectral_shift_ghz": shifts.tolist(),
        "peak_correlation": peaks.tolist(),
    }
    (output / "config.json").write_text(json.dumps(config, indent=2), encoding="utf-8")
    (output / "metrics.json").write_text(json.dumps(metrics, indent=2), encoding="utf-8")
    summary = (
        "# Dual auxiliary minimal experiment\n\n"
        "- Status: `COMPLETED`\n"
        f"- Reference CH1 phase span: {ref_qc['phase_span_rad']:.9f} rad\n"
        f"- Signal CH1 phase span: {sig_qc['phase_span_rad']:.9f} rad\n"
        f"- Common phase span: {common_span:.9f} rad\n"
        f"- Segment / hop / padded: {args.segment} / {args.hop} / {processing['padded_length']}\n"
        f"- Spectral resolution: {abs(processing['spectral_shift_ghz_per_lag']):.9f} GHz/lag\n"
        f"- Deformation region median / MAD: {inside.get('median_ghz', float('nan')):.6f} / {inside.get('mad_ghz', float('nan')):.6f} GHz\n"
        f"- Outside region median / MAD: {outside['median_ghz']:.6f} / {outside['mad_ghz']:.6f} GHz\n"
        f"- Wall time: {metrics['wall_seconds']:.3f} s\n\n"
        "## Interpretation\n\n"
        "Reference and signal were independently re-sampled with the CH1 acquired in the same sweep. "
        "The correlation search was limited to the configured physical spectral-shift range and the peak was interpolated below one lag.\n"
    )
    (output / "summary.md").write_text(summary, encoding="utf-8")
    print(json.dumps({"output": str(output), "inside": inside, "outside": outside, "processing": processing}, indent=2))


if __name__ == "__main__":
    main()
