using System;
using System.Numerics;
using System.Threading;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Processing
{
    public static class StageZncc
    {
        // Returns circular-index storage for lags within +/- (N/2-2). In overlap
        // mode the FFT itself is padded to 2N, so no wrapped samples enter ZNCC.
        public static void Correlate(Complex[] a, Complex[] b, StageCorrelationMode mode, double minimumOverlap, FftEngine fft, CancellationToken token)
        {
            var n = a.Length; var length = mode == StageCorrelationMode.OverlapZncc ? checked(2 * n) : n;
            var x = new Complex[length]; var y = new Complex[length];
            var sx = new double[n + 1]; var sy = new double[n + 1]; var ex = new double[n + 1]; var ey = new double[n + 1];
            double mx = 0, my = 0;
            for (var i = 0; i < n; i++) { mx += a[i].Magnitude; my += b[i].Magnitude; } mx /= n; my /= n;
            for (var i = 0; i < n; i++)
            {
                if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
                var u = a[i].Magnitude - mx; var v = b[i].Magnitude - my;
                x[i] = new Complex(u, 0); y[i] = new Complex(v, 0);
                sx[i + 1] = sx[i] + u; sy[i + 1] = sy[i] + v; ex[i + 1] = ex[i] + u * u; ey[i + 1] = ey[i] + v * v;
            }
            fft.ForwardInPlace(x); fft.ForwardInPlace(y);
            for (var i = 0; i < length; i++) y[i] *= Complex.Conjugate(x[i]);
            fft.InverseInPlace(y);
            for (var i = 0; i < n; i++) b[i] = new Complex(double.NaN, 0);
            for (var lag = -n / 2 + 1; lag < n / 2; lag++)
            {
                if ((lag & 0x3fff) == 0) token.ThrowIfCancellationRequested();
                var overlap = mode == StageCorrelationMode.OverlapZncc;
                var first = overlap ? Math.Max(0, -lag) : 0; var end = overlap ? Math.Min(n, n - lag) : n;
                var count = end - first; if (count < 4 || count / (double)n < minimumOverlap) continue;
                var yf = overlap ? first + lag : 0; var ye = overlap ? end + lag : n;
                var ux = sx[end] - sx[first]; var uy = sy[ye] - sy[yf];
                var vx = ex[end] - ex[first] - ux * ux / count; var vy = ey[ye] - ey[yf] - uy * uy / count;
                if (vx <= Math.Max(1e-300, ex[n] * 1e-14) || vy <= Math.Max(1e-300, ey[n] * 1e-14)) continue;
                var covariance = y[(lag + length) % length].Real - ux * uy / count;
                b[(lag + n) % n] = new Complex(Math.Max(-1, Math.Min(1, covariance / Math.Sqrt(vx * vy))), 0);
            }
        }

        public static StageQualityPoint Evaluate(Complex[] correlation, int maxLag, int exclusion, DocumentStageSettings d)
        {
            var n = correlation.Length; var bound = maxLag == 0 ? n / 2 - 2 : Math.Min(maxLag, n / 2 - 2);
            var peak = 0; var best = double.NegativeInfinity;
            for (var lag = -bound; lag <= bound; lag++)
            {
                var v = correlation[(lag + n) % n].Real;
                if (ModeHopSettings.IsFinite(v) && v > best) { best = v; peak = lag; }
            }
            var result = new StageQualityPoint { CandidateLag = double.NaN, CandidateShiftPm = double.NaN, PeakZncc = double.NaN,
                SecondPeak = double.NaN, Separation = double.NaN, Status = "INSUFFICIENT_VARIANCE_OR_OVERLAP" };
            if (!ModeHopSettings.IsFinite(best)) return result;
            var second = double.NegativeInfinity;
            for (var lag = -bound; lag <= bound; lag++)
            {
                var v = correlation[(lag + n) % n].Real;
                if (Math.Abs(lag - peak) > exclusion && ModeHopSettings.IsFinite(v)) second = Math.Max(second, v);
            }
            double fractional = 0;
            if (Math.Abs(peak) < bound)
            {
                var left = correlation[(peak - 1 + n) % n].Real; var right = correlation[(peak + 1 + n) % n].Real;
                var denominator = left - 2 * best + right;
                if (ModeHopSettings.IsFinite(denominator) && Math.Abs(denominator) > 1e-15) fractional = Math.Max(-.5, Math.Min(.5, .5 * (left - right) / denominator));
            }
            result.CandidateLag = peak + fractional; result.PeakZncc = best;
            result.SecondPeak = ModeHopSettings.IsFinite(second) ? second : double.NaN;
            result.Separation = ModeHopSettings.IsFinite(second) ? best - second : double.NaN;
            result.Overlap = d.Correlation == StageCorrelationMode.OverlapZncc ? (n - Math.Abs(peak)) / (double)n : 1;
            result.Status = d.Correlation == StageCorrelationMode.Legacy ? "LEGACY_CORRELATION"
                : Math.Abs(peak) == bound ? "SEARCH_BOUNDARY" : best < d.MinimumZncc ? "LOW_CORRELATION"
                : !ModeHopSettings.IsFinite(second) ? "NO_SECOND_PEAK_SUPPORT" : result.Separation < d.MinimumSeparation ? "AMBIGUOUS_PEAK" : "VALID_RELATIVE";
            return result;
        }
    }
}
