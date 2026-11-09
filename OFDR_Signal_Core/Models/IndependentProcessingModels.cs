using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.Serialization;

namespace OFDR_Signal_Core.Models
{
    public sealed class OfdrChannelData
    {
        public string SourcePath { get; set; }
        public string SourceType { get; set; }
        public string MeasurementId { get; set; }
        public double DisplacementMm { get; set; }
        public string Repetition { get; set; }
        public int ChannelNumber { get; set; }
        public double SampleIntervalSeconds { get; set; }
        public double[] Values { get; set; }
        public PreparedSweepMetadata Preparation { get; set; }
    }

    public sealed class IndependentOfdrInput
    {
        public OfdrChannelData Auxiliary { get; set; }
        public OfdrChannelData SignalAuxiliary { get; set; }
        public OfdrChannelData Reference { get; set; }
        public OfdrChannelData Signal { get; set; }
    }

    [DataContract]
    public sealed class IndependentOfdrProcessingConfig
    {
        [DataMember(Order = 1)] public int SchemaVersion { get; set; } = 3;
        [DataMember(Order = 2)] public double MaxAuxNonmonotonicRatio { get; set; } = 1e-5;
        [DataMember(Order = 3)] public string WindowFunction { get; set; } = "Hann";
        [DataMember(Order = 4)] public int SegmentSize { get; set; } = 1024;
        [DataMember(Order = 5)] public int SegmentHop { get; set; } = 256;
        [DataMember(Order = 6)] public int SegmentIfftZeroPadFactor { get; set; } = 16;
        [DataMember(Order = 7)] public double CrossCorrelationScale { get; set; } = 1.0;
        [DataMember(Order = 8)] public bool NormalizeCrossCorrelation { get; set; } = true;
        [DataMember(Order = 9)] public int MaxPreviewPoints { get; set; } = 10000;
        [DataMember(Order = 10)] public bool UsePhysicalAxes { get; set; } = true;
        [DataMember(Order = 11)] public double AuxiliaryPathDifferenceMeters { get; set; } = 18.2703;
        [DataMember(Order = 12)] public double DistanceGeometryFactor { get; set; } = 1.0;
        [DataMember(Order = 13)] public double DistanceOffsetMeters { get; set; } = 0.0;
        [DataMember(Order = 14)] public double CenterWavelengthNm { get; set; } = 1550.0;
        [DataMember(Order = 15)] public double StrainCoefficient { get; set; } = 0.78;
        [DataMember(Order = 16)] public double StrainScaleFactor { get; set; } = 1.0;
        [DataMember(Order = 17)] public double StrainOffsetMicrostrain { get; set; } = 0.0;
        [DataMember(Order = 18)] public double MeasurementStartMeters { get; set; } = 5.5;
        [DataMember(Order = 19)] public double MeasurementEndMeters { get; set; } = 6.5;
        [DataMember(Order = 20)] public double DeformationLengthMeters { get; set; } = 1.04;
        [DataMember(Order = 21)] public double SweepRangeNm { get; set; } = 16.0;
        [DataMember(Order = 22)] public double SweepRateNmPerSecond { get; set; } = 100.0;
        [DataMember(Order = 23)] public double SamplingRateSamplesPerSecond { get; set; } = 25000000.0;
        [DataMember(Order = 24)] public bool RestrictToMeasurementRange { get; set; } = true;
        [DataMember(Order = 25)] public double AuxiliaryGroupIndex { get; set; } = 1.4682;
        [DataMember(Order = 26)] public double MaximumSpectralShiftGHz { get; set; } = 180.0;
        [DataMember(Order = 27)] public double AnalysisStartMeters { get; set; } = 3.5;
        [DataMember(Order = 28)] public double AnalysisEndMeters { get; set; } = 8.7;
        [DataMember(Order = 29)] public double FftDbFloor { get; set; } = -140.0;
        [DataMember(Order = 30)] public bool AllowFailedPhaseQcForDiagnostics { get; set; }
        [DataMember(Order = 31)] public bool NotebookReproduction { get; set; }
        [DataMember(Order = 32)] public int NotebookResampledSamples { get; set; } = 524288;
        [DataMember(Order = 33)] public int NotebookAuxiliarySliceStart { get; set; } = 40;
        [DataMember(Order = 34)] public int NotebookMeasurementSliceStart { get; set; } = 1;
        [DataMember(Order = 35)] public bool StageComparisonEnabled { get; set; }
        [DataMember(Order = 36)] public StageComparisonSettings StageComparison { get; set; } = new StageComparisonSettings();

        [OnDeserializing]
        private void InitializeDefaults(StreamingContext context)
        {
            StageComparison = new StageComparisonSettings();
            NotebookResampledSamples = 524288; NotebookAuxiliarySliceStart = 40; NotebookMeasurementSliceStart = 1;
            SchemaVersion = 3;
            MaxAuxNonmonotonicRatio = 1e-5;
            WindowFunction = "Hann";
            SegmentSize = 1024;
            SegmentHop = 256;
            SegmentIfftZeroPadFactor = 16;
            CrossCorrelationScale = 1.0;
            NormalizeCrossCorrelation = true;
            MaxPreviewPoints = 10000;
            UsePhysicalAxes = true;
            AuxiliaryPathDifferenceMeters = 18.2703;
            DistanceGeometryFactor = 1.0;
            CenterWavelengthNm = 1550.0;
            StrainCoefficient = 0.78;
            StrainScaleFactor = 1.0;
            MeasurementStartMeters = 5.5;
            MeasurementEndMeters = 6.5;
            DeformationLengthMeters = 1.04;
            SweepRangeNm = 16.0;
            SweepRateNmPerSecond = 100.0;
            SamplingRateSamplesPerSecond = 25000000.0;
            RestrictToMeasurementRange = true;
            AuxiliaryGroupIndex = 1.4682;
            MaximumSpectralShiftGHz = 180.0;
            AnalysisStartMeters = 3.5;
            AnalysisEndMeters = 8.7;
            FftDbFloor = -140.0;
        }
    }

    [DataContract]
    public sealed class PhysicalAxisCalibrationResult
    {
        [DataMember(Order = 1)] public bool Enabled { get; set; }
        [DataMember(Order = 2)] public double DistanceMetersPerFftBin { get; set; }
        [DataMember(Order = 3)] public double LocalSpectrumDeltaKRadPerMeter { get; set; }
        [DataMember(Order = 4)] public double StrainMicrostrainPerLag { get; set; }
        [DataMember(Order = 5)] public double? ExpectedAppliedStrainMicrostrain { get; set; }
        [DataMember(Order = 6)] public double LocalSpectrumLength { get; set; }
        [DataMember(Order = 7)] public string Note { get; set; }
        [DataMember(Order = 8)] public double SpectralShiftGHzPerLag { get; set; }
        [DataMember(Order = 9)] public double SweepSpanGHz { get; set; }
        [DataMember(Order = 10)] public double AuxiliaryGroupIndex { get; set; }
    }

    [DataContract]
    public sealed class KLinearizationDiagnostics
    {
        [DataMember(Order = 1)] public PreviewSeries AuxiliaryRaw { get; set; }
        [DataMember(Order = 2)] public PreviewSeries AuxiliaryMeanRemoved { get; set; }
        [DataMember(Order = 3)] public PreviewSeries AnalyticMagnitude { get; set; }
        [DataMember(Order = 4)] public PreviewSeries WrappedPhase { get; set; }
        [DataMember(Order = 5)] public PreviewSeries UnwrappedPhase { get; set; }
        [DataMember(Order = 6)] public PreviewSeries PhaseIncrement { get; set; }
        [DataMember(Order = 7)] public PreviewSeries UniformPhaseGrid { get; set; }
    }

    public sealed class DetailedKLinearizationResult
    {
        public KLinearizationResult Linearized { get; set; }
        public KLinearizationDiagnostics Diagnostics { get; set; }
    }

    public sealed class DualSweepKLinearizationResult
    {
        public double[] Reference { get; set; }
        public double[] Signal { get; set; }
        public KLinearizationMetrics ReferenceMetrics { get; set; }
        public KLinearizationMetrics SignalMetrics { get; set; }
        public KLinearizationDiagnostics ReferenceDiagnostics { get; set; }
        public KLinearizationDiagnostics SignalDiagnostics { get; set; }
        public double CommonPhaseSpanRadians { get; set; }
    }

    [DataContract]
    public sealed class SegmentCorrelationView
    {
        [DataMember(Order = 1)] public int SegmentIndex { get; set; }
        [DataMember(Order = 2)] public int SegmentStartFftBin { get; set; }
        [DataMember(Order = 3)] public PreviewSeries SignalSegment { get; set; }
        [DataMember(Order = 4)] public PreviewSeries ReferenceSegment { get; set; }
        [DataMember(Order = 5)] public PreviewSeries Correlation { get; set; }
        [DataMember(Order = 6)] public PreviewSeries CorrelationZoom { get; set; }
        [DataMember(Order = 7)] public int PeakIndex { get; set; }
        [DataMember(Order = 8)] public int ZeroLagIndex { get; set; }
        [DataMember(Order = 9)] public int PeakLagBin { get; set; }
        [DataMember(Order = 10)] public double PeakMagnitude { get; set; }
        [DataMember(Order = 11)] public int ZoomPeakIndex { get; set; }
        [DataMember(Order = 12)] public PreviewSeries WindowedSignalSegment { get; set; }
        [DataMember(Order = 13)] public PreviewSeries WindowedReferenceSegment { get; set; }
        [DataMember(Order = 14)] public PreviewSeries SignalIfft { get; set; }
        [DataMember(Order = 15)] public PreviewSeries ReferenceIfft { get; set; }
        [DataMember(Order = 16)] public double SegmentCenterDistanceMeters { get; set; }
        [DataMember(Order = 17)] public double PeakStrainMicrostrain { get; set; }
        [DataMember(Order = 18)] public double PeakLagBins { get; set; }
        [DataMember(Order = 19)] public double PeakSpectralShiftGHz { get; set; }
    }

    [DataContract]
    public sealed class IndependentOfdrProcessingResult : IDisposable
    {
        [DataMember(Order = 1)] public string AuxiliaryId { get; set; }
        [DataMember(Order = 2)] public string ReferenceId { get; set; }
        [DataMember(Order = 3)] public string SignalId { get; set; }
        [DataMember(Order = 4)] public string Polarization { get; set; }
        [DataMember(Order = 5)] public int SampleCount { get; set; }
        [DataMember(Order = 6)] public KLinearizationMetrics KLinearization { get; set; }
        [DataMember(Order = 7)] public KLinearizationDiagnostics KDiagnostics { get; set; }
        [DataMember(Order = 8)] public PreviewSeries ReferenceRaw { get; set; }
        [DataMember(Order = 9)] public PreviewSeries SignalRaw { get; set; }
        [DataMember(Order = 10)] public PreviewSeries WindowedReference { get; set; }
        [DataMember(Order = 11)] public PreviewSeries WindowedSignal { get; set; }
        [DataMember(Order = 12)] public PreviewSeries ReferenceSpectrum { get; set; }
        [DataMember(Order = 13)] public PreviewSeries SignalSpectrum { get; set; }
        [DataMember(Order = 14)] public PreviewSeries PeakLagProfile { get; set; }
        [DataMember(Order = 15)] public SegmentCorrelationView SelectedSegment { get; set; }
        [DataMember(Order = 16)] public List<NamedMetric> PerformanceMetrics { get; set; } = new List<NamedMetric>();
        [DataMember(Order = 17)] public string FftProvider { get; set; }
        [DataMember(Order = 18)] public PreviewSeries StrainProfile { get; set; }
        [DataMember(Order = 19)] public PhysicalAxisCalibrationResult PhysicalAxis { get; set; }
        [DataMember(Order = 26)] public bool IsDiagnosticAnalysis { get; set; }
        [DataMember(Order = 27)] public bool InputPhaseQcPassed { get; set; }
        [DataMember(Order = 28)] public string PhaseQcSummary { get; set; }
        [DataMember(Order = 20)] public PreviewSeries PreKReferenceSpectrum { get; set; }
        [DataMember(Order = 21)] public PreviewSeries PreKSignalSpectrum { get; set; }
        [DataMember(Order = 22)] public KLinearizationMetrics SignalKLinearization { get; set; }
        [DataMember(Order = 23)] public KLinearizationDiagnostics SignalKDiagnostics { get; set; }
        [DataMember(Order = 24)] public PreviewSeries SpectralShiftProfile { get; set; }
        [DataMember(Order = 25)] public string SignalAuxiliaryId { get; set; }
        [IgnoreDataMember] public Complex[] ReferenceSpectrumValues { get; set; }
        [IgnoreDataMember] public Complex[] SignalSpectrumValues { get; set; }
        [IgnoreDataMember] public int[] SegmentStarts { get; set; }
        [IgnoreDataMember] public int[] SegmentPeakIndices { get; set; }
        [IgnoreDataMember] public double[] SegmentPeakMagnitudes { get; set; }
        [IgnoreDataMember] public double[] SegmentPeakLags { get; set; }
        [IgnoreDataMember] public double[] SegmentCenterDistancesMeters { get; set; }
        [IgnoreDataMember] public IndependentOfdrProcessingConfig Config { get; set; }
        [IgnoreDataMember] public NotebookTemperatureProfile NotebookProfile { get; set; }
        [IgnoreDataMember] public NotebookTemperatureProfile NotebookReferenceProfile { get; set; }

        public void Dispose()
        {
            ReferenceSpectrumValues = null;
            SignalSpectrumValues = null;
            SegmentStarts = null;
            SegmentPeakIndices = null;
            SegmentPeakMagnitudes = null;
            SegmentPeakLags = null;
            SegmentCenterDistancesMeters = null;
        }
    }

    [DataContract]
    public sealed class ChannelCsvMetadata
    {
        [DataMember(Order = 1)] public int SchemaVersion { get; set; } = 1;
        [DataMember(Order = 2)] public string MeasurementId { get; set; }
        [DataMember(Order = 3)] public double DisplacementMm { get; set; }
        [DataMember(Order = 4)] public string Repetition { get; set; }
        [DataMember(Order = 5)] public int ChannelNumber { get; set; }
        [DataMember(Order = 6)] public long RowCount { get; set; }
        [DataMember(Order = 7)] public string CsvFileName { get; set; }
        [DataMember(Order = 8)] public string CsvSha256 { get; set; }
        [DataMember(Order = 9)] public DateTime ConvertedUtc { get; set; }
        [DataMember(Order = 10)] public string ConverterVersion { get; set; }
        [DataMember(Order = 11)] public IsfSourceMetadata Source { get; set; }
    }

    public sealed class ChannelConversionResult
    {
        public string CsvPath { get; set; }
        public string MetadataPath { get; set; }
        public int RowCount { get; set; }
        public bool Skipped { get; set; }
        public TimeSpan Elapsed { get; set; }
    }
}
