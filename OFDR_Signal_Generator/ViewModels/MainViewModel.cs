using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Microsoft.Win32;
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
        private readonly RelayCommand removeEventCommand;
        private LoadedSignalData loadedRawSignal;
        private LoadedSignalData loadedReferenceSignal;
        private SyntheticSignalResult currentSignal;
        private PipelineRunResult lastPipelineResult;
        private ComparisonSummary comparisonSummary;
        private ReflectionEvent selectedReflectionEvent;
        private StageResult selectedStage;
        private int selectedSegmentIndex;
        private string selectedConjMulViewMode;
        private int sampleCount;
        private double sampleSpacing;
        private int randomSeed;
        private double rawNoiseLevel;
        private double rawAttenuation;
        private double rawBaselineOffset;
        private double referenceAmplitude;
        private int referenceDelaySamples;
        private double referenceWidth;
        private double referenceNoiseLevel;
        private double referenceBaselineOffset;
        private double referencePhaseShiftDegrees;
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

        public MainViewModel()
        {
            signalGenerator = new SyntheticSignalGenerator();
            processingPipeline = new SignalProcessingPipeline();
            csvSignalLoader = new CsvSignalLoader();

            ReflectionEvents = new ObservableCollection<ReflectionEvent>();
            StageResults = new ObservableCollection<StageResult>();
            DetectedEvents = new ObservableCollection<DetectedEvent>();
            MatchResults = new ObservableCollection<EventMatchResult>();
            SegmentLabels = new ObservableCollection<string>();
            ConjMulViewModes = new ObservableCollection<string>(new[] { "Magnitude", "Real", "Imag", "Phase" });
            selectedSegmentIndex = -1;
            selectedConjMulViewMode = "Magnitude";

            GenerateSignalCommand = new RelayCommand(GenerateSignal);
            RunPipelineCommand = new RelayCommand(RunPipeline);
            AddEventCommand = new RelayCommand(AddReflectionEvent);
            removeEventCommand = new RelayCommand(RemoveSelectedEvent, () => SelectedReflectionEvent != null);
            ResetDefaultsCommand = new RelayCommand(ResetDefaults);
            LoadRawCsvCommand = new RelayCommand(LoadRawCsv);
            LoadReferenceCsvCommand = new RelayCommand(LoadReferenceCsv);
            ClearRawCsvCommand = new RelayCommand(ClearRawCsv);
            ClearReferenceCsvCommand = new RelayCommand(ClearReferenceCsv);

            ResetDefaults();
        }

        public ObservableCollection<ReflectionEvent> ReflectionEvents { get; private set; }

        public ObservableCollection<StageResult> StageResults { get; private set; }

        public ObservableCollection<DetectedEvent> DetectedEvents { get; private set; }

        public ObservableCollection<EventMatchResult> MatchResults { get; private set; }

        public ObservableCollection<string> SegmentLabels { get; private set; }

        public ObservableCollection<string> ConjMulViewModes { get; private set; }

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

        public RelayCommand ResetDefaultsCommand { get; private set; }

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
                    return "Synthetic raw signal will be used.";
                }

                var mode = loadedRawSignal.HasImaginaryComponent ? "complex I/Q" : "real-valued";
                return string.Format(CultureInfo.InvariantCulture, "Loaded {0} {1} samples from {2}", loadedRawSignal.Samples.Length, mode, loadedRawSignal.SampleColumnName);
            }
        }

        public string ReferenceCsvStatus
        {
            get
            {
                if (loadedReferenceSignal == null)
                {
                    return "Synthetic reference signal will be used.";
                }

                var mode = loadedReferenceSignal.HasImaginaryComponent ? "complex I/Q" : "real-valued";
                return string.Format(CultureInfo.InvariantCulture, "Loaded {0} {1} samples from {2}", loadedReferenceSignal.Samples.Length, mode, loadedReferenceSignal.SampleColumnName);
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
                    RebuildStageResults();
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
                    RebuildStageResults();
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
                    return "Run the OFDR pipeline to inspect segment-by-segment Raw/Ref, ConjMul, and IFFT results.";
                }

                var start = lastPipelineResult.SegmentStartIndices[SelectedSegmentIndex];
                var center = lastPipelineResult.SegmentCenterIndices[SelectedSegmentIndex];
                var segmentLength = lastPipelineResult.RawSegments[SelectedSegmentIndex].Length;
                var peak = lastPipelineResult.IfftPeakIndices[SelectedSegmentIndex];
                var representative = lastPipelineResult.SegmentRepresentativeValues[SelectedSegmentIndex];
                var end = start + segmentLength - 1;

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "Segment {0}/{1} | FFT bins {2}-{3} | center {4} | shifted IFFT peak {5} | peak offset {6:0.0000} bins",
                    SelectedSegmentIndex + 1,
                    SegmentCount,
                    start,
                    end,
                    center,
                    peak,
                    representative);
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
                    return "Generate and process the OFDR signal set to see stage outputs and final correlation accuracy.";
                }

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "Expected {0} / Detected {1} / Matched {2} / Missed {3} / False+ {4} / MAE {5:0.00} samples",
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

            ReferenceAmplitude = 1.0;
            ReferenceDelaySamples = 14;
            ReferenceWidth = 3.5;
            ReferenceNoiseLevel = 0.01;
            ReferenceBaselineOffset = 0.02;
            ReferencePhaseShiftDegrees = 12.0;

            FftZeroPadFactor = 2;
            UseHannWindow = true;

            SegmentSize = 64;
            SegmentHop = 32;

            ConjugateMultiplyScale = 1.0;
            NormalizeConjugateMultiply = true;
            SelectedConjMulViewMode = "Magnitude";

            FinalSmoothingWindow = 7;
            PeakThresholdRatio = 0.34;

            ReflectionEvents.Clear();
            ReflectionEvents.Add(new ReflectionEvent { PositionIndex = 90, Amplitude = 1.2, Width = 5.0 });
            ReflectionEvents.Add(new ReflectionEvent { PositionIndex = 210, Amplitude = 0.8, Width = 7.0 });
            ReflectionEvents.Add(new ReflectionEvent { PositionIndex = 360, Amplitude = 1.0, Width = 6.0 });
            SelectedReflectionEvent = ReflectionEvents.FirstOrDefault();

            loadedRawSignal = null;
            loadedReferenceSignal = null;
            RawCsvPath = string.Empty;
            ReferenceCsvPath = string.Empty;
            OnPropertyChanged(nameof(RawCsvStatus));
            OnPropertyChanged(nameof(ReferenceCsvStatus));

            currentSignal = null;
            ClearPipelineState();
            StageResults.Clear();
            SelectedStage = null;
            DetectedEvents.Clear();
            MatchResults.Clear();
            ComparisonSummary = null;
            StatusMessage = "Defaults restored. Generate Raw/Ref data, or load CSV files for either signal path.";
        }

        private void AddReflectionEvent()
        {
            var nextIndex = ReflectionEvents.Count == 0 ? 50 : ReflectionEvents.Last().PositionIndex + 40;
            var reflectionEvent = new ReflectionEvent
            {
                PositionIndex = nextIndex,
                Amplitude = 0.7,
                Width = 5.0
            };

            ReflectionEvents.Add(reflectionEvent);
            SelectedReflectionEvent = reflectionEvent;
            StatusMessage = "Added a new raw reflection event.";
        }

        private void RemoveSelectedEvent()
        {
            if (SelectedReflectionEvent == null)
            {
                return;
            }

            ReflectionEvents.Remove(SelectedReflectionEvent);
            SelectedReflectionEvent = ReflectionEvents.FirstOrDefault();
            StatusMessage = "Removed the selected raw reflection event.";
        }

        private void LoadRawCsv()
        {
            var path = PromptForCsv("Load Raw CSV");
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            loadedRawSignal = csvSignalLoader.Load(path);
            RawCsvPath = path;
            ApplyLoadedSignalMetadata(loadedRawSignal);
            OnPropertyChanged(nameof(RawCsvStatus));
            currentSignal = null;
            ClearPipelineVisuals();
            StatusMessage = "Raw CSV loaded. The loaded raw signal will be used for generation and processing.";
        }

        private void LoadReferenceCsv()
        {
            var path = PromptForCsv("Load Reference CSV");
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            loadedReferenceSignal = csvSignalLoader.Load(path);
            ReferenceCsvPath = path;
            ApplyLoadedSignalMetadata(loadedReferenceSignal);
            OnPropertyChanged(nameof(ReferenceCsvStatus));
            currentSignal = null;
            ClearPipelineVisuals();
            StatusMessage = "Reference CSV loaded. The loaded reference signal will be used for generation and processing.";
        }

        private void ClearRawCsv()
        {
            loadedRawSignal = null;
            RawCsvPath = string.Empty;
            OnPropertyChanged(nameof(RawCsvStatus));
            currentSignal = null;
            ClearPipelineVisuals();
            StatusMessage = "Loaded raw CSV cleared. Synthetic raw generation will be used again.";
        }

        private void ClearReferenceCsv()
        {
            loadedReferenceSignal = null;
            ReferenceCsvPath = string.Empty;
            OnPropertyChanged(nameof(ReferenceCsvStatus));
            currentSignal = null;
            ClearPipelineVisuals();
            StatusMessage = "Loaded reference CSV cleared. Synthetic reference generation will be used again.";
        }

        private void GenerateSignal()
        {
            var request = BuildRequest();
            var generated = signalGenerator.Generate(request);
            var rawSignal = loadedRawSignal == null ? generated.RawSignal : loadedRawSignal.Samples;
            var referenceSignal = loadedReferenceSignal == null ? generated.ReferenceSignal : loadedReferenceSignal.Samples;
            var hasExternalRaw = loadedRawSignal != null;
            var hasExternalRef = loadedReferenceSignal != null;

            currentSignal = new SyntheticSignalResult
            {
                RawSignal = rawSignal,
                ReferenceSignal = referenceSignal,
                RawComplexSignal = loadedRawSignal == null ? generated.RawComplexSignal : loadedRawSignal.ComplexSamples,
                ReferenceComplexSignal = loadedReferenceSignal == null ? generated.ReferenceComplexSignal : loadedReferenceSignal.ComplexSamples,
                GroundTruthEvents = hasExternalRaw ? new ReflectionEvent[0] : generated.GroundTruthEvents
            };

            SampleCount = Math.Max(rawSignal == null ? 0 : rawSignal.Length, referenceSignal == null ? 0 : referenceSignal.Length);

            ClearPipelineState();
            DetectedEvents.Clear();
            MatchResults.Clear();
            ComparisonSummary = null;
            RebuildStageResults("Raw Data");
            StatusMessage = "Raw and reference data prepared. Loaded CSV data, if present, is now active in the pipeline.";
        }

        private void RunPipeline()
        {
            if (currentSignal == null)
            {
                GenerateSignal();
            }

            var request = BuildRequest();
            var previousStageName = SelectedStage == null ? "IFFT" : SelectedStage.Name;
            lastPipelineResult = processingPipeline.Run(currentSignal, request);

            ReplaceItems(DetectedEvents, lastPipelineResult.DetectedEvents);
            ReplaceItems(MatchResults, lastPipelineResult.MatchResults);
            ComparisonSummary = lastPipelineResult.ComparisonSummary;

            UpdateSegmentLabels();
            RebuildStageResults(previousStageName);
            StatusMessage = "OFDR pipeline completed using segment-wise ConjMul and shifted-IFFT peak offset analysis.";
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
                ReferenceAmplitude = ReferenceAmplitude,
                ReferenceDelaySamples = Math.Max(0, ReferenceDelaySamples),
                ReferenceWidth = Math.Max(1.0, ReferenceWidth),
                ReferenceNoiseLevel = Math.Max(0.0, ReferenceNoiseLevel),
                ReferenceBaselineOffset = ReferenceBaselineOffset,
                ReferencePhaseShiftDegrees = ReferencePhaseShiftDegrees,
                FftZeroPadFactor = Math.Max(1, FftZeroPadFactor),
                UseHannWindow = UseHannWindow,
                SegmentSize = Math.Max(2, SegmentSize),
                SegmentHop = Math.Max(1, SegmentHop),
                ConjugateMultiplyScale = ConjugateMultiplyScale,
                NormalizeConjugateMultiply = NormalizeConjugateMultiply,
                FinalSmoothingWindow = Math.Max(1, FinalSmoothingWindow),
                PeakThresholdRatio = PeakThresholdRatio,
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
            var stages = new List<StageResult>();
            if (currentSignal != null)
            {
                stages.Add(BuildStage(
                    "Raw Data",
                    loadedRawSignal == null ? "Generated raw interferometric signal before FFT." : "Loaded raw CSV signal before FFT.",
                    ToMagnitudes(currentSignal.RawComplexSignal, currentSignal.RawSignal),
                    currentSignal.GroundTruthEvents.Select(item => item.PositionIndex).ToArray()));

                stages.Add(BuildStage(
                    "Ref Data",
                    loadedReferenceSignal == null ? "Generated reference-arm signal before FFT." : "Loaded reference CSV signal before FFT.",
                    ToMagnitudes(currentSignal.ReferenceComplexSignal, currentSignal.ReferenceSignal),
                    loadedReferenceSignal == null ? new[] { ReferenceDelaySamples } : new int[0]));
            }

            if (lastPipelineResult != null)
            {
                stages.Add(BuildStage(
                    "Raw FFT",
                    "Full raw FFT magnitude. Segmentation now extracts local windows from this spectrum without averaging bins.",
                    lastPipelineResult.RawSpectrumMagnitudes,
                    new int[0]));

                stages.Add(BuildStage(
                    "Ref FFT",
                    "Full reference FFT magnitude. Segment windows are taken directly from this spectrum.",
                    lastPipelineResult.ReferenceSpectrumMagnitudes,
                    new int[0]));

                if (HasSelectedSegment())
                {
                    var segmentIndex = SelectedSegmentIndex;
                    var start = lastPipelineResult.SegmentStartIndices[segmentIndex];
                    var center = lastPipelineResult.SegmentCenterIndices[segmentIndex];
                    var segmentLength = lastPipelineResult.RawSegments[segmentIndex].Length;
                    var end = start + segmentLength - 1;
                    var peakIndex = lastPipelineResult.IfftPeakIndices[segmentIndex];
                    var zeroLagIndex = lastPipelineResult.IfftSegments[segmentIndex].Length / 2;

                    stages.Add(BuildStage(
                        "Raw Segmentation",
                        BuildSegmentDescription("Selected raw FFT window extracted for local OFDR comparison.", segmentIndex, start, end, center),
                        lastPipelineResult.RawSegments[segmentIndex].Select(value => value.Magnitude).ToArray(),
                        new int[0]));

                    stages.Add(BuildStage(
                        "Ref Segmentation",
                        BuildSegmentDescription("Selected reference FFT window extracted with the same start and span.", segmentIndex, start, end, center),
                        lastPipelineResult.ReferenceSegments[segmentIndex].Select(value => value.Magnitude).ToArray(),
                        new int[0]));

                    stages.Add(BuildStage(
                        "ConjMul",
                        BuildSegmentDescription("Bin-wise Raw segment multiplied by the conjugate of the reference segment.", segmentIndex, start, end, center),
                        ProjectConjMul(lastPipelineResult.ConjugateMultiplySegments[segmentIndex], SelectedConjMulViewMode),
                        new int[0]));

                    stages.Add(BuildStage(
                        "IFFT",
                        BuildSegmentDescription("Shifted IFFT of the selected ConjMul segment. Center is zero-lag and the marker shows the detected shifted peak.", segmentIndex, start, end, center),
                        lastPipelineResult.IfftSegments[segmentIndex].Select(value => value.Magnitude).ToArray(),
                        new[] { zeroLagIndex, peakIndex }));
                }

                stages.Add(BuildStage(
                    "Final Result",
                    "Absolute peak-offset from each shifted segment IFFT mapped to segment center positions, then smoothed and normalized. Identical Raw/Ref should stay near zero.",
                    lastPipelineResult.FinalProfile,
                    lastPipelineResult.DetectedEvents.Select(item => item.PositionIndex).ToArray(),
                    lastPipelineResult.DetectedEvents));
            }

            ReplaceItems(StageResults, stages);
            var targetStageName = preferredStageName ?? (SelectedStage == null ? null : SelectedStage.Name);
            SelectedStage = targetStageName == null
                ? StageResults.FirstOrDefault()
                : StageResults.FirstOrDefault(item => string.Equals(item.Name, targetStageName, StringComparison.Ordinal)) ?? StageResults.FirstOrDefault();
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
                "{0} Segment {1} covers FFT bins {2}-{3} with center {4}.",
                prefix,
                segmentIndex + 1,
                start,
                end,
                center);
        }

        private static double[] ToMagnitudes(System.Numerics.Complex[] complexSamples, double[] realSamples)
        {
            if (complexSamples != null && complexSamples.Length > 0)
            {
                return complexSamples.Select(value => value.Magnitude).ToArray();
            }

            return realSamples ?? new double[0];
        }

        private static double[] ProjectConjMul(System.Numerics.Complex[] samples, string mode)
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

        private static StageResult BuildStage(string name, string description, double[] samples, int[] markers, IList<DetectedEvent> detectedEvents = null)
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
                DetectedEvents = detectedEvents ?? new DetectedEvent[0]
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
    }
}










