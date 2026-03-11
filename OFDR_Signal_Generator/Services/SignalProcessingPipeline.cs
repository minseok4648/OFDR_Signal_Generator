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
            var fftLength = CalculateFftLength(Math.Max(rawComplex.Length, referenceComplex.Length), request == null ? 1 : request.FftZeroPadFactor);
            var sampleSpacing = request == null ? 1.0 : Math.Max(0.0001, request.SampleSpacing);

            var rawSignalMagnitudes = rawComplex.Select(value => value.Magnitude).ToArray();
            var referenceSignalMagnitudes = referenceComplex.Select(value => value.Magnitude).ToArray();
            var rawSpectrum = ForwardTransform(rawComplex, fftLength, request != null && request.UseHannWindow);
            var refSpectrum = ForwardTransform(referenceComplex, fftLength, request != null && request.UseHannWindow);
            var rawSpectrumMagnitudes = rawSpectrum.Select(value => value.Magnitude).ToArray();
            var refSpectrumMagnitudes = refSpectrum.Select(value => value.Magnitude).ToArray();

            var segmentResult = BuildSegments(rawSpectrum, refSpectrum, request == null ? 64 : request.SegmentSize, request == null ? 32 : request.SegmentHop);
            var conjMulSegments = segmentResult.RawSegments
                .Zip(segmentResult.ReferenceSegments, (rawSeg, refSeg) => ConjugateMultiply(rawSeg, refSeg, request))
                .ToList();
            var ifftSegments = conjMulSegments
                .Select(InverseTransform)
                .Select(FftShift)
                .ToList();

            var zeroLagIndices = ifftSegments.Select(segment => segment.Length / 2).ToList();
            var ifftPeakIndices = ifftSegments.Select(FindPeakIndex).ToList();
            var representativeValues = ifftSegments
                .Select((segment, index) => segment.Length == 0 ? 0.0 : Math.Abs(ifftPeakIndices[index] - zeroLagIndices[index]))
                .ToList();

            var finalProfile = BuildFinalProfile(representativeValues, segmentResult.SegmentCenterIndices, fftLength);
            finalProfile = Smooth(finalProfile, request == null ? 5 : request.FinalSmoothingWindow);
            finalProfile = Normalize(finalProfile);

            var detectedEvents = DetectEvents(finalProfile, sampleSpacing, request == null ? 0.35 : request.PeakThresholdRatio);
            var comparison = verifier.Compare(signal == null ? null : signal.GroundTruthEvents, detectedEvents, sampleSpacing);

            var stages = new List<StageResult>
            {
                BuildStage("Raw Data", "Active raw signal source before any spectral processing.", rawSignalMagnitudes, signal == null ? null : signal.GroundTruthEvents.Select(item => item.PositionIndex).ToArray()),
                BuildStage("Ref Data", "Active reference signal source before any spectral processing.", referenceSignalMagnitudes, new int[0]),
                BuildStage("Raw FFT", "Frequency-domain magnitude of the raw data after optional Hann windowing and zero padding.", rawSpectrumMagnitudes, null),
                BuildStage("Ref FFT", "Frequency-domain magnitude of the reference data after optional Hann windowing and zero padding.", refSpectrumMagnitudes, null),
                BuildStage("Final Result", "Segment-wise IFFT peak shift magnitude mapped onto position and normalized as the final OFDR difference profile.", finalProfile, detectedEvents.Select(item => item.PositionIndex).ToArray(), detectedEvents)
            };

            return new PipelineRunResult
            {
                Stages = stages,
                RawSignalMagnitudes = rawSignalMagnitudes,
                ReferenceSignalMagnitudes = referenceSignalMagnitudes,
                RawSpectrumMagnitudes = rawSpectrumMagnitudes,
                ReferenceSpectrumMagnitudes = refSpectrumMagnitudes,
                RawSegments = segmentResult.RawSegments,
                ReferenceSegments = segmentResult.ReferenceSegments,
                ConjugateMultiplySegments = conjMulSegments,
                IfftSegments = ifftSegments,
                SegmentStartIndices = segmentResult.SegmentStartIndices,
                SegmentCenterIndices = segmentResult.SegmentCenterIndices,
                SegmentRepresentativeValues = representativeValues,
                IfftPeakIndices = ifftPeakIndices,
                FinalProfile = finalProfile,
                DetectedEvents = detectedEvents,
                ComparisonSummary = comparison.Summary,
                MatchResults = comparison.Rows
            };
        }

        private static StageResult BuildStage(string name, string description, double[] samples, int[] markerIndices, IList<DetectedEvent> detectedEvents = null)
        {
            var safeSamples = samples ?? new double[0];
            var safeMarkers = markerIndices ?? new int[0];
            return new StageResult
            {
                Name = name,
                Description = description,
                Samples = safeSamples,
                MarkerIndices = safeMarkers,
                Metrics = StageMath.BuildMetrics(safeSamples, safeMarkers.Length),
                DetectedEvents = detectedEvents ?? new DetectedEvent[0]
            };
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

        private static SegmentWindowResult BuildSegments(Complex[] rawSpectrum, Complex[] refSpectrum, int segmentSize, int hop)
        {
            rawSpectrum = rawSpectrum ?? new Complex[0];
            refSpectrum = refSpectrum ?? new Complex[0];
            var length = Math.Min(rawSpectrum.Length, refSpectrum.Length);
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
                AddSegment(rawSpectrum, refSpectrum, segmentSize, start, rawSegments, referenceSegments, startIndices, centerIndices);
            }

            if (startIndices.Count == 0 || startIndices[startIndices.Count - 1] != lastPossibleStart)
            {
                AddSegment(rawSpectrum, refSpectrum, segmentSize, lastPossibleStart, rawSegments, referenceSegments, startIndices, centerIndices);
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
            Complex[] refSpectrum,
            int segmentSize,
            int start,
            IList<Complex[]> rawSegments,
            IList<Complex[]> referenceSegments,
            IList<int> startIndices,
            IList<int> centerIndices)
        {
            rawSegments.Add(rawSpectrum.Skip(start).Take(segmentSize).ToArray());
            referenceSegments.Add(refSpectrum.Skip(start).Take(segmentSize).ToArray());
            startIndices.Add(start);
            centerIndices.Add(start + (segmentSize / 2));
        }

        private static Complex[] ConjugateMultiply(Complex[] rawSegment, Complex[] refSegment, SyntheticSignalRequest request)
        {
            var length = Math.Min(rawSegment == null ? 0 : rawSegment.Length, refSegment == null ? 0 : refSegment.Length);
            var output = new Complex[length];
            var scale = request == null ? 1.0 : request.ConjugateMultiplyScale;
            var normalize = request != null && request.NormalizeConjugateMultiply;

            for (var index = 0; index < length; index++)
            {
                var value = rawSegment[index] * Complex.Conjugate(refSegment[index]) * scale;
                if (normalize)
                {
                    var denom = rawSegment[index].Magnitude * refSegment[index].Magnitude;
                    if (denom > 1e-9)
                    {
                        value /= denom;
                    }
                }

                output[index] = value;
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

        private static double[] BuildFinalProfile(IList<double> representativeValues, IList<int> segmentCenterIndices, int outputLength)
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
            for (var i = 0; i <= firstCenter; i++)
            {
                profile[i] = representativeValues[0];
            }

            for (var seg = 0; seg < representativeValues.Count - 1; seg++)
            {
                var left = Math.Max(0, Math.Min(outputLength - 1, segmentCenterIndices[seg]));
                var right = Math.Max(0, Math.Min(outputLength - 1, segmentCenterIndices[seg + 1]));
                if (right <= left)
                {
                    profile[left] = representativeValues[seg];
                    continue;
                }

                for (var x = left; x <= right; x++)
                {
                    var ratio = (x - left) / (double)(right - left);
                    profile[x] = representativeValues[seg] + ((representativeValues[seg + 1] - representativeValues[seg]) * ratio);
                }
            }

            var lastCenter = Math.Max(0, Math.Min(outputLength - 1, segmentCenterIndices[segmentCenterIndices.Count - 1]));
            for (var i = lastCenter; i < outputLength; i++)
            {
                profile[i] = representativeValues[representativeValues.Count - 1];
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
    }

    internal sealed class SegmentWindowResult
    {
        public IList<Complex[]> RawSegments { get; set; }

        public IList<Complex[]> ReferenceSegments { get; set; }

        public IList<int> SegmentStartIndices { get; set; }

        public IList<int> SegmentCenterIndices { get; set; }
    }
}
