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
    public sealed class ModeHopPreviewViewModel : ObservableObject, IDisposable
    {
        public AuxiliaryQualityViewModel AuxiliaryQuality { get; } = new AuxiliaryQualityViewModel();
        public SweepPreparationViewModel SweepPreparation { get; }
        public ModeHopRecording LoadedRecording => recording;
        public ModeHopAnalysis AppliedExclusion => analysis;
        public double AppliedThreshold => appliedSettings?.ThresholdVoltage ?? 1.8;
        public bool ManualSweepEnabled => recording?.HasFourChannels != true;
        public string SweepHint => ManualSweepEnabled ? "초기 1200 ms는 약 1.2초의 참고값입니다. 비워 두면 수동 경계 표시를 끕니다." : "4채널 입력: CH1 > 1.8 V 상승 에지로 스윕 경계를 표시합니다. 수동 스윕 시간·오프셋은 사용하지 않습니다.";
        private readonly ModeHopPreviewEngine engine = new ModeHopPreviewEngine();
        private readonly Stack<Tuple<double, double>> history = new Stack<Tuple<double, double>>();
        // Full arrays stay in the Core recording; only Viewport is bound to the view.
        private ModeHopRecording recording;
        private ModeHopAnalysis analysis;
        private ModeHopSettings appliedSettings;
        private CancellationTokenSource cancellation;
        private TaskCompletionSource<bool> idleSignal;
        private bool disposed;
        private bool busy;
        private bool useModeHopExclusion = true;
        private string sourcePath = "";
        private string thresholdText = "1.8";
        private string exclusionTailText = "0";
        private string exclusionLeadText = "0";
        private string sweepText = "1200";
        private string originText = "0";
        private int maximumPreviewPoints = 10000;
        private string status = "Tektronix CSV를 선택하세요. CH2=트리거 / CH3=보조간섭계";
        private string metadata = "";
        private string summary = "";
        private string resultDirectory = "";
        private double progress;
        private ModeHopViewport viewport;
        private ModeHopRange selectedRange;

        public ModeHopPreviewViewModel()
        {
            SweepPreparation = new SweepPreparationViewModel(this);
            SweepPreparation.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(SweepPreparation.IsBusy)) { OnPropertyChanged(nameof(IsBusy)); RaiseCommands(); } };
            AuxiliaryQuality.ParentIdle = () => !busy && !SweepPreparation.IsBusy;
            AuxiliaryQuality.PropertyChanged += (sender, e) => { if (e.PropertyName == nameof(AuxiliaryQuality.IsBusy)) { OnPropertyChanged(nameof(IsBusy)); RaiseCommands(); } };
            BrowseCommand = new RelayCommand(Browse, () => !IsBusy);
            ReloadCommand = new RelayCommand(() => { _ = LoadAsync(SourcePath); }, () => !IsBusy && !string.IsNullOrWhiteSpace(SourcePath));
            ApplyCommand = new RelayCommand(() => { _ = ApplyAsync(); }, () => !IsBusy && recording != null);
            CancelCommand = new RelayCommand(() => { cancellation?.Cancel(); AuxiliaryQuality.Cancel(); SweepPreparation.Cancel(); }, () => IsBusy);
            FullViewCommand = new RelayCommand(() => { _ = ShowRangeAsync(0, recording.DurationSeconds, true); }, () => !IsBusy && recording != null);
            BackCommand = new RelayCommand(Back, () => !IsBusy && history.Count > 0);
            SaveSettingsCommand = new RelayCommand(SaveSettings, () => !IsBusy);
            LoadSettingsCommand = new RelayCommand(LoadSettings, () => !IsBusy);
        }

        public RelayCommand BrowseCommand { get; }
        public RelayCommand ReloadCommand { get; }
        public RelayCommand ApplyCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand FullViewCommand { get; }
        public RelayCommand BackCommand { get; }
        public RelayCommand SaveSettingsCommand { get; }
        public RelayCommand LoadSettingsCommand { get; }
        public string SourcePath { get => sourcePath; private set { SetProperty(ref sourcePath, value); RaiseCommands(); } }
        public string ThresholdText { get => thresholdText; set { if (SetProperty(ref thresholdText, value)) { AuxiliaryQuality.InvalidateDiagnosis(); SweepPreparation.InvalidateActive(); } } }
        public bool UseModeHopExclusion
        {
            get => useModeHopExclusion;
            set
            {
                if (!SetProperty(ref useModeHopExclusion, value)) return;
                AuxiliaryQuality.InvalidateDiagnosis(); SweepPreparation.InvalidateActive();
                Status = "제거 설정을 변경했습니다. 설정 적용 또는 스윕 검토 후 Ref/Signal을 다시 진단·확정하세요. 기존 확정 입력은 유지됩니다.";
            }
        }
        public string ExclusionTailText { get => exclusionTailText; set { if (SetProperty(ref exclusionTailText, value)) { AuxiliaryQuality.InvalidateDiagnosis(); SweepPreparation.InvalidateActive(); } } }
        public string ExclusionLeadText { get => exclusionLeadText; set { if (SetProperty(ref exclusionLeadText, value)) { AuxiliaryQuality.InvalidateDiagnosis(); SweepPreparation.InvalidateActive(); } } }
        public string SweepText { get => sweepText; set => SetProperty(ref sweepText, value); }
        public string OriginText { get => originText; set => SetProperty(ref originText, value); }
        public string Status { get => status; private set => SetProperty(ref status, value); }
        public string Metadata { get => metadata; private set => SetProperty(ref metadata, value); }
        public string Summary { get => summary; private set => SetProperty(ref summary, value); }
        public string ResultDirectory { get => resultDirectory; private set => SetProperty(ref resultDirectory, value); }
        public double Progress { get => progress; private set => SetProperty(ref progress, value); }
        public bool IsBusy { get => busy || AuxiliaryQuality.IsBusy || (SweepPreparation?.IsBusy ?? false); private set { SetProperty(ref busy, value); RaiseCommands(); } }
        public ModeHopViewport Viewport { get => viewport; private set { SetProperty(ref viewport, value); OnPropertyChanged(nameof(MeasurementPlot)); } }
        public DiagnosticPlotData MeasurementPlot => Viewport?.Measurement == null ? null : new DiagnosticPlotData { Title = "CH4 원본 측정 신호", XAxis = "기록 시작 기준 시간 (s)", YAxis = "V", XStart = Viewport.StartSeconds, XEnd = Viewport.EndSeconds, Primary = Viewport.Measurement, Markers = Viewport.SweepBoundaries.ConvertAll(b => b.Seconds).ToArray() };
        public IReadOnlyList<ModeHopRange> Ranges => analysis?.Ranges;
        public ModeHopRange SelectedRange
        {
            get => selectedRange;
            set
            {
                if (SetProperty(ref selectedRange, value) && value != null && !IsBusy && recording != null)
                {
                    var padding = Math.Max((value.EndSeconds - value.StartSeconds) * 3, recording.SampleIntervalSeconds * 10);
                    _ = ShowRangeAsync(Math.Max(0, value.StartSeconds - padding), Math.Min(recording.DurationSeconds, value.EndSeconds + padding), true);
                }
            }
        }

        private void Browse()
        {
            var dialog = new OpenFileDialog { Filter = "Tektronix CSV|*.csv", Title = "CH2 트리거 + CH3 보조간섭계 CSV 선택" };
            if (dialog.ShowDialog() == true) _ = LoadAsync(dialog.FileName);
        }

        public async Task LoadAsync(string path)
        {
            if (IsBusy || disposed) return;
            ModeHopSettings settings;
            try { settings = ReadSettings(); } catch (Exception ex) { Status = ex.Message; return; }
            SourcePath = path;
            Begin();
            recording = null; analysis = null; Viewport = null;
            OnPropertyChanged(nameof(ManualSweepEnabled)); OnPropertyChanged(nameof(SweepHint));
            SweepPreparation.OnSourceLoaded(null);
            AuxiliaryQuality.UpdateSource(null, null, 0);
            Metadata = ""; Summary = ""; history.Clear();
            SetProperty(ref selectedRange, null, nameof(SelectedRange)); OnPropertyChanged(nameof(Ranges));
            ModeHopRunRecorder log = null;
            Exception failure = null;
            var stage = "CSV 읽기";
            try
            {
                log = new ModeHopRunRecorder(Path.Combine("harness", "results"), path, settings, "mode_hop_preview");
                ResultDirectory = log.DirectoryPath;
                var reporter = new Progress<OperationProgress>(value => { if (!disposed && IsBusy) { Progress = value.Percent * 0.85; Status = value.Stage; } });
                var token = cancellation.Token;
                recording = await Task.Run(() => new TekScopeCsvReader().Read(path, token, reporter));
                stage = "High 구간 검출";
                Status = stage;
                var loaded = recording;
                var detected = await Task.Run(() => engine.Detect(loaded, settings, token));
                stage = "전체 미리보기";
                var preview = await Task.Run(() => engine.Preview(loaded, detected, settings, 0, loaded.DurationSeconds, token));
                token.ThrowIfCancellationRequested();
                if (disposed) return;
                analysis = detected; appliedSettings = settings; Viewport = preview;
                AuxiliaryQuality.UpdateSource(recording, analysis, settings.ThresholdVoltage);
                SweepPreparation.OnSourceLoaded(recording);
                OnPropertyChanged(nameof(ManualSweepEnabled)); OnPropertyChanged(nameof(SweepHint));
                Metadata = string.Format(CultureInfo.InvariantCulture, "{0:N0} samples · {1:G9} μs · {2:G9} s · 원본 시작 {3:G9} s · 전 채널 inf 무시 {5:N0} samples · SHA-256 {4}", loaded.SampleCount, loaded.SampleIntervalSeconds * 1e6, loaded.DurationSeconds, loaded.SourceStartSeconds, loaded.Sha256, loaded.InvalidSampleCount);
                RefreshSummary();
                Progress = 100; Status = "전체 신호 읽기 완료. 붉은 음영은 CH2 High·시작 전/종단 추가 제외·전 채널 inf 제외 구간입니다.";
            }
            catch (Exception ex) { failure = ex; Status = ex is OperationCanceledException ? "읽기를 취소했습니다." : "실패: " + ex.Message; }
            finally
            {
                FinishLog(log, failure, stage);
                if (failure != null) { recording = null; analysis = null; Viewport = null; Metadata = ""; Summary = ""; OnPropertyChanged(nameof(Ranges)); }
                End();
            }
        }

        public async Task<bool> ApplyAsync()
        {
            if (IsBusy || recording == null || disposed) return false;
            ModeHopSettings settings;
            try { settings = ReadSettings(); } catch (Exception ex) { Status = ex.Message; return false; }
            Begin();
            var loaded = recording;
            ModeHopRunRecorder log = null;
            Exception failure = null;
            try
            {
                log = new ModeHopRunRecorder(Path.Combine("harness", "results"), SourcePath, settings, "mode_hop_settings");
                ResultDirectory = log.DirectoryPath;
                var token = cancellation.Token;
                var detected = await Task.Run(() => engine.Detect(loaded, settings, token));
                var preview = await Task.Run(() => engine.Preview(loaded, detected, settings, Viewport.StartSeconds, Viewport.EndSeconds, token));
                token.ThrowIfCancellationRequested();
                if (disposed) return false;
                analysis = detected; appliedSettings = settings; Viewport = preview;
                AuxiliaryQuality.UpdateSource(recording, analysis, settings.ThresholdVoltage);
                SweepPreparation.InvalidateActive();
                SetProperty(ref selectedRange, null, nameof(SelectedRange));
                RefreshSummary(); Progress = 100;
                Status = "설정 적용 완료. 원본 CSV를 다시 읽지 않고 전체 샘플에서 재검출했습니다.";
            }
            catch (Exception ex) { failure = ex; Status = ex is OperationCanceledException ? "설정 적용을 취소했습니다. 이전 결과를 유지합니다." : "설정 적용 실패: " + ex.Message; }
            finally { FinishLog(log, failure, "설정 적용"); End(); }
            return failure == null;
        }

        public async Task ShowRangeAsync(double start, double end, bool remember)
        {
            if (IsBusy || recording == null || appliedSettings == null || disposed) return;
            var old = Viewport;
            Begin();
            try
            {
                var loaded = recording; var detected = analysis; var settings = appliedSettings; var token = cancellation.Token;
                var preview = await Task.Run(() => engine.Preview(loaded, detected, settings, start, end, token));
                token.ThrowIfCancellationRequested();
                if (disposed) return;
                if (remember && old != null) history.Push(Tuple.Create(old.StartSeconds, old.EndSeconds));
                Viewport = preview; Progress = 100;
                Status = string.Format(CultureInfo.InvariantCulture, "표시 범위 {0:G9}~{1:G9} s · CH3 {2:N0} / CH2 {3:N0} preview points{4}", start, end, preview.Auxiliary.Values.Length, preview.Trigger.Values.Length, preview.SweepMarkersDecimated ? " · 스윕 경계는 일부만 표시 (확대하여 확인)" : "");
            }
            catch (Exception ex) { Status = ex is OperationCanceledException ? "확대를 취소했습니다." : "표시 실패: " + ex.Message; }
            finally { End(); }
        }

        private void Back()
        {
            if (history.Count == 0) return;
            var range = history.Pop(); _ = ShowRangeAsync(range.Item1, range.Item2, false);
        }

        private ModeHopSettings ReadSettings()
        {
            var settings = new ModeHopSettings { ModeHopExclusionBypassed = !UseModeHopExclusion, ThresholdVoltage = Parse(ThresholdText), ExclusionLeadSeconds = Parse(ExclusionLeadText) / 1000, ExclusionTailSeconds = Parse(ExclusionTailText) / 1000, SweepDurationSeconds = string.IsNullOrWhiteSpace(SweepText) ? (double?)null : Parse(SweepText) / 1000, SweepOriginSeconds = Parse(OriginText) / 1000, MaximumPreviewPoints = maximumPreviewPoints };
            settings.Validate(); return settings;
        }

        private static double Parse(string text)
        {
            double value;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) throw new ArgumentException("설정값을 숫자로 입력하세요 (소수점 '.').");
            return value;
        }

        private void SaveSettings()
        {
            try
            {
                ReadSettings();
                var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = "mode_hop_settings.json" };
                if (dialog.ShowDialog() == true) SaveSettingsTo(dialog.FileName);
            }
            catch (Exception ex) { Status = "설정 저장 실패: " + ex.Message; }
        }

        private void LoadSettings()
        {
            try
            {
                var dialog = new OpenFileDialog { Filter = "JSON|*.json" };
                if (dialog.ShowDialog() != true) return;
                LoadSettingsFrom(dialog.FileName);
            }
            catch (Exception ex) { Status = "설정 읽기 실패: " + ex.Message; }
        }

        public void SaveSettingsTo(string path) { JsonContractSerializer.Write(path, ReadSettings()); Status = "설정 저장: " + path; }
        public void LoadSettingsFrom(string path)
        {
            var settings = JsonContractSerializer.Read<ModeHopSettings>(path); settings.Validate();
            maximumPreviewPoints = settings.MaximumPreviewPoints;
            UseModeHopExclusion = !settings.ModeHopExclusionBypassed;
            ThresholdText = settings.ThresholdVoltage.ToString("G17", CultureInfo.InvariantCulture);
            ExclusionTailText = (settings.ExclusionTailSeconds * 1000).ToString("G12", CultureInfo.InvariantCulture);
            ExclusionLeadText = (settings.ExclusionLeadSeconds * 1000).ToString("G12", CultureInfo.InvariantCulture);
            SweepText = settings.SweepDurationSeconds.HasValue ? (settings.SweepDurationSeconds.Value * 1000).ToString("G17", CultureInfo.InvariantCulture) : "";
            OriginText = (settings.SweepOriginSeconds * 1000).ToString("G17", CultureInfo.InvariantCulture);
            Status = "설정을 읽었습니다. '설정 적용'을 누르세요.";
        }

        private void RefreshSummary()
        {
            OnPropertyChanged(nameof(Ranges));
            if (appliedSettings.ModeHopExclusionBypassed)
            {
                Summary = string.Format(CultureInfo.InvariantCulture, "모드홉 제거 OFF · CH2 High와 앞뒤 추가 제외를 적용하지 않습니다. 전 채널 inf 제외 {0:N0} samples · 실제 제외 {1:N0} samples", analysis.IgnoredSamples, analysis.ExcludedSamples);
                return;
            }
            Summary = string.Format(CultureInfo.InvariantCulture, "제외 예정: {0:N0}개 구간 · {1:N0} samples · {2:F7} s · {3:F5}%  |  CH2 > {4:G9} V · 시작 전 추가 제외 {7:G9} ms · 종단 추가 제외 {5:G9} ms · 전 채널 inf 무시 {6:N0} samples (High와 겹침 포함)", analysis.Ranges.Count, analysis.ExcludedSamples, analysis.ExcludedSeconds, analysis.ExcludedPercent, appliedSettings.ThresholdVoltage, appliedSettings.ExclusionTailSeconds * 1000, analysis.IgnoredSamples, appliedSettings.ExclusionLeadSeconds * 1000);
        }

        private void Begin() { cancellation?.Dispose(); cancellation = new CancellationTokenSource(); idleSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); IsBusy = true; Progress = 0; Status = "작업 중..."; }
        private void End() { IsBusy = false; idleSignal?.TrySetResult(true); }
        public async Task CancelAndWaitAsync() { cancellation?.Cancel(); await SweepPreparation.CancelAndWaitAsync(); await AuxiliaryQuality.CancelAndWaitAsync(); if (idleSignal != null) await idleSignal.Task; }
        private void FinishLog(ModeHopRunRecorder log, Exception failure, string stage)
        {
            try { log?.Finish(recording, analysis, Viewport, failure, stage); }
            catch (Exception ex) { Status += " / 결과 기록 실패: " + ex.Message; }
        }
        private void RaiseCommands()
        {
            AuxiliaryQuality.RefreshCommands();
            SweepPreparation?.RefreshCommands();
            foreach (var command in new[] { BrowseCommand, ReloadCommand, ApplyCommand, CancelCommand, FullViewCommand, BackCommand, SaveSettingsCommand, LoadSettingsCommand }) command?.RaiseCanExecuteChanged();
        }
        public void Dispose() { disposed = true; cancellation?.Cancel(); SweepPreparation.Dispose(); AuxiliaryQuality.Dispose(); recording = null; analysis = null; }
    }
}
