using System;
using System.Runtime.Serialization;

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
        [OnDeserializing] private void Defaults(StreamingContext context)
        { RemoveAuxiliaryMean = true; CenterLocalMagnitudes = true; OutputSamples = 524288; }
        public void Validate(IndependentOfdrProcessingConfig c)
        {
            if (!Enum.IsDefined(typeof(AuxiliaryPhaseOrder), PhaseOrder) || !Enum.IsDefined(typeof(RelativePhaseGrid), Grid)
                || !Enum.IsDefined(typeof(DiagnosticWindowPlacement), WindowPlacement)) throw new ArgumentException("단계 선택값을 확인하세요.");
            if (c.NotebookReproduction || !c.AllowFailedPhaseQcForDiagnostics || c.UsePhysicalAxes)
                throw new ArgumentException("단계별 비교는 노트북 고정 모드와 별개이며, 진단 실행 ON / 물리축 OFF가 필요합니다.");
            if (OutputSamples < 8 || OutputSamples > 33554432 || AuxiliaryOffset < 0 || MeasurementOffset < 0
                || c.SegmentSize < 4 || c.SegmentSize > OutputSamples / 2 || c.SegmentHop < 1 || c.SegmentIfftZeroPadFactor < 1
                || c.MaxPreviewPoints < 4 || c.MaxPreviewPoints > 10000
                || MaximumLagSamples < 0 || MaximumLagSamples > ((long)c.SegmentSize * c.SegmentIfftZeroPadFactor) / 2 - 2)
                throw new ArgumentException("단계별 비교의 점수/offset/segment/lag 설정을 확인하세요.");
            var length = (long)c.SegmentSize * c.SegmentIfftZeroPadFactor;
            if (length > 16777216 || (length & (length - 1)) != 0) throw new ArgumentException("단계별 비교의 IFFT 길이는 2의 거듭제곱이어야 합니다.");
            var stop = StopFftBin == 0 ? OutputSamples / 2 : StopFftBin;
            if (stop < 1 || stop > OutputSamples / 2 || (stop - 1L) / c.SegmentHop + 1 > 10000)
                throw new ArgumentException("단계별 진단 profile은 최대 10,000점입니다. 종료 bin/이동 간격을 조정하세요.");
        }
        public string Description => "AUX DC " + (RemoveAuxiliaryMean ? "제거" : "유지") + " / " + PhaseOrder + " / " + Grid
            + " / Window " + WindowPlacement + " / N=" + OutputSamples + " / offset=" + AuxiliaryOffset + "/" + MeasurementOffset;
        public string PhaseKey => RemoveAuxiliaryMean + ":" + PhaseOrder + ":" + AuxiliaryOffset + ":" + MeasurementOffset;
    }
}
