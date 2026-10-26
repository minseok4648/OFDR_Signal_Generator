using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Services;

namespace OFDR_Signal_Core.Processing
{
    public sealed class OfdrProcessingEngine
    {
        public const string MetricAbsolutePeakOffset = "Absolute Peak Offset";
        public const string MetricSignedPeakOffset = "Signed Peak Offset";
        public const string MetricPeakAmplitude = "Peak Amplitude";
        public const string MetricPeakCenterDelta = "Peak-Center Delta";

        private readonly IsfWaveformReader reader;
        private readonly FftEngine fft;
        private readonly AuxKLinearizer linearizer;

        public OfdrProcessingEngine(IsfWaveformReader reader = null, FftEngine fft = null)
        {
            this.reader = reader ?? new IsfWaveformReader();
            this.fft = fft ?? new FftEngine();
            linearizer = new AuxKLinearizer(this.fft);
        }

        public string FftProviderDescription { get { return fft.ProviderDescription; } }
        public bool NativeFftAvailable { get { return fft.NativeProviderAvailable; } }

        public OfdrSetProcessingResult RunSet(OfdrChannelSet set, OfdrProcessingConfig config, CancellationToken cancellationToken, IProgress<OperationProgress> progress = null)
        {
            if (set == null) throw new ArgumentNullException(nameof(set));
            if (!set.IsValid) throw new InvalidOperationException("Cannot process invalid set " + set.SetId + ".");
            config = config ?? new OfdrProcessingConfig();
            ValidateConfig(config);
            var total = Stopwatch.StartNew();
            var performance = new List<NamedMetric>();
            var stage = Stopwatch.StartNew();

            var ch1 = reader.ReadAllVoltages(set.Channel1Path, cancellationToken, ScaleProgress(progress, "Read CH1", 0.0, 10.0));
            var ch2 = reader.ReadAllVoltages(set.Channel2Path, cancellationToken, ScaleProgress(progress, "Read CH2", 10.0, 20.0));
            var ch3 = reader.ReadAllVoltages(set.Channel3Path, cancellationToken, ScaleProgress(progress, "Read CH3", 20.0, 30.0));
            performance.Add(Metric("read_channels_sec", stage.Elapsed.TotalSeconds, "s"));

            return RunChannels(set.SetId, ch1, ch2, ch3, config, cancellationToken, progress, performance, 30.0, total);
        }

        public OfdrSetProcessingResult RunConvertedCsv(ConvertedOfdrCsvData data, OfdrProcessingConfig config, CancellationToken cancellationToken, IProgress<OperationProgress> progress = null)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Reference == null || data.XPolarization == null || data.YPolarization == null) throw new ArgumentNullException("CSV channels are required.");
            if (data.Reference.Length == 0 || data.XPolarization.Length != data.Reference.Length || data.YPolarization.Length != data.Reference.Length)
                throw new ArgumentException("CSV channel lengths must match and contain at least one sample.");
            config = config ?? new OfdrProcessingConfig();
            ValidateConfig(config);
            var performance = new List<NamedMetric>
            {
                Metric("csv_rows", data.RowCount, "count"),
                Metric("csv_sample_interval_sec", data.SampleIntervalSeconds, "s")
            };
            return RunChannels(data.SetId, data.Reference, data.XPolarization, data.YPolarization, config, cancellationToken, progress, performance, 0.0, Stopwatch.StartNew());
        }

        private OfdrSetProcessingResult RunChannels(string setId, double[] ch1, double[] ch2, double[] ch3, OfdrProcessingConfig config, CancellationToken cancellationToken, IProgress<OperationProgress> progress, List<NamedMetric> performance, double progressOffset, Stopwatch total)
        {
            var stage = Stopwatch.StartNew();
            var kStart = progressOffset;
            var kEnd = progressOffset <= 0.0 ? 24.0 : 48.0;
            var refPoint = progressOffset <= 0.0 ? 25.0 : 49.0;
            var xStart = progressOffset <= 0.0 ? 26.0 : 50.0;
            var xEnd = progressOffset <= 0.0 ? 62.0 : 74.0;
            var yStart = progressOffset <= 0.0 ? 62.0 : 74.0;
            var yEnd = 98.0;

            stage.Restart();
            KLinearizationResult linearized;
            if (config.EnableKLinearization)
                linearized = linearizer.Linearize(ch1, ch2, ch3, config.MaxAuxNonmonotonicRatio, cancellationToken, ScaleProgress(progress, "k-linearization", kStart, kEnd));
            else
                linearized = new KLinearizationResult
                {
                    Reference = ch1,
                    XPolarization = ch2,
                    YPolarization = ch3,
                    Metrics = new KLinearizationMetrics { AcceptedAnchorCount = ch1.Length, SampleCount = ch1.Length, SweepDirection = 1 }
                };
            ch1 = null; ch2 = null; ch3 = null;
            performance.Add(Metric("k_linearization_sec", stage.Elapsed.TotalSeconds, "s"));

            stage.Restart();
            progress?.Report(new OperationProgress { Stage = "Reference FFT", Detail = setId, Percent = refPoint });
            var referenceSpectrum = BuildSpectrum(linearized.Reference, config.UseHannWindow, cancellationToken);
            var referencePreview = PreviewReducer.FromReal(linearized.Reference, config.MaxPreviewPoints, "Normalized k sample", "Voltage (V)");
            performance.Add(Metric("reference_fft_sec", stage.Elapsed.TotalSeconds, "s"));

            stage.Restart();
            var xResult = ProcessPolarization("Xpol", linearized.XPolarization, referenceSpectrum, config, cancellationToken, ScaleProgress(progress, "Process Xpol", xStart, xEnd));
            performance.Add(Metric("xpol_processing_sec", stage.Elapsed.TotalSeconds, "s"));
            linearized.XPolarization = null;

            stage.Restart();
            var yResult = ProcessPolarization("Ypol", linearized.YPolarization, referenceSpectrum, config, cancellationToken, ScaleProgress(progress, "Process Ypol", yStart, yEnd));
            performance.Add(Metric("ypol_processing_sec", stage.Elapsed.TotalSeconds, "s"));
            linearized.YPolarization = null;
            linearized.Reference = null;

            total.Stop();
            performance.Add(Metric("total_processing_sec", total.Elapsed.TotalSeconds, "s"));
            performance.Add(Metric("peak_working_set_bytes", Process.GetCurrentProcess().PeakWorkingSet64, "bytes"));
            progress?.Report(new OperationProgress { Stage = "Complete", Detail = setId, Percent = 100.0 });
            return new OfdrSetProcessingResult
            {
                SetId = setId,
                KLinearization = linearized.Metrics,
                ReferencePreview = referencePreview,
                ReferenceSpectrumPreview = PreviewReducer.FromMagnitude(referenceSpectrum, config.MaxPreviewPoints, "FFT bin", "Magnitude"),
                XPolarization = xResult,
                YPolarization = yResult,
                ReferenceSpectrum = referenceSpectrum,
                Config = config,
                FftProvider = fft.ProviderDescription,
                PerformanceMetrics = performance
            };
        }

        public SegmentInspection InspectSegment(OfdrSetProcessingResult result, string polarization, int segmentIndex)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            var selected = string.Equals(polarization, "Ypol", StringComparison.OrdinalIgnoreCase) ? result.YPolarization : result.XPolarization;
            if (selected == null || selected.Spectrum == null || result.ReferenceSpectrum == null) throw new InvalidOperationException("Processing spectra are no longer available.");
            if (segmentIndex < 0 || segmentIndex >= selected.SegmentCount) throw new ArgumentOutOfRangeException(nameof(segmentIndex));
            var start = selected.SegmentStartIndices[segmentIndex];
            var size = result.Config.SegmentSize;
            var raw = new Complex[size];
            var reference = new Complex[size];
            Array.Copy(selected.Spectrum, start, raw, 0, size);
            Array.Copy(result.ReferenceSpectrum, start, reference, 0, size);
            var conjugate = BuildConjugateMultiply(raw, reference, result.Config);
            var shifted = BuildShiftedIfft(conjugate, selected.SegmentIfftLength);
            return new SegmentInspection
            {
                SegmentIndex = segmentIndex,
                StartIndex = start,
                CenterIndex = selected.SegmentCenterIndices[segmentIndex],
                RawSegment = raw,
                ReferenceSegment = reference,
                ConjugateMultiply = conjugate,
                ShiftedIfft = shifted,
                PeakIndex = FindPeakIndex(shifted),
                ZeroLagIndex = shifted.Length / 2
            };
        }

        private PolarizationProcessingResult ProcessPolarization(string name, double[] signal, Complex[] referenceSpectrum, OfdrProcessingConfig config, CancellationToken token, IProgress<OperationProgress> progress)
        {
            progress?.Report(new OperationProgress { Stage = name, Detail = "FFT", Percent = 0.0 });
            var spectrum = BuildSpectrum(signal, config.UseHannWindow, token);
            var starts = BuildSegmentStarts(spectrum.Length, config.SegmentSize, config.SegmentHop);
            var centers = starts.Select(value => value + (config.SegmentSize / 2)).ToArray();
            var peaks = new int[starts.Length];
            var representatives = new double[starts.Length];
            var ifftLength = Radix2Fft.NextPowerOfTwo(config.SegmentSize * config.SegmentIfftZeroPadFactor);
            var completed = 0;
            var lastReported = 0;
            var progressSync = new object();
            var options = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
            Parallel.For(0, starts.Length, options, segmentIndex =>
            {
                var conjugate = new Complex[config.SegmentSize];
                var start = starts[segmentIndex];
                for (var i = 0; i < conjugate.Length; i++)
                {
                    var rawValue = spectrum[start + i];
                    var referenceValue = referenceSpectrum[start + i];
                    var value = rawValue * Complex.Conjugate(referenceValue) * config.ConjugateMultiplyScale;
                    if (config.NormalizeConjugateMultiply)
                    {
                        var denominator = rawValue.Magnitude * referenceValue.Magnitude;
                        if (denominator > 1e-12) value /= denominator;
                    }
                    conjugate[i] = value;
                }
                var shifted = BuildShiftedIfft(conjugate, ifftLength);
                var peak = FindPeakIndex(shifted);
                peaks[segmentIndex] = peak;
                representatives[segmentIndex] = BuildRepresentativeValue(config.FinalMetricMode, shifted, peak, shifted.Length / 2);
                var now = Interlocked.Increment(ref completed);
                if (now == starts.Length || now % 1024 == 0)
                {
                    lock (progressSync)
                    {
                        if (now > lastReported)
                        {
                            lastReported = now;
                            progress?.Report(new OperationProgress { Stage = name, Detail = "Segment IFFT", Completed = now, Total = starts.Length, Percent = 10.0 + (75.0 * now / starts.Length) });
                        }
                    }
                }
            });

            var finalProfile = BuildRepresentativeProfile(representatives, centers, spectrum.Length);
            SmoothInPlace(finalProfile, config.FinalSmoothingWindow);
            var detected = DetectEvents(finalProfile, config.PeakThresholdRatio);
            var metrics = BuildProfileMetrics(finalProfile, starts.Length);
            var finalProfilePreview = PreviewReducer.FromReal(finalProfile, config.MaxPreviewPoints, "FFT bin / segment position", FinalMetricYAxis(config.FinalMetricMode));
            progress?.Report(new OperationProgress { Stage = name, Detail = "Complete", Completed = starts.Length, Total = starts.Length, Percent = 100.0 });
            return new PolarizationProcessingResult
            {
                Polarization = name,
                SampleCount = signal.Length,
                SegmentCount = starts.Length,
                SegmentIfftLength = ifftLength,
                SegmentStartIndices = starts,
                SegmentCenterIndices = centers,
                PeakIndices = peaks,
                RepresentativeValues = representatives,
                FinalProfile = null,
                SignalPreview = PreviewReducer.FromReal(signal, config.MaxPreviewPoints, "Normalized k sample", "Voltage (V)"),
                SpectrumPreview = PreviewReducer.FromMagnitude(spectrum, config.MaxPreviewPoints, "FFT bin", "Magnitude"),
                FinalProfilePreview = finalProfilePreview,
                DetectedFeatures = detected,
                Metrics = metrics,
                Spectrum = spectrum
            };
        }

        private Complex[] BuildSpectrum(double[] signal, bool useHannWindow, CancellationToken token)
        {
            var spectrum = new Complex[signal.Length];
            var denominator = Math.Max(1, signal.Length - 1);
            for (var i = 0; i < signal.Length; i++)
            {
                if ((i & 0x3ffff) == 0) token.ThrowIfCancellationRequested();
                var value = signal[i];
                if (useHannWindow) value *= 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / denominator));
                spectrum[i] = new Complex(value, 0.0);
            }
            fft.ForwardInPlace(spectrum);
            return spectrum;
        }

        private static int[] BuildSegmentStarts(int length, int size, int hop)
        {
            var last = Math.Max(0, length - size);
            var starts = new List<int>();
            for (var start = 0; start <= last; start += hop) starts.Add(start);
            if (starts.Count == 0 || starts[starts.Count - 1] != last) starts.Add(last);
            return starts.ToArray();
        }

        private static Complex[] BuildConjugateMultiply(Complex[] raw, Complex[] reference, OfdrProcessingConfig config)
        {
            var length = Math.Min(raw.Length, reference.Length);
            var output = new Complex[length];
            for (var i = 0; i < length; i++)
            {
                var value = raw[i] * Complex.Conjugate(reference[i]) * config.ConjugateMultiplyScale;
                if (config.NormalizeConjugateMultiply)
                {
                    var denominator = raw[i].Magnitude * reference[i].Magnitude;
                    if (denominator > 1e-12) value /= denominator;
                }
                output[i] = value;
            }
            return output;
        }

        private static Complex[] BuildShiftedIfft(Complex[] input, int paddedLength)
        {
            var buffer = new Complex[paddedLength];
            Array.Copy(input, buffer, Math.Min(input.Length, buffer.Length));
            Radix2Fft.InverseInPlace(buffer);
            var shifted = new Complex[paddedLength];
            var shift = paddedLength / 2;
            for (var i = 0; i < paddedLength; i++) shifted[i] = buffer[(i + shift) % paddedLength];
            return shifted;
        }

        private static int FindPeakIndex(Complex[] values)
        {
            var peak = 0;
            var magnitude = values.Length == 0 ? 0.0 : values[0].Magnitude;
            for (var i = 1; i < values.Length; i++) if (values[i].Magnitude > magnitude) { magnitude = values[i].Magnitude; peak = i; }
            return peak;
        }

        private static double BuildRepresentativeValue(string mode, Complex[] ifft, int peak, int center)
        {
            var peakMagnitude = ifft[peak].Magnitude;
            var centerMagnitude = ifft[center].Magnitude;
            switch (NormalizeMetricMode(mode))
            {
                case MetricSignedPeakOffset: return peak - center;
                case MetricPeakAmplitude: return peakMagnitude;
                case MetricPeakCenterDelta: return peakMagnitude - centerMagnitude;
                default: return Math.Abs(peak - center);
            }
        }

        private static double[] BuildRepresentativeProfile(double[] values, int[] centers, int outputLength)
        {
            var output = new double[outputLength];
            if (values.Length == 0) return output;
            var first = Math.Max(0, Math.Min(outputLength - 1, centers[0]));
            for (var i = 0; i <= first; i++) output[i] = values[0];
            for (var segment = 0; segment < values.Length - 1; segment++)
            {
                var left = centers[segment];
                var right = centers[segment + 1];
                if (right <= left) { output[left] = values[segment]; continue; }
                for (var i = left; i <= right; i++) output[i] = values[segment] + ((values[segment + 1] - values[segment]) * (i - left) / (right - left));
            }
            var last = Math.Max(0, Math.Min(outputLength - 1, centers[centers.Length - 1]));
            for (var i = last; i < outputLength; i++) output[i] = values[values.Length - 1];
            return output;
        }

        private static void SmoothInPlace(double[] samples, int window)
        {
            window = Math.Max(1, window);
            if (window == 1 || samples.Length == 0) return;
            var source = (double[])samples.Clone();
            var half = window / 2;
            var sum = 0.0;
            var left = 0;
            var right = -1;
            for (var i = 0; i < samples.Length; i++)
            {
                var desiredLeft = Math.Max(0, i - half);
                var desiredRight = Math.Min(samples.Length - 1, i + half);
                while (right < desiredRight) sum += source[++right];
                while (left < desiredLeft) sum -= source[left++];
                samples[i] = sum / (desiredRight - desiredLeft + 1);
            }
        }

        private static List<DetectedFeature> DetectEvents(double[] profile, double thresholdRatio)
        {
            var result = new List<DetectedFeature>();
            if (profile.Length < 3) return result;
            var max = profile.Max(value => Math.Abs(value));
            if (max <= 0.0) return result;
            var threshold = max * Math.Max(0.05, Math.Min(0.95, thresholdRatio));
            var minimumSeparation = Math.Max(4, profile.Length / 48);
            var last = -minimumSeparation;
            for (var i = 1; i < profile.Length - 1; i++)
            {
                var value = Math.Abs(profile[i]);
                if (value < threshold || value < Math.Abs(profile[i - 1]) || value <= Math.Abs(profile[i + 1])) continue;
                if (i - last < minimumSeparation)
                {
                    if (result.Count > 0 && value > result[result.Count - 1].Value) { result[result.Count - 1] = new DetectedFeature { PositionIndex = i, Value = value }; last = i; }
                    continue;
                }
                result.Add(new DetectedFeature { PositionIndex = i, Value = value });
                last = i;
            }
            return result;
        }

        private static List<NamedMetric> BuildProfileMetrics(double[] profile, int segmentCount)
        {
            var min = profile.Length == 0 ? 0.0 : profile.Min();
            var max = profile.Length == 0 ? 0.0 : profile.Max();
            var mean = profile.Length == 0 ? 0.0 : profile.Average();
            var rms = profile.Length == 0 ? 0.0 : Math.Sqrt(profile.Average(value => value * value));
            return new List<NamedMetric> { Metric("samples", profile.Length, "count"), Metric("segments", segmentCount, "count"), Metric("min", min, ""), Metric("max", max, ""), Metric("mean", mean, ""), Metric("rms", rms, "") };
        }

        private static NamedMetric Metric(string name, double value, string unit) { return new NamedMetric { Name = name, Value = value, Unit = unit }; }

        private static void ValidateConfig(OfdrProcessingConfig config)
        {
            if (config.SegmentSize < 2) throw new ArgumentOutOfRangeException(nameof(config.SegmentSize));
            if (config.SegmentHop < 1) throw new ArgumentOutOfRangeException(nameof(config.SegmentHop));
            if (config.SegmentIfftZeroPadFactor < 1) throw new ArgumentOutOfRangeException(nameof(config.SegmentIfftZeroPadFactor));
            if (config.MaxAuxNonmonotonicRatio < 0.0 || config.MaxAuxNonmonotonicRatio > 1.0) throw new ArgumentOutOfRangeException(nameof(config.MaxAuxNonmonotonicRatio));
            config.FinalMetricMode = NormalizeMetricMode(config.FinalMetricMode);
            config.MaxPreviewPoints = Math.Max(16, config.MaxPreviewPoints);
        }

        public static string NormalizeMetricMode(string mode)
        {
            switch (mode)
            {
                case MetricSignedPeakOffset:
                case MetricPeakAmplitude:
                case MetricPeakCenterDelta:
                case MetricAbsolutePeakOffset: return mode;
                default: return MetricAbsolutePeakOffset;
            }
        }

        public static string FinalMetricYAxis(string mode)
        {
            switch (NormalizeMetricMode(mode))
            {
                case MetricSignedPeakOffset: return "Shift (bins)";
                case MetricPeakAmplitude: return "Peak magnitude";
                case MetricPeakCenterDelta: return "Magnitude delta";
                default: return "Offset (bins)";
            }
        }

        private static IProgress<OperationProgress> ScaleProgress(IProgress<OperationProgress> target, string stage, double start, double end)
        {
            if (target == null) return null;
            return new InlineProgress(value => target.Report(new OperationProgress
            {
                Stage = stage,
                Detail = value == null ? string.Empty : value.Detail,
                Completed = value == null ? 0 : value.Completed,
                Total = value == null ? 0 : value.Total,
                Percent = start + ((end - start) * (value == null ? 0.0 : value.Percent) / 100.0)
            }));
        }

        private sealed class InlineProgress : IProgress<OperationProgress>
        {
            private readonly Action<OperationProgress> report;

            public InlineProgress(Action<OperationProgress> reportAction)
            {
                report = reportAction;
            }

            public void Report(OperationProgress value)
            {
                report(value);
            }
        }
    }
}
