using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;
using OFDR_Signal_Generator.Infrastructure;
using OFDR_Signal_Generator.Models;
using OFDR_Signal_Generator.Processing;
using OFDR_Signal_Generator.Services;

namespace OFDR_Signal_Generator.ViewModels
{
    public sealed class MainViewModel : ObservableObject
    {
        private readonly SyntheticSignalGenerator signalGenerator;
        private readonly SignalProcessingPipeline processingPipeline;
        private readonly CsvSignalLoader csvSignalLoader;
        private readonly OfdrDatasetScanner isfDatasetScanner;
        private readonly OfdrSetCsvConverter isfCsvConverter;
        private readonly ConvertedOfdrCsvReader convertedCsvReader;
        private readonly RelayCommand removeEventCommand;
        private readonly RelayCommand scanIsfFolderCommand;
        private readonly RelayCommand convertSelectedIsfCommand;
        private readonly RelayCommand convertAllIsfCommand;
        private readonly RelayCommand processSelectedIsfCommand;
        private readonly RelayCommand browseConvertedCsvCommand;
        private readonly RelayCommand processConvertedCsvCommand;
        private readonly RelayCommand cancelIsfOperationCommand;
        private readonly RelayCommand inspectIsfSegmentCommand;
        private readonly RelayCommand browseIsfInputFolderCommand;
        private readonly RelayCommand browseIsfOutputFolderCommand;
        private LoadedSignalData loadedRawSignal;
        private LoadedSignalData loadedReferenceSignal;
        private SyntheticSignalResult currentSignal;
        private PipelineRunResult lastPipelineResult;
        private ComparisonSummary comparisonSummary;
        private ReflectionEvent selectedReflectionEvent;
        private StageResult selectedStage;
        private int selectedSegmentIndex;
        private string selectedConjMulViewMode;
        private int selectedCorrelationLag;
        private string selectedFinalMetricMode;
        private int sampleCount;
        private double sampleSpacing;
        private int randomSeed;
        private double rawNoiseLevel;
        private double rawAttenuation;
        private double rawBaselineOffset;
        private bool useRawSineSignal;
        private double rawSineFrequency;
        private double rawSineAmplitude;
        private double rawSinePhaseDegrees;
        private double referenceAmplitude;
        private int referenceDelaySamples;
        private double referenceWidth;
        private double referenceNoiseLevel;
        private double referenceBaselineOffset;
        private double referencePhaseShiftDegrees;
        private bool useReferenceSineSignal;
        private double referenceSineFrequency;
        private double referenceSineAmplitude;
        private double referenceSinePhaseDegrees;
        private int fftZeroPadFactor;
        private bool useHannWindow;
        private int segmentSize;
        private int segmentHop;
        private double conjugateMultiplyScale;
        private bool normalizeConjugateMultiply;
        private int finalSmoothingWindow;
        private double peakThresholdRatio;
        private string rawCsvPath;
        private string referenceCsvPath;
        private string statusMessage;
        private OfdrSetProcessingResult isfProcessingResult;
        private OfdrProcessingEngine isfProcessingEngine;
        private SegmentInspection isfSegmentInspection;
        private CancellationTokenSource isfCancellation;
        private OfdrChannelSet selectedIsfSet;
        private string isfInputFolder;
        private string isfOutputFolder;
        private string isfProgressText;
        private double isfProgressPercent;
        private bool isIsfBusy;
        private string selectedIsfPolarization;
        private int isfSegmentIndex;
        private string convertedCsvPath;

        public MainViewModel()
        {
            signalGenerator = new SyntheticSignalGenerator();
            processingPipeline = new SignalProcessingPipeline();
            csvSignalLoader = new CsvSignalLoader();
            isfDatasetScanner = new OfdrDatasetScanner();
            isfCsvConverter = new OfdrSetCsvConverter();
            convertedCsvReader = new ConvertedOfdrCsvReader();

            ReflectionEvents = new ObservableCollection<ReflectionEvent>();
            StageResults = new ObservableCollection<StageResult>();
            DetectedEvents = new ObservableCollection<DetectedEvent>();
            MatchResults = new ObservableCollection<EventMatchResult>();
            SegmentLabels = new ObservableCollection<string>();
            IsfSets = new ObservableCollection<OfdrChannelSet>();
            IsfPolarizations = new ObservableCollection<string>(new[] { "Xpol", "Ypol" });
            ConjMulViewModes = new ObservableCollection<string>(new[] { "Magnitude", "Real", "Imag", "Phase" });
            FinalMetricModes = new ObservableCollection<string>(new[]
            {
                SignalProcessingPipeline.FinalMetricAbsolutePeakOffset,
                SignalProcessingPipeline.FinalMetricSignedPeakOffset,
                SignalProcessingPipeline.FinalMetricPeakAmplitude,
                SignalProcessingPipeline.FinalMetricPeakCenterDelta
            });
            selectedSegmentIndex = -1;
            selectedConjMulViewMode = "Magnitude";
            selectedCorrelationLag = 0;
            selectedFinalMetricMode = SignalProcessingPipeline.FinalMetricAbsolutePeakOffset;
            selectedIsfPolarization = "Xpol";
            isfInputFolder = @"C:\Project\OFDR\Datas\OFDR Raw data";
            isfOutputFolder = Path.Combine(isfInputFolder, "converted_csv");

            GenerateSignalCommand = new RelayCommand(GenerateSignal);
            RunPipelineCommand = new RelayCommand(RunPipeline);
            AddEventCommand = new RelayCommand(AddReflectionEvent);
            removeEventCommand = new RelayCommand(RemoveSelectedEvent, () => SelectedReflectionEvent != null);
            ResetDefaultsCommand = new RelayCommand(ResetDefaults, () => !IsIsfBusy);
            LoadRawCsvCommand = new RelayCommand(LoadRawCsv);
            LoadReferenceCsvCommand = new RelayCommand(LoadReferenceCsv);
            ClearRawCsvCommand = new RelayCommand(ClearRawCsv);
            ClearReferenceCsvCommand = new RelayCommand(ClearReferenceCsv);
            ExportReconstructedRawCsvCommand = new RelayCommand(ExportReconstructedRawCsv);
            scanIsfFolderCommand = new RelayCommand(async () => await ScanIsfFolderAsync(), () => !IsIsfBusy);
            convertSelectedIsfCommand = new RelayCommand(async () => await ConvertIsfSetsAsync(false), () => !IsIsfBusy && SelectedIsfSet != null && SelectedIsfSet.IsValid);
            convertAllIsfCommand = new RelayCommand(async () => await ConvertIsfSetsAsync(true), () => !IsIsfBusy && IsfSets.Any(item => item.IsValid));
            processSelectedIsfCommand = new RelayCommand(async () => await ProcessSelectedIsfAsync(), () => !IsIsfBusy && SelectedIsfSet != null && SelectedIsfSet.IsValid);
            browseConvertedCsvCommand = new RelayCommand(BrowseConvertedCsv, () => !IsIsfBusy);
            processConvertedCsvCommand = new RelayCommand(async () => await ProcessConvertedCsvAsync(), () => !IsIsfBusy && !string.IsNullOrWhiteSpace(ConvertedCsvPath) && File.Exists(ConvertedCsvPath));
            cancelIsfOperationCommand = new RelayCommand(CancelIsfOperation, () => IsIsfBusy);
            inspectIsfSegmentCommand = new RelayCommand(InspectIsfSegment, () => isfProcessingResult != null && !IsIsfBusy);
            browseIsfInputFolderCommand = new RelayCommand(BrowseIsfInputFolder, () => !IsIsfBusy);
            browseIsfOutputFolderCommand = new RelayCommand(BrowseIsfOutputFolder, () => !IsIsfBusy);

            ResetDefaults();
        }

        public ObservableCollection<ReflectionEvent> ReflectionEvents { get; private set; }

        public ObservableCollection<StageResult> StageResults { get; private set; }

        public ObservableCollection<DetectedEvent> DetectedEvents { get; private set; }

        public ObservableCollection<EventMatchResult> MatchResults { get; private set; }

        public ObservableCollection<string> SegmentLabels { get; private set; }

        public ObservableCollection<string> ConjMulViewModes { get; private set; }

        public ObservableCollection<string> FinalMetricModes { get; private set; }

        public ObservableCollection<OfdrChannelSet> IsfSets { get; private set; }

        public ObservableCollection<string> IsfPolarizations { get; private set; }

        public RelayCommand GenerateSignalCommand { get; private set; }

        public RelayCommand RunPipelineCommand { get; private set; }

        public RelayCommand AddEventCommand { get; private set; }

        public RelayCommand LoadRawCsvCommand { get; private set; }

        public RelayCommand LoadReferenceCsvCommand { get; private set; }

        public RelayCommand ClearRawCsvCommand { get; private set; }

        public RelayCommand ClearReferenceCsvCommand { get; private set; }

        public RelayCommand RemoveEventCommand
        {
            get { return removeEventCommand; }
        }


        public RelayCommand ExportReconstructedRawCsvCommand { get; private set; }
        public RelayCommand ResetDefaultsCommand { get; private set; }

        public RelayCommand ScanIsfFolderCommand { get { return scanIsfFolderCommand; } }
        public RelayCommand ConvertSelectedIsfCommand { get { return convertSelectedIsfCommand; } }
        public RelayCommand ConvertAllIsfCommand { get { return convertAllIsfCommand; } }
        public RelayCommand ProcessSelectedIsfCommand { get { return processSelectedIsfCommand; } }
        public RelayCommand BrowseConvertedCsvCommand { get { return browseConvertedCsvCommand; } }
        public RelayCommand ProcessConvertedCsvCommand { get { return processConvertedCsvCommand; } }
        public RelayCommand CancelIsfOperationCommand { get { return cancelIsfOperationCommand; } }
        public RelayCommand InspectIsfSegmentCommand { get { return inspectIsfSegmentCommand; } }
        public RelayCommand BrowseIsfInputFolderCommand { get { return browseIsfInputFolderCommand; } }
        public RelayCommand BrowseIsfOutputFolderCommand { get { return browseIsfOutputFolderCommand; } }

        public int SampleCount
        {
            get { return sampleCount; }
            set { SetProperty(ref sampleCount, value); }
        }

        public double SampleSpacing
        {
            get { return sampleSpacing; }
            set { SetProperty(ref sampleSpacing, value); }
        }

        public int RandomSeed
        {
            get { return randomSeed; }
            set { SetProperty(ref randomSeed, value); }
        }

        public double RawNoiseLevel
        {
            get { return rawNoiseLevel; }
            set { SetProperty(ref rawNoiseLevel, value); }
        }

        public double RawAttenuation
        {
            get { return rawAttenuation; }
            set { SetProperty(ref rawAttenuation, value); }
        }

        public double RawBaselineOffset
        {
            get { return rawBaselineOffset; }
            set { SetProperty(ref rawBaselineOffset, value); }
        }

        public bool UseRawSineSignal
        {
            get { return useRawSineSignal; }
            set { SetProperty(ref useRawSineSignal, value); }
        }

        public double RawSineFrequency
        {
            get { return rawSineFrequency; }
            set { SetProperty(ref rawSineFrequency, value); }
        }

        public double RawSineAmplitude
        {
            get { return rawSineAmplitude; }
            set { SetProperty(ref rawSineAmplitude, value); }
        }

        public double RawSinePhaseDegrees
        {
            get { return rawSinePhaseDegrees; }
            set { SetProperty(ref rawSinePhaseDegrees, value); }
        }

        public double ReferenceAmplitude
        {
            get { return referenceAmplitude; }
            set { SetProperty(ref referenceAmplitude, value); }
        }

        public int ReferenceDelaySamples
        {
            get { return referenceDelaySamples; }
            set { SetProperty(ref referenceDelaySamples, value); }
        }

        public double ReferenceWidth
        {
            get { return referenceWidth; }
            set { SetProperty(ref referenceWidth, value); }
        }

        public double ReferenceNoiseLevel
        {
            get { return referenceNoiseLevel; }
            set { SetProperty(ref referenceNoiseLevel, value); }
        }

        public double ReferenceBaselineOffset
        {
            get { return referenceBaselineOffset; }
            set { SetProperty(ref referenceBaselineOffset, value); }
        }

        public double ReferencePhaseShiftDegrees
        {
            get { return referencePhaseShiftDegrees; }
            set { SetProperty(ref referencePhaseShiftDegrees, value); }
        }

        public bool UseReferenceSineSignal
        {
            get { return useReferenceSineSignal; }
            set { SetProperty(ref useReferenceSineSignal, value); }
        }

        public double ReferenceSineFrequency
        {
            get { return referenceSineFrequency; }
            set { SetProperty(ref referenceSineFrequency, value); }
        }

        public double ReferenceSineAmplitude
        {
            get { return referenceSineAmplitude; }
            set { SetProperty(ref referenceSineAmplitude, value); }
        }

        public double ReferenceSinePhaseDegrees
        {
            get { return referenceSinePhaseDegrees; }
            set { SetProperty(ref referenceSinePhaseDegrees, value); }
        }

        public int FftZeroPadFactor
        {
            get { return fftZeroPadFactor; }
            set { SetProperty(ref fftZeroPadFactor, value); }
        }

        public bool UseHannWindow
        {
            get { return useHannWindow; }
            set { SetProperty(ref useHannWindow, value); }
        }

        public int SegmentSize
        {
            get { return segmentSize; }
            set { SetProperty(ref segmentSize, value); }
        }

        public int SegmentHop
        {
            get { return segmentHop; }
            set { SetProperty(ref segmentHop, value); }
        }

        public double ConjugateMultiplyScale
        {
            get { return conjugateMultiplyScale; }
            set { SetProperty(ref conjugateMultiplyScale, value); }
        }

        public bool NormalizeConjugateMultiply
        {
            get { return normalizeConjugateMultiply; }
            set { SetProperty(ref normalizeConjugateMultiply, value); }
        }

        public int FinalSmoothingWindow
        {
            get { return finalSmoothingWindow; }
            set { SetProperty(ref finalSmoothingWindow, value); }
        }

        public double PeakThresholdRatio
        {
            get { return peakThresholdRatio; }
            set { SetProperty(ref peakThresholdRatio, value); }
        }

        public string RawCsvPath
        {
            get { return rawCsvPath; }
            set { SetProperty(ref rawCsvPath, value); }
        }

        public string ReferenceCsvPath
        {
            get { return referenceCsvPath; }
            set { SetProperty(ref referenceCsvPath, value); }
        }

        public string RawCsvStatus
        {
            get
            {
                if (loadedRawSignal == null)
                {
                    return "Synthetic Raw signal을 사용합니다.";
                }

                var mode = loadedRawSignal.HasImaginaryComponent ? "complex I/Q" : "real";
                return string.Format(CultureInfo.InvariantCulture, "{2} column에서 {0:N0}개 {1} sample을 불러왔습니다.", loadedRawSignal.Samples.Length, mode, loadedRawSignal.SampleColumnName);
            }
        }

        public string ReferenceCsvStatus
        {
            get
            {
                if (loadedReferenceSignal == null)
                {
                    return "Synthetic Ref signal을 사용합니다.";
                }

                var mode = loadedReferenceSignal.HasImaginaryComponent ? "complex I/Q" : "real";
                return string.Format(CultureInfo.InvariantCulture, "{2} column에서 {0:N0}개 {1} sample을 불러왔습니다.", loadedReferenceSignal.Samples.Length, mode, loadedReferenceSignal.SampleColumnName);
            }
        }

        public ReflectionEvent SelectedReflectionEvent
        {
            get { return selectedReflectionEvent; }
            set
            {
                if (SetProperty(ref selectedReflectionEvent, value))
                {
                    removeEventCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public StageResult SelectedStage
        {
            get { return selectedStage; }
            set { SetProperty(ref selectedStage, value); }
        }

        public int SelectedSegmentIndex
        {
            get { return selectedSegmentIndex; }
            set
            {
                if (SetProperty(ref selectedSegmentIndex, value))
                {
                    OnPropertyChanged(nameof(SelectedSegmentSummary));
                    RebuildStageResults(SelectedStage == null ? null : SelectedStage.Name);
                }
            }
        }

        public string SelectedConjMulViewMode
        {
            get { return selectedConjMulViewMode; }
            set
            {
                if (SetProperty(ref selectedConjMulViewMode, string.IsNullOrWhiteSpace(value) ? "Magnitude" : value))
                {
                    RebuildStageResults(SelectedStage == null ? null : SelectedStage.Name);
                }
            }
        }

        public int SelectedCorrelationLag
        {
            get { return selectedCorrelationLag; }
            set
            {
                var bounds = GetSelectedCorrelationLagBounds();
                var clamped = Math.Max(bounds.Item1, Math.Min(bounds.Item2, value));
                if (SetProperty(ref selectedCorrelationLag, clamped))
                {
                    NotifyCorrelationInspectorChanged();
                    RebuildStageResults(SelectedStage == null ? null : SelectedStage.Name);
                }
            }
        }

        public int SelectedCorrelationLagMinimum
        {
            get { return GetSelectedCorrelationLagBounds().Item1; }
        }

        public int SelectedCorrelationLagMaximum
        {
            get { return GetSelectedCorrelationLagBounds().Item2; }
        }

        public bool IsCorrelationInspectorVisible
        {
            get { return false; }
        }

        public string SelectedCorrelationLagSummary
        {
            get
            {
                if (!HasSelectedSegment())
                {
                    return "Segment를 선택하고 실행하면 Raw/Ref Segment IFFT signal의 lag별 overlap을 확인할 수 있습니다.";
                }

                var segmentIndex = SelectedSegmentIndex;
                var rawMagnitudes = lastPipelineResult.RawIfftSegments[segmentIndex].Select(value => value.Magnitude).ToArray();
                var referenceMagnitudes = lastPipelineResult.ReferenceIfftSegments[segmentIndex].Select(value => value.Magnitude).ToArray();
                var overlapStats = CalculateLagOverlapStats(rawMagnitudes, referenceMagnitudes, SelectedCorrelationLag);
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "k = {0} | overlap {1}/{2} | 평균 abs diff {3:0.0000} | RMS diff {4:0.0000}",
                    SelectedCorrelationLag,
                    overlapStats.Item1,
                    rawMagnitudes.Length,
                    overlapStats.Item2,
                    overlapStats.Item3);
            }
        }

        public double[] CorrelationOverlayRawSamples
        {
            get
            {
                if (!HasSelectedSegment())
                {
                    return new double[0];
                }

                return lastPipelineResult.RawIfftSegments[SelectedSegmentIndex].Select(value => value.Magnitude).ToArray();
            }
        }

        public double[] CorrelationOverlayReferenceSamples
        {
            get
            {
                if (!HasSelectedSegment())
                {
                    return new double[0];
                }

                var rawMagnitudes = lastPipelineResult.RawIfftSegments[SelectedSegmentIndex].Select(value => value.Magnitude).ToArray();
                var referenceMagnitudes = lastPipelineResult.ReferenceIfftSegments[SelectedSegmentIndex].Select(value => value.Magnitude).ToArray();
                return BuildLagShiftedReferenceOverlay(rawMagnitudes.Length, referenceMagnitudes, SelectedCorrelationLag);
            }
        }

        public double CorrelationOverlayXEndValue
        {
            get { return Math.Max(0, CorrelationOverlayRawSamples.Length - 1); }
        }

        public string SelectedFinalMetricMode
        {
            get { return selectedFinalMetricMode; }
            set
            {
                var normalized = SignalProcessingPipeline.NormalizeFinalMetricMode(value);
                if (SetProperty(ref selectedFinalMetricMode, normalized))
                {
                    if (currentSignal != null && lastPipelineResult != null)
                    {
                        RunPipeline();
                    }
                }
            }
        }

        public int SegmentCount
        {
            get { return SegmentLabels.Count; }
        }

        public string SelectedSegmentSummary
        {
            get
            {
                if (lastPipelineResult == null || SegmentCount == 0 || SelectedSegmentIndex < 0 || SelectedSegmentIndex >= SegmentCount)
                {
                    return "OFDR Pipeline을 실행하면 Segment별 FFT, ConjMul, IFFT 결과를 확인할 수 있습니다.";
                }

                var start = lastPipelineResult.SegmentStartIndices[SelectedSegmentIndex];
                var center = lastPipelineResult.SegmentCenterIndices[SelectedSegmentIndex];
                var segmentLength = lastPipelineResult.RawSegments[SelectedSegmentIndex].Length;
                var peak = lastPipelineResult.IfftPeakIndices[SelectedSegmentIndex];
                var representative = lastPipelineResult.SegmentRepresentativeValues[SelectedSegmentIndex];
                var end = start + segmentLength - 1;

                var configuredShiftBins = GetConfiguredShiftBinsForSegment(SelectedSegmentIndex);
                var shiftSummary = configuredShiftBins == 0
                    ? "설정된 Raw IFFT shift 없음"
                    : string.Format(CultureInfo.InvariantCulture, "설정된 Raw IFFT shift {0:+#;-#;0} bin", configuredShiftBins);

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "Segment {0}/{1} | FFT bin {2}-{3} | center {4} | IFFT peak {5} | {6}: {7:0.0000} | {8}",
                    SelectedSegmentIndex + 1,
                    SegmentCount,
                    start,
                    end,
                    center,
                    peak,
                    SignalProcessingPipeline.GetFinalMetricYAxisTitle(lastPipelineResult.FinalMetricMode),
                    representative,
                    shiftSummary);
            }
        }

        public string StatusMessage
        {
            get { return statusMessage; }
            set { SetProperty(ref statusMessage, value); }
        }

        public ComparisonSummary ComparisonSummary
        {
            get { return comparisonSummary; }
            private set
            {
                if (SetProperty(ref comparisonSummary, value))
                {
                    OnPropertyChanged(nameof(ComparisonSummaryText));
                }
            }
        }

        public string ComparisonSummaryText
        {
            get
            {
                if (ComparisonSummary == null)
                {
                    return "OFDR signal set을 생성하고 처리하면 stage 출력과 최종 detection 정확도를 볼 수 있습니다.";
                }

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "Expected {0} / Detected {1} / Matched {2} / Missed {3} / False+ {4} / MAE {5:0.00} sample",
                    ComparisonSummary.ExpectedCount,
                    ComparisonSummary.DetectedCount,
                    ComparisonSummary.MatchedCount,
                    ComparisonSummary.MissedCount,
                    ComparisonSummary.FalsePositiveCount,
                    ComparisonSummary.MeanAbsoluteErrorSamples);
            }
        }

        public void ResetDefaults()
        {
            SampleCount = 512;
            SampleSpacing = 0.05;
            RandomSeed = 42;

            RawNoiseLevel = 0.03;
            RawAttenuation = 1.2;
            RawBaselineOffset = 0.06;
            UseRawSineSignal = false;
            RawSineFrequency = 4.0;
            RawSineAmplitude = 1.0;
            RawSinePhaseDegrees = 0.0;

            ReferenceAmplitude = 1.0;
            ReferenceDelaySamples = 14;
            ReferenceWidth = 3.5;
            ReferenceNoiseLevel = 0.01;
            ReferenceBaselineOffset = 0.02;
            ReferencePhaseShiftDegrees = 12.0;
            UseReferenceSineSignal = false;
            ReferenceSineFrequency = 4.0;
            ReferenceSineAmplitude = 1.0;
            ReferenceSinePhaseDegrees = 0.0;

            FftZeroPadFactor = 2;
            UseHannWindow = true;

            SegmentSize = 64;
            SegmentHop = 32;

            ConjugateMultiplyScale = 1.0;
            NormalizeConjugateMultiply = true;
            SelectedConjMulViewMode = "Magnitude";
            selectedFinalMetricMode = SignalProcessingPipeline.FinalMetricAbsolutePeakOffset;
            OnPropertyChanged(nameof(SelectedFinalMetricMode));

            FinalSmoothingWindow = 7;
            PeakThresholdRatio = 0.34;

            ReflectionEvents.Clear();
            ReflectionEvents.Add(new ReflectionEvent { PositionIndex = 2, Amplitude = 6.0, Width = 0.0 });
            ReflectionEvents.Add(new ReflectionEvent { PositionIndex = 4, Amplitude = -4.0, Width = 0.0 });
            ReflectionEvents.Add(new ReflectionEvent { PositionIndex = 6, Amplitude = 3.0, Width = 0.0 });
            SelectedReflectionEvent = ReflectionEvents.FirstOrDefault();

            loadedRawSignal = null;
            loadedReferenceSignal = null;
            RawCsvPath = string.Empty;
            ReferenceCsvPath = string.Empty;
            ConvertedCsvPath = string.Empty;
            OnPropertyChanged(nameof(RawCsvStatus));
            OnPropertyChanged(nameof(ReferenceCsvStatus));

            currentSignal = null;
            ClearPipelineState();
            StageResults.Clear();
            SelectedStage = null;
            DetectedEvents.Clear();
            MatchResults.Clear();
            ComparisonSummary = null;
            if (isfProcessingResult != null) isfProcessingResult.Dispose();
            isfProcessingResult = null;
            isfSegmentInspection = null;
            IsfSegmentIndex = 0;
            IsfProgressPercent = 0.0;
            IsfProgressText = "ISF 폴더를 Scan할 준비가 되었습니다.";
            OnPropertyChanged(nameof(IsRealDataActive));
            OnPropertyChanged(nameof(IsSyntheticControlsEnabled));
            OnPropertyChanged(nameof(IsfProcessingSummary));
            StatusMessage = "기본값을 복원했습니다. Raw/Ref data를 생성하거나 CSV 파일을 불러오세요.";
        }

        private async Task ScanIsfFolderAsync()
        {
            if (string.IsNullOrWhiteSpace(IsfInputFolder) || !Directory.Exists(IsfInputFolder))
            {
                StatusMessage = "먼저 올바른 ISF 입력 폴더를 선택하세요.";
                return;
            }
            BeginIsfOperation("ISF header를 Scan하는 중...");
            try
            {
                var manifest = await Task.Run(() => isfDatasetScanner.Scan(IsfInputFolder));
                ReplaceItems(IsfSets, manifest.Sets);
                SelectedIsfSet = IsfSets.FirstOrDefault(item => item.IsValid) ?? IsfSets.FirstOrDefault();
                IsfProgressPercent = 100.0;
                IsfProgressText = string.Format(CultureInfo.InvariantCulture, "{0}개 Set을 찾았고, 그중 {1}개가 유효합니다.", IsfSets.Count, IsfSets.Count(item => item.IsValid));
                StatusMessage = IsfProgressText;
            }
            catch (Exception ex)
            {
                IsfProgressText = "Scan 실패: " + ex.Message;
                StatusMessage = IsfProgressText;
            }
            finally { EndIsfOperation(); }
        }

        private void BrowseIsfInputFolder()
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "CH1/CH2/CH3 OFDR ISF 파일이 들어 있는 폴더를 선택하세요.",
                SelectedPath = Directory.Exists(IsfInputFolder) ? IsfInputFolder : string.Empty,
                ShowNewFolderButton = false
            })
            {
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    IsfInputFolder = dialog.SelectedPath;
                }
            }
        }

        private void BrowseIsfOutputFolder()
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "통합 CSV 파일과 JSON sidecar를 저장할 출력 폴더를 선택하세요.",
                SelectedPath = Directory.Exists(IsfOutputFolder) ? IsfOutputFolder : IsfInputFolder,
                ShowNewFolderButton = true
            })
            {
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    IsfOutputFolder = dialog.SelectedPath;
                }
            }
        }

        private void BrowseConvertedCsv()
        {
            var path = PromptForCsv("변환된 OFDR 통합 CSV 열기");
            if (!string.IsNullOrWhiteSpace(path)) ConvertedCsvPath = path;
        }

        private async Task ConvertIsfSetsAsync(bool all)
        {
            var sets = all ? IsfSets.Where(item => item.IsValid).ToList() : new List<OfdrChannelSet> { SelectedIsfSet };
            sets = sets.Where(item => item != null && item.IsValid).ToList();
            if (sets.Count == 0) return;
            var overwrite = false;
            if (sets.Any(item => File.Exists(Path.Combine(IsfOutputFolder, item.SetId + ".csv"))))
            {
                overwrite = System.Windows.MessageBox.Show(
                    "기존 output이 최신이면 그대로 사용하고, 오래된 output이면 다시 생성합니다. 계속할까요?",
                    "ISF CSV output이 이미 있습니다",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;
                if (!overwrite) return;
            }
            BeginIsfOperation("ISF channel set을 CSV로 변환하는 중...");
            var progress = BuildIsfProgress();
            var outcomes = new List<IsfConversionOutcome>();
            try
            {
                var completedSets = 0;
                await Task.Run(() =>
                {
                    foreach (var set in sets)
                    {
                        isfCancellation.Token.ThrowIfCancellationRequested();
                        var setIndex = completedSets;
                        var setProgress = new InlineOperationProgress(value =>
                        {
                            if (value == null) return;
                            progress.Report(new OperationProgress
                            {
                                Stage = value.Stage,
                                Detail = string.Format(CultureInfo.InvariantCulture, "{0}/{1} {2}: {3}", setIndex + 1, sets.Count, set.SetId, value.Detail ?? string.Empty),
                                Completed = value.Completed,
                                Total = value.Total,
                                Percent = 100.0 * (setIndex + (value.Percent / 100.0)) / sets.Count
                            });
                        });
                        try
                        {
                            var result = isfCsvConverter.Convert(set, IsfOutputFolder, overwrite, isfCancellation.Token, setProgress);
                            outcomes.Add(new IsfConversionOutcome { Set = set, Status = result.Skipped ? "최신" : "변환 완료" });
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            outcomes.Add(new IsfConversionOutcome { Set = set, Status = "실패", Error = ex.Message });
                        }
                        completedSets++;
                    }
                });
                foreach (var outcome in outcomes) outcome.Set.CsvStatus = outcome.Status;
                IsfProgressPercent = 100.0;
                var failed = outcomes.Where(item => item.Error != null).ToList();
                IsfProgressText = failed.Count == 0
                    ? string.Format(CultureInfo.InvariantCulture, "{0}개 Set 변환/검증을 완료했습니다.", completedSets)
                    : string.Format(CultureInfo.InvariantCulture, "{0}개 Set까지 처리했고, {1}개가 실패했습니다: {2}", completedSets, failed.Count, string.Join(" | ", failed.Select(item => item.Set.SetId + ": " + item.Error)));
                StatusMessage = IsfProgressText;
            }
            catch (OperationCanceledException)
            {
                foreach (var outcome in outcomes) outcome.Set.CsvStatus = outcome.Status;
                IsfProgressText = "ISF 변환을 취소했습니다. partial output은 제거되었습니다.";
                StatusMessage = IsfProgressText;
            }
            catch (Exception ex)
            {
                IsfProgressText = "ISF 변환 실패: " + ex.Message;
                StatusMessage = IsfProgressText;
            }
            finally { EndIsfOperation(); }
        }

        private async Task ProcessSelectedIsfAsync()
        {
            if (SelectedIsfSet == null || !SelectedIsfSet.IsValid) return;
            var processingSet = SelectedIsfSet;
            BeginIsfOperation("선택한 5M Set을 읽고 처리하는 중...");
            var progress = BuildIsfProgress();
            try
            {
                var config = BuildOfdrProcessingConfig();
                var engine = isfProcessingEngine ?? (isfProcessingEngine = new OfdrProcessingEngine());
                var result = await Task.Run(() => engine.RunSet(processingSet, config, isfCancellation.Token, progress));
                if (isfProcessingResult != null) isfProcessingResult.Dispose();
                isfProcessingResult = result;
                isfSegmentInspection = null;
                processingSet.QcStatus = "통과";
                IsfSegmentIndex = 0;
                currentSignal = null;
                lastPipelineResult = null;
                OnPropertyChanged(nameof(IsRealDataActive));
                OnPropertyChanged(nameof(IsSyntheticControlsEnabled));
                OnPropertyChanged(nameof(IsfProcessingSummary));
                RebuildStageResults("Final Metric Profile");
                IsfProgressPercent = 100.0;
                IsfProgressText = "5M CH1 k-linearization과 Xpol/Ypol 처리를 완료했습니다.";
                StatusMessage = IsfProgressText;
            }
            catch (OperationCanceledException)
            {
                processingSet.QcStatus = "취소됨";
                IsfProgressText = "ISF 처리를 취소했습니다. 현재 stage 경계에서 안전하게 멈췄습니다.";
                StatusMessage = IsfProgressText;
            }
            catch (Exception ex)
            {
                processingSet.QcStatus = "실패";
                IsfProgressText = "ISF 처리 실패: " + ex.Message;
                StatusMessage = IsfProgressText;
            }
            finally { EndIsfOperation(); }
        }

        private OfdrProcessingConfig BuildOfdrProcessingConfig()
        {
            return new OfdrProcessingConfig
            {
                EnableKLinearization = true,
                MaxAuxNonmonotonicRatio = 1e-5,
                UseHannWindow = UseHannWindow,
                SegmentSize = Math.Max(2, SegmentSize),
                SegmentHop = Math.Max(1, SegmentHop),
                SegmentIfftZeroPadFactor = Math.Max(1, FftZeroPadFactor),
                ConjugateMultiplyScale = ConjugateMultiplyScale,
                NormalizeConjugateMultiply = NormalizeConjugateMultiply,
                FinalMetricMode = SelectedFinalMetricMode,
                FinalSmoothingWindow = Math.Max(1, FinalSmoothingWindow),
                PeakThresholdRatio = PeakThresholdRatio,
                MaxPreviewPoints = 10000
            };
        }

        private async Task ProcessConvertedCsvAsync()
        {
            if (string.IsNullOrWhiteSpace(ConvertedCsvPath) || !File.Exists(ConvertedCsvPath))
            {
                StatusMessage = "먼저 변환된 통합 CSV 파일을 선택하세요.";
                return;
            }

            BeginIsfOperation("변환된 CSV를 읽고 OFDR 처리를 준비하는 중...");
            var progress = BuildIsfProgress();
            try
            {
                var readProgress = new InlineOperationProgress(value =>
                {
                    if (value == null) return;
                    progress.Report(new OperationProgress
                    {
                        Stage = value.Stage,
                        Detail = value.Detail,
                        Completed = value.Completed,
                        Total = value.Total,
                        Percent = Math.Min(30.0, value.Percent * 0.30)
                    });
                });
                var data = await Task.Run(() => convertedCsvReader.Read(ConvertedCsvPath, isfCancellation.Token, readProgress));
                var config = BuildOfdrProcessingConfig();
                var engine = isfProcessingEngine ?? (isfProcessingEngine = new OfdrProcessingEngine());
                var engineProgress = new InlineOperationProgress(value =>
                {
                    if (value == null) return;
                    progress.Report(new OperationProgress
                    {
                        Stage = value.Stage,
                        Detail = value.Detail,
                        Completed = value.Completed,
                        Total = value.Total,
                        Percent = 30.0 + (70.0 * value.Percent / 100.0)
                    });
                });
                var result = await Task.Run(() => engine.RunConvertedCsv(data, config, isfCancellation.Token, engineProgress));
                if (isfProcessingResult != null) isfProcessingResult.Dispose();
                isfProcessingResult = result;
                isfSegmentInspection = null;
                IsfSegmentIndex = 0;
                currentSignal = null;
                lastPipelineResult = null;
                OnPropertyChanged(nameof(IsRealDataActive));
                OnPropertyChanged(nameof(IsSyntheticControlsEnabled));
                OnPropertyChanged(nameof(IsfProcessingSummary));
                RebuildStageResults("Final Metric Profile");
                IsfProgressPercent = 100.0;
                IsfProgressText = string.Format(CultureInfo.InvariantCulture, "CSV {0} 처리 완료: {1:N0} row", Path.GetFileName(ConvertedCsvPath), data.RowCount);
                StatusMessage = IsfProgressText;
            }
            catch (OperationCanceledException)
            {
                IsfProgressText = "CSV 처리를 취소했습니다. 현재 stage 경계에서 안전하게 멈췄습니다.";
                StatusMessage = IsfProgressText;
            }
            catch (Exception ex)
            {
                IsfProgressText = "CSV 처리 실패: " + ex.Message;
                StatusMessage = IsfProgressText;
            }
            finally { EndIsfOperation(); }
        }

        private void InspectIsfSegment()
        {
            if (isfProcessingResult == null || isfProcessingEngine == null) return;
            try
            {
                var selected = string.Equals(SelectedIsfPolarization, "Ypol", StringComparison.Ordinal) ? isfProcessingResult.YPolarization : isfProcessingResult.XPolarization;
                IsfSegmentIndex = Math.Max(0, Math.Min(selected.SegmentCount - 1, IsfSegmentIndex));
                isfSegmentInspection = isfProcessingEngine.InspectSegment(isfProcessingResult, SelectedIsfPolarization, IsfSegmentIndex);
                RebuildStageResults("IFFT");
                StatusMessage = string.Format(CultureInfo.InvariantCulture, "{0} segment {1:N0}을 확인했습니다.", SelectedIsfPolarization, IsfSegmentIndex);
            }
            catch (Exception ex) { StatusMessage = "Segment 확인 실패: " + ex.Message; }
        }

        private void CancelIsfOperation()
        {
            if (isfCancellation != null) isfCancellation.Cancel();
            IsfProgressText = "취소를 요청했습니다. 현재 FFT 또는 block 처리가 끝날 때까지 기다립니다.";
        }

        private void BeginIsfOperation(string message)
        {
            if (isfCancellation != null) isfCancellation.Dispose();
            isfCancellation = new CancellationTokenSource();
            IsfProgressPercent = 0.0;
            IsfProgressText = message;
            IsIsfBusy = true;
            OnPropertyChanged(nameof(IsSyntheticControlsEnabled));
        }

        private void EndIsfOperation()
        {
            IsIsfBusy = false;
            OnPropertyChanged(nameof(IsSyntheticControlsEnabled));
        }

        private IProgress<OperationProgress> BuildIsfProgress()
        {
            return new Progress<OperationProgress>(value =>
            {
                if (value == null) return;
                IsfProgressPercent = value.Percent;
                IsfProgressText = string.Format(CultureInfo.InvariantCulture, "{0}: {1}", value.Stage, value.Detail ?? string.Empty);
            });
        }

        private void RaiseIsfCommandStates()
        {
            scanIsfFolderCommand.RaiseCanExecuteChanged();
            convertSelectedIsfCommand.RaiseCanExecuteChanged();
            convertAllIsfCommand.RaiseCanExecuteChanged();
            processSelectedIsfCommand.RaiseCanExecuteChanged();
            browseConvertedCsvCommand.RaiseCanExecuteChanged();
            processConvertedCsvCommand.RaiseCanExecuteChanged();
            cancelIsfOperationCommand.RaiseCanExecuteChanged();
            inspectIsfSegmentCommand.RaiseCanExecuteChanged();
            browseIsfInputFolderCommand.RaiseCanExecuteChanged();
            browseIsfOutputFolderCommand.RaiseCanExecuteChanged();
            ResetDefaultsCommand.RaiseCanExecuteChanged();
        }

        private void AddReflectionEvent()
        {
            var nextIndex = ReflectionEvents.Count == 0 ? 1 : ReflectionEvents.Last().PositionIndex + 1;
            var reflectionEvent = new ReflectionEvent
            {
                PositionIndex = nextIndex,
                Amplitude = 4.0,
                Width = 0.0
            };

            ReflectionEvents.Add(reflectionEvent);
            SelectedReflectionEvent = reflectionEvent;
            StatusMessage = "새 Raw Segment IFFT shift event를 추가했습니다.";
        }

        private void RemoveSelectedEvent()
        {
            if (SelectedReflectionEvent == null)
            {
                return;
            }

            ReflectionEvents.Remove(SelectedReflectionEvent);
            SelectedReflectionEvent = ReflectionEvents.FirstOrDefault();
            StatusMessage = "선택한 Raw Segment IFFT shift event를 삭제했습니다.";
        }

        private void LoadRawCsv()
        {
            var path = PromptForCsv("Raw CSV 열기");
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            ClearIsfProcessingState();
            loadedRawSignal = csvSignalLoader.Load(path);
            RawCsvPath = path;
            ApplyLoadedSignalMetadata(loadedRawSignal);
            OnPropertyChanged(nameof(RawCsvStatus));
            currentSignal = null;
            ClearPipelineVisuals();
            StatusMessage = "Raw CSV를 불러왔습니다. 불러온 Raw signal을 생성/처리에 사용합니다.";
        }

        private void LoadReferenceCsv()
        {
            var path = PromptForCsv("Ref CSV 열기");
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            ClearIsfProcessingState();
            loadedReferenceSignal = csvSignalLoader.Load(path);
            ReferenceCsvPath = path;
            ApplyLoadedSignalMetadata(loadedReferenceSignal);
            OnPropertyChanged(nameof(ReferenceCsvStatus));
            currentSignal = null;
            ClearPipelineVisuals();
            StatusMessage = "Ref CSV를 불러왔습니다. 불러온 Ref signal을 생성/처리에 사용합니다.";
        }

        private void ClearRawCsv()
        {
            loadedRawSignal = null;
            RawCsvPath = string.Empty;
            OnPropertyChanged(nameof(RawCsvStatus));
            currentSignal = null;
            ClearPipelineVisuals();
            StatusMessage = "불러온 Raw CSV를 해제했습니다. 다시 Synthetic Raw 생성을 사용합니다.";
        }

        private void ClearReferenceCsv()
        {
            loadedReferenceSignal = null;
            ReferenceCsvPath = string.Empty;
            OnPropertyChanged(nameof(ReferenceCsvStatus));
            currentSignal = null;
            ClearPipelineVisuals();
            StatusMessage = "불러온 Ref CSV를 해제했습니다. 다시 Synthetic Ref 생성을 사용합니다.";
        }

        private void ExportReconstructedRawCsv()
        {
            if (currentSignal == null)
            {
                GenerateSignal();
            }

            if (lastPipelineResult == null)
            {
                RunPipeline();
            }

            if (lastPipelineResult == null || lastPipelineResult.ReconstructedRawComplexSignal == null || lastPipelineResult.ReconstructedRawComplexSignal.Length == 0)
            {
                StatusMessage = "아직 Reconstructed Raw signal이 없습니다. 먼저 Pipeline을 실행하세요.";
                return;
            }

            var path = PromptForSaveCsv("Reconstructed Raw CSV 저장", "synthetic_reconstructed_raw.csv");
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            csvSignalLoader.SaveSignal(path, lastPipelineResult.ReconstructedRawComplexSignal, SampleSpacing);
            var segmentPath = SaveSelectedFftSegmentCsv(path);
            StatusMessage = string.IsNullOrWhiteSpace(segmentPath)
                ? string.Format(CultureInfo.InvariantCulture, "Reconstructed Raw CSV를 저장했습니다: {0}", path)
                : string.Format(CultureInfo.InvariantCulture, "Reconstructed Raw CSV를 저장했습니다: {0} / 선택한 FFT Segment CSV: {1}", path, segmentPath);
        }

        public string IsfInputFolder
        {
            get { return isfInputFolder; }
            set
            {
                if (SetProperty(ref isfInputFolder, value) && !string.IsNullOrWhiteSpace(value))
                {
                    IsfOutputFolder = Path.Combine(value, "converted_csv");
                }
            }
        }

        public string IsfOutputFolder
        {
            get { return isfOutputFolder; }
            set { SetProperty(ref isfOutputFolder, value); }
        }

        public string ConvertedCsvPath
        {
            get { return convertedCsvPath; }
            set
            {
                if (SetProperty(ref convertedCsvPath, value)) processConvertedCsvCommand.RaiseCanExecuteChanged();
            }
        }

        public OfdrChannelSet SelectedIsfSet
        {
            get { return selectedIsfSet; }
            set
            {
                if (SetProperty(ref selectedIsfSet, value)) RaiseIsfCommandStates();
            }
        }

        public string IsfProgressText
        {
            get { return string.IsNullOrWhiteSpace(isfProgressText) ? "ISF 폴더를 Scan할 준비가 되었습니다." : isfProgressText; }
            private set { SetProperty(ref isfProgressText, value); }
        }

        public double IsfProgressPercent
        {
            get { return isfProgressPercent; }
            private set { SetProperty(ref isfProgressPercent, Math.Max(0.0, Math.Min(100.0, value))); }
        }

        public bool IsIsfBusy
        {
            get { return isIsfBusy; }
            private set
            {
                if (SetProperty(ref isIsfBusy, value)) RaiseIsfCommandStates();
            }
        }

        public string SelectedIsfPolarization
        {
            get { return selectedIsfPolarization; }
            set
            {
                if (SetProperty(ref selectedIsfPolarization, string.Equals(value, "Ypol", StringComparison.OrdinalIgnoreCase) ? "Ypol" : "Xpol") && isfProcessingResult != null)
                {
                    isfSegmentInspection = null;
                    RebuildStageResults(SelectedStage == null ? null : SelectedStage.Name);
                }
            }
        }

        public int IsfSegmentIndex
        {
            get { return isfSegmentIndex; }
            set { SetProperty(ref isfSegmentIndex, Math.Max(0, value)); }
        }

        public bool IsRealDataActive
        {
            get { return isfProcessingResult != null; }
        }

        public bool IsSyntheticControlsEnabled
        {
            get { return !IsRealDataActive && !IsIsfBusy; }
        }

        public string IsfProcessingSummary
        {
            get
            {
                if (isfProcessingResult == null) return "유효한 Set을 처리하면 k-linearized Xpol/Ypol 결과를 확인할 수 있습니다.";
                var selected = string.Equals(SelectedIsfPolarization, "Ypol", StringComparison.Ordinal) ? isfProcessingResult.YPolarization : isfProcessingResult.XPolarization;
                return string.Format(CultureInfo.InvariantCulture,
                    "{0} | {1:N0} sample | {2:N0} segment | CH1 phase span {3:G8} rad | nonmonotonic {4:G4}",
                    isfProcessingResult.SetId, selected.SampleCount, selected.SegmentCount,
                    isfProcessingResult.KLinearization.PhaseSpanRadians,
                    isfProcessingResult.KLinearization.NonmonotonicRatio);
            }
        }
        private void GenerateSignal()
        {
            ClearIsfProcessingState();
            var request = BuildRequest();
            var generated = signalGenerator.Generate(request);
            var hasExternalRaw = loadedRawSignal != null;
            var hasExternalRef = loadedReferenceSignal != null;
            var useReferenceAsRawBase = !hasExternalRaw && hasExternalRef;

            var referenceSignal = hasExternalRef ? loadedReferenceSignal.Samples : generated.ReferenceSignal;
            var referenceComplex = hasExternalRef ? loadedReferenceSignal.ComplexSamples : generated.ReferenceComplexSignal;

            var rawSignal = hasExternalRaw
                ? loadedRawSignal.Samples
                : useReferenceAsRawBase
                    ? signalGenerator.ApplyEventsToBaseSignal(referenceSignal, request)
                    : generated.RawSignal;
            var rawComplex = hasExternalRaw
                ? loadedRawSignal.ComplexSamples
                : useReferenceAsRawBase
                    ? signalGenerator.ApplyEventsToBaseComplexSignal(referenceComplex, request)
                    : generated.RawComplexSignal;

            currentSignal = new SyntheticSignalResult
            {
                RawSignal = rawSignal,
                ReferenceSignal = referenceSignal,
                RawComplexSignal = rawComplex,
                ReferenceComplexSignal = referenceComplex,
                GroundTruthEvents = hasExternalRaw ? new ReflectionEvent[0] : generated.GroundTruthEvents
            };

            SampleCount = Math.Max(rawSignal == null ? 0 : rawSignal.Length, referenceSignal == null ? 0 : referenceSignal.Length);

            ClearPipelineState();
            DetectedEvents.Clear();
            MatchResults.Clear();
            ComparisonSummary = null;
            RebuildStageResults("Raw Data");
            StatusMessage = useReferenceAsRawBase
                ? "불러온 Ref signal에서 Raw data를 준비했습니다. 설정한 segment shift event는 이후 Raw Segment IFFT stage에서 적용됩니다."
                : "Raw/Ref data를 준비했습니다. 불러온 CSV data가 있으면 Pipeline에 적용됩니다.";
        }

        private void RunPipeline()
        {
            if (currentSignal == null)
            {
                GenerateSignal();
            }

            var request = BuildRequest();
            var previousStageName = SelectedStage == null ? "Final Metric Profile" : SelectedStage.Name;
            lastPipelineResult = processingPipeline.Run(currentSignal, request);

            ReplaceItems(DetectedEvents, lastPipelineResult.DetectedEvents);
            ReplaceItems(MatchResults, lastPipelineResult.MatchResults);
            ComparisonSummary = lastPipelineResult.ComparisonSummary;

            UpdateSegmentLabels();
            RebuildStageResults(previousStageName);
            StatusMessage = string.Format(CultureInfo.InvariantCulture, "OFDR Pipeline을 완료했습니다. Segment 추출, ConjMul, zero-padded IFFT 처리를 마쳤고 Final Metric Profile은 {0}를 사용합니다.", lastPipelineResult.FinalMetricMode);
        }

        private void ClearIsfProcessingState()
        {
            if (isfProcessingResult == null) return;
            isfProcessingResult.Dispose();
            isfProcessingResult = null;
            isfSegmentInspection = null;
            OnPropertyChanged(nameof(IsRealDataActive));
            OnPropertyChanged(nameof(IsSyntheticControlsEnabled));
            OnPropertyChanged(nameof(IsfProcessingSummary));
            inspectIsfSegmentCommand.RaiseCanExecuteChanged();
        }

        private SyntheticSignalRequest BuildRequest()
        {
            return new SyntheticSignalRequest
            {
                SampleCount = Math.Max(1, SampleCount),
                SampleSpacing = Math.Max(0.0000001, SampleSpacing),
                RandomSeed = RandomSeed,
                RawNoiseLevel = Math.Max(0.0, RawNoiseLevel),
                RawAttenuation = Math.Max(0.0, RawAttenuation),
                RawBaselineOffset = RawBaselineOffset,
                UseRawSineSignal = UseRawSineSignal,
                RawSineFrequency = RawSineFrequency,
                RawSineAmplitude = RawSineAmplitude,
                RawSinePhaseDegrees = RawSinePhaseDegrees,
                ReferenceAmplitude = ReferenceAmplitude,
                ReferenceDelaySamples = Math.Max(0, ReferenceDelaySamples),
                ReferenceWidth = Math.Max(1.0, ReferenceWidth),
                ReferenceNoiseLevel = Math.Max(0.0, ReferenceNoiseLevel),
                ReferenceBaselineOffset = ReferenceBaselineOffset,
                ReferencePhaseShiftDegrees = ReferencePhaseShiftDegrees,
                UseReferenceSineSignal = UseReferenceSineSignal,
                ReferenceSineFrequency = ReferenceSineFrequency,
                ReferenceSineAmplitude = ReferenceSineAmplitude,
                ReferenceSinePhaseDegrees = ReferenceSinePhaseDegrees,
                FftZeroPadFactor = Math.Max(1, FftZeroPadFactor),
                UseHannWindow = UseHannWindow,
                SegmentSize = Math.Max(2, SegmentSize),
                SegmentHop = Math.Max(1, SegmentHop),
                ConjugateMultiplyScale = ConjugateMultiplyScale,
                NormalizeConjugateMultiply = NormalizeConjugateMultiply,
                FinalSmoothingWindow = Math.Max(1, FinalSmoothingWindow),
                PeakThresholdRatio = PeakThresholdRatio,
                FinalMetricMode = SelectedFinalMetricMode,
                Events = ReflectionEvents.Select(item => item.Clone()).ToList()
            };
        }

        private void ApplyLoadedSignalMetadata(LoadedSignalData loadedSignal)
        {
            if (loadedSignal == null)
            {
                return;
            }

            SampleCount = loadedSignal.Samples == null ? SampleCount : loadedSignal.Samples.Length;
            if (loadedSignal.SampleSpacing.HasValue)
            {
                SampleSpacing = loadedSignal.SampleSpacing.Value;
            }
        }

        private void ClearPipelineVisuals()
        {
            ClearPipelineState();
            StageResults.Clear();
            SelectedStage = null;
            DetectedEvents.Clear();
            MatchResults.Clear();
            ComparisonSummary = null;
        }

        private void ClearPipelineState()
        {
            lastPipelineResult = null;
            SegmentLabels.Clear();
            selectedSegmentIndex = -1;
            OnPropertyChanged(nameof(SelectedSegmentIndex));
            OnPropertyChanged(nameof(SegmentCount));
            OnPropertyChanged(nameof(SelectedSegmentSummary));
        }

        private void UpdateSegmentLabels()
        {
            SegmentLabels.Clear();
            if (lastPipelineResult == null || lastPipelineResult.RawSegments == null)
            {
                selectedSegmentIndex = -1;
                OnPropertyChanged(nameof(SelectedSegmentIndex));
                OnPropertyChanged(nameof(SegmentCount));
                OnPropertyChanged(nameof(SelectedSegmentSummary));
                return;
            }

            for (var index = 0; index < lastPipelineResult.RawSegments.Count; index++)
            {
                var start = lastPipelineResult.SegmentStartIndices[index];
                var center = lastPipelineResult.SegmentCenterIndices[index];
                SegmentLabels.Add(string.Format(CultureInfo.InvariantCulture, "Segment {0} (start {1}, center {2})", index + 1, start, center));
            }

            if (SegmentLabels.Count == 0)
            {
                selectedSegmentIndex = -1;
            }
            else if (selectedSegmentIndex < 0 || selectedSegmentIndex >= SegmentLabels.Count)
            {
                selectedSegmentIndex = 0;
            }

            OnPropertyChanged(nameof(SelectedSegmentIndex));
            OnPropertyChanged(nameof(SegmentCount));
            OnPropertyChanged(nameof(SelectedSegmentSummary));
        }

        private void RebuildStageResults(string preferredStageName = null)
        {
            if (isfProcessingResult != null)
            {
                RebuildIsfStageResults(preferredStageName);
                return;
            }

            var stages = new List<StageResult>();
            if (currentSignal != null)
            {
                var rawDescription = loadedRawSignal != null
                    ? "FFT 전 단계의 불러온 Raw CSV signal입니다."
                    : loadedReferenceSignal != null
                        ? "불러온 Ref CSV에서 만든 Raw signal입니다. Segment shift event는 이후 Raw Segment IFFT source stage에서 적용됩니다."
                        : "FFT 전 단계의 생성된 Raw interferometric signal입니다.";
                var rawSamples = ToSignalDisplaySamples(currentSignal.RawComplexSignal, currentSignal.RawSignal);
                var referenceSamples = ToSignalDisplaySamples(currentSignal.ReferenceComplexSignal, currentSignal.ReferenceSignal);

                stages.Add(BuildStage(
                    "Raw Data",
                    rawDescription,
                    rawSamples,
                    new int[0],
                    "Sample Index",
                    GetSignalDisplayYAxisTitle(currentSignal.RawComplexSignal),
                    0.0,
                    Math.Max(0, rawSamples.Length - 1)));

                stages.Add(BuildStage(
                    "Ref Data",
                    loadedReferenceSignal == null ? "FFT 전 단계의 생성된 Ref-arm signal입니다." : "FFT 전 단계의 불러온 Ref CSV signal입니다.",
                    referenceSamples,
                    loadedReferenceSignal == null ? new[] { ReferenceDelaySamples } : new int[0],
                    "Sample Index",
                    GetSignalDisplayYAxisTitle(currentSignal.ReferenceComplexSignal),
                    0.0,
                    Math.Max(0, referenceSamples.Length - 1)));
            }

            if (lastPipelineResult != null)
            {
                stages.Add(BuildStage(
                    "Raw FFT",
                    "전체 Raw FFT magnitude입니다. 이 FFT를 local block으로 나눈 뒤 ConjMul과 IFFT 처리를 진행합니다.",
                    lastPipelineResult.RawSpectrumMagnitudes,
                    new int[0],
                    "FFT Bin",
                    "Magnitude",
                    0.0,
                    Math.Max(0, lastPipelineResult.RawSpectrumMagnitudes.Length - 1)));

                stages.Add(BuildStage(
                    "Ref FFT",
                    "전체 Ref FFT magnitude입니다. 같은 위치의 FFT block을 Reconstructed Raw spectrum과 비교합니다.",
                    lastPipelineResult.ReferenceSpectrumMagnitudes,
                    new int[0],
                    "FFT Bin",
                    "Magnitude",
                    0.0,
                    Math.Max(0, lastPipelineResult.ReferenceSpectrumMagnitudes.Length - 1)));

                if (HasSelectedSegment())
                {
                    var segmentIndex = SelectedSegmentIndex;
                    var start = lastPipelineResult.SegmentStartIndices[segmentIndex];
                    var center = lastPipelineResult.SegmentCenterIndices[segmentIndex];
                    var segmentLength = lastPipelineResult.RawSegments[segmentIndex].Length;
                    var end = start + segmentLength - 1;

                    var syntheticRawIfftLength = lastPipelineResult.RawIfftSegments[segmentIndex].Length;
                    var syntheticRawZeroLagIndex = syntheticRawIfftLength / 2;
                    var syntheticRawLagEnd = syntheticRawIfftLength - syntheticRawZeroLagIndex - 1;
                    var syntheticReferenceIfftLength = lastPipelineResult.ReferenceIfftSegments[segmentIndex].Length;
                    var syntheticReferenceZeroLagIndex = syntheticReferenceIfftLength / 2;
                    var syntheticReferenceLagEnd = syntheticReferenceIfftLength - syntheticReferenceZeroLagIndex - 1;

                    var ifftLength = lastPipelineResult.IfftSegments[segmentIndex].Length;
                    var ifftZeroLagIndex = ifftLength / 2;
                    var ifftLagEnd = ifftLength - ifftZeroLagIndex - 1;
                    var ifftPeakIndex = lastPipelineResult.IfftPeakIndices[segmentIndex];

                    stages.Add(BuildStage(
                        "Synthetic Raw Segment IFFT",
                        BuildRawSegmentIfftDescription(segmentIndex, start, end, center),
                        lastPipelineResult.RawIfftSegments[segmentIndex].Select(value => value.Magnitude).ToArray(),
                        BuildRawSegmentIfftMarkers(segmentIndex, syntheticRawZeroLagIndex, syntheticRawIfftLength),
                        "Lag Bin",
                        "Magnitude",
                        -syntheticRawZeroLagIndex,
                        syntheticRawLagEnd));

                    stages.Add(BuildStage(
                        "Reference Segment IFFT Source",
                        BuildSegmentDescription("Ref FFT block을 synthetic shift 생성에 쓰는 zero-padded IFFT 보기로 변환했습니다.", segmentIndex, start, end, center),
                        lastPipelineResult.ReferenceIfftSegments[segmentIndex].Select(value => value.Magnitude).ToArray(),
                        new[] { syntheticReferenceZeroLagIndex },
                        "Lag Bin",
                        "Magnitude",
                        -syntheticReferenceZeroLagIndex,
                        syntheticReferenceLagEnd));
                }

                var reconstructedSamples = ToSignalDisplaySamples(lastPipelineResult.ReconstructedRawComplexSignal, lastPipelineResult.ReconstructedRawSignal);
                stages.Add(BuildStage(
                    "Reconstructed Raw",
                    "Shift가 적용된 Raw Segment IFFT block을 다시 spectrum domain으로 FFT하고, segment를 overlap-average한 뒤 inverse full FFT로 복원한 Raw signal입니다.",
                    reconstructedSamples,
                    new int[0],
                    "Sample Index",
                    GetSignalDisplayYAxisTitle(lastPipelineResult.ReconstructedRawComplexSignal),
                    0.0,
                    Math.Max(0, reconstructedSamples.Length - 1)));

                if (HasSelectedSegment())
                {
                    var segmentIndex = SelectedSegmentIndex;
                    var start = lastPipelineResult.SegmentStartIndices[segmentIndex];
                    var center = lastPipelineResult.SegmentCenterIndices[segmentIndex];
                    var segmentLength = lastPipelineResult.RawSegments[segmentIndex].Length;
                    var end = start + segmentLength - 1;
                    var ifftLength = lastPipelineResult.IfftSegments[segmentIndex].Length;
                    var ifftZeroLagIndex = ifftLength / 2;
                    var ifftLagEnd = ifftLength - ifftZeroLagIndex - 1;
                    var ifftPeakIndex = lastPipelineResult.IfftPeakIndices[segmentIndex];

                    stages.Add(BuildStage(
                        "Raw Segmentation",
                        BuildSegmentDescription("Local ConjMul 처리에 사용할 Reconstructed Raw spectrum의 선택 FFT window입니다.", segmentIndex, start, end, center),
                        lastPipelineResult.RawSegments[segmentIndex].Select(value => value.Magnitude).ToArray(),
                        new int[0],
                        "FFT Bin",
                        "Magnitude",
                        start,
                        end));

                    stages.Add(BuildStage(
                        "Ref Segmentation",
                        BuildSegmentDescription("같은 start와 span으로 추출한 Ref FFT window입니다.", segmentIndex, start, end, center),
                        lastPipelineResult.ReferenceSegments[segmentIndex].Select(value => value.Magnitude).ToArray(),
                        new int[0],
                        "FFT Bin",
                        "Magnitude",
                        start,
                        end));

                    stages.Add(BuildStage(
                        "ConjMul",
                        BuildSegmentDescription("FFT domain에서 Segment별 Raw * Conj(Ref)를 비교합니다.", segmentIndex, start, end, center),
                        ProjectComplexView(lastPipelineResult.ConjugateMultiplySegments[segmentIndex], SelectedConjMulViewMode),
                        new int[0],
                        "FFT Bin",
                        GetComplexViewYAxisTitle(SelectedConjMulViewMode),
                        start,
                        end));

                    stages.Add(BuildStage(
                        "IFFT",
                        BuildSegmentDescription("선택한 ConjMul block에 IFFT를 적용했습니다. Zero-lag를 가운데에 두고 검출된 peak를 표시합니다.", segmentIndex, start, end, center),
                        ProjectComplexView(lastPipelineResult.IfftSegments[segmentIndex], SelectedConjMulViewMode),
                        new[] { ifftZeroLagIndex, ifftPeakIndex },
                        "Lag Bin",
                        GetComplexViewYAxisTitle(SelectedConjMulViewMode),
                        -ifftZeroLagIndex,
                        ifftLagEnd));
                }

                stages.Add(BuildStage(
                    "Final Metric Profile",
                    string.Format(CultureInfo.InvariantCulture, "{0}로 만든 Segment별 IFFT feature profile입니다. 값은 FFT-bin axis의 segment-center 위치에 매핑됩니다.", lastPipelineResult.FinalMetricMode),
                    lastPipelineResult.FinalProfile,
                    BuildFinalMetricProfileMarkers(),
                    "FFT Bin / Segment Position",
                    SignalProcessingPipeline.GetFinalMetricYAxisTitle(lastPipelineResult.FinalMetricMode),
                    0.0,
                    Math.Max(0, lastPipelineResult.FinalProfile.Length - 1),
                    lastPipelineResult.DetectedEvents));
            }

            ReplaceItems(StageResults, stages);
            var targetStageName = preferredStageName ?? (SelectedStage == null ? null : SelectedStage.Name);
            SelectedStage = targetStageName == null
                ? StageResults.FirstOrDefault()
                : StageResults.FirstOrDefault(item => string.Equals(item.Name, targetStageName, StringComparison.Ordinal)) ?? StageResults.FirstOrDefault();
        }

        private void RebuildIsfStageResults(string preferredStageName)
        {
            var selected = string.Equals(SelectedIsfPolarization, "Ypol", StringComparison.Ordinal)
                ? isfProcessingResult.YPolarization
                : isfProcessingResult.XPolarization;
            var stages = new List<StageResult>
            {
                BuildPreviewStage(
                    "CH1 k-linearized Ref",
                    "CH1 auxiliary interferometer에 mean 제거, analytic-phase unwrap, monotonic-anchor QC를 적용한 뒤 uniform normalized-k grid로 보간한 결과입니다.",
                    isfProcessingResult.ReferencePreview),
                BuildPreviewStage(
                    "CH1 Ref FFT",
                    "Xpol/Ypol 양쪽에서 공통으로 쓰는 CH1 reference spectrum입니다. Axis는 FFT bin이며 physical-distance calibration은 적용하지 않았습니다.",
                    isfProcessingResult.ReferenceSpectrumPreview),
                BuildPreviewStage(
                    SelectedIsfPolarization + " k-linearized Signal",
                    "CH1에서 만든 uniform normalized-k grid 위로 " + SelectedIsfPolarization + " voltage를 보간한 결과입니다.",
                    selected.SignalPreview),
                BuildPreviewStage(
                    SelectedIsfPolarization + " FFT",
                    "전체 " + SelectedIsfPolarization + " spectrum을 최대 10,000 point min/max envelope preview로 표시합니다.",
                    selected.SpectrumPreview)
            };

            if (isfSegmentInspection != null)
            {
                var inspection = isfSegmentInspection;
                var segmentEnd = inspection.StartIndex + inspection.RawSegment.Length - 1;
                stages.Add(BuildStage(
                    "Raw Segmentation",
                    string.Format(CultureInfo.InvariantCulture, "{0}의 segment {1:N0} FFT block을 선택했습니다. 전체 segment array는 필요할 때만 다시 계산합니다.", SelectedIsfPolarization, inspection.SegmentIndex),
                    inspection.RawSegment.Select(value => value.Magnitude).ToArray(),
                    new int[0], "FFT bin", "Magnitude", inspection.StartIndex, segmentEnd));
                stages.Add(BuildStage(
                    "Ref Segmentation",
                    "Local complex 비교에 사용하는 같은 위치의 CH1 reference FFT block입니다.",
                    inspection.ReferenceSegment.Select(value => value.Magnitude).ToArray(),
                    new int[0], "FFT bin", "Magnitude", inspection.StartIndex, segmentEnd));
                stages.Add(BuildStage(
                    "ConjMul",
                    SelectedIsfPolarization + " x conj(CH1)에 설정한 scaling과 normalization을 적용한 결과입니다.",
                    ProjectComplexView(inspection.ConjugateMultiply, SelectedConjMulViewMode),
                    new int[0], "FFT bin", GetComplexViewYAxisTitle(SelectedConjMulViewMode), inspection.StartIndex, segmentEnd));
                stages.Add(BuildStage(
                    "IFFT",
                    "Zero-padded Segment IFFT입니다. Zero-lag를 가운데에 두고 대표 peak를 표시합니다.",
                    ProjectComplexView(inspection.ShiftedIfft, SelectedConjMulViewMode),
                    new[] { inspection.ZeroLagIndex, inspection.PeakIndex },
                    "Lag bin", GetComplexViewYAxisTitle(SelectedConjMulViewMode),
                    -inspection.ZeroLagIndex, inspection.ShiftedIfft.Length - inspection.ZeroLagIndex - 1));
            }

            var detected = selected.DetectedFeatures.Select(item => new DetectedEvent
            {
                PositionIndex = item.PositionIndex,
                Amplitude = item.Value,
                Distance = item.PositionIndex
            }).ToList();
            var markers = selected.DetectedFeatures
                .Select(item => MapXValueToPreviewIndex(item.PositionIndex, selected.FinalProfilePreview))
                .Distinct()
                .ToArray();
            stages.Add(BuildStage(
                "Final Metric Profile",
                string.Format(CultureInfo.InvariantCulture, "{0} streaming segment profile입니다. Metric은 {1}를 사용하고, 위치는 physical-distance calibration 전까지 FFT/segment bin으로 표시합니다.", SelectedIsfPolarization, isfProcessingResult.Config.FinalMetricMode),
                selected.FinalProfilePreview.Values,
                markers,
                selected.FinalProfilePreview.XAxisTitle,
                selected.FinalProfilePreview.YAxisTitle,
                selected.FinalProfilePreview.XStart,
                selected.FinalProfilePreview.XEnd,
                detected));

            ReplaceItems(StageResults, stages);
            ReplaceItems(DetectedEvents, detected);
            MatchResults.Clear();
            ComparisonSummary = null;
            var targetStageName = preferredStageName ?? (SelectedStage == null ? null : SelectedStage.Name);
            SelectedStage = targetStageName == null
                ? StageResults.FirstOrDefault()
                : StageResults.FirstOrDefault(item => string.Equals(item.Name, targetStageName, StringComparison.Ordinal)) ?? StageResults.FirstOrDefault();
        }

        private static StageResult BuildPreviewStage(string name, string description, PreviewSeries preview)
        {
            preview = preview ?? new PreviewSeries { Values = new double[0] };
            return BuildStage(name, description, preview.Values, new int[0], preview.XAxisTitle, preview.YAxisTitle, preview.XStart, preview.XEnd);
        }

        private static int MapXValueToPreviewIndex(double xValue, PreviewSeries preview)
        {
            if (preview == null || preview.Values == null || preview.Values.Length <= 1 || preview.XEnd <= preview.XStart) return 0;
            var ratio = (xValue - preview.XStart) / (preview.XEnd - preview.XStart);
            return Math.Max(0, Math.Min(preview.Values.Length - 1, (int)Math.Round(ratio * (preview.Values.Length - 1))));
        }

        private void SyncSelectedCorrelationLag(bool preferPeak)
        {
            var bounds = GetSelectedCorrelationLagBounds();
            var desired = preferPeak ? GetSelectedCorrelationPeakLag() : Math.Max(bounds.Item1, Math.Min(bounds.Item2, selectedCorrelationLag));
            if (selectedCorrelationLag != desired)
            {
                selectedCorrelationLag = desired;
                OnPropertyChanged(nameof(SelectedCorrelationLag));
            }

            NotifyCorrelationInspectorChanged();
        }

        private void NotifyCorrelationInspectorChanged()
        {
            OnPropertyChanged(nameof(IsCorrelationInspectorVisible));
            OnPropertyChanged(nameof(SelectedCorrelationLagMinimum));
            OnPropertyChanged(nameof(SelectedCorrelationLagMaximum));
            OnPropertyChanged(nameof(SelectedCorrelationLagSummary));
            OnPropertyChanged(nameof(CorrelationOverlayRawSamples));
            OnPropertyChanged(nameof(CorrelationOverlayReferenceSamples));
            OnPropertyChanged(nameof(CorrelationOverlayXEndValue));
        }

        private Tuple<int, int> GetSelectedCorrelationLagBounds()
        {
            if (!HasSelectedSegment())
            {
                return Tuple.Create(0, 0);
            }

            var rawLength = lastPipelineResult.RawIfftSegments[SelectedSegmentIndex].Length;
            var referenceLength = lastPipelineResult.ReferenceIfftSegments[SelectedSegmentIndex].Length;
            return Tuple.Create(-Math.Max(0, referenceLength - 1), Math.Max(0, rawLength - 1));
        }

        private int GetSelectedCorrelationPeakLag()
        {
            if (!HasSelectedSegment())
            {
                return 0;
            }

            var zeroLagIndex = Math.Max(0, lastPipelineResult.ReferenceIfftSegments[SelectedSegmentIndex].Length - 1);
            return lastPipelineResult.IfftPeakIndices[SelectedSegmentIndex] - zeroLagIndex;
        }

        private static double[] BuildLagShiftedReferenceOverlay(int targetLength, double[] referenceSamples, int lag)
        {
            targetLength = Math.Max(0, targetLength);
            referenceSamples = referenceSamples ?? new double[0];
            var shifted = new double[targetLength];
            for (var displayIndex = 0; displayIndex < targetLength; displayIndex++)
            {
                var sourceIndex = displayIndex - lag;
                shifted[displayIndex] = sourceIndex >= 0 && sourceIndex < referenceSamples.Length ? referenceSamples[sourceIndex] : 0.0;
            }

            return shifted;
        }

        private static Tuple<int, double, double> CalculateLagOverlapStats(double[] rawSamples, double[] referenceSamples, int lag)
        {
            rawSamples = rawSamples ?? new double[0];
            referenceSamples = referenceSamples ?? new double[0];
            var overlapCount = 0;
            double absSum = 0.0;
            double squaredSum = 0.0;

            for (var rawIndex = 0; rawIndex < rawSamples.Length; rawIndex++)
            {
                var referenceIndex = rawIndex - lag;
                if (referenceIndex < 0 || referenceIndex >= referenceSamples.Length)
                {
                    continue;
                }

                var diff = rawSamples[rawIndex] - referenceSamples[referenceIndex];
                absSum += Math.Abs(diff);
                squaredSum += diff * diff;
                overlapCount++;
            }

            if (overlapCount == 0)
            {
                return Tuple.Create(0, 0.0, 0.0);
            }

            return Tuple.Create(overlapCount, absSum / overlapCount, Math.Sqrt(squaredSum / overlapCount));
        }

        private static string PromptForSaveCsv(string title, string defaultFileName)
        {
            var dialog = new SaveFileDialog
            {
                Title = title,
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = defaultFileName,
                AddExtension = true,
                DefaultExt = ".csv",
                OverwritePrompt = true
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        private string SaveSelectedFftSegmentCsv(string reconstructedRawPath)
        {
            if (!HasSelectedSegment())
            {
                return null;
            }

            var segmentIndex = SelectedSegmentIndex;
            var rawSegment = lastPipelineResult.RawSegments[segmentIndex] ?? new System.Numerics.Complex[0];
            var referenceSegment = lastPipelineResult.ReferenceSegments[segmentIndex] ?? new System.Numerics.Complex[0];
            var length = Math.Min(rawSegment.Length, referenceSegment.Length);
            if (length == 0)
            {
                return null;
            }

            var start = lastPipelineResult.SegmentStartIndices[segmentIndex];
            var directory = Path.GetDirectoryName(reconstructedRawPath) ?? string.Empty;
            var fileName = Path.GetFileNameWithoutExtension(reconstructedRawPath);
            var segmentPath = Path.Combine(directory, string.Format(CultureInfo.InvariantCulture, "{0}_segment_{1:D3}_fft.csv", fileName, segmentIndex + 1));
            var lines = new List<string>(length + 1)
            {
                "LocalIndex,FftBin,RawMagnitude,RawI_Re,RawQ_Im,RefMagnitude,RefI_Re,RefQ_Im"
            };

            for (var localIndex = 0; localIndex < length; localIndex++)
            {
                var rawValue = rawSegment[localIndex];
                var referenceValue = referenceSegment[localIndex];
                lines.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0},{1},{2:F12},{3:F12},{4:F12},{5:F12},{6:F12},{7:F12}",
                    localIndex,
                    start + localIndex,
                    rawValue.Magnitude,
                    rawValue.Real,
                    rawValue.Imaginary,
                    referenceValue.Magnitude,
                    referenceValue.Real,
                    referenceValue.Imaginary));
            }

            File.WriteAllLines(segmentPath, lines);
            return segmentPath;
        }
        private bool HasSelectedSegment()
        {
            return lastPipelineResult != null &&
                   lastPipelineResult.RawSegments != null &&
                   SelectedSegmentIndex >= 0 &&
                   SelectedSegmentIndex < lastPipelineResult.RawSegments.Count;
        }

        private static string PromptForCsv(string title)
        {
            var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        private static string BuildSegmentDescription(string prefix, int segmentIndex, int start, int end, int center)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} Segment {1}은 FFT bin {2}-{3} 범위이며 center는 {4}입니다.",
                prefix,
                segmentIndex + 1,
                start,
                end,
                center);
        }

        private static double[] ToSignalDisplaySamples(System.Numerics.Complex[] complexSamples, double[] realSamples)
        {
            if (complexSamples != null && complexSamples.Length > 0)
            {
                var hasImaginaryContent = complexSamples.Any(value => Math.Abs(value.Imaginary) > 1e-9);
                return hasImaginaryContent
                    ? complexSamples.Select(value => value.Magnitude).ToArray()
                    : complexSamples.Select(value => value.Real).ToArray();
            }

            return realSamples ?? new double[0];
        }

        private static string GetSignalDisplayYAxisTitle(System.Numerics.Complex[] complexSamples)
        {
            if (complexSamples != null && complexSamples.Length > 0)
            {
                var hasImaginaryContent = complexSamples.Any(value => Math.Abs(value.Imaginary) > 1e-9);
                return hasImaginaryContent ? "Magnitude" : "Signal";
            }

            return "Signal";
        }

        private static double[] ProjectComplexView(System.Numerics.Complex[] samples, string mode)
        {
            samples = samples ?? new System.Numerics.Complex[0];
            switch (mode)
            {
                case "Real":
                    return samples.Select(value => value.Real).ToArray();
                case "Imag":
                    return samples.Select(value => value.Imaginary).ToArray();
                case "Phase":
                    return samples.Select(value => Math.Atan2(value.Imaginary, value.Real)).ToArray();
                default:
                    return samples.Select(value => value.Magnitude).ToArray();
            }
        }

        private static string GetComplexViewYAxisTitle(string mode)
        {
            switch (mode)
            {
                case "Real":
                    return "Real";
                case "Imag":
                    return "Imaginary";
                case "Phase":
                    return "Phase (rad)";
                default:
                    return "Magnitude";
            }
        }

        private string BuildRawSegmentIfftDescription(int segmentIndex, int start, int end, int center)
        {
            var description = BuildSegmentDescription("선택한 Raw FFT Segment block을 zero-padding한 뒤 IFFT를 적용했습니다.", segmentIndex, start, end, center);
            var shiftBins = GetConfiguredShiftBinsForSegment(segmentIndex);
            return shiftBins == 0
                ? description
                : string.Format(CultureInfo.InvariantCulture, "{0} 이후 이 Raw Segment IFFT에 circular shift {1:+#;-#;0} bin을 직접 적용합니다.", description, shiftBins);
        }
        private int[] BuildRawSegmentIfftMarkers(int segmentIndex, int zeroLagIndex, int sampleLength)
        {
            if (sampleLength <= 0)
            {
                return new int[0];
            }
            var markers = new List<int>
            {
                Math.Max(0, Math.Min(sampleLength - 1, zeroLagIndex))
            };
            var shiftBins = GetConfiguredShiftBinsForSegment(segmentIndex);
            if (shiftBins != 0)
            {
                markers.Add(WrapIndex(zeroLagIndex + shiftBins, sampleLength));
            }
            return markers.Distinct().OrderBy(index => index).ToArray();
        }
        private int[] BuildFinalMetricProfileMarkers()
        {
            var markers = new List<int>();
            markers.AddRange(GetConfiguredSegmentCenterMarkers());
            if (lastPipelineResult != null && lastPipelineResult.DetectedEvents != null)
            {
                markers.AddRange(lastPipelineResult.DetectedEvents.Select(item => item.PositionIndex));
            }
            return markers.Distinct().OrderBy(index => index).ToArray();
        }
        private int[] GetConfiguredSegmentCenterMarkers()
        {
            if (lastPipelineResult == null || lastPipelineResult.SegmentCenterIndices == null)
            {
                return new int[0];
            }
            return ReflectionEvents
                .Where(item => item != null)
                .Select(item => item.PositionIndex - 1)
                .Where(index => index >= 0 && index < lastPipelineResult.SegmentCenterIndices.Count)
                .Select(index => lastPipelineResult.SegmentCenterIndices[index])
                .Distinct()
                .OrderBy(index => index)
                .ToArray();
        }
        private int GetConfiguredShiftBinsForSegment(int segmentIndex)
        {
            if (segmentIndex < 0)
            {
                return 0;
            }
            return ReflectionEvents
                .Where(item => item != null && item.PositionIndex == segmentIndex + 1)
                .Select(item => (int)Math.Round(item.Amplitude))
                .Sum();
        }
        private static int WrapIndex(int index, int sampleLength)
        {
            if (sampleLength <= 0)
            {
                return 0;
            }
            var wrapped = index % sampleLength;
            if (wrapped < 0)
            {
                wrapped += sampleLength;
            }
            return wrapped;
        }

        private static StageResult BuildStage(string name, string description, double[] samples, int[] markers, string xAxisTitle, string yAxisTitle, double xStartValue, double xEndValue, IList<DetectedEvent> detectedEvents = null)
        {
            markers = markers ?? new int[0];
            samples = samples ?? new double[0];
            return new StageResult
            {
                Name = name,
                Description = description,
                Samples = samples,
                MarkerIndices = markers,
                Metrics = StageMath.BuildMetrics(samples, markers.Length),
                DetectedEvents = detectedEvents ?? new DetectedEvent[0],
                XAxisTitle = xAxisTitle,
                YAxisTitle = yAxisTitle,
                XStartValue = xStartValue,
                XEndValue = xEndValue
            };
        }

        private static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> items)
        {
            target.Clear();
            if (items == null)
            {
                return;
            }

            foreach (var item in items)
            {
                target.Add(item);
            }
        }

        private sealed class InlineOperationProgress : IProgress<OperationProgress>
        {
            private readonly Action<OperationProgress> report;

            public InlineOperationProgress(Action<OperationProgress> reportAction)
            {
                report = reportAction;
            }

            public void Report(OperationProgress value)
            {
                report(value);
            }
        }

        private sealed class IsfConversionOutcome
        {
            public OfdrChannelSet Set { get; set; }
            public string Status { get; set; }
            public string Error { get; set; }
        }
    }
}
