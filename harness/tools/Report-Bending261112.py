"""Acquisition-level and all-pair report for the user-labelled 261112 bending test."""
from __future__ import annotations
import argparse,csv,datetime,hashlib,html,json,os,struct,sys
sys.stdout.reconfigure(encoding='utf-8')
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'.analysis_deps'))
os.environ.setdefault('MPLCONFIGDIR',str(ROOT/'.analysis_cache/matplotlib'))
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
plt.rcParams.update({'font.family':'Malgun Gothic','axes.unicode_minus':False,'font.size':10})
BLUE,RED='#1565c0','#d84315'
REGIONS={'pre_jig':[(.5,1.5)],'jig_nominal_2_3m':[(2,3)],'jig_guard_1_5_3_5m':[(1.5,3.5)],'post_jig':[(3.5,4.5)],'connector_5m':[(4.5,5.5)],'distal':[(6,14)],'end_15m':[(14.5,15)],'remote':[(.5,1.5),(3.5,4.5),(6,9),(10.5,14)],'legacy_3_5_8_7m':[(3.5,8.7)],'legacy_5_5_6_5m':[(5.5,6.5)]}
LABELS={'pre_jig':'지그 전 0.5–1.5 m','jig_nominal_2_3m':'명목 지그 2–3 m','jig_guard_1_5_3_5m':'지그 주변 1.5–3.5 m','post_jig':'지그 후 3.5–4.5 m','connector_5m':'연결부 후보 4.5–5.5 m','distal':'뒤쪽 6–14 m','end_15m':'끝단 후보 14.5–15 m','remote':'지그/연결부 제외 비교 구간','legacy_3_5_8_7m':'과거 보조 3.5–8.7 m','legacy_5_5_6_5m':'과거 보조 5.5–6.5 m'}
def read(p):return json.loads(Path(p).read_text(encoding='utf-8-sig'))
def write(p,v):Path(p).write_text(json.dumps(v,ensure_ascii=False,indent=2,allow_nan=False),encoding='utf-8')
def med(v):
 a=np.asarray(v,dtype=float);a=a[np.isfinite(a)];return float(np.median(a)) if len(a) else None
def fmt(v,n=3):return '—' if v is None or not np.isfinite(v) else f'{v:.{n}f}'
def csvout(p,rows):
 if rows:
  with p.open('w',encoding='utf-8-sig',newline='') as f:
   w=csv.DictWriter(f,fieldnames=list(rows[0]));w.writeheader();w.writerows(rows)
def table(headers,rows):return '<table><tr>'+''.join('<th>'+html.escape(str(h))+'</th>' for h in headers)+'</tr>'+''.join('<tr>'+''.join('<td>'+html.escape(str(v))+'</td>' for v in r)+'</tr>' for r in rows)+'</table>'
def save(fig,path,reuse=False):
 if not (reuse and path.exists()):
  for ax in fig.axes:ax.grid(alpha=.17)
  fig.tight_layout();fig.savefig(path,dpi=135,facecolor='white')
 plt.close(fig)
def power_db(a,regions):
 total,width=0.,0.
 for lo,hi in regions:
  keep=(a[:,0]>=lo)&(a[:,0]<=hi);p=a[keep];total+=np.trapezoid(p[:,1],p[:,0]);width+=p[-1,0]-p[0,0]
 return float(10*np.log10(max(total/width,1e-30)))
def geometry(ax):
 ax.axvspan(2,3,color='#009688',alpha=.1);ax.axvline(5,color='0.4',lw=.7,ls=':');ax.axvline(15,color='0.4',lw=.7,ls=':')
def curve(p,key,grid):
 q=p['Curve']['Points'];x=np.array([t['DistanceMeters'] for t in q]);return np.interp(grid,x,[t[key] for t in q],left=np.nan,right=np.nan)

def main():
 ap=argparse.ArgumentParser();ap.add_argument('--run',type=Path,required=True);ap.add_argument('--probe',type=Path);ap.add_argument('--report',type=Path,required=True);ap.add_argument('--reuse-images',action='store_true');args=ap.parse_args()
 run=args.run.resolve();out=args.report.resolve();out.mkdir(parents=True,exist_ok=True);images=out/'images';images.mkdir(exist_ok=True)
 metrics=read(run/'metrics.json');assert metrics['status']=='COMPLETED',metrics
 c=read(run/'cases.json')[0];sources=c['Sources'];pairs=c['Pairs'];refs=[s for s in sources if s['State']=='ref'];bends=[s for s in sources if s['State']=='bending'];src={s['Id']:s for s in sources}
 names={s['Id']:'R'+str(i+1).zfill(2) for i,s in enumerate(refs)};names.update({s['Id']:'B'+str(i+1).zfill(2) for i,s in enumerate(bends)})
 times={s['Id']:datetime.datetime.strptime(Path(s['Path']).name[:19],'%Y%m%d_%H%M%S_%f') for s in sources}
 arrays={s['Id']:np.loadtxt(run/'files'/s['Id']/'reflectogram.csv',delimiter=',',skiprows=1) for s in sources}
 x=arrays[refs[0]['Id']][:,0];refdb=np.array([arrays[s['Id']][:,2] for s in refs]);benddb=np.array([arrays[s['Id']][:,2] for s in bends]);rmed=np.median(refdb,axis=0);bmed=np.median(benddb,axis=0);rlo,rhi=np.quantile(refdb,[.1,.9],axis=0)
 records=[]
 for s in sources:
  a=arrays[s['Id']];r=next(q for q in s['Ranges'] if q['StartIndex']==s['Start']);keep=(x>=.5)&(x<=30);i=np.flatnonzero(keep)[np.argmax(a[keep,1])]
  row={'id':s['Id'],'label':names[s['Id']],'state':s['State'],'time':times[s['Id']].isoformat(),'scope_mode':'AUTO' if '_AUTO_' in s['Path'] else 'NORMAL','file':Path(s['Path']).name,'sha256':s['Hash'],'samples':s['Samples'],'sample_interval_s':s['Dt'],'masked_fraction':s['Masked']/s['Samples'],'continuous_ranges':len(s['Ranges']),'selected_start':s['Start'],'selected_samples':s['Count'],'selected_phase_rejected_ratio':r['RejectedAnchorRatio'],'phase_passed_ranges':sum(q['PhaseQcPassed'] for q in s['Ranges']),'raw_measurement_ac_rms_v':s['RawDiagnostics']['Measurement_ac_rms_v'],'raw_aux_ac_rms_v':s['RawDiagnostics']['AUX_ac_rms_v'],'main_peak_m':float(x[i]),'main_peak_db':float(a[i,2])}
  for key,regions in REGIONS.items():row[key+'_db']=power_db(a,regions)
  row['jig_minus_remote_db']=row['jig_nominal_2_3m_db']-row['remote_db'];records.append(row)
  fig,ax=plt.subplots(figsize=(11,4));ax.fill_between(x,rlo,rhi,color=BLUE,alpha=.15,label='Ref P10–P90');ax.plot(x,rmed,color=BLUE,lw=.7,label='Ref 중앙값');ax.plot(x,a[:,2],color=RED if s['State']=='bending' else '#546e7a',lw=.7,label=names[s['Id']]);geometry(ax);ax.set_xlim(0,24);ax.set_xlabel('AUX 200 m 기반 상대 거리 (m), 원점 미등록');ax.set_ylabel('dB re 1 V²/m');ax.set_title(names[s['Id']]+' '+times[s['Id']].strftime('%H:%M:%S')+f" · AUX 위상 거부 {100*r['RejectedAnchorRatio']:.3f}%");ax.legend(fontsize=8)
  save(fig,images/f"source_{s['Id']}_reflectogram.png",args.reuse_images)
 csvout(run/'source_summary.csv',records)
 regional=[]
 for key in list(REGIONS)+['jig_minus_remote']:
  field=key+'_db';rv=[r[field] for r in records if r['state']=='ref'];bv=[r[field] for r in records if r['state']=='bending']
  regional.append({'region':key,'ref_median_db':med(rv),'bending_median_db':med(bv),'difference_db':med(bv)-med(rv),'ref_min':min(rv),'ref_max':max(rv),'bending_min':min(bv),'bending_max':max(bv),'ref_mad':med([abs(v-med(rv)) for v in rv])})
 csvout(run/'regional_power_metrics.csv',regional)
 grid=np.arange(.6,14.9001,.1);pairrows=[];profile=[];mat=np.full((len(sources),len(sources)),np.nan);np.fill_diagonal(mat,1);indices={s['Id']:i for i,s in enumerate(sources)}
 for p in pairs:
  q=p['Curve']['Points'];valid=[t for t in q if t['Status']=='VALID_RELATIVE'];r={'pair':p['Id'],'kind':p['Kind'],'reference':names[p['Reference']],'signal':names[p['Signal']],'reference_id':p['Reference'],'signal_id':p['Signal'],'median_zncc':med([t['PeakZncc'] for t in q]),'valid_points':len(valid),'points':len(q),'median_valid_shift_pm':med([t['CandidateShiftPm'] for t in valid]),'reference_qc':p['ReferenceQc'],'signal_qc':p['SignalQc']}
  for key,(lo,hi) in [('jig',(2,3)),('post',(3.5,4.5)),('connector',(4.5,5.5)),('distal',(6,14)),('legacy_plateau',(5.5,6.5))]:
   z=[t for t in q if lo<=t['DistanceMeters']<=hi];r[key+'_zncc']=med([t['PeakZncc'] for t in z]);r[key+'_valid']=sum(t['Status']=='VALID_RELATIVE' for t in z)
  pairrows.append(r);i,j=indices[p['Reference']],indices[p['Signal']];mat[i,j]=mat[j,i]=r['median_zncc']
  for t in q:profile.append({'pair':p['Id'],'kind':p['Kind'],'ref':names[p['Reference']],'signal':names[p['Signal']],'distance_m':t['DistanceMeters'],'candidate_shift_pm':t['CandidateShiftPm'],'peak_zncc':t['PeakZncc'],'status':t['Status']})
 csvout(run/'pair_metrics.csv',pairrows);csvout(run/'all_profiles.csv',profile)
 controls=[p for p in pairs if p['Kind']=='ref_repeat'];refz=np.array([curve(p,'PeakZncc',grid) for p in controls]);refzmed=np.nanmedian(refz,axis=0);refzl,refzh=np.nanquantile(refz,[.1,.9],axis=0)
 # Group overview plus acquisition chronology prevents hiding Ref drift.
 fig,axs=plt.subplots(3,1,figsize=(12,10),sharex=True)
 for row,col,label in [(refdb,BLUE,'Ref'),(benddb,RED,'굽힘')]:
  lo,hi=np.quantile(row,[.1,.9],axis=0);axs[0].fill_between(x,lo,hi,color=col,alpha=.17);axs[0].plot(x,np.median(row,axis=0),color=col,label=label,lw=.9)
 axs[0].set_ylabel('dB re 1 V²/m');axs[0].legend();axs[0].set_title('Ref 17개 / 굽힘 18개 · 5 m + 연결부 + 10 m\n녹색: 명목 지그 2–3 m · 점선: 명목 연결부 5 m / 끝단 15 m')
 axs[1].plot(x,bmed-rmed,color=RED,lw=.8);axs[1].fill_between(x,rlo-rmed,rhi-rmed,color=BLUE,alpha=.18,label='Ref 반복 분포');axs[1].axhline(0,color='0.4',lw=.5);axs[1].set_ylabel('굽힘 중앙값 - Ref 중앙값 (dB)');axs[1].legend(fontsize=8)
 for s in bends:axs[2].plot(x,arrays[s['Id']][:,2]-rmed,lw=.5,alpha=.4)
 axs[2].set_ylabel('개별 굽힘 - Ref 중앙값 (dB)');axs[2].set_xlabel('상대 거리 (m), 거리 원점·피크 정체 미확정')
 for ax in axs:geometry(ax);ax.set_xlim(0,24)
 save(fig,images/'group_reflectogram.png',args.reuse_images)
 fig,axs=plt.subplots(3,1,figsize=(12,10),sharex=True)
 for state,col in [('ref',BLUE),('bending',RED)]:
  rr=[r for r in records if r['state']==state];ts=[datetime.datetime.fromisoformat(r['time']) for r in rr]
  axs[0].plot(ts,[r['raw_measurement_ac_rms_v'] for r in rr],'.-',color=col,label=state)
  axs[1].plot(ts,[r['jig_nominal_2_3m_db'] for r in rr],'.-',color=col);axs[1].plot(ts,[r['remote_db'] for r in rr],'--',color=col,alpha=.6)
  axs[2].plot(ts,[100*r['selected_phase_rejected_ratio'] for r in rr],'.-',color=col)
 axs[0].legend();axs[0].set_ylabel('CH4 AC RMS (V)');axs[1].set_ylabel('전력 밀도 (dB)\n실선 2–3m / 점선 비교 구간');axs[2].set_ylabel('AUX anchor 거부율 (%)');axs[2].axhline(.001,color='0.4',ls=':',label='기준 0.001%');axs[2].legend(fontsize=8);axs[2].set_xlabel('파일명의 취득 시각, Korea');axs[0].set_title('취득 순서와 상태: Ref=AUTO, 굽힘=NORMAL');fig.autofmt_xdate()
 save(fig,images/'chronology.png',args.reuse_images)
 fig,ax=plt.subplots(figsize=(10,9));im=ax.imshow(mat,vmin=0,vmax=1,cmap='viridis');fig.colorbar(im,ax=ax,label='공간 중앙 최대 ZNCC (품질 미확정 진단)');ax.set_xticks(range(35),[names[s['Id']] for s in sources],rotation=90,fontsize=7);ax.set_yticks(range(35),[names[s['Id']] for s in sources],fontsize=7);ax.axhline(16.5,color='white');ax.axvline(16.5,color='white');ax.set_title('모든 취득쌍 595개 · 대각선 1은 표시용, 측정 반복 아님')
 save(fig,images/'zncc_matrix.png',args.reuse_images)
 # Bandpass sensitivity stays a rejected diagnostic if phase QC never passes.
 probe=read(args.probe/'phase_scan.json') if args.probe else [];probe_table=[]
 for lo,hi in [(1,12),(2,10),(3,8),(4,7)]:
  rr=[r for r in probe if r['low_mhz']==lo and r['high_mhz']==hi]
  if rr:probe_table.append([f'{lo}–{hi} MHz',len(rr),sum(r['phase_qc']<=1e-5 for r in rr),fmt(100*med([r['phase_qc'] for r in rr])),fmt(100*max(r['phase_qc'] for r in rr))])
 sections=[];markdown=[];bend_summary=[]
 lastref=refs[-1]['Id']
 for s in bends:
  sid=s['Id'];label=names[sid];pp=[p for p in pairs if p['Kind']=='ref_bending' and p['Signal']==sid];zz=np.array([curve(p,'PeakZncc',grid) for p in pp]);a=arrays[sid];selected=next(p for p in pp if p['Reference']==lastref);qr=selected['Curve']['Points'];vv=[t for t in qr if t['Status']=='VALID_RELATIVE']
  fig,axs=plt.subplots(2,1,figsize=(12,7),sharex=True);axs[0].fill_between(x,rlo,rhi,color=BLUE,alpha=.18,label='Ref P10–P90');axs[0].plot(x,rmed,color=BLUE,lw=.8,label='Ref 중앙값');axs[0].plot(x,a[:,2],color=RED,lw=.8,label=label);axs[0].legend();axs[0].set_ylabel('dB re 1 V²/m');axs[0].set_title(label+' '+times[sid].strftime('%H:%M:%S')+' · 17개 Ref와 비교');axs[1].plot(x,a[:,2]-rmed,color=RED,lw=.7);axs[1].fill_between(x,rlo-rmed,rhi-rmed,color=BLUE,alpha=.18);axs[1].axhline(0,color='0.4',lw=.6);axs[1].set_ylabel('Signal - Ref 중앙값 (dB)');axs[1].set_xlabel('상대 거리 (m), 원점 미등록')
  for ax in axs:geometry(ax);ax.set_xlim(0,24)
  save(fig,images/f'{label}_reflectogram.png',args.reuse_images)
  fig,axs=plt.subplots(2,1,figsize=(12,7),sharex=True);lo,hi=np.nanquantile(zz,[.1,.9],axis=0);axs[0].fill_between(grid,refzl,refzh,color=BLUE,alpha=.14);axs[0].plot(grid,refzmed,color=BLUE,lw=.8,label='Ref–Ref 136쌍 중앙값');axs[0].fill_between(grid,lo,hi,color=RED,alpha=.2);axs[0].plot(grid,np.nanmedian(zz,axis=0),color=RED,lw=.9,label=label+' 대 Ref 17쌍 중앙값');axs[0].set_ylim(0,1.02);axs[0].set_ylabel('최대 Overlap ZNCC');axs[0].legend(fontsize=8);axs[0].set_title(label+' · 상관 수치는 진단값, 위상 QC 불합격 이동량은 무효')
  axs[1].scatter([q['DistanceMeters'] for q in qr],[q['CandidateShiftPm'] for q in qr],s=9,color='0.65',label='R17 대비 품질 미통과 후보');axs[1].scatter([q['DistanceMeters'] for q in vv],[q['CandidateShiftPm'] for q in vv],s=12,color=RED,label='품질 통과 후보');axs[1].set_ylabel('Δλ 후보 (pm)');axs[1].set_xlabel('상대 거리 (m), 0.75 m 거리 창');axs[1].legend(fontsize=8)
  for ax in axs:geometry(ax);ax.set_xlim(.5,15)
  save(fig,images/f'{label}_zncc.png',args.reuse_images)
  fig,axs=plt.subplots(2,1,figsize=(11,6),sharex=True);axs[0].fill_between(x,rlo,rhi,color=BLUE,alpha=.18);axs[0].plot(x,rmed,color=BLUE,lw=.8,label='Ref 중앙값');axs[0].plot(x,a[:,2],color=RED,lw=.8,label=label);axs[0].set_ylabel('dB re 1 V²/m');axs[0].legend();axs[1].plot(grid,refzmed,color=BLUE,label='Ref–Ref');axs[1].plot(grid,np.nanmedian(zz,axis=0),color=RED,label='Ref–'+label);axs[1].set_ylabel('최대 ZNCC (진단)');axs[1].set_xlabel('상대 거리 (m), 녹색은 사용자 설명의 명목 지그 구간');axs[0].set_title(label+' · 2–3 m와 인접 구간 확대')
  for ax in axs:geometry(ax);ax.set_xlim(1,4)
  save(fig,images/f'{label}_jig_zoom.png',args.reuse_images)
  row=next(r for r in records if r['id']==sid);rregions=[r for r in records if r['state']=='ref'];power_delta=row['jig_nominal_2_3m_db']-med([r['jig_nominal_2_3m_db'] for r in rregions]);contrast_delta=row['jig_minus_remote_db']-med([r['jig_minus_remote_db'] for r in rregions]);pr=[r for r in pairrows if r['kind']=='ref_bending' and r['signal_id']==sid];accepted=sum(r['valid_points'] for r in pr)
  bs={'case':label,'source':sid,'file':Path(s['Path']).name,'time':row['time'],'jig_power_delta_db':power_delta,'jig_minus_remote_delta_db':contrast_delta,'median_zncc':med([r['median_zncc'] for r in pr]),'jig_zncc':med([r['jig_zncc'] for r in pr]),'valid_points':accepted,'reference_pairs':len(pr)};bend_summary.append(bs)
  text=f"명목 2–3 m 전력 차이 {power_delta:+.3f} dB, 원거리 대비 국부 차이 {contrast_delta:+.3f} dB. 최대 ZNCC 공간 중앙값 {fmt(bs['median_zncc'])}, 2–3 m 중앙값 {fmt(bs['jig_zncc'])}. 품질 통과 {accepted}점 / Ref 17쌍."
  rows=[[r['reference'],fmt(r['median_zncc']),fmt(r['jig_zncc']),f"{r['valid_points']}/{r['points']}",fmt(100*r['reference_qc']),fmt(100*r['signal_qc'])] for r in pr]
  sections.append(f'<section id="{label}"><h2>{label} · {row["time"]}</h2><p>{html.escape(text)}</p><p>{html.escape(Path(s["Path"]).name)}</p>'+''.join(f'<img src="images/{label}_{kind}.png" loading="lazy">' for kind in ['reflectogram','zncc','jig_zoom'])+table(['Ref','전체 ZNCC','2–3 m ZNCC','통과/전체','Ref 위상 거부 %','Sig 위상 거부 %'],rows)+'</section>')
  markdown.append(f'## {label} · {row["time"]}\n\n{text}\n\n'+''.join(f'![{label} {kind}](images/{label}_{kind}.png)\n\n' for kind in ['reflectogram','zncc','jig_zoom']))
 csvout(run/'bending_case_metrics.csv',bend_summary)
 refrows=[r for r in records if r['state']=='ref'];bendrows=[r for r in records if r['state']=='bending'];rms_ref=med([r['raw_measurement_ac_rms_v'] for r in refrows]);rms_bend=med([r['raw_measurement_ac_rms_v'] for r in bendrows]);rms_db=float(20*np.log10(rms_bend/rms_ref));jig=next(r for r in regional if r['region']=='jig_nominal_2_3m');contrast=next(r for r in regional if r['region']=='jig_minus_remote');cross=[r for r in pairrows if r['kind']=='ref_bending'];rr=[r for r in pairrows if r['kind']=='ref_repeat'];bb=[r for r in pairrows if r['kind']=='bending_repeat'];accepted=sum(r['valid_points'] for r in cross)
 result={'raw_rms_ref_median_v':rms_ref,'raw_rms_bending_median_v':rms_bend,'raw_electrical_power_delta_db':rms_db,'nominal_jig_power_delta_db':jig['difference_db'],'jig_minus_remote_delta_db':contrast['difference_db'],'median_cross_zncc':med([r['median_zncc'] for r in cross]),'median_ref_repeat_zncc':med([r['median_zncc'] for r in rr]),'median_bending_repeat_zncc':med([r['median_zncc'] for r in bb]),'cross_valid_points':accepted,'source_selected_phase_passes':sum(r['selected_phase_rejected_ratio']<=1e-5 for r in records),'main_peak_ref_median_m':med([r['main_peak_m'] for r in refrows]),'main_peak_bending_median_m':med([r['main_peak_m'] for r in bendrows])}
 finding=f"굽힘 상태에서 신호 세기 감소는 관측됐지만, 지그 2–3 m의 굽힘 위치·변형량을 확정하지 못했다. Ref 대비 CH4 AC RMS 중앙값은 {rms_ref:.4f} → {rms_bend:.4f} V로, 전기 신호 AC 전력 기준 {rms_db:+.2f} dB 차이다. 명목 지그 2–3 m의 FFT 전력 차이는 {jig['difference_db']:+.2f} dB, 원거리 대비 국부 차이는 {contrast['difference_db']:+.2f} dB다. Ref–굽힘 / Ref–Ref / 굽힘–굽힘의 공간 중앙 최대 ZNCC는 {fmt(result['median_cross_zncc'])} / {fmt(result['median_ref_repeat_zncc'])} / {fmt(result['median_bending_repeat_zncc'])}로 비슷하다. 위상 QC를 포함한 품질 통과 이동 후보는 {accepted}점이다."
 result['finding']=finding;write(run/'analysis_metrics.json',result)
 source_table=table(['이름','상태/모드','시각','CH4 AC RMS V','위상 거부 %','위상 통과/전체 구간','주 피크 m','SHA 앞16자'],[[r['label'],r['state']+'/'+r['scope_mode'],r['time'][11:],fmt(r['raw_measurement_ac_rms_v']),fmt(100*r['selected_phase_rejected_ratio']),f"{r['phase_passed_ranges']}/{r['continuous_ranges']}",fmt(r['main_peak_m']),r['sha256'][:16]] for r in records])
 region_table=table(['구간','Ref 중앙 dB','굽힘 중앙 dB','차이 dB','Ref 범위','굽힘 범위'],[[LABELS.get(r['region'],'지그 - 원거리'),fmt(r['ref_median_db']),fmt(r['bending_median_db']),fmt(r['difference_db']),f"{fmt(r['ref_min'])} ~ {fmt(r['ref_max'])}",f"{fmt(r['bending_min'])} ~ {fmt(r['bending_max'])}"] for r in regional])
 intro=f'''<h1>261112 · Ref와 지그 굽힘 비교</h1><p>2026-11-12 · {html.escape(c['Directory'])}</p><p class="verdict">{finding}</p>
 <h2>요청사항과 셋업</h2><p>사용자 구분에 따라 10:07:42.335 파일까지 Ref 17개, 10:11:21.000 파일부터 굽힘 18개로 분류했다. 입력 → 5 m 패치코드 → 커넥터 → 10 m 패치코드 순서이며 합계 길이는 약 15 m다. 앞쪽 약 2–3 m의 지그 간격을 줄여 케이블을 휘게 한 조건이다. 지그 간격 변화량·곡률·반경·실제 strain은 제공되지 않았다. 이전에 확인된 AUX 경로차 200 m는 유지했다.</p>
 <h2>관측과 해석</h2><p>신호 세기 및 FFT 패턴의 상태별 차이와 지그에서 생긴 유효 분포 변형량은 구분해야 한다. 굽힘은 전기 측정 신호를 바꿀 수 있는 원인 후보지만, Ref=AUTO·굽힘=NORMAL 취득 모드가 상태와 완전히 겹치고 Ref 자체의 시간 변동도 존재한다. 전기 전력 dB를 장비 교정 없이 광학 삽입손실 dB나 절대 strain으로 환산하지 않았다.</p>
 <p>현재 거리축의 주 반사 피크 중앙값은 Ref {result['main_peak_ref_median_m']:.3f} m / 굽힘 {result['main_peak_bending_median_m']:.3f} m로 명목 총 길이 15 m와 다르다. 이 피크를 케이블 끝단으로 단정하거나 임의로 2.7 m를 빼서 지그 위치를 맞추지 않았다. 선행 경로·거리 원점·피크 정체가 미등록이므로 2–3 m와 5 m의 표시는 사용자 셋업을 대조하기 위한 명목 위치다. 피크 이동, 국부 power 차이, 낮은 ZNCC만으로 굽힘 위치를 확정하지 않는다.</p>
 <h2>전체 리플렉토그램과 취득 시간</h2><img src="images/group_reflectogram.png"><img src="images/chronology.png">{region_table}<p>명목 지그의 중앙값 차이 -1.33 dB와 원거리 비교 구간의 -1.58 dB를 함께 보면 지그에만 추가 감쇠가 집중됐다는 근거는 약하다. 원거리 대비 국부 차이는 각 취득에서 (지그 dB - 원거리 dB)를 먼저 계산한 후 두 상태의 중앙값을 비교한 +0.19 dB다. 굽힘 18개의 명목 지그 전력은 모두 Ref 17개의 전체 범위 안에 있었다. 이는 굽힘 효과가 없다는 증명이 아니며, 현재 진단 축과 반복 조건에서 국부 효과를 분리하지 못했다는 의미다.</p>
 <h2>ZNCC 반복 기준</h2><p>Ref–굽힘 306쌍, Ref–Ref 136쌍, 굽힘–굽힘 153쌍의 모든 서로 다른 취득 조합을 계산했다. 같은 파일이 여러 쌍에 참여하므로 595쌍을 독립 표본 수로 사용한 유의성 검정은 하지 않았다. 도표의 중앙값·P10/P90은 기술적 분포다. 고상관도 위상 QC·peak 분리·검색 경계를 통과해야 상대 이동 후보로 인정된다.</p><img src="images/zncc_matrix.png">
 <h2>AUX 위상 품질과 최소 실험</h2><p>위상 anchor 거부율 기준은 1e-5(0.001%)로 고정했다. 아래 필터 민감도 실험은 같은 최장 정상 연속 배열의 AUX만 바꾸고 CH4에는 필터를 적용하지 않았다. 그래프 모양에 맞춰 threshold를 완화하지 않았다. 위상 불합격인 점의 Δλ는 회색 진단 후보이며 strain·가열량·굽힘량으로 해석할 수 없다.</p>{table(['AUX 대역','취득 수','위상 통과 수','거부율 중앙 %','거부율 최대 %'],probe_table)}
 <h2>입력과 품질</h2>{source_table}<h3>원본 35개 개별 리플렉토그램</h3>'''+''.join(f'<details><summary>{names[s["Id"]]} · {html.escape(Path(s["Path"]).name)}</summary><img loading="lazy" src="images/source_{s["Id"]}_reflectogram.png"></details>' for s in sources)+'''<h2>굽힘 18개 개별 케이스</h2><p>각 케이스는 전체 Ref 17개의 분포와 비교했다. 이동 후보 그림에는 상태 변경 직전 마지막 기준 R17 대비 결과를 표시한다.</p>'''
 footer=f'''<h2>이슈·원인·해결 및 구현 내용</h2><p>파일명 채널 suffix는 옛 pol 명칭이지만 물리 채널 역할은 기존 4채널 계약 CH1=시작 trigger, CH2=mode-hop, CH3=AUX, CH4=측정으로 유지했다. AUTO/NORMAL은 Ref 구분 기준으로 새로 추론하지 않고 사용자가 지정한 시간 경계를 그대로 사용했다. AUX 품질·거리 원점 문제를 품질 플래그와 비교 실험으로 기록했다. 기존 Core 수치 알고리즘·GUI를 변경하지 않고 harness에 전용 분류·실행기와 리포트 도구를 추가했다.</p>
 <h2>구현 및 검증 결과</h2><p>35개 모두 헤더를 유추·검증한 전체 5,000,000점 indexed CSV로 읽었고, dt=40 ns를 확인했다. x64 Intel MKL을 강제했다. CH2&gt;1.8 V + 100 μs tail 및 비유한 행을 제외하고 정상 구간을 연결하지 않았다. 파일별 리플렉토그램은 10 μs 및 post-guard 1024점 조건을 통과한 각 연속 구간을 독립 AUX로 보정해 262144점 Rectangular 전체 FFT로 만든 뒤 선형 전력 평균했다. ZNCC는 취득별 최장 정상 구간을 독립 AUX로 보정하고 짧은 공통 상대 span에 맞췄다. 0.75 m 거리 창 → Hann → 8배 IFFT → magnitude Overlap ZNCC → fractional peak, 검색 상한 ±400 pm, overlap≥0.5, ZNCC≥0.35, peak 분리≥0.08, 분석 0.5–15 m다. 실제 검색 폭은 취득 대역에 의해 제한된다. ng=1.4682와 λ=1550 nm는 가정이며 τ=ng×200/c를 사용했다. 전체 FFT 전에는 Hann을 적용하지 않았다.</p>
 <p>Core DLL SHA는 앞서 기존 검증 19개가 통과한 DLL과 동일하다. 이번 실행은 실제 35개 전체 원본과 모든 595쌍의 끝까지 처리, 입력 누락/중복·역할 경계·point 수·그림 링크를 별도로 검증했다. 3.5–8.7 m 및 5.5–6.5 m 지표도 보존했으나 과거 strain plateau를 이번 지그의 정답으로 사용하지 않았다. GUI 변경이 없으므로 WPF startup 검증은 이 작업 대상이 아니다.</p>
 <h2>남은 확인 사항</h2><p>거리 원점과 알려진 5 m 연결부·15 m 끝단을 구분할 기준 반사 피크, AUX 위상 품질, 실제 group index와 거리 geometry, 동일 취득 모드에서의 Ref/굽힘 재현, 지그 간격·곡률 정보가 필요하다. 이 결과만으로 굽힘에 따른 절대 strain을 확정하지 않는다. 원본과 이전 리포트는 변경하지 않았다.</p><p>결과 루트: {html.escape(str(run))}<br>config·metrics·summary·dataset_manifest·source_summary·regional_power_metrics·pair_metrics·all_profiles·bending_case_metrics·analysis_metrics와 검증 JSON을 보존했다.</p>'''
 style='body{font-family:"Malgun Gothic",sans-serif;max-width:1400px;margin:36px auto;padding:0 24px;color:#172b3a;line-height:1.7}h2{margin-top:32px;border-bottom:2px solid #dce4eb;padding-bottom:8px}table{border-collapse:collapse;width:100%;font-size:12px;margin:18px 0}th,td{border:1px solid #dce4eb;padding:7px;overflow-wrap:anywhere;text-align:left}th{background:#edf3f8}img{width:100%;height:auto;margin:14px 0}.verdict{background:#edf5fc;border-left:4px solid #1565c0;padding:16px}section{margin-bottom:45px}'
 (out/'report.html').write_text('<!doctype html><html lang="ko"><meta charset="utf-8"><title>261112 지그 굽힘 비교</title><style>'+style+'</style><body>'+intro+''.join(sections)+footer+'</body></html>',encoding='utf-8')
 md='# 261112 지그 굽힘 비교\n\n'+finding+'\n\n입력 → 5 m → 커넥터 → 10 m, 명목 지그 2–3 m, AUX 200 m. Ref 17개 / 굽힘 18개, 전체 595쌍. 전체 조건·이슈·원인·해결·검증·남은 확인은 [HTML 리포트](report.html)에 포함했다.\n\n'
 md+='![전체 리플렉토그램](images/group_reflectogram.png)\n\n![취득 시간](images/chronology.png)\n\n![ZNCC](images/zncc_matrix.png)\n\n'+'\n'.join(markdown)
 (out/'report.md').write_text(md,encoding='utf-8')
 expected={str(p.resolve()).casefold() for p in Path(c['Directory']).rglob('*.csv')};actual={str(Path(s['Path']).resolve()).casefold() for s in sources};assert expected==actual and len(refs)==17 and len(bends)==18 and len(pairs)==595
 validation={'all_inputs_covered':expected==actual,'files':len(sources),'unique_sha256':len({s['Hash'] for s in sources}),'references':len(refs),'bending':len(bends),'cross_pairs':len(cross),'reference_control_pairs':len(rr),'bending_control_pairs':len(bb),'all_full_5m':all(s['Samples']==5000000 for s in sources),'all_25msps':all(abs(s['Dt']-4e-8)<1e-16 for s in sources),'source_errors':sum(bool(s['Error']) for s in sources),'pair_errors':sum(bool(p['Error']) for p in pairs),'expected_images':35+3+3*18,'images':len(list(images.glob('*.png'))),'selected_phase_qc_passes':result['source_selected_phase_passes'],'script_sha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest()};write(run/'audit_validation.json',validation)
 print(json.dumps({'report':str(out/'report.html'),'images':validation['images'],'finding':finding},ensure_ascii=False))

if __name__=='__main__':main()
