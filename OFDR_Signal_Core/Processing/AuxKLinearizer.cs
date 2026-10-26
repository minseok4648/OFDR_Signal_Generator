using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Processing
{
    public sealed class AuxKLinearizer
    {
        private readonly FftEngine fft;

        public AuxKLinearizer(FftEngine fft)
        {
            this.fft = fft ?? throw new ArgumentNullException(nameof(fft));
        }

        public KLinearizationResult Linearize(double[] reference, double[] xPolarization, double[] yPolarization, double maximumNonmonotonicRatio, CancellationToken cancellationToken, IProgress<OperationProgress> progress = null)
        {
            return LinearizeDetailed(reference, xPolarization, yPolarization, maximumNonmonotonicRatio, 0, cancellationToken, progress).Linearized;
        }

        public DetailedKLinearizationResult LinearizeDetailed(double[] reference, double[] xPolarization, double[] yPolarization, double maximumNonmonotonicRatio, int maximumPreviewPoints, CancellationToken cancellationToken, IProgress<OperationProgress> progress = null)
        {
            if (reference == null || xPolarization == null || yPolarization == null) throw new ArgumentNullException("All three channels are required.");
            if (reference.Length < 4 || xPolarization.Length != reference.Length || yPolarization.Length != reference.Length) throw new ArgumentException("Channel lengths must match and contain at least four samples.");
            var n = reference.Length;
            progress?.Report(new OperationProgress { Stage = "k-linearization", Detail = "Build CH1 analytic signal", Percent = 0.0 });
            var mean = reference.Average();
            var diagnostics = maximumPreviewPoints > 0
                ? new KLinearizationDiagnostics
                {
                    AuxiliaryRaw = PreviewReducer.FromReal(reference, maximumPreviewPoints, "Sample index", "Voltage (V)"),
                    AuxiliaryMeanRemoved = PreviewReducer.FromSelector(n, maximumPreviewPoints, index => reference[index] - mean, "Sample index", "Voltage (V)")
                }
                : null;
            var analytic = new Complex[n];
            for (var i = 0; i < n; i++) analytic[i] = new Complex(reference[i] - mean, 0.0);
            fft.ForwardInPlace(analytic);
            var half = n / 2;
            for (var i = 1; i < n; i++)
            {
                if (i < half + (n % 2)) analytic[i] *= 2.0;
                else if (i > half || (n % 2) != 0) analytic[i] = Complex.Zero;
            }
            fft.InverseInPlace(analytic);

            if (diagnostics != null)
            {
                diagnostics.AnalyticMagnitude = PreviewReducer.FromMagnitude(analytic, maximumPreviewPoints, "Sample index", "Analytic magnitude");
                diagnostics.WrappedPhase = PreviewReducer.FromSelector(n, maximumPreviewPoints, index => Math.Atan2(analytic[index].Imaginary, analytic[index].Real), "Sample index", "Phase (rad)");
            }

            var phase = new double[n];
            var previousWrapped = Math.Atan2(analytic[0].Imaginary, analytic[0].Real);
            phase[0] = previousWrapped;
            var offset = 0.0;
            for (var i = 1; i < n; i++)
            {
                if ((i & 0x1ffff) == 0) cancellationToken.ThrowIfCancellationRequested();
                var wrapped = Math.Atan2(analytic[i].Imaginary, analytic[i].Real);
                var delta = wrapped - previousWrapped;
                if (delta > Math.PI) offset -= 2.0 * Math.PI;
                else if (delta < -Math.PI) offset += 2.0 * Math.PI;
                phase[i] = wrapped + offset;
                previousWrapped = wrapped;
            }
            analytic = null;

            var direction = phase[n - 1] >= phase[0] ? 1 : -1;
            if (direction < 0) for (var i = 0; i < n; i++) phase[i] = -phase[i];
            if (diagnostics != null)
            {
                diagnostics.UnwrappedPhase = PreviewReducer.FromReal(phase, maximumPreviewPoints, "Sample index", "Phase (rad)");
                diagnostics.PhaseIncrement = PreviewReducer.FromSelector(n - 1, maximumPreviewPoints, index => phase[index + 1] - phase[index], "Sample index", "Delta phase (rad)");
            }
            var anchors = new int[n];
            anchors[0] = 0;
            var accepted = 1;
            var nonmonotonic = 0;
            var lastPhase = phase[0];
            var positiveDeltas = new List<double>(Math.Min(n, 1048576));
            var sampleStep = Math.Max(1, n / 1048576);
            for (var i = 1; i < n; i++)
            {
                var delta = phase[i] - lastPhase;
                if (delta > 0.0)
                {
                    anchors[accepted++] = i;
                    lastPhase = phase[i];
                    if (i % sampleStep == 0) positiveDeltas.Add(delta);
                }
                else nonmonotonic++;
            }
            var ratio = nonmonotonic / (double)(n - 1);
            if (ratio > maximumNonmonotonicRatio)
                throw new InvalidOperationException(string.Format(System.Globalization.CultureInfo.InvariantCulture, "CH1 phase QC failed: nonmonotonic ratio {0:G6} exceeds {1:G6}.", ratio, maximumNonmonotonicRatio));
            if (accepted < 2 || phase[anchors[accepted - 1]] - phase[anchors[0]] < 2.0 * Math.PI) throw new InvalidOperationException("CH1 phase span is too small for k-linearization.");

            positiveDeltas.Sort();
            var medianDelta = positiveDeltas.Count == 0 ? 0.0 : positiveDeltas[positiveDeltas.Count / 2];
            var outputReference = new double[n];
            var outputX = new double[n];
            var outputY = new double[n];
            var firstPhase = phase[anchors[0]];
            var phaseSpan = phase[anchors[accepted - 1]] - firstPhase;
            if (diagnostics != null)
                diagnostics.UniformPhaseGrid = PreviewReducer.FromSelector(n, maximumPreviewPoints, index => firstPhase + (phaseSpan * index / (n - 1.0)), "Normalized-k sample", "Uniform phase (rad)");
            var anchorCursor = 0;
            for (var outputIndex = 0; outputIndex < n; outputIndex++)
            {
                if ((outputIndex & 0x1ffff) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new OperationProgress { Stage = "k-linearization", Detail = "Interpolate uniform phase", Completed = outputIndex, Total = n, Percent = 50.0 + (50.0 * outputIndex / n) });
                }
                var target = firstPhase + (phaseSpan * outputIndex / (n - 1.0));
                while (anchorCursor + 1 < accepted && phase[anchors[anchorCursor + 1]] < target) anchorCursor++;
                var left = anchors[Math.Min(anchorCursor, accepted - 2)];
                var right = anchors[Math.Min(anchorCursor + 1, accepted - 1)];
                var denominator = phase[right] - phase[left];
                var fraction = denominator <= 0.0 ? 0.0 : (target - phase[left]) / denominator;
                outputReference[outputIndex] = Lerp(reference[left], reference[right], fraction);
                outputX[outputIndex] = Lerp(xPolarization[left], xPolarization[right], fraction);
                outputY[outputIndex] = Lerp(yPolarization[left], yPolarization[right], fraction);
            }
            progress?.Report(new OperationProgress { Stage = "k-linearization", Detail = "Complete", Completed = n, Total = n, Percent = 100.0 });
            return new DetailedKLinearizationResult
            {
                Diagnostics = diagnostics,
                Linearized = new KLinearizationResult
                {
                    Reference = outputReference,
                    XPolarization = outputX,
                    YPolarization = outputY,
                    Metrics = new KLinearizationMetrics
                    {
                        PhaseSpanRadians = phaseSpan,
                        MedianPhaseIncrementRadians = medianDelta,
                        NonmonotonicRatio = ratio,
                        AcceptedAnchorCount = accepted,
                        SampleCount = n,
                        SweepDirection = direction
                    }
                }
            };
        }

        public DualSweepKLinearizationResult LinearizeSweepsDetailed(
            double[] referenceAuxiliary,
            double[] referenceSignal,
            double[] signalAuxiliary,
            double[] signal,
            double maximumNonmonotonicRatio,
            int maximumPreviewPoints,
            CancellationToken cancellationToken,
            IProgress<OperationProgress> progress = null)
        {
            if (referenceAuxiliary == null || referenceSignal == null || signalAuxiliary == null || signal == null)
                throw new ArgumentNullException("기준/측정 sweep의 CH1과 편광 신호가 모두 필요합니다.");
            if (referenceAuxiliary.Length != referenceSignal.Length || signalAuxiliary.Length != signal.Length
                || referenceSignal.Length != signal.Length || referenceSignal.Length < 4)
                throw new ArgumentException("기준/측정 sweep의 채널 길이는 모두 같아야 합니다.");

            var referenceDetailed = LinearizeDetailed(
                referenceAuxiliary,
                referenceSignal,
                referenceSignal,
                maximumNonmonotonicRatio,
                maximumPreviewPoints,
                cancellationToken,
                ScaleProgress(progress, "기준 sweep CH1", 0.0, 50.0));
            var referenceValues = referenceDetailed.Linearized.XPolarization;
            referenceDetailed.Linearized.Reference = null;
            referenceDetailed.Linearized.YPolarization = null;

            var signalDetailed = LinearizeDetailed(
                signalAuxiliary,
                signal,
                signal,
                maximumNonmonotonicRatio,
                maximumPreviewPoints,
                cancellationToken,
                ScaleProgress(progress, "측정 sweep CH1", 50.0, 100.0));
            var signalValues = signalDetailed.Linearized.XPolarization;
            signalDetailed.Linearized.Reference = null;
            signalDetailed.Linearized.YPolarization = null;

            var referenceSpan = referenceDetailed.Linearized.Metrics.PhaseSpanRadians;
            var signalSpan = signalDetailed.Linearized.Metrics.PhaseSpanRadians;
            var commonSpan = Math.Min(referenceSpan, signalSpan);
            referenceValues = ResampleUniformSpan(referenceValues, referenceSpan, commonSpan, cancellationToken);
            signalValues = ResampleUniformSpan(signalValues, signalSpan, commonSpan, cancellationToken);
            SetUniformGrid(referenceDetailed.Diagnostics, commonSpan, referenceValues.Length, maximumPreviewPoints);
            SetUniformGrid(signalDetailed.Diagnostics, commonSpan, signalValues.Length, maximumPreviewPoints);

            progress?.Report(new OperationProgress { Stage = "k-linearization", Detail = "두 sweep 공통 phase grid 완료", Completed = signalValues.Length, Total = signalValues.Length, Percent = 100.0 });
            return new DualSweepKLinearizationResult
            {
                Reference = referenceValues,
                Signal = signalValues,
                ReferenceMetrics = referenceDetailed.Linearized.Metrics,
                SignalMetrics = signalDetailed.Linearized.Metrics,
                ReferenceDiagnostics = referenceDetailed.Diagnostics,
                SignalDiagnostics = signalDetailed.Diagnostics,
                CommonPhaseSpanRadians = commonSpan
            };
        }

        private static double[] ResampleUniformSpan(double[] values, double sourceSpan, double targetSpan, CancellationToken token)
        {
            if (Math.Abs(sourceSpan - targetSpan) <= Math.Max(1e-12, sourceSpan * 1e-12)) return values;
            var output = new double[values.Length];
            var maximumSourceIndex = values.Length - 1.0;
            for (var i = 0; i < output.Length; i++)
            {
                if ((i & 0x1ffff) == 0) token.ThrowIfCancellationRequested();
                var sourceIndex = maximumSourceIndex * targetSpan * i / Math.Max(1.0, sourceSpan * (output.Length - 1.0));
                var left = Math.Max(0, Math.Min(values.Length - 1, (int)Math.Floor(sourceIndex)));
                var right = Math.Min(values.Length - 1, left + 1);
                output[i] = Lerp(values[left], values[right], sourceIndex - left);
            }
            return output;
        }

        private static void SetUniformGrid(KLinearizationDiagnostics diagnostics, double phaseSpan, int length, int maximumPreviewPoints)
        {
            if (diagnostics == null || maximumPreviewPoints <= 0) return;
            diagnostics.UniformPhaseGrid = PreviewReducer.FromSelector(length, maximumPreviewPoints,
                index => phaseSpan * index / Math.Max(1.0, length - 1.0), "Normalized-k sample", "Common relative phase (rad)");
        }

        private static IProgress<OperationProgress> ScaleProgress(IProgress<OperationProgress> target, string detailPrefix, double start, double end)
        {
            if (target == null) return null;
            return new InlineProgress(value => target.Report(new OperationProgress
            {
                Stage = "k-linearization",
                Detail = detailPrefix + " · " + (value == null ? string.Empty : value.Detail),
                Completed = value == null ? 0 : value.Completed,
                Total = value == null ? 0 : value.Total,
                Percent = start + ((end - start) * (value == null ? 0.0 : value.Percent) / 100.0)
            }));
        }

        private sealed class InlineProgress : IProgress<OperationProgress>
        {
            private readonly Action<OperationProgress> report;
            public InlineProgress(Action<OperationProgress> report) { this.report = report; }
            public void Report(OperationProgress value) { report(value); }
        }

        private static double Lerp(double left, double right, double fraction)
        {
            if (fraction < 0.0) fraction = 0.0;
            if (fraction > 1.0) fraction = 1.0;
            return left + ((right - left) * fraction);
        }
    }
}
