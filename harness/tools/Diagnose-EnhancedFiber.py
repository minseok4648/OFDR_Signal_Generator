"""Read-only enhanced-FUT investigation. All transforms use native MKL; no fallback."""
from __future__ import annotations
import argparse
import importlib.util
import json
import os
from pathlib import Path
import sys
import time
import traceback

ROOT = Path(__file__).resolve().parents[2]
os.environ.setdefault("MPLCONFIGDIR", str(ROOT / ".analysis_cache/matplotlib"))
sys.path.insert(0, str(ROOT / ".analysis_deps"))
spec = importlib.util.spec_from_file_location("notice_native", Path(__file__).with_name("Analyze-Notice50M.py"))
native = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native)
np, pd, plt = native.np, native.pd, native.plt


def write(path, obj):
    native.write(path, obj)


def read_csv(path, max_rows=None):
    before = path.stat()
    fields, header = {}, []
    with path.open(encoding="utf-8-sig") as stream:
        for line in stream:
            line = line.rstrip("\r\n")
            header.append(line)
            p = line.split(",")
            if p[0] == "TIME":
                if p != ["TIME", "CH1", "CH2", "CH3", "CH4"]:
                    raise ValueError("Unexpected channel mapping")
                break
            if p[0]:
                fields[p[0]] = p[1:]
        else:
            raise ValueError("TIME header missing")
    # Explicit float converters preserve oscilloscope +/-Inf tokens for diagnosis.
    a = pd.read_csv(path, skiprows=len(header), header=None, converters={i:float for i in range(5)}, nrows=max_rows).to_numpy()
    dt = float(fields["Sample Interval"][0])
    declared = int(float(fields["Record Length"][0]))
    if max_rows is None and len(a) != declared:
        raise ValueError(f"Row count mismatch: {len(a)} != {declared}")
    if a.shape[1] != 5 or not np.isfinite(a[:, :3]).all():
        raise ValueError("Invalid TIME/trigger channels")
    metadata = dict(path=str(path.resolve()), sha256=native.digest(path), bytes=before.st_size,
                    header=fields, declared_rows=declared, rows_read=len(a),
                    sample_interval_s=dt, sample_rate_hz=1/dt,
                    first_time_s=float(a[0, 0]), last_read_time_s=float(a[-1, 0]),
                    full_record_numeric_read=max_rows is None,
                    max_time_step_error_s=float(np.max(np.abs(np.diff(a[:, 0])-dt))))
    if (before.st_size, before.st_mtime_ns) != (path.stat().st_size, path.stat().st_mtime_ns):
        raise RuntimeError("Source changed during analysis")
    return a, metadata


def stats(x):
    finite = np.isfinite(x)
    v = x[finite]
    if not len(v):
        return dict(samples=len(x), nonfinite=int((~finite).sum()), no_finite_values=True)
    levels, counts = np.unique(v, return_counts=True)
    d = np.diff(levels)
    return dict(samples=len(x), nonfinite=int((~finite).sum()), mean_v=float(v.mean()),
                ac_rms_v=float(v.std()), min_v=float(v.min()), max_v=float(v.max()),
                p01_v=float(np.quantile(v, .01)), p99_v=float(np.quantile(v, .99)),
                unique_voltage_levels=len(levels), minimum_level_step_v=float(d[d > 0].min()) if len(d) else 0.,
                at_min_fraction=float(counts[0]/len(v)), at_max_fraction=float(counts[-1]/len(v)),
                most_common_level_fraction=float(counts.max()/len(v)),
                dc_to_ac_rms=float(abs(v.mean())/v.std()) if v.std() > 0 else None)


def phase(aux, fft, remove_mean):
    z = fft.fft(aux - aux.mean() if remove_mean else aux)
    n = len(z)
    z[1:(n+1)//2] *= 2
    z[n//2+1:] = 0
    z = fft.fft(z, inverse=True)
    p = np.unwrap(np.angle(z))
    direction = 1 if p[-1] >= p[0] else -1
    p *= direction
    inc = np.diff(p)
    accepted = np.r_[True, p[1:] > np.maximum.accumulate(p)[:-1]]
    mag = np.abs(z)
    m = dict(samples=n, dc_removed=remove_mean, phase_span_rad=float(p[-1]-p[0]),
             adjacent_nonincreasing_ratio=float(np.mean(inc <= 0)),
             record_high_rejection_ratio=float((n-accepted.sum())/(n-1)),
             phase_qc_limit=1e-5, phase_qc_passed=bool((n-accepted.sum())/(n-1) <= 1e-5),
             increment_p01_rad=float(np.quantile(inc, .01)), increment_median_rad=float(np.median(inc)),
             increment_p99_rad=float(np.quantile(inc, .99)),
             analytic_amplitude_p01_v=float(np.quantile(mag, .01)),
             analytic_amplitude_median_v=float(np.median(mag)),
             amplitude_below_tenth_median_fraction=float(np.mean(mag < .1*np.median(mag))))
    return p, accepted, m


def raw_spectrum(x, dt, fft):
    n = len(x)
    a = abs(fft.fft((x-x.mean())*np.hanning(n)))[:n//2]**2
    total = a.sum()
    peak = int(np.argmax(a[1:])+1)
    return dict(peak_frequency_hz=peak/(n*dt),
                energy_above_0_8_nyquist_fraction=float(a[int(.8*len(a)):].sum()/total),
                energy_below_0_05_nyquist_fraction=float(a[:int(.05*len(a))].sum()/total))


def primary_crop(a):
    edges = np.flatnonzero((a[:-1, 1] <= 3.) & (a[1:, 1] > 3.)) + 1
    above = np.flatnonzero(a[:, 1] > 3.)
    if not len(above):
        raise ValueError("CH1 > 3 V trigger missing")
    first = int(above[0])
    later = edges[edges > first+100]
    stop = int(later[0]) if len(later) else len(a)
    return first, stop, edges


def inventory(args, out, fft):
    paths = [args.input/f'tek{i:04d}ALL.csv' for i in args.numbers] if args.numbers else sorted(args.input.glob("*.csv"))
    if not args.numbers:paths += sorted((args.input/"10M").glob("*.csv"))
    old = Path(r"D:\project\OFDR\signal processing\261106_RAW\tek0002ALL.csv")
    if old.exists() and not args.numbers:
        paths.append(old)
    summaries = []
    for path in paths:
        label = path.stem if path.parent == args.input else path.parent.name+"_"+path.stem
        print("INVENTORY", label, flush=True)
        a, meta = read_csv(path, 4_167_008 if path == old else None)
        first, stop, edges = primary_crop(a)
        if path == old:
            first = 167008
            stop = len(a)
        selected = a[first:stop]
        meta.update(label=label, trigger_index=first, selection_stop=stop, selected_samples=len(selected),
                    ch1_rising_edges=edges[:100].tolist(), ch1_rising_edge_count=len(edges),
                    mode_hop_above_1_8v_fraction=float(np.mean(selected[:, 2] > 1.8)),
                    selected_start_time_s=float(selected[0, 0]), selected_end_time_s=float(selected[-1, 0]))
        meta["raw_channels"] = {f"CH{i}":stats(a[:, i]) for i in [2, 3, 4]}
        meta["selected_channels"] = {f"CH{i}":stats(selected[:, i]) for i in [2, 3, 4]}
        meta["phase"] = {}
        if np.isfinite(selected[:, 3]).all() and len(selected) >= 100:
            aux = selected[40:-1, 3]
            for dc in [False, True]:
                _, _, m = phase(aux, fft, dc)
                m["mean_frequency_hz"] = m["phase_span_rad"]/(2*np.pi*(len(aux)-1)*meta["sample_interval_s"])
                m["median_frequency_hz"] = m["increment_median_rad"]/(2*np.pi*meta["sample_interval_s"])
                meta["phase"]["dc_removed" if dc else "dc_retained"] = m
            meta["auxiliary_spectrum"] = raw_spectrum(aux, meta["sample_interval_s"], fft)
        write(out/"inventory"/(label+".json"),meta)
        summaries.append(meta)
        # Previews contain at most 5000 points and no raw arrays are retained.
        ix=np.linspace(first, stop-1, min(5000,stop-first)).astype(int)
        fig, axs=plt.subplots(3,1,figsize=(11,7),sharex=True)
        for ax,ch in zip(axs,[2,3,4]):
            ax.plot(a[ix,0]*1e3,a[ix,ch],lw=.6);ax.set_ylabel(f"CH{ch} (V)");ax.grid(alpha=.2)
        axs[-1].set_xlabel("Recording time (ms)");fig.suptitle(label)
        fig.tight_layout();fig.savefig(out/"inventory"/(label+".png"),dpi=130);plt.close(fig)
        del a, selected
    write(out/"inventory.json",summaries)
    return summaries


def exclusion_mask(a, lead, tail, dt):
    pulse = a[:, 2] > 1.8
    changes=np.diff(np.r_[False,pulse,False].astype(np.int8))
    starts,ends=np.flatnonzero(changes==1),np.flatnonzero(changes==-1)
    mask=np.zeros(len(a),dtype=bool)
    before=int(np.ceil(lead/dt));after=int(np.ceil(tail/dt))
    for s,e in zip(starts,ends):mask[max(0,s-before):min(len(a),e+after)]=True
    return mask


def prepare_inputs(args,out,fft):
    records={}; common=None; metrics=[]; dt=None
    numbers=args.numbers or list(range(4,8))
    for number in numbers:
        path=args.input/f'tek{number:04d}ALL.csv'
        print('PREPARE',path.name,flush=True)
        a,meta=read_csv(path)
        first,stop,_=primary_crop(a);dt=meta['sample_interval_s']
        x=a[first:stop].copy();del a
        if not np.isclose(dt,4e-8,rtol=0,atol=1e-16):raise ValueError('Target sample interval changed')
        invalid=~np.isfinite(x[:,3:]).all(axis=1)
        pulses=exclusion_mask(x,0,0,dt)
        guarded=exclusion_mask(x,.0001,.0003,dt)
        info=dict(source=meta,selection_start=first,selection_stop=stop,
                  invalid_auxiliary=int((~np.isfinite(x[:,3])).sum()),invalid_measurement=int((~np.isfinite(x[:,4])).sum()),
                  invalid_union=int(invalid.sum()),invalid_outside_ch2_high=int((invalid&~pulses).sum()),
                  invalid_outside_guarded_ch2=int((invalid&~guarded).sum()),
                  mode_hop_intervals=[],variants={})
        flags=np.diff(np.r_[False,pulses,False].astype(np.int8))
        info['mode_hop_intervals']=[[int(s),int(e)] for s,e in zip(np.flatnonzero(flags==1),np.flatnonzero(flags==-1))]
        for name,mask in [('masked',pulses|invalid),('guarded',guarded|invalid)]:
            ix=np.flatnonzero(~mask)
            au=x[ix,3].copy();me=x[ix,4].copy()
            dest=out/'prepared'/name;dest.mkdir(parents=True,exist_ok=True)
            au.astype('<f8').tofile(dest/f'{number}_aux.bin');me.astype('<f8').tofile(dest/f'{number}_main.bin')
            record=dict(samples=len(ix),excluded=int(mask.sum()),joins=int((np.diff(ix)>1).sum()),
                        raw_source_index_first=int(ix[0]+first),raw_source_index_last=int(ix[-1]+first),
                        auxiliary=stats(au),measurement=stats(me),phase={})
            for dc in [False,True]:
                _,_,p=phase(au[40:-1],fft,dc)
                record['phase']['dc_removed' if dc else 'dc_retained']=p
            record['auxiliary_spectrum']=raw_spectrum(au,dt,fft)
            info['variants'][name]=record
        keep=~(guarded|invalid)
        if common is None:common=keep.copy()
        else:
            # A one-sample trigger shift can change the remaining record length.
            # Only the shared trigger-relative prefix is eligible for a common window.
            shared_length=min(len(common),len(keep))
            common=common[:shared_length] & keep[:shared_length]
        records[number]=x;metrics.append(info)
    changes=np.diff(np.r_[False,common,False].astype(np.int8));starts=np.flatnonzero(changes==1);ends=np.flatnonzero(changes==-1)
    k=int(np.argmax(ends-starts));left,right=int(starts[k]),int(ends[k])
    for number,x in records.items():
        dest=out/'prepared'/'continuous';dest.mkdir(parents=True,exist_ok=True)
        au=x[left:right,3];me=x[left:right,4]
        au.astype('<f8').tofile(dest/f'{number}_aux.bin');me.astype('<f8').tofile(dest/f'{number}_main.bin')
        _,_,p=phase(au[40:-1],fft,True)
        metrics[numbers.index(number)]['variants']['continuous']=dict(samples=len(au),joins=0,start_after_trigger_s=left*dt,end_after_trigger_s=right*dt,
                                                       auxiliary=stats(au),measurement=stats(me),phase=dict(dc_removed=p))
    write(out/'prepared_manifest.json',metrics)
    write(out/'continuous_window.json',dict(trigger_relative_start_sample=left,trigger_relative_stop_sample=right,
                                           duration_s=(right-left)*dt,selection='longest intersection of guarded finite samples, chosen before correlation'))
    return metrics


def main():
    parser=argparse.ArgumentParser(__doc__)
    parser.add_argument('--input',type=Path,default=Path(r'D:\project\전송 자료\data\new'))
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--mode',choices=['inventory','prepare'],default='inventory')
    parser.add_argument('--numbers',type=int,nargs='+',help='Explicit file numbers; defaults preserve the original investigation.')
    parser.add_argument('--fut-length-m',type=float)
    args=parser.parse_args()
    out=args.output.resolve()
    out.mkdir(parents=True,exist_ok=False)
    config=dict(input=str(args.input),mode=args.mode,explicit_file_numbers=args.numbers,user_hardware=dict(main_fraction=.9,aux_fraction=.1,old_main_fraction=.5,old_aux_fraction=.5,FUT='enhanced fiber in last confirmed setup',FUT_length_m=args.fut_length_m if args.fut_length_m is not None else (20 if not args.numbers else None),auxiliary_coil_m=100,
                reference='tek0004ALL.csv' if not args.numbers else None,same_unchanged_state_signals=['tek0005ALL.csv','tek0006ALL.csv','tek0007ALL.csv'] if not args.numbers else None,
                context='Hardware from user conversation; explicit file roles are not inferred'),
                crop='first CH1 > 3 V through next rising edge or record end; old comparator explicit [167008,4167008)',
                phase_offsets_samples=dict(auxiliary=40,measurement=1),
                mode_hop_mask='not applied to primary inventory; fraction recorded',
                no_baseline_subtraction=True,phase_qc_limit=1e-5,
                native_mkl_dll=str(ROOT/'build_verify_20261109_sampling_control/libMathNetNumericsMKL.dll'))
    write(out/'config.json',config)
    (out/'summary.md').write_text('# RUNNING\n',encoding='utf8')
    start=time.perf_counter();fft=None;error=None;status='FAILED'
    try:
        fft=native.NativeMkl(Path(config['native_mkl_dll']))
        if args.mode=='inventory': inventory(args,out,fft)
        else: prepare_inputs(args,out,fft)
        status='COMPLETED'
    except Exception:
        error=traceback.format_exc();print(error,flush=True)
    finally:
        calls=fft.calls if fft else 0
        if fft: fft.close()
        write(out/'metrics.json',dict(status=status,error=error,elapsed_seconds=time.perf_counter()-start,native_mkl_calls=calls,script_sha256=native.digest(Path(__file__))))
        (out/'summary.md').write_text(f'# {status}\n\nRead-only raw-data/phase investigation. Native MKL required. No data or baseline modification.\n\n{error or "See inventory JSON and subsequent interpretation."}\n',encoding='utf8')
    if status!='COMPLETED':raise SystemExit(1)


if __name__=='__main__':main()
