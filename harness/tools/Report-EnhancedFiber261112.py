"""Reproducible 21 m enhanced-fiber tension report; no physical-axis fitting."""
from __future__ import annotations
import argparse, collections, datetime, hashlib, html, importlib.util, json, re, sys
from pathlib import Path
sys.stdout.reconfigure(encoding='utf-8')
spec = importlib.util.spec_from_file_location('report_helpers', Path(__file__).with_name('Report-Bending261112.py'))
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)
np, plt = helper.np, helper.plt
read, write, med, fmt, csvout, table, save, power_db, curve = (getattr(helper, n) for n in ['read','write','med','fmt','csvout','table','save','power_db','curve'])
BLUE, RED = helper.BLUE, helper.RED
REGIONS = {'before_2_9m': [(2,9)], 'near_before_8_10m': [(8,10)], 'near_after_10_12m': [(10,12)], 'after_10_20m': [(10,20)], 'far_after_12_20m': [(12,20)], 'end_20_21m': [(20,21)], 'legacy_3_5_8_7m': [(3.5,8.7)], 'legacy_5_5_6_5m': [(5.5,6.5)]}
LABELS = {'before_2_9m':'앞쪽 비교 2–9 m','near_before_8_10m':'10 m 직전 8–10 m','near_after_10_12m':'10 m 직후 10–12 m','after_10_20m':'후반 비교 10–20 m','far_after_12_20m':'후반 12–20 m','end_20_21m':'명목 끝단 주변 20–21 m','legacy_3_5_8_7m':'기존 보조 3.5–8.7 m','legacy_5_5_6_5m':'기존 보조 5.5–6.5 m','after_minus_before':'후반 10–20 m - 앞쪽 2–9 m'}

def geometry(ax):
    ax.axvline(10, color='#00897b', ls='--', lw=.9)
    ax.axvline(21, color='0.4', ls=':', lw=.8)

def bands(ax, x, values, color, label):
    lo, hi = np.nanquantile(values, [.1,.9], axis=0)
    ax.fill_between(x, lo, hi, color=color, alpha=.17)
    ax.plot(x, np.nanmedian(values, axis=0), color=color, lw=.9, label=label)

def main():
    ap=argparse.ArgumentParser()
    ap.add_argument('--run', type=Path, required=True)
    ap.add_argument('--probe', type=Path)
    ap.add_argument('--report', type=Path, required=True)
    ap.add_argument('--reuse-images', action='store_true')
    args=ap.parse_args();run=args.run.resolve();out=args.report.resolve();out.mkdir(parents=True,exist_ok=True)
    images=out/'images';images.mkdir(exist_ok=True)
    metrics=read(run/'metrics.json');assert metrics['status']=='COMPLETED',metrics
    case=read(run/'cases.json')[0];sources=case['Sources'];pairs=case['Pairs']
    refs=[s for s in sources if s['State']=='ref'];sigs=[s for s in sources if s['State']=='tension']
    assert len(sources)==23 and len(refs)==13 and len(sigs)==10 and len(pairs)==253
    assert not any(s['Error'] for s in sources) and not any(p['Error'] for p in pairs)
    names={s['Id']:f'R{i+1:02}' for i,s in enumerate(refs)}|{s['Id']:f'T{i+1:02}' for i,s in enumerate(sigs)}
    times={s['Id']:datetime.datetime.strptime(Path(s['Path']).name[:19],'%Y%m%d_%H%M%S_%f') for s in sources}
    arrays={s['Id']:np.loadtxt(run/'files'/s['Id']/'reflectogram.csv',delimiter=',',skiprows=1) for s in sources}
    x=arrays[refs[0]['Id']][:,0]
    rdb=np.array([arrays[s['Id']][:,2] for s in refs]);sdb=np.array([arrays[s['Id']][:,2] for s in sigs])
    rmed=np.median(rdb,axis=0);smed=np.median(sdb,axis=0);rlo,rhi=np.quantile(rdb,[.1,.9],axis=0)
    records=[]
    for s in sources:
        sid=s['Id'];a=arrays[sid];sel=next(r for r in s['Ranges'] if r['StartIndex']==s['Start'])
        keep=(x>=.5)&(x<=30);peak=np.flatnonzero(keep)[np.argmax(a[keep,1])]
        row={'id':sid,'label':names[sid],'state':s['State'],'time':times[sid].isoformat(),'filename':Path(s['Path']).name,'sha256':s['Hash'],'samples':s['Samples'],'dt_s':s['Dt'],'scope_mode':'NORMAL' if '_NORMAL_' in s['Path'] else 'OTHER','masked_fraction':s['Masked']/s['Samples'],'normal_ranges':len(s['Ranges']),'phase_passed_ranges':sum(r['PhaseQcPassed'] for r in s['Ranges']),'selected_phase_rejected_ratio':sel['RejectedAnchorRatio'],'selected_samples':s['Count'],'selected_start':s['Start'],'main_peak_m':float(x[peak]),'main_peak_db':float(a[peak,2]),'raw_ac_rms_v':s['RawDiagnostics']['Measurement_ac_rms_v'],'aux_ac_rms_v':s['RawDiagnostics']['AUX_ac_rms_v']}
        row.update({key+'_db':power_db(a,regions) for key,regions in REGIONS.items()})
        row['after_minus_before_db']=row['after_10_20m_db']-row['before_2_9m_db'];records.append(row)
        fig,ax=plt.subplots(figsize=(11,4));bands(ax,x,rdb,BLUE,'Ref 중앙값 / P10–P90');ax.plot(x,a[:,2],color=RED if s['State']=='tension' else '#546e7a',lw=.7,label=names[sid]);geometry(ax);ax.set_xlim(0,30);ax.set_xlabel('AUX 200 m 기반 거리 (m), 물리 원점 미등록');ax.set_ylabel('dB re 1 V²/m');ax.set_title(f"{names[sid]} · {times[sid]:%H:%M:%S} · AUX 위상 거부 {100*sel['RejectedAnchorRatio']:.3f}%");ax.legend(fontsize=8);save(fig,images/f'source_{sid}_reflectogram.png',args.reuse_images)
    csvout(run/'source_summary.csv',records)
    rrows=[r for r in records if r['state']=='ref'];srows=[r for r in records if r['state']=='tension']
    regional=[]
    for key in [*REGIONS,'after_minus_before']:
        field=key+'_db';rv=[r[field] for r in rrows];sv=[r[field] for r in srows]
        regional.append({'region':key,'ref_median_db':med(rv),'tension_median_db':med(sv),'difference_db':med(sv)-med(rv),'ref_min':min(rv),'ref_max':max(rv),'tension_min':min(sv),'tension_max':max(sv),'signals_outside_ref_range':sum(v<min(rv) or v>max(rv) for v in sv)})
    csvout(run/'regional_power_metrics.csv',regional)
    grid=np.arange(.6,20.9001,.1);rows=[];profiles=[];matrix=np.full((23,23),np.nan);np.fill_diagonal(matrix,1)
    indices={s['Id']:i for i,s in enumerate(sources)}
    for p in pairs:
        q=p['Curve']['Points'];valid=[t for t in q if t['Status']=='VALID_RELATIVE']
        row={'pair':p['Id'],'kind':p['Kind'],'reference':names[p['Reference']],'signal':names[p['Signal']],'reference_id':p['Reference'],'signal_id':p['Signal'],'median_zncc':med([t['PeakZncc'] for t in q]),'valid_points':len(valid),'points':len(q),'median_valid_shift_pm':med([t['CandidateShiftPm'] for t in valid]),'reference_qc':p['ReferenceQc'],'signal_qc':p['SignalQc']}
        for key,regs in REGIONS.items():
            qq=[t for t in q if any(lo<=t['DistanceMeters']<=hi for lo,hi in regs)]
            row[key+'_zncc']=med([t['PeakZncc'] for t in qq]);row[key+'_valid']=sum(t['Status']=='VALID_RELATIVE' for t in qq)
        rows.append(row);i,j=indices[p['Reference']],indices[p['Signal']];matrix[i,j]=matrix[j,i]=row['median_zncc']
        for t in q:profiles.append({'pair':p['Id'],'kind':p['Kind'],'reference':names[p['Reference']],'signal':names[p['Signal']],'distance_m':t['DistanceMeters'],'peak_zncc':t['PeakZncc'],'candidate_shift_pm':t['CandidateShiftPm'],'status':t['Status']})
    csvout(run/'pair_metrics.csv',rows);csvout(run/'all_profiles.csv',profiles)
    cross=[r for r in rows if r['kind']=='ref_tension'];rr=[r for r in rows if r['kind']=='ref_repeat'];ss=[r for r in rows if r['kind']=='tension_repeat']
    assert len(cross)==130 and len(rr)==78 and len(ss)==45
    zref=np.array([curve(p,'PeakZncc',grid) for p in pairs if p['Kind']=='ref_repeat'])
    zcross=np.array([curve(p,'PeakZncc',grid) for p in pairs if p['Kind']=='ref_tension'])
    zsig=np.array([curve(p,'PeakZncc',grid) for p in pairs if p['Kind']=='tension_repeat'])
    fig,axs=plt.subplots(3,1,figsize=(12,10),sharex=True)
    bands(axs[0],x,rdb,BLUE,'Ref 13개');bands(axs[0],x,sdb,RED,'인장 10개');axs[0].legend();axs[0].set_ylabel('dB re 1 V²/m');axs[0].set_title('Enhanced fiber 21 m · 10 m 이후 지그, 정확한 위치·길이 미정\n초록 점선 10 m / 회색 점선 명목 끝단 21 m')
    axs[1].plot(x,smed-rmed,color=RED,lw=.8);axs[1].fill_between(x,rlo-rmed,rhi-rmed,color=BLUE,alpha=.17,label='Ref P10–P90 대비');axs[1].axhline(0,color='0.4',lw=.6);axs[1].set_ylabel('인장 중앙값 - Ref 중앙값 (dB)');axs[1].legend(fontsize=8)
    bands(axs[2],grid,zref,BLUE,'Ref–Ref');bands(axs[2],grid,zcross,RED,'Ref–인장');axs[2].set_ylim(0,1);axs[2].set_ylabel('최대 ZNCC');axs[2].legend(fontsize=8);axs[2].set_xlabel('거리 (m), 물리 원점 미등록 · QC 불합격 결과는 진단값')
    for ax in axs:geometry(ax);ax.set_xlim(0,30)
    save(fig,images/'group_reflectogram.png',args.reuse_images)
    fig,axs=plt.subplots(3,1,figsize=(12,9),sharex=True)
    for state,col in [('ref',BLUE),('tension',RED)]:
        r=[t for t in records if t['state']==state];ts=[datetime.datetime.fromisoformat(t['time']) for t in r]
        axs[0].plot(ts,[t['raw_ac_rms_v'] for t in r],'.-',color=col,label=state)
        axs[1].plot(ts,[t['after_10_20m_db'] for t in r],'.-',color=col,label=state+' 10–20m');axs[1].plot(ts,[t['before_2_9m_db'] for t in r],'--',color=col,label=state+' 2–9m')
        axs[2].plot(ts,[100*t['selected_phase_rejected_ratio'] for t in r],'.-',color=col)
    axs[0].set_title('취득 시간 순서 · 두 상태 모두 NORMAL 모드');axs[0].set_ylabel('CH4 AC RMS (V)');axs[0].legend();axs[1].set_ylabel('FFT 평균 전력 밀도 (dB)');axs[1].legend(fontsize=8);axs[2].set_ylabel('AUX anchor 거부율 (%)');axs[2].axhline(.001,color='0.5',ls=':',label='판정 기준 0.001%');axs[2].legend();axs[2].set_xlabel('파일명 기준 시각 (Korea)');fig.autofmt_xdate();save(fig,images/'chronology.png',args.reuse_images)
    fig,ax=plt.subplots(figsize=(9,8));im=ax.imshow(matrix,vmin=0,vmax=1,cmap='viridis');fig.colorbar(im,ax=ax,label='공간 중앙 최대 ZNCC');ax.set_xticks(range(23),[names[s['Id']] for s in sources],rotation=90,fontsize=8);ax.set_yticks(range(23),[names[s['Id']] for s in sources],fontsize=8);ax.axhline(12.5,color='white');ax.axvline(12.5,color='white');ax.set_title('전체 253쌍 · 대각선 1은 표시용');save(fig,images/'zncc_matrix.png',args.reuse_images)
    # One-metre regions are descriptive bins, not fitted strain plateaus.
    binrows=[];binref=[];binsig=[]
    for lo in range(1,21):
        rv=[power_db(arrays[s['Id']],[(lo,lo+1)]) for s in refs];sv=[power_db(arrays[s['Id']],[(lo,lo+1)]) for s in sigs];binref.append(rv);binsig.append(sv)
        binrows.append({'start_m':lo,'end_m':lo+1,'ref_db':med(rv),'tension_db':med(sv),'delta_db':med(sv)-med(rv),'signals_outside_ref_range':sum(v<min(rv) or v>max(rv) for v in sv)})
    csvout(run/'one_meter_power_metrics.csv',binrows)
    fig,axs=plt.subplots(2,1,figsize=(12,7),sharex=True);centres=np.arange(1,21)+.5
    bands(axs[0],centres,np.array(binref).T,BLUE,'Ref');bands(axs[0],centres,np.array(binsig).T,RED,'인장');axs[0].legend();axs[0].set_ylabel('1 m 구간 평균 PSD (dB)');axs[0].set_title('1 m 구간별 전력 비교 · 지그 길이 또는 strain plateau를 가정하지 않음')
    axs[1].plot(centres,[r['delta_db'] for r in binrows],'.-',color=RED);axs[1].axhline(0,color='0.4');axs[1].set_ylabel('인장 - Ref (dB)');axs[1].set_xlabel('거리 구간 중심 (m)')
    for ax in axs:geometry(ax);ax.set_xlim(.5,21.5)
    save(fig,images/'regional_power.png',args.reuse_images)
    sections=[];case_rows=[]
    for s in sigs:
        sid=s['Id'];label=names[sid];a=arrays[sid];pp=[p for p in pairs if p['Kind']=='ref_tension' and p['Signal']==sid];zz=np.array([curve(p,'PeakZncc',grid) for p in pp]);selected=next(p for p in pp if p['Reference']==refs[-1]['Id']);q=selected['Curve']['Points'];valid=[t for t in q if t['Status']=='VALID_RELATIVE']
        fig,axs=plt.subplots(2,1,figsize=(12,7),sharex=True);bands(axs[0],x,rdb,BLUE,'Ref 13개 중앙값 / P10–P90');axs[0].plot(x,a[:,2],color=RED,lw=.8,label=label);axs[0].legend(fontsize=8);axs[0].set_ylabel('dB re 1 V²/m');axs[0].set_title(f'{label} · {times[sid]:%H:%M:%S} · Ref 13개와 비교');axs[1].plot(x,a[:,2]-rmed,color=RED,lw=.8);axs[1].fill_between(x,rlo-rmed,rhi-rmed,color=BLUE,alpha=.17);axs[1].axhline(0,color='0.4',lw=.6);axs[1].set_ylabel('인장 - Ref 중앙값 (dB)');axs[1].set_xlabel('거리 (m), 물리 원점 미등록')
        for ax in axs:geometry(ax);ax.set_xlim(0,30)
        save(fig,images/f'{label}_reflectogram.png',args.reuse_images)
        fig,axs=plt.subplots(2,1,figsize=(12,7),sharex=True);bands(axs[0],grid,zref,BLUE,'Ref–Ref 78쌍');bands(axs[0],grid,zz,RED,label+' 대 Ref 13쌍');axs[0].set_ylim(0,1.02);axs[0].set_ylabel('최대 Overlap ZNCC');axs[0].legend(fontsize=8);axs[0].set_title(label+' · 상관도와 상대 이동 후보의 품질을 별도 판정')
        invalid=[t for t in q if t['Status']!='VALID_RELATIVE'];axs[1].scatter([t['DistanceMeters'] for t in invalid],[t['CandidateShiftPm'] for t in invalid],s=8,color='0.65',label='R13 대비 품질 미통과 후보');axs[1].scatter([t['DistanceMeters'] for t in valid],[t['CandidateShiftPm'] for t in valid],s=11,color=RED,label='품질 통과 상대 후보');axs[1].set_ylabel('Δλ 후보 (pm)');axs[1].legend(fontsize=8);axs[1].set_xlabel('거리 (m), 창 0.75 m · 절대 인장량 환산 안 함')
        for ax in axs:geometry(ax);ax.set_xlim(.5,21)
        save(fig,images/f'{label}_zncc.png',args.reuse_images)
        fig,axs=plt.subplots(2,1,figsize=(12,7),sharex=True);bands(axs[0],x,rdb,BLUE,'Ref');axs[0].plot(x,a[:,2],color=RED,lw=.8,label=label);axs[0].legend(fontsize=8);axs[0].set_ylabel('dB re 1 V²/m');axs[0].set_title(label+' · 10 m 전후 및 후반부 확대');bands(axs[1],grid,zref,BLUE,'Ref–Ref');bands(axs[1],grid,zz,RED,'Ref–'+label);axs[1].set_ylim(0,1.02);axs[1].set_ylabel('최대 ZNCC');axs[1].legend(fontsize=8);axs[1].set_xlabel('거리 (m) · 10 m는 대략적 지그 시작 위치 기준')
        for ax in axs:geometry(ax);ax.set_xlim(8,21)
        save(fig,images/f'{label}_after10m.png',args.reuse_images)
        pr=[r for r in cross if r['signal_id']==sid];sr=next(r for r in srows if r['id']==sid)
        cr={'case':label,'source':sid,'filename':Path(s['Path']).name,'time':sr['time'],'after_power_delta_db':sr['after_10_20m_db']-med([r['after_10_20m_db'] for r in rrows]),'after_minus_before_delta_db':sr['after_minus_before_db']-med([r['after_minus_before_db'] for r in rrows]),'median_zncc':med([r['median_zncc'] for r in pr]),'after_zncc':med([r['after_10_20m_zncc'] for r in pr]),'valid_points':sum(r['valid_points'] for r in pr),'reference_pairs':len(pr)};case_rows.append(cr)
        note=f"후반 10–20 m 전력 차이 {cr['after_power_delta_db']:+.3f} dB, 앞쪽 대비 후반 차이 {cr['after_minus_before_delta_db']:+.3f} dB. 전체/후반 ZNCC {fmt(cr['median_zncc'])}/{fmt(cr['after_zncc'])}. 품질 통과 상대 이동 후보 {cr['valid_points']}점."
        tr=[[r['reference'],fmt(r['median_zncc']),fmt(r['before_2_9m_zncc']),fmt(r['after_10_20m_zncc']),f"{r['valid_points']}/{r['points']}"] for r in pr]
        sections.append(f'<section id="{label}"><h2>{label} · {sr["time"]}</h2><p>{html.escape(cr["filename"])}</p><p>{note}</p>'+''.join(f'<img src="images/{label}_{kind}.png" loading="lazy" alt="{label} {kind}">' for kind in ['reflectogram','zncc','after10m'])+table(['Ref','전체 ZNCC','2–9 m ZNCC','10–20 m ZNCC','통과/전체'],tr)+'</section>')
    csvout(run/'tension_case_metrics.csv',case_rows)
    def delta(key):return next(r['difference_db'] for r in regional if r['region']==key)
    def pair_summary(rr):return {'pairs':len(rr),'median_zncc':med([r['median_zncc'] for r in rr]),'before_zncc':med([r['before_2_9m_zncc'] for r in rr]),'after_zncc':med([r['after_10_20m_zncc'] for r in rr]),'valid_points':sum(r['valid_points'] for r in rr)}
    result={'raw_rms_ref_v':med([r['raw_ac_rms_v'] for r in rrows]),'raw_rms_tension_v':med([r['raw_ac_rms_v'] for r in srows]),'before_power_delta_db':delta('before_2_9m'),'after_power_delta_db':delta('after_10_20m'),'after_minus_before_delta_db':delta('after_minus_before'),'ref_tension':pair_summary(cross),'ref_repeat':pair_summary(rr),'tension_repeat':pair_summary(ss),'selected_phase_passes':sum(r['selected_phase_rejected_ratio']<=1e-5 for r in records),'all_range_phase_passes':sum(r['phase_passed_ranges'] for r in records),'all_ranges':sum(r['normal_ranges'] for r in records),'main_peak_ref_m':med([r['main_peak_m'] for r in rrows]),'main_peak_tension_m':med([r['main_peak_m'] for r in srows]),'status_counts':dict(collections.Counter(p['status'] for p in profiles))}
    result['raw_electrical_power_delta_db']=float(20*np.log10(result['raw_rms_tension_v']/result['raw_rms_ref_v']))
    result['near_before_8_10m_power_delta_db']=delta('near_before_8_10m')
    result['near_after_10_12m_power_delta_db']=delta('near_after_10_12m')
    result['exclude_first_tension_sensitivity']={'ref_tension':pair_summary([r for r in cross if r['signal_id']!=sigs[0]['Id']]),'after_power_delta_db':med([r['after_10_20m_db'] for r in srows[1:]])-med([r['after_10_20m_db'] for r in rrows]),'after_minus_before_delta_db':med([r['after_minus_before_db'] for r in srows[1:]])-med([r['after_minus_before_db'] for r in rrows])}
    qc_statement=(f"위상 QC를 포함해 품질을 통과한 Ref–인장 상대 이동 후보는 {result['ref_tension']['valid_points']}점이다. " + ('따라서 이 처리 결과로 인장 위치와 변형량을 확정할 수 없다.' if result['ref_tension']['valid_points']==0 else '통과 후보도 거리 원점과 strain 계수 확인 전에는 절대 변형량으로 확정하지 않는다.'))
    finding=f"Ref 13개와 인장 10개를 비교했다. CH4 AC RMS 중앙값 {result['raw_rms_ref_v']:.4f} → {result['raw_rms_tension_v']:.4f} V, 전기 AC 전력 기준 {result['raw_electrical_power_delta_db']:+.2f} dB 차이다. 후반 10–20 m의 FFT 평균 전력 밀도 차이는 {result['after_power_delta_db']:+.2f} dB, 앞쪽 2–9 m는 {result['before_power_delta_db']:+.2f} dB, 각 취득의 후반-앞쪽 차이를 비교하면 {result['after_minus_before_delta_db']:+.2f} dB다. Ref–인장 / Ref–Ref / 인장–인장 ZNCC 공간 중앙값은 {fmt(result['ref_tension']['median_zncc'])} / {fmt(result['ref_repeat']['median_zncc'])} / {fmt(result['tension_repeat']['median_zncc'])}다. {qc_statement}"
    finding+=f" 10 m 직전 8–10 m와 직후 10–12 m의 전력 차이도 각각 {delta('near_before_8_10m'):+.2f}/{delta('near_after_10_12m'):+.2f} dB로, 감소가 지그 이후에만 나타나지는 않는다."
    result['finding']=finding;write(run/'analysis_metrics.json',result)
    probe=read(args.probe/'phase_scan.json') if args.probe else [];proberows=[]
    for low,high in [(1,12),(2,10),(3,8),(4,7)]:
        pr=[r for r in probe if r['low_mhz']==low and r['high_mhz']==high]
        if pr:proberows.append([f'{low}–{high} MHz',len(pr),sum(r['phase_qc']<=1e-5 for r in pr),fmt(100*med([r['phase_qc'] for r in pr]))])
    source_table=table(['이름','상태','시각','AC RMS V','위상 거부 %','통과/전체 구간','주 피크 m','SHA 앞16자'],[[r['label'],r['state'],r['time'][11:],fmt(r['raw_ac_rms_v']),fmt(100*r['selected_phase_rejected_ratio']),f"{r['phase_passed_ranges']}/{r['normal_ranges']}",fmt(r['main_peak_m']),r['sha256'][:16]] for r in records])
    region_table=table(['구간','Ref dB','인장 dB','차이 dB','Ref 전체 범위','인장 전체 범위','Ref 범위 밖 인장 수'],[[LABELS[r['region']],fmt(r['ref_median_db']),fmt(r['tension_median_db']),fmt(r['difference_db']),f"{fmt(r['ref_min'])} ~ {fmt(r['ref_max'])}",f"{fmt(r['tension_min'])} ~ {fmt(r['tension_max'])}",r['signals_outside_ref_range']] for r in regional])
    pair_table=table(['비교','쌍 수','전체 ZNCC','2–9 m','10–20 m','품질 통과 점'],[[label,v['pairs'],fmt(v['median_zncc']),fmt(v['before_zncc']),fmt(v['after_zncc']),v['valid_points']] for label,v in [('Ref–인장',result['ref_tension']),('Ref–Ref',result['ref_repeat']),('인장–인장',result['tension_repeat'])]])
    sensitivity=result['exclude_first_tension_sensitivity']
    intro=f'''<h1>Enhanced fiber 21 m · 지그 인장 비교</h1><p>2026-11-12 · {html.escape(case['Directory'])}</p><p class="verdict">{finding}</p>
    <h2>요청사항과 측정 환경</h2><p>21 m enhanced fiber, 약 10 m 이후 지그 설치. 10:57:08.509–11:00:00.760의 13개를 Ref, 11:19:48.541–11:21:57.808의 10개를 인장 후로 구분했다. 11:19:48 파일은 사용자가 인장 후라고 명시 확인했다. 지그 사이 길이·정확한 위치·인장 이동량은 미정이다. AUX 물리 경로차 200 m는 앞선 사용자 확인값을 유지했다.</p>
    <h2>관측과 해석</h2><p>취득 모드는 모두 NORMAL이다. 다만 Ref와 인장 후 취득 사이에 약 20분 간격이 있고 하중 제거 후 재측정이 없으므로 시간 드리프트와 인장 영향을 완전히 분리할 수 없다. 전기 신호 변화, FFT 패턴 변화, 유효 Rayleigh spectral shift는 서로 다른 지표로 보고한다. 전기 AC 전력 dB를 광학 삽입손실이나 strain으로 환산하지 않았다.</p>
    <p>주 FFT 피크의 중앙 위치는 Ref {result['main_peak_ref_m']:.3f} m, 인장 {result['main_peak_tension_m']:.3f} m다. 이 피크를 21 m 끝단으로 단정하거나 거리축을 이동시키지 않았다. 10 m와 21 m는 사용자 셋업의 명목 기준선이다. 10–20 m는 변화 탐색용 넓은 비교 구간이며, 이 전체 구간에 인장이 가해졌다고 가정하지 않는다. 원점 미등록 및 위상 품질 문제 때문에 그래프상의 위치를 실제 지그 위치로 확정할 수 없다.</p>
    <h2>리플렉토그램과 전력 분포</h2><img src="images/group_reflectogram.png"><img src="images/regional_power.png">{region_table}<p>전력 지표는 선형 PSD를 구간별 적분한 후 길이로 나누고 dB로 변환했다. 후반-앞쪽 지표는 각 취득에서 먼저 두 구간의 dB 차이를 구한 뒤 상태 중앙값을 비교한다. Ref 범위 밖 개수와 P10/P90은 기술적 분포이며 유의성 판정이 아니다. AUX 위상 QC가 실패한 리플렉토그램은 거리 보정이 검증되지 않은 진단 결과다.</p>
    <h2>시간 변화와 ZNCC 반복 기준</h2><img src="images/chronology.png">{pair_table}<img src="images/zncc_matrix.png"><p>Ref×인장 130쌍, Ref×Ref 78쌍, 인장×인장 45쌍을 계산했다. 같은 파일이 여러 비교쌍에 재사용되므로 253쌍을 독립 표본으로 취급하지 않는다. 최대 ZNCC는 검색 중 가장 높은 상관도이며, 그 자체가 위치·이동량의 정확도를 보장하지 않는다.</p>
    <h2>AUX 품질과 최소 실험</h2><p>선택된 최장 정상 구간의 위상 QC 통과는 {result['selected_phase_passes']}/23개, 전체 정상 연속 구간에서는 {result['all_range_phase_passes']}/{result['all_ranges']}개다. 기준은 anchor 거부율 ≤1e-5(0.001%)로 유지했다. {qc_statement} 같은 원본 최장 연속 구간에서 AUX 대역만 4개 조건으로 바꾸고 CH4와 품질 기준을 고정한 민감도 실험은 다음과 같다.</p>{table(['AUX 대역','취득 수','위상 통과 수','거부율 중앙 %'],proberows)}<p>필터를 바꿔도 품질 통과가 생기지 않으면 대역 외 성분 제거만으로 위상 문제가 해결된다는 가설은 채택하지 않는다. 후보 이동량은 품질 미통과 시 회색으로 표시하며 절대 strain으로 환산하지 않는다.</p>
    <h2>분류 경계 민감도</h2><p>사용자 확인에 따라 T01을 본 분석에 포함했다. T01을 제외해도 비교 가능한 117쌍의 ZNCC 중앙값은 {fmt(sensitivity['ref_tension']['median_zncc'])}, 후반 전력 차이 {sensitivity['after_power_delta_db']:+.3f} dB, 앞쪽 대비 후반 차이 {sensitivity['after_minus_before_delta_db']:+.3f} dB, 품질 통과 {sensitivity['ref_tension']['valid_points']}점이다.</p>
    <h2>입력 23개와 개별 리플렉토그램</h2>{source_table}'''
    interpretation=f"<p>10–12 m의 인장 10개 전력은 모두 Ref 전체 범위보다 낮지만, 8–10 m에서도 {delta('near_before_8_10m'):+.3f} dB, 10–12 m에서 {delta('near_after_10_12m'):+.3f} dB로 비슷한 감소가 나타난다. 따라서 전력의 상태 차이는 관측되지만 10 m 이후 지그에만 집중된 인장 효과로 분리하기는 어렵다. 10–20 m 전체 평균은 약 17.7 m의 큰 피크 영향을 많이 받으므로 -0.09 dB만으로 개별 구간의 변화가 없다고 판단하지 않는다. 원본 CH4 AC RMS는 마스킹 전 전체 5M점을 사용하며, 리플렉토그램은 마스킹 후 정상 구간을 사용한다.</p>"
    intro=intro.replace('<h2>시간 변화와 ZNCC 반복 기준</h2>',interpretation+'<h2>시간 변화와 ZNCC 반복 기준</h2>')
    intro+=''.join(f'<details><summary>{names[s["Id"]]} · {html.escape(Path(s["Path"]).name)}</summary><img src="images/source_{s["Id"]}_reflectogram.png" loading="lazy"></details>' for s in sources)
    intro+='<h2>인장 10개 케이스</h2><p>각 케이스에 전체 리플렉토그램, Ref 13개 대비 ZNCC, 10 m 전후 확대를 저장했다. Δλ 후보 도표는 마지막 Ref R13을 기준으로 한다.</p>'
    footer=f'''<h2>이슈·원인·해결방안 및 구현</h2><p>11:19:48의 분류는 사용자 확인으로 해결했다. CSV의 옛 suffix를 채널 역할로 오해하지 않고 CH1=시작 trigger, CH2=mode-hop, CH3=AUX, CH4=측정 계약을 사용했다. 지그 구간 길이는 미정이므로 1 m 구간 탐색과 앞/뒤 넓은 비교를 병행했다. AUX 품질 실패의 물리 원인은 아직 확정되지 않았다. 대역 민감도와 품질 플래그를 보존하고 그래프 모양에 맞춰 기준을 완화하지 않았다. Core 수치 알고리즘과 GUI는 변경하지 않고 전용 분류·전체 실행 명령과 보고서 도구만 추가했다.</p>
    <h2>처리 조건과 검증 결과</h2><p>23개 전체 5,000,000점 CSV를 읽고 Index·Time_s 전체 축, 25 MS/s(dt=40 ns), SHA-256과 4채널 구성을 확인했다. x64 native MKL 필수이며 느린 fallback을 사용하지 않았다. CH2&gt;1.8 V와 100 μs tail을 제외하고 정상 연속 구간을 연결하지 않았다. 각 연속 구간마다 자신의 AUX를 1–12 MHz로 전후 필터링, 양끝 2 μs 제외, 독립 k-선형화, 262144점 전체 Rectangular FFT 후 선형 전력 평균으로 리플렉토그램을 만들었다.</p>
    <p>ZNCC는 취득별 최장 정상 연속 구간을 자신의 AUX로 독립 보정하고 짧은 공통 상대 phase span에 맞췄다. 전체 FFT → 0.75 m 거리 창(0.1 m 간격) → Hann → 8배 IFFT → magnitude Overlap ZNCC → fractional peak 순서다. 분석 0.5–21 m, 검색 상한 ±400 pm(실제 폭은 공통 대역 한계 적용), overlap≥0.5, ZNCC≥0.35, peak 분리≥0.08, peak 제외 폭 8 pm. ng=1.4682와 λ=1550 nm는 가정, τ=ng×200/c=0.979477609 μs다. config의 사용하지 않는 과거 일반 축 기본값과 달리 이번 활성 Document 단계는 이 설정을 사용한다.</p>
    <p>전체 23개와 253쌍에서 계산 오류는 {metrics['failures']}개다. 처리 완료와 물리 유효성은 별도 판정했다. 기존 3.5–8.7 m, 5.5–6.5 m 지표는 보존했지만 이 데이터의 정답 plateau로 사용하지 않았다. Core DLL은 이전 19개 회귀 검증을 통과한 DLL과 SHA를 대조하고 결과를 validation에 기록한다. 이번 수치 알고리즘 변경은 없으며 실제 전체 입력을 재처리했다. 그림·HTML 링크·분류·전수 포함·중복 SHA 여부를 별도로 검증한다.</p>
    <h2>남은 확인 사항</h2><p>지그 시작·끝 거리, 지그 간 거리와 인장 이동량, 거리 원점과 알려진 끝단 반사, 실제 group index 및 AUX 광학 경로, 위상 품질 원인, 동일 조건에서 Ref→인장→복귀 재현이 필요하다. 현재 데이터만으로 인장량을 확정하거나 변화가 없다고 단정하지 않는다.</p><p>실행 결과: {html.escape(str(run))}<br>config, metrics, dataset_manifest, all_profiles.csv, pair_metrics.csv, regional_power_metrics.csv, one_meter_power_metrics.csv, tension_case_metrics.csv, analysis_metrics.json, audit_validation.json, provenance.json에 재현 정보를 저장했다.</p>'''
    css='body{font-family:"Malgun Gothic",sans-serif;max-width:1400px;margin:36px auto;padding:0 24px;color:#172b3a;line-height:1.75}h2{margin-top:32px;border-bottom:2px solid #dce4eb}table{border-collapse:collapse;width:100%;font-size:12px;margin:18px 0}th,td{border:1px solid #dce4eb;padding:7px;overflow-wrap:anywhere;text-align:left}th{background:#edf3f8}img{width:100%;height:auto;margin:14px 0}.verdict{background:#edf5fc;border-left:4px solid #1565c0;padding:16px}section{margin-bottom:45px}'
    page='<!doctype html><html lang="ko"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Enhanced fiber 21 m 인장 분석</title><style>'+css+'</style><body>'+intro+''.join(sections)+footer+'</body></html>'
    (out/'report.html').write_text(page,encoding='utf-8')
    md='# Enhanced fiber 21 m 인장 분석\n\n'+finding+'\n\n전체 조건·해석·검증·제한은 [HTML 리포트](report.html)에 포함했다.\n\n'
    md+=''.join(f'![{kind}](images/{kind}.png)\n\n' for kind in ['group_reflectogram','regional_power','chronology','zncc_matrix'])
    for s in sigs:
        label=names[s['Id']];md+=f'## {label}\n\n'+''.join(f'![{label} {kind}](images/{label}_{kind}.png)\n\n' for kind in ['reflectogram','zncc','after10m'])
    (out/'report.md').write_text(md,encoding='utf-8')
    expected={str(p.resolve()).casefold() for p in Path(case['Directory']).glob('*.csv')};actual={str(Path(s['Path']).resolve()).casefold() for s in sources}
    links=re.findall(r'<img[^>]+src="([^"]+)"',page)
    validation={'all_inputs_covered':expected==actual,'files':len(sources),'unique_sha256':len({s['Hash'] for s in sources}),'references':len(refs),'tension':len(sigs),'first_tension_user_confirmed':Path(sigs[0]['Path']).name.startswith('20261112_111948_541'),'pairs':len(pairs),'unique_distinct_pairs':len({tuple(sorted((p['Reference'],p['Signal']))) for p in pairs}),'all_full_5m':all(s['Samples']==5000000 for s in sources),'all_25msps':all(abs(s['Dt']-4e-8)<1e-16 for s in sources),'all_normal_mode':all('_NORMAL_' in s['Path'] for s in sources),'source_errors':sum(bool(s['Error']) for s in sources),'pair_errors':sum(bool(p['Error']) for p in pairs),'expected_images':57,'actual_images':len(list(images.glob('*.png'))),'html_images':len(links),'missing_images':[v for v in links if not (out/v).is_file()],'case_sections':len(sections),'source_galleries':page.count('<details>'),'script_sha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest()}
    assert validation['all_inputs_covered'] and validation['unique_sha256']==23 and validation['unique_distinct_pairs']==253 and validation['actual_images']==57 and not validation['missing_images']
    write(run/'audit_validation.json',validation)
    print(json.dumps({'report':str(out/'report.html'),'images':57,'finding':finding},ensure_ascii=False))

if __name__=='__main__':main()
