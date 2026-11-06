"""Bounded 25 MS/s audit and distance-bin sizing; no production algorithm changes."""
import ctypes
import hashlib
import json
import math
from pathlib import Path
import time
import traceback
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
DATA = Path('C:/Project/OFDR/Datas/OFDR Raw data/NoticeData/새 폴더/25M_Sampling')
OUT = ROOT / 'harness/results/20261106_25msps_segment_recommendation'
LOGS = ROOT / 'OFDR_Signal_Generator/bin/Debug/harness/results'
STARTS = [1000000, 1500000, 2250000, 3000000, 3500000]
COUNT = 32768
C, NG, OPD = 299792458.0, 1.4682, 200.0


def write(name, value):
    (OUT / name).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')


def peak_memory():
    class Counters(ctypes.Structure):
        _fields_ = [('cb', ctypes.c_uint32), ('PageFaultCount', ctypes.c_uint32)] + [
            (s, ctypes.c_size_t) for s in ['PeakWorkingSetSize', 'WorkingSetSize', 'QuotaPeakPagedPoolUsage',
            'QuotaPagedPoolUsage', 'QuotaPeakNonPagedPoolUsage', 'QuotaNonPagedPoolUsage', 'PagefileUsage', 'PeakPagefileUsage']]
    p = Counters(); p.cb = ctypes.sizeof(p)
    ctypes.windll.kernel32.GetCurrentProcess.restype = ctypes.c_void_p
    f = ctypes.windll.psapi.GetProcessMemoryInfo
    f.argtypes = [ctypes.c_void_p, ctypes.POINTER(Counters), ctypes.c_uint32]
    if not f(ctypes.windll.kernel32.GetCurrentProcess(), ctypes.byref(p), p.cb): raise ctypes.WinError()
    return p.PeakWorkingSetSize


def inspect(path):
    before = path.stat(); digest = hashlib.sha256(); header = []; blocks = [[] for _ in STARTS]
    in_data = False; rows = 0; block = 0
    with path.open('rb') as stream:
        for line in stream:
            digest.update(line)
            if not in_data:
                text = line.decode('utf-8-sig').strip(); header.append(text)
                in_data = text.startswith('TIME,')
                continue
            if not line.strip(): continue
            while block < len(STARTS) and rows >= STARTS[block] + COUNT: block += 1
            if block < len(STARTS) and STARTS[block] <= rows < STARTS[block] + COUNT: blocks[block].append(line)
            rows += 1
    after = path.stat()
    if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns): raise RuntimeError('Source changed: ' + str(path))
    meta = {s.split(',')[0]:s.split(',')[1] for s in header if ',' in s}
    dt = float(meta['Sample Interval']); expected = int(float(meta['Record Length']))
    if rows != expected: raise RuntimeError('Record length mismatch: ' + str(path))
    result = dict(path=str(path), sha256=digest.hexdigest(), header=header, sample_interval_s=dt,
                  row_count=rows, record_duration_s=rows*dt, numeric_samples=len(STARTS)*COUNT,
                  hash_scope='entire file; only selected windows parsed numerically', windows=[])
    for start, lines in zip(STARTS, blocks):
        if len(lines) != COUNT: raise RuntimeError('Incomplete sample window')
        a = np.fromstring(b''.join(lines).replace(b'\n',b',').replace(b'\r',b''), sep=',').reshape(COUNT,5)
        keep = np.isfinite(a[:,1:]).all(axis=1) & (a[:,2] <= 1.8)
        edges = np.flatnonzero(np.diff(np.r_[False,keep,False].astype(int))).reshape(-1,2)
        w = dict(start_index=start, end_index_exclusive=start+COUNT, start_s=start*dt,
                 high_fraction=float(np.mean(np.isfinite(a[:,2]) & (a[:,2]>1.8))), nonfinite_rows=int(np.sum(~np.isfinite(a[:,1:]).all(axis=1))))
        if len(edges):
            lo,hi=edges[np.argmax(edges[:,1]-edges[:,0])]
            if hi-lo >= 4096:
                x=a[lo:hi,3].copy(); x-=x.mean(); n=len(x)
                h=np.zeros(n); h[0]=1; h[1:(n+1)//2]=2
                if n%2==0: h[n//2]=1
                analytic=np.fft.ifft(np.fft.fft(x)*h); phase=np.unwrap(np.angle(analytic))[512:-512]
                if np.median(np.diff(phase))<0: phase=-phase
                freq=np.diff(phase)/(2*np.pi*dt)
                peak=int(np.argmax(abs(np.fft.rfft(x*np.hanning(n)))[1:])+1)
                w.update(continuous_indices=[int(start+lo),int(start+hi)], fft_peak_hz=peak/(n*dt),
                         mean_phase_frequency_hz=float(np.mean(freq)), frequency_quantiles_hz=np.quantile(freq,[.05,.5,.95]).tolist(),
                         rejected_anchor_fraction=float(np.mean(phase[1:]<=np.maximum.accumulate(phase)[:-1])),
                         phase_edge_trim_samples=512, analytic_magnitude_quantiles=np.quantile(abs(analytic)[512:-512],[.05,.5,.95]).tolist())
        result['windows'].append(w)
    return result


def main():
    OUT.mkdir(parents=True,exist_ok=True); started=time.perf_counter()
    write('config.json',dict(data=str(DATA), sample_starts=STARTS, samples_per_window=COUNT,
         max_numeric_samples_per_file=250000, actual_numeric_samples_per_file=len(STARTS)*COUNT,
         stated_sweep_nm=5, stated_sweep_s=.13, center_wavelength_nm=1550,
         auxiliary_round_trip_path_m_provisional=OPD, group_index_assumed=NG,
         scope='Headers/SHA/row count + bounded continuous CH2 Low windows + existing saved analysis logs. No full numeric rerun or segment-parameter optimization.'))
    (OUT/'summary.md').write_text('RUNNING\n',encoding='utf-8')
    metrics={'status':'FAILED'}
    try:
        sources=[]
        for path in sorted(DATA.glob('*.csv')):
            item=inspect(path); sources.append(item)
            print(json.dumps({'file':path.name,'rows':item['row_count'],'samples':item['numeric_samples'],
                'fft_peak_mhz':[round(w['fft_peak_hz']/1e6,4) for w in item['windows'] if 'fft_peak_hz' in w]}),flush=True)
        write('datasets.json',sources); hashes={s['sha256'] for s in sources}
        logs=[]
        for d in sorted(LOGS.glob('20261106_*prepared_ofdr_analysis*')):
            if '25M_Sampling' not in (d/'config.json').read_text(encoding='utf-8-sig'): continue
            read=lambda name:json.loads((d/name).read_text(encoding='utf-8-sig'))
            p,q=read('ref_preparation.json'),read('signal_preparation.json')
            if p['SourceSha256'] not in hashes or q['SourceSha256'] not in hashes: raise RuntimeError('Saved log source hash mismatch')
            config=read('processing_config.json'); result=read('result.json'); axis=result['PhysicalAxis']
            factor=OPD/config['AuxiliaryPathDifferenceMeters']
            item=dict(path=str(d), config=config, saved_axis=axis, corrected_distance_m_per_bin=axis['DistanceMetersPerFftBin']*factor,
                      corrected_sweep_ghz=axis['SweepSpanGHz']/factor, source_hashes_match=True, ref=p['SourcePath'], sig=q['SourcePath'],
                      sample_count=result['SampleCount'], qc_passed=result['InputPhaseQcPassed'],
                      ref_phase=p['Diagnosis']['Phase'], sig_phase=q['Diagnosis']['Phase'],
                      ref_sweep=p['Sweep'], sig_sweep=q['Sweep'], ref_lead_s=p.get('ModeHopExclusionLeadSeconds',0),ref_tail_s=p.get('ModeHopExclusionTailSeconds',0),
                      saved_sha256={f:hashlib.sha256((d/f).read_bytes()).hexdigest() for f in ['config.json','processing_config.json','ref_preparation.json','signal_preparation.json','result.json']})
            logs.append(item)
        write('saved_runs.json',logs)
        bandwidth=C*(1/(1547.5e-9)-1/(1552.5e-9)); ideal_bin=C/(2*NG*bandwidth)
        representative=next(r for r in logs if r['path'].endswith('20261106_065640_393_prepared_ofdr_analysis_40b193'))
        dz=representative['corrected_distance_m_per_bin']
        table=[dict(segment_size=size,hop=hop,ideal_gauge_m=size*ideal_bin,ideal_hop_m=hop*ideal_bin,
                    logged_phase_gauge_m=size*dz,logged_phase_hop_m=hop*dz,overlap_percent=100*(1-hop/size))
               for size,hop in [(1024,256),(2048,512),(4096,512),(4096,1024),(8192,1024),(4096,128)]]
        write('segment_table.json',table)
        metrics.update(status='PASS',files=len(sources),numeric_samples_per_file=len(STARTS)*COUNT,
            saved_runs_reused=len(logs),ideal_5nm_bandwidth_ghz=bandwidth/1e9,ideal_distance_m_per_bin=ideal_bin,
            ideal_auxiliary_beat_hz=NG*OPD/C*bandwidth/.13,fs_hz=25e6,nyquist_hz=12.5e6,
            representative_logged_200m_bin_m=dz,representative_logged_200m_bandwidth_ghz=representative['corrected_sweep_ghz'],
            recommendation=dict(segment_size=4096,segment_hop=512,status='starting comparison, not validated optimum'),
            full_5m_numeric_rerun=False,product_changes=False)
    except BaseException:
        metrics['error']=traceback.format_exc(); raise
    finally:
        metrics.update(elapsed_seconds=time.perf_counter()-started,peak_working_set_bytes=peak_memory())
        write('metrics.json',metrics)
        (OUT/'summary.md').write_text('# 25 MS/s segment sizing\n\n'+json.dumps(metrics,ensure_ascii=False,indent=2)+'\n\nSee datasets.json, saved_runs.json, segment_table.json. Full-file hashing/row counting is distinct from bounded numeric signal analysis. Phase-failed saved runs only establish software bin scales, not trustworthy physical calibration or temperature discrimination.\n',encoding='utf-8')


if __name__=='__main__': main()
