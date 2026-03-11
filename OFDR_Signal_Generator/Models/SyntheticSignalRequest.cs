using System.Collections.Generic;

namespace OFDR_Signal_Generator.Models
{
    public sealed class SyntheticSignalRequest
    {
        public int SampleCount { get; set; }

        public double SampleSpacing { get; set; }

        public int RandomSeed { get; set; }

        public double RawNoiseLevel { get; set; }

        public double RawAttenuation { get; set; }

        public double RawBaselineOffset { get; set; }

        public double ReferenceAmplitude { get; set; }

        public int ReferenceDelaySamples { get; set; }

        public double ReferenceWidth { get; set; }

        public double ReferenceNoiseLevel { get; set; }

        public double ReferenceBaselineOffset { get; set; }

        public double ReferencePhaseShiftDegrees { get; set; }

        public int FftZeroPadFactor { get; set; }

        public bool UseHannWindow { get; set; }

        public int SegmentSize { get; set; }

        public int SegmentHop { get; set; }

        public double ConjugateMultiplyScale { get; set; }

        public bool NormalizeConjugateMultiply { get; set; }

        public int FinalSmoothingWindow { get; set; }

        public double PeakThresholdRatio { get; set; }

        public IList<ReflectionEvent> Events { get; set; }
    }
}
