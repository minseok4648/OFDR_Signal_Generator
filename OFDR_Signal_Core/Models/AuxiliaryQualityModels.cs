using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace OFDR_Signal_Core.Models
{
    public sealed class StitchedAuxiliarySignal
    {
        public ModeHopAnalysis Exclusion { get; internal set; }
        public double[] Values { get; internal set; }
        public int[] OriginalIndices { get; internal set; }
        public int[] JoinIndices { get; internal set; }
        public double SampleIntervalSeconds { get; internal set; }
        public double DurationSeconds => Values.Length * SampleIntervalSeconds;
    }

    [DataContract]
    public sealed class AuxiliaryInspectionSettings
    {
        [DataMember] public double StartSeconds { get; set; }
        [DataMember] public double? EndSeconds { get; set; }
        [DataMember] public double MaximumNonmonotonicRatio { get; set; } = 1e-5;
        [DataMember] public int MaximumPreviewPoints { get; set; } = 10000;
        public static int SampleIndexAtOrAfter(double seconds, double intervalSeconds)
        {
            if (!ModeHopSettings.IsFinite(seconds) || seconds < 0 || !ModeHopSettings.IsFinite(intervalSeconds) || intervalSeconds <= 0)
                throw new ArgumentException("샘플 시간과 간격을 확인하세요.");
            var samples = seconds / intervalSeconds;
            // Scale floating-point tolerance with the index; 1e-9 alone is smaller than one ULP above 2^24.
            var tolerance = Math.Max(1e-9, 8 * 2.2204460492503131e-16 * Math.Abs(samples));
            return checked((int)Math.Ceiling(samples - tolerance));
        }
        public void Validate()
        {
            if (!ModeHopSettings.IsFinite(StartSeconds) || StartSeconds < 0 || (EndSeconds.HasValue && (!ModeHopSettings.IsFinite(EndSeconds.Value) || EndSeconds.Value <= StartSeconds))) throw new ArgumentException("접합 신호의 시작·끝 시간을 확인하세요.");
            if (!ModeHopSettings.IsFinite(MaximumNonmonotonicRatio) || MaximumNonmonotonicRatio < 0 || MaximumNonmonotonicRatio > 1) throw new ArgumentException("비단조 비율 한계는 0~1이어야 합니다.");
            if (MaximumPreviewPoints < 4 || MaximumPreviewPoints > 10000) throw new ArgumentException("미리보기 크기는 4~10,000이어야 합니다.");
        }
    }

    [DataContract]
    public sealed class DiagnosticPlotData
    {
        [DataMember] public string Title { get; set; }
        [DataMember] public string XAxis { get; set; }
        [DataMember] public string YAxis { get; set; }
        [DataMember] public double XStart { get; set; }
        [DataMember] public double XEnd { get; set; }
        [DataMember] public TimedPreview Primary { get; set; }
        [DataMember] public TimedPreview Secondary { get; set; }
        [DataMember] public string SecondaryLabel { get; set; }
        [DataMember] public double[] Markers { get; set; }
    }

    [DataContract]
    public sealed class AuxiliaryQualityMetrics
    {
        [DataMember] public string Decision { get; set; }
        [DataMember] public string Reason { get; set; }
        [DataMember] public string Provider { get; set; }
        [DataMember] public int SelectedStartIndex { get; set; }
        [DataMember] public int SelectedEndIndexExclusive { get; set; }
        [DataMember] public double SelectedStartSeconds { get; set; }
        [DataMember] public double SelectedEndSeconds { get; set; }
        [DataMember] public int SourceStartIndex { get; set; }
        [DataMember] public int SourceEndIndexInclusive { get; set; }
        [DataMember] public int JoinCount { get; set; }
        [DataMember] public double MaximumJoinVoltageStep { get; set; }
        [DataMember] public double MaximumJoinPhaseStep { get; set; }
        [DataMember] public KLinearizationMetrics Phase { get; set; }
        [DataMember] public double UniformGridStepRadians { get; set; }
        [DataMember] public double PhaseFitResidualRmsRadians { get; set; }
        [DataMember] public double PhaseFitR2 { get; set; }
        [DataMember] public double? ResampledPhaseResidualRmsRadians { get; set; }
        [DataMember] public KLinearizationMetrics ResampledPhase { get; set; }
        [DataMember] public string Note { get; set; } = "Uniform grid is constructed linear. Phase QC is provisional for this selected concatenated range; source phase vs time need not be linear. Residuals and join steps are diagnostics, not calibrated acceptance limits.";
    }

    [DataContract]
    public sealed class AuxiliaryQualityResult
    {
        [DataMember] public AuxiliaryQualityMetrics Metrics { get; set; }
        [DataMember] public List<DiagnosticPlotData> Plots { get; set; }
    }
}
