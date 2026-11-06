"""Compare saved GUI outputs without repeating multi-million-sample processing.

Usage: python Compare-NoticeWindowDurations.py --sample-run <bounded diagnostic run>
Uses an explicit, frozen list of 20, 40 and 168.07969 ms GUI runs, not 'latest'.
"""
import argparse
import csv
import hashlib
import json
import math
from pathlib import Path
import shutil
import statistics
import time

ROOT = Path(__file__).resolve().parents[2]
RUNS = [
    ("20 ms", "20261106_005216_780_prepared_ofdr_analysis_b8ee2b"),
    ("40 ms", "20261106_012903_509_prepared_ofdr_analysis_a00d61"),
    ("168.08 ms", "20261106_004426_668_prepared_ofdr_analysis_02c1fa"),
]


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def save(path, value):
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def table(path, rows):
    with Path(path).open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)


def quantile(values, p):
    values = sorted(values)
    return values[math.floor((len(values) - 1) * p)] if values else None


def coords(series):
    n = len(series["Values"])
    return [series["XStart"] + (series["XEnd"] - series["XStart"]) * i / max(1, n - 1) for i in range(n)]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--sample-run", type=Path, required=True)
    args = parser.parse_args()
    out = args.sample_run.resolve() / "duration_comparison"
    out.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()
    save(out / "config.json", {"runs": RUNS, "source": str(ROOT / "OFDR_Signal_Generator/bin/Debug/harness/results"),
         "numeric_scope": "Reuse saved full-run GUI profiles; no original CSV reread; bounded raw diagnostics separately logged",
         "requested_long_window_ms": 160, "available_saved_long_window_ms": 168.07969,
         "temperature_state": "Same maintained temperature repeated acquisition, confirmed by user",
         "provisional_travel_path_m": 200, "product_changes": False})
    rows, regions, fft_regions, histories, pairs, sample_metrics = [], [], [], [], [], []
    try:
        for label, name in RUNS:
            folder = ROOT / "OFDR_Signal_Generator/bin/Debug/harness/results" / name
            snap = out / "snapshots" / name
            snap.mkdir(parents=True, exist_ok=True)
            manifest = []
            for filename in ["config.json", "processing_config.json", "ref_preparation.json", "signal_preparation.json", "result.json", "metrics.json", "summary.md"]:
                source = folder / filename
                shutil.copy2(source, snap / filename)
                manifest.append({"path": str(source), "sha256": hashlib.sha256(source.read_bytes()).hexdigest(), "bytes": source.stat().st_size})
            save(snap / "manifest.json", manifest)
            result = read(snap / "result.json")
            config = read(snap / "processing_config.json")
            ref = read(snap / "ref_preparation.json")
            sig = read(snap / "signal_preparation.json")
            axis = result["PhysicalAxis"]
            step = axis["DistanceMetersPerFftBin"]
            maximum_lag = next(m["Value"] for m in result["PerformanceMetrics"] if m["Name"] == "maximum_correlation_search_lag")
            row = {"Label": label, "Run": name, "SelectedMs": (ref["SelectedEndIndexExclusive"] - ref["SelectedStartIndex"]) * ref["SampleIntervalSeconds"] * 1000,
                   "SourceStart": ref["Diagnosis"]["SourceStartIndex"], "SourceEndInclusive": ref["Diagnosis"]["SourceEndIndexInclusive"],
                   "SourceCoveredMs": (ref["Diagnosis"]["SourceEndIndexInclusive"] - ref["Diagnosis"]["SourceStartIndex"] + 1) * ref["SampleIntervalSeconds"] * 1000,
                   "AuxiliaryPathM": config["AuxiliaryPathDifferenceMeters"], "DistanceBinM": step,
                   "GaugeM": config["SegmentSize"] * step, "HopM": config["SegmentHop"] * step,
                   "GaugeMAt200": config["SegmentSize"] * step * 200 / config["AuxiliaryPathDifferenceMeters"],
                   "BinsForHalfMeterAt200": round(.5 / (step * 200 / config["AuxiliaryPathDifferenceMeters"])),
                   "HopBinsFor125mmAt200": round(.125 / (step * 200 / config["AuxiliaryPathDifferenceMeters"])),
                   "RefQcPercent": 100 * result["KLinearization"]["NonmonotonicRatio"],
                   "SigQcPercent": 100 * result["SignalKLinearization"]["NonmonotonicRatio"],
                   "RefJoins": ref["Diagnosis"]["JoinCount"], "SigJoins": sig["Diagnosis"]["JoinCount"],
                   "SpanGHzAtStoredPath": axis["SweepSpanGHz"], "SpanGHzAt200": axis["SweepSpanGHz"] * config["AuxiliaryPathDifferenceMeters"] / 200,
                   "EffectiveSearchHalfGHzAtStoredPath": maximum_lag * axis["SpectralShiftGHzPerLag"],
                   "ProfilePoints": len(result["SpectralShiftProfile"]["Values"]),
                   "SelectedCorrelationDistance": result["SelectedSegment"]["SegmentCenterDistanceMeters"],
                   "SelectedCorrelationPeak": result["SelectedSegment"]["PeakMagnitude"],
                   "SelectedCorrelationShiftGHz": result["SelectedSegment"]["PeakSpectralShiftGHz"],
                   "RefSourceSha256": ref["SourceSha256"], "SigSourceSha256": sig["SourceSha256"]}
            rows.append(row)
            assert row["RefSourceSha256"] == rows[0]["RefSourceSha256"] and row["SigSourceSha256"] == rows[0]["SigSourceSha256"], "GUI histories must use the same two raw files."
            assert config["AuxiliaryPathDifferenceMeters"] == 100 and config["SegmentSize"] == 1024 and config["SegmentHop"] == 256
            assert config["AnalysisStartMeters"] == 0 and config["AnalysisEndMeters"] == 50 and config["AllowFailedPhaseQcForDiagnostics"]
            assert ref["ModeHopExclusionTailSeconds"] == sig["ModeHopExclusionTailSeconds"] == .0003
            assert ref["SelectedStartIndex"] == sig["SelectedStartIndex"] == 0
            profile = result["SpectralShiftProfile"]
            x = coords(profile)
            assert len(x) < 10000, "Stats require uncompressed result profile, not reduced preview."
            for lo, hi in [(0, 12), (12, 24), (24, 50)]:
                values = [v for d, v in zip(x, profile["Values"]) if lo <= d < hi]
                regions.append({"Label": label, "DisplayedRangeM": f"{lo}-{hi}", "Count": len(values),
                                "ShiftP10GHz": quantile(values, .1), "ShiftMedianGHz": quantile(values, .5), "ShiftP90GHz": quantile(values, .9),
                                "Within1GHzFraction": sum(abs(v) <= 1 for v in values) / len(values),
                                "Within5GHzFraction": sum(abs(v) <= 5 for v in values) / len(values),
                                "Interpretation": "Descriptive only; these are not accepted QC limits or verified FUT boundaries"})
            for channel in ["ReferenceSpectrum", "SignalSpectrum"]:
                preview = result[channel]
                # Min/max preview pairs cover source bins. Retain bucket bounds, not fictitious exact peak locations.
                n = int(round(preview["XEnd"] / step)) + 1
                count = len(preview["Values"]) // 2
                for lo, hi in [(0, 12), (12, 24), (24, 50), (100, 150)]:
                    buckets = [(i * n // count * step, ((i + 1) * n // count - 1) * step,
                                max(preview["Values"][2*i:2*i+2])) for i in range(count)]
                    values = [v for a, b, v in buckets if a >= lo and b < hi]
                    peak = max((b for b in buckets if b[0] >= lo and b[1] < hi), key=lambda b: b[2])
                    fft_regions.append({"Label": label, "Channel": channel, "DisplayedRangeM": f"{lo}-{hi}",
                                        "MedianBucketMaxDb": statistics.median(values), "MaxDb": peak[2],
                                        "PeakBucketStartM": peak[0], "PeakBucketEndM": peak[1],
                                        "Interpretation": "Individually normalized min/max FFT preview envelope; not calibrated power or absolute SNR"})
            histories.append({"Label": label, "Summary": row, "Profile": profile,
                              "ReferenceSpectrum": result["ReferenceSpectrum"], "SignalSpectrum": result["SignalSpectrum"]})
        for role in range(2):
            source = read(args.sample_run / f"source_{role}.json")
            expected = rows[0]["RefSourceSha256" if role == 0 else "SigSourceSha256"]
            assert source["Sha256"].lower() == expected.lower(), "Bounded sample and GUI source hashes must match."
            for w in range(1, 4):
                context = read(args.sample_run / f"file{role}_w{w}_context.json")
                for variant in ["raw100", "filtered50", "filtered20", "unfiltered20"]:
                    j = read(args.sample_run / f"file{role}_w{w}_{variant}_aux.json")
                    sample_metrics.append({"Role": role, "Window": w, "Variant": variant, "StartMs": context["record_relative_start_s"] * 1000,
                                           "QcPercent": 100 * j["Phase"]["NonmonotonicRatio"], "InteriorQcPercent": 100 * j["Values"]["interior_nonmonotonic_ratio"],
                                           "AuxRmsMv": 1000 * j["Values"]["ac_rms_v"], "PhaseAverageMHz": j["Values"]["phase_average_hz"] / 1e6,
                                           "Above10MHzPercent": 100 * j["Values"]["energy_above_10mhz_fraction"],
                                           "ModeHopHighFraction": context["mode_hop_high_fraction"], "CH4Levels": context["measurement_distinct_voltage_levels"],
                                           "CH4VoltageStep": context["measurement_minimum_voltage_step_v"]})
        for filename in sorted(args.sample_run.glob("pair_w*.json")) + sorted(args.sample_run.glob("control_mismatched*.json")):
            j = read(filename)
            pairs.append({"Name": filename.stem, **j["Metrics"]})
        for name, data in [("duration_metrics", rows), ("distance_region_statistics", regions), ("fft_envelope_statistics", fft_regions),
                           ("bounded_phase_metrics", sample_metrics), ("bounded_pair_metrics", pairs)]:
            table(out / (name + ".csv"), data)
        save(out / "plot_data.json", {"histories": histories, "bounded_phase": sample_metrics})
        save(out / "metrics.json", {"status": "PASS", "history_count": len(rows), "source_hashes_match": True,
              "original_samples_reread": 0, "profile_points_analyzed": sum(r["ProfilePoints"] for r in rows),
              "elapsed_seconds": time.perf_counter() - started,
              "full_processing_repeated": False, "exact_160ms_run_available": False})
        (out / "summary.md").write_text("# PASS: saved-duration comparison\n\n20, 40 and 168.07969 ms saved GUI results compared; an exact 160 ms run was not present. "
            "All three use the same raw-file hashes, 100 m path setting, 1024 bins, 0.3 ms exclusion tail, diagnostic QC override and 0-50 m analysis. "
            "No full raw data was reread. Bucket bounds are retained for reduced FFT previews. "
            "Source numeric checks use the separately recorded 245760 samples per file. "
            "No production processing or UI changes, no new full-record or golden execution.\n", encoding="utf-8")
    except BaseException as exc:
        save(out / "metrics.json", {"status": "FAIL", "error": repr(exc), "elapsed_seconds": time.perf_counter() - started})
        (out / "summary.md").write_text(f"# FAIL\n\n{exc!r}\n", encoding="utf-8")
        raise
    print(out)


if __name__ == "__main__":
    main()
