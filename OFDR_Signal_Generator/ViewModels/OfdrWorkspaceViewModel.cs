using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;
using OFDR_Signal_Generator.Infrastructure;
using WinForms = System.Windows.Forms;

namespace OFDR_Signal_Generator.ViewModels
{
    public sealed class OfdrWorkspaceViewModel : ObservableObject, IDisposable
    {
        public ModeHopPreviewViewModel ModeHopPreview { get; } = new ModeHopPreviewViewModel();
        private const string DefaultDataRoot = @"C:\Project\OFDR\Datas\OFDR Raw data";
        private readonly OfdrChannelFileReader channelReader = new OfdrChannelFileReader();
        private readonly OfdrChannelCsvConverter channelConverter = new OfdrChannelCsvConverter();
        private CancellationTokenSource cancellation;
        private IndependentOfdrProcessingEngine engine;
        private IndependentOfdrInput preparedInput;
        private string[] legacyPaths;
        private string legacyPolarization;
        private TaskCompletionSource<bool> operationIdle;
        private int workspaceSelectedTab;
        public bool IsLegacyInput => preparedInput == null;
        public string ProcessingQcBlockReason => preparedInput == null ? null : PreparedSweepQcPolicy.InputBlockReason(preparedInput, MaxNonmonotonicRatio);
        public bool IsProcessingQcBlocked => ProcessingQcBlockReason != null;
        public string ProcessingReadiness => preparedInput == null ? "" : IsProcessingQcBlocked ? "전체 처리 시작 차단\n" + ProcessingQcBlockReason + "\n해당 역할의 구간을 조정하고 다시 진단하세요."
            : "확정된 Ref/Signal CH3 진단이 현재 분석 한계를 통과했습니다.";
        public string ReferenceAuxiliaryLabel => IsLegacyInput ? "기준 sweep 보조간섭계 · Ref와 동시 취득한 CH1" : "기준 sweep 보조간섭계 · 선택 Ref의 CH3";
        public string SignalAuxiliaryLabel => IsLegacyInput ? "측정 sweep 보조간섭계 · Sig와 동시 취득한 CH1" : "측정 sweep 보조간섭계 · 선택 Signal의 CH3";
        public int WorkspaceSelectedTab { get => workspaceSelectedTab; set => SetProperty(ref workspaceSelectedTab, value); }
        public RelayCommand UseLegacyInputsCommand { get; }
        private IndependentOfdrProcessingResult processingResult;
        private SegmentCorrelationView selectedSegment;
        private string auxiliaryPath;
        private string signalAuxiliaryPath;
        private string referencePath;
        private string signalPath;
        private string channelCsvOutputFolder;
        private string selectedPolarization = "CH2 / Xpol";
        private string selectedWindowFunction = "Hann";
        private int segmentSize = 1024;
        private int segmentHop = 256;
        private int zeroPadFactor = 16;
        private double correlationScale = 1.0;
        private bool normalizeCorrelation = true;
        private double maxNonmonotonicRatio = 1e-5;
        private int maxPreviewPoints = 10000;
        private bool usePhysicalAxes = true;
        private double auxiliaryPathDifferenceMeters = 18.2703;
        private double auxiliaryGroupIndex = 1.4682;
        private double maximumSpectralShiftGHz = 180.0;
        private double distanceGeometryFactor = 1.0;
        private double distanceOffsetMeters;
        private double centerWavelengthNm = 1550.0;
        private double strainCoefficient = 0.78;
        private double strainScaleFactor = 1.0;
        private double strainOffsetMicrostrain;
        private double measurementStartMeters = 5.5;
        private double measurementEndMeters = 6.5;
        private double analysisStartMeters = 3.5;
        private double analysisEndMeters = 8.7;
        private double deformationLengthMeters = 1.04;
        private double sweepRangeNm = 16.0;
        private double sweepRateNmPerSecond = 100.0;
        private double samplingRateSamplesPerSecond = 25000000.0;
        private bool restrictToMeasurementRange = true;
        private int selectedSegmentIndex;
        private string selectedLargeGraph = "1. Raw 데이터";
        private bool isBusy;
        private double progressPercent;
        private string progressText = "입력 파일을 선택한 뒤 전체 처리를 실행하세요.";
        private string inputValidationText = "기준 CH1은 Ref와, 측정 CH1은 Sig와 같은 변형량·반복 파일을 선택합니다.";

        public OfdrWorkspaceViewModel()
        {
            AuxiliaryPath = ExistingOrEmpty(Path.Combine(DefaultDataRoot, "0_0mm0000CH1.isf"));
            SignalAuxiliaryPath = ExistingOrEmpty(Path.Combine(DefaultDataRoot, "0_4mm0000CH1.isf"));
            ReferencePath = ExistingOrEmpty(Path.Combine(DefaultDataRoot, "0_0mm0000CH2.isf"));
            SignalPath = ExistingOrEmpty(Path.Combine(DefaultDataRoot, "0_4mm0000CH2.isf"));
            ChannelCsvOutputFolder = Path.Combine(DefaultDataRoot, "converted_channels");
            BrowseAuxiliaryCommand = new RelayCommand(() => BrowseInput("기준 sweep 보조간섭계 CH1 파일 선택", value => AuxiliaryPath = value), () => !IsBusy);
            BrowseSignalAuxiliaryCommand = new RelayCommand(() => BrowseInput("측정 sweep 보조간섭계 CH1 파일 선택", value => SignalAuxiliaryPath = value), () => !IsBusy);
            BrowseReferenceCommand = new RelayCommand(() => BrowseInput("Ref 파일 선택", value => { ReferencePath = value; DetectPolarization(value); SetCompanionAuxiliary(value, false); }), () => !IsBusy);
            BrowseSignalCommand = new RelayCommand(() => BrowseInput("Sig 파일 선택", value => { SignalPath = value; DetectPolarization(value); SetCompanionAuxiliary(value, true); }), () => !IsBusy);
            BrowseOutputFolderCommand = new RelayCommand(BrowseOutputFolder, () => !IsBusy);
            ConvertAuxiliaryCommand = new RelayCommand(() => ConvertInputAsync(InputRole.Auxiliary), () => CanConvert(AuxiliaryPath));
            ConvertSignalAuxiliaryCommand = new RelayCommand(() => ConvertInputAsync(InputRole.SignalAuxiliary), () => CanConvert(SignalAuxiliaryPath));
            ConvertReferenceCommand = new RelayCommand(() => ConvertInputAsync(InputRole.Reference), () => CanConvert(ReferencePath));
            ConvertSignalCommand = new RelayCommand(() => ConvertInputAsync(InputRole.Signal), () => CanConvert(SignalPath));
            RunProcessingCommand = new RelayCommand(() => { _ = RunProcessingAsync(); }, () => !IsBusy && !ModeHopPreview.IsBusy && InputsSelected() && !IsProcessingQcBlocked);
            UseLegacyInputsCommand = new RelayCommand(ClearPreparedInput, () => !IsBusy);
            ModeHopPreview.SweepPreparation.WorkspaceIdle = () => !IsBusy;
            ModeHopPreview.SweepPreparation.ProcessingLimit = () => MaxNonmonotonicRatio;
            ModeHopPreview.SweepPreparation.ApplyRequested += ApplyPreparedSweeps;
            ModeHopPreview.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(ModeHopPreview.IsBusy)) RunProcessingCommand.RaiseCanExecuteChanged(); };
            CancelCommand = new RelayCommand(Cancel, () => IsBusy);
            InspectSegmentCommand = new RelayCommand(InspectSegment, () => !IsBusy && ProcessingResult != null);
        }

        public RelayCommand BrowseAuxiliaryCommand { get; private set; }
        public RelayCommand BrowseSignalAuxiliaryCommand { get; private set; }
        public RelayCommand BrowseReferenceCommand { get; private set; }
        public RelayCommand BrowseSignalCommand { get; private set; }
        public RelayCommand BrowseOutputFolderCommand { get; private set; }
        public RelayCommand ConvertAuxiliaryCommand { get; private set; }
        public RelayCommand ConvertSignalAuxiliaryCommand { get; private set; }
        public RelayCommand ConvertReferenceCommand { get; private set; }
        public RelayCommand ConvertSignalCommand { get; private set; }
        public RelayCommand RunProcessingCommand { get; private set; }
        public RelayCommand CancelCommand { get; private set; }
        public RelayCommand InspectSegmentCommand { get; private set; }
        public IReadOnlyList<string> PolarizationOptions => IsLegacyInput ? new[] { "CH2 / Xpol", "CH3 / Ypol" } : new[] { "CH4 / 편광 미지정" };
        public IReadOnlyList<string> WindowFunctions { get; } = IndependentOfdrProcessingEngine.SupportedWindows;
        public IReadOnlyList<string> LargeGraphOptions { get; } = new[]
        {
            "1. Raw 데이터",
            "2. FFT (K 보정 전)",
            "3. FFT 거리 데이터 (K 보정 후)",
            "4. 선택 Segment",
            "5. Window 적용 Segment",
            "6. IFFT 국부 스펙트럼",
            "7. Cross Correlation",
            "8. Cross Correlation Peak 확대",
            "9. 거리별 Rayleigh Spectral Shift",
            "10. 거리별 Strain 결과"
        };

        public string AuxiliaryPath { get { return auxiliaryPath; } set { if (SetProperty(ref auxiliaryPath, value)) RaiseCommandStates(); } }
        public string SignalAuxiliaryPath { get { return signalAuxiliaryPath; } set { if (SetProperty(ref signalAuxiliaryPath, value)) RaiseCommandStates(); } }
        public string ReferencePath { get { return referencePath; } set { if (SetProperty(ref referencePath, value)) RaiseCommandStates(); } }
        public string SignalPath { get { return signalPath; } set { if (SetProperty(ref signalPath, value)) RaiseCommandStates(); } }
        public string ChannelCsvOutputFolder { get { return channelCsvOutputFolder; } set { SetProperty(ref channelCsvOutputFolder, value); } }
        public string SelectedPolarization { get { return selectedPolarization; } set { SetProperty(ref selectedPolarization, value); } }
        public string SelectedWindowFunction { get { return selectedWindowFunction; } set { SetProperty(ref selectedWindowFunction, value); } }
        public int SegmentSize { get { return segmentSize; } set { SetProperty(ref segmentSize, value); } }
        public int SegmentHop { get { return segmentHop; } set { SetProperty(ref segmentHop, value); } }
        public int ZeroPadFactor { get { return zeroPadFactor; } set { SetProperty(ref zeroPadFactor, value); } }
        public double CorrelationScale { get { return correlationScale; } set { SetProperty(ref correlationScale, value); } }
        public bool NormalizeCorrelation { get { return normalizeCorrelation; } set { SetProperty(ref normalizeCorrelation, value); } }
        public double MaxNonmonotonicRatio { get { return maxNonmonotonicRatio; } set { if (SetProperty(ref maxNonmonotonicRatio, value)) RefreshPreparedQc(); } }
        public int MaxPreviewPoints { get { return maxPreviewPoints; } set { SetProperty(ref maxPreviewPoints, value); } }
        public bool UsePhysicalAxes { get { return usePhysicalAxes; } set { SetProperty(ref usePhysicalAxes, value); } }
        public double AuxiliaryPathDifferenceMeters { get { return auxiliaryPathDifferenceMeters; } set { SetProperty(ref auxiliaryPathDifferenceMeters, value); } }
        public double AuxiliaryGroupIndex { get { return auxiliaryGroupIndex; } set { SetProperty(ref auxiliaryGroupIndex, value); } }
        public double MaximumSpectralShiftGHz { get { return maximumSpectralShiftGHz; } set { SetProperty(ref maximumSpectralShiftGHz, value); } }
        public double DistanceGeometryFactor { get { return distanceGeometryFactor; } set { SetProperty(ref distanceGeometryFactor, value); } }
        public double DistanceOffsetMeters { get { return distanceOffsetMeters; } set { SetProperty(ref distanceOffsetMeters, value); } }
        public double CenterWavelengthNm { get { return centerWavelengthNm; } set { SetProperty(ref centerWavelengthNm, value); } }
        public double StrainCoefficient { get { return strainCoefficient; } set { SetProperty(ref strainCoefficient, value); } }
        public double StrainScaleFactor { get { return strainScaleFactor; } set { SetProperty(ref strainScaleFactor, value); } }
        public double StrainOffsetMicrostrain { get { return strainOffsetMicrostrain; } set { SetProperty(ref strainOffsetMicrostrain, value); } }
        public double MeasurementStartMeters { get { return measurementStartMeters; } set { SetProperty(ref measurementStartMeters, value); } }
        public double MeasurementEndMeters { get { return measurementEndMeters; } set { SetProperty(ref measurementEndMeters, value); } }
        public double AnalysisStartMeters { get { return analysisStartMeters; } set { SetProperty(ref analysisStartMeters, value); } }
        public double AnalysisEndMeters { get { return analysisEndMeters; } set { SetProperty(ref analysisEndMeters, value); } }
        public double DeformationLengthMeters { get { return deformationLengthMeters; } set { SetProperty(ref deformationLengthMeters, value); } }
        public double SweepRangeNm { get { return sweepRangeNm; } set { SetProperty(ref sweepRangeNm, value); } }
        public double SweepRateNmPerSecond { get { return sweepRateNmPerSecond; } set { SetProperty(ref sweepRateNmPerSecond, value); } }
        public double SamplingRateSamplesPerSecond { get { return samplingRateSamplesPerSecond; } set { SetProperty(ref samplingRateSamplesPerSecond, value); } }
        public bool RestrictToMeasurementRange { get { return restrictToMeasurementRange; } set { SetProperty(ref restrictToMeasurementRange, value); } }
        public double SamplingRateMsps
        {
            get { return samplingRateSamplesPerSecond / 1e6; }
            set { if (SetProperty(ref samplingRateSamplesPerSecond, value * 1e6)) OnPropertyChanged(nameof(SamplingRateSamplesPerSecond)); }
        }
        public int SelectedSegmentIndex { get { return selectedSegmentIndex; } set { SetProperty(ref selectedSegmentIndex, value); } }
        public string SelectedLargeGraph { get { return selectedLargeGraph; } set { if (SetProperty(ref selectedLargeGraph, value)) RaiseLargeGraphProperties(); } }
        public bool IsBusy { get { return isBusy; } private set { if (SetProperty(ref isBusy, value)) RaiseCommandStates(); } }
        public double ProgressPercent { get { return progressPercent; } private set { SetProperty(ref progressPercent, value); } }
        public string ProgressText { get { return progressText; } private set { SetProperty(ref progressText, value); } }
        public string InputValidationText { get { return inputValidationText; } private set { SetProperty(ref inputValidationText, value); } }
        public IndependentOfdrProcessingResult ProcessingResult { get { return processingResult; } private set { if (SetProperty(ref processingResult, value)) { OnPropertyChanged(nameof(HasResult)); OnPropertyChanged(nameof(CalibrationSummary)); RaiseLargeGraphProperties(); RaiseCommandStates(); } } }
        public bool HasResult { get { return ProcessingResult != null; } }
        public SegmentCorrelationView SelectedSegment { get { return selectedSegment; } private set { if (SetProperty(ref selectedSegment, value)) { OnPropertyChanged(nameof(PeakDescription)); OnPropertyChanged(nameof(CorrelationPeakMarkers)); OnPropertyChanged(nameof(CorrelationZoomPeakMarkers)); RaiseLargeGraphProperties(); } } }
        public IEnumerable<int> CorrelationPeakMarkers { get { return SelectedSegment == null ? new int[0] : new[] { SelectedSegment.PeakIndex }; } }
        public IEnumerable<int> CorrelationZoomPeakMarkers { get { return SelectedSegment == null ? new int[0] : new[] { SelectedSegment.ZoomPeakIndex }; } }
        public string PeakDescription
        {
            get
            {
                return SelectedSegment == null ? "Segment 처리 후 peak 정보가 표시됩니다."
                    : string.Format(CultureInfo.InvariantCulture, "Segment {0:N0} · 중심 {1:F6} m · Peak {2:+0.000;-0.000;0.000} bin · {3:+0.000;-0.000;0.000} GHz · {4:+0.0;-0.0;0.0} με · 상관 {5:G6}",
                        SelectedSegment.SegmentIndex, SelectedSegment.SegmentCenterDistanceMeters, SelectedSegment.PeakLagBins,
                        SelectedSegment.PeakSpectralShiftGHz, SelectedSegment.PeakStrainMicrostrain, SelectedSegment.PeakMagnitude);
            }
        }

        public string CalibrationSummary
        {
            get
            {
                if (ProcessingResult == null || ProcessingResult.PhysicalAxis == null)
                    return "거리/Strain 환산은 처리 후 표시됩니다. 중심 파장 1550 nm와 strain 계수 0.78은 장비·광섬유 값으로 확인하세요.";
                var value = ProcessingResult.PhysicalAxis;
                return string.Format(CultureInfo.InvariantCulture,
                    "거리 간격 {0:G9} m/FFT bin · Spectral shift {1:G9} GHz/lag · Strain {2:G9} με/lag · sweep {3:F3} GHz · 인가 변형 기준 {4:F3} με",
                    value.DistanceMetersPerFftBin, value.SpectralShiftGHzPerLag, value.StrainMicrostrainPerLag, value.SweepSpanGHz, value.ExpectedAppliedStrainMicrostrain.HasValue ? value.ExpectedAppliedStrainMicrostrain.Value.ToString("F3", CultureInfo.InvariantCulture) : "미지정");
            }
        }

        public IEnumerable<double> LargeGraphPrimaryValues { get { var series = GetLargeGraphPrimary(); return series == null || series.Values == null ? new double[0] : series.Values; } }
        public IEnumerable<double> LargeGraphSecondaryValues { get { var series = GetLargeGraphSecondary(); return series == null || series.Values == null ? new double[0] : series.Values; } }
        public IEnumerable<int> LargeGraphMarkers
        {
            get
            {
                if (SelectedSegment == null) return new int[0];
                if (SelectedLargeGraph.StartsWith("7.", StringComparison.Ordinal)) return new[] { SelectedSegment.PeakIndex };
                if (SelectedLargeGraph.StartsWith("8.", StringComparison.Ordinal)) return new[] { SelectedSegment.ZoomPeakIndex };
                return new int[0];
            }
        }
        public double LargeGraphXStart { get { var series = GetLargeGraphPrimary(); return series == null ? 0.0 : series.XStart; } }
        public double LargeGraphXEnd { get { var series = GetLargeGraphPrimary(); return series == null ? 1.0 : series.XEnd; } }
        public string LargeGraphXAxisTitle { get { var series = GetLargeGraphPrimary(); return series == null ? "X" : series.XAxisTitle; } }
        public string LargeGraphYAxisTitle { get { var series = GetLargeGraphPrimary(); return series == null ? "Y" : series.YAxisTitle; } }

        public void ApplyPreparedSweeps(PreparedSweepData reference, PreparedSweepData signal)
        {
            if (IsBusy) return;
            var block = PreparedSweepQcPolicy.PairBlockReason(reference?.Metadata, signal?.Metadata, MaxNonmonotonicRatio);
            if (block != null) { ReportQcBlock("apply_input", reference?.Metadata, signal?.Metadata, block); return; }
            var input = PreparedSweepData.ToInput(reference, signal);
            IndependentOfdrProcessingEngine.ValidateInput(input);
            if (preparedInput == null) { legacyPaths = new[] { AuxiliaryPath, SignalAuxiliaryPath, ReferencePath, SignalPath }; legacyPolarization = SelectedPolarization; }
            preparedInput = input;
            OnPropertyChanged(nameof(PolarizationOptions)); SelectedPolarization = "CH4 / 편광 미지정";
            AuxiliaryPath = ReferencePath = reference.Metadata.SourcePath;
            SignalAuxiliaryPath = SignalPath = signal.Metadata.SourcePath;
            SamplingRateSamplesPerSecond = 1.0 / reference.Metadata.SampleIntervalSeconds;
            OnPropertyChanged(nameof(SamplingRateMsps));
            InputValidationText = string.Format(CultureInfo.InvariantCulture, "생성 입력: Ref S{0} ({1:N0}) / Signal S{2} ({3:N0}) · CH3 보조간섭계 / CH4 측정 · 편광·변형량 미지정. QC: {4} / {5}", reference.Metadata.Sweep.Number, reference.SampleCount, signal.Metadata.Sweep.Number, signal.SampleCount, reference.Metadata.Diagnosis?.Decision, signal.Metadata.Diagnosis?.Decision);
            ProcessingResult?.Dispose(); ProcessingResult = null; SelectedSegment = null;
            OnPropertyChanged(nameof(IsLegacyInput)); RaiseCommandStates(); WorkspaceSelectedTab = 1;
            OnPropertyChanged(nameof(ReferenceAuxiliaryLabel)); OnPropertyChanged(nameof(SignalAuxiliaryLabel));
            RefreshPreparedQc();
        }

        private void RefreshPreparedQc()
        {
            OnPropertyChanged(nameof(ProcessingQcBlockReason)); OnPropertyChanged(nameof(IsProcessingQcBlocked)); OnPropertyChanged(nameof(ProcessingReadiness));
            RunProcessingCommand?.RaiseCanExecuteChanged(); ModeHopPreview.SweepPreparation.RefreshCommands();
        }

        private void ReportQcBlock(string stage, PreparedSweepMetadata reference, PreparedSweepMetadata signal, string reason)
        {
            InputValidationText = "분석 시작 차단\n" + reason;
            ProgressText = "위상 QC를 통과하지 못해 처리를 시작하지 않았습니다.\n" + reason;
            try { SweepRunRecorder.RecordQcBlock(stage, reference, signal, MaxNonmonotonicRatio, reason); }
            catch (Exception ex) { ProgressText += "\n결과 기록 실패: " + ex.Message; }
            RefreshPreparedQc();
        }

        private void ClearPreparedInput()
        {
            if (IsBusy || preparedInput == null) return;
            preparedInput = null;
            OnPropertyChanged(nameof(PolarizationOptions)); SelectedPolarization = legacyPolarization ?? "CH2 / Xpol";
            if (legacyPaths != null) { AuxiliaryPath = legacyPaths[0]; SignalAuxiliaryPath = legacyPaths[1]; ReferencePath = legacyPaths[2]; SignalPath = legacyPaths[3]; }
            OnPropertyChanged(nameof(IsLegacyInput)); RaiseCommandStates(); InputValidationText = "기존 CH1 보조간섭계 / CH2·CH3 편광 파일 입력 모드입니다.";
            OnPropertyChanged(nameof(ReferenceAuxiliaryLabel)); OnPropertyChanged(nameof(SignalAuxiliaryLabel));
            RefreshPreparedQc();
        }

        public async Task RunProcessingAsync()
        {
            if (IsBusy || ModeHopPreview.IsBusy || !InputsSelected()) return;
            var qcBlock = ProcessingQcBlockReason;
            if (qcBlock != null) { ReportQcBlock("run_command", preparedInput.Reference.Preparation, preparedInput.Signal.Preparation, qcBlock); return; }
            SweepRunRecorder preparedLog = null; Exception preparedFailure = null;
            BeginOperation("입력 파일을 순서대로 읽는 중...");
            try
            {
                var expectedPolChannel = SelectedPolarization.StartsWith("CH3", StringComparison.OrdinalIgnoreCase) ? 3 : 2;
                var auxiliaryProgress = new Progress<OperationProgress>(value => UpdateProgress(value, 0.0, 7.5));
                var signalAuxiliaryProgress = new Progress<OperationProgress>(value => UpdateProgress(value, 7.5, 15.0));
                var referenceProgress = new Progress<OperationProgress>(value => UpdateProgress(value, 15.0, 22.5));
                var signalProgress = new Progress<OperationProgress>(value => UpdateProgress(value, 22.5, 30.0));
                if (preparedInput != null) preparedLog = new SweepRunRecorder("prepared_ofdr_analysis");
                var input = preparedInput ?? await Task.Run(() => new IndependentOfdrInput
                {
                    Auxiliary = channelReader.Read(AuxiliaryPath, 1, cancellation.Token, auxiliaryProgress),
                    SignalAuxiliary = channelReader.Read(SignalAuxiliaryPath, 1, cancellation.Token, signalAuxiliaryProgress),
                    Reference = channelReader.Read(ReferencePath, expectedPolChannel, cancellation.Token, referenceProgress),
                    Signal = channelReader.Read(SignalPath, expectedPolChannel, cancellation.Token, signalProgress)
                });
                IndependentOfdrProcessingEngine.ValidateInput(input);
                if (preparedLog != null)
                {
                    JsonContractSerializer.Write(Path.Combine(preparedLog.DirectoryPath, "ref_preparation.json"), input.Reference.Preparation);
                    JsonContractSerializer.Write(Path.Combine(preparedLog.DirectoryPath, "signal_preparation.json"), input.Signal.Preparation);
                }
                InputValidationText = string.Format(CultureInfo.InvariantCulture, "검증 완료: 기준 보조 {0}, Ref {1}, 측정 보조 {2}, Sig {3}, {4:N0} 포인트",
                    input.Auxiliary.MeasurementId, input.Reference.MeasurementId, input.SignalAuxiliary.MeasurementId, input.Signal.MeasurementId, input.Signal.Values.Length);
                var config = new IndependentOfdrProcessingConfig
                {
                    WindowFunction = SelectedWindowFunction,
                    SegmentSize = SegmentSize,
                    SegmentHop = SegmentHop,
                    SegmentIfftZeroPadFactor = ZeroPadFactor,
                    CrossCorrelationScale = CorrelationScale,
                    NormalizeCrossCorrelation = NormalizeCorrelation,
                    MaxAuxNonmonotonicRatio = MaxNonmonotonicRatio,
                    MaxPreviewPoints = MaxPreviewPoints,
                    UsePhysicalAxes = UsePhysicalAxes,
                    AuxiliaryPathDifferenceMeters = AuxiliaryPathDifferenceMeters,
                    AuxiliaryGroupIndex = AuxiliaryGroupIndex,
                    MaximumSpectralShiftGHz = MaximumSpectralShiftGHz,
                    DistanceGeometryFactor = DistanceGeometryFactor,
                    DistanceOffsetMeters = DistanceOffsetMeters,
                    CenterWavelengthNm = CenterWavelengthNm,
                    StrainCoefficient = StrainCoefficient,
                    StrainScaleFactor = StrainScaleFactor,
                    StrainOffsetMicrostrain = StrainOffsetMicrostrain,
                    MeasurementStartMeters = MeasurementStartMeters,
                    MeasurementEndMeters = MeasurementEndMeters,
                    AnalysisStartMeters = AnalysisStartMeters,
                    AnalysisEndMeters = AnalysisEndMeters,
                    DeformationLengthMeters = DeformationLengthMeters,
                    SweepRangeNm = SweepRangeNm,
                    SweepRateNmPerSecond = SweepRateNmPerSecond,
                    SamplingRateSamplesPerSecond = SamplingRateSamplesPerSecond,
                    RestrictToMeasurementRange = RestrictToMeasurementRange
                };
                var processingProgress = new Progress<OperationProgress>(value => UpdateProgress(value, 30.0, 100.0));
                engine = new IndependentOfdrProcessingEngine();
                if (preparedLog != null) JsonContractSerializer.Write(Path.Combine(preparedLog.DirectoryPath, "processing_config.json"), config);
                var result = await Task.Run(() => engine.Run(input, config, cancellation.Token, processingProgress));
                if (ProcessingResult != null) ProcessingResult.Dispose();
                ProcessingResult = result;
                if (preparedLog != null) JsonContractSerializer.Write(Path.Combine(preparedLog.DirectoryPath, "result.json"), result);
                SelectedSegment = result.SelectedSegment;
                SelectedSegmentIndex = result.SelectedSegment.SegmentIndex;
                ProgressPercent = 100.0;
                ProgressText = string.Format(CultureInfo.InvariantCulture, "전체 처리 완료: {0:N0} 포인트, {1:N0} Segment, {2}", result.SampleCount, result.SegmentStarts.Length, result.Polarization);
            }
            catch (OperationCanceledException ex)
            {
                preparedFailure = ex;
                ProgressText = "사용자 요청으로 처리를 취소했습니다.";
            }
            catch (Exception ex)
            {
                preparedFailure = ex;
                ProgressText = "처리 실패: " + ex.Message;
                InputValidationText = "입력 검증 또는 처리 중 오류가 발생했습니다: " + ex.Message;
            }
            finally { try { preparedLog?.Finish(preparedFailure); } catch (Exception ex) { ProgressText += " / 결과 기록 실패: " + ex.Message; } EndOperation(); }
        }

        private async void ConvertInputAsync(InputRole role)
        {
            var source = GetPath(role);
            BeginOperation("채널별 CSV로 변환하는 중...");
            try
            {
                var progress = new Progress<OperationProgress>(value => UpdateProgress(value, 0.0, 100.0));
                ChannelConversionResult result;
                try
                {
                    result = await Task.Run(() => channelConverter.Convert(source, ChannelCsvOutputFolder, false, cancellation.Token, progress));
                }
                catch (IOException ex)
                {
                    var overwrite = MessageBox.Show(ex.Message + Environment.NewLine + "기존 파일을 덮어쓸까요?", "채널 CSV 덮어쓰기", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (overwrite != MessageBoxResult.Yes) throw;
                    result = await Task.Run(() => channelConverter.Convert(source, ChannelCsvOutputFolder, true, cancellation.Token, progress));
                }
                SetPath(role, result.CsvPath);
                ProgressText = result.Skipped ? "최신 채널 CSV가 있어 변환을 건너뛰었습니다." : "채널 CSV 변환 완료: " + result.CsvPath;
                ProgressPercent = 100.0;
            }
            catch (OperationCanceledException) { ProgressText = "CSV 변환을 취소했습니다."; }
            catch (Exception ex) { ProgressText = "CSV 변환 실패: " + ex.Message; }
            finally { EndOperation(); }
        }

        private void InspectSegment()
        {
            try
            {
                SelectedSegment = engine.InspectSegment(ProcessingResult, SelectedSegmentIndex);
                ProcessingResult.SelectedSegment = SelectedSegment;
                ProgressText = PeakDescription;
            }
            catch (Exception ex) { ProgressText = "Segment 확인 실패: " + ex.Message; }
        }

        private void BrowseInput(string title, Action<string> setter)
        {
            var dialog = new OpenFileDialog { Title = title, Filter = "OFDR channel (*.isf;*.csv)|*.isf;*.csv|ISF (*.isf)|*.isf|CSV (*.csv)|*.csv|모든 파일 (*.*)|*.*", Multiselect = false };
            if (Directory.Exists(DefaultDataRoot)) dialog.InitialDirectory = DefaultDataRoot;
            if (dialog.ShowDialog() == true) { ClearPreparedInput(); setter(dialog.FileName); }
        }

        private void BrowseOutputFolder()
        {
            using (var dialog = new WinForms.FolderBrowserDialog { Description = "채널별 CSV 출력 폴더", SelectedPath = Directory.Exists(ChannelCsvOutputFolder) ? ChannelCsvOutputFolder : DefaultDataRoot })
                if (dialog.ShowDialog() == WinForms.DialogResult.OK) ChannelCsvOutputFolder = dialog.SelectedPath;
        }

        private void DetectPolarization(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            if (name.EndsWith("CH3", StringComparison.OrdinalIgnoreCase)) SelectedPolarization = "CH3 / Ypol";
            else if (name.EndsWith("CH2", StringComparison.OrdinalIgnoreCase)) SelectedPolarization = "CH2 / Xpol";
        }

        private void SetCompanionAuxiliary(string polarizationPath, bool signalSweep)
        {
            var fullPath = Path.GetFullPath(polarizationPath);
            var name = Path.GetFileNameWithoutExtension(fullPath) ?? string.Empty;
            if (!name.EndsWith("CH2", StringComparison.OrdinalIgnoreCase) && !name.EndsWith("CH3", StringComparison.OrdinalIgnoreCase)) return;
            var auxiliaryName = name.Substring(0, name.Length - 3) + "CH1" + Path.GetExtension(fullPath);
            var candidate = Path.Combine(Path.GetDirectoryName(fullPath) ?? string.Empty, auxiliaryName);
            if (!File.Exists(candidate)) return;
            if (signalSweep) SignalAuxiliaryPath = candidate;
            else AuxiliaryPath = candidate;
        }

        private void BeginOperation(string message)
        {
            operationIdle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            IsBusy = true;
            ProgressPercent = 0.0;
            ProgressText = message;
        }

        private void EndOperation() { IsBusy = false; operationIdle?.TrySetResult(true); }
        public Task CancelAndWaitAsync() { cancellation?.Cancel(); return operationIdle?.Task ?? Task.CompletedTask; }
        private void Cancel() { cancellation?.Cancel(); ProgressText = "취소 요청을 전달했습니다..."; }

        private void UpdateProgress(OperationProgress value, double start, double end)
        {
            if (value == null) return;
            ProgressPercent = start + ((end - start) * value.Percent / 100.0);
            ProgressText = string.IsNullOrWhiteSpace(value.Detail) ? value.Stage : value.Stage + " · " + value.Detail;
        }

        private bool InputsSelected() { return !string.IsNullOrWhiteSpace(AuxiliaryPath) && !string.IsNullOrWhiteSpace(SignalAuxiliaryPath) && !string.IsNullOrWhiteSpace(ReferencePath) && !string.IsNullOrWhiteSpace(SignalPath); }
        private bool CanConvert(string path) { return !IsBusy && !string.IsNullOrWhiteSpace(path) && string.Equals(Path.GetExtension(path), ".isf", StringComparison.OrdinalIgnoreCase); }
        private static string ExistingOrEmpty(string path) { return File.Exists(path) ? path : string.Empty; }

        private PreviewSeries GetLargeGraphPrimary()
        {
            if (ProcessingResult == null) return null;
            if (SelectedLargeGraph.StartsWith("1.", StringComparison.Ordinal)) return ProcessingResult.SignalRaw;
            if (SelectedLargeGraph.StartsWith("2.", StringComparison.Ordinal)) return ProcessingResult.PreKSignalSpectrum;
            if (SelectedLargeGraph.StartsWith("3.", StringComparison.Ordinal)) return ProcessingResult.SignalSpectrum;
            if (SelectedLargeGraph.StartsWith("4.", StringComparison.Ordinal)) return SelectedSegment == null ? null : SelectedSegment.SignalSegment;
            if (SelectedLargeGraph.StartsWith("5.", StringComparison.Ordinal)) return SelectedSegment == null ? null : SelectedSegment.WindowedSignalSegment;
            if (SelectedLargeGraph.StartsWith("6.", StringComparison.Ordinal)) return SelectedSegment == null ? null : SelectedSegment.SignalIfft;
            if (SelectedLargeGraph.StartsWith("7.", StringComparison.Ordinal)) return SelectedSegment == null ? null : SelectedSegment.Correlation;
            if (SelectedLargeGraph.StartsWith("8.", StringComparison.Ordinal)) return SelectedSegment == null ? null : SelectedSegment.CorrelationZoom;
            if (SelectedLargeGraph.StartsWith("9.", StringComparison.Ordinal)) return ProcessingResult.SpectralShiftProfile;
            return ProcessingResult.StrainProfile;
        }

        private PreviewSeries GetLargeGraphSecondary()
        {
            if (ProcessingResult == null) return null;
            if (SelectedLargeGraph.StartsWith("1.", StringComparison.Ordinal)) return ProcessingResult.ReferenceRaw;
            if (SelectedLargeGraph.StartsWith("2.", StringComparison.Ordinal)) return ProcessingResult.PreKReferenceSpectrum;
            if (SelectedLargeGraph.StartsWith("3.", StringComparison.Ordinal)) return ProcessingResult.ReferenceSpectrum;
            if (SelectedLargeGraph.StartsWith("4.", StringComparison.Ordinal)) return SelectedSegment == null ? null : SelectedSegment.ReferenceSegment;
            if (SelectedLargeGraph.StartsWith("5.", StringComparison.Ordinal)) return SelectedSegment == null ? null : SelectedSegment.WindowedReferenceSegment;
            if (SelectedLargeGraph.StartsWith("6.", StringComparison.Ordinal)) return SelectedSegment == null ? null : SelectedSegment.ReferenceIfft;
            return null;
        }

        private void RaiseLargeGraphProperties()
        {
            OnPropertyChanged(nameof(LargeGraphPrimaryValues));
            OnPropertyChanged(nameof(LargeGraphSecondaryValues));
            OnPropertyChanged(nameof(LargeGraphMarkers));
            OnPropertyChanged(nameof(LargeGraphXStart));
            OnPropertyChanged(nameof(LargeGraphXEnd));
            OnPropertyChanged(nameof(LargeGraphXAxisTitle));
            OnPropertyChanged(nameof(LargeGraphYAxisTitle));
        }

        private void RaiseCommandStates()
        {
            ModeHopPreview.SweepPreparation.RefreshCommands(); UseLegacyInputsCommand?.RaiseCanExecuteChanged();
            BrowseAuxiliaryCommand?.RaiseCanExecuteChanged();
            BrowseSignalAuxiliaryCommand?.RaiseCanExecuteChanged();
            BrowseReferenceCommand?.RaiseCanExecuteChanged();
            BrowseSignalCommand?.RaiseCanExecuteChanged();
            BrowseOutputFolderCommand?.RaiseCanExecuteChanged();
            ConvertAuxiliaryCommand?.RaiseCanExecuteChanged();
            ConvertSignalAuxiliaryCommand?.RaiseCanExecuteChanged();
            ConvertReferenceCommand?.RaiseCanExecuteChanged();
            ConvertSignalCommand?.RaiseCanExecuteChanged();
            RunProcessingCommand?.RaiseCanExecuteChanged();
            CancelCommand?.RaiseCanExecuteChanged();
            InspectSegmentCommand?.RaiseCanExecuteChanged();
        }

        private string GetPath(InputRole role) { return role == InputRole.Auxiliary ? AuxiliaryPath : role == InputRole.SignalAuxiliary ? SignalAuxiliaryPath : role == InputRole.Reference ? ReferencePath : SignalPath; }
        private void SetPath(InputRole role, string path) { if (role == InputRole.Auxiliary) AuxiliaryPath = path; else if (role == InputRole.SignalAuxiliary) SignalAuxiliaryPath = path; else if (role == InputRole.Reference) ReferencePath = path; else SignalPath = path; }
        private enum InputRole { Auxiliary, SignalAuxiliary, Reference, Signal }

        public void Dispose()
        {
            ModeHopPreview.Dispose();
            cancellation?.Cancel();
            cancellation?.Dispose();
            ProcessingResult?.Dispose();
        }
    }
}
