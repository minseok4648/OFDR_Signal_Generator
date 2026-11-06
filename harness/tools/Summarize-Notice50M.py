"""Summarize completed experiments without changing any peak or processing result."""
import argparse
import importlib.util
import json
from pathlib import Path

spec = importlib.util.spec_from_file_location("notice", Path(__file__).with_name("Analyze-Notice50M.py"))
n = importlib.util.module_from_spec(spec)
spec.loader.exec_module(n)
np, pd, plt = n.np, n.pd, n.plt


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", type=Path, required=True)
    ap.add_argument("--golden", type=Path, required=True)
    args = ap.parse_args()
    run = args.run.resolve()
    c = json.loads((run/"config.json").read_text(encoding="utf-8"))
    s, p = pd.read_csv(run/"source_metrics.csv"), pd.read_csv(run/"pair_metrics.csv")
    manifest = json.loads((run/"dataset_manifest.json").read_text(encoding="utf-8"))
    execution = json.loads((run/"metrics.json").read_text(encoding="utf-8"))
    if execution["status"] != "PASS_EXECUTION":
        raise RuntimeError("Refusing to summarize an incomplete full run")
    groups = []
    for folder in c["folders"]:
        src, pairs = s[s.folder == folder], p[p.folder == folder]
        row = dict(folder=folder, physical_files=sum(m["folder"] == folder for m in manifest),
                   analyzed_sources=len(src), comparisons=len(pairs),
                   phase_qc_passed=int(src.phase_qc_passed.sum()),
                   full_qc_reject_min_percent=float(src.nonmonotonic_ratio.min()*100),
                   full_qc_reject_max_percent=float(src.nonmonotonic_ratio.max()*100),
                   contiguous_qc_passed=int(src.longest_contiguous_phase_qc_passed.sum()),
                   gauge_min_m=float(pairs.gauge_length_m.min()), gauge_max_m=float(pairs.gauge_length_m.max()),
                   hop_min_m=float(pairs.hop_m.min()), hop_max_m=float(pairs.hop_m.max()),
                   dominant_peak_min_m=float(src.dominant_peak_m.min()), dominant_peak_max_m=float(src.dominant_peak_m.max()),
                   dominant_pm_0p1m_median_energy_percent=float(src.dominant_peak_pm_0p1m_energy_fraction.median()*100),
                   fft_noise_floor_slope_db_per_m=float(src.median_db_slope_per_m.median()))
        for kind in ["within", "between"]:
            subset = pairs[pairs.kind == kind]
            row[f"{kind}_comparisons"] = len(subset)
            row[f"{kind}_median_abs_shift_ghz"] = float(subset.median_abs_shift_ghz.median())
            row[f"{kind}_median_correlation"] = float(subset.median_peak_correlation.median())
        groups.append(row)
    pd.DataFrame(groups).to_csv(run/"group_summary.csv", index=False)
    # Same instrument scale only, excluding the copied pre-gain Ref.
    gain = p[(p.folder == "50M_sampling_GainUpdate") & (p.ref != "ref.csv") & (p.sig != "ref.csv")]
    gain_summary = gain.groupby("kind")[["median_abs_shift_ghz", "median_peak_correlation"]].median().reset_index().to_dict("records")
    source_files = ["OFDR_Signal_Generator/MainWindow.xaml.cs", "OFDR_Signal_Generator/MainWindow.xaml",
                    "OFDR_Signal_Generator/ViewModels/OfdrWorkspaceViewModel.cs",
                    "OFDR_Signal_Generator/ViewModels/ModeHopPreviewViewModel.cs",
                    "OFDR_Signal_Generator/ViewModels/SweepPreparationViewModel.cs",
                    "OFDR_Signal_Core/Services/TekScopeCsvReader.cs",
                    "OFDR_Signal_Core/Processing/SweepPreparationEngine.cs",
                    "OFDR_Signal_Core/Processing/ModeHopPreviewEngine.cs",
                    "OFDR_Signal_Core/Processing/AuxiliaryQualityEngine.cs",
                    "OFDR_Signal_Core/Processing/AuxiliaryPhaseExtractor.cs",
                    "OFDR_Signal_Core/Processing/AuxKLinearizer.cs",
                    "OFDR_Signal_Core/Processing/IndependentOfdrProcessingEngine.cs"]
    n.write(run/"ui_source_audit.json", dict(scope="Current workspace source, including pre-existing uncommitted edits; live GUI controls were not read",
            source_files=[dict(path=name, sha256=n.digest(n.ROOT/name)) for name in source_files],
            defaults=dict(segment=1024, hop=256, padding=16, physical_path_difference_m=18.2703,
                          analysis_start_m=3.5, analysis_end_m=8.7, mode_hop_tail_ms=0),
            experiment_overrides=dict(segment=4096, hop=128, coil_one_way_m=100, physical_path_difference_m=200,
                                      analysis_start_m=0, analysis_end_m=23, mode_hop_tail_ms=.3),
            raw_reader_difference="C# rejects CH3 infinity before masking; Python permits only infinity already masked by CH2 High/CH4 policy. Zero nonfinite CH3 values reach phase estimation."))
    n.write(run/"interpretation.json", dict(decision="REJECT_SENSING_VALIDATION", execution_success=True,
            reason="Full stitched phase QC fails in every analyzed source; same-state reproducibility and distributed high correlation are insufficient to assign a heated region or calibrated strain.",
            groups=groups, gain_consistent_subset=gain_summary,
            duplicates=[m for m in manifest if m["duplicate_of"]],
            strain_interpretation="Apparent strain only; uncalibrated and cross-sensitive to temperature",
            golden_run=str(args.golden.resolve()), summarizer_sha256=n.digest(Path(__file__))))
    labels = ["Sampling 1", "Sampling 2", "Sampling 3", "GainUpdate"]
    fig, axes = plt.subplots(2, 2, figsize=(12, 8), constrained_layout=True)
    positions = np.arange(4)
    for offset, kind, label, color in [(-.18,"within","Same state", "#2563eb"), (.18,"between","Different states", "#dc2626")]:
        axes[0, 0].bar(positions+offset, [g[f"{kind}_median_correlation"] for g in groups], .36, label=label, color=color)
        axes[0, 1].bar(positions+offset, [g[f"{kind}_median_abs_shift_ghz"] for g in groups], .36, label=label, color=color)
    axes[0, 0].set(ylabel="Median peak correlation", ylim=(0, 1))
    axes[0, 0].axhline(.8, ls=":", color="black", lw=.8)
    axes[0, 0].legend()
    axes[0, 1].set(ylabel="Median |spectral shift| (GHz)")
    axes[0, 1].text(.02,.98,"Small shift with low correlation\ndoes not establish zero strain.", transform=axes[0, 1].transAxes, va="top", fontsize=9)
    for i, folder in enumerate(c["folders"]):
        v = s[s.folder == folder].nonmonotonic_ratio.to_numpy()*100
        axes[1, 0].scatter(np.full(len(v), i)+np.linspace(-.1,.1,len(v)), v, color="#0f766e", s=30)
    axes[1, 0].axhline(c["max_nonmonotonic_ratio"]*100, color="#dc2626", ls="--", label="QC limit")
    axes[1, 0].set(yscale="log", ylabel="Rejected phase anchors (%)")
    axes[1, 0].legend()
    axes[1, 1].bar(positions, [g["gauge_max_m"] for g in groups], color="#0f766e")
    axes[1, 1].set(ylabel="4096-bin segment width (m)", ylim=(0, 5.5))
    for i,g in enumerate(groups):
        axes[1, 1].text(i, g["gauge_max_m"]+.08, f"{g['gauge_min_m']:.2f}–{g['gauge_max_m']:.2f}", ha="center", fontsize=9)
    for ax in axes.flat:
        ax.set(xticks=positions, xticklabels=labels)
        ax.grid(axis="y", alpha=.2)
        ax.set_axisbelow(True)
    fig.suptitle("50 MS/s full-record analysis | 100 m coil / ~200 m OPD | segment 4096 / hop 128 / cable 23 m\nAll full stitched inputs fail phase QC; profiles are diagnostic", fontsize=12)
    fig.savefig(run/"comparison_summary.png", dpi=160)
    plt.close(fig)
    golden = json.loads((args.golden/"metrics.json").read_text(encoding="utf-8"))
    lines = ["# Full-record Python analysis / PASS_EXECUTION", "",
             "Decision: **REJECT_SENSING_VALIDATION**. Execution completed; sensing accuracy was not established.", "",
             f"- {execution['source_files']} files / {execution['source_numeric_rows']:,} numeric rows / {execution['unique_source_hashes']} unique hashes / {execution['pairs']} pair comparisons.",
             "- User confirmed 100 m one-way coil: approximate physical travel-path difference 200 m. Analysis was rerun, including segment selection and correlation, over 0–23 m.",
             "- Segment 4096, hop 128, Hann, zero padding 16; no filtering or QC relaxation.",
             "- Full-source phase QC: 0/22 pass. GainUpdate continuous intervals: 5/6 pass; this does not validate stitched phase or thermal localization.",
             f"- Independent real 5M golden: plateau {golden['inside_median_ghz']:.6f} GHz, outside {golden['outside_median_ghz']:.6f} GHz, archived-preview slope {golden['ui_preview_slope_band_difference_db']:.6f} dB.", "",
             "| Folder | Same-state corr. | Different-state corr. | Gauge (m) | Dominant peak (m) |",
             "|---|---:|---:|---:|---:|"]
    for g in groups:
        lines.append(f"| {g['folder']} | {g['within_median_correlation']:.3f} | {g['between_median_correlation']:.3f} | {g['gauge_min_m']:.2f}–{g['gauge_max_m']:.2f} | {g['dominant_peak_min_m']:.2f}–{g['dominant_peak_max_m']:.2f} |")
    lines += ["", "See group_summary.csv, source_metrics.csv, pair_metrics.csv, region_metrics.csv, all *_profile.csv files, dataset_manifest.json, controls.json, and interpretation.json.",
              "", "Detailed Korean report: docs/report/20261106_notice50m_python_4096_128_analysis.md."]
    (run/"summary.md").write_text("\n".join(lines)+"\n", encoding="utf-8")
    print(json.dumps(dict(groups=groups, gain_consistent_subset=gain_summary), indent=2))


if __name__ == "__main__":
    main()
