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
            var events = (request.Events ?? new List<ReflectionEvent>())
                .Select(item => item.Clone())
                .Where(item => item.PositionIndex >= 0 && item.PositionIndex < sampleCount)
                .ToList();

            var rawRandom = new Random(request.RandomSeed);
            var refRandom = new Random(request.RandomSeed + 97);
            var rawSignal = BuildRawSignal(sampleCount, request, events, rawRandom);
            var referenceSignal = BuildReferenceSignal(sampleCount, request, refRandom);

            return new SyntheticSignalResult
            {
                RawSignal = rawSignal,
                ReferenceSignal = referenceSignal,
                RawComplexSignal = rawSignal.Select(value => new Complex(value, 0.0)).ToArray(),
                ReferenceComplexSignal = referenceSignal.Select(value => new Complex(value, 0.0)).ToArray(),
                GroundTruthEvents = events
            };
        }

        private static double[] BuildRawSignal(int sampleCount, SyntheticSignalRequest request, IList<ReflectionEvent> events, Random random)
        {
            var signal = new double[sampleCount];

            for (var index = 0; index < sampleCount; index++)
            {
                var normalized = index / (double)Math.Max(1, sampleCount - 1);
                signal[index] = request.RawBaselineOffset * (1.0 - (request.RawAttenuation * normalized * 0.18));
            }

            foreach (var reflectionEvent in events)
            {
                var width = Math.Max(1.0, reflectionEvent.Width);
                for (var index = 0; index < sampleCount; index++)
                {
                    var normalizedDistance = (index - reflectionEvent.PositionIndex) / width;
                    var pulse = reflectionEvent.Amplitude * Math.Exp(-0.5 * normalizedDistance * normalizedDistance);
                    var attenuationFactor = Math.Exp(-request.RawAttenuation * index / Math.Max(1.0, sampleCount));
                    signal[index] += pulse * attenuationFactor;
                }
            }

            for (var index = 0; index < sampleCount; index++)
            {
                signal[index] += NextGaussian(random) * Math.Max(0.0, request.RawNoiseLevel);
            }

            return signal;
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

        private static double NextGaussian(Random random)
        {
            var u1 = 1.0 - random.NextDouble();
            var u2 = 1.0 - random.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }
    }
}
