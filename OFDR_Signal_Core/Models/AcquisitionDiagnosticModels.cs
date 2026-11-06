using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace OFDR_Signal_Core.Models
{
    // These bounded experiment inputs never replace the production recording or its settings.
    public sealed class AcquisitionDiagnosticBlock
    {
        public string SourcePath { get; set; }
        public int StartIndex { get; set; }
        public int PreviousHighEndIndex { get; set; } = int.MinValue;
        public double SampleIntervalSeconds { get; set; }
        public double[] Auxiliary { get; set; }
        public double[] Measurement { get; set; }
        public double[] Trigger { get; set; }
        public double[] SweepTrigger { get; set; }
        public int[] SourceIndices { get; set; }
        public int[] Joins { get; set; } = new int[0];
    }

    [DataContract]
    public sealed class AcquisitionDiagnosticSource
    {
        [DataMember] public string Path { get; set; }
        [DataMember] public string Sha256 { get; set; }
        [DataMember] public long Bytes { get; set; }
        [DataMember] public DateTime LastWriteUtc { get; set; }
        [DataMember] public Dictionary<string, string> Header { get; set; }
        [DataMember] public double SampleIntervalSeconds { get; set; }
        [DataMember] public int DeclaredSamples { get; set; }
        [DataMember] public int NumericSamples { get; set; }
        [DataMember] public int TriggerRowsScanned { get; set; }
        [DataMember] public List<int[]> SourceRanges { get; set; } = new List<int[]>();
        [DataMember] public string Scope { get; set; } = "Bounded numeric windows; full-file bytes hashed; no full-record numeric validation.";
        [IgnoreDataMember] public List<AcquisitionDiagnosticBlock> Blocks { get; set; } = new List<AcquisitionDiagnosticBlock>();
    }

    [DataContract]
    public sealed class AcquisitionSignalMetrics
    {
        [DataMember] public Dictionary<string, double> Values { get; set; }
        [DataMember] public KLinearizationMetrics Phase { get; set; }
        [DataMember] public string Decision { get; set; }
        [DataMember] public string Provider { get; set; }
    }

    [DataContract]
    public sealed class AcquisitionPairReport
    {
        [DataMember] public string Status { get; set; }
        [DataMember] public string Interpretation { get; set; }
        [DataMember] public IndependentOfdrProcessingConfig Config { get; set; }
        [DataMember] public Dictionary<string, double> Metrics { get; set; }
        [DataMember] public double[] DistancesMeters { get; set; }
        [DataMember] public double[] SpectralShiftGHz { get; set; }
        [DataMember] public double[] CorrelationPeaks { get; set; }
        [DataMember] public double[] PeakSeparation { get; set; }
        [DataMember] public bool[] PeakSeparationAvailable { get; set; }
        [DataMember] public bool[] SearchBoundary { get; set; }
        [DataMember] public PreviewSeries ReferenceDistanceFft { get; set; }
        [DataMember] public PreviewSeries SignalDistanceFft { get; set; }
    }
}
