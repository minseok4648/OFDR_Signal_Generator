using OFDR_Signal_Core.Models;
using OFDR_Signal_Generator.Infrastructure;
using OFDR_Signal_Core.Services;

namespace OFDR_Signal_Generator.ViewModels
{
    public sealed class StageOptionsViewModel : ObservableObject
    {
        private bool removeAuxiliaryMean = true, removeMeasurementMean, centerLocalMagnitudes = true;
        private int phaseOrder, grid = 2, window, outputSamples = 524288, auxiliaryOffset, measurementOffset, maximumLag, stopBin;
        public string[] PhaseOrders { get; } = { "취득 순서 증가 anchor", "위상 크기순 정렬 (노트북)" };
        public string[] Grids { get; } = { "공통 상대 span · 직접 보간", "독립 endpoint span (노트북)", "공통 상대 span · 2단계 보간" };
        public string[] Windows { get; } = { "거리 segment", "전체 k 보간 신호 (노트북)", "미적용", "전체 + segment" };
        public bool RemoveAuxiliaryMean { get => removeAuxiliaryMean; set => SetProperty(ref removeAuxiliaryMean, value); }
        public bool RemoveMeasurementMean { get => removeMeasurementMean; set => SetProperty(ref removeMeasurementMean, value); }
        public bool CenterLocalMagnitudes { get => centerLocalMagnitudes; set => SetProperty(ref centerLocalMagnitudes, value); }
        public int PhaseOrderIndex { get => phaseOrder; set => SetProperty(ref phaseOrder, value); }
        public int GridIndex { get => grid; set => SetProperty(ref grid, value); }
        public int WindowIndex { get => window; set => SetProperty(ref window, value); }
        public int OutputSamples { get => outputSamples; set => SetProperty(ref outputSamples, value); }
        public int AuxiliaryOffset { get => auxiliaryOffset; set => SetProperty(ref auxiliaryOffset, value); }
        public int MeasurementOffset { get => measurementOffset; set => SetProperty(ref measurementOffset, value); }
        public int MaximumLagSamples { get => maximumLag; set => SetProperty(ref maximumLag, value); }
        public int StopFftBin { get => stopBin; set => SetProperty(ref stopBin, value); }
        private DocumentStageSettings document = new DocumentStageSettings();
        public DocumentStageSettings Document { get => document; private set => SetProperty(ref document, value); }
        public string[] DocumentDelayModes { get; } = { "지연 정렬 OFF", "수동 시간 (us)", "반사 거리 기반" };
        public string[] DocumentCorrelations { get; } = { "기존 상관 설정", "순환 ZNCC", "유효 중첩 선형 ZNCC" };
        public int DocumentDelayIndex { get => (int)Document.DelayMode; set { Document.DelayMode = (StageDelayMode)value; OnPropertyChanged(); } }
        public int DocumentCorrelationIndex { get => (int)Document.Correlation; set { Document.Correlation = (StageCorrelationMode)value; OnPropertyChanged(); } }
        public StageComparisonSettings Build() => new StageComparisonSettings { RemoveAuxiliaryMean = RemoveAuxiliaryMean,
            PhaseOrder = (AuxiliaryPhaseOrder)PhaseOrderIndex, Grid = (RelativePhaseGrid)GridIndex, WindowPlacement = (DiagnosticWindowPlacement)WindowIndex,
            RemoveMeasurementMean = RemoveMeasurementMean, CenterLocalMagnitudes = CenterLocalMagnitudes, OutputSamples = OutputSamples,
            AuxiliaryOffset = AuxiliaryOffset, MeasurementOffset = MeasurementOffset, MaximumLagSamples = MaximumLagSamples, StopFftBin = StopFftBin,
            Document = JsonContractSerializer.Clone(Document) };
        public void SetDocumentPreset(int stage)
        {
            SetPreset(false); GridIndex = 0; OutputSamples = 65536;
            Document = new DocumentStageSettings { Enabled = true, BandpassEnabled = true, PhysicalWindow = true,
                UseNominalGrid = stage == 0, DelayMode = stage == 2 ? StageDelayMode.ReflectionDistance : StageDelayMode.None };
            OnPropertyChanged(nameof(DocumentDelayIndex)); OnPropertyChanged(nameof(DocumentCorrelationIndex));
        }
        public void SetCableRange()
        {
            var copy = JsonContractSerializer.Clone(Document); copy.PhysicalWindow = true; copy.StartMeters = 0; copy.EndMeters = 15;
            Document = copy;
        }
        public void SetPreset(bool notebook)
        {
            Document = new DocumentStageSettings(); OnPropertyChanged(nameof(DocumentDelayIndex)); OnPropertyChanged(nameof(DocumentCorrelationIndex));
            RemoveAuxiliaryMean = !notebook; PhaseOrderIndex = notebook ? 1 : 0; GridIndex = notebook ? 1 : 2; WindowIndex = notebook ? 1 : 0;
            RemoveMeasurementMean = notebook; CenterLocalMagnitudes = !notebook; OutputSamples = 524288;
            AuxiliaryOffset = notebook ? 40 : 0; MeasurementOffset = notebook ? 1 : 0; MaximumLagSamples = 0; StopFftBin = 0;
        }
    }
}
