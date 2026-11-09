using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace OFDR_Signal_Core.Models
{
    // Separate from polarization inputs: in this acquisition CH2 is trigger, CH3 is auxiliary.
    public sealed class ModeHopRecording
    {
        public string SourcePath { get; internal set; }
        public string Sha256 { get; internal set; }
        public Dictionary<string, string> Header { get; internal set; }
        public double SourceStartSeconds { get; internal set; }
        public double SampleIntervalSeconds { get; internal set; }
        public double[] Trigger { get; internal set; }
        public double[] SweepTrigger { get; internal set; }
        public double[] Measurement { get; internal set; }
        public int[] InvalidMeasurementIndices { get; internal set; }
        public int InvalidMeasurementCount => InvalidMeasurementIndices?.Length ?? 0;
        private int[] invalidSampleIndices;
        // Union across all supplied channels. The CH4-only fields remain for old callers/metadata.
        public int[] InvalidSampleIndices { get => invalidSampleIndices ?? InvalidMeasurementIndices; internal set => invalidSampleIndices = value; }
        public int InvalidSampleCount => InvalidSampleIndices?.Length ?? 0;
        public int FirstSampleIndex { get; internal set; }
        public bool IsPartialRead { get; internal set; }
        public bool HasFourChannels => SweepTrigger != null && Measurement != null;
        internal List<SweepRange> CompleteSweeps { get; set; }
        internal int? FirstSweepRisingIndex { get; set; }
        public double[] Auxiliary { get; internal set; }
        public int SampleCount => Trigger.Length;
        public double DurationSeconds => SampleCount * SampleIntervalSeconds;
    }

    [DataContract]
    public sealed class ModeHopSettings
    {
        // False preserves historical settings files that do not contain this field.
        [DataMember] public bool ModeHopExclusionBypassed { get; set; }
        [DataMember] public double ThresholdVoltage { get; set; } = 1.8;
        [DataMember] public double ExclusionTailSeconds { get; set; }
        [DataMember] public double ExclusionLeadSeconds { get; set; }
        [DataMember] public double? SweepDurationSeconds { get; set; } = 1.2;
        [DataMember] public double SweepOriginSeconds { get; set; }
        [DataMember] public int MaximumPreviewPoints { get; set; } = 10000;

        public void Validate()
        {
            if (!IsFinite(ThresholdVoltage)) throw new ArgumentException("트리거 문턱값은 유한한 숫자여야 합니다.");
            if (!IsFinite(ExclusionTailSeconds) || ExclusionTailSeconds < 0) throw new ArgumentException("모드홉 종단 추가 제외 시간은 0 이상의 유한한 숫자여야 합니다.");
            if (!IsFinite(ExclusionLeadSeconds) || ExclusionLeadSeconds < 0) throw new ArgumentException("모드홉 시작 전 추가 제외 시간은 0 이상의 유한한 숫자여야 합니다.");
            if (SweepDurationSeconds.HasValue && (!IsFinite(SweepDurationSeconds.Value) || SweepDurationSeconds.Value <= 0))
                throw new ArgumentException("스윕 시간은 양수이거나 비워 두어야 합니다.");
            if (!IsFinite(SweepOriginSeconds) || SweepOriginSeconds < 0) throw new ArgumentException("스윕 시작 오프셋은 0 이상이어야 합니다.");
            if (MaximumPreviewPoints < 4 || MaximumPreviewPoints > 10000) throw new ArgumentException("미리보기 크기는 4~10,000이어야 합니다.");
        }

        public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        public static bool IsRisingEdge(double previous, double current, double threshold) =>
            IsFinite(previous) && IsFinite(current) && previous <= threshold && current > threshold;
    }

    [DataContract]
    public sealed class ModeHopRange
    {
        [DataMember] public int Number { get; set; }
        [DataMember] public int StartIndex { get; set; }
        [DataMember] public int EndIndexExclusive { get; set; }
        [DataMember] public double StartSeconds { get; set; }
        [DataMember] public double EndSeconds { get; set; }
        public int SampleCount => EndIndexExclusive - StartIndex;
        public double StartMilliseconds => StartSeconds * 1000;
        public double EndMilliseconds => EndSeconds * 1000;
        public double DurationMilliseconds => (EndSeconds - StartSeconds) * 1000;
    }

    [DataContract]
    public sealed class ModeHopAnalysis
    {
        [DataMember] public bool ModeHopExclusionBypassed { get; set; }
        [DataMember] public double ExclusionTailSeconds { get; set; }
        [DataMember] public double ExclusionLeadSeconds { get; set; }
        [DataMember] public List<ModeHopRange> IgnoredMeasurementRanges { get; set; }
        [DataMember] public int IgnoredMeasurementSamples { get; set; }
        private List<ModeHopRange> ignoredSampleRanges;
        [DataMember] public List<ModeHopRange> IgnoredSampleRanges { get => ignoredSampleRanges ?? IgnoredMeasurementRanges; set => ignoredSampleRanges = value; }
        [DataMember] public int IgnoredSamples { get; set; }
        [DataMember] public List<ModeHopRange> Ranges { get; set; }
        [DataMember] public long ExcludedSamples { get; set; }
        [DataMember] public double ExcludedSeconds { get; set; }
        [DataMember] public double ExcludedPercent { get; set; }
    }

    [DataContract]
    public sealed class TimedPreview
    {
        [DataMember] public double[] Seconds { get; set; }
        [DataMember] public double[] Values { get; set; }
    }

    [DataContract]
    public sealed class SweepBoundary
    {
        [DataMember] public long Number { get; set; }
        [DataMember] public double Seconds { get; set; }
    }

    [DataContract]
    public sealed class ModeHopViewport
    {
        [DataMember] public double ThresholdVoltage { get; set; }
        [DataMember] public double StartSeconds { get; set; }
        [DataMember] public double EndSeconds { get; set; }
        [DataMember] public TimedPreview Auxiliary { get; set; }
        [DataMember] public TimedPreview Trigger { get; set; }
        [DataMember] public TimedPreview SweepTrigger { get; set; }
        [DataMember] public TimedPreview Measurement { get; set; }
        [DataMember] public List<ModeHopRange> Ranges { get; set; }
        [DataMember] public List<SweepBoundary> SweepBoundaries { get; set; }
        [DataMember] public bool SweepMarkersDecimated { get; set; }
    }
}
