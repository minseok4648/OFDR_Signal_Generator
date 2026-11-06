"""Isolate sample-count/preview effects without accepting a processing candidate."""
import importlib.util
import json
from pathlib import Path
import sys


def load(name,file):
    s=importlib.util.spec_from_file_location(name,Path(__file__).with_name(file))
    m=importlib.util.module_from_spec(s);s.loader.exec_module(m);return m


v=load("validator","Validate-NoticeThermalCandidates.py")
b=v.base
np=b.np
out=Path(sys.argv[1]).resolve()
out.mkdir(parents=True,exist_ok=False)
cfg=json.loads((b.ROOT/"harness/configs/notice_thermal_profile_research.json").read_text(encoding="utf-8"))
c=json.loads((b.ROOT/cfg["base_config"]).read_text(encoding="utf-8"))
c.update(segment_size=1024,segment_hop=256,auxiliary_path_difference_m=18.2703,
         measurement_cable_m=8.7,analysis_start_m=3.5,sample_interval_s=40e-9)
b.write(out/"config.json",dict(processing=c,research=cfg,
        purpose="Hypothesis: 10k min/max preview median changes with bucket parity and is unsuitable for comparing changed sample counts.",
        variants=["baseline","raw_trim64","lowpass_two_stage","lowpass_direct"]))
fft=b.NativeMkl(b.ROOT/c["native_mkl_dll"])
try:
    raw=[]
    for prefix in ["0_0mm0000","0_4mm0000"]:
        aux,_=v.golden_reader.read_isf(Path(c["data_root"]).parents[1]/f"{prefix}CH1.isf")
        sig,_=v.golden_reader.read_isf(Path(c["data_root"]).parents[1]/f"{prefix}CH2.isf")
        a=np.zeros((len(aux),5));a[:,3]=aux;a[:,4]=sig;raw.append(a)
    rows=[]
    for name in ["baseline","raw_trim64","lowpass_two_stage","lowpass_direct"]:
        method="ui_baseline" if name in ["baseline","raw_trim64"] else name
        inputs=[a[64:-64] if name=="raw_trim64" else a for a in raw]
        prepared=[v.research.prepare_candidate(a,c,cfg,method,fft) for a in inputs]
        rs,ss,span=v.research.spectra_for(*prepared,method,fft)
        dx=np.pi*c["auxiliary_path_difference_m"]/span
        y=20*np.log10(np.maximum(np.abs(ss[:len(ss)//2]),1e-300)/np.max(np.abs(ss[:len(ss)//2])))
        x=np.arange(len(y))*dx
        early=y[(x>=4)&(x<=4.5)];late=y[(x>=7.5)&(x<=8)]
        row=dict(variant=name,n=len(ss),preview_difference_db=v.preview_slope(ss,dx),
                 full_bin_median_difference_db=float(np.median(late)-np.median(early)),
                 full_bin_p90_difference_db=float(np.quantile(late,.9)-np.quantile(early,.9)),
                 early_bins=len(early),late_bins=len(late))
        rows.append(row);print(json.dumps(row),flush=True)
        del prepared,rs,ss
    b.write(out/"metrics.json",dict(status="PASS_EXECUTION",variants=rows,
            scientific_acceptance="DIAGNOSIS_ONLY",script_sha256=b.digest(Path(__file__))))
    (out/"summary.md").write_text("# PASS_EXECUTION / DIAGNOSIS_ONLY\n\nSample-count and preview sensitivity isolation; no candidate acceptance implied.\n",encoding="utf-8")
except BaseException as exc:
    b.write(out/"metrics.json",dict(status="FAIL",error=repr(exc)))
    (out/"summary.md").write_text("# FAIL\n\n"+repr(exc),encoding="utf-8");raise
finally:
    fft.close()
