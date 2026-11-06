"""Publish repeatability and ambiguity evidence from completed research runs."""
import argparse
import importlib.util
import json
from pathlib import Path

s=importlib.util.spec_from_file_location("thermal",Path(__file__).with_name("Research-NoticeThermalProfile.py"))
r=importlib.util.module_from_spec(s);s.loader.exec_module(r)
b=r.base
np,pd,plt=b.np,b.pd,b.plt


def main():
    ap=argparse.ArgumentParser()
    ap.add_argument("--run",type=Path,required=True)
    ap.add_argument("--golden",type=Path,required=True)
    args=ap.parse_args()
    out=args.run.resolve()
    execution=json.loads((out/"metrics.json").read_text())
    golden=json.loads((args.golden/"metrics.json").read_text())
    assert execution["status"]=="PASS_EXECUTION" and golden["status"]=="PASS"
    method=pd.read_csv(out/"method_metrics.csv")
    selected=out/"50M_sampling_GainUpdate/lowpass_direct_lead100us"
    diagnostics=json.loads((selected/"correlation_candidates.json").read_text())
    fig,axes=plt.subplots(3,1,figsize=(11,9),constrained_layout=True)
    for ax,pos,d in zip(axes,[6,12,18],diagnostics):
        a=pd.read_csv(selected/f"correlation_{pos}m.csv")
        keep=a.lag.abs()<=2000
        ax.plot(a.loc[keep,"lag"],a.loc[keep,"correlation"],color="#2563eb",lw=1,label="Ref2 -> heat")
        candidates=d["candidates"][:4]
        for p in candidates:
            if abs(p["lag"])<=2000:
                ax.plot(p["lag"],p["correlation"],"o",ms=4,color="#dc2626")
        text="; ".join(f"lag {p['lag']:+d}: r={p['correlation']:.3f}" for p in candidates[:3])
        ax.set(title=f"Center {d['center_m']:.3f} m | {text}",xlabel="Signed correlation lag (bins)",ylabel="Correlation")
        ax.grid(alpha=.2)
    fig.suptitle("Competing correlation peaks in current data\nPeak switching can create step-shaped profiles; this does not diagnose the supplied screenshot",fontsize=12)
    fig.savefig(out/"correlation_ambiguity.png",dpi=150)
    plt.close(fig)
    order=["ui_baseline","lowpass_two_stage","lowpass_direct","lowpass_direct_lead100us","continuous_lowpass_direct"]
    labels=["A: UI","B: LP + 2x","C: LP + direct","D: + lead100us","E: continuous"]
    fig,ax=plt.subplots(1,2,figsize=(13,5),constrained_layout=True)
    for offset,folder,label,color in [(-.18,"50M_sampling_GainUpdate","GainUpdate","#2563eb"),(.18,"50M_Sampling_2","Sampling_2 validation","#dc2626")]:
        rows=method[method.folder==folder].set_index("method").loc[order]
        x=np.arange(5)+offset
        ax[0].bar(x,rows.phase_qc_passed_sources,.36,label=label,color=color)
        ax[1].bar(x,rows.distributed_between_correlation,.36,label=label,color=color)
    ax[0].set(ylabel="Sources passing phase QC (out of 5)",ylim=(0,5.8))
    ax[1].set(ylabel="Median distributed Ref/Heating correlation",ylim=(0,.6))
    ax[1].text(.02,.98,"E: ~15.4m gauge; no distributed window\nin GainUpdate after reflector exclusion",transform=ax[1].transAxes,va="top",fontsize=8)
    for a in ax:
        a.set(xticks=np.arange(5),xticklabels=labels)
        a.tick_params(axis="x",rotation=20);a.grid(axis="y",alpha=.2);a.set_axisbelow(True);a.legend(fontsize=8)
    fig.suptitle("Phase QC improves; distributed thermal-profile repeatability remains insufficient",fontsize=12)
    fig.savefig(out/"method_comparison.png",dpi=150)
    plt.close(fig)
    control=pd.read_csv(selected/"pair_3_4_profile.csv")
    worst=control.loc[control.shift_ghz.abs().idxmax()]
    first=diagnostics[0]["candidates"]
    interpretation=dict(status="COMPLETED_RESEARCH",decision="REJECT_PRODUCTION_ADOPTION",
           qualitative_step_shapes_observed=True,validated_thermal_profile_reproduced=False,
           image_temperature_mapping_available=False,image_axes_known=False,
           method_groups=10,pair_calculations=100,
           candidate_points_before_phase_gate=int(method.candidate_points.sum()),accepted_points=int(method.accepted_points.sum()),
           same_state_outlier=dict(reference="ref2.csv",signal="ref3.csv",distance_m=float(worst.distance_m),
               shift_ghz=float(worst.shift_ghz),correlation=float(worst.peak_correlation),margin=float(worst.peak_margin)),
           example_competing_peaks=dict(center_m=diagnostics[0]["center_m"],candidates=first[:4],
               second_to_first_peak_ratio=first[1]["correlation"]/first[0]["correlation"]),
           golden_run=str(args.golden.resolve()),golden_methods_passed=len(golden["methods"]),
           script_sha256=b.digest(Path(__file__)))
    b.write(out/"interpretation.json",interpretation)
    profiles=list(out.glob("*/*/pair_*_profile.csv"))
    assert len(profiles)==100
    maxpoints=0
    for file in profiles:
        p=pd.read_csv(file)
        assert np.isfinite(p.select_dtypes(include="number")).all().all()
        assert p.distance_m.is_monotonic_increasing and p.distance_m.max()<=23
        maxpoints=max(maxpoints,len(p))
    b.write(out/"artifact_audit.json",dict(status="PASS",profile_csvs=len(profiles),all_profile_numbers_finite=True,
            all_profile_axes_increasing=True,maximum_profile_points=maxpoints,
            executed_candidate_hash_matches_current=b.digest(Path(r.__file__))==json.loads((out/"environment.json").read_text())["analyzer_sha256"],
            golden_status=golden["status"]))
    text=["# Thermal profile research completed", "", "Decision: **REJECT_PRODUCTION_ADOPTION**.", "",
          "Current Ref/Heating files reproduce step-shaped diagnostic curves, but a validated thermal profile matching the reference was not established.",
          "The screenshot has no matching source file; its axes and temperature mapping are unavailable.", "",
          "10 original files / 10M numeric rows / 5 methods / 100 pair calculations / 0 accepted points.",
          "GainUpdate phase QC: 0/5 baseline, 4/5 with CH3 low-pass + direct interpolation + 0.1ms lead exclusion, 5/5 in continuous windows.",
          "Continuous 4096-bin gauge is ~15.4m, preventing narrow thermal localization in this control.",
          f"Same-state Ref2/Ref3 outlier: {worst.shift_ghz:.6f} GHz at {worst.distance_m:.4f}m, correlation {worst.peak_correlation:.4f}.",
          "All three full real 5M golden variants pass plateau, outside-baseline, and full-resolution FFT slope checks.",
          "", "See method_metrics.csv, interpretation.json, per-method profile_overlay.png, correlation_ambiguity.png, and the detailed Korean report.",
          "", "Report: docs/report/20261106_notice_reference_thermal_profile_research.md"]
    (out/"summary.md").write_text("\n".join(text)+"\n",encoding="utf-8")
    print(json.dumps(interpretation,indent=2))


if __name__=="__main__":
    main()
