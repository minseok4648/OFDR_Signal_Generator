"""Plot exported GUI reflectograms, never recompute FFT or alter measured powers."""
import json
import sys
from pathlib import Path
if len(sys.argv) > 3:
    sys.path.insert(0, sys.argv[3])
import numpy as np
import pandas as pd
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt

run = Path(sys.argv[1])
baseline = Path(sys.argv[2])
plt.rcParams.update({'font.size': 10, 'axes.spines.top': False, 'axes.spines.right': False})
fig, axes = plt.subplots(2, 2, figsize=(13, 7.5), sharey=True)
comparison, noise_axes = plt.subplots(1, 2, figsize=(13, 4.3), sharey=True)
measurements = {}
for row, (folder, label, oldname) in enumerate([('reference', 'Ref', 'ref'), ('signal', 'Heating', 'heating')]):
    data = pd.read_csv(run / folder / 'power_density.csv')
    meta = json.loads((run / folder / 'metrics.json').read_text(encoding='utf-8-sig'))
    n = len(meta['Ranges'])
    for col in range(2):
        ax = axes[row, col]
        ax.plot(data.distance_m, data.B_dB_re_1V2_per_m, color='#bd641f', ls='--', lw=.65, label='B: nominal 50 nm/s')
        ax.plot(data.distance_m, data.C_dB_re_1V2_per_m, color='#007eab', lw=.7, label=f'C: AUX phase, {n}-range power mean')
        ax.set(xlim=(0, 36) if col == 0 else (17.75, 18.55), ylim=(-65, 8),
               title=f'{label} | ' + ('Full distance' if col == 0 else 'Corrected main reflection'),
               xlabel='Optical relative distance (m)', ylabel='Power density (dB re 1 V²/m)')
        ax.grid(alpha=.18)
        if col == 0:
            ax.legend(loc='upper left', fontsize=8)
        else:
            peak = meta['Metrics']['C_peak_m']
            ax.axvline(peak, color='#007eab', lw=.7, ls=':')
            ax.text(.03, .94, f'C peak = {peak:.3f} m', transform=ax.transAxes, va='top', color='#007eab')
    old = pd.read_csv(baseline / f'{oldname}_masked_joined.csv', usecols=['distance_m', 'db_density'])
    old_roi = old[(old.distance_m >= 26) & (old.distance_m <= 36)]
    new_roi = data[(data.distance_m >= 26) & (data.distance_m <= 36)]
    old_db = old_roi.db_density.to_numpy()
    new_db = new_roi.C_dB_re_1V2_per_m.to_numpy()
    old_width, new_width = np.ptp(old_db), np.ptp(new_db)
    measurements[label] = {'noise_roi_m': [26, 36], 'old_full_width_dB': float(old_width), 'new_full_width_dB': float(new_width),
        'old_P95_P05_dB': float(np.quantile(old_db, .95) - np.quantile(old_db, .05)),
        'new_P95_P05_dB': float(np.quantile(new_db, .95) - np.quantile(new_db, .05)),
        'old_scope': 'previous independent full-file masked+joined general-mode audit; not exact paired GUI crop',
        'new_scope': 'current GUI power-density CSV, zero-pad x4, 1 mm grid', 'ranges': n, 'phase_qc_failed_ranges': meta['Metrics']['phase_qc_failed_ranges']}
    ax = noise_axes[row]
    # Same physical ROI; align medians only to compare fluctuations, not absolute noise floor.
    ax.plot(old_roi.distance_m, old_db - np.median(old_db), color='#8d9ead', lw=.35, label='General: one FFT (earlier full-file audit)')
    ax.plot(new_roi.distance_m, new_db - np.median(new_db), color='#007eab', lw=.7, label=f'New GUI: {n}-range power mean')
    ax.set(xlim=(26, 27), ylim=(-58, 15), xlabel='Optical relative distance (m)', ylabel='dB relative to each 26–36 m median',
           title=f'{label} | full noise span {old_width:.1f} → {new_width:.1f} dB')
    ax.legend(loc='lower left', fontsize=8); ax.grid(alpha=.18)

fig.suptitle('100 MS/s, 0.2 s | segmented reflectogram from the new GUI', fontsize=16)
fig.text(.5, .018, 'Rectangular window · equal linear-power mean · AUX delay 0.979 µs · ng 1.4682\n1 mm display grid ≠ spatial resolution. PDF script unavailable; phase QC failures remain flagged.', ha='center', fontsize=9)
fig.tight_layout(rect=(0, .07, 1, .95))
fig.savefig(run / 'reflectogram_bc.png', dpi=160)
comparison.suptitle('Noise-band comparison using the same ref.csv / heating.csv', fontsize=15)
comparison.text(.5, .02, 'Median aligned to compare band width. Reduction is estimator variance, not demonstrated hardware-noise reduction.', ha='center', fontsize=9)
comparison.tight_layout(rect=(0, .06, 1, .93))
comparison.savefig(run / 'noise_band_before_after.png', dpi=160)
(run / 'display_comparison_metrics.json').write_text(json.dumps(measurements, indent=2), encoding='utf-8')
print(json.dumps(measurements, indent=2))
