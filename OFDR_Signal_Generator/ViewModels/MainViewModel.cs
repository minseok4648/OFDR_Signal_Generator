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

            GenerateSignalCommand = new RelayCommand(GenerateSignal);
            RunPipelineCommand = new RelayCommand(RunPipeline);
            AddEventCommand = new RelayCommand(AddReflectionEvent);
            removeEventCommand = new RelayCommand(RemoveSelectedEvent, () => SelectedReflectionEvent != null);
            ResetDefaultsCommand = new RelayCommand(ResetDefaults);
            LoadRawCsvCommand = new RelayCommand(LoadRawCsv);
            LoadReferenceCsvCommand = new RelayCommand(LoadReferenceCsv);
            ClearRawCsvCommand = new RelayCommand(ClearRawCsv);
            ClearReferenceCsvCommand = new RelayCommand(ClearReferenceCsv);
            ExportReconstructedRawCsvCommand = new RelayCommand(ExportReconstructedRawCsv);

            ResetDefaults();
        }

        public ObservableCollection<ReflectionEvent> ReflectionEvents { get; private set; }

        public ObservableCollection<StageResult> StageResults { get; private set; }

        public ObservableCollection<DetectedEvent> DetectedEvents { get; private set; }

        public ObservableCollection<EventMatchResult> MatchResults { get; private set; }

        public ObservableCollection<string> SegmentLabels { get; private set; }

        public ObservableCollection<string> ConjMulViewModes { get; private set; }

        public ObservableCollection<string> FinalMetricModes { get; private set; }

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
            get { return HasSelectedSegment() && SelectedStage != null && string.Equals(SelectedStage.Name, "X-Corr", StringComparison.Ordinal); }
        }

        public string SelectedCorrelationLagSummary
        {
            get
            {
                if (!HasSelectedSegment())
                {
                    return "Select and run a segment to inspect lag-by-lag overlap between the Raw and Ref segment IFFT signals.";
                }

                var segmentIndex = SelectedSegmentIndex;
                var rawMagnitudes = lastPipelineResult.RawIfftSegments[segmentIndex].Select(value => value.Magnitude).ToArray();
                var referenceMagnitudes = lastPipelineResult.ReferenceIfftSegments[segmentIndex].Select(value => value.Magnitude).ToArray();
                var overlapStats = CalculateLagOverlapStats(rawMagnitudes, referenceMagnitudes, SelectedCorrelationLag);
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "k = {0} | overlap {1}/{2} | mean abs diff {3:0.0000} | RMS diff {4:0.0000}",
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
                    return "Run the OFDR pipeline to inspect segment-by-segment Raw/Ref IFFT and X-Corr results.";
                }

                var start = lastPipelineResult.SegmentStartIndices[SelectedSegmentIndex];
                var center = lastPipelineResult.SegmentCenterIndices[SelectedSegmentIndex];
                var segmentLength = lastPipelineResult.RawSegments[SelectedSegmentIndex].Length;
                var peak = lastPipelineResult.CrossCorrelationPeakIndices[SelectedSegmentIndex];
                var representative = lastPipelineResult.SegmentRepresentativeValues[SelectedSegmentIndex];
                var end = start + segmentLength - 1;

                var configuredShiftBins = GetConfiguredShiftBinsForSegment(SelectedSegmentIndex);
                var shiftSummary = configuredShiftBins == 0
                    ? "no configured Raw IFFT shift"
                    : string.Format(CultureInfo.InvariantCulture, "configured Raw IFFT shift {0:+#;-#;0} bin(s)", configuredShiftBins);

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "Segment {0}/{1} | FFT bins {2}-{3} | center {4} | X-Corr peak {5} | {6}: {7:0.0000} | {8}",
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
            var nextIndex = ReflectionEvents.Count == 0 ? 1 : ReflectionEvents.Last().PositionIndex + 1;
            var reflectionEvent = new ReflectionEvent
            {
                PositionIndex = nextIndex,
                Amplitude = 4.0,
                Width = 0.0
            };

            ReflectionEvents.Add(reflectionEvent);
            SelectedReflectionEvent = reflectionEvent;
            StatusMessage = "Added a new Raw segment IFFT shift event.";
        }

        private void RemoveSelectedEvent()
        {
            if (SelectedReflectionEvent == null)
            {
                return;
            }

            ReflectionEvents.Remove(SelectedReflectionEvent);
            SelectedReflectionEvent = ReflectionEvents.FirstOrDefault();
            StatusMessage = "Removed the selected Raw segment IFFT shift event.";
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
                StatusMessage = "No reconstructed Raw signal is available yet. Run the pipeline first.";
                return;
            }

            var path = PromptForSaveCsv("Save Reconstructed Raw CSV", "synthetic_reconstructed_raw.csv");
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            csvSignalLoader.SaveSignal(path, lastPipelineResult.ReconstructedRawComplexSignal, SampleSpacing);
            StatusMessage = string.Format(CultureInfo.InvariantCulture, "Reconstructed Raw CSV saved to {0}", path);
        }

        private void GenerateSignal()
        {
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
                ? "Raw data prepared from the loaded reference signal. Configured segment shift events are applied later at the Raw Segment IFFT stage."
                : "Raw and reference data prepared. Loaded CSV data, if present, is now active in the pipeline.";
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
            StatusMessage = string.Format(CultureInfo.InvariantCulture, "OFDR pipeline completed with segment extraction, post-segmentation zero-padded IFFT, and X-Corr processing. Final metric profile uses {0}.", lastPipelineResult.FinalMetricMode);
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
            var stages = new List<StageResult>();
            if (currentSignal != null)
            {
                var rawDescription = loadedRawSignal != null
                    ? "Loaded raw CSV signal before FFT."
                    : loadedReferenceSignal != null
                        ? "Raw signal derived from the loaded reference CSV. Segment shift events are applied later at the Raw Segment IFFT stage."
                        : "Generated raw interferometric signal before FFT.";
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
                    loadedReferenceSignal == null ? "Generated reference-arm signal before FFT." : "Loaded reference CSV signal before FFT.",
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
                    "Full raw FFT magnitude. Local windows are extracted from this spectrum, then each selected block is zero-padded before IFFT.",
                    lastPipelineResult.RawSpectrumMagnitudes,
                    new int[0],
                    "FFT Bin",
                    "Magnitude",
                    0.0,
                    Math.Max(0, lastPipelineResult.RawSpectrumMagnitudes.Length - 1)));

                stages.Add(BuildStage(
                    "Ref FFT",
                    "Full reference FFT magnitude. Local windows are taken from this spectrum, then zero-padded before segment IFFT.",
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
                    var rawIfftLength = lastPipelineResult.RawIfftSegments[segmentIndex].Length;
                    var rawIfftZeroLagIndex = rawIfftLength / 2;
                    var rawIfftLagEnd = rawIfftLength - rawIfftZeroLagIndex - 1;
                    var referenceIfftLength = lastPipelineResult.ReferenceIfftSegments[segmentIndex].Length;
                    var referenceIfftZeroLagIndex = referenceIfftLength / 2;
                    var referenceIfftLagEnd = referenceIfftLength - referenceIfftZeroLagIndex - 1;
                    var correlationPeakIndex = lastPipelineResult.CrossCorrelationPeakIndices[segmentIndex];
                    var zeroLagIndex = Math.Max(0, lastPipelineResult.ReferenceIfftSegments[segmentIndex].Length - 1);
                    var selectedLagMarkerIndex = Math.Max(0, Math.Min(lastPipelineResult.CrossCorrelationSegments[segmentIndex].Length - 1, SelectedCorrelationLag + zeroLagIndex));
                    var correlationLagStart = -Math.Max(0, lastPipelineResult.ReferenceIfftSegments[segmentIndex].Length - 1);
                    var correlationLagEnd = Math.Max(0, lastPipelineResult.RawIfftSegments[segmentIndex].Length - 1);

                    stages.Add(BuildStage(
                        "Raw Segmentation",
                        BuildSegmentDescription("Selected raw FFT window extracted for local OFDR comparison.", segmentIndex, start, end, center),
                        lastPipelineResult.RawSegments[segmentIndex].Select(value => value.Magnitude).ToArray(),
                        new int[0],
                        "FFT Bin",
                        "Magnitude",
                        start,
                        end));

                    stages.Add(BuildStage(
                        "Ref Segmentation",
                        BuildSegmentDescription("Selected reference FFT window extracted with the same start and span.", segmentIndex, start, end, center),
                        lastPipelineResult.ReferenceSegments[segmentIndex].Select(value => value.Magnitude).ToArray(),
                        new int[0],
                        "FFT Bin",
                        "Magnitude",
                        start,
                        end));

                    stages.Add(BuildStage(
                        "Raw Segment IFFT",
                        BuildRawSegmentIfftDescription(segmentIndex, start, end, center),
                        lastPipelineResult.RawIfftSegments[segmentIndex].Select(value => value.Magnitude).ToArray(),
                        BuildRawSegmentIfftMarkers(segmentIndex, rawIfftZeroLagIndex, rawIfftLength),
                        "Lag Bin",
                        "Magnitude",
                        -rawIfftZeroLagIndex,
                        rawIfftLagEnd));

                    stages.Add(BuildStage(
                        "Ref Segment IFFT",
                        BuildSegmentDescription("IFFT applied after zero-padding the selected reference FFT segment block.", segmentIndex, start, end, center),
                        lastPipelineResult.ReferenceIfftSegments[segmentIndex].Select(value => value.Magnitude).ToArray(),
                        new[] { referenceIfftZeroLagIndex },
                        "Lag Bin",
                        "Magnitude",
                        -referenceIfftZeroLagIndex,
                        referenceIfftLagEnd));

                    stages.Add(BuildStage(
                        "X-Corr",
                        BuildSegmentDescription("Cross-correlation between the raw-segment IFFT and reference-segment IFFT. Zero-lag is centered.", segmentIndex, start, end, center),
                        ProjectComplexView(lastPipelineResult.CrossCorrelationSegments[segmentIndex], SelectedConjMulViewMode),
                        new[] { zeroLagIndex, correlationPeakIndex, selectedLagMarkerIndex },
                        "Lag Bin",
                        GetComplexViewYAxisTitle(SelectedConjMulViewMode),
                        correlationLagStart,
                        correlationLagEnd));
                }

                stages.Add(BuildStage(
                    "Final Metric Profile",
                    string.Format(CultureInfo.InvariantCulture, "Segment-wise X-Corr feature profile built from {0}. Values are mapped across segment-center positions on the FFT-bin axis.", lastPipelineResult.FinalMetricMode),
                    lastPipelineResult.FinalProfile,
                    BuildFinalMetricProfileMarkers(),
                    "FFT Bin / Segment Position",
                    SignalProcessingPipeline.GetFinalMetricYAxisTitle(lastPipelineResult.FinalMetricMode),
                    0.0,
                    Math.Max(0, lastPipelineResult.FinalProfile.Length - 1),
                    lastPipelineResult.DetectedEvents));

                var reconstructedSamples = ToSignalDisplaySamples(lastPipelineResult.ReconstructedRawComplexSignal, lastPipelineResult.ReconstructedRawSignal);
                stages.Add(BuildStage(
                    "Reconstructed Raw",
                    "Raw signal reconstructed from the shifted Raw Segment IFFT blocks by FFT-ing each block back to spectrum space, overlap-averaging the segments, and applying the inverse full FFT.",
                    reconstructedSamples,
                    new int[0],
                    "Sample Index",
                    GetSignalDisplayYAxisTitle(lastPipelineResult.ReconstructedRawComplexSignal),
                    0.0,
                    Math.Max(0, reconstructedSamples.Length - 1)));
            }

            ReplaceItems(StageResults, stages);
            var targetStageName = preferredStageName ?? (SelectedStage == null ? null : SelectedStage.Name);
            SelectedStage = targetStageName == null
                ? StageResults.FirstOrDefault()
                : StageResults.FirstOrDefault(item => string.Equals(item.Name, targetStageName, StringComparison.Ordinal)) ?? StageResults.FirstOrDefault();
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
            return lastPipelineResult.CrossCorrelationPeakIndices[SelectedSegmentIndex] - zeroLagIndex;
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
            var description = BuildSegmentDescription("IFFT applied after zero-padding the selected raw FFT segment block.", segmentIndex, start, end, center);
            var shiftBins = GetConfiguredShiftBinsForSegment(segmentIndex);
            return shiftBins == 0
                ? description
                : string.Format(CultureInfo.InvariantCulture, "{0} A circular shift of {1:+#;-#;0} bin(s) is then applied directly to this Raw Segment IFFT.", description, shiftBins);
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
    }
}









































