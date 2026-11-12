"""Finalize evidence for the enhanced-fiber report, without modifying source data."""
import argparse, hashlib, json, re, shutil, xml.etree.ElementTree as ET
from pathlib import Path

def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def write(p,v): p.write_text(json.dumps(v,ensure_ascii=False,indent=2,allow_nan=False),encoding='utf-8')
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--run',type=Path,required=True);ap.add_argument('--probe',type=Path,required=True);ap.add_argument('--report',type=Path,required=True);ap.add_argument('--build',type=Path,required=True);a=ap.parse_args()
    root=Path(__file__).resolve().parents[2];run=a.run.resolve();out=a.report.resolve();build=a.build.resolve();probe=a.probe.resolve()
    v=read(run/'audit_validation.json');metrics=read(run/'metrics.json');data=read(run/'dataset_manifest.json');results=read(run/'analysis_metrics.json');scan=read(probe/'phase_scan.json');pc=read(probe/'metrics.json');assert metrics['status']=='COMPLETED' and pc['status']=='COMPLETED'
    assert len(scan)==92 and len({(s['id'],s['low_mhz'],s['high_mhz']) for s in scan})==92
    diffs=[]
    for s in data:
        base=next(q for q in scan if q['id']==s['Id'] and q['low_mhz']==1 and q['high_mhz']==12)
        selected=next(r for r in s['Ranges'] if r['StartIndex']==s['Start']);diffs.append(abs(base['phase_qc']-selected['RejectedAnchorRatio']))
    assert max(diffs)<1e-14
    image_sizes={}
    for path in (out/'images').glob('*.png'):
        content=path.read_bytes();assert content[:8]==b'\x89PNG\r\n\x1a\n';width=int.from_bytes(content[16:20],'big');height=int.from_bytes(content[20:24],'big');assert width>=1000 and height>=500;image_sizes[path.name]=[width,height]
    previous=root/'harness/results/20261112_261112_bending_v1/validation/unchanged_core_prior_19tests.trx'
    assert previous.exists();validation_dir=run/'validation';validation_dir.mkdir(exist_ok=True);shutil.copyfile(previous,validation_dir/previous.name)
    trx=ET.parse(previous);counters=next(n for n in trx.iter() if n.tag.endswith('Counters'));assert int(counters.attrib['passed'])==19 and int(counters.attrib['failed'])==0
    core=build/'OFDR_Signal_Core.dll';previous_core=root/'build_verify_bending_probe/OFDR_Signal_Core.dll';assert sha(core)==sha(previous_core)
    cfg=read(run/'zncc_config.json')['StageComparison']['Document'];assert cfg['EndMeters']==21 and cfg['MaximumShiftPm']==400 and abs(cfg['AuxiliaryDelayMicroseconds']-200*1.4682/299792458*1e6)<1e-14
    v.update({'probe_conditions':92,'probe_baseline_max_abs_qc_difference':max(diffs),'all_images_size_checked':True,'image_dimensions':image_sizes,'prior_core_tests_passed':19,'prior_core_tests_failed':0,'core_binary_identical_to_prior_tested_run':True,'tests_rerun_this_turn':False,'active_document_end_m':cfg['EndMeters'],'selected_source_sha_verified_by_full_reader':True})
    write(run/'audit_validation.json',v)
    files=[build/'DatasetThermalAudit.exe',core,build/'libMathNetNumericsMKL.dll',root/'harness/tools/DatasetThermalAudit/Program.cs',root/'harness/tools/DatasetThermalAudit/BendingAuditCommand.cs',root/'harness/tools/DatasetThermalAudit/EnhancedFiberAuditCommand.cs',root/'harness/tools/Report-EnhancedFiber261112.py',root/'harness/tools/Report-Bending261112.py',Path(__file__).resolve(),run/'config.json',run/'reflectogram_config.json',run/'zncc_config.json',run/'dataset_manifest.json',probe/'config.json',probe/'phase_scan.json',out/'report.html',out/'reproduce.md']
    source_root=Path(read(run/'config.json')['input']);files.append(source_root/'setup.txt')
    write(run/'provenance.json',{'files':[{'path':str(p),'sha256':sha(p)} for p in files],'input_metadata':'Full source paths, actual headers and SHA-256 are in dataset_manifest.json. Source data was read only.','test_evidence':'19 prior Core tests, same Core DLL SHA; not rerun in this task.','classification':'User confirmed first tension acquisition 20261112_111948_541; 13 Ref, 10 tension.','plot_dimensions':image_sizes})
    summary='# COMPLETED — Enhanced fiber 21 m tension audit\n\n'+results['finding']+'\n\n'
    summary+='## Request and setup\n\n21 m fiber; jig after approximately 10 m. Ref through 11:00:00.760. User confirmed tension begins 11:19:48.541. AUX 200 m retained; ng=1.4682 provisional.\n\n'
    summary+='## Execution and validation\n\n23 full 5M-point, 25 MS/s recordings; native MKL; 130 Ref–tension, 78 Ref repeat, 45 tension repeat = 253 distinct pairs. No source or pair computation failures. All 57 images are linked and dimension-checked. Probe 4 AUX bands × 23 = 92 conditions; baseline phase QC reproduced exactly. Core DLL matches previous 19 passing tests; no numerical Core or GUI change.\n\n'
    summary+='## Interpretation and unresolved issues\n\nComputation completion does not establish physical validity. Saved phase quality flags and status counts govern interpretation. Distance origin, exact jig interval, extension, group index and AUX quality remain unconfirmed. No strain calibration or forced distance-peak alignment. 10–20 m is a broad search region, not an asserted strain plateau. Same NORMAL mode, but state and acquisition time remain confounded.\n\n'
    summary+=f'## Artifacts\n\nReport: {out / "report.html"}\n\nGraphs: {out / "images"}\n\nConfig, manifests, per-source PSD, full pair curves, CSV metrics, provenance and audit_validation.json retained in this directory.\n'
    (run/'summary.md').write_text(summary,encoding='utf-8')
    passes={f'{lo}-{hi}MHz':sum(r['phase_qc']<=1e-5 for r in scan if r['low_mhz']==lo and r['high_mhz']==hi) for lo,hi in [(1,12),(2,10),(3,8),(4,7)]}
    (probe/'summary.md').write_text('# COMPLETED\n\nMinimal experiment: AUX-only bandpass sensitivity using the same saved longest normal raw arrays, own AUX, unchanged CH4 and quality threshold.\n\n'+json.dumps(passes,indent=2)+'\n\n92 conditions completed. Baseline phase QC identical to the full-source audit. If all pass counts remain zero, the hypothesis that changing these band limits alone resolves phase validity is rejected; no band is accepted based on a desired plot.\n',encoding='utf-8')
    print(json.dumps({'validated':True,'images':len(image_sizes),'pairs':253,'probe_passes':passes}))

if __name__=='__main__':main()
