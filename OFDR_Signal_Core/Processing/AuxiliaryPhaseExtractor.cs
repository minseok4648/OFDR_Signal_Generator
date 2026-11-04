using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Processing
{
    // Shared by production k-linearization and the auxiliary-only quality experiment.
    internal static class AuxiliaryPhaseExtractor
    {
        internal sealed class Result
        {
            public double[] Phase;
            public int[] Anchors;
            public Complex[] Analytic;
            public KLinearizationMetrics Metrics;
            public KLinearizationDiagnostics Diagnostics;
            public string Failure(double maximumRatio)
            {
                if (Metrics.NonmonotonicRatio > maximumRatio)
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture, "CH1 phase QC failed: nonmonotonic ratio {0:G6} exceeds {1:G6}.", Metrics.NonmonotonicRatio, maximumRatio);
                if (Metrics.AcceptedAnchorCount < 2 || Metrics.PhaseSpanRadians < 2 * Math.PI) return "CH1 phase span is too small for k-linearization.";
                return null;
            }
        }

        public static Result Extract(FftEngine fft, double[] reference, int maximumPreviewPoints, CancellationToken token, IProgress<OperationProgress> progress = null, bool keepAnalytic = false)
        {
            token.ThrowIfCancellationRequested();
            var n = reference.Length;
            progress?.Report(new OperationProgress { Stage = "k-linearization", Detail = "Build CH1 analytic signal", Percent = 0 });
            var mean = reference.Average();
            var diagnostics = maximumPreviewPoints > 0 ? new KLinearizationDiagnostics
            {
                AuxiliaryRaw = PreviewReducer.FromReal(reference, maximumPreviewPoints, "Sample index", "Voltage (V)"),
                AuxiliaryMeanRemoved = PreviewReducer.FromSelector(n, maximumPreviewPoints, i => reference[i] - mean, "Sample index", "Voltage (V)")
            } : null;
            var analytic = new Complex[n];
            for (var i = 0; i < n; i++) analytic[i] = new Complex(reference[i] - mean, 0);
            fft.ForwardInPlace(analytic);
            var half = n / 2;
            for (var i = 1; i < n; i++)
            {
                if (i < half + n % 2) analytic[i] *= 2;
                else if (i > half || n % 2 != 0) analytic[i] = Complex.Zero;
            }
            fft.InverseInPlace(analytic);
            if (diagnostics != null)
            {
                diagnostics.AnalyticMagnitude = PreviewReducer.FromMagnitude(analytic, maximumPreviewPoints, "Sample index", "Analytic magnitude");
                diagnostics.WrappedPhase = PreviewReducer.FromSelector(n, maximumPreviewPoints, i => Math.Atan2(analytic[i].Imaginary, analytic[i].Real), "Sample index", "Phase (rad)");
            }
            var phase = new double[n];
            var previous = Math.Atan2(analytic[0].Imaginary, analytic[0].Real);
            phase[0] = previous;
            var offset = 0.0;
            for (var i = 1; i < n; i++)
            {
                if ((i & 0x1ffff) == 0) token.ThrowIfCancellationRequested();
                var wrapped = Math.Atan2(analytic[i].Imaginary, analytic[i].Real);
                var delta = wrapped - previous;
                if (delta > Math.PI) offset -= 2 * Math.PI;
                else if (delta < -Math.PI) offset += 2 * Math.PI;
                phase[i] = wrapped + offset;
                previous = wrapped;
            }
            var direction = phase[n - 1] >= phase[0] ? 1 : -1;
            if (direction < 0) for (var i = 0; i < n; i++) phase[i] = -phase[i];
            if (diagnostics != null)
            {
                diagnostics.UnwrappedPhase = PreviewReducer.FromReal(phase, maximumPreviewPoints, "Sample index", "Phase (rad)");
                diagnostics.PhaseIncrement = PreviewReducer.FromSelector(n - 1, maximumPreviewPoints, i => phase[i + 1] - phase[i], "Sample index", "Delta phase (rad)");
            }
            var anchors = new int[n]; anchors[0] = 0;
            var accepted = 1; var rejected = 0; var last = phase[0];
            var deltas = new List<double>(Math.Min(n, 1048576));
            var step = Math.Max(1, n / 1048576);
            for (var i = 1; i < n; i++)
            {
                if ((i & 0x1ffff) == 0) token.ThrowIfCancellationRequested();
                var delta = phase[i] - last;
                if (delta > 0) { anchors[accepted++] = i; last = phase[i]; if (i % step == 0) deltas.Add(delta); }
                else rejected++;
            }
            deltas.Sort();
            return new Result { Phase = phase, Anchors = anchors, Analytic = keepAnalytic ? analytic : null, Diagnostics = diagnostics, Metrics = new KLinearizationMetrics
            {
                SampleCount = n, SweepDirection = direction, AcceptedAnchorCount = accepted,
                NonmonotonicRatio = rejected / (double)(n - 1), MedianPhaseIncrementRadians = deltas.Count == 0 ? 0 : deltas[deltas.Count / 2],
                PhaseSpanRadians = phase[anchors[accepted - 1]] - phase[anchors[0]]
            } };
        }
    }
}
