using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.Serialization;

namespace OFDR_Signal_Core.Models
{
    [DataContract]
    public sealed class OfdrProcessingConfig
    {
        [DataMember(Order = 1)] public int SchemaVersion { get; set; } = 1;
        [DataMember(Order = 2)] public bool EnableKLinearization { get; set; } = true;
        [DataMember(Order = 3)] public double MaxAuxNonmonotonicRatio { get; set; } = 1e-5;
        [DataMember(Order = 4)] public bool UseHannWindow { get; set; } = true;
        [DataMember(Order = 5)] public int SegmentSize { get; set; } = 64;
        [DataMember(Order = 6)] public int SegmentHop { get; set; } = 32;
        [DataMember(Order = 7)] public int SegmentIfftZeroPadFactor { get; set; } = 2;
        [DataMember(Order = 8)] public double ConjugateMultiplyScale { get; set; } = 1.0;
        [DataMember(Order = 9)] public bool NormalizeConjugateMultiply { get; set; } = true;
        [DataMember(Order = 10)] public string FinalMetricMode { get; set; } = "Absolute Peak Offset";
        [DataMember(Order = 11)] public int FinalSmoothingWindow { get; set; } = 7;
        [DataMember(Order = 12)] public double PeakThresholdRatio { get; set; } = 0.34;
        [DataMember(Order = 13)] public int MaxPreviewPoints { get; set; } = 10000;
    }

    [DataContract]
    public sealed class NamedMetric
    {
        [DataMember(Order = 1)] public string Name { get; set; }
        [DataMember(Order = 2)] public double Value { get; set; }
        [DataMember(Order = 3)] public string Unit { get; set; }
    }

    [DataContract]
    public sealed class KLinearizationMetrics
    {
        [DataMember(Order = 1)] public double PhaseSpanRadians { get; set; }
        [DataMember(Order = 2)] public double MedianPhaseIncrementRadians { get; set; }
        [DataMember(Order = 3)] public double NonmonotonicRatio { get; set; }
        [DataMember(Order = 4)] public int AcceptedAnchorCount { get; set; }
        [DataMember(Order = 5)] public int SampleCount { get; set; }
        [DataMember(Order = 6)] public int SweepDirection { get; set; }
    }

    public sealed class KLinearizationResult
    {
        public double[] Reference { get; set; }
        public double[] XPolarization { get; set; }
        public double[] YPolarization { get; set; }
        public KLinearizationMetrics Metrics { get; set; }
    }

    [DataContract]
    public sealed class PreviewSeries
    {
        [DataMember(Order = 1)] public double[] Values { get; set; }
        [DataMember(Order = 2)] public double XStart { get; set; }
        [DataMember(Order = 3)] public double XEnd { get; set; }
        [DataMember(Order = 4)] public string XAxisTitle { get; set; }
        [DataMember(Order = 5)] public string YAxisTitle { get; set; }
    }

    [DataContract]
    public sealed class DetectedFeature
    {
        [DataMember(Order = 1)] public int PositionIndex { get; set; }
        [DataMember(Order = 2)] public double Value { get; set; }
    }

    [DataContract]
    public sealed class PolarizationProcessingResult
    {
        [DataMember(Order = 1)] public string Polarization { get; set; }
        [DataMember(Order = 2)] public int SampleCount { get; set; }
        [DataMember(Order = 3)] public int SegmentCount { get; set; }
        [DataMember(Order = 4)] public int SegmentIfftLength { get; set; }
        [IgnoreDataMember] public int[] SegmentStartIndices { get; set; }
        [IgnoreDataMember] public int[] SegmentCenterIndices { get; set; }
        [IgnoreDataMember] public int[] PeakIndices { get; set; }
        [IgnoreDataMember] public double[] RepresentativeValues { get; set; }
        [IgnoreDataMember] public double[] FinalProfile { get; set; }
        [DataMember(Order = 10)] public PreviewSeries SignalPreview { get; set; }
        [DataMember(Order = 11)] public PreviewSeries SpectrumPreview { get; set; }
        [DataMember(Order = 12)] public PreviewSeries FinalProfilePreview { get; set; }
        [DataMember(Order = 13)] public List<DetectedFeature> DetectedFeatures { get; set; } = new List<DetectedFeature>();
        [DataMember(Order = 14)] public List<NamedMetric> Metrics { get; set; } = new List<NamedMetric>();
        [IgnoreDataMember] public Complex[] Spectrum { get; set; }
    }

    public sealed class SegmentInspection
    {
        public int SegmentIndex { get; set; }
        public int StartIndex { get; set; }
        public int CenterIndex { get; set; }
        public Complex[] RawSegment { get; set; }
        public Complex[] ReferenceSegment { get; set; }
        public Complex[] ConjugateMultiply { get; set; }
        public Complex[] ShiftedIfft { get; set; }
        public int PeakIndex { get; set; }
        public int ZeroLagIndex { get; set; }
    }

    [DataContract]
    public sealed class OfdrSetProcessingResult : IDisposable
    {
        [DataMember(Order = 1)] public string SetId { get; set; }
        [DataMember(Order = 2)] public KLinearizationMetrics KLinearization { get; set; }
        [DataMember(Order = 3)] public PreviewSeries ReferencePreview { get; set; }
        [DataMember(Order = 4)] public PolarizationProcessingResult XPolarization { get; set; }
        [DataMember(Order = 5)] public PolarizationProcessingResult YPolarization { get; set; }
        [DataMember(Order = 6)] public List<NamedMetric> PerformanceMetrics { get; set; } = new List<NamedMetric>();
        [DataMember(Order = 7)] public string FftProvider { get; set; }
        [DataMember(Order = 8)] public PreviewSeries ReferenceSpectrumPreview { get; set; }
        [IgnoreDataMember] public Complex[] ReferenceSpectrum { get; set; }
        [IgnoreDataMember] public OfdrProcessingConfig Config { get; set; }

        public void Dispose()
        {
            ReferenceSpectrum = null;
            if (XPolarization != null) XPolarization.Spectrum = null;
            if (YPolarization != null) YPolarization.Spectrum = null;
        }
    }
}
