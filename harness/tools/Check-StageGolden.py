"""Compare a completed normal 5M run to archived plateau/baseline/FFT-slope metrics."""
import json
from pathlib import Path
import sys
import statistics

ROOT = Path(__file__).resolve().parents[2]
run = Path(sys.argv[1])
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
r = read(run/'metrics.json')
gold = read(ROOT/'harness/results/20260828_dual_aux_csharp_series_regression/metrics.json')
expected = next(v for v in gold['Series'] if v['DisplacementMm'] == .4)


def band(series, ranges):
    y = series['Values']; start, end = series['XStart'], series['XEnd']
    return statistics.median(v for i, v in enumerate(y) if any(a <= start+(end-start)*i/(len(y)-1) <= b for a, b in ranges))


inside = band(r['SpectralShiftProfile'], [(5.5, 6.5)])
outside = band(r['SpectralShiftProfile'], [(3.7, 5.3), (6.7, 8.4)])
early = band(r['SignalSpectrum'], [(4, 4.5)])
late = band(r['SignalSpectrum'], [(7.5, 8)])
checks = dict(full_5m=r['KLinearization']['SampleCount'] == r['SignalKLinearization']['SampleCount'] == 5000000,
              phase_qc_passed=r['InputPhaseQcPassed'],
              plateau=abs(inside-expected['InsideMedianGHz']) < .05,
              outside=abs(outside-expected['OutsideMedianGHz']) < .05,
              fft_slope=abs(late-early-gold['ReferenceFftSlope']['DifferenceDb']) < .1)
metrics = dict(status='PASS' if all(checks.values()) else 'FAIL', checks=checks,
               source_result=str(run), provider=r['FftProvider'], inside_median_ghz=inside, outside_median_ghz=outside,
               signal_fft_preview_early_db=early, signal_fft_preview_late_db=late, fft_slope_difference_db=late-early,
               expected=expected, expected_fft_slope=gold['ReferenceFftSlope'],
               slope_note='Historical ReferenceFftSlope key actually refers to SignalSpectrum min/max preview; same preview convention retained.',
               performance=r['PerformanceMetrics'])
(run/'regression_metrics.json').write_text(json.dumps(metrics, indent=2, ensure_ascii=False), encoding='utf-8')
(run/'regression_summary.md').write_text('# '+metrics['status']+'\n\nFull 5M native legacy path: plateau, outside baseline, phase QC and distance FFT slope checked against archived 0.4 mm golden.\n', encoding='utf-8')
print(json.dumps(metrics, indent=2, ensure_ascii=False))
if not all(checks.values()):
    raise AssertionError('Golden regression failed')
