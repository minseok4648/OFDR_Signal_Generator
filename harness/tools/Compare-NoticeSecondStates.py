"""Bounded 50M_Sampling_2 audit; duplicate acquisitions never count as repeats."""
import argparse
import csv
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import time
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("state_utils", Path(__file__).with_name("Compare-NoticeStates.py"))
utils = importlib.util.module_from_spec(spec)
spec.loader.exec_module(utils)
read, write, table, describe = utils.read, utils.write, utils.table, utils.describe
RUNS = [
    ("R1-R2", "within", "20261106_025527_213_prepared_ofdr_analysis_54f3ed"),
    ("R1-R3", "redundant_duplicate", "20261106_025600_014_prepared_ofdr_analysis_956c4c"),
    ("R1-H1", "between", "20261106_025631_117_prepared_ofdr_analysis_918e1b"),
    ("R1-H2", "between", "20261106_025718_229_prepared_ofdr_analysis_be3cfa"),
    ("R1-H3", "between", "20261106_025811_117_prepared_ofdr_analysis_8329d5"),
    ("R2-R3 (identical bytes)", "duplicate_control", "20261106_030552_310_prepared_ofdr_analysis_48e7f4"),
]
REGIONS = [(0, 8), (8, 16), (16, 20), (20, 24), (24, 48)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sample-run", type=Path, required=True)
    ap.add_argument("--previous-run", type=Path, required=True)
    ap.add_argument("--synthetic-run", type=Path, required=True)
    args = ap.parse_args()
    out = args.sample_run / "state_comparison"
    out.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()
    write(out / "config.json", {"histories": RUNS, "travel_path_m_provisional": 200,
          "reported_affected_length_m": 2, "reported_temperature_difference_minimum_c": 15,
          "affected_position_known": False, "optical_hardware_changes_specified": False,
          "source_numeric_scope": "230000 original samples/file, indices [550000,780000); duplicate excluded",
          "history_scope": "Saved 750000-sample/15 ms results; no full numeric reprocessing",
          "previous_run": str(args.previous_run.resolve()), "synthetic_control_reused": str(args.synthetic_run.resolve()),
          "product_changes": False, "qc_limit_changed": False})
    try:
        config = read(args.sample_run / "experiment_config.json")
        source_dir = Path(config["Files"][0]).parent
        manifest = []
        for path in sorted(source_dir.glob("*.csv")):
            digest = hashlib.sha256()
            before = path.stat()
            with path.open("rb") as f:
                for block in iter(lambda: f.read(1048576), b""):
                    digest.update(block)
            header = []
            with path.open(encoding="utf-8-sig") as f:
                for line in f:
                    header.append(line.rstrip("\r\n"))
                    if line.startswith("TIME,"):
                        break
            after = path.stat()
            assert before.st_size == after.st_size and before.st_mtime_ns == after.st_mtime_ns
            manifest.append({"path": str(path), "bytes": before.st_size, "mtime_ns": before.st_mtime_ns,
                             "sha256": digest.hexdigest(), "header": header,
                             "numeric_analysis_samples": 0 if path.name == "ref (3).csv" else config["WindowSamples"]})
        write(out / "dataset_manifest.json", manifest)
        hashes = {Path(m["path"]).name: m["sha256"] for m in manifest}
        assert hashes["ref (2).csv"] == hashes["ref (3).csv"]
        assert len(set(hashes.values())) == 5
        sources = []
        for i in range(5):
            source = read(args.sample_run / f"source_{i}.json")
            assert source["Sha256"] == hashes[Path(source["Path"]).name]
            raw = read(args.sample_run / f"file{i}_raw_aux.json")
            filt = read(args.sample_run / f"file{i}_filtered20_aux.json")
            sig = read(args.sample_run / f"file{i}_raw_sig.json")
            context = read(args.sample_run / f"file{i}_energy_context.json")
            sources.append({"File": Path(source["Path"]).name, "State": config["States"][i], "Sha256": source["Sha256"],
                "NumericSamples": source["NumericSamples"], "RawQcPercent": raw["Phase"]["NonmonotonicRatio"] * 100,
                "FilteredQcPercent": filt["Phase"]["NonmonotonicRatio"] * 100,
                "AuxAverageMHz": raw["Values"]["phase_average_hz"] / 1e6, "SignalRmsV": sig["ac_rms_v"], **context})
        histories, history_rows, regions = [], [], []
        for label, kind, run in RUNS:
            folder = ROOT / "OFDR_Signal_Generator/bin/Debug/harness/results" / run
            snap = out / "snapshots" / run
            snap.mkdir(parents=True, exist_ok=True)
            files = []
            for name in ["config.json", "processing_config.json", "ref_preparation.json", "signal_preparation.json", "result.json", "metrics.json", "summary.md"]:
                shutil.copy2(folder / name, snap / name)
                files.append({"path": str(folder / name), "sha256": hashlib.sha256((snap / name).read_bytes()).hexdigest()})
            write(snap / "manifest.json", files)
            j, c, ref, sig = [read(snap / n) for n in ["result.json", "processing_config.json", "ref_preparation.json", "signal_preparation.json"]]
            for m in [ref, sig]:
                assert m["SourceSha256"] == hashes[Path(m["SourcePath"]).name]
                assert m["SelectedStartIndex"] == 50000 and m["SelectedEndIndexExclusive"] == 800000
                assert m["ModeHopExclusionTailSeconds"] == .0003
                assert m["Diagnosis"]["JoinCount"] == 3
            assert c["AuxiliaryPathDifferenceMeters"] == 100 and c["SegmentSize"] == 1024 and c["SegmentHop"] == 256
            p = j["SpectralShiftProfile"]
            assert len(p["Values"]) < 10000
            x = 2 * np.linspace(p["XStart"], p["XEnd"], len(p["Values"]))
            y = np.asarray(p["Values"]) / 2
            keep = x <= 48
            x, y = x[keep], y[keep]
            maxlag = next(m["Value"] for m in j["PerformanceMetrics"] if m["Name"] == "maximum_correlation_search_lag")
            row = {"Label": label, "Kind": kind, "Run": run, "Ref": Path(ref["SourcePath"]).name, "Sig": Path(sig["SourcePath"]).name,
                "RefQcPercent": j["KLinearization"]["NonmonotonicRatio"] * 100,
                "SigQcPercent": j["SignalKLinearization"]["NonmonotonicRatio"] * 100,
                "GaugeAt200M": 2048 * j["PhysicalAxis"]["DistanceMetersPerFftBin"],
                "OpticalSpanAt200GHz": j["PhysicalAxis"]["SweepSpanGHz"] / 2,
                "SearchHalfWidthAt200GHz": maxlag * j["PhysicalAxis"]["SpectralShiftGHzPerLag"] / 2,
                "SelectedDistanceAt200M": 2 * j["SelectedSegment"]["SegmentCenterDistanceMeters"],
                "SelectedCorrelationPeak": j["SelectedSegment"]["PeakMagnitude"],
                "AbsMaxGHz": float(np.max(np.abs(y))), "DisplayedAnalysisEndSavedM": c["AnalysisEndMeters"]}
            history_rows.append(row)
            for lo, hi in REGIONS:
                regions.append({"Label": label, "Kind": kind, "RangeM": f"{lo}-{hi}", **describe(y[(x >= lo) & (x < hi)])})
            histories.append({"Label": label, "Kind": kind, "X": x.tolist(), "Y": y.tolist(), "Summary": row})
        grid = np.arange(1, 47, .3)
        aligned = {h["Label"]: np.interp(grid, h["X"], h["Y"]) for h in histories}
        contrast = []
        for center in np.arange(2, 46, .3):
            mask = np.abs(grid - center) <= 1
            baseline = float(np.median(aligned["R1-R2"][mask]))
            cross = np.array([np.median(aligned[f"R1-H{i}"][mask]) for i in range(1, 4)])
            contrast.append({"CenterM": float(center), "WidthM": 2, "BaselineMedianGHz": baseline,
                "BetweenMedianMinGHz": float(cross.min()), "BetweenMedianMaxGHz": float(cross.max()),
                "MinimumCrossVsBaselineGHz": float(np.min(np.abs(cross - baseline))),
                "CrossRepeatSpreadGHz": float(np.ptp(cross)),
                "AllCrossSameSignRelativeToBaseline": bool(np.all(cross > baseline) or np.all(cross < baseline)),
                "Interpretation": "Exploratory overlapping windows; one independent Ref pair only; not a heated-position detection"})
        pair_rows, pair_regions, bounded, peak_neighborhood = [], [], [], []
        for path in sorted(args.sample_run.glob("pair_*.json")) + sorted(args.sample_run.glob("control_*.json")):
            j = read(path)
            name, kind = path.stem, "control"
            if name.startswith("pair_"):
                a, b = map(int, name.split("_")[1:3])
                kind = "within" if config["States"][a] == config["States"][b] else "between"
            pair_rows.append({"Name": name, "Kind": kind, **j["Metrics"]})
            x, y, corr = map(np.asarray, [j["DistancesMeters"], j["SpectralShiftGHz"], j["CorrelationPeaks"]])
            if kind != "control":
                for i in np.where((x >= 17.7) & (x <= 18.6))[0]:
                    peak_neighborhood.append({"Name": name, "Kind": kind, "DistanceM": float(x[i]),
                        "ShiftGHz": float(y[i]), "Correlation": float(corr[i]),
                        "PeakSeparation": j["PeakSeparation"][i], "PeakSeparationAvailable": j["PeakSeparationAvailable"][i]})
            for lo, hi in REGIONS:
                mask = (x >= lo) & (x < hi)
                pair_regions.append({"Name": name, "Kind": kind, "RangeM": f"{lo}-{hi}", **describe(y[mask]),
                                     "CorrelationMedian": float(np.median(corr[mask]))})
            if name in ["pair_3_4_raw", "pair_0_3_raw", "control_independent_stationary_noise", "control_self"]:
                bounded.append({"Label": name, "X": x.tolist(), "Y": y.tolist(), "Correlation": corr.tolist()})
        prior_sources = list(csv.DictReader((args.previous_run / "state_comparison/source_metrics.csv").open(encoding="utf-8-sig")))
        prior = [{"File": s["File"], "Dataset": "50M_Sampling", "RawQcPercent": float(s["RawQcPercent"]),
                  "FilteredQcPercent": float(s["FilteredQcPercent"]), "SignalRmsV": float(s["SignalRmsV"]),
                  "PeakEnergyFraction": float(s["energy_fraction_within_peak_plusminus_0.25m"])} for s in prior_sources]
        prior += [{"File": s["File"], "Dataset": "50M_Sampling_2", "RawQcPercent": s["RawQcPercent"],
                   "FilteredQcPercent": s["FilteredQcPercent"], "SignalRmsV": s["SignalRmsV"],
                   "PeakEnergyFraction": s["energy_fraction_within_peak_plusminus_0.25m"]} for s in sources]
        for name, rows in [("source_metrics", sources), ("history_metrics", history_rows), ("history_regions", regions),
                           ("exploratory_2m_contrast", contrast), ("bounded_pairs", pair_rows), ("bounded_regions", pair_regions),
                           ("peak_neighborhood", peak_neighborhood), ("previous_comparison", prior)]:
            table(out / (name + ".csv"), rows)
        write(out / "plot_data.json", {"Histories": histories, "Sources": sources, "Bounded": bounded,
            "Synthetic": read(args.synthetic_run / "synthetic_known_local_shift.json"), "SyntheticMetrics": read(args.synthetic_run / "metrics.json")})
        metrics = {"status": "PASS", "source_hashes_verified": True, "file_count": 6, "unique_file_count": 5,
            "independent_gui_within_pairs": 1, "independent_gui_between_pairs": 3,
            "duplicate_control_abs_max_ghz": history_rows[-1]["AbsMaxGHz"],
            "redundant_comparison_max_difference_ghz": float(np.max(np.abs(aligned["R1-R2"] - aligned["R1-R3"]))),
            "original_numeric_samples_reread_by_this_script": 0, "elapsed_seconds": time.perf_counter() - started,
            "heated_position_identified": False, "full_record_numeric_reprocessing": False}
        write(out / "metrics.json", metrics)
        (out / "summary.md").write_text("# PASS: second 50 MS/s dataset audit\n\nSix files have five unique SHA-256 hashes. Ref (2) and Ref (3) are identical and are excluded from independent-repeat counts. "
            "Six saved GUI runs were hash-verified and copied, including duplicate controls. Source acquisition windows are limited to 230000 samples/file in the separate harness run. "
            "Historical profiles use provisional algebraic 100-to-200 m coordinate conversion, not full reprocessing. Full-record thermal discrimination is not established by this audit.\n", encoding="utf-8")
        print(json.dumps(metrics))
        print(out)
    except BaseException as exc:
        write(out / "metrics.json", {"status": "FAIL", "error": repr(exc), "elapsed_seconds": time.perf_counter() - started})
        (out / "summary.md").write_text(f"# FAIL\n\n{exc!r}\n", encoding="utf-8")
        raise


if __name__ == "__main__":
    main()
