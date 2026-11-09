"""Summarize full WPF experiments without treating notebook lag as calibrated strain."""
import argparse
import hashlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
if (ROOT / '.analysis_deps').exists():
    sys.path.insert(0, str(ROOT / '.analysis_deps'))

import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def stats(values):
    y = values[np.isfinite(values)]
    median = float(np.median(y))
    return {'count': len(y), 'median': median, 'p05': float(np.percentile(y, 5)),
            'p95': float(np.percentile(y, 95)), 'robust_sigma': float(1.4826 * np.median(abs(y - median)))}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--result', type=Path, required=True)
    parser.add_argument('--golden', type=Path, required=True)
    args = parser.parse_args()
    p = args.result
    golden = read_json(args.golden / 'metrics.json')
    golden_sha = {Path(s['Path']).name: s['Sha256'] for s in golden['Sources']}
    labels = ['Ref', '60C', '80C', '100C']
    colors = ['#64748b', '#16a34a', '#f59e0b', '#ef4444']
    results, sources, legacy, notebook = {}, [], {}, {}
    for i, label in enumerate(labels):
        meta = read_json(p / (label + '_preparation.json'))
        expected_sha = golden_sha[Path(meta['SourcePath']).name]
        if meta['SourceSha256'] != expected_sha:
            raise ValueError('Source SHA mismatch: ' + label)
        sources.append({'label': label, 'path': meta['SourcePath'], 'sha256': meta['SourceSha256'],
                        'header': meta['SourceHeader'], 'metadata': label + '_preparation.json',
                        'source_matches_previous_python_golden': True})
        d = np.genfromtxt(p / (label + '_legacy_profile.csv'), delimiter=',', names=True)
        r = read_json(p / (label + '_legacy_result.json'))
        legacy[label] = d
        calibration = r['PhysicalAxis']
        dz = calibration['DistanceMetersPerFftBin']
        limit = next(m['Value'] for m in r['PerformanceMetrics'] if m['Name'] == 'maximum_correlation_search_lag')
        m = d['distance_m'] <= 24
        results[label] = {
            'legacy_qc_passed': r['InputPhaseQcPassed'],
            'ref_phase_rejection_ratio': r['KLinearization']['NonmonotonicRatio'],
            'sig_phase_rejection_ratio': r['SignalKLinearization']['NonmonotonicRatio'],
            'ref_phase_span': r['KLinearization']['PhaseSpanRadians'],
            'sig_phase_span': r['SignalKLinearization']['PhaseSpanRadians'],
            'legacy_output_samples': r['SampleCount'], 'legacy_profile_points': len(d),
            'gauge_m': dz * 4096, 'hop_m': dz * 50,
            'legacy_0_24m_ghz': stats(d['spectral_shift_ghz'][m]),
            'legacy_0_24m_peak_magnitude': stats(d['peak_magnitude'][m]),
            'legacy_0_50m_ghz': stats(d['spectral_shift_ghz']),
            'legacy_search_edge_fraction': float(np.mean(abs(d['lag_samples']) >= limit - .5)),
            'legacy_near_zero_lag_fraction': float(np.mean(abs(d['lag_samples']) < 1))}
        if i:
            n = np.genfromtxt(p / (label + '_notebook_profile.csv'), delimiter=',', names=True)
            nr = read_json(p / (label + '_notebook_result.json'))
            notebook[label] = n
            g = np.genfromtxt(args.golden / ('measurement_' + str(i - 1) + '_profile.csv'), delimiter=',', names=True)
            equal_nan = np.array_equal(np.isnan(n['lag_samples']), np.isnan(g['shift_samples']))
            error = float(np.nanmax(abs(n['lag_samples'] - g['shift_samples'])))
            if not equal_nan or error > 1e-7:
                raise ValueError('Notebook golden mismatch: ' + label)
            finite = np.isfinite(n['lag_samples'])
            in_bins = (d['fft_bin'] >= n['fft_bin'][finite].min()) & (d['fft_bin'] <= n['fft_bin'][finite].max())
            interpolated = np.interp(d['fft_bin'][in_bins], n['fft_bin'][finite], n['lag_samples'][finite])
            results[label].update({
                'notebook_output_samples': nr['SampleCount'], 'notebook_profile_points': len(n),
                'notebook_lag_samples': stats(n['lag_samples']), 'notebook_golden_max_error_samples': error,
                'notebook_golden_nan_mask_equal': equal_nan,
                'notebook_ref_phase_span': nr['KLinearization']['PhaseSpanRadians'],
                'notebook_sig_phase_span': nr['SignalKLinearization']['PhaseSpanRadians'],
                'native_bin_shape_pearson': float(np.corrcoef(d['lag_samples'][in_bins], interpolated)[0, 1]),
                'native_bin_shape_note': 'Diagnostic shape only. Independent notebook phase spans differ; same bin/lag is not the same calibrated physical coordinate.'})

    plt.rcParams.update({'font.size': 11, 'axes.grid': True, 'grid.alpha': .22})
    fig, axs = plt.subplots(2, 1, figsize=(13, 9), constrained_layout=True)
    for label, color in zip(labels, colors):
        d = legacy[label]
        axs[0].plot(d['distance_m'], d['spectral_shift_ghz'], color=color, lw=.65, label=label, alpha=.85)
    axs[0].set(xlabel='Distance (m), provisional OPD = 200 m', ylabel='Spectral shift (GHz)', xlim=(0, 50),
               title='Existing algorithm | mode-hop removal OFF | 4096 / 50 | phase QC FAILED')
    axs[0].axvline(24, color='black', ls='--', lw=1, label='Approx. FUT end (24 m)')
    axs[0].legend(ncol=5, loc='upper right')
    axs[1].axhline(0, color=colors[0], lw=1, label='Ref self = 0')
    for label, color in zip(labels[1:], colors[1:]):
        n = notebook[label]
        axs[1].plot(n['profile_index'], n['lag_samples'], color=color, lw=.65, label=label)
    axs[1].set(xlabel='Notebook profile index (NOT distance)', ylabel='Correlation lag (samples)',
               title='Notebook algorithm | same raw crops | 4096 / 50 | uncalibrated diagnostic')
    axs[1].legend(ncol=4, loc='upper right')
    fig.savefig(p / 'legacy_vs_notebook.png', dpi=150)
    plt.close(fig)

    fig, axs = plt.subplots(3, 1, figsize=(13, 10), sharex=True, constrained_layout=True)
    for ax, label in zip(axs, labels[1:]):
        d, n = legacy[label], notebook[label]
        ax.plot(d['fft_bin'], d['lag_samples'], color='#2563eb', lw=.7, label='Existing, no removal')
        ax.plot(n['fft_bin'], n['lag_samples'], color='#f97316', lw=.7, label='Notebook')
        ax.set(title=label, ylabel='Native lag samples', xlim=(0, 262144))
        ax.legend(loc='upper right')
    axs[-1].set_xlabel('Native FFT bin; diagnostic shape comparison, NOT matched physical positions')
    fig.suptitle('Removing the mode-hop mask does not reproduce the notebook processing', fontsize=15)
    fig.savefig(p / 'native_bin_shape_comparison.png', dpi=150)
    plt.close(fig)
    output = {'status': 'COMPLETED', 'note': 'QC-failed diagnostic; no claim of calibrated temperature or absolute strain.',
              'settings': read_json(p / 'config.json'), 'metrics': results, 'sources': sources,
              'notebook_golden_directory': str(args.golden.resolve())}
    (p / 'comparison_metrics.json').write_text(json.dumps(output, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    (p / 'dataset_manifest.json').write_text(json.dumps(sources, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    artifacts = [{'path': str(f.resolve()), 'sha256': hashlib.sha256(f.read_bytes()).hexdigest()}
                 for f in sorted(p.glob('*')) if f.is_file() and f.suffix in ('.csv', '.json', '.png')]
    (p / 'artifact_manifest.json').write_text(json.dumps(artifacts, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    for label, m in results.items():
        print(label, 'GHz 0..24m', m['legacy_0_24m_ghz'], 'QC', m['sig_phase_rejection_ratio'],
              'shape r', m.get('native_bin_shape_pearson'))


if __name__ == '__main__':
    main()
