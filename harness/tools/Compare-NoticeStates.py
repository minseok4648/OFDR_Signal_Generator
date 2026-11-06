"""Audit saved 15 ms state comparisons and separately bounded 50 MS/s controls."""
import argparse
import csv
import hashlib
import json
import math
from pathlib import Path
import shutil
import time
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
RUNS = [
    ("H1-H2", "within", "20261106_021714_725_prepared_ofdr_analysis_d52c62"),
    ("H1-H3", "within", "20261106_021748_701_prepared_ofdr_analysis_bfae9e"),
    ("C1-C2", "within", "20261106_021901_605_prepared_ofdr_analysis_232f4e"),
    ("H1-C1", "between", "20261106_021646_861_prepared_ofdr_analysis_b53efa"),
    ("H1-C2", "between", "20261106_021423_276_prepared_ofdr_analysis_4c6ec8"),
    ("C1-H1", "reverse", "20261106_021930_294_prepared_ofdr_analysis_d6a076"),
]
REGIONS = [(0, 8), (8, 16), (16, 20), (20, 24), (24, 48)]


def read(p):
    return json.loads(Path(p).read_text(encoding="utf-8-sig"))


def write(p, v):
    Path(p).write_text(json.dumps(v, indent=2, ensure_ascii=False), encoding="utf-8")


def table(p, rows):
    with Path(p).open("w", encoding="utf-8-sig", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)


def describe(v):
    v = np.asarray(v)
    return {"N": len(v), "P10GHz": float(np.quantile(v, .1)), "MedianGHz": float(np.median(v)),
            "P90GHz": float(np.quantile(v, .9)), "Within1GHzFraction": float(np.mean(np.abs(v) < 1))}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sample-run", type=Path, required=True)
    ap.add_argument("--synthetic-run", type=Path, required=True)
    args = ap.parse_args()
    out = args.sample_run / "state_comparison"
    out.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()
    write(out / "config.json", {"histories": RUNS, "comparison_travel_path_m": 200,
          "reported_affected_length_m": 2, "reported_temperature_difference_lower_bound_c": 15,
          "affected_position_known": False, "temperature_coefficient_reference_c_per_ghz": .801,
          "coefficient_is_calibrated_for_this_setup": False,
          "original_raw_numeric_scope": "230000 samples/file in separately recorded 4.6 ms window",
          "history_scope": "Saved 750000-sample/15 ms results; no full numeric processing repeated",
          "synthetic_run": str(args.synthetic_run.resolve()), "product_changes": False})
    try:
        source_rows = []
        source_hashes = {}
        for i in range(5):
            source = read(args.sample_run / f"source_{i}.json")
            source_hashes[Path(source["Path"]).name] = source["Sha256"]
            context = read(args.sample_run / f"file{i}_energy_context.json")
            raw = read(args.sample_run / f"file{i}_raw_aux.json")
            filt = read(args.sample_run / f"file{i}_filtered20_aux.json")
            sig = read(args.sample_run / f"file{i}_raw_sig.json")
            source_rows.append({"File": Path(source["Path"]).name, "Sha256": source["Sha256"], "NumericSamples": source["NumericSamples"],
                               "RawQcPercent": raw["Phase"]["NonmonotonicRatio"] * 100, "FilteredQcPercent": filt["Phase"]["NonmonotonicRatio"] * 100,
                               "AuxAverageMHz": raw["Values"]["phase_average_hz"] / 1e6, "SignalRmsV": sig["ac_rms_v"], **context})
        histories, history_rows, regions = [], [], []
        for label, kind, name in RUNS:
            folder = ROOT / "OFDR_Signal_Generator/bin/Debug/harness/results" / name
            snap = out / "snapshots" / name
            snap.mkdir(parents=True, exist_ok=True)
            manifest = []
            for filename in ["config.json", "processing_config.json", "ref_preparation.json", "signal_preparation.json", "result.json", "metrics.json", "summary.md"]:
                source = folder / filename
                shutil.copy2(source, snap / filename)
                manifest.append({"path": str(source), "sha256": hashlib.sha256(source.read_bytes()).hexdigest()})
            write(snap / "manifest.json", manifest)
            j, c, ref, sig = [read(snap / n) for n in ["result.json", "processing_config.json", "ref_preparation.json", "signal_preparation.json"]]
            for m in [ref, sig]:
                assert m["SourceSha256"] == source_hashes[Path(m["SourcePath"]).name]
                assert m["SelectedStartIndex"] == 50000 and m["SelectedEndIndexExclusive"] == 800000
                assert m["ModeHopExclusionTailSeconds"] == .0003
            assert c["AuxiliaryPathDifferenceMeters"] == 100 and c["SegmentSize"] == 1024 and c["SegmentHop"] == 256
            p = j["SpectralShiftProfile"]
            assert len(p["Values"]) < 10000
            # Algebraic coordinate correction only, no reprocessing or changed peak search.
            x = 2 * np.linspace(p["XStart"], p["XEnd"], len(p["Values"]))
            y = np.array(p["Values"]) / 2
            keep = x <= 48
            x, y = x[keep], y[keep]
            maxlag = next(m["Value"] for m in j["PerformanceMetrics"] if m["Name"] == "maximum_correlation_search_lag")
            row = {"Label": label, "Kind": kind, "Run": name, "Ref": Path(ref["SourcePath"]).name, "Sig": Path(sig["SourcePath"]).name,
                   "RefQcPercent": j["KLinearization"]["NonmonotonicRatio"] * 100, "SigQcPercent": j["SignalKLinearization"]["NonmonotonicRatio"] * 100,
                   "GaugeAt200M": c["SegmentSize"] * j["PhysicalAxis"]["DistanceMetersPerFftBin"] * 2,
                   "OpticalSpanAt200GHz": j["PhysicalAxis"]["SweepSpanGHz"] / 2,
                   "SearchHalfWidthAt200GHz": maxlag * j["PhysicalAxis"]["SpectralShiftGHzPerLag"] / 2,
                   "SelectedDistanceAt200M": j["SelectedSegment"]["SegmentCenterDistanceMeters"] * 2,
                   "SelectedCorrelationPeak": j["SelectedSegment"]["PeakMagnitude"], "DisplayedAnalysisEndSavedM": c["AnalysisEndMeters"]}
            history_rows.append(row)
            for lo, hi in REGIONS:
                v = y[(x >= lo) & (x < hi)]
                regions.append({"Label": label, "Kind": kind, "RangeM": f"{lo}-{hi}", **describe(v)})
            histories.append({"Label": label, "Kind": kind, "X": x.tolist(), "Y": y.tolist(), "Summary": row})
        grid = np.arange(1, 47, .3)
        aligned = {h["Label"]: np.interp(grid, h["X"], h["Y"]) for h in histories}
        reverse = float(np.max(np.abs(aligned["H1-C1"] + aligned["C1-H1"])))
        within = np.array([aligned[k] for k in ["H1-H2", "H1-H3", "C1-C2"]])
        between = np.array([aligned[k] for k in ["H1-C1", "H1-C2"]])
        contrast = []
        for center in np.arange(2, 46, .3):
            mask = np.abs(grid - center) <= 1
            w = np.median(within[:, mask], axis=1)
            b = np.median(between[:, mask], axis=1)
            contrast.append({"CenterM": float(center), "WidthM": 2, "WithinMedianMinGHz": float(w.min()), "WithinMedianMaxGHz": float(w.max()),
                             "BetweenMedianMinGHz": float(b.min()), "BetweenMedianMaxGHz": float(b.max()),
                             "ConservativeAbsoluteContrastGHz": float(np.min(np.abs(b)) - np.max(np.abs(w))),
                             "Interpretation": "Exploratory overlapping windows, not calibrated confidence or a detected heated position"})
        pair_rows, pair_regions, bounded = [], [], []
        states = ["H", "H", "H", "C", "C"]
        for file in sorted(args.sample_run.glob("pair_*.json")) + sorted(args.sample_run.glob("control_*.json")):
            j = read(file)
            name = file.stem
            kind = "control"
            if name.startswith("pair_"):
                a, b = [int(v) for v in name.split("_")[1:3]]
                kind = "within" if states[a] == states[b] else "between"
            pair_rows.append({"Name": name, "Kind": kind, **j["Metrics"]})
            x, y = np.array(j["DistancesMeters"]), np.array(j["SpectralShiftGHz"])
            corr = np.array(j["CorrelationPeaks"])
            for lo, hi in REGIONS:
                mask = (x >= lo) & (x < hi)
                pair_regions.append({"Name": name, "Kind": kind, "RangeM": f"{lo}-{hi}", **describe(y[mask]), "CorrelationMedian": float(np.median(corr[mask]))})
            if name in ["pair_0_1_raw", "pair_0_3_raw", "control_independent_stationary_noise", "control_independent_common_envelope_noise", "control_self"]:
                bounded.append({"Label": name, "X": x.tolist(), "Y": y.tolist(), "Correlation": corr.tolist()})
        synthetic = read(args.synthetic_run / "synthetic_known_local_shift.json")
        synthetic_metrics = read(args.synthetic_run / "metrics.json")
        for name, rows in [("source_metrics", source_rows), ("history_metrics", history_rows), ("history_regions", regions),
                           ("exploratory_2m_contrast", contrast), ("bounded_pairs", pair_rows), ("bounded_regions", pair_regions)]:
            table(out / (name + ".csv"), rows)
        write(out / "plot_data.json", {"Histories": histories, "Sources": source_rows, "Bounded": bounded, "Synthetic": synthetic, "SyntheticMetrics": synthetic_metrics})
        write(out / "metrics.json", {"status": "PASS", "source_hashes_match": True, "history_count": len(histories), "original_samples_reread": 0,
              "reverse_pair_antisymmetry_max_error_ghz": reverse, "elapsed_seconds": time.perf_counter() - started,
              "reference_15c_shift_ghz": 15 / .801, "heated_position_identified": False,
              "known_shift_control_passed": abs(synthetic_metrics["recovered_inside_median_ghz"] - 18.7) < .1 and synthetic_metrics["outside_abs_shift_p90_ghz"] < .1})
        (out / "summary.md").write_text("# PASS: saved state comparison\n\nSix GUI runs with verified source hashes compared. Saved path setting 100 m; plots algebraically rescale coordinates to provisional 200 m. "
            "These are saved 15 ms results, not regenerated full inputs. Bounded controls use 4.6 ms and cannot by themselves reject an expected >=18.7 GHz thermal shift outside their approximately +/-13.6 GHz search. "
            "State names are experimental labels; exact within-state temperature stability and heated position are unknown. No statistical classifier or heated-position claim is made.\n", encoding="utf-8")
    except BaseException as exc:
        write(out / "metrics.json", {"status": "FAIL", "error": repr(exc), "elapsed_seconds": time.perf_counter() - started})
        (out / "summary.md").write_text(f"# FAIL\n\n{exc!r}\n", encoding="utf-8")
        raise
    print(out)


if __name__ == "__main__":
    main()
