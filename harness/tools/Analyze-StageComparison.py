"""Interpret the fixed-data stage factorial without tuning to the requested ROI.

Run after compare-processing-stages. Reads saved outputs only; no raw reprocessing.
"""
from pathlib import Path
import csv
import hashlib
import json
import os
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.analysis_deps'))
os.environ.setdefault('MPLCONFIGDIR', str(ROOT / '.analysis_cache/matplotlib'))
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt


def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def write(path, value):
    Path(path).write_text(json.dumps(value, indent=2, ensure_ascii=False, allow_nan=False), encoding='utf-8')


def profile(path):
    return np.genfromtxt(path, delimiter=',', names=True, encoding='utf-8-sig')


def series(value):
    y = np.asarray(value['Values'], float)
    return np.linspace(value['XStart'], value['XEnd'], len(y)), y


def stats(p, low, high):
    v = p['lag_samples'][(p['profile_index'] >= low) & (p['profile_index'] <= high)]
    v = v[np.isfinite(v)]
    m = float(np.median(v))
    return dict(count=len(v), median=m, mad_sigma=float(1.4826*np.median(np.abs(v-m))),
                p05=float(np.quantile(v, .05)), p95=float(np.quantile(v, .95)))


def main():
    root = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / 'harness/results/20261109_stage_ablation_v2'
    out = root / 'analysis'
    out.mkdir(exist_ok=True)
    run = read(root/'metrics.json')
    if run['status'] != 'COMPLETED':
        raise RuntimeError('Only completed experiments may be interpreted as complete.')
    golden = ROOT / 'harness/results/20261106_261106_notebook_temperature_verified'
    manifest = read(root/'dataset_manifest.json')
    expected_sources = read(golden/'metrics.json')['Sources']
    source_checks = {v['Key']: v['Sha256'] == expected_sources[i]['Sha256'] for i, v in enumerate(manifest)}
    if not all(source_checks.values()):
        raise AssertionError('Original notebook source checksums differ.')
    rows, numeric_checks = [], []
    profiles = {}
    for ti, label in enumerate(['60C', '80C', '100C']):
        g = profile(golden/f'measurement_{ti}_profile.csv')
        for folder in sorted((root/label).iterdir()):
            if not folder.is_dir():
                continue
            p = profile(folder/'profile.csv')
            profiles[(label, folder.name)] = p
            r = read(folder/'result.json')
            m = {x['Name']: x['Value'] for x in r['PerformanceMetrics']}
            roi, before, after = stats(p, 2500, 3000), stats(p, 500, 2000), stats(p, 3500, 4500)
            row = dict(temperature=label, case=folder.name, roi=roi, before=before, after=after,
                       roi_minus_before=roi['median']-before['median'], roi_minus_after=roi['median']-after['median'],
                       reference_phase=r['KLinearization'], signal_phase=r['SignalKLinearization'], phase_metrics=m,
                       runtime=read(folder/'metrics.json'))
            rows.append(row)
            if folder.name == 'dc0_sort_ind_global':
                mask = np.isfinite(p['lag_samples']) & np.isfinite(g['shift_samples'])
                err = float(np.max(np.abs(p['lag_samples'][mask]-g['shift_samples'][mask])))
                check = dict(temperature=label, points=len(p), max_abs_lag_error=err,
                             positions_equal=bool(np.array_equal(p['fft_bin'], g['fft_bin'])),
                             nan_masks_equal=bool(np.array_equal(np.isnan(p['lag_samples']), np.isnan(g['shift_samples']))))
                check['passed'] = check['positions_equal'] and check['nan_masks_equal'] and err < 1e-6
                numeric_checks.append(check)

    def get(label, case):
        return next(r for r in rows if r['temperature'] == label and r['case'] == case)

    baseline = 'dc0_sort_ind_global'
    # Explicit one-factor contrasts and secondary controls. Never optimize an ROI score.
    comparisons = []
    for label in ['60C', '80C', '100C']:
        base = profiles[label, baseline]
        for case in ['dc1_sort_ind_global', 'dc0_anchor_ind_global', 'dc1_anchor_ind_global',
                     'dc0_sort_common_global', 'dc0_sort_ind_local', 'nb_window_None', 'nb_window_Both',
                     'nb_measurement_dc_retained', 'nb_center_normalize_correlation', 'nb_output_17M', 'nb_offsets_0_0']:
            p = profiles[label, case]
            valid = np.isfinite(base['lag_samples']) & np.isfinite(p['lag_samples'])
            delta = p['lag_samples'][valid]-base['lag_samples'][valid]
            comparisons.append(dict(temperature=label, baseline=baseline, variant=case,
                                    max_abs_lag_difference=float(np.max(np.abs(delta))),
                                    median_abs_lag_difference=float(np.median(np.abs(delta))),
                                    correlation=float(np.corrcoef(base['lag_samples'][valid], p['lag_samples'][valid])[0, 1]),
                                    roi_minus_before=get(label, case)['roi_minus_before']))
    reference_span = get('60C', baseline)['reference_phase']['PhaseSpanRadians']
    summary = dict(status='PASS' if all(c['passed'] for c in numeric_checks) else 'FAIL',
                   source_checks=source_checks, notebook_numerical_checks=numeric_checks,
                   roi_profile_index=[2500, 3000], before_profile_index=[500, 2000], after_profile_index=[3500, 4500],
                   baseline_caution='Outside windows are comparison regions, not independently verified unheated fiber.',
                   coordinate_note='Profile index * 50 is FFT bin. Independent endpoint grids do not have a single physically aligned Ref/Sig distance axis.',
                   provisional_ref_only_roi_m=(np.asarray([125000, 150000])*np.pi*200/reference_span).tolist(),
                   run=run, cases=rows, one_factor_and_controls=comparisons,
                   script_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest())
    control = ROOT / 'harness/results/20261109_stage_sampling_control'
    if read(control/'status.json')['status'] != 'COMPLETED':
        raise RuntimeError('Resampling control is incomplete.')
    sampling_rows = []
    fig, axs = plt.subplots(3, 1, figsize=(13, 10), sharex=True, constrained_layout=True)
    for ax, label in zip(axs, ['60C','80C','100C']):
        for name, color in [('dc0_sort_ind_global', '#111827'), ('nb_output_17M', '#e11d48'),
                            ('dense_then_524k_unfiltered', '#d97706'), ('dense_then_524k_lowpass', '#2563eb')]:
            p = profiles[label, name] if (label, name) in profiles else profile(control/label/name/'profile.csv')
            roi, before, after = stats(p, 2500, 3000), stats(p, 500, 2000), stats(p, 3500, 4500)
            sampling_rows.append(dict(temperature=label, case=name, roi=roi, before=before, after=after,
                                     roi_minus_before=roi['median']-before['median']))
            ax.plot(p['profile_index'], p['lag_samples'], color=color, lw=.65, alpha=.85, label=name)
        ax.axvspan(2500, 3000, color='#fbbf24', alpha=.2); ax.grid(alpha=.2)
        ax.set(title=label, xlim=(400,4600), ylabel='Lag (samples)')
    axs[0].legend(fontsize=9, ncol=2); axs[-1].set_xlabel('Native profile index (hop 50)')
    fig.suptitle('Resampling-density and low-pass control: same notebook phase / span / global Hann')
    fig.savefig(out/'resampling_control.png', dpi=150); plt.close(fig)
    summary['resampling_control'] = dict(path=str(control), metrics=read(control/'metrics.json'), cases=sampling_rows,
                                        note='Low-pass is a research control in uniform k, not a product correction. 17M and 524k share the endpoint phase span; FFT period convention differs by N/(N-1).')
    # A readable summary of the causal control, with full unsmoothed profiles also retained above.
    from matplotlib.font_manager import FontProperties
    kr = FontProperties(fname='C:/Windows/Fonts/malgun.ttf')
    fig, axs = plt.subplots(1, 3, figsize=(12, 4.6), constrained_layout=True, sharey=True)
    for ax, label in zip(axs, ['60C','80C','100C']):
        for name, title, color, marker in [('dc0_sort_ind_global', '노트북 524,288점', '#2563eb', 'o'),
                                         ('nb_output_17M', '17,000,000점', '#e11d48', 's'),
                                         ('dense_then_524k_lowpass', '저역통과 후 524,288점', '#059669', 'x')]:
            r = next(v for v in sampling_rows if v['temperature']==label and v['case']==name)
            ax.plot([0,1],[r['before']['median'],r['roi']['median']],marker=marker,ms=7,lw=1.8,color=color,label=title)
        ax.set_title('Ref vs '+label); ax.set_xticks([0,1],['비교 구간\n500~2000','관심 구간\n2500~3000'],fontproperties=kr)
        ax.axhline(0,color='gray',lw=.7); ax.grid(alpha=.2); ax.set_xlim(-.2,1.2)
    axs[0].set_ylabel('상관 lag 중앙값 (sample)',fontproperties=kr)
    axs[1].legend(prop=kr,loc='lower left',fontsize=9)
    fig.suptitle('국부 변화처럼 보이는 대비가 샘플 축소 방식에 따라 달라집니다',fontproperties=kr,fontsize=15)
    fig.savefig(out/'sampling_summary_kr.png',dpi=160);plt.close(fig)
    write(out/'metrics.json', summary)
    with (out/'case_statistics.csv').open('w', newline='', encoding='utf-8') as f:
        w = csv.writer(f)
        w.writerow(['temperature','case','roi_median','before_median','after_median','roi_minus_before','roi_sigma','before_sigma','after_sigma'])
        for r in rows:
            w.writerow([r['temperature'], r['case'], r['roi']['median'], r['before']['median'], r['after']['median'],
                        r['roi_minus_before'], r['roi']['mad_sigma'], r['before']['mad_sigma'], r['after']['mad_sigma']])

    selected = [(baseline, 'Notebook combination', '#111827'),
                ('dc1_sort_ind_global', 'AUX DC removal only', '#16a34a'),
                ('dc0_anchor_ind_global', 'Monotonic anchors only', '#d97706'),
                ('dc0_sort_common_global', 'Common span only', '#e11d48'),
                ('dc0_sort_ind_local', 'Local Hann only', '#2563eb')]
    fig, axs = plt.subplots(3, 1, figsize=(13, 11), sharex=True, constrained_layout=True)
    for ax, label in zip(axs, ['60C', '80C', '100C']):
        for case, title, color in selected:
            p = profiles[label, case]
            ax.plot(p['profile_index'], p['lag_samples'], color=color, lw=.6, alpha=.85, label=title)
        ax.axvspan(2500, 3000, color='#fbbf24', alpha=.22)
        ax.axhline(0, color='#9ca3af', lw=.6)
        ax.set(title=f'Ref vs {label}: one change at a time', ylabel='Correlation lag (samples)', xlim=(400, 4600))
        ax.grid(alpha=.18)
    axs[0].legend(ncol=3, fontsize=9)
    axs[-1].set_xlabel('Native notebook profile index (hop = 50 FFT bins)')
    fig.suptitle('Same raw input: stage choices change localization\n4096 / hop 50 / padding 8 / N 524288; no mode-hop removal', fontsize=14)
    fig.savefig(out/'one_stage_profiles.png', dpi=150); plt.close(fig)

    # Broad changes can obscure overlays: separate panels, identical y scales per temperature.
    fig, axs = plt.subplots(3, 5, figsize=(18, 9), sharex=True, sharey='row', constrained_layout=True)
    for row, label in enumerate(['60C', '80C', '100C']):
        for col, (case, title, color) in enumerate(selected):
            ax = axs[row, col]; p = profiles[label, case]
            ax.plot(p['profile_index'], p['lag_samples'], color=color, lw=.55)
            ax.axvspan(2500, 3000, color='#fbbf24', alpha=.2); ax.axhline(0, color='gray', lw=.5)
            ax.set_xlim(400, 4600); ax.grid(alpha=.2)
            if row == 0: ax.set_title(title, fontsize=10)
            if col == 0: ax.set_ylabel(label+' lag (sample)')
            if row == 2: ax.set_xlabel('Profile index')
    fig.suptitle('Local shape is an algorithm-dependent observation, not a temperature calibration')
    fig.savefig(out/'stage_panels.png', dpi=150); plt.close(fig)

    table_cases = [baseline, 'dc1_sort_ind_global', 'dc0_anchor_ind_global', 'dc1_anchor_ind_global',
                   'dc0_sort_common_global', 'dc0_sort_ind_local', 'nb_window_None', 'nb_window_Both',
                   'legacy_controls_output_524k', 'legacy_controls_output_17M']
    values = np.array([[get(l, c)['roi_minus_before'] for l in ['60C','80C','100C']] for c in table_cases])
    fig, ax = plt.subplots(figsize=(9, 7), constrained_layout=True)
    bound = np.max(np.abs(values)); im = ax.imshow(values, cmap='RdBu_r', vmin=-bound, vmax=bound, aspect='auto')
    ax.set_yticks(range(len(table_cases)), table_cases); ax.set_xticks(range(3), ['60C','80C','100C'])
    for i in range(len(table_cases)):
        for j in range(3):
            ax.text(j, i, f'{values[i,j]:.1f}', ha='center', va='center', color='white' if abs(values[i,j]) > bound*.6 else 'black')
    ax.set_title('ROI median minus comparison-region median (lag samples)\nROI 2500-3000; comparison 500-2000; not a pass/fail quality score')
    fig.colorbar(im, ax=ax, shrink=.7)
    fig.savefig(out/'roi_contrasts.png', dpi=150); plt.close(fig)
    (out/'summary.md').write_text('# '+summary['status']+'\n\n75 completed fixed-data cases. Notebook-equivalent branch checked against all native archived profile points and NaN positions. The factorial measures processing sensitivity; it does not establish the true heated-fiber position or calibrated strain.\n', encoding='utf-8')
    print(json.dumps(dict(status=summary['status'], checks=numeric_checks, cases=len(rows), elapsed=run['elapsed_seconds']), indent=2))
    if summary['status'] != 'PASS':
        raise AssertionError('Notebook golden differs; inspect saved metrics before claiming equivalence.')


if __name__ == '__main__':
    main()
