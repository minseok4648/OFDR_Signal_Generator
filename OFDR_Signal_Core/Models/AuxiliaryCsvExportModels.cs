using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace OFDR_Signal_Core.Models
{
    [DataContract]
    public sealed class AuxiliaryCsvExportRequest
    {
        [DataMember] public string CsvPath { get; set; }
        [DataMember] public bool EntireStitchedSignal { get; set; }
        [DataMember] public int StartIndex { get; set; }
        [DataMember] public int EndIndexExclusive { get; set; }
        [DataMember] public bool Overwrite { get; set; }
    }
    [DataContract]
    public sealed class AuxiliaryCsvExportMetadata
    {
        [DataMember] public int SchemaVersion { get; set; } = 1;
        [DataMember] public string SignalKind { get; set; } = "Uncorrected concatenated CH3 auxiliary, before k-linearization";
        [DataMember] public string CsvColumns { get; set; } = "Index,Time_s,Voltage_V,SourceIndex,SourceTime_s,IsJoin";
        [DataMember] public string TimeAxis { get; set; } = "Index and Time_s retain full stitched-record coordinates; removed time is compressed; no rebasing. SourceTime_s retains the original acquisition time.";
        [DataMember] public string JoinRule { get; set; } = "IsJoin=1 at a discontinuity in original sample indices in the full stitched signal, including an exported first row if it is a global join.";
        [DataMember] public string SourcePath { get; set; }
        [DataMember] public string SourceSha256 { get; set; }
        [DataMember] public Dictionary<string, string> SourceHeader { get; set; }
        [DataMember] public double SourceStartSeconds { get; set; }
        [DataMember] public double SampleIntervalSeconds { get; set; }
        [DataMember] public double ThresholdVoltage { get; set; }
        [DataMember] public bool ModeHopExclusionBypassed { get; set; }
        [DataMember] public double ExclusionTailSeconds { get; set; }
        [DataMember] public double ExclusionLeadSeconds { get; set; }
        [DataMember] public List<ModeHopRange> IgnoredMeasurementRanges { get; set; }
        private List<ModeHopRange> ignoredSampleRanges;
        [DataMember] public List<ModeHopRange> IgnoredSampleRanges { get => ignoredSampleRanges ?? IgnoredMeasurementRanges; set => ignoredSampleRanges = value; }
        [DataMember] public bool EntireStitchedSignal { get; set; }
        [DataMember] public int StartIndex { get; set; }
        [DataMember] public int EndIndexExclusive { get; set; }
        [DataMember] public int RowCount { get; set; }
        [DataMember] public int JoinRows { get; set; }
        [DataMember] public string CsvSha256 { get; set; }
        [DataMember] public DateTime ExportedUtc { get; set; }
        [DataMember] public AuxiliaryQualityMetrics LastDiagnosis { get; set; }
        [DataMember] public AuxiliaryInspectionSettings LastDiagnosisSettings { get; set; }
    }

    [DataContract]
    public sealed class AuxiliaryCsvExportResult
    {
        [DataMember] public string CsvPath { get; set; }
        [DataMember] public string MetadataPath { get; set; }
        [DataMember] public AuxiliaryCsvExportMetadata Metadata { get; set; }
        [DataMember] public double ElapsedSeconds { get; set; }
    }
}
