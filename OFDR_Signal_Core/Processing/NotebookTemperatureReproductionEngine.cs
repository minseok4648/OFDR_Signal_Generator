using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Processing
{
    // Implements 30-CSV-TO_OFDR.ipynb cells 28, 35-37, including its independent
    // endpoint spans, global Hann and unnormalized circular magnitude correlation.
    // Do not substitute this historical diagnostic for IndependentOfdrProcessingEngine.
    public sealed class NotebookTemperatureReproductionEngine
    {
        private readonly FftEngine fft = new FftEngine();
        public string ProviderDescription => fft.ProviderDescription;

        public void RequireNativeProvider()
        {
            if (!Environment.Is64BitProcess || !fft.NativeProviderAvailable)
                throw new InvalidOperationException("Notebook full-record reproduction requires x64 and native MKL FFT. " + fft.NativeProviderFailure);
        }

        public NotebookTemperatureSweep Prepare(ModeHopRecording recording, NotebookTemperatureConfig config, CancellationToken token)
        {
            config.Validate();
            RequireNativeProvider();
            token.ThrowIfCancellationRequested();
            if (recording == null || !recording.HasFourChannels || recording.IsPartialRead)
                throw new ArgumentException("A complete CH1/CH2/CH3/CH4 recording is required.");
            var trigger = Array.FindIndex(recording.SweepTrigger, x => x > config.TriggerVoltage);
            if (trigger < 0) throw new ArgumentException("CH1 start trigger was not found.");
            var start = checked(trigger + config.StartOffsetSamples);
            if ((long)start + config.InputSamples > recording.SampleCount)
                throw new ArgumentException("The complete configured crop is not available; truncation is not allowed.");
            var length = config.InputSamples - config.AuxiliarySliceStart - config.MeasurementSliceStart;
            var aux = new double[length];
            var signal = new double[length];
            for (var i = 0; i < length; i++)
            {
                if ((i & 0x3ffff) == 0) token.ThrowIfCancellationRequested();
                aux[i] = recording.Auxiliary[start + config.AuxiliarySliceStart + i];
                signal[i] = recording.Measurement[start + config.MeasurementSliceStart + i];
                if (!ModeHopSettings.IsFinite(aux[i]) || !ModeHopSettings.IsFinite(signal[i]))
                    throw new ArgumentException("Nonfinite CH3/CH4 in the notebook crop; raw samples cannot be silently removed.");
            }
            double first, last, ratio;
            var values = LinearizeLikeNotebook(aux, signal, config.ResampledSamples, token, out first, out last, out ratio);
            return new NotebookTemperatureSweep { Values = values, Spectrum = BuildSpectrum(values), Metrics = new NotebookSweepMetrics
            {
                SourceSamples = recording.SampleCount, SampleIntervalSeconds = recording.SampleIntervalSeconds,
                TriggerIndex = trigger, CropStartIndex = start, UsedSamples = length,
                FirstPhaseRadians = first, LastPhaseRadians = last, NonmonotonicRatio = ratio
            } };
        }

        public double[] LinearizeLikeNotebook(double[] auxiliary, double[] measurement, int outputSamples,
            CancellationToken token, out double first, out double last, out double nonmonotonicRatio, KLinearizationDiagnostics diagnostics = null, int previewPoints = 10000)
        {
            if (auxiliary == null || measurement == null || auxiliary.Length != measurement.Length || auxiliary.Length < 4 || outputSamples < 2)
                throw new ArgumentException("Matching auxiliary/measurement arrays are required.");
            var n = auxiliary.Length;
            var analytic = new Complex[n];
            for (var i = 0; i < n; i++)
            {
                if (!ModeHopSettings.IsFinite(auxiliary[i]) || !ModeHopSettings.IsFinite(measurement[i]))
                    throw new ArgumentException("Nonfinite input to notebook interpolation.");
                analytic[i] = new Complex(auxiliary[i], 0); // No DC subtraction: matches scipy.signal.hilbert(aux).
            }
            fft.ForwardInPlace(analytic);
            for (var i = 1; i < n; i++)
            {
                if (i < (n + 1) / 2) analytic[i] *= 2;
                else if (i > n / 2 || n % 2 != 0) analytic[i] = Complex.Zero;
            }
            fft.InverseInPlace(analytic);
            var phase = new double[n];
            var order = new int[n];
            var previous = Math.Atan2(analytic[0].Imaginary, analytic[0].Real);
            phase[0] = previous;
            var offset = 0.0;
            var reversed = 0;
            for (var i = 1; i < n; i++)
            {
                if ((i & 0x3ffff) == 0) token.ThrowIfCancellationRequested();
                var wrapped = Math.Atan2(analytic[i].Imaginary, analytic[i].Real);
                var delta = wrapped - previous;
                if (delta > Math.PI) offset -= 2 * Math.PI;
                else if (delta < -Math.PI) offset += 2 * Math.PI;
                phase[i] = wrapped + offset;
                if (phase[i] <= phase[i - 1]) reversed++;
                previous = wrapped;
                order[i] = i;
            }
            first = phase[0]; last = phase[n - 1];
            nonmonotonicRatio = reversed / (double)(n - 1);
            if (diagnostics != null)
            {
                diagnostics.AuxiliaryRaw = PreviewReducer.FromReal(auxiliary, previewPoints, "Selected sample", "V");
                diagnostics.AnalyticMagnitude = PreviewReducer.FromMagnitude(analytic, previewPoints, "Selected sample", "Magnitude");
                diagnostics.WrappedPhase = PreviewReducer.FromSelector(n, previewPoints, i => Math.Atan2(analytic[i].Imaginary, analytic[i].Real), "Selected sample", "rad");
                diagnostics.UnwrappedPhase = PreviewReducer.FromReal(phase, previewPoints, "Selected sample", "rad");
                diagnostics.PhaseIncrement = PreviewReducer.FromSelector(n - 1, previewPoints, i => phase[i + 1] - phase[i], "Selected sample", "rad/sample");
                var gridFirst = first; var span = last - first;
                diagnostics.UniformPhaseGrid = PreviewReducer.FromSelector(outputSamples, previewPoints, i => gridFirst + i * span / (outputSamples - 1), "Resampled index", "rad");
            }
            // scipy interp1d defaults to stable sorting, including nonmonotonic regions.
            // The original endpoint order defines linspace even after the sort.
            Array.Sort(order, Comparer<int>.Create((a, b) => { var cmp = phase[a].CompareTo(phase[b]); return cmp != 0 ? cmp : a.CompareTo(b); }));
            token.ThrowIfCancellationRequested();
            var values = new double[outputSamples];
            for (var i = 0; i < outputSamples; i++)
            {
                var target = i == outputSamples - 1 ? last : first + i * ((last - first) / (outputSamples - 1));
                // numpy.interp uses the rightmost equal point (SciPy's real 1-D fast path).
                var low = 0; var high = n;
                while (low < high) { var mid = low + (high - low) / 2; if (phase[order[mid]] <= target) low = mid + 1; else high = mid; }
                var left = Math.Max(0, Math.Min(n - 2, low - 1));
                var a = order[left]; var b = order[left + 1];
                values[i] = target == phase[b] ? measurement[b] : measurement[a] +
                    (target - phase[a]) * ((measurement[b] - measurement[a]) / (phase[b] - phase[a]));
            }
            return values;
        }

        public Complex[] BuildSpectrum(double[] values)
        {
            double mean = 0;
            foreach (var value in values) mean += value;
            mean /= values.Length;
            var result = new Complex[values.Length];
            for (var i = 0; i < values.Length; i++) result[i] = new Complex((values[i] - mean) * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (values.Length - 1))), 0);
            fft.ForwardInPlace(result);
            Array.Resize(ref result, values.Length / 2);
            return result;
        }

        public NotebookTemperatureProfile Compare(Complex[] reference, Complex[] signal, NotebookTemperatureConfig config,
            CancellationToken token, Action<int, int> progress = null)
        {
            config.Validate();
            var count = (config.StopBin - config.FirstBin - 1) / config.SegmentStep + 1;
            var result = new NotebookTemperatureProfile { Positions = new int[count], Shifts = new double[count] };
            var n = checked(config.SegmentSize * config.ZeroPadFactor);
            // Reuse buffers: do not accumulate complete segment/local/correlation arrays.
            var a = new Complex[n]; var b = new Complex[n];
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                var position = config.FirstBin + i * config.SegmentStep;
                result.Positions[i] = position; result.Shifts[i] = double.NaN;
                var start = position - config.SegmentSize / 2;
                if (start < 0 || start + config.SegmentSize > Math.Min(reference.Length, signal.Length)) continue;
                Array.Clear(a, 0, n); Array.Clear(b, 0, n);
                Array.Copy(reference, start, a, 0, config.SegmentSize);
                Array.Copy(signal, start, b, 0, config.SegmentSize);
                fft.InverseInPlace(a); fft.InverseInPlace(b);
                for (var j = 0; j < n; j++) { a[j] = new Complex(a[j].Magnitude, 0); b[j] = new Complex(b[j].Magnitude, 0); }
                fft.ForwardInPlace(a); fft.ForwardInPlace(b);
                for (var j = 0; j < n; j++) b[j] *= Complex.Conjugate(a[j]);
                fft.InverseInPlace(b);
                result.Shifts[i] = FindShift(b);
                if (i % 500 == 0) progress?.Invoke(i, count);
            }
            return result;
        }

        public static double FindShift(Complex[] correlation)
        {
            if (correlation == null || correlation.Length < 3) throw new ArgumentException("Correlation requires at least three points.");
            var n = correlation.Length; var middle = n / 2; var rotation = (n + 1) / 2;
            var peak = 0; var maximum = double.NegativeInfinity;
            for (var j = 0; j < n; j++)
            {
                var value = correlation[(j + rotation) % n].Real;
                if (value > maximum) { maximum = value; peak = j; }
            }
            double shift = peak - middle;
            if (peak > 0 && peak < n - 1)
            {
                var y1 = correlation[(peak - 1 + rotation) % n].Real;
                var y2 = correlation[(peak + rotation) % n].Real;
                var y3 = correlation[(peak + 1 + rotation) % n].Real;
                var denominator = y1 - 2 * y2 + y3;
                if (Math.Abs(denominator) > 1e-15) shift += 0.5 * (y1 - y3) / denominator;
            }
            return shift;
        }
    }
}
