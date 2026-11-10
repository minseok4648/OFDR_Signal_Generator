"""Compare only reflection-test files 0002 and 0003; no measurement correction."""
import argparse
import importlib.util
import json
from pathlib import Path


def module(name,file):
    spec=importlib.util.spec_from_file_location(name,Path(__file__).with_name(file));m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);return m


d=module('reflection_prepare','Diagnose-EnhancedFiber.py')
a=module('reflection_quality','Analyze-EnhancedFiber.py')
b=module('reflection_envelope','Analyze-ApcReflections.py')
np,pd,plt=d.np,d.pd,d.plt


def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))


def main():
    ap=argparse.ArgumentParser(__doc__);ap.add_argument('--output',type=Path,required=True);args=ap.parse_args()
    out=args.output;out.mkdir(parents=True,exist_ok=False)
    prepared=d.ROOT/'harness/results/20261109_pc_2_3_prepared_v1'
    manifest=read(prepared/'prepared_manifest.json')
    assert {Path(m['source']['path']).name for m in manifest}=={'tek0002ALL.csv','tek0003ALL.csv'}
    d.write(out/'config.json',dict(sources=[m['source']['path'] for m in manifest],only_files=[2,3],
                                 topology_m=[3,20,3,3],user_confirmed_interface='PC end of APC-PC cable is mated to next APC-APC cable',
                                 interface_position_from_input_m=26,aux_coil_m=100,optical_path_geometry='unverified',
                                 preprocessing='CH2>1.8V plus same-index nonfinite mask; explicit AUX40/CH4 1 offsets',
                                 single_carrier_phase_use='diagnostic only; CH4 phase never used as k-clock to correct measurements'))
    (out/'summary.md').write_text('# RUNNING\n',encoding='utf8')
    fft=d.native.NativeMkl(d.ROOT/'build_verify_20261109_sampling_control/libMathNetNumericsMKL.dll')
    rows=[];fig,axs=plt.subplots(3,1,figsize=(12,10))
    try:
        for number in [2,3]:
            metrics=manifest[number-2];aux=np.fromfile(prepared/'prepared/continuous'/f'{number}_aux.bin',dtype='<f8');main=np.fromfile(prepared/'prepared/continuous'/f'{number}_main.bin',dtype='<f8')
            aux_phase,_,aux_m=d.phase(aux,fft,True);main_phase,_,main_m=d.phase(main,fft,True)
            slope,intercept=np.polyfit(main_phase,aux_phase,1);residual=aux_phase-(intercept+slope*main_phase)
            aux_ridge,f,aux_power=a.ridge(aux,4e-8,fft);main_ridge,mf,main_power=a.ridge(main,4e-8,fft,0)
            row=dict(file=f'tek{number:04d}ALL.csv',source=metrics['source'],masked=metrics['variants']['masked'],guarded=metrics['variants']['guarded'],continuous=dict(
                samples=len(aux),aux_phase=aux_m,measurement_phase=main_m,aux_ridge=aux_ridge,measurement_ridge=main_ridge,
                aux_phase_per_main_phase_slope=float(slope),phase_relation_residual_rms_rad=float(np.std(residual)),
                relation_note='Descriptive fit only; no assumption that main phase is a validated wavelength standard.'))
            rows.append(row)
            axs[0].plot(f/1e6,10*np.log10(np.maximum(aux_power/aux_power.sum(),1e-20)),label=f'AUX {number}',lw=.9)
            axs[0].plot(mf/1e6,10*np.log10(np.maximum(main_power/main_power.sum(),1e-20)),label=f'CH4 {number}',lw=.9,ls='--')
            raw=np.fromfile(prepared/'prepared/masked'/f'{number}_main.bin',dtype='<f8')
            p=np.abs(fft.fft((raw-raw.mean())*np.hanning(len(raw))))[:len(raw)//2]
            nblocks=1200;edges=np.linspace(1,len(p),nblocks+1).astype(int)
            x=[];v=[]
            for lo,hi in zip(edges[:-1],edges[1:]):x.append((lo+hi-1)/2/(len(raw)*4e-8)/1e6);v.append(float(p[lo:hi].max()))
            v=np.asarray(v);axs[1].plot(x,20*np.log10(np.maximum(v/v.max(),1e-12)),label=f'CH4 raw {number}',lw=.9)
            for folder,label,style in [('20261109_pc_2_3_fft_base_v1','524288','-'),('20261109_pc_2_3_fft_dense_v1','dense','--')]:
                root=d.ROOT/'harness/results'/folder
                z=np.fromfile(root/f'{number}_spectrum.bin',dtype='<c16')
                x,med,mx=b.envelope(z[:262144]);axs[2].plot(x,med,label=f'{number}: N={label}',lw=.9,ls=style)
                row.setdefault('core_fft',{})[label]=dict(phase=read(root/f'{number}_phase.json'),max_positive_bin=int(np.argmax(abs(z))),
                                                        maximum_bin_power_fraction=float(np.max(abs(z)**2)/np.sum(abs(z)**2)))
            dcroot=d.ROOT/'harness/results/20261109_pc_2_3_fft_dc_on_v1';row['core_fft']['dc_on']=read(dcroot/f'{number}_phase.json')
            main_min=float(np.min(main));main_max=float(np.max(main));row['continuous']['main_extreme_fraction']=float(np.mean((main==main_min)|(main==main_max)))
        for ax in axs:ax.grid(alpha=.2);ax.legend()
        axs[0].set_xlim(0,12.5);axs[0].set_ylim(-55,0);axs[0].set_xlabel('Frequency (MHz)');axs[0].set_ylabel('Normalized power / 24.4kHz bin (dB)');axs[0].set_title('Same contiguous window: AUX versus measurement carrier')
        axs[1].set_xlim(0,12.5);axs[1].set_ylim(-75,3);axs[1].set_xlabel('Frequency (MHz)');axs[1].set_ylabel('Raw CH4 AC FFT magnitude (dB)');axs[1].set_title('Before k-linearization: native FFT, same input channel')
        axs[2].set_xlabel('FFT bin; distance calibration not accepted');axs[2].set_ylabel('Bucket median normalized dB');axs[2].set_title('Actual Core k-linearized FFT: broad structure persists with dense output')
        compact,cax=plt.subplots(figsize=(10,4))
        for curve in axs[0].get_lines():
            cax.plot(curve.get_xdata(),curve.get_ydata(),label=curve.get_label(),ls=curve.get_linestyle(),lw=1)
        cax.set_xlim(0,12.5);cax.set_ylim(-55,0);cax.set_xlabel('Frequency (MHz)',fontsize=12);cax.set_ylabel('Normalized power / bin (dB)',fontsize=12)
        cax.set_title('Only files 0002 / 0003: CH4 carrier is clear; AUX band is weak',fontsize=12);cax.tick_params(labelsize=11);cax.legend(fontsize=11,ncol=2);cax.grid(alpha=.2)
        compact.tight_layout();compact.savefig(out/'aux_vs_main_compact.png',dpi=150);plt.close(compact)
        fig.tight_layout();fig.savefig(out/'signal_and_fft_comparison.png',dpi=150);plt.close(fig)
        d.write(out/'file_comparison.json',rows)
        pair=d.ROOT/'harness/results/20261109_pc_2_3_core_v1/masked_v0_2vs3'
        profile=pd.read_csv(pair/'profile.csv');pr=read(pair/'metrics.json');rr=read(pair/'result.json')
        d.write(out/'core_pair_summary.json',dict(metrics=pr,reference_phase=rr['KLinearization'],signal_phase=rr['SignalKLinearization']))
        fig,ax=plt.subplots(figsize=(12,4));ax.plot(profile.fft_bin,profile.lag_samples,lw=.65);ax.axhline(0,color='0.4',lw=.7)
        ax.set_xlabel('FFT bin');ax.set_ylabel('Uncalibrated correlation lag (sample)');ax.set_title('Only tek0002 vs tek0003, actual Core full processing');ax.grid(alpha=.2);fig.tight_layout();fig.savefig(out/'pair_profile.png',dpi=150);plt.close(fig)
        d.write(out/'dataset_manifest.json',[m['source'] for m in manifest])
        d.write(out/'metrics.json',dict(status='COMPLETED_DIAGNOSIS',measurement_validity='NOT_ACCEPTED_AUX_PHASE_QC',files=[2,3],
                                      full_source_rows=[m['source']['rows_read'] for m in manifest],core_fft_exports=6,
                                      direct_PC_APC_mating_confirmed=True,core_pair_median_lag=pr['median_lag'],
                                      native_calls=fft.calls,script_sha256=d.native.digest(Path(__file__))))
        (out/'summary.md').write_text('# Comparison completed\n\nOnly files0002/0003 were compared. Actual Core full processing and six FFT exports completed using x64 native MKL. PC-to-APC direct mating is confirmed by the user; it is not an open PC-air reflection standard. AUX phase quality remains unaccepted. See report for optical connection versus processing causes.\n',encoding='utf8')
    except Exception:
        import traceback
        error=traceback.format_exc();d.write(out/'metrics.json',dict(status='FAILED',error=error));(out/'summary.md').write_text('# FAILED\n\n'+error,encoding='utf8');raise
    finally:fft.close()


if __name__=='__main__':main()
