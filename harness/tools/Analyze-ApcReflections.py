"""Inspect actual Core FFT exports for the user's APC 3+3+3+20m setup."""
import argparse
import importlib.util
import json
from pathlib import Path

spec=importlib.util.spec_from_file_location('enhanced',Path(__file__).with_name('Diagnose-EnhancedFiber.py'))
d=importlib.util.module_from_spec(spec);spec.loader.exec_module(d)
np,plt=d.np,d.plt


def read(path):return json.loads(path.read_text(encoding='utf-8-sig'))


def envelope(z,bins=800):
    db=20*np.log10(np.maximum(abs(z)/max(float(abs(z).max()),1e-300),1e-12))
    edges=np.linspace(0,len(db),bins+1).astype(int)
    x=[];median=[];maximum=[]
    for a,b in zip(edges[:-1],edges[1:]):
        if b>a:x.append((a+b-1)/2);median.append(float(np.median(db[a:b])));maximum.append(float(db[a:b].max()))
    return np.array(x),np.array(median),np.array(maximum)


def main():
    ap=argparse.ArgumentParser(__doc__);ap.add_argument('--output',type=Path,required=True);args=ap.parse_args()
    out=args.output;out.mkdir(parents=True,exist_ok=False)
    d.write(out/'config.json',dict(files=['D:/project/전송 자료/data/new/50ns/tek0015ALL.csv','D:/project/전송 자료/data/new/50ns/tek0016ALL.csv'],
                                 connector='All APC, including distal end',patch_lengths_m=[3,3,3,20],nominal_event_positions_from_FUT_input_m=[3,6,9,29],
                                 lead_offset_m='unknown',auxiliary_coil_m=100,auxiliary_optical_path_geometry='not independently confirmed',
                                 source_selection='first CH1>3 V to end, CH2>1.8V/CH3&4 nonfinite union excluded; not exact GUI selection'))
    rows=[];fig,axs=plt.subplots(2,1,figsize=(12,8),sharex=True)
    for ax,number in zip(axs,[15,16]):
        for folder,label,color in [('20261109_apc_fft_masked_v1','N=524288, AUX DC retained','C0'),('20261109_apc_fft_dc_removed_v1','N=524288, AUX DC removed','C1'),('20261109_apc_fft_dense_v1','Dense uniform k, AUX DC retained','C2')]:
            root=d.ROOT/'harness/results'/folder
            if read(root/'metrics.json')['status']!='COMPLETED':raise RuntimeError('Incomplete Core export')
            p=read(root/f'{number}_phase.json');z=np.fromfile(root/f'{number}_spectrum.bin',dtype='<c16')
            x,med,maximum=envelope(z[:262144])
            ax.plot(x,med,color=color,label=label,lw=1)
            ax.plot(x,maximum,color=color,lw=.45,alpha=.35)
            item=dict(file=f'tek{number:04d}ALL.csv',source_result=folder,phase=p,
                      plot_range_note='Same first262144 bins; each shown range normalized to own maximum; thin curve=bucket max, thick=median',
                      conditional_axes=[])
            for opd in [100,200]:
                if p['span']<=0:continue
                step=np.pi*opd*p['output_count']/((p['output_count']-1)*p['span'])
                item['conditional_axes'].append(dict(auxiliary_path_difference_m=opd,geometry_assumption='single-ended return; group-index treatment consistent between AUX and FUT',
                    distance_m_per_bin=step,positive_fft_nyquist_distance_m=(p['output_count']//2)*step,
                    event_bins_without_lead_offset=[x/step for x in [3,6,9,29]],
                    calibration_accepted=False,note='AUX phase QC fails and geometry/lead not verified; illustrative only'))
            rows.append(item)
        ax.set_title(f'tek{number:04d}ALL: actual Core k-linearized FFT');ax.set_ylim(-40,3);ax.set_ylabel('Normalized magnitude (dB)');ax.grid(alpha=.2);ax.legend()
    axs[-1].set_xlabel('FFT bin; physical distance calibration not accepted')
    fig.suptitle('All-APC setup: no assumption of four strong reflection peaks')
    fig.tight_layout();fig.savefig(out/'core_fft_comparison.png',dpi=150);plt.close(fig)
    prepared=read(d.ROOT/'harness/results/20261109_apc_15_16_prepared_v1/prepared_manifest.json')
    d.write(out/'dataset_manifest.json',prepared);d.write(out/'fft_axis_cases.json',rows)
    basic=[r for r in rows if r['source_result']=='20261109_apc_fft_masked_v1']
    d.write(out/'metrics.json',dict(status='COMPLETED_DIAGNOSIS',measurement_validity='NOT_ACCEPTED_PHASE_QC',
                                  source_files=2,full_source_rows_per_file=5000000,actual_sample_interval_s=4e-8,actual_sample_rate_hz=25e6,
                                  apc_topology_total_m=29,core_fft_export_cases=len(rows),
                                  dc_retained_nonincreasing_ratio=[r['phase']['adjacent_reverse_ratio'] for r in basic],
                                  four_bright_peaks_required=False,physical_peak_positions_claimed=False,
                                  script_sha256=d.native.digest(Path(__file__))))
    (out/'summary.md').write_text('# Investigation completed\n\nAll connectors and distal termination are APC. Strong specular peaks at every joint/end are not a required normal outcome. Actual 15/16 files have40ns headers despite50ns folder name. Full5M records and native Core FFT exports were inspected; phase QC fails. See report for connector physics versus waveform-quality interpretation. No signal or baseline correction was applied.\n',encoding='utf8')


if __name__=='__main__':main()
