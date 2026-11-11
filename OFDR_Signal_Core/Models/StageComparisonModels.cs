using System;
using System.Runtime.Serialization;
using OFDR_Signal_Core.Processing;

namespace OFDR_Signal_Core.Models
{
    public enum AuxiliaryPhaseOrder { IncreasingAnchors, StablePhaseSort }
    public enum RelativePhaseGrid { CommonSpan, IndependentEndpoints, CommonSpanTwoPass }
    public enum DiagnosticWindowPlacement { DistanceSegment, GlobalSignal, None, Both }

    [DataContract]
    public sealed class StageComparisonSettings
    {
        [DataMember] public bool RemoveAuxiliaryMean { get; set; } = true;
        [DataMember] public AuxiliaryPhaseOrder PhaseOrder { get; set; }
        [DataMember] public RelativePhaseGrid Grid { get; set; }
        [DataMember] public DiagnosticWindowPlacement WindowPlacement { get; set; }
        [DataMember] public bool RemoveMeasurementMean { get; set; }
        [DataMember] public bool CenterLocalMagnitudes { get; set; } = true;
        [DataMember] public int OutputSamples { get; set; } = 524288;
        [DataMember] public int AuxiliaryOffset { get; set; }
        [DataMember] public int MeasurementOffset { get; set; }
        [DataMember] public int MaximumLagSamples { get; set; }
        [DataMember] public int StopFftBin { get; set; }
        [DataMember] public int StartFftBin { get; set; }
        [DataMember] public DocumentStageSettings Document { get; set; } = new DocumentStageSettings();
        [OnDeserializing] private void Defaults(StreamingContext context)
        { RemoveAuxiliaryMean = true; CenterLocalMagnitudes = true; OutputSamples = 524288; Document = new DocumentStageSettings(); }
        public void Validate(IndependentOfdrProcessingConfig c, bool physicalWindowResolved = false)
        {
            if (Document == null) throw new ArgumentException("문서 단계 설정이 없습니다.");
            Document.Validate();
            if (Document.Enabled && (Grid == RelativePhaseGrid.IndependentEndpoints || AuxiliaryOffset != 0 || MeasurementOffset != 0))
                throw new ArgumentException("문서 단계는 공통 상대 grid와 기존 AUX/측정 offset 0/0을 사용하세요. 지연은 us 설정으로 적용합니다.");
            if (!Enum.IsDefined(typeof(AuxiliaryPhaseOrder), PhaseOrder) || !Enum.IsDefined(typeof(RelativePhaseGrid), Grid)
                || !Enum.IsDefined(typeof(DiagnosticWindowPlacement), WindowPlacement)) throw new ArgumentException("단계 선택값을 확인하세요.");
            if (c.NotebookReproduction || !c.AllowFailedPhaseQcForDiagnostics || (c.UsePhysicalAxes && !Document.Enabled))
                throw new ArgumentException("단계별 비교는 진단 실행 ON이 필요합니다. 물리축/strain 환산은 문서 단계에서 지원하며 기존 노트북 lag 비교는 물리축 OFF로 사용하세요.");
            if (Document.Enabled && c.UsePhysicalAxes) DocumentPhysicalAxis.Validate(c);
            if (OutputSamples < 8 || OutputSamples > 33554432 || AuxiliaryOffset < 0 || MeasurementOffset < 0
                || c.SegmentSize < 4 || c.SegmentSize > OutputSamples / 2 || c.SegmentHop < 1 || c.SegmentIfftZeroPadFactor < 1
                || c.MaxPreviewPoints < 4 || c.MaxPreviewPoints > 10000
                || MaximumLagSamples < 0 || MaximumLagSamples > ((long)c.SegmentSize * c.SegmentIfftZeroPadFactor) / 2 - 2)
                throw new ArgumentException("단계별 비교의 점수/offset/segment/lag 설정을 확인하세요.");
            var length = (long)c.SegmentSize * c.SegmentIfftZeroPadFactor;
            if (length > (Document.Enabled ? 262144 : 16777216) || (!Document.Enabled && (length & (length - 1)) != 0)) throw new ArgumentException("IFFT 길이를 확인하세요. 문서 단계 최대 262144, 기존 단계는 2의 거듭제곱입니다.");
            var stop = StopFftBin == 0 ? OutputSamples / 2 : StopFftBin;
            var profileCount = Document.Enabled && Document.PhysicalWindow && !physicalWindowResolved
                ? Math.Ceiling((Document.EndMeters - Document.StartMeters) / Document.HopMeters) + 1
                : (stop - StartFftBin - 1L) / c.SegmentHop + 1;
            if (StartFftBin < 0 || stop <= StartFftBin || stop > OutputSamples / 2 || profileCount > 10000)
                throw new ArgumentException("단계별 진단 profile은 최대 10,000점입니다. 종료 bin/이동 간격을 조정하세요.");
        }
        public string Description => "AUX DC " + (RemoveAuxiliaryMean ? "제거" : "유지") + " / " + PhaseOrder + " / " + Grid
            + " / Window " + WindowPlacement + " / N=" + OutputSamples + " / offset=" + AuxiliaryOffset + "/" + MeasurementOffset
            + (Document?.Enabled == true ? " / " + Document.Description : "");
        public string PhaseKey => RemoveAuxiliaryMean + ":" + PhaseOrder + ":" + AuxiliaryOffset + ":" + MeasurementOffset
            + (Document?.Enabled == true ? ":" + Document.PhaseKey : "");
    }
}
