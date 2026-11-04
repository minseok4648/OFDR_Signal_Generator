using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;
using OFDR_Signal_Generator.Infrastructure;

namespace OFDR_Signal_Generator.ViewModels
{
    public sealed class AuxiliaryQualityViewModel : ObservableObject, IDisposable
    {
        private readonly AuxiliaryQualityEngine engine = new AuxiliaryQualityEngine();
        private ModeHopRecording source;
        private ModeHopRecording pairedOriginal;
        private DiagnosticPlotData measurementPlot;
        public StitchedAuxiliarySignal Stitched => stitched;
        public AuxiliaryInspectionSettings DiagnosisSettings => diagnosisSettings;
        public string SourceDescription => source == null ? "" : Path.GetFileName(source.SourcePath) + " · 원본 인덱스 [" + source.FirstSampleIndex + ", " + (source.FirstSampleIndex + source.SampleCount) + ")";
        public DiagnosticPlotData MeasurementPlot { get => measurementPlot; private set => SetProperty(ref measurementPlot, value); }
        private ModeHopAnalysis exclusion;
        private StitchedAuxiliarySignal stitched;
        private double threshold;
        private CancellationTokenSource cancellation;
        private TaskCompletionSource<bool> idle;
        private bool busy, disposed;
        private string start = "0", end = "", ratio = "0.00001";
        private string status = "먼저 원본 CSV를 읽고 트리거 문턱값을 적용하세요.";
        private string summary = "";
        private string directory = "";
        private DiagnosticPlotData signalPlot;
        private AuxiliaryQualityResult quality;
        private AuxiliaryInspectionSettings diagnosisSettings;
        private int selectedTab;
        public Func<bool> ParentIdle { get; set; } = () => true;
        public AuxiliaryQualityViewModel()
        {
            StitchCommand = new RelayCommand(() => { _ = StitchAsync(); }, () => Ready && source != null);
            AnalyzeCommand = new RelayCommand(() => { _ = AnalyzeAsync(); }, () => Ready && stitched != null);
            CancelCommand = new RelayCommand(Cancel, () => IsBusy);
            ExportDiagnosedCommand = new RelayCommand(() => SaveCsv(false), () => Ready && stitched != null && Quality != null);
            ExportEntireCommand = new RelayCommand(() => SaveCsv(true), () => Ready && stitched != null);
        }
        private bool Ready => !disposed && !IsBusy && ParentIdle();
        public bool IsEditable => Ready;
        public RelayCommand StitchCommand { get; }
        public RelayCommand AnalyzeCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand ExportDiagnosedCommand { get; }
        public RelayCommand ExportEntireCommand { get; }
        public string StartText { get => start; set { if (SetProperty(ref start, value) && pairedOriginal != null) InvalidateDiagnosis(); } }
        public string EndText { get => end; set { if (SetProperty(ref end, value) && pairedOriginal != null) InvalidateDiagnosis(); } }
        public string RatioText { get => ratio; set { if (SetProperty(ref ratio, value) && pairedOriginal != null) InvalidateDiagnosis(); } }
        public string Status { get => status; private set => SetProperty(ref status, value); }
        public string Summary { get => summary; private set => SetProperty(ref summary, value); }
        public string ResultDirectory { get => directory; private set => SetProperty(ref directory, value); }
        public bool IsBusy { get => busy; private set { SetProperty(ref busy, value); RefreshCommands(); } }
        public int SelectedTab { get => selectedTab; set => SetProperty(ref selectedTab, value); }
        public DiagnosticPlotData SignalPlot { get => signalPlot; private set => SetProperty(ref signalPlot, value); }
        public AuxiliaryQualityResult Quality { get => quality; private set { SetProperty(ref quality, value); if (value == null) diagnosisSettings = null; OnPropertyChanged(nameof(Plots)); } }
        public IReadOnlyList<DiagnosticPlotData> Plots => Quality?.Plots;
        public void InvalidateDiagnosis() { Quality = null; RefreshCommands(); }
        public void UpdateSource(ModeHopRecording recording, ModeHopAnalysis analysis, double appliedThreshold, ModeHopRecording original = null)
        {
            source = recording; exclusion = analysis; threshold = appliedThreshold; pairedOriginal = original; MeasurementPlot = null;
            OnPropertyChanged(nameof(SourceDescription));
            stitched = null; SignalPlot = null; Quality = null; Summary = ""; EndText = ""; StartText = "0"; SelectedTab = 0;
            Status = recording == null ? "먼저 원본 CSV를 읽으세요." : "문턱값 적용 완료. '제외 후 접합'을 누르세요. 문턱값을 재적용하면 이 결과는 초기화됩니다.";
            RefreshCommands();
        }
        public async Task StitchAsync()
        {
            if (!Ready || source == null) return;
            Begin(); AuxiliaryQualityRunRecorder log = null; Exception failure = null;
            try
            {
                stitched = null; SignalPlot = null; Quality = null; Summary = "";
                log = new AuxiliaryQualityRunRecorder(source, threshold, new AuxiliaryInspectionSettings(), "aux_stitch"); ResultDirectory = log.DirectoryPath;
                var recording = source; var ranges = exclusion; var token = cancellation.Token;
                var result = await Task.Run(() => engine.Stitch(recording, ranges, token));
                var preview = await Task.Run(() => engine.SignalPreview(result, 10000, token));
                var measurement = pairedOriginal == null ? null : await Task.Run(() => new SweepPreparationEngine().MeasurementPreview(pairedOriginal, result, token));
                token.ThrowIfCancellationRequested();
                stitched = result; SignalPlot = preview; MeasurementPlot = measurement;
                StartText = "0"; EndText = (result.DurationSeconds * 1000).ToString("G12", CultureInfo.InvariantCulture); SelectedTab = 0;
                Summary = string.Format(CultureInfo.InvariantCulture, "{0:N0} → {1:N0} samples · 접합점 {2:N0}개 · 접합 환산 길이 {3:F7} s", source.SampleCount, result.Values.Length, result.JoinIndices.Length, result.DurationSeconds);
                Status = "접합 완료. 진폭 보정 없이 남은 샘플을 그대로 이어 붙였습니다. 그래프를 드래그하거나 시작·끝 시간을 입력한 뒤 위상 분석을 실행하세요.";
            }
            catch (Exception ex) { failure = ex; Status = ex is OperationCanceledException ? "접합을 취소했습니다." : "접합 실패: " + ex.Message; }
            finally { Finish(log, failure); End(); }
        }
        public void SelectRange(double firstSeconds, double endSeconds)
        {
            if (IsBusy || stitched == null) return;
            StartText = (firstSeconds * 1000).ToString("G12", CultureInfo.InvariantCulture);
            EndText = (endSeconds * 1000).ToString("G12", CultureInfo.InvariantCulture);
            Status = "분석 범위를 지정했습니다. '선택 구간 위상 분석'을 누르세요.";
        }
        public async Task AnalyzeAsync()
        {
            if (!Ready || stitched == null) return;
            AuxiliaryInspectionSettings settings;
            try
            {
                settings = new AuxiliaryInspectionSettings { StartSeconds = Parse(StartText) / 1000, EndSeconds = string.IsNullOrWhiteSpace(EndText) ? (double?)null : Parse(EndText) / 1000, MaximumNonmonotonicRatio = Parse(RatioText) };
                settings.Validate();
            }
            catch (Exception ex) { Status = ex.Message; return; }
            Begin(); AuxiliaryQualityRunRecorder log = null; Exception failure = null;
            try
            {
                Quality = null;
                log = new AuxiliaryQualityRunRecorder(source, threshold, settings, "aux_quality"); ResultDirectory = log.DirectoryPath;
                var signal = stitched; var token = cancellation.Token;
                var result = await Task.Run(() => engine.Analyze(signal, settings, token));
                token.ThrowIfCancellationRequested(); Quality = result; diagnosisSettings = settings; SelectedTab = 1;
                var m = result.Metrics;
                Status = string.Format(CultureInfo.InvariantCulture, "{0} · 비단조 {1:G6} (한계 {2:G6}) · span {3:G8} rad · 앵커 {4:N0}/{5:N0} · 선택 구간 접합점 {6} · 재추출 위상 잔차 RMS {7} rad\n{8}", m.Decision, m.Phase.NonmonotonicRatio, settings.MaximumNonmonotonicRatio, m.Phase.PhaseSpanRadians, m.Phase.AcceptedAnchorCount, m.Phase.SampleCount, m.JoinCount, m.ResampledPhaseResidualRmsRadians?.ToString("G6", CultureInfo.InvariantCulture) ?? "N/A", m.Reason);
                Status += string.Format(CultureInfo.InvariantCulture, "\n접합점 최대 |ΔV| {0:G6} V / |Δφ| {1:G6} rad · Grid 간격 {2:G6} rad · 보간 후 재추출 비단조 비율 {3}", m.MaximumJoinVoltageStep, m.MaximumJoinPhaseStep, m.UniformGridStepRadians, m.ResampledPhase?.NonmonotonicRatio.ToString("G6", CultureInfo.InvariantCulture) ?? "N/A");
            }
            catch (Exception ex) { failure = ex; Status = ex is OperationCanceledException ? "위상 분석을 취소했습니다." : "위상 분석 실패: " + ex.Message; }
            finally { Finish(log, failure); End(); }
        }
        private async void SaveCsv(bool entireSignal)
        {
            if (!Ready || stitched == null || (!entireSignal && Quality == null)) return;
            try
            {
                var first = entireSignal ? 0 : Quality.Metrics.SelectedStartIndex;
                var end = entireSignal ? stitched.Values.Length : Quality.Metrics.SelectedEndIndexExclusive;
                var dialog = new SaveFileDialog
                {
                    Title = entireSignal ? "접합 신호 전체 CSV 저장" : "마지막 진단 구간의 접합 신호 CSV 저장",
                    Filter = "CSV 파일 (*.csv)|*.csv", DefaultExt = ".csv", AddExtension = true, OverwritePrompt = true,
                    FileName = Path.GetFileNameWithoutExtension(source.SourcePath) + (entireSignal ? "_stitched_all" : "_stitched_diagnosed_" + first + "_" + end) + ".csv"
                };
                if (dialog.ShowDialog() == true) await ExportCsvAsync(dialog.FileName, entireSignal, true);
            }
            catch (Exception ex) { Status = "CSV 저장 실패: " + ex.Message; }
        }
        public async Task<AuxiliaryCsvExportResult> ExportCsvAsync(string path, bool entireSignal, bool overwrite = false)
        {
            if (!Ready || stitched == null || (!entireSignal && Quality == null)) return null;
            // Use the completed diagnosis, not edited text fields or a zoomed preview.
            var diagnosis = Quality?.Metrics; var settings = diagnosisSettings;
            var signal = stitched; var recording = source;
            var first = entireSignal ? 0 : diagnosis.SelectedStartIndex;
            var end = entireSignal ? signal.Values.Length : diagnosis.SelectedEndIndexExclusive;
            Begin(); AuxiliaryQualityRunRecorder log = null; Exception failure = null;
            AuxiliaryCsvExportResult result = null;
            try
            {
                log = new AuxiliaryQualityRunRecorder(recording, threshold, settings ?? new AuxiliaryInspectionSettings(), "aux_csv_export"); ResultDirectory = log.DirectoryPath;
                JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "export_request.json"), new AuxiliaryCsvExportRequest { CsvPath = Path.GetFullPath(path), EntireStitchedSignal = entireSignal, StartIndex = first, EndIndexExclusive = end, Overwrite = overwrite });
                var token = cancellation.Token;
                var reporter = new Progress<OperationProgress>(value => { if (IsBusy && !token.IsCancellationRequested) Status = string.Format(CultureInfo.InvariantCulture, "CSV 저장 중: {0:N0}/{1:N0} samples ({2:F0}%)", value.Completed, value.Total, value.Percent); });
                result = await Task.Run(() => new AuxiliaryCsvExporter().Export(recording, signal, path, entireSignal, threshold, diagnosis, settings, overwrite, token, reporter));
                JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "export.json"), result);
                Status = string.Format(CultureInfo.InvariantCulture, "{0} CSV 저장 완료: {1:N0} samples · {2}\n추적 정보: {3} · 마지막 진단: {4}", entireSignal ? "접합 전체" : "마지막 진단 구간", result.Metadata.RowCount, result.CsvPath, result.MetadataPath, diagnosis?.Decision ?? "진단 전");
            }
            catch (Exception ex) { failure = ex; Status = ex is OperationCanceledException ? "CSV 저장을 취소했습니다. 진단 결과는 유지합니다." : "CSV 저장 실패: " + ex.Message; }
            finally { Finish(log, failure); End(); }
            return failure == null ? result : null;
        }
        private static double Parse(string text)
        {
            double value;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) throw new ArgumentException("구간과 한계를 숫자로 입력하세요.");
            return value;
        }
        private void Begin() { cancellation?.Dispose(); cancellation = new CancellationTokenSource(); idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); IsBusy = true; Status = "계산 중..."; }
        private void End() { IsBusy = false; idle?.TrySetResult(true); }
        private void Finish(AuxiliaryQualityRunRecorder log, Exception error)
        {
            try { log?.Finish(stitched, Quality, error); } catch (Exception ex) { Status += " / 결과 기록 실패: " + ex.Message; }
        }
        public void RefreshCommands() { OnPropertyChanged(nameof(IsEditable)); StitchCommand?.RaiseCanExecuteChanged(); AnalyzeCommand?.RaiseCanExecuteChanged(); CancelCommand?.RaiseCanExecuteChanged(); ExportDiagnosedCommand?.RaiseCanExecuteChanged(); ExportEntireCommand?.RaiseCanExecuteChanged(); }
        public void Cancel() { cancellation?.Cancel(); }
        public Task CancelAndWaitAsync() { Cancel(); return idle?.Task ?? Task.CompletedTask; }
        public void Dispose() { disposed = true; Cancel(); stitched = null; source = null; pairedOriginal = null; exclusion = null; }
    }
}
