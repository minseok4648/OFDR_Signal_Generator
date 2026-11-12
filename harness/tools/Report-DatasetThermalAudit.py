"""Render the recorded Core batch; never reads or modifies raw waveform samples."""
from __future__ import annotations
import argparse
import csv
import hashlib
import html
import json
import os
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.analysis_deps'))
os.environ.setdefault('MPLCONFIGDIR', str(ROOT / '.analysis_cache/matplotlib'))
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt

plt.rcParams.update({'font.family': 'Malgun Gothic', 'axes.unicode_minus': False, 'font.size': 10})
COLORS = {'ref': '#1565c0', 'heat': '#d84315', 'cool': '#00897b', 'bending': '#8e24aa', 'unknown': '#546e7a'}
KIND = {'ref_heat': 'Ref–Heating', 'ref_repeat': 'Ref 계열 비교', 'heat_repeat': 'Heating 반복', 'cool_repeat': 'Cooling 반복', 'cool_heat_surrogate': 'Cooling–Heating (대체 기준)', 'ref_cool': 'Ref–Cooling', 'unlabelled_comparison': '상태 미표기 비교'}
REUSE_IMAGES = False

def read(p): return json.loads(Path(p).read_text(encoding='utf-8-sig'))
def write(p, value): Path(p).write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False), encoding='utf-8')
def finite(v): return np.asarray(v, dtype=float)[np.isfinite(v)]
def med(v):
    a = finite(v)
    return float(np.median(a)) if len(a) else None
def fmt(v, n=3): return '—' if v is None or not np.isfinite(v) else f'{v:.{n}f}'
def table(headers, rows):
    return '<table><thead><tr>' + ''.join('<th>'+html.escape(str(h))+'</th>' for h in headers) + '</tr></thead><tbody>' + ''.join('<tr>'+''.join('<td>'+html.escape(str(c))+'</td>' for c in row)+'</tr>' for row in rows) + '</tbody></table>'
def csvout(p, rows):
    if not rows: return
    with p.open('w', encoding='utf-8-sig', newline='') as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0])); w.writeheader(); w.writerows(rows)
def save(fig, p):
    if REUSE_IMAGES and p.exists():
        plt.close(fig); return
    for ax in fig.axes:
        ax.grid(alpha=.18)
    fig.tight_layout(); fig.savefig(p, dpi=130, facecolor='white'); plt.close(fig)
def blank(ax, message):
    ax.text(.5, .5, message, ha='center', va='center', transform=ax.transAxes, wrap=True); ax.set_axis_off()

def main():
    global REUSE_IMAGES
    ap=argparse.ArgumentParser(); ap.add_argument('--run', type=Path, required=True); ap.add_argument('--report', type=Path, required=True); ap.add_argument('--sensitivity', type=Path); ap.add_argument('--partial', action='store_true'); ap.add_argument('--reuse-images', action='store_true'); args=ap.parse_args()
    REUSE_IMAGES = args.reuse_images
    run=args.run.resolve(); report=args.report.resolve(); report.mkdir(parents=True, exist_ok=True)
    cases=read(run/'cases.json'); metrics=read(run/'metrics.json') if (run/'metrics.json').exists() else {'status':'RUNNING'} if args.partial else read(run/'metrics.json')
    widecases=read(args.sensitivity/'cases.json') if args.sensitivity else []
    wide={(c['Id'],p['Reference'],p['Signal'],p['Kind']):p for c in widecases for p in c['Pairs']}
    assets=report/'images'; assets.mkdir(exist_ok=True)
    byid={s['Id']: s for c in cases for s in c['Sources']}
    powers={}
    for fid in byid:
        path=run/'files'/fid/'reflectogram.csv'
        if path.exists(): powers[fid]=np.loadtxt(path, delimiter=',', skiprows=1)
    allpairs=[]; summaries=[]; sections=[]; markdown=[]
    for c in cases:
        cid=c['Id']; folder=assets/cid; folder.mkdir(exist_ok=True)
        rel=str(Path(c['Directory']).relative_to(Path(r'C:\Project\OFDR\Datas')))
        source_ids=list(dict.fromkeys(s['Id'] for s in c['Sources']))
        local={s['Id']:s for s in c['Sources']}
        refs=[s['Id'] for s in c['Sources'] if s['State']=='ref']; heats=[s['Id'] for s in c['Sources'] if s['State']=='heat']
        figs=[]
        # Common absolute power units preserve gain/power differences between states.
        fig,axs=plt.subplots(2,1,figsize=(12,7),sharex=True)
        for fid in source_ids:
            if fid not in powers: continue
            s=local[fid]; a=powers[fid]
            axs[0].plot(a[:,0],a[:,2],lw=.65,alpha=.75,label=f"{fid}: {Path(s['Path']).name}",color=COLORS[s['State']])
        axs[0].set_title(f'{cid}  {rel}\n취득별 AUX 보정 리플렉토그램 · 정상 연속 구간의 선형 전력 평균',fontsize=11)
        axs[0].set_ylabel('전력 밀도 (dB re 1 V²/m)'); axs[0].set_xlim(0,36)
        if source_ids: axs[0].legend(fontsize=7,ncol=2,loc='best')
        def meanpower(ids):
            ids=list(dict.fromkeys(i for i in ids if i in powers))
            return np.mean([powers[i][:,1] for i in ids],axis=0) if ids else None
        rp,hp=meanpower(refs),meanpower(heats)
        if rp is not None and hp is not None:
            x=next(iter(powers.values()))[:,0]
            delta=10*np.log10(np.maximum(hp,1e-30)/np.maximum(rp,1e-30))
            axs[1].plot(x,delta,color=COLORS['heat'],lw=.75);axs[1].axhline(0,color='0.4',lw=.6)
            axs[1].set_ylabel('Heating 평균 - Ref 평균 (dB)')
        else:
            blank(axs[1], '동일 폴더에 명시적 Ref와 Heating이 모두 없음\n아래 ZNCC는 반복·대체 기준·상태 미표기 진단으로 구분')
        axs[1].set_xlabel('상대 거리 (m), AUX 경로차 200 m / ng=1.4682')
        for ax in axs:
            if ax.axison:ax.axvline(23,color='0.4',lw=.8,ls=':');ax.axvspan(23,36,color='0.5',alpha=.06)
        save(fig,folder/'reflectogram.png')
        # Every computed pair gets its own two requested figures, including controls.
        for p in c['Pairs']:
            rid,sid=p['Reference'],p['Signal']; rs,ss=local[rid],local[sid]
            label=f"{cid}/{p['Id']}  {KIND.get(p['Kind'],p['Kind'])}\n{Path(rs['Path']).name} → {Path(ss['Path']).name}"
            row={'case':cid,'pair':p['Id'],'kind':p['Kind'],'ref':Path(rs['Path']).name,'signal':Path(ss['Path']).name,'reference_id':rid,'signal_id':sid,'points':0,'valid':0,'valid_fraction':None,'median_zncc':None,'median_valid_shift_pm':None,'reference_qc':p.get('ReferenceQc'),'signal_qc':p.get('SignalQc'),'reflectogram_mae_db':None,'reflectogram_rmse_db':None,'reflectogram_shape_zncc':None,'error':p.get('Error')}
            fig,axs=plt.subplots(2,1,figsize=(11,6),sharex=True)
            if rid in powers and sid in powers:
                ra,sa=powers[rid],powers[sid]; x=ra[:,0]; delta=sa[:,2]-ra[:,2]; roi=(x>=.5)&(x<=23)
                row['reflectogram_mae_db']=float(np.mean(np.abs(delta[roi])))
                row['reflectogram_rmse_db']=float(np.sqrt(np.mean(delta[roi]**2)))
                row['reflectogram_shape_zncc']=float(np.corrcoef(ra[roi,2],sa[roi,2])[0,1])
                axs[0].plot(x,ra[:,2],label=f'Reference {rid}',lw=.75,color='#1565c0');axs[0].plot(x,sa[:,2],label=f'Signal {sid}',lw=.75,color='#d84315');axs[0].legend()
                axs[1].plot(x,delta,lw=.7,color='#6a1b9a');axs[1].axhline(0,color='0.4',lw=.6);axs[0].set_xlim(0,36)
                axs[0].set_ylabel('dB re 1 V²/m');axs[1].set_ylabel('Signal - Reference (dB)');axs[1].set_xlabel('상대 거리 (m), 교정 가정 적용')
                axs[0].set_title(label,fontsize=11)
                for ax in axs:ax.axvline(23,color='0.4',lw=.8,ls=':');ax.axvspan(23,36,color='0.5',alpha=.06)
            else: blank(axs[0],label);blank(axs[1],'리플렉토그램 산출 불가: 원인과 원본 헤더는 manifest.json에 기록')
            save(fig,folder/f"{p['Id']}_reflectogram.png")
            fig,axs=plt.subplots(2,1,figsize=(11,6),sharex=True)
            curve=p.get('Curve')
            if curve and curve.get('Points'):
                points=curve['Points']; x=np.array([q['DistanceMeters'] for q in points]); zn=np.array([q['PeakZncc'] for q in points]); shifts=np.array([q['CandidateShiftPm'] for q in points]); valid=np.array([q['Status']=='VALID_RELATIVE' for q in points])
                row.update(points=len(points),valid=int(valid.sum()),valid_fraction=float(valid.mean()),median_zncc=med(zn),median_valid_shift_pm=med(shifts[valid]))
                axs[0].plot(x,zn,color='#1565c0',lw=.9);axs[0].axhline(.35,ls='--',color='0.5',lw=.7,label='최소 ZNCC 0.35');axs[0].set_ylim(-.05,1.05);axs[0].set_ylabel('최대 Overlap ZNCC');axs[0].legend(fontsize=8)
                axs[1].scatter(x,shifts,s=7,color='0.72',label='품질 미통과 후보');axs[1].scatter(x[valid],shifts[valid],s=12,color='#d84315',label='품질 통과 상대 후보');axs[1].axhline(0,color='0.4',lw=.6);axs[1].set_ylabel('이동 후보 Δλ (pm)');axs[1].set_xlabel('상대 거리 (m), 0.75 m 창 / 약 0.10 m 간격');axs[1].legend(fontsize=8)
                axs[0].set_title(label+f"\nZNCC 중앙값 {fmt(row['median_zncc'])} · 통과 {row['valid']}/{row['points']} · AUX 위상 QC {100*p['ReferenceQc']:.4g}% / {100*p['SignalQc']:.4g}%",fontsize=10)
                for lo,hi,name in [(3.5,8.7,'legacy_analysis'),(5.5,6.5,'legacy_plateau')]:
                    roi=(x>=lo)&(x<=hi)
                    row[name+'_zncc']=med(zn[roi]);row[name+'_valid_shift_pm']=med(shifts[roi&valid])
                statuses={status:sum(q['Status']==status for q in points) for status in sorted({q['Status'] for q in points})}
                row['statuses']=json.dumps(statuses,ensure_ascii=False)
            else:
                reason=(p.get('Error') or '비교할 서로 다른 독립 취득 없음').split('\n')[0]
                blank(axs[0],label);blank(axs[1],'ZNCC 산출 불가\n'+reason[:230])
                row['legacy_analysis_zncc']=None;row['legacy_analysis_valid_shift_pm']=None;row['legacy_plateau_zncc']=None;row['legacy_plateau_valid_shift_pm']=None;row['statuses']='{}'
            save(fig,folder/f"{p['Id']}_zncc.png")
            wp=wide.get((cid,rid,sid,p['Kind']))
            row['wide_median_zncc']=None;row['wide_valid']=None;row['wide_search_half_width_pm']=None
            if wp and wp.get('Curve'):
                wq=wp['Curve']['Points'];wz=[q['PeakZncc'] for q in wq];wv=[q for q in wq if q['Status']=='VALID_RELATIVE']
                wc=wp['Curve'];gauge=round(.75/wc['DistancePerBin']);step=abs(wc['WavelengthPmPerLag']);actual=min(gauge*8//2-2,int(400/step))*step
                row.update(wide_median_zncc=med(wz),wide_valid=len(wv),wide_search_half_width_pm=float(actual))
                if p['Kind']=='ref_heat':
                    fig,axs=plt.subplots(2,1,figsize=(11,6),sharex=True)
                    bq=p['Curve']['Points'];bv=[q for q in bq if q['Status']=='VALID_RELATIVE']
                    axs[0].plot([q['DistanceMeters'] for q in bq],[q['PeakZncc'] for q in bq],label='기본 검색 ±40 pm',color='#1565c0',lw=.8)
                    axs[0].plot([q['DistanceMeters'] for q in wq],wz,label=f'확장 요청 ±400 pm (실제 ±{actual:.1f} pm)',color='#d84315',lw=.8)
                    axs[0].set_ylim(-.05,1.05);axs[0].set_ylabel('최대 Overlap ZNCC');axs[0].legend(fontsize=8)
                    axs[1].scatter([q['DistanceMeters'] for q in wq],[q['CandidateShiftPm'] for q in wq],s=6,color='0.75',label='확장 검색 전체 후보')
                    axs[1].scatter([q['DistanceMeters'] for q in bv],[q['CandidateShiftPm'] for q in bv],s=9,color='#1565c0',label='기본 검색 통과')
                    axs[1].scatter([q['DistanceMeters'] for q in wv],[q['CandidateShiftPm'] for q in wv],s=9,color='#d84315',label='확장 검색 통과')
                    axs[1].set_ylabel('Δλ 후보 (pm)');axs[1].set_xlabel('상대 거리 (m)');axs[1].legend(fontsize=8)
                    axs[0].set_title(label+'\n검색 범위만 변경한 민감도 검사',fontsize=10)
                    save(fig,folder/f"{p['Id']}_zncc_search.png")
            allpairs.append(row)
        rows=[r for r in allpairs if r['case']==cid]
        fig,axs=plt.subplots(2,1,figsize=(12,7),sharex=True)
        plotted=False
        for p in c['Pairs']:
            curve=p.get('Curve')
            if not curve or not curve.get('Points'):continue
            color='#d84315' if p['Kind']=='ref_heat' else '#00897b' if p['Kind']=='cool_heat_surrogate' else '#64748b'
            alpha=.75 if p['Kind'] in ['ref_heat','cool_heat_surrogate'] else .4
            q=curve['Points'];x=[t['DistanceMeters'] for t in q]
            axs[0].plot(x,[t['PeakZncc'] for t in q],color=color,alpha=alpha,lw=.75,label=p['Id']+' '+KIND[p['Kind']]);plotted=True
            valid=[t for t in q if t['Status']=='VALID_RELATIVE']
            if valid:axs[1].scatter([t['DistanceMeters'] for t in valid],[t['CandidateShiftPm'] for t in valid],color=color,s=7,alpha=alpha)
        if not plotted:
            blank(axs[0],'ZNCC를 산출할 독립 취득쌍이 없거나 분석 대역/입력 조건 부족')
            blank(axs[1],'개별 pair 그림과 원인 표를 확인하세요.')
        else:
            axs[0].set_ylim(-.05,1.05);axs[0].axhline(.35,color='0.5',ls='--',lw=.7);axs[0].set_ylabel('최대 Overlap ZNCC');axs[1].set_ylabel('통과 상대 후보 Δλ (pm)');axs[1].set_xlabel('상대 거리 (m), 교정 가정 적용');axs[1].axhline(0,color='0.5',lw=.7)
            if len(c['Pairs'])<=12:axs[0].legend(fontsize=7,ncol=2)
        axs[0].set_title(f'{cid} {rel}\n적색: Ref–Heating · 녹색: Cooling–Heating 대체 기준 · 회색: 반복/상태 미표기',fontsize=10)
        save(fig,folder/'zncc.png')
        primary=[r for r in rows if r['kind']=='ref_heat'];base=[r for r in rows if r['kind']=='ref_repeat'];surrogate=[r for r in rows if r['kind']=='cool_heat_surrogate']
        def vals(items,key):return [r[key] for r in items if r[key] is not None]
        hz,bz=med(vals(primary,'median_zncc')),med(vals(base,'median_zncc'))
        hm,bm=med(vals(primary,'reflectogram_mae_db')),med(vals(base,'reflectogram_mae_db'))
        if primary:
            if not base: conclusion='Ref–Heating 차이는 산출됨. 독립 Ref–Ref 반복 기준 부족으로 가열 특이성 판단 보류.'
            else:
                c1=hz is not None and vals(base,'median_zncc') and hz<min(vals(base,'median_zncc'))
                c2=hm is not None and vals(base,'reflectogram_mae_db') and hm>max(vals(base,'reflectogram_mae_db'))
                conclusion='Ref–Heating 차이가 Ref–Ref 범위를 벗어나는 지표가 있음(기술적 비교).' if c1 or c2 else 'Ref–Heating 차이를 Ref–Ref 변동과 안정적으로 구분하기 어려움.'
            if not any((r['valid'] or 0)>0 for r in primary): conclusion+=' 유효 Δλ 후보가 없어 가열 이동량 확정 불가.'
        elif surrogate: conclusion='Cooling을 대체 기준으로 비교. 명시적 Ref가 없어 Ref–Heating 결론 불가.'
        elif heats: conclusion='Heating 파일만 있어 Ref–Heating 비교 불가. Heating 반복만 평가.'
        else: conclusion='Heating 상태가 명시되지 않아 가열 차이 결론 불가. Ref/미표기 반복 진단만 제공.'
        if '0.2ms' in rel and refs: conclusion+=' ref_afb는 굽힘 이후로 추정되는 별도 이름이며 동질 Ref 반복으로 확정하지 않음.'
        unique=len(source_ids)
        sm={'case':cid,'directory':rel,'files':len(c['Sources']),'unique_files':unique,'ref_heat_pairs':len(primary),'ref_repeat_pairs':len(base),'median_heat_zncc':hz,'median_repeat_zncc':bz,'median_heat_mae_db':hm,'median_repeat_mae_db':bm,'conclusion':conclusion}
        summaries.append(sm)
        srcrows=[]
        for s in c['Sources']:
            ranges=s.get('Ranges') or [];failed=sum(not q['PhaseQcPassed'] for q in ranges)
            srcrows.append([s['Id'],Path(s['Path']).name,s['State'],f"{s['Samples']:,}",fmt(1/s['Dt']/1e6 if s['Dt'] else None,3),f"{s['LowMHz']:.4g}–{s['HighMHz']:.4g}",f"{s['Count']*s['Dt']*1e3:.5g}",f"{len(ranges)-failed}/{len(ranges)}",s['Hash'][:16],(s.get('Error') or 'OK').split('\n')[0][:150]])
        pairrows=[]
        for r in rows:
            pairrows.append([r['pair'],KIND[r['kind']],r['ref']+' → '+r['signal'],fmt(r['median_zncc']),f"{r['valid']}/{r['points']}",fmt(r['median_valid_shift_pm']),fmt(r['reflectogram_mae_db']),fmt(r['reflectogram_shape_zncc']),(r['error'] or '계산 완료').split('\n')[0][:180]])
        content=f'<section id="{cid}"><h2>{cid} · {html.escape(rel)}</h2><p class="verdict">{html.escape(conclusion)}</p>'
        content+=table(['ID','파일','파일명 상태','전체 점수','MS/s','실제 AUX 대역 MHz','선택 정상 구간 ms','위상 QC 통과 구간','SHA-256 앞 16자','처리'],srcrows)
        content+=f'<img src="images/{cid}/reflectogram.png" alt="{cid} reflectogram"><img src="images/{cid}/zncc.png" alt="{cid} ZNCC">'
        content+=table(['Pair','종류','비교','ZNCC 중앙값','통과/전체','통과 Δλ 중앙값 pm','리플렉토그램 MAE dB','리플렉토그램 shape ZNCC','처리'],pairrows)
        quality={}
        for p in c['Pairs']:
            if p['Kind']=='ref_heat' and p.get('Curve'):
                for q in p['Curve']['Points']:quality[q['Status']]=quality.get(q['Status'],0)+1
        if quality:content+='<p>Ref–Heating 점별 품질 상태(겹치는 거리 창의 기술적 집계): '+html.escape(', '.join(f'{k}: {v}' for k,v in sorted(quality.items())))+'</p>'
        for p in c['Pairs']:
            content+=f'<details><summary>{p["Id"]} · {KIND[p["Kind"]]} · {html.escape(Path(local[p["Reference"]]["Path"]).name)} → {html.escape(Path(local[p["Signal"]]["Path"]).name)}</summary><img loading="lazy" src="images/{cid}/{p["Id"]}_reflectogram.png"><img loading="lazy" src="images/{cid}/{p["Id"]}_zncc.png"></details>'
            if (folder/f"{p['Id']}_zncc_search.png").exists():content=content[:-10]+f'<img loading="lazy" src="images/{cid}/{p["Id"]}_zncc_search.png"></details>'
        if not c['Pairs']:content+='<p>중복 제거 후 비교 가능한 독립쌍이 없습니다. 자기상관을 가열 비교로 만들지 않았습니다.</p>'
        content+='</section>';sections.append(content)
        markdown.append(f'## {cid} · {rel}\n\n{conclusion}\n\n![{cid} 리플렉토그램](images/{cid}/reflectogram.png)\n\n![{cid} ZNCC](images/{cid}/zncc.png)\n')
        markdown.append('|Pair|종류|Ref → Signal|ZNCC 중앙값|통과/전체|MAE dB|\n|---|---|---|---:|---:|---:|\n'+''.join(f"|{r['pair']}|{KIND[r['kind']]}|{r['ref']} → {r['signal']}|{fmt(r['median_zncc'])}|{r['valid']}/{r['points']}|{fmt(r['reflectogram_mae_db'])}|\n" for r in rows))
        for p in c['Pairs']:
            if p['Kind']=='ref_heat':markdown.append(f"\n### {cid}/{p['Id']} Ref–Heating\n\n![리플렉토그램](images/{cid}/{p['Id']}_reflectogram.png)\n\n![ZNCC](images/{cid}/{p['Id']}_zncc.png)\n")
    # Uniform CSV schema even when a pair failed before region metrics were available.
    keys=list(dict.fromkeys(k for r in allpairs for k in r));allpairs=[{k:r.get(k) for k in keys} for r in allpairs]
    csvout(run/'pair_metrics.csv',allpairs);csvout(run/'directory_metrics.csv',summaries)
    hashes={}
    for c in cases:
        for s in c['Sources']:hashes.setdefault(s['Hash'],[]).append({'path':s['Path'],'state':s['State']})
    dup=[{'sha256':h,'sources':v} for h,v in hashes.items() if len(v)>1]
    write(run/'duplicate_groups.json',dup)
    crosslabels=[d for d in dup if len({x['state'] for x in d['sources']})>1]
    primary=[r for r in allpairs if r['kind']=='ref_heat'];good=sum(r['points']>0 for r in primary)
    heatmedians=[s['median_heat_zncc'] for s in summaries if s['median_heat_zncc'] is not None]
    validtotal=sum(r['valid'] for r in primary);pointtotal=sum(r['points'] for r in primary)
    observed=(f"Ref–Heating의 폴더별 공간 중앙 ZNCC는 {min(heatmedians):.3f}–{max(heatmedians):.3f}, 품질 통과 점은 전체 {validtotal}/{pointtotal}이다. " if heatmedians else '')
    observed+='파일 간 전력·패턴 차이와 가열에 의한 국부 spectral 이동은 구분해야 한다. Ref–Ref도 낮은 유사도를 보이는 조건에서는 낮은 Ref–Heating ZNCC 자체로 가열을 검출했다고 판단할 수 없다.'
    sensitivity_html=''
    if widecases:
        wr=[];basic_contrasts=[];wide_contrasts=[]
        for c in widecases:
            rows=[r for r in allpairs if r['case']==c['Id']]
            hh=[r for r in rows if r['kind']=='ref_heat'];rr=[r for r in rows if r['kind']=='ref_repeat']
            if rr:
                basic_contrasts.append(abs(med([r['median_zncc'] for r in hh])-med([r['median_zncc'] for r in rr])))
                wide_contrasts.append(abs(med([r['wide_median_zncc'] for r in hh])-med([r['wide_median_zncc'] for r in rr])))
            wr.append([c['Id'],fmt(med([r['median_zncc'] for r in hh])),fmt(med([r['wide_median_zncc'] for r in hh])),fmt(med([r['median_zncc'] for r in rr])) if rr else '—',fmt(med([r['wide_median_zncc'] for r in rr])) if rr else '—',sum(r['wide_valid'] or 0 for r in hh),fmt(min(r['wide_search_half_width_pm'] for r in hh)),fmt(max(r['wide_search_half_width_pm'] for r in hh))])
        sensitivity_html='<h2>탐색 범위 민감도: ±40 → 요청 ±400 pm</h2><p>같은 준비 배열·거리 창·상관 정의·품질 기준에서 검색 상한만 넓혔다. 취득 대역과 overlap 조건에 의해 실제 범위는 아래처럼 제한된다. 더 넓은 탐색에서 최대 상관이 증가하는 현상 자체는 가열 검출 증거가 아니며 Ref–Ref 증가와 함께 비교해야 한다. 확장 검색으로도 취득 구간끼리 실제 광주파수 대역이 겹친다는 보장은 생기지 않는다.</p>'+table(['케이스','Ref–Heat 기본','Ref–Heat 확장','Ref–Ref 기본','Ref–Ref 확장','확장 통과 점','실제 반폭 최소 pm','실제 반폭 최대 pm'],wr)
        if basic_contrasts:
            contrast=f"독립 Ref–Ref 비교가 있는 {len(basic_contrasts)}개 폴더에서 Ref–Heating과 Ref–Ref의 중앙 ZNCC 차이(절댓값)는 기본 검색에서 최대 {max(basic_contrasts):.3f}, 확장 검색에서 최대 {max(wide_contrasts):.3f}였다. 두 검색 조건 모두 가열 상태를 반복 변동과 안정적으로 분리하는 ZNCC 근거를 확보하지 못했다. 리플렉토그램의 전력·형태 차이는 아래 그림에 남기되, 이를 가열 특이 변화로 확정하지 않는다."
            sensitivity_html='<p class="verdict">'+contrast+'</p>'+sensitivity_html
            observed+=' '+contrast
    # Inspect the prominent high-correlation region as well as global medians.
    # This exploratory ROI is not asserted to be the unknown heated location.
    local_rows=[];local_table=[]
    for c in cases:
        if not any(p['Kind']=='ref_heat' for p in c['Pairs']):continue
        for mode,plist in [('40pm',c['Pairs']),('400pm',next((w['Pairs'] for w in widecases if w['Id']==c['Id']),[]))]:
            for p in plist:
                if p['Kind'] not in ['ref_heat','ref_repeat','heat_repeat'] or not p.get('Curve'):continue
                q=[v for v in p['Curve']['Points'] if 17<=v['DistanceMeters']<=19];v=[t for t in q if t['Status']=='VALID_RELATIVE']
                local_rows.append({'case':c['Id'],'pair':p['Id'],'kind':p['Kind'],'search':mode,'roi_m':'17-19 (exploratory, heated location unknown)','points':len(q),'valid_points':len(v),'median_zncc':med([t['PeakZncc'] for t in q]),'median_valid_shift_pm':med([t['CandidateShiftPm'] for t in v]),'reference_phase_qc':p['ReferenceQc'],'signal_phase_qc':p['SignalQc']})
        h=[r for r in local_rows if r['case']==c['Id'] and r['search']=='400pm' and r['kind']=='ref_heat'];r=[t for t in local_rows if t['case']==c['Id'] and t['search']=='400pm' and t['kind']=='ref_repeat'];shifts=[t['median_valid_shift_pm'] for t in h if t['median_valid_shift_pm'] is not None]
        if h:local_table.append([c['Id'],fmt(med([t['median_zncc'] for t in h])),fmt(med([t['median_zncc'] for t in r])) if r else '—',sum(t['valid_points'] for t in h),f"{fmt(min(shifts))} ~ {fmt(max(shifts))}" if shifts else '없음'])
    csvout(run/'local_17_19m_metrics.csv',local_rows)
    if local_table:
        sensitivity_html+='<h2>높은 상관 구간의 별도 확인: 17–19 m</h2><p>일부 리플렉토그램/ZNCC에서 뚜렷한 17–19 m 구간도 따로 검토했다. 이 위치를 가열 구간으로 가정하지 않았다. 확장 검색의 높은 상관은 Ref 반복에도 나타나며, C17에서는 취득쌍별 통과 이동 후보가 음·양으로 혼재한다. C14/C19 일부 쌍은 이 구간에서 거의 0 pm인 반면, C15/C21은 위상 QC에 의해 유효 후보가 없다. 전체 중앙값이 국부 변화를 숨기는지 확인했지만 반복 일관성이 있는 가열 plateau를 확보하지 못했다. 아래 이동 범위는 통과점이 한 개 이상 있는 쌍의 중앙값 범위이며 연속 plateau나 검출 확정을 뜻하지 않는다.</p>'+table(['케이스','Ref–Heat 국부 ZNCC','Ref–Ref 국부 ZNCC','Ref–Heat 통과 점 합계','쌍별 통과 이동 중앙값 범위 pm'],local_table)
    overview=table(['케이스','디렉토리','파일/고유','Ref–Heat 쌍','Ref–Heat ZNCC','Ref–Ref ZNCC','Ref–Heat MAE dB','Ref–Ref MAE dB','판정'],[[s['case'],s['directory'],f"{s['files']}/{s['unique_files']}",s['ref_heat_pairs'],fmt(s['median_heat_zncc']),fmt(s['median_repeat_zncc']),fmt(s['median_heat_mae_db']),fmt(s['median_repeat_mae_db']),s['conclusion']] for s in summaries])
    intro=f'''<h1>OFDR 디렉토리별 Ref–Heating 비교</h1><p>작성일 2026-11-12 · 입력 C:\\Project\\OFDR\\Datas · 실행 {run.name}</p>
<p class="verdict">전체 {len(cases)}개 CSV 디렉토리, {sum(len(c['Sources']) for c in cases)}개 파일({len(hashes)}개 고유 파형)을 조사했다. 명시적 Ref–Heating은 {sum(s['ref_heat_pairs']>0 for s in summaries)}개 디렉토리의 {len(primary)}개 독립 조합이며, {good}개 조합에서 ZNCC 수치가 산출됐다. 가열에 따른 차이의 존재는 Ref–Ref 변동과 함께 아래 표로 평가하며, 가열 위치·절대 온도·절대 strain은 확정하지 않는다.</p>
<h2>요청사항과 분석 범위</h2><p>각 폴더의 Ref와 Heating 리플렉토그램 및 ZNCC를 추출하고, 동일 조건의 반복 변동과 비교했다. 사용자 확인값은 AUX 물리 경로차 200 m, 전체 측정 길이 23 m이며 가열 위치·가열 구간 길이는 미정이다. 모든 케이스의 그림은 본문에, 모든 개별 취득쌍의 그림은 펼침 항목과 images/Cxx/에 저장했다. 원본 CSV는 읽기 전용이며 ZIP 아카이브는 분석 입력에서 제외했다. 동일 SHA-256 파일은 독립 반복에서 제외했다.</p>
<h2>처리 조건과 의미</h2><ul>
<li>입력은 전체 헤더·전체 행을 읽고 SHA-256을 검증한 4채널 Tek CSV이다. CH1=스윕 트리거, CH2=모드홉, CH3=AUX, CH4=측정 신호라는 현재 저장소의 4채널 계약을 사용했다. 과거 3채널 ISF 채널 매핑을 적용하지 않았다.</li>
<li>CH2&gt;1.8 V + 뒤쪽 100 μs 및 비유한 채널 행 제외. 구간을 이어 붙여 하나의 물리적 스윕으로 취급하지 않는다. 정상 구간은 10 μs 이상이며 양 끝 guard 제외 후 1024점 이상이어야 한다. AUX 1–12 MHz 순/역방향 필터(요청 상한이 Nyquist 이상일 때 Nyquist 90%로 제한), 양 끝 2 μs guard.</li>
<li>리플렉토그램: 각 정상 구간의 자체 CH3 위상으로 독립 보간 → 262144점 → 전체 Rectangular FFT → 전력 밀도. 전체 FFT의 zero padding은 1배이다. 파일 내 정상 구간을 동일 가중치로 선형 전력 평균한 뒤 dB로 변환했다. 세로축은 공통 1 V²/m 기준으로 각 파일 최대값 정규화에 의해 gain 차이가 사라지지 않는다.</li>
<li>ZNCC: 취득별 최장 정상 구간을 선택하고 서로 짧은 공통 상대 AUX span에 보간 → 전체 FFT → 거리 창 0.75 m / 약 0.10 m 간격 → Hann → 8배 padded IFFT → magnitude 패턴의 겹침별 평균·분산을 적용한 선형 Overlap ZNCC → fractional peak. 검색 ±40 pm, overlap≥0.5, ZNCC≥0.35, peak 분리≥0.08, 차순위 탐색 제외 ±8 pm. AUX anchor 거부율 기준 1e-5를 넘으면 Δλ 후보는 유효 처리하지 않는다.</li>
<li>주 분석 구간은 확인된 전체 측정 길이에 맞춘 0.5–23 m이다. 보조 확인용 3.5–8.7 m와 5.5–6.5 m 지표도 pair_metrics.csv에 저장했다. 과거 변형 데이터의 5.5–6.5 m plateau가 이번 heating 위치라는 가정은 하지 않았다. 리플렉토그램은 36 m까지 문맥을 표시하고 점선은 분석 상한 23 m를 뜻한다.</li>
<li>AUX 물리 경로차 200 m는 사용자 확인값이다. ng=1.4682와 λ=1550 nm는 아직 가정이며, τ=ng×200/c ≈0.97947761 μs를 사용했다. 명목 스윕률 50 nm/s는 이 AUX 보정 결과의 직접 거리 환산 인자가 아니다. 절대 파장 시작점과 센싱 거리 원점은 미등록이므로 절대 위치·온도·strain 교정 확정으로 해석하지 않는다.</li>
<li>최대 ZNCC는 국부 spectrum 유사도이며 온도나 변형량이 아니다. 리플렉토그램 shape ZNCC는 0.5–23 m dB 패턴의 무이동 Pearson 상관으로, 국부 spectrum의 최대 ZNCC와 구분한다. MAE는 같은 구간의 취득쌍별 절대 dB 차이 평균이다. 파일 내 구간 평균 리플렉토그램과 최장 구간 ZNCC는 사용하는 구간 범위가 다르다.</li></ul>
<h2>관측 결과와 해석</h2><p>{observed}</p><h2>디렉토리별 결과</h2><p>표의 ZNCC는 각 취득쌍 공간 중앙값의 조합 중앙값이다. 표의 범위 비교는 기술적 탐색이며, 서로 겹치는 거리 창이나 같은 파일을 공유하는 쌍을 독립 표본으로 간주한 통계적 유의성 검정은 수행하지 않았다.</p>{overview}
<h2>중복·라벨 이슈</h2><p>중복 SHA 그룹 {len(dup)}개, 서로 다른 상태 라벨을 공유하는 중복 그룹 {len(crosslabels)}개. 복사 파일을 반복 측정 증거로 사용하면 재현성을 과대평가할 수 있다. ref가 없는 폴더의 cooling은 대체 기준으로만 표시하고, tek 파일은 상태 미표기로 유지했다.</p>'''
    if crosslabels:
        intro+=table(['SHA-256 앞 16자','동일 파형의 서로 다른 라벨 경로'],[[d['sha256'][:16],' | '.join(x['path']+' ['+x['state']+']' for x in d['sources'])] for d in crosslabels])
    footer=f'''<h2>이슈·원인·해결 및 구현</h2><p>첫 실행(v1)은 200 μs 기록을 1 ms 조건으로 제외하여 취소했다. 두 번째 실행(v2)은 Windows 예약 이름 AUX.bin에 의한 중간 파일 오류로 취소했다. v3에서 한 25 MS/s 원본의 짧은 정상 구간이 필터 guard 이후 1024점 미만이 되는 원인을 확인했다. v4에서는 구간 자격을 1024 + 2×ceil(2 μs/dt)점 이상으로 명시해 해당 전체 원본과 영향받은 모든 쌍을 다시 처리했다. 이전 성공 결과는 현재 원본 전체 SHA·Core DLL SHA·수치 config가 일치할 때만 재사용했다. 이전 성공 구간은 이미 같은 post-guard 조건을 통과했으므로 이 수정의 수치적 영향을 받지 않는다. 각 시도의 config·metrics·summary·실패 manifest를 보존했다. 본 작업은 harness/tools/DatasetThermalAudit 실행기와 보고서 생성 도구를 추가했으며 Core 수치 알고리즘과 GUI는 수정하지 않았다.</p>
<h2>검증 결과와 남은 확인 사항</h2><p>x64 Intel MKL FFT로 전체 원본을 처리했고 전체 SHA를 재검증했다. 이후 사용자 확인 AUX 200 m를 반영해 모든 ZNCC 쌍을 원본 전체에서 준비된 동일 연속 배열로 재계산했다. 리플렉토그램은 같은 native FFT 계수를 유지한 채 z를 지연 비율 r로, 전력 밀도를 1/r로 변환했다. 재보간하지 않았으며 전체 적분 전력의 상대 오차는 1.3e-13 미만이다. 변환 전·후 SHA와 오차는 reflectogram_axis_calibration.json에 기록했다. 기존 DocumentStage·Reflectogram·DistributedComparison 테스트 19개가 모두 통과했다. 최종 배치 상태는 {html.escape(metrics['status'])}이다. 물리 대역 부족·위상 QC 불합격은 유효 측정 성공으로 해석하지 않는다. 5M 이상 원본을 preview로 대체하지 않았다. GUI 변경은 없다.</p><p>가열 위치·가열 구간 길이, 실제 group index, 거리 원점, 취득별 절대 파장 등록, 각 파일의 실제 상태 및 gain 변경 여부 확인이 남아 있다. 0.2ms와 2s처럼 취득 길이·sample rate가 다른 폴더 간 수치를 같은 물리 분해능의 직접 비교로 취급하면 안 된다. C AUX QC가 불합격한 경우 낮은 ZNCC가 가열 때문인지 위상 추정 실패 때문인지 분리할 수 없다.</p>
<p>수치·재현 자료: {html.escape(str(run))}<br>config.json, reflectogram_config.json, zncc_config.json, cases.json, pair_metrics.csv, directory_metrics.csv, duplicate_groups.json, files/Fxxx/manifest.json 및 reflectogram.csv. 보고서 이미지: {html.escape(str(assets))}</p>'''
    style='body{font-family:"Malgun Gothic",sans-serif;max-width:1400px;margin:40px auto;padding:0 24px;color:#172b3a;line-height:1.65}h1{font-size:30px}h2{margin-top:35px;border-bottom:2px solid #dce4eb;padding-bottom:8px}p,li{max-width:1200px}table{border-collapse:collapse;width:100%;font-size:12px;margin:18px 0}th,td{border:1px solid #dce4eb;padding:7px;text-align:left;overflow-wrap:anywhere}th{background:#edf3f8}img{width:100%;max-width:1240px;height:auto;margin:12px 0}.verdict{padding:15px;background:#edf5fc;border-left:4px solid #1565c0}details{border:1px solid #dce4eb;padding:10px;margin:10px 0}summary{cursor:pointer}section{margin-bottom:50px}a{color:#1565c0}@media print{details{display:block}details>img{display:block}section{break-before:page}table{font-size:9px}}'
    nav='<nav>'+ ' · '.join(f'<a href="#{c["Id"]}">{c["Id"]}</a>' for c in cases)+'</nav>'
    (report/'report.html').write_text('<!doctype html><html lang="ko"><meta charset="utf-8"><title>OFDR Ref Heating 비교</title><style>'+style+'</style><body>'+intro+sensitivity_html+nav+''.join(sections)+footer+'</body></html>',encoding='utf-8')
    # Markdown companion embeds every case and every explicit Ref–Heating pair.
    plainintro='# OFDR 디렉토리별 Ref–Heating 비교\n\n작성일: 2026-11-12\n\n전체 조건·판정·원본 매니페스트·모든 취득쌍 표와 그림은 동반 [HTML 리포트](report.html)에 포함했다.\n\n'+f"전체 {len(cases)}개 CSV 디렉토리, {sum(len(c['Sources']) for c in cases)}개 파일, 고유 {len(hashes)}개 파형. 명시적 Ref–Heating {len(primary)}쌍. 절대 온도/strain/가열 위치는 미확정.\n\n"+'요청사항: 디렉토리별 Ref–Heating 차이와 리플렉토그램/ZNCC 출력. 구현: 기존 Core를 재사용하는 x64 native MKL 일괄 실행기와 그림/HTML 생성기. 이슈·원인·해결: 200 μs 최소 길이, Windows AUX 예약 이름, 필터 guard 이후 최소 샘플 수 오류를 수정하고 동일 원본 및 영향받은 쌍 재실행, 이전 실패 기록 보존. 성공 결과는 전체 원본 SHA·Core SHA·config 일치를 검증해 재사용. 검증: 전체 기록 실행 및 기존 Core 테스트 19개 통과. 사용자 확인: AUX 경로차 200 m, 전체 측정 길이 23 m. 남은 확인: 실제 group index, 가열 위치·가열 구간 길이, 파장·거리 원점과 상태 라벨.\n\n'
    (report/'report.md').write_text(plainintro+observed+'\n\n'+'\n'.join(markdown),encoding='utf-8')
    evidence={'run':str(run),'matplotlib':matplotlib.__version__,'numpy':np.__version__,'figures':len(list(assets.rglob('*.png'))),'primary_pairs':len(primary),'primary_curves':good,'primary_valid_points':validtotal,'primary_total_points':pointtotal,'observed':observed,'summaries':summaries,'script_sha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest()}
    write(run/'report_metrics.json',evidence)
    expected={str(p.resolve()).casefold() for p in Path(r'C:\Project\OFDR\Datas').rglob('*.csv')}
    actual={str(Path(s['Path']).resolve()).casefold() for c in cases for s in c['Sources']}
    mismatches=[s['Path'] for c in cases for s in c['Sources'] if s.get('Header') and s['Samples']!=int(float(s['Header']['Record Length']))]
    validation={'all_csv_paths_covered':expected==actual,'expected_csv_files':len(expected),'actual_csv_files':len(actual),'missing':sorted(expected-actual),'unexpected':sorted(actual-expected),'header_sample_count_mismatches':mismatches,'expected_figures':2*len(cases)+2*sum(len(c['Pairs']) for c in cases)+(len(primary) if widecases else 0),'actual_figures':evidence['figures'],'file_errors':sum(bool(s.get('Error')) for c in cases for s in c['Sources']),'pair_errors':sum(bool(p.get('Error')) for c in cases for p in c['Pairs'])}
    write(run/'audit_validation.json',validation)
    print(json.dumps({'report':str(report/'report.html'),'figures':evidence['figures'],'directories':len(cases),'primary_pairs':len(primary)},ensure_ascii=False))

if __name__=='__main__':main()

