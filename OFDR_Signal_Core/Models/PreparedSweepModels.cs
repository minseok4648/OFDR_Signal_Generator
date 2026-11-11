using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace OFDR_Signal_Core.Models
{
    public enum SweepSelectionMode
    {
        CompleteSweeps,
        RecordingStartToFirstEdge,
        FirstEdgeToRecordingEnd,
        RecordingStartToRecordingEnd
    }

    [DataContract]
    public sealed class SweepRange
    {
        [DataMember] public int Number { get; set; }
        [DataMember] public int StartIndex { get; set; }
        [DataMember] public int EndIndexExclusive { get; set; }
        [DataMember] public double StartSeconds { get; set; }
        [DataMember] public double EndSeconds { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool StartSpecifiedAtRecordStart { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool EndSpecifiedAtRecordEnd { get; set; }
        public int SampleCount => EndIndexExclusive - StartIndex;
        public double DurationSeconds => EndSeconds - StartSeconds;
        public string SelectionDescription => StartSpecifiedAtRecordStart && EndSpecifiedAtRecordEnd ? "기록 시작 → 기록 끝 직접 지정" : EndSpecifiedAtRecordEnd ? "첫 상승 엣지 → 파일 끝 직접 지정" : StartSpecifiedAtRecordStart ? "기록 시작 수동 지정" : "";
        public override string ToString() => string.Format(System.Globalization.CultureInfo.InvariantCulture, "S{0}: {1:F7}–{2:F7} s ({3:N0} samples){4}", Number, StartSeconds, EndSeconds, SampleCount, SelectionDescription.Length > 0 ? " · " + SelectionDescription : "");
    }

    [DataContract]
    public sealed class SavedCsvInputFiles
    {
        [DataMember] public string AuxiliaryCsvPath { get; set; }
        [DataMember] public string AuxiliaryCsvSha256 { get; set; }
        [DataMember] public string MeasurementPath { get; set; }
        [DataMember] public string MeasurementSha256 { get; set; }
        [DataMember] public AuxiliaryCsvExportMetadata OriginalExport { get; set; }
        [DataMember] public bool PartialValidationInput { get; set; }
        [DataMember] public int LoadedRows { get; set; }
        [DataMember] public string Mapping { get; set; } = "CH4 selected at exactly the auxiliary CSV SourceIndex values; original acquisition indices retained.";
    }

    [DataContract]
    public sealed class PreparedSweepMetadata
    {
        [DataMember] public int SchemaVersion { get; set; } = 1;
        [DataMember] public string Role { get; set; }
        [DataMember] public string SourcePath { get; set; }
        [DataMember] public string SourceSha256 { get; set; }
        [DataMember] public Dictionary<string, string> SourceHeader { get; set; }
        [DataMember] public double SourceStartSeconds { get; set; }
        [DataMember] public double SampleIntervalSeconds { get; set; }
        [DataMember] public int AuxiliaryChannel { get; set; } = 3;
        [DataMember] public int MeasurementChannel { get; set; } = 4;
        [DataMember] public string Polarization { get; set; } = "Unspecified";
        [DataMember] public double? KnownDisplacementMm { get; set; }
        [DataMember] public double SweepThresholdVoltage { get; set; } = 1.8;
        [DataMember] public double ModeHopThresholdVoltage { get; set; }
        [DataMember] public double ModeHopExclusionTailSeconds { get; set; }
        [DataMember] public double ModeHopExclusionLeadSeconds { get; set; }
        [DataMember] public bool ModeHopExclusionBypassed { get; set; }
        [DataMember] public SweepRange Sweep { get; set; }
        [DataMember] public List<ModeHopRange> ExcludedRanges { get; set; }
        [DataMember] public List<ModeHopRange> IgnoredMeasurementRanges { get; set; }
        private List<ModeHopRange> ignoredSampleRanges;
        [DataMember] public List<ModeHopRange> IgnoredSampleRanges { get => ignoredSampleRanges ?? IgnoredMeasurementRanges; set => ignoredSampleRanges = value; }
        [DataMember] public int SelectedStartIndex { get; set; }
        [DataMember] public int SelectedEndIndexExclusive { get; set; }
        [DataMember] public int RowCount { get; set; }
        [DataMember] public AuxiliaryQualityMetrics Diagnosis { get; set; }
        [DataMember] public AuxiliaryInspectionSettings DiagnosisSettings { get; set; }
        [DataMember] public string CsvSha256 { get; set; }
        [DataMember] public SavedCsvInputFiles FileInputs { get; set; }
        [DataMember] public string TimeAxis { get; set; } = "Time_s is compressed selected time from zero; SourceTime_s is original acquisition time.";
    }

    public sealed class PreparedSweepData
    {
        public PreparedSweepMetadata Metadata { get; internal set; }
        public double[] Auxiliary { get; internal set; }
        public double[] Measurement { get; internal set; }
        public int[] OriginalIndices { get; internal set; }
        public int SampleCount => Auxiliary.Length;

        public void Validate()
        {
            var m = Metadata;
            if (m == null || m.SchemaVersion != 1 || (m.Role != "Ref" && m.Role != "Signal") || m.AuxiliaryChannel != 3 || m.MeasurementChannel != 4
                || m.Sweep == null || m.Sweep.StartIndex < 0 || m.Sweep.EndIndexExclusive <= m.Sweep.StartIndex
                || (m.Sweep.StartSpecifiedAtRecordStart && m.Sweep.StartIndex != 0)
                || !ModeHopSettings.IsFinite(m.SampleIntervalSeconds) || m.SampleIntervalSeconds <= 0 || !ModeHopSettings.IsFinite(m.SourceStartSeconds)
                || !ModeHopSettings.IsFinite(m.ModeHopThresholdVoltage) || m.SweepThresholdVoltage != 1.8
                || !ModeHopSettings.IsFinite(m.ModeHopExclusionTailSeconds) || m.ModeHopExclusionTailSeconds < 0
                || !ModeHopSettings.IsFinite(m.ModeHopExclusionLeadSeconds) || m.ModeHopExclusionLeadSeconds < 0
                || string.IsNullOrWhiteSpace(m.SourcePath) || m.SourceSha256 == null || m.SourceSha256.Length != 64
                || Auxiliary == null || Measurement == null || OriginalIndices == null || Auxiliary.Length < 4
                || Measurement.Length != Auxiliary.Length || OriginalIndices.Length != Auxiliary.Length || m.RowCount != Auxiliary.Length
                || m.SelectedStartIndex < 0 || m.SelectedEndIndexExclusive - m.SelectedStartIndex != Auxiliary.Length)
                throw new ArgumentException("생성 스윕 데이터와 metadata가 일치하지 않습니다.");
            for (var i = 0; i < Auxiliary.Length; i++)
                if (!ModeHopSettings.IsFinite(Auxiliary[i]) || !ModeHopSettings.IsFinite(Measurement[i]) || OriginalIndices[i] < m.Sweep.StartIndex
                    || OriginalIndices[i] >= m.Sweep.EndIndexExclusive || (i > 0 && OriginalIndices[i] <= OriginalIndices[i - 1]))
                    throw new ArgumentException("생성 스윕의 채널 값 또는 원본 인덱스가 올바르지 않습니다.");
            if (m.ExcludedRanges == null) throw new ArgumentException("제외 구간 추적 정보가 없습니다.");
            var cursor = m.Sweep.StartIndex; var output = 0; var kept = 0;
            foreach (var range in m.ExcludedRanges)
            {
                if (range.StartIndex < cursor || range.EndIndexExclusive <= range.StartIndex || range.EndIndexExclusive > m.Sweep.EndIndexExclusive) throw new ArgumentException("제외 구간 metadata 오류입니다.");
                CheckKept(cursor, range.StartIndex, m.SelectedStartIndex, m.SelectedEndIndexExclusive, ref kept, ref output);
                cursor = range.EndIndexExclusive;
            }
            CheckKept(cursor, m.Sweep.EndIndexExclusive, m.SelectedStartIndex, m.SelectedEndIndexExclusive, ref kept, ref output);
            if (output != Auxiliary.Length || m.SelectedEndIndexExclusive > kept) throw new ArgumentException("선택 범위와 제외 구간의 대응이 다릅니다.");
        }

        private void CheckKept(int first, int end, int selectedFirst, int selectedEnd, ref int kept, ref int output)
        {
            var length = end - first;
            var from = Math.Max(0, selectedFirst - kept); var to = Math.Min(length, selectedEnd - kept);
            for (var offset = from; offset < to; offset++) if (output >= OriginalIndices.Length || OriginalIndices[output++] != first + offset) throw new ArgumentException("CSV 원본 인덱스가 제외/선택 범위와 다릅니다.");
            kept += length;
        }

        public static IndependentOfdrInput ToInput(PreparedSweepData reference, PreparedSweepData signal)
        {
            reference.Validate(); signal.Validate();
            if (reference.Metadata.Role != "Ref" || signal.Metadata.Role != "Signal") throw new ArgumentException("Ref와 Signal 역할을 확인하세요.");
            return new IndependentOfdrInput { Auxiliary = reference.Channel(true), Reference = reference.Channel(false), SignalAuxiliary = signal.Channel(true), Signal = signal.Channel(false) };
        }

        private OfdrChannelData Channel(bool auxiliary)
        {
            var m = Metadata;
            return new OfdrChannelData { SourcePath = m.FileInputs == null ? m.SourcePath : auxiliary ? m.FileInputs.AuxiliaryCsvPath : m.FileInputs.MeasurementPath, SourceType = m.FileInputs == null ? "PreparedSweep" : "SavedDiagnosticCsv", MeasurementId = m.Role + ":" + System.IO.Path.GetFileNameWithoutExtension(m.SourcePath) + (m.FileInputs == null ? ":S" + m.Sweep.Number : ":CSV"),
                Repetition = "S" + m.Sweep.Number, ChannelNumber = auxiliary ? 3 : 4, SampleIntervalSeconds = m.SampleIntervalSeconds,
                Values = auxiliary ? Auxiliary : Measurement, Preparation = m };
        }
    }
}
