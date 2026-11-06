"""Synthetic interpolation isolation only; no phase estimation or product changes."""
import hashlib
import ctypes
import json
from pathlib import Path
import time
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "harness/results/20261106_klinearization_improvement_research"
OUT.mkdir(parents=True, exist_ok=True)


def write(name, obj):
    (OUT / name).write_text(json.dumps(obj, indent=2), encoding="utf-8")


def peak_working_set():
    class Counters(ctypes.Structure):
        _fields_ = [("cb", ctypes.c_uint32), ("PageFaultCount", ctypes.c_uint32)] + [
            (name, ctypes.c_size_t) for name in ["PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage",
                "QuotaPagedPoolUsage", "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage", "PagefileUsage", "PeakPagefileUsage"]]
    counters = Counters()
    counters.cb = ctypes.sizeof(counters)
    ctypes.windll.kernel32.GetCurrentProcess.restype = ctypes.c_void_p
    query = ctypes.windll.psapi.GetProcessMemoryInfo
    query.argtypes = [ctypes.c_void_p, ctypes.POINTER(Counters), ctypes.c_uint32]
    if not query(ctypes.windll.kernel32.GetCurrentProcess(), ctypes.byref(counters), counters.cb):
        raise ctypes.WinError()
    return counters.PeakWorkingSetSize


def local_cubic(x, y, target):
    left = np.clip(np.searchsorted(x, target) - 2, 0, len(x) - 4)
    answer = np.zeros_like(target)
    for j in range(4):
        weight = np.ones_like(target)
        for k in range(4):
            if j != k:
                weight *= (target - x[left + k]) / (x[left + j] - x[left + k])
        answer += weight * y[left + j]
    return answer


def main():
    config = {"samples_per_synthetic_sweep": 131072, "sample_rate_hz": 50e6,
        "auxiliary_average_hz": 5.8e6, "provisional_travel_path_m": 200,
        "test_distances_m": [5, 24, 50], "signal_span_fractional_offsets": [.00012, .01],
        "instantaneous_frequency_modulation_fraction": .05,
        "instantaneous_frequency_modulation_cycles": 3,
        "scope": "Known exact monotone phase, single cosine CH4; interpolation error isolation, not real-data performance",
        "raw_samples_read": 0, "phase_noise": 0, "mode_hop": False,
        "reference_code": "AuxKLinearizer.LinearizeSweepsDetailed and ResampleUniformSpan",
        "methods": ["current_two_stage_linear", "direct_linear", "direct_four_point_lagrange"],
        "note": "Four-point Lagrange is not a natural cubic spline or monotone phase fitting. No production acceptance inferred."}
    write("config.json", config)
    started = time.perf_counter()
    try:
        n = config["samples_per_synthetic_sweep"]
        u = np.arange(n) / (n - 1)
        span = 2 * np.pi * config["auxiliary_average_hz"] / config["sample_rate_hz"] * (n - 1)
        ref_phase = span * (u + .05 * (1 - np.cos(6 * np.pi * u)) / (6 * np.pi))
        target = np.linspace(0, ref_phase[-1], n)
        cases = []
        for offset in config["signal_span_fractional_offsets"]:
            sig_phase = ref_phase * (1 + offset)
            first_grid = np.linspace(0, sig_phase[-1], n)
            for distance in config["test_distances_m"]:
                ratio = 2 * distance / config["provisional_travel_path_m"]
                ref = np.cos(ratio * ref_phase + .31)
                sig = np.cos(ratio * sig_phase + .31)
                truth = np.cos(ratio * target + .31)
                ref_resampled = np.interp(target, ref_phase, ref)
                candidates = {
                    "current_two_stage_linear": np.interp(target, first_grid, np.interp(first_grid, sig_phase, sig)),
                    "direct_linear": np.interp(target, sig_phase, sig),
                    "direct_four_point_lagrange": local_cubic(sig_phase, sig, target)}
                for name, actual in candidates.items():
                    interior = slice(8, -8)
                    scale = np.sqrt(np.mean(truth[interior] ** 2))
                    reference_method = local_cubic(ref_phase, ref, target) if name == "direct_four_point_lagrange" else ref_resampled
                    cases.append({"span_offset_fraction": offset, "distance_m": distance, "method": name,
                        "signal_nrmse_against_exact_truth": float(np.sqrt(np.mean((actual[interior] - truth[interior]) ** 2)) / scale),
                        "ref_sig_nrmse_despite_same_physical_signal": float(np.sqrt(np.mean((actual[interior] - reference_method[interior]) ** 2)) / scale)})
        write("synthetic_interpolation_cases.json", cases)
        write("metrics.json", {"status": "PASS_EXECUTION", "cases": len(cases), "elapsed_seconds": time.perf_counter() - started,
            "raw_numeric_samples": 0, "real_data_improvement_verified": False,
            "peak_working_set_bytes": peak_working_set(),
            "script_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest()})
        (OUT / "summary.md").write_text("# Research / synthetic isolation completed\n\nKnown exact phases isolate the effect of one versus two interpolation stages. This is not an OFDR phase-estimation or thermal-sensing validation. No raw CSV was read or production path changed. Results cannot demonstrate that k-linearization is the dominant real-data bottleneck. See the report under docs/report.\n", encoding="utf-8")
        print(json.dumps(cases, indent=2))
    except BaseException as exc:
        write("metrics.json", {"status": "FAIL", "error": repr(exc), "elapsed_seconds": time.perf_counter() - started})
        (OUT / "summary.md").write_text(f"# FAIL\n\n{exc!r}\n", encoding="utf-8")
        raise


if __name__ == "__main__":
    main()
