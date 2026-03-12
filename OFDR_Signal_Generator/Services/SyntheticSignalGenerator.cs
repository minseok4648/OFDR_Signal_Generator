using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OFDR_Signal_Generator.Models;

namespace OFDR_Signal_Generator.Services
{
    public sealed class SyntheticSignalGenerator
    {
        public SyntheticSignalResult Generate(SyntheticSignalRequest request)
        {
            var sampleCount = Math.Max(1, request.SampleCount);
            var events = GetValidEvents(request, sampleCount);
            var refRandom = new Random(request.RandomSeed + 97);
            var referenceSignal = request.UseReferenceSineSignal
                ? BuildSineSignal(sampleCount, request.ReferenceSineAmplitude, request.ReferenceSineFrequency, request.ReferenceSinePhaseDegrees, request.ReferenceBaselineOffset, request.ReferenceNoiseLevel, refRandom)
                : BuildReferenceSignal(sampleCount, request, refRandom);
            var referenceComplex = referenceSignal.Select(value => new Complex(value, 0.0)).ToArray();

            Complex[] rawComplex;
            if (request.UseRawSineSignal)
            {
                if (ShouldMirrorReferenceSine(request))
                {
                    rawComplex = referenceComplex.ToArray();
                }
                else
                {
                    var rawRandom = new Random(request.RandomSeed + 11);
                    var rawSignal = BuildSineSignal(sampleCount, request.RawSineAmplitude, request.RawSineFrequency, request.RawSinePhaseDegrees, request.RawBaselineOffset, 0.0, rawRandom);
                    rawComplex = rawSignal.Select(value => new Complex(value, 0.0)).ToArray();
                }
            }
            else
            {
                rawComplex = ApplyEventsToBaseComplexSignal(referenceComplex, request, applyNoise: true);
            }

            return new SyntheticSignalResult
            {
                RawSignal = rawComplex.Select(value => value.Real).ToArray(),
                ReferenceSignal = referenceSignal,
                RawComplexSignal = rawComplex,
                ReferenceComplexSignal = referenceComplex,
                GroundTruthEvents = events
            };
        }

        public double[] ApplyEventsToBaseSignal(double[] baseSignal, SyntheticSignalRequest request)
        {
            var complexSource = (baseSignal == null ? new double[Math.Max(1, request.SampleCount)] : baseSignal)
                .Select(value => new Complex(value, 0.0))
                .ToArray();
            return ApplyEventsToBaseComplexSignal(complexSource, request).Select(value => value.Real).ToArray();
        }

        public Complex[] ApplyEventsToBaseComplexSignal(Complex[] baseSignal, SyntheticSignalRequest request)
        {
            return ApplyEventsToBaseComplexSignal(baseSignal, request, applyNoise: true);
        }

        private static Complex[] ApplyEventsToBaseComplexSignal(Complex[] baseSignal, SyntheticSignalRequest request, bool applyNoise)
        {
            var source = baseSignal == null ? new Complex[Math.Max(1, request.SampleCount)] : baseSignal.ToArray();
            if (source.Length == 0)
            {
                return source;
            }

            var output = source.ToArray();
            if (applyNoise)
            {
                ApplyNoiseToComplexSignal(output, request.RawNoiseLevel, new Random(request.RandomSeed + 11));
            }

            return output;
        }

        private static IList<ReflectionEvent> GetValidEvents(SyntheticSignalRequest request, int sampleCount)
        {
            return (request.Events ?? new List<ReflectionEvent>())
                .Select(item => item.Clone())
                .Where(item => item.PositionIndex >= 0 && item.PositionIndex < sampleCount)
                .ToList();
        }

        private static bool ShouldMirrorReferenceSine(SyntheticSignalRequest request)
        {
            return request.UseReferenceSineSignal
                && Math.Abs(request.RawSineAmplitude - request.ReferenceSineAmplitude) < 1e-9
                && Math.Abs(request.RawSineFrequency - request.ReferenceSineFrequency) < 1e-9
                && Math.Abs(request.RawSinePhaseDegrees - request.ReferenceSinePhaseDegrees) < 1e-9
                && Math.Abs(request.RawBaselineOffset - request.ReferenceBaselineOffset) < 1e-9;
        }

        private static double[] BuildReferenceSignal(int sampleCount, SyntheticSignalRequest request, Random random)
        {
            var signal = new double[sampleCount];
            var center = Math.Max(0, Math.Min(sampleCount - 1, request.ReferenceDelaySamples));
            var width = Math.Max(1.0, request.ReferenceWidth);
            var phaseShift = request.ReferencePhaseShiftDegrees * Math.PI / 180.0;

            for (var index = 0; index < sampleCount; index++)
            {
                signal[index] = request.ReferenceBaselineOffset;
                var normalizedDistance = (index - center) / width;
                var pulse = request.ReferenceAmplitude * Math.Exp(-0.5 * normalizedDistance * normalizedDistance);
                var phaseEnvelope = Math.Cos(phaseShift + (normalizedDistance * 0.2));
                signal[index] += pulse * phaseEnvelope;
                signal[index] += NextGaussian(random) * Math.Max(0.0, request.ReferenceNoiseLevel);
            }

            return signal;
        }

        private static double[] BuildSineSignal(int sampleCount, double amplitude, double frequencyCyclesPerWindow, double phaseDegrees, double baseline, double noiseLevel, Random random)
        {
            var signal = new double[sampleCount];
            var phase = phaseDegrees * Math.PI / 180.0;
            for (var index = 0; index < sampleCount; index++)
            {
                var normalizedPosition = index / (double)Math.Max(1, sampleCount - 1);
                var sine = amplitude * Math.Sin((2.0 * Math.PI * frequencyCyclesPerWindow * normalizedPosition) + phase);
                signal[index] = baseline + sine;
                signal[index] += NextGaussian(random) * Math.Max(0.0, noiseLevel);
            }

            return signal;
        }

        private static void ApplyEventsToSpectrum(IList<Complex> spectrum, IEnumerable<ReflectionEvent> events)
        {
            var length = spectrum == null ? 0 : spectrum.Count;
            if (length == 0)
            {
                return;
            }

            foreach (var reflectionEvent in events ?? Enumerable.Empty<ReflectionEvent>())
            {
                var center = Math.Max(0, Math.Min(length - 1, reflectionEvent.PositionIndex));
                var width = Math.Max(2, (int)Math.Round(reflectionEvent.Width));
                var halfWidth = Math.Max(1, width / 2);
                var start = Math.Max(0, center - halfWidth);
                var end = Math.Min(length - 1, center + halfWidth);
                var localCenter = (start + end) / 2.0;
                var slope = reflectionEvent.Amplitude;

                for (var index = start; index <= end; index++)
                {
                    var normalized = (index - localCenter) / Math.Max(1.0, halfWidth);
                    var window = 0.5 * (1.0 + Math.Cos(Math.PI * normalized));
                    var phase = slope * (index - localCenter) * window;
                    spectrum[index] *= Complex.FromPolarCoordinates(1.0, phase);
                }
            }
        }

        private static Complex[] ForwardTransform(Complex[] input)
        {
            return DiscreteFourierTransform(input ?? new Complex[0], false);
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

        private static void ApplyNoiseToComplexSignal(IList<Complex> signal, double noiseLevel, Random random)
        {
            if (signal == null || random == null)
            {
                return;
            }

            var noise = Math.Max(0.0, noiseLevel);
            for (var index = 0; index < signal.Count; index++)
            {
                var real = NextGaussian(random) * noise;
                var imaginary = NextGaussian(random) * noise;
                signal[index] += new Complex(real, imaginary);
            }
        }

        private static double NextGaussian(Random random)
        {
            var u1 = 1.0 - random.NextDouble();
            var u2 = 1.0 - random.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }
    }
}



