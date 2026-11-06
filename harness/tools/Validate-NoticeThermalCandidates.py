"""Full real 5M golden regression of the new filtering/direct-grid candidates."""
import argparse
import importlib.util
import json
from pathlib import Path
import time
import traceback


def module(name, filename):
    s = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    m = importlib.util.module_from_spec(s)
    s.loader.exec_module(m)
    return m


research = module("thermal", "Research-NoticeThermalProfile.py")
golden_reader = module("golden", "Validate-NoticePythonGolden.py")
base = research.base
np, ROOT, write = base.np, base.ROOT, base.write


def preview_slope(spectrum, dx):
    db = 20*np.log10(np.maximum(np.abs(spectrum[:len(spectrum)//2]),1e-300)/np.max(np.abs(spectrum[:len(spectrum)//2])))
    preview = []
    for bucket in range(5000):
        part = db[bucket*len(db)//5000:(bucket+1)*len(db)//5000]
        mi,ma = int(np.argmin(part)),int(np.argmax(part))
        preview.extend([part[mi],part[ma]] if mi <= ma else [part[ma],part[mi]])
    x = np.linspace(0,(len(db)-1)*dx,len(preview))
    y = np.asarray(preview)
    return float(np.median(y[(x>=7.5)&(x<=8)])-np.median(y[(x>=4)&(x<=4.5)]))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--output",type=Path,required=True)
    args = ap.parse_args()
    out = args.output.resolve()
    out.mkdir(parents=True,exist_ok=False)
    r = json.loads((ROOT/"harness/configs/notice_thermal_profile_research.json").read_text(encoding="utf-8"))
    c = json.loads((ROOT/r["base_config"]).read_text(encoding="utf-8"))
    c.update(segment_size=1024,segment_hop=256,auxiliary_path_difference_m=18.2703,
             measurement_cable_m=8.7,analysis_start_m=3.5,sample_interval_s=40e-9)
    write(out/"config.json",dict(processing=c,research=r,methods=["ui_baseline","lowpass_two_stage","lowpass_direct"],
          gates=dict(inside_expected_ghz=25.984624,inside_tolerance_ghz=.1,outside_max_abs_ghz=.2,
                     baseline_expected_preview_slope_db=-10.385419,slope_tolerance_db=1.,
                     candidate_slope_metric="Full-resolution median and p90 band differences must remain negative and within 1dB of baseline.",
                     metric_revision_evidence="20261106_notice_thermal_golden_slope_diagnosis_v1: trim-only changes preview sign, while full-bin median/p90 slopes remain stable.")))
    start = time.perf_counter()
    fft = None
    try:
        fft = base.NativeMkl(ROOT/c["native_mkl_dll"])
        raw,sources = [],[]
        for prefix in ["0_0mm0000","0_4mm0000"]:
            aux,ma = golden_reader.read_isf(Path(c["data_root"]).parents[1]/f"{prefix}CH1.isf")
            sig,ms = golden_reader.read_isf(Path(c["data_root"]).parents[1]/f"{prefix}CH2.isf")
            a = np.zeros((len(aux),5))
            a[:,3],a[:,4] = aux,sig
            raw.append(a)
            sources.extend([ma,ms])
        write(out/"dataset_manifest.json",sources)
        rows=[]
        fig,ax = base.plt.subplots(2,1,figsize=(11,8),constrained_layout=True)
        for method in ["ui_baseline","lowpass_two_stage","lowpass_direct"]:
            prepared = [research.prepare_candidate(a,c,r,method,fft) for a in raw]
            rs,ss,span = research.spectra_for(*prepared,method,fft)
            p,axis = base.profile(rs,ss,span,fft,c)
            p.to_csv(out/f"{method}_profile.csv",index=False)
            inside = float(p[(p.distance_m>=5.5)&(p.distance_m<=6.5)].shift_ghz.median())
            outside = float(p[((p.distance_m>=3.7)&(p.distance_m<=5.3))|((p.distance_m>=6.7)&(p.distance_m<=8.4))].shift_ghz.median())
            slope = preview_slope(ss,axis["distance_m_per_bin"])
            x = np.arange(len(ss)//2)*axis["distance_m_per_bin"]
            db = 20*np.log10(np.maximum(np.abs(ss[:len(ss)//2]),1e-300)/np.max(np.abs(ss[:len(ss)//2])))
            early,late = db[(x>=4)&(x<=4.5)],db[(x>=7.5)&(x<=8)]
            full_slope = float(np.median(late)-np.median(early))
            p90_slope = float(np.quantile(late,.9)-np.quantile(early,.9))
            if method == "ui_baseline":
                baseline_full,baseline_p90 = full_slope,p90_slope
            slope_pass = full_slope<0 and p90_slope<0 and abs(full_slope-baseline_full)<1 and abs(p90_slope-baseline_p90)<1
            if method == "ui_baseline":
                slope_pass &= abs(slope+10.385419)<1
            passed = bool(abs(inside-25.984624)<.1 and abs(outside)<.2 and slope_pass and
                          all(q["metrics"]["phase_qc_passed"] for q in prepared))
            row = dict(method=method,inside_median_ghz=inside,outside_median_ghz=outside,
                       preview_slope_band_difference_db=slope,full_bin_median_slope_difference_db=full_slope,
                       full_bin_p90_slope_difference_db=p90_slope,
                       phase_qc=[q["metrics"] for q in prepared],pass_all=passed)
            rows.append(row)
            ax[0].plot(p.distance_m,p.shift_ghz,lw=.8,label=method)
            ax[1].plot(p.distance_m,p.peak_correlation,lw=.8,label=method)
            print(method,inside,outside,slope,passed,flush=True)
            del prepared,rs,ss
        ax[0].axvspan(5.5,6.5,color="orange",alpha=.15)
        ax[0].set(xlabel="Distance (m)",ylabel="Shift (GHz)",title="Full 5M/channel: known 0.4 mm strain plateau")
        ax[1].set(xlabel="Distance (m)",ylabel="Peak normalized correlation")
        for a in ax:
            a.legend();a.grid(alpha=.2)
        fig.savefig(out/"golden_candidates.png",dpi=150)
        base.plt.close(fig)
        metrics = dict(status="PASS" if all(r["pass_all"] for r in rows) else "FAIL",methods=rows,
                       input_samples_per_channel=5000000,numeric_source_samples=20000000,
                       native_fft_calls=fft.calls,elapsed_seconds=time.perf_counter()-start,
                       candidate_script_sha256=base.digest(Path(research.__file__)),
                       validation_script_sha256=base.digest(Path(__file__)))
        write(out/"metrics.json",metrics)
        (out/"summary.md").write_text("# "+metrics["status"]+"\n\nFull 5M original golden processing of baseline, filtered two-stage, and filtered direct-grid methods. See metrics.json.\n",encoding="utf-8")
        if metrics["status"] != "PASS":
            raise RuntimeError("A candidate failed golden validation; retain metrics and investigate")
    except BaseException as exc:
        if not (out/"metrics.json").exists():
            write(out/"metrics.json",dict(status="FAIL",error=repr(exc),elapsed_seconds=time.perf_counter()-start))
        (out/"summary.md").write_text("# FAIL\n\n"+traceback.format_exc(),encoding="utf-8")
        raise
    finally:
        if fft is not None:
            fft.close()


if __name__ == "__main__":
    main()
