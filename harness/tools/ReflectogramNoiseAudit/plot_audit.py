"""Summarize recorded native C# FFT outputs; no source-data FFT is performed here."""
import json
import sys
from pathlib import Path
if len(sys.argv) > 2:
    sys.path.insert(0, sys.argv[2])
import numpy as np
import pandas as pd
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt

root = Path(sys.argv[1])
cases = json.loads((root / 'cases.json').read_text(encoding='utf-8-sig'))
lookup = {c['case']: c for c in cases}
plt.rcParams.update({'font.size': 10, 'axes.spines.top': False, 'axes.spines.right': False})

def load(name):
    return pd.read_csv(root / (name + '.csv')).to_records(index=False)

fig, axes = plt.subplots(2, 2, figsize=(13, 8), constrained_layout=True)
summary = []
for col, role in enumerate(['ref', 'heating']):
    means = sorted(int(c['case'].rsplit('_', 1)[1]) for c in cases if c['case'].startswith(role + '_rect_power_mean_'))
    final_count = means[-1]
    names = [role + '_whole_unmasked', role + '_masked_joined', role + '_rect_power_mean_' + str(final_count)]
    labels = ['General: one FFT, whole record', 'General: one FFT, masked + joined', f'Segmented: mean of {final_count} powers']
    colors = ['#bd6a2a', '#287fa0', '#15835f']
    for name, label, color in zip(names, labels, colors):
        data = load(name)
        selected = (data['distance_m'] >= 26) & (data['distance_m'] <= 27)
        median = float(lookup[name]['noise_p50_db'])
        axes[0, col].plot(data['distance_m'][selected], data['db_density'][selected] - median, lw=.45 if 'General' in label else 1.0, alpha=.8, label=label, color=color)
    axes[0, col].set(title=f'{role}: same background region, median aligned', xlabel='Optical relative distance (m)', ylabel='dB relative to each 26-35 m median', ylim=(-55, 18))
    axes[0, col].legend(fontsize=8, loc='lower left')
    widths = []
    for m in means:
        r = lookup[f'{role}_rect_power_mean_{m}']
        widths.append(float(r['width_p95_p05_db']))
    axes[1, col].plot(means, widths, 'o-', color='#15835f', label='Measured P95-P05 (same Rect window)')
    axes[1, col].set(title=f'{role}: power averaging only, common 4 mm grid', xlabel='Number of continuous segments averaged', ylabel='Central 90% band width (dB)', xticks=means, ylim=(0, 20))
    axes[1, col].axhline(float(lookup[role+'_masked_joined']['width_p95_p05_db']), color='#287fa0', ls='--', label='Single joined FFT')
    axes[1, col].legend(fontsize=8)
    for ax in axes[:, col]: ax.grid(alpha=.2)
    summary.append({'role': role, 'segments': final_count, 'general_whole': lookup[role+'_whole_unmasked'], 'general_joined': lookup[role+'_masked_joined'],
                    'general_whole_default_axis': lookup[role+'_whole_unmasked_gui18m_axis'], 'average_rect': lookup[f'{role}_rect_power_mean_{final_count}'], 'average_hann': lookup[f'{role}_hann_power_mean_{final_count}']})
fig.suptitle('100 MS/s data: single-FFT fluctuations versus averaging linear power\nSame raw files; controlled attribution experiment, not exact PDF reproduction', fontsize=13)
fig.savefig(root / 'noise_band_comparison.png', dpi=170)
plt.close(fig)

# Explicitly quantify how min/max plotting differs from a central percentile width.
display = []
for role in ['ref','heating']:
    data = load(role + '_whole_unmasked_gui18m_axis')
    roi = data[(data['distance_m'] >= 26) & (data['distance_m'] <= 35)]
    for columns in [600, 1000]:
        groups = np.array_split(roi['db_density'], columns)
        display.append({'role':role,'horizontal_groups':columns,'median_group_max_min_db':float(np.median([g.max()-g.min() for g in groups])),
                        'whole_roi_max_min_db':float(np.ptp(roi['db_density'])), 'whole_roi_p95_p05_db':float(np.diff(np.quantile(roi['db_density'],[.05,.95]))[0])})
(root/'analysis_metrics.json').write_text(json.dumps({'cases':summary,'display_envelope':display},ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'cases':summary,'display_envelope':display},ensure_ascii=False,indent=2))
