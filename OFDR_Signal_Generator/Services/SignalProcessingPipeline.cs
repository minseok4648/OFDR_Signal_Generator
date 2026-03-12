using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OFDR_Signal_Generator.Models;
using OFDR_Signal_Generator.Processing;

namespace OFDR_Signal_Generator.Services
{
    public sealed class SignalProcessingPipeline
    {
        public const string FinalMetricAbsolutePeakOffset = "Absolute Peak Offset";
        public const string FinalMetricSignedPeakOffset = "Signed Peak Offset";
        public const string FinalMetricPeakAmplitude = "Peak Amplitude";
        public const string FinalMetricPeakCenterDelta = "Peak-Center Delta";

        private readonly PipelineVerifier verifier;

        public SignalProcessingPipeline()
        {
            verifier = new PipelineVerifier();
        }

        public PipelineRunResult Run(SyntheticSignalResult signal, SyntheticSignalRequest request)
        {
            var raw = signal == null ? new double[0] : signal.RawSignal ?? new double[0];
            var reference = signal == null ? new double[0] : signal.ReferenceSignal ?? new double[0];
            var rawComplex = GetComplexSignal(signal == null ? null : signal.RawComplexSignal, raw);
            var referenceComplex = GetComplexSignal(signal == null ? null : signal.ReferenceComplexSignal, reference);
            var spectrumLength = Math.Max(1, Math.Max(rawComplex.Length, referenceComplex.Length));
            var sampleSpacing = request == null ? 1.0 : Math.Max(0.0001, request.SampleSpacing);
            var finalMetricMode = NormalizeFinalMetricMode(request == null ? null : request.FinalMetricMode);

            var rawSignalMagnitudes = rawComplex.Select(value => value.Magnitude).ToArray();
            var referenceSignalMagnitudes = referenceComplex.Select(value => value.Magnitude).ToArray();
            var rawSpectrum = ForwardTransform(rawComplex, spectrumLength, request != null && request.UseHannWindow);
            var referenceSpectrum = ForwardTransform(referenceComplex, spectrumLength, request != null && request.UseHannWindow);
            var rawSpectrumMagnitudes = rawSpectrum.Select(value => value.Magnitude).ToArray();
            var referenceSpectrumMagnitudes = referenceSpectrum.Select(value => value.Magnitude).ToArray();

            var segmentResult = BuildSegments(rawSpectrum, referenceSpectrum, request == null ? 64 : request.SegmentSize, request == null ? 32 : request.SegmentHop);
            var segmentIfftLength = CalculateFftLength(segmentResult.RawSegments.Count == 0 ? 1 : segmentResult.RawSegments[0].Length, request == null ? 1 : request.FftZeroPadFactor);
            var rawIfftSegments = segmentResult.RawSegments.Select(segment => InverseTransform(ZeroPadSegment(segment, segmentIfftLength))).Select(FftShift).ToList();
            ApplyRawSegmentShiftEvents(rawIfftSegments, request == null ? null : request.Events);
            var referenceIfftSegments = segmentResult.ReferenceSegments.Select(segment => InverseTransform(ZeroPadSegment(segment, segmentIfftLength))).Select(FftShift).ToList();
            var reconstructedRawSpectrum = ReconstructSpectrumFromShiftedSegments(rawIfftSegments, segmentResult.RawSegments, segmentResult.SegmentStartIndices, spectrumLength);
            var reconstructedRawComplex = InverseTransform(reconstructedRawSpectrum);
            var reconstructedRawSignal = reconstructedRawComplex.Select(value => value.Real).ToArray();
            var crossCorrelationSegments = rawIfftSegments
                .Zip(referenceIfftSegments, (rawSegment, referenceSegment) => CrossCorrelate(rawSegment, referenceSegment, request))
                .ToList();

            var zeroLagIndices = crossCorrelationSegments
                .Select(segment => segment.Length == 0 ? 0 : Math.Max(0, segment.Length / 2))
                .ToList();
            var peakIndices = crossCorrelationSegments.Select(FindPeakIndex).ToList();
            var representativeValues = BuildFinalMetricValues(finalMetricMode, crossCorrelationSegments, peakIndices, zeroLagIndices);

            var finalProfile = BuildRepresentativeProfile(representativeValues, segmentResult.SegmentCenterIndices, spectrumLength);
            finalProfile = Smooth(finalProfile, request == null ? 5 : request.FinalSmoothingWindow);

            var detectionProfile = Normalize(finalProfile.Select(Math.Abs).ToArray());
            var detectedEvents = DetectEvents(detectionProfile, sampleSpacing, request == null ? 0.35 : request.PeakThresholdRatio);
            var comparison = verifier.Compare(signal == null ? null : signal.GroundTruthEvents, detectedEvents, sampleSpacing);

            return new PipelineRunResult
            {
                Stages = new List<StageResult>(),
                RawSignalMagnitudes = rawSignalMagnitudes,
                ReferenceSignalMagnitudes = referenceSignalMagnitudes,
                RawSpectrumMagnitudes = rawSpectrumMagnitudes,
                ReferenceSpectrumMagnitudes = referenceSpectrumMagnitudes,
                RawSegments = segmentResult.RawSegments,
                ReferenceSegments = segmentResult.ReferenceSegments,
                RawIfftSegments = rawIfftSegments,
                ReferenceIfftSegments = referenceIfftSegments,
                CrossCorrelationSegments = crossCorrelationSegments,
                SegmentStartIndices = segmentResult.SegmentStartIndices,
                SegmentCenterIndices = segmentResult.SegmentCenterIndices,
                SegmentRepresentativeValues = representativeValues,
                CrossCorrelationPeakIndices = peakIndices,
                FinalDisplayProfile = finalProfile,
                FinalProfile = finalProfile,
                FinalMetricMode = finalMetricMode,
                ReconstructedRawSignal = reconstructedRawSignal,
                ReconstructedRawComplexSignal = reconstructedRawComplex,
                DetectedEvents = detectedEvents,
                ComparisonSummary = comparison.Summary,
                MatchResults = comparison.Rows
            };
        }

        public static string NormalizeFinalMetricMode(string mode)
        {
            switch (mode)
            {
                case FinalMetricSignedPeakOffset:
                case FinalMetricPeakAmplitude:
                case FinalMetricPeakCenterDelta:
                case FinalMetricAbsolutePeakOffset:
                    return mode;
                default:
                    return FinalMetricAbsolutePeakOffset;
            }
        }

        public static string GetFinalMetricYAxisTitle(string mode)
        {
            switch (NormalizeFinalMetricMode(mode))
            {
                case FinalMetricSignedPeakOffset:
                    return "Shift (bins)";
                case FinalMetricPeakAmplitude:
                    return "Peak magnitude";
                case FinalMetricPeakCenterDelta:
                    return "Magnitude delta";
                default:
                    return "Offset (bins)";
            }
        }

        private static IList<double> BuildFinalMetricValues(string mode, IList<Complex[]> crossCorrelationSegments, IList<int> peakIndices, IList<int> zeroLagIndices)
        {
            var metricMode = NormalizeFinalMetricMode(mode);
            var values = new List<double>();
            for (var index = 0; index < crossCorrelationSegments.Count; index++)
            {
                var segment = crossCorrelationSegments[index] ?? new Complex[0];
                if (segment.Length == 0)
                {
                    values.Add(0.0);
                    continue;
                }

                var peakIndex = peakIndices[index];
                var zeroLagIndex = zeroLagIndices[index];
                var boundedPeakIndex = Math.Max(0, Math.Min(segment.Length - 1, peakIndex));
                var boundedZeroIndex = Math.Max(0, Math.Min(segment.Length - 1, zeroLagIndex));
                var peakMagnitude = segment[boundedPeakIndex].Magnitude;
                var centerMagnitude = segment[boundedZeroIndex].Magnitude;

                switch (metricMode)
                {
                    case FinalMetricSignedPeakOffset:
                        values.Add(peakIndex - zeroLagIndex);
                        break;
                    case FinalMetricPeakAmplitude:
                        values.Add(peakMagnitude);
                        break;
                    case FinalMetricPeakCenterDelta:
                        values.Add(peakMagnitude - centerMagnitude);
                        break;
                    default:
                        values.Add(Math.Abs(peakIndex - zeroLagIndex));
                        break;
                }
            }

            return values;
        }

        private static int CalculateFftLength(int inputLength, int zeroPadFactor)
        {
            var baseLength = Math.Max(1, inputLength);
            var factor = Math.Max(1, zeroPadFactor);
            return baseLength * factor;
        }

        private static Complex[] GetComplexSignal(Complex[] preferred, double[] fallback)
        {
            if (preferred != null && preferred.Length > 0)
            {
                return preferred;
            }

            fallback = fallback ?? new double[0];
            return fallback.Select(value => new Complex(value, 0.0)).ToArray();
        }

        private static Complex[] ForwardTransform(Complex[] timeSignal, int fftLength, bool useHannWindow)
        {
            var prepared = new Complex[fftLength];
            var source = timeSignal ?? new Complex[0];
            var length = Math.Min(source.Length, fftLength);

            for (var index = 0; index < length; index++)
            {
                var value = source[index];
                if (useHannWindow && source.Length > 1)
                {
                    var window = 0.5 - (0.5 * Math.Cos((2.0 * Math.PI * index) / (source.Length - 1)));
                    value *= window;
                }

                prepared[index] = value;
            }

            return DiscreteFourierTransform(prepared, false);
        }

        private static Complex[] InverseTransform(Complex[] spectrum)
        {
            return DiscreteFourierTransform(spectrum ?? new Complex[0], true);
        }

        private static Complex[] ZeroPadSegment(Complex[] segment, int paddedLength)
        {
            segment = segment ?? new Complex[0];
            paddedLength = Math.Max(segment.Length, paddedLength);
            var padded = new Complex[paddedLength];
            Array.Copy(segment, padded, segment.Length);
            return padded;
        }
        private static Complex[] FftShift(Complex[] input)
        {
            input = input ?? new Complex[0];
            if (input.Length == 0)
            {
                return input;
            }

            var output = new Complex[input.Length];
            var shift = input.Length / 2;
            for (var index = 0; index < input.Length; index++)
            {
                output[index] = input[(index + shift) % input.Length];
            }

            return output;
        }

        private static Complex[] InverseFftShift(Complex[] input)
        {
            input = input ?? new Complex[0];
            if (input.Length == 0)
            {
                return input;
            }

            var output = new Complex[input.Length];
            var shift = input.Length / 2;
            for (var index = 0; index < input.Length; index++)
            {
                output[(index + shift) % input.Length] = input[index];
            }

            return output;
        }

        private static Complex[] ReconstructSpectrumFromShiftedSegments(IList<Complex[]> rawIfftSegments, IList<Complex[]> rawSegments, IList<int> segmentStartIndices, int spectrumLength)
        {
            spectrumLength = Math.Max(1, spectrumLength);
            var accumulated = new Complex[spectrumLength];
            var counts = new double[spectrumLength];

            if (rawIfftSegments == null || rawSegments == null || segmentStartIndices == null)
            {
                return accumulated;
            }

            var segmentCount = Math.Min(rawIfftSegments.Count, Math.Min(rawSegments.Count, segmentStartIndices.Count));
            for (var segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
            {
                var shiftedIfft = rawIfftSegments[segmentIndex] ?? new Complex[0];
                var originalSegment = rawSegments[segmentIndex] ?? new Complex[0];
                if (shiftedIfft.Length == 0 || originalSegment.Length == 0)
                {
                    continue;
                }

                var unshiftedIfft = InverseFftShift(shiftedIfft);
                var paddedSpectrum = ForwardTransform(unshiftedIfft, unshiftedIfft.Length, false);
                var segmentLength = Math.Min(originalSegment.Length, paddedSpectrum.Length);
                var start = segmentStartIndices[segmentIndex];

                for (var localIndex = 0; localIndex < segmentLength; localIndex++)
                {
                    var spectrumIndex = start + localIndex;
                    if (spectrumIndex < 0 || spectrumIndex >= spectrumLength)
                    {
                        continue;
                    }

                    accumulated[spectrumIndex] += paddedSpectrum[localIndex];
                    counts[spectrumIndex] += 1.0;
                }
            }

            for (var index = 0; index < accumulated.Length; index++)
            {
                if (counts[index] > 0.0)
                {
                    accumulated[index] /= counts[index];
                }
            }

            return accumulated;
        }
        private static Complex[] DiscreteFourierTransform(Complex[] input, bool inverse)
        {
            var length = input == null ? 0 : input.Length;
            var output = new Complex[length];
            if (length == 0)
            {
                return output;
            }

            var direction = inverse ? 1.0 : -1.0;
            for (var k = 0; k < length; k++)
            {
                Complex sum = Complex.Zero;
                for (var n = 0; n < length; n++)
                {
                    var angle = direction * 2.0 * Math.PI * k * n / length;
                    var twiddle = new Complex(Math.Cos(angle), Math.Sin(angle));
                    sum += input[n] * twiddle;
                }

                output[k] = inverse ? sum / length : sum;
            }

            return output;
        }

        private static SegmentWindowResult BuildSegments(Complex[] rawSpectrum, Complex[] referenceSpectrum, int segmentSize, int hop)
        {
            rawSpectrum = rawSpectrum ?? new Complex[0];
            referenceSpectrum = referenceSpectrum ?? new Complex[0];
            var length = Math.Min(rawSpectrum.Length, referenceSpectrum.Length);
            var rawSegments = new List<Complex[]>();
            var referenceSegments = new List<Complex[]>();
            var startIndices = new List<int>();
            var centerIndices = new List<int>();

            if (length == 0)
            {
                return new SegmentWindowResult
                {
                    RawSegments = rawSegments,
                    ReferenceSegments = referenceSegments,
                    SegmentStartIndices = startIndices,
                    SegmentCenterIndices = centerIndices
                };
            }

            segmentSize = Math.Max(2, Math.Min(length, segmentSize));
            hop = Math.Max(1, hop);
            var lastPossibleStart = Math.Max(0, length - segmentSize);

            for (var start = 0; start <= lastPossibleStart; start += hop)
            {
                AddSegment(rawSpectrum, referenceSpectrum, segmentSize, start, rawSegments, referenceSegments, startIndices, centerIndices);
            }

            if (startIndices.Count == 0 || startIndices[startIndices.Count - 1] != lastPossibleStart)
            {
                AddSegment(rawSpectrum, referenceSpectrum, segmentSize, lastPossibleStart, rawSegments, referenceSegments, startIndices, centerIndices);
            }

            return new SegmentWindowResult
            {
                RawSegments = rawSegments,
                ReferenceSegments = referenceSegments,
                SegmentStartIndices = startIndices,
                SegmentCenterIndices = centerIndices
            };
        }

        private static void AddSegment(
            Complex[] rawSpectrum,
            Complex[] referenceSpectrum,
            int segmentSize,
            int start,
            IList<Complex[]> rawSegments,
            IList<Complex[]> referenceSegments,
            IList<int> startIndices,
            IList<int> centerIndices)
        {
            rawSegments.Add(rawSpectrum.Skip(start).Take(segmentSize).ToArray());
            referenceSegments.Add(referenceSpectrum.Skip(start).Take(segmentSize).ToArray());
            startIndices.Add(start);
            centerIndices.Add(start + (segmentSize / 2));
        }

        private static void ApplyRawSegmentShiftEvents(IList<Complex[]> rawIfftSegments, IEnumerable<ReflectionEvent> events)
        {
            if (rawIfftSegments == null || rawIfftSegments.Count == 0 || events == null)
            {
                return;
            }

            foreach (var shiftEvent in events)
            {
                if (shiftEvent == null)
                {
                    continue;
                }

                var segmentIndex = shiftEvent.PositionIndex - 1;
                if (segmentIndex < 0 || segmentIndex >= rawIfftSegments.Count)
                {
                    continue;
                }

                var shiftBins = (int)Math.Round(shiftEvent.Amplitude);
                rawIfftSegments[segmentIndex] = CircularShift(rawIfftSegments[segmentIndex], shiftBins);
            }
        }

        private static Complex[] CircularShift(Complex[] samples, int shift)
        {
            samples = samples ?? new Complex[0];
            if (samples.Length == 0)
            {
                return samples;
            }

            var normalizedShift = shift % samples.Length;
            if (normalizedShift < 0)
            {
                normalizedShift += samples.Length;
            }

            if (normalizedShift == 0)
            {
                return samples.ToArray();
            }

            var output = new Complex[samples.Length];
            for (var index = 0; index < samples.Length; index++)
            {
                var targetIndex = (index + normalizedShift) % samples.Length;
                output[targetIndex] = samples[index];
            }

            return output;
        }

        private static Complex[] CrossCorrelate(Complex[] rawSegment, Complex[] referenceSegment, SyntheticSignalRequest request)
        {
            rawSegment = rawSegment ?? new Complex[0];
            referenceSegment = referenceSegment ?? new Complex[0];
            if (rawSegment.Length == 0 || referenceSegment.Length == 0)
            {
                return new Complex[0];
            }

            var outputLength = rawSegment.Length + referenceSegment.Length - 1;
            var output = new Complex[outputLength];
            var scale = request == null ? 1.0 : request.ConjugateMultiplyScale;
            var normalize = request != null && request.NormalizeConjugateMultiply;
            var norm = 1.0;

            if (normalize)
            {
                var rawEnergy = rawSegment.Sum(value => value.Magnitude * value.Magnitude);
                var referenceEnergy = referenceSegment.Sum(value => value.Magnitude * value.Magnitude);
                var denom = Math.Sqrt(rawEnergy * referenceEnergy);
                if (denom > 1e-9)
                {
                    norm = denom;
                }
            }

            for (var lag = -(referenceSegment.Length - 1); lag <= rawSegment.Length - 1; lag++)
            {
                Complex sum = Complex.Zero;
                for (var rawIndex = 0; rawIndex < rawSegment.Length; rawIndex++)
                {
                    var referenceIndex = rawIndex - lag;
                    if (referenceIndex >= 0 && referenceIndex < referenceSegment.Length)
                    {
                        sum += rawSegment[rawIndex] * Complex.Conjugate(referenceSegment[referenceIndex]);
                    }
                }

                output[lag + referenceSegment.Length - 1] = (sum * scale) / norm;
            }

            return output;
        }

        private static int FindPeakIndex(Complex[] segment)
        {
            if (segment == null || segment.Length == 0)
            {
                return 0;
            }

            var peakIndex = 0;
            var peakMagnitude = segment[0].Magnitude;
            for (var index = 1; index < segment.Length; index++)
            {
                var magnitude = segment[index].Magnitude;
                if (magnitude > peakMagnitude)
                {
                    peakMagnitude = magnitude;
                    peakIndex = index;
                }
            }

            return peakIndex;
        }

        private static double[] BuildRepresentativeProfile(IList<double> representativeValues, IList<int> segmentCenterIndices, int outputLength)
        {
            outputLength = Math.Max(1, outputLength);
            var profile = new double[outputLength];
            if (representativeValues == null || segmentCenterIndices == null || representativeValues.Count == 0 || segmentCenterIndices.Count == 0)
            {
                return profile;
            }

            if (representativeValues.Count == 1)
            {
                for (var index = 0; index < outputLength; index++)
                {
                    profile[index] = representativeValues[0];
                }

                return profile;
            }

            var firstCenter = Math.Max(0, Math.Min(outputLength - 1, segmentCenterIndices[0]));
            for (var index = 0; index <= firstCenter; index++)
            {
                profile[index] = representativeValues[0];
            }

            for (var segmentIndex = 0; segmentIndex < representativeValues.Count - 1; segmentIndex++)
            {
                var left = Math.Max(0, Math.Min(outputLength - 1, segmentCenterIndices[segmentIndex]));
                var right = Math.Max(0, Math.Min(outputLength - 1, segmentCenterIndices[segmentIndex + 1]));
                if (right <= left)
                {
                    profile[left] = representativeValues[segmentIndex];
                    continue;
                }

                for (var x = left; x <= right; x++)
                {
                    var ratio = (x - left) / (double)(right - left);
                    profile[x] = representativeValues[segmentIndex] + ((representativeValues[segmentIndex + 1] - representativeValues[segmentIndex]) * ratio);
                }
            }

            var lastCenter = Math.Max(0, Math.Min(outputLength - 1, segmentCenterIndices[segmentCenterIndices.Count - 1]));
            for (var index = lastCenter; index < outputLength; index++)
            {
                profile[index] = representativeValues[representativeValues.Count - 1];
            }

            return profile;
        }

        private static double[] Smooth(double[] samples, int windowSize)
        {
            samples = samples ?? new double[0];
            if (samples.Length == 0)
            {
                return samples;
            }

            windowSize = Math.Max(1, windowSize);
            if (windowSize == 1)
            {
                return samples.ToArray();
            }

            var halfWindow = windowSize / 2;
            var smoothed = new double[samples.Length];
            for (var index = 0; index < samples.Length; index++)
            {
                var start = Math.Max(0, index - halfWindow);
                var end = Math.Min(samples.Length - 1, index + halfWindow);
                double sum = 0.0;
                for (var cursor = start; cursor <= end; cursor++)
                {
                    sum += samples[cursor];
                }

                smoothed[index] = sum / (end - start + 1);
            }

            return smoothed;
        }

        private static double[] Normalize(double[] samples)
        {
            samples = samples ?? new double[0];
            if (samples.Length == 0)
            {
                return samples;
            }

            var max = samples.Max();
            if (max <= 1e-9)
            {
                return samples.ToArray();
            }

            return samples.Select(value => value / max).ToArray();
        }

        private static IList<DetectedEvent> DetectEvents(double[] samples, double sampleSpacing, double thresholdRatio)
        {
            samples = samples ?? new double[0];
            var detections = new List<DetectedEvent>();
            if (samples.Length < 3)
            {
                return detections;
            }

            thresholdRatio = Math.Max(0.05, Math.Min(0.95, thresholdRatio));
            var max = samples.Max();
            var threshold = max * thresholdRatio;
            var minimumSeparation = Math.Max(4, samples.Length / 48);
            var lastAccepted = -minimumSeparation;

            for (var index = 1; index < samples.Length - 1; index++)
            {
                var isPeak = samples[index] >= threshold &&
                             samples[index] >= samples[index - 1] &&
                             samples[index] > samples[index + 1];

                if (!isPeak)
                {
                    continue;
                }

                if (index - lastAccepted < minimumSeparation)
                {
                    if (detections.Count > 0 && samples[index] > detections[detections.Count - 1].Amplitude)
                    {
                        detections[detections.Count - 1] = new DetectedEvent
                        {
                            PositionIndex = index,
                            Amplitude = samples[index],
                            Distance = index * sampleSpacing
                        };
                        lastAccepted = index;
                    }

                    continue;
                }

                detections.Add(new DetectedEvent
                {
                    PositionIndex = index,
                    Amplitude = samples[index],
                    Distance = index * sampleSpacing
                });
                lastAccepted = index;
            }

            return detections;
        }

        private sealed class SegmentWindowResult
        {
            public IList<Complex[]> RawSegments { get; set; }

            public IList<Complex[]> ReferenceSegments { get; set; }

            public IList<int> SegmentStartIndices { get; set; }

            public IList<int> SegmentCenterIndices { get; set; }
        }
    }







}
