"""Interpret enhanced-FUT controls, preserving absolute lag and all QC failures."""
from __future__ import annotations
import argparse
import importlib.util
import json
from pathlib import Path
import time
import traceback

spec=importlib.util.spec_from_file_location('enhanced_probe',Path(__file__).with_name('Diagnose-EnhancedFiber.py'))
d=importlib.util.module_from_spec(spec);spec.loader.exec_module(d)
np,pd,plt=d.np,d.pd,d.plt


def read(path):return json.loads(path.read_text(encoding='utf-8-sig'))


def prepare(aux,main,fft,dc=False,anchor=False):
    p,accepted,m=d.phase(aux[40:-1],fft,dc)
    v=main[1:-40]
    order=np.flatnonzero(accepted) if anchor else np.argsort(p,kind='stable')
    return dict(p=p[order],v=v[order],first=float(p[0]),span=float(p[-1]-p[0]),metrics=m)


def spectrum(plan,fft,span=None,global_window=True):
    span=plan['span'] if span is None else span
    v=np.interp(np.linspace(plan['first'],plan['first']+span,524288),plan['p'],plan['v'])
    v-=v.mean()
    if global_window:v*=np.hanning(len(v))
    return fft.fft(v)[:len(v)//2]


def quality(r,s,center,fft,local_window):
    size=4096;n=size*8;start=center-size//2
    w=np.hanning(size) if local_window else np.ones(size)
    a=np.abs(fft.fft(r[start:start+size]*w,inverse=True,n=n))
    b=np.abs(fft.fft(s[start:start+size]*w,inverse=True,n=n))
    corr=fft.fft(fft.fft(b)*fft.fft(a).conj(),inverse=True).real
    shifted=np.fft.fftshift(corr);peak=int(np.argmax(shifted));lag=float(peak-n//2)
    if 0<peak<n-1:
        den=shifted[peak-1]-2*shifted[peak]+shifted[peak+1]
        if abs(den)>1e-15:lag+=.5*(shifted[peak-1]-shifted[peak+1])/den
    aa=a-a.mean();bb=b-b.mean();den=float(np.linalg.norm(aa)*np.linalg.norm(bb))
    # Circular mean removal changes correlation by a constant, preserving the maximum.
    normalized=(shifted-n*a.mean()*b.mean())/den if den>1e-30 else np.zeros(n)
    competitors=normalized[np.abs(np.arange(n)-peak)>=8]
    from scipy.ndimage import uniform_filter1d
    fa=a-uniform_filter1d(a,size=513,mode='nearest');fb=b-uniform_filter1d(b,size=513,mode='nearest')
    fine_zero=float(fa@fb/(np.linalg.norm(fa)*np.linalg.norm(fb)))
    return dict(fft_bin=center,lag_samples=lag,peak_centered_correlation=float(normalized[peak]),
                zero_lag_centered_correlation=float(normalized[n//2]),peak_margin_over_competitor=float(normalized[peak]-competitors.max()),
                fine_structure_zero_lag_correlation=fine_zero,
                fine_structure_note='Diagnostic residual after fixed 513-sample moving mean; not a measurement correction'),a,b,normalized


def ridge(x,dt,fft,minimum_frequency_hz=.5e6):
    # Match physical frame duration and frequency-bin width across sample rates.
    n=int(round(40.96e-6/dt));count=min(40,len(x)//n);powers=[];rows=[]
    starts=np.linspace(0,len(x)-n,count).astype(int)
    for start in starts:
        y=x[start:start+n];p=np.abs(fft.fft((y-y.mean())*np.hanning(n)))[:n//2]**2
        p[0]=0;powers.append(p)
        floor=int(minimum_frequency_hz*n*dt)
        k=int(np.argmax(p[max(1,floor):])+max(1,floor))
        rows.append(dict(peak_hz=k/(n*dt),seven_bin_peak_energy_fraction=float(p[max(1,k-3):k+4].sum()/p.sum())))
    power=np.mean(powers,axis=0)
    bands=[]
    for lo,hi in [(0,.5e6),(.5e6,2e6),(2e6,5e6),(5e6,10e6),(10e6,min(15e6,.5/dt)),(15e6,.5/dt)]:
        if hi<=lo:continue
        a=int(lo*n*dt);b=min(len(power),int(hi*n*dt))
        bands.append(dict(low_hz=lo,high_hz=hi,energy_fraction=float(power[a:b].sum()/power.sum())))
    return dict(windows=count,frame_samples=n,frame_duration_s=n*dt,frequency_bin_width_hz=1/(n*dt),peak_search_minimum_hz=minimum_frequency_hz,
                median_seven_bin_peak_energy_fraction=float(np.median([r['seven_bin_peak_energy_fraction'] for r in rows])),
                peak_hz_p10=float(np.quantile([r['peak_hz'] for r in rows],.1)),peak_hz_p90=float(np.quantile([r['peak_hz'] for r in rows],.9)),bands=bands),np.arange(len(power))/(n*dt),power


def main():
    ap=argparse.ArgumentParser(__doc__)
    ap.add_argument('--core',type=Path,required=True);ap.add_argument('--prepared',type=Path,required=True);ap.add_argument('--inventory',type=Path,required=True)
    ap.add_argument('--analysis-name',default='analysis')
    ap.add_argument('--core-spectra',type=Path)
    args=ap.parse_args();out=args.core/args.analysis_name;out.mkdir(exist_ok=False)
    d.write(out/'config.json',dict(core=str(args.core),prepared=str(args.prepared),quality_fft_bins=[4096,10240,20480,40960,81920,139264],
                                 no_baseline_subtraction=True,physical_axes='100m and 200m OPD examples only; geometry not independently verified'))
    (out/'summary.md').write_text('# RUNNING\n',encoding='utf8')
    start=time.perf_counter();fft=None;error=None;status='FAILED'
    try:
        if read(args.core/'metrics.json')['status']!='COMPLETED':raise ValueError('Core batch not complete')
        fft=d.native.NativeMkl(d.ROOT/'build_verify_20261109_sampling_control/libMathNetNumericsMKL.dll')
        summaries=[]
        for folder in sorted(args.core.iterdir()):
            if not (folder/'profile.csv').exists():continue
            p=pd.read_csv(folder/'profile.csv');v=p.lag_samples.dropna().to_numpy()
            r=read(folder/'result.json');m={x['Name']:x['Value'] for x in r['PerformanceMetrics']}
            row=dict(case=folder.name,median_lag=float(np.median(v)),median_abs_lag=float(np.median(abs(v))),
                     lag_p05=float(np.quantile(v,.05)),lag_p95=float(np.quantile(v,.95)),
                     fraction_within_1_sample=float(np.mean(abs(v)<=1)),fraction_within_8_samples=float(np.mean(abs(v)<=8)),
                     reference_span=m['reference_grid_span'],signal_span=m['signal_grid_span'],span_ratio=m['signal_grid_span']/m['reference_grid_span'],
                     reference_qc=r['KLinearization']['PhaseQcPassed'],signal_qc=r['SignalKLinearization']['PhaseQcPassed'])
            for opd in [100,200]:
                x=p.fft_bin.to_numpy()*np.pi*opd/m['reference_grid_span']
                mask=(x>=0)&(x<=20)&np.isfinite(p.lag_samples.to_numpy())
                vv=p.lag_samples.to_numpy()[mask]
                row[f'provisional_OPD{opd}_first20m_median_lag']=float(np.median(vv)) if len(vv) else None
                row[f'provisional_OPD{opd}_first20m_count']=len(vv)
            summaries.append(row)
        d.write(out/'case_statistics.json',summaries);pd.DataFrame(summaries).to_csv(out/'case_statistics.csv',index=False)
        fig,axs=plt.subplots(2,1,figsize=(12,8),sharex=True)
        for sig,color in [(5,'C0'),(6,'C1'),(7,'C2')]:
            p=pd.read_csv(args.core/f'masked_v0_4vs{sig}_fullhop/profile.csv')
            axs[0].plot(p.fft_bin,p.lag_samples,lw=.65,color=color,label=f'4 vs {sig}, notebook settings')
            p=pd.read_csv(args.core/f'masked_v5_4vs{sig}/profile.csv')
            axs[1].plot(p.fft_bin,p.lag_samples,lw=.65,color=color,label=f'4 vs {sig}, DC/anchor/common/local')
        for ax in axs:ax.axhline(0,color='0.3',lw=.7);ax.set_ylabel('Uncalibrated lag (sample)');ax.legend();ax.grid(alpha=.2)
        axs[-1].set_xlabel('Distance FFT bin (physical calibration not accepted)');fig.suptitle('Same unchanged FUT: repeated measurements do not reproduce zero lag')
        fig.tight_layout();fig.savefig(out/'repeat_profiles.png',dpi=150);plt.close(fig)
        aux={i:np.fromfile(args.prepared/'masked'/f'{i}_aux.bin',dtype='<f8') for i in range(4,8)}
        main={i:np.fromfile(args.prepared/'masked'/f'{i}_main.bin',dtype='<f8') for i in range(4,8)}
        plans={i:prepare(aux[i],main[i],fft) for i in range(4,8)}
        spectra={i:spectrum(plans[i],fft) for i in range(4,8)}
        if args.core_spectra:
            phase_differences=[]
            for i in range(4,8):
                core_phase=read(args.core_spectra/f'{i}_phase.json')
                phase_differences.append(dict(file=i,python_span=plans[i]['span'],core_span=core_phase['span'],
                                              difference_cycles=(plans[i]['span']-core_phase['span'])/(2*np.pi),
                                              note='Independent phase extraction differs on this failing-QC input; quality probes use exported actual Core spectra.'))
                spectra[i]=np.fromfile(args.core_spectra/f'{i}_spectrum.bin',dtype='<c16')
            d.write(out/'independent_phase_sensitivity.json',phase_differences)
        qualities=[];checks=[]
        for sig in [5,6,7]:
            profile=pd.read_csv(args.core/f'masked_v0_4vs{sig}_fullhop/profile.csv').set_index('fft_bin')
            for center in [5000,10000,20000,40000,80000,140000]:
                q,aa,bb,corr=quality(spectra[4],spectra[sig],center,fft,False);q['signal']=sig;qualities.append(q)
                expected=float(profile.loc[center,'lag_samples']);checks.append(dict(signal=sig,fft_bin=center,csharp_lag=expected,python_lag=q['lag_samples'],error=abs(expected-q['lag_samples'])))
                if sig==5 and center in [20000,140000]:
                    fig,axs=plt.subplots(2,1,figsize=(11,6))
                    ix=np.arange(0,len(aa),8)
                    axs[0].plot(ix,aa[ix]/np.max(aa),label='Ref 4',lw=.8);axs[0].plot(ix,bb[ix]/np.max(bb),label='Repeat 5',lw=.8)
                    axs[0].set_ylabel('Magnitude / own maximum');axs[0].legend();axs[0].grid(alpha=.2)
                    lag=np.arange(-len(corr)//2,len(corr)//2);axs[1].plot(lag,corr,lw=.8);axs[1].set_ylabel('Centered normalized correlation');axs[1].set_xlabel('Circular lag (sample)');axs[1].grid(alpha=.2)
                    fig.suptitle(f'4 vs 5, FFT bin {center}, peak lag {q["lag_samples"]:.2f}');fig.tight_layout();fig.savefig(out/f'local_spectrum_bin{center}.png',dpi=140);plt.close(fig)
        d.write(out/'quality_probes.json',qualities);d.write(out/'independent_native_crosscheck.json',checks)
        d.write(out/'python_phase_plans.json',{str(k):dict(first=v['first'],span=v['span'],metrics=v['metrics']) for k,v in plans.items()})
        if max(c['error'] for c in checks)>0.05:raise AssertionError('Python/C# quality probes disagree')
        ridge_rows=[];fig,ax=plt.subplots(figsize=(11,5))
        for number in range(4,8):
            path=args.prepared/'continuous'/f'{number}_aux.bin';x=np.fromfile(path,dtype='<f8')
            rr,f,p=ridge(x,4e-8,fft);rr.update(file=f'tek000{number}ALL.csv',channel='CH3',crop='shared contiguous no-mode-hop window');ridge_rows.append(rr)
            ax.plot(f/1e6,10*np.log10(np.maximum(p/p.sum(),1e-20)),lw=.7,label=f'New {number}')
        # Old reference: use a finite, CH2-clean contiguous window, without stitching.
        a,meta=d.read_csv(Path(r'D:\project\OFDR\signal processing\261106_RAW\tek0002ALL.csv'),4_167_008)
        a=a[167008:];bad=d.exclusion_mask(a,.0001,.0003,meta['sample_interval_s'])|~np.isfinite(a[:,3:]).all(axis=1)
        fl=np.diff(np.r_[False,~bad,False].astype(np.int8));left=np.flatnonzero(fl==1);right=np.flatnonzero(fl==-1);j=int(np.argmax(right-left))
        old=a[left[j]:right[j],3];rr,f,p=ridge(old,meta['sample_interval_s'],fft);rr.update(file='old 261106 tek0002ALL.csv',channel='CH3',crop='longest finite guarded window in first 40ms');ridge_rows.append(rr)
        old_metrics=dict(source=meta,trigger_relative_start=int(left[j]),trigger_relative_stop=int(right[j]),
                         samples=len(old),duration_s=len(old)*meta['sample_interval_s'],auxiliary=d.stats(old),measurement=d.stats(a[left[j]:right[j],4]),phase={})
        for remove_dc in [False,True]:
            _,_,pm=d.phase(old[40:-1],fft,remove_dc)
            old_metrics['phase']['dc_removed' if remove_dc else 'dc_retained']=pm
        d.write(out/'old_contiguous_metrics.json',old_metrics)
        ax.plot(f/1e6,10*np.log10(np.maximum(p/p.sum(),1e-20)),lw=.9,label='Old 261106 reference')
        ax.set_xlim(0,12.5);ax.set_ylim(-55,-10);ax.set_xlabel('Frequency (MHz)');ax.set_ylabel('Normalized power / 24.4 kHz bin (dB)');ax.grid(alpha=.2);ax.legend();fig.suptitle('AUX spectra: matched 40.96 us frames, no mode-hop joins')
        fig.tight_layout();fig.savefig(out/'auxiliary_spectra.png',dpi=150);plt.close(fig);d.write(out/'auxiliary_ridge_metrics.json',ridge_rows)
        main_ridge=[];fig,ax=plt.subplots(figsize=(11,5))
        for number in range(4,8):
            x=np.fromfile(args.prepared/'continuous'/f'{number}_main.bin',dtype='<f8')
            rr,f,p=ridge(x,4e-8,fft,0);rr.update(file=f'tek000{number}ALL.csv',channel='CH4');main_ridge.append(rr)
            ax.plot(f/1e6,10*np.log10(np.maximum(p/p.sum(),1e-20)),lw=.7,label=f'New {number}')
        old_main=a[left[j]:right[j],4];rr,f,p=ridge(old_main,meta['sample_interval_s'],fft,0);rr.update(file='old 261106 tek0002ALL.csv',channel='CH4');main_ridge.append(rr)
        ax.plot(f/1e6,10*np.log10(np.maximum(p/p.sum(),1e-20)),lw=.9,label='Old 261106 reference')
        ax.set_xlim(0,12.5);ax.set_ylim(-55,-10);ax.set_xlabel('Frequency (MHz)');ax.set_ylabel('Normalized power / 24.4 kHz bin (dB)');ax.grid(alpha=.2);ax.legend();fig.suptitle('Measurement CH4 spectra: matched 40.96 us frames')
        fig.tight_layout();fig.savefig(out/'measurement_spectra.png',dpi=150);plt.close(fig);d.write(out/'measurement_ridge_metrics.json',main_ridge)
        inventory=read(args.inventory/'inventory.json');duplicates={}
        for m in inventory:duplicates.setdefault(m['sha256'],[]).append(m['path'])
        d.write(out/'duplicate_sources.json',[x for x in duplicates.values() if len(x)>1])
        d.write(out/'source_manifests.json',dict(prepared=read(args.prepared.parent/'prepared_manifest.json'),inventory=inventory))
        status='COMPLETED'
    except Exception:
        error=traceback.format_exc();print(error,flush=True)
    finally:
        calls=fft.calls if fft else 0
        if fft:fft.close()
        d.write(out/'metrics.json',dict(status=status,error=error,elapsed_seconds=time.perf_counter()-start,native_mkl_calls=calls,
                                       script_sha256=d.native.digest(Path(__file__))))
        (out/'summary.md').write_text(f'# {status}\n\nAbsolute lag and QC failures preserved. Geometry examples are provisional.\n\n{error or "See report for interpretation."}',encoding='utf8')
    if status!='COMPLETED':raise SystemExit(1)


if __name__=='__main__':main()
