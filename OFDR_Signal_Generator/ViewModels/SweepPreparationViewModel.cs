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
    public sealed class SweepPreparationViewModel : ObservableObject, IDisposable
    {
        private readonly ModeHopPreviewViewModel parent;
        private readonly SweepPreparationEngine engine = new SweepPreparationEngine();
        private string role = "Ref", status = "Ref와 Signal 파일을 각각 읽고 스윕을 검토하세요.";
        private string refPath = @"C:\Project\OFDR\Datas\OFDR Raw data\NoticeData\tek0003ALL.csv";
        private string sigPath = @"C:\Project\OFDR\Datas\OFDR Raw data\NoticeData\tek0005ALL.csv";
        private bool busy, disposed;
        private SweepSelectionMode selectionMode;
        private CancellationTokenSource cancellation;
        private TaskCompletionSource<bool> idle;
        private IReadOnlyList<SweepRange> sweeps;
        private SweepRange selectedSweep, activeSweep;
        private ModeHopRecording activeSource;
        private double activeThreshold;
        private PreparedSweepData reference, signal;
        private bool useModeHopExclusion = true;
        public bool UseModeHopExclusion
        {
            get => useModeHopExclusion;
            set { if (SetProperty(ref useModeHopExclusion, value)) InvalidateActive(); }
        }
        public event Action<PreparedSweepData, PreparedSweepData> ApplyRequested;
        public event Action NotebookPresetRequested;
        public Func<bool> WorkspaceIdle { get; set; } = () => true;
        public Func<double> ProcessingLimit { get; set; } = () => 1e-5;
        public Func<bool> AllowDiagnostic { get; set; } = () => false;
        public bool DiagnosticMode => AllowDiagnostic();
        public AuxiliaryQualityViewModel Quality => parent.AuxiliaryQuality;
        public SweepPreparationViewModel(ModeHopPreviewViewModel owner)
        {
            parent = owner;
            parent.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(parent.ThresholdText)) OnPropertyChanged(nameof(ModeHopThresholdText)); if (e.PropertyName == nameof(parent.ExclusionTailText)) OnPropertyChanged(nameof(ModeHopExclusionTailText)); if (e.PropertyName == nameof(parent.ExclusionLeadText)) OnPropertyChanged(nameof(ModeHopExclusionLeadText)); };
            BrowseRefCommand = new RelayCommand(() => Browse("Ref"), () => Ready);
            BrowseSignalCommand = new RelayCommand(() => Browse("Signal"), () => Ready);
            LoadCommand = new RelayCommand(() => { _ = LoadForRoleAsync(ActiveRole, ActiveRole == "Ref" ? RefPath : SignalPath); }, () => Ready);
            ReviewCommand = new RelayCommand(() => { _ = ReviewAsync(); }, () => Ready && SelectedSweep != null && parent.LoadedRecording?.HasFourChannels == true);
            ConfirmCommand = new RelayCommand(() => { _ = ConfirmAsync(); }, () => Ready && activeSource != null && Quality.Quality != null);
            ApplyCommand = new RelayCommand(ApplyConfirmed, () => Ready && WorkspaceIdle() && CanAnalyzeConfirmed);
            SetNotebookCropCommand = new RelayCommand(SetNotebookCrop, () => Ready && activeSource != null && Quality.Stitched != null);
            NotebookPresetCommand = new RelayCommand(() => { NotebookPresetRequested?.Invoke(); Status = "261106 원본 비교 조건 적용: 첫 상승 엣지→파일 끝, CH2 제외 안 함, 노트북 진단 처리. 각 역할에서 검토→17M crop→위상 분석→확정을 진행하세요."; }, () => Ready && WorkspaceIdle());
            SaveRefCommand = new RelayCommand(() => Save("Ref"), () => Ready && reference != null);
            SaveSignalCommand = new RelayCommand(() => Save("Signal"), () => Ready && signal != null);
            ImportRefCommand = new RelayCommand(() => Import("Ref"), () => Ready);
            ImportSignalCommand = new RelayCommand(() => Import("Signal"), () => Ready);
            Quality.PropertyChanged += (s, e) => RefreshCommands();
        }
        private bool Ready => !disposed && !parent.IsBusy;
        public bool IsBusy { get => busy; private set { SetProperty(ref busy, value); RefreshCommands(); } }
        public bool CanEdit => Ready && WorkspaceIdle();
        public RelayCommand CancelCommand => parent.CancelCommand;
        public string RefPath { get => refPath; set { if (SetProperty(ref refPath, value) && ActiveRole == "Ref") InvalidateActive(); } }
        public string SignalPath { get => sigPath; set { if (SetProperty(ref sigPath, value) && ActiveRole == "Signal") InvalidateActive(); } }
        public string ActiveRole { get => role; set { if (value != "Ref" && value != "Signal") return; if (SetProperty(ref role, value)) InvalidateActive(); } }
        public string ModeHopThresholdText { get => parent.ThresholdText; set => parent.ThresholdText = value; }
        public string ModeHopExclusionTailText { get => parent.ExclusionTailText; set => parent.ExclusionTailText = value; }
        public string ModeHopExclusionLeadText { get => parent.ExclusionLeadText; set => parent.ExclusionLeadText = value; }
        public bool UseRecordingStartAsSweepStart
        {
            get => selectionMode == SweepSelectionMode.RecordingStartToFirstEdge;
            set { if (value) SetSelectionMode(SweepSelectionMode.RecordingStartToFirstEdge); }
        }
        public bool UseFirstRisingEdgeToRecordEnd
        {
            get => selectionMode == SweepSelectionMode.FirstEdgeToRecordingEnd;
            set { if (value) SetSelectionMode(SweepSelectionMode.FirstEdgeToRecordingEnd); }
        }
        public bool UseCompleteSweeps
        {
            get => selectionMode == SweepSelectionMode.CompleteSweeps;
            set { if (value) SetSelectionMode(SweepSelectionMode.CompleteSweeps); }
        }
        private void SetSelectionMode(SweepSelectionMode value)
        {
            if (!CanEdit || selectionMode == value) return;
            selectionMode = value;
            OnPropertyChanged(nameof(UseCompleteSweeps)); OnPropertyChanged(nameof(UseRecordingStartAsSweepStart)); OnPropertyChanged(nameof(UseFirstRisingEdgeToRecordEnd));
            OnSourceLoaded(parent.LoadedRecording);
        }
        public IReadOnlyList<string> Roles { get; } = new[] { "Ref", "Signal" };
        public IReadOnlyList<SweepRange> Sweeps { get => sweeps; private set => SetProperty(ref sweeps, value); }
        public SweepRange SelectedSweep { get => selectedSweep; set { if (SetProperty(ref selectedSweep, value)) InvalidateActive(); } }
        public string Status { get => status; private set => SetProperty(ref status, value); }
        public PreparedSweepData PreparedReference => reference;
        public PreparedSweepData PreparedSignal => signal;
        public string RefSummary => Describe(reference);
        public string SignalSummary => Describe(signal);
        public string AnalysisBlockReason => PreparedSweepQcPolicy.PairBlockReason(reference?.Metadata, signal?.Metadata, ProcessingLimit(), DiagnosticMode);
        public bool CanAnalyzeConfirmed => AnalysisBlockReason == null;
        public string AnalysisReadiness => CanAnalyzeConfirmed ? DiagnosticMode ? "상태 확인용 진단 분석에 적용합니다. QC 불합격 여부를 유지하고 FFT·상관을 확인합니다.\n" + (PreparedSweepQcPolicy.PairBlockReason(reference?.Metadata, signal?.Metadata, ProcessingLimit()) ?? "저장된 위상 QC는 통과 상태입니다.") : "Ref/Signal의 확정 구간이 현재 분석 한계의 위상 QC를 통과했습니다. 기존 OFDR 분석에 적용할 수 있습니다."
            : "분석 적용 차단\n" + AnalysisBlockReason + "\n불합격 구간을 조정한 뒤 다시 진단하세요. 진단 데이터의 확정·CSV 저장은 계속 사용할 수 있습니다.";
        public RelayCommand BrowseRefCommand { get; }
        public RelayCommand BrowseSignalCommand { get; }
        public RelayCommand LoadCommand { get; }
        public RelayCommand ReviewCommand { get; }
        public RelayCommand ConfirmCommand { get; }
        public RelayCommand ApplyCommand { get; }
        public RelayCommand SetNotebookCropCommand { get; }
        public RelayCommand NotebookPresetCommand { get; }
        public RelayCommand SaveRefCommand { get; }
        public RelayCommand SaveSignalCommand { get; }
        public RelayCommand ImportRefCommand { get; }
        public RelayCommand ImportSignalCommand { get; }

        public void InvalidateActive() { activeSource = null; activeSweep = null; Quality.InvalidateDiagnosis(); RefreshCommands(); }
        public void OnSourceLoaded(ModeHopRecording source)
        {
            InvalidateActive(); Sweeps = source?.HasFourChannels == true ? engine.DetectSweeps(source, CancellationToken.None, selectionMode) : null;
            SelectedSweep = Sweeps != null && Sweeps.Count > 0 ? Sweeps[0] : null;
            Status = source == null ? "원본을 읽으세요. 확정된 Ref/Signal 입력은 유지됩니다." : !source.HasFourChannels ? "현재 파일은 기존 2채널 형식입니다. 스윕 입력 생성에는 CH1~4가 필요합니다."
                : Sweeps.Count == 0 ? UseFirstRisingEdgeToRecordEnd ? "CH1 상승 엣지가 없어 파일 끝까지 사용할 시작점을 정할 수 없습니다. CH1이 처음부터 High인 상태만으로는 상승 엣지로 판단하지 않습니다."
                    : UseRecordingStartAsSweepStart ? "CH1 상승 에지가 없어 기록 시작 수동 스윕의 끝을 정할 수 없습니다." : "완전한 스윕이 없습니다. 사용 범위에서 '기록 시작 → 첫 상승 엣지' 또는 '첫 상승 엣지 → 파일 끝'을 선택할 수 있습니다."
                : UseFirstRisingEdgeToRecordEnd ? "첫 CH1 상승 엣지부터 마지막 샘플까지를 사용 범위로 지정했습니다. 중간의 다른 상승 엣지도 포함하며, 한 주기의 완전한 스윕으로 자동 판정하지 않습니다. 아래에서 진단할 구간을 선택하세요."
                    : "선택 가능 스윕 " + Sweeps.Count + "개" + (UseRecordingStartAsSweepStart ? " (S1: 기록 시작 수동 지정)" : "") + ". 선택 스윕 검토 후 아래 기존 위상 진단 화면에서 구간을 지정하세요.";
        }
        private void Browse(string target)
        {
            var dialog = new OpenFileDialog { Filter = "Tektronix CSV|*.csv", Title = target + " 4채널 원본 선택" };
            if (dialog.ShowDialog() == true) _ = LoadForRoleAsync(target, dialog.FileName);
        }
        public async Task LoadForRoleAsync(string target, string path)
        {
            if (!Ready) return;
            ActiveRole = target;
            if (target == "Ref") RefPath = path; else SignalPath = path;
            await parent.LoadAsync(path);
        }
        public async Task ReviewAsync()
        {
            if (!Ready || SelectedSweep == null || parent.LoadedRecording?.HasFourChannels != true) return;
            var expectedPath = ActiveRole == "Ref" ? RefPath : SignalPath;
            if (!string.Equals(Path.GetFullPath(expectedPath), Path.GetFullPath(parent.LoadedRecording.SourcePath), StringComparison.OrdinalIgnoreCase)) { Status = "선택한 역할의 원본을 먼저 읽으세요."; return; }
            if (!await parent.ApplyAsync()) { Status = parent.Status; return; }
            var source = parent.LoadedRecording; var sweep = SelectedSweep;
            await RunAsync("sweep_review", async token =>
            {
                var threshold = parent.AppliedThreshold;
                var part = await Task.Run(() => engine.Slice(source, sweep, token));
                var exclusion = await Task.Run(() => new ModeHopPreviewEngine().ClipToSamples(parent.AppliedExclusion, sweep.StartIndex, sweep.EndIndexExclusive, source.SampleIntervalSeconds, token));
                if (!UseModeHopExclusion)
                {
                    if (part.InvalidSampleCount > 0) throw new InvalidOperationException("제외 없이 재현하려는 범위에 비유한 채널 값이 있습니다. 구간을 조정하세요.");
                    exclusion = new ModeHopAnalysis { Ranges = new List<ModeHopRange>(), IgnoredMeasurementRanges = new List<ModeHopRange>(), IgnoredSampleRanges = new List<ModeHopRange>() };
                }
                token.ThrowIfCancellationRequested(); activeSource = source; activeSweep = sweep; activeThreshold = threshold;
                Quality.UpdateSource(part, exclusion, threshold, source);
            });
            if (activeSource == null) return;
            await Quality.StitchAsync();
            if (Quality.Stitched == null) { Status = Quality.Status; return; }
            Status = ActiveRole + " / " + sweep + " · 접합 CH3/CH4를 아래에서 검토하고 선택 구간 위상 분석을 실행하세요.";
        }
        public async Task ConfirmAsync()
        {
            if (!Ready || activeSource == null || Quality.Quality == null || Quality.DiagnosisSettings == null) return;
            var source = activeSource; var sweep = activeSweep; var target = ActiveRole;
            var stitched = Quality.Stitched; var diagnosis = Quality.Quality.Metrics; var settings = Quality.DiagnosisSettings;
            await RunAsync("sweep_confirm", async token =>
            {
                var data = await Task.Run(() => engine.Prepare(source, sweep, target, stitched, diagnosis, settings, activeThreshold, token));
                data.Metadata.ModeHopExclusionBypassed = !UseModeHopExclusion;
                token.ThrowIfCancellationRequested(); SetPrepared(target, data);
                var block = PreparedSweepQcPolicy.BlockReason(data.Metadata, target, ProcessingLimit());
                Status = block == null ? target + " 확정: " + Describe(data) + " · CSV 저장 또는 기존 분석에 적용할 수 있습니다."
                    : "진단 구간을 보존했습니다. CSV 저장은 가능하며 분석 적용은 차단됩니다.\n" + block;
            });
        }
        private void SetPrepared(string target, PreparedSweepData data)
        {
            if (target == "Ref") reference = data; else signal = data;
            OnPropertyChanged(nameof(RefSummary)); OnPropertyChanged(nameof(SignalSummary)); RefreshCommands();
        }
        private void ApplyConfirmed()
        {
            if (!Ready || !WorkspaceIdle()) return;
            var block = AnalysisBlockReason;
            if (block != null)
            {
                Status = "분석 적용 차단\n" + block;
                try { SweepRunRecorder.RecordQcBlock("apply_command", reference?.Metadata, signal?.Metadata, ProcessingLimit(), block); }
                catch (Exception ex) { Status += "\n기록 실패: " + ex.Message; }
                return;
            }
            ApplyRequested?.Invoke(reference, signal);
            Status = (DiagnosticMode ? "진단용 Ref/Signal (QC 판정 유지)" : "QC 통과 Ref/Signal") + " 스윕을 분석 입력에 적용했습니다. 분석 실행 버튼을 누르세요.";
        }
        private void SetNotebookCrop()
        {
            var indices = Quality.Stitched?.OriginalIndices;
            if (activeSource == null || indices == null) return;
            var trigger = Array.FindIndex(activeSource.SweepTrigger, v => v > 3.0);
            var firstOriginal = trigger + 15000; var first = Array.BinarySearch(indices, firstOriginal);
            const int count = 17000000;
            if (trigger < 0 || first < 0 || (long)first + count > indices.Length || indices[first + count - 1] != firstOriginal + count - 1)
            { Status = "261106 crop을 만들 수 없습니다. '첫 상승 엣지 → 파일 끝'을 선택하고 CH2 제외 적용을 해제한 뒤 스윕을 다시 검토하세요."; return; }
            Quality.SelectRange(first * activeSource.SampleIntervalSeconds, (first + count) * activeSource.SampleIntervalSeconds);
            Status = string.Format(CultureInfo.InvariantCulture, "261106 crop: 원본 [{0:N0}, {1:N0}) · 17,000,000점. 아래 '선택 구간 위상 분석' 후 현재 역할로 확정하세요.", firstOriginal, firstOriginal + count);
        }
        private static string Describe(PreparedSweepData data) => data == null ? "미확정" : string.Format(CultureInfo.InvariantCulture, "{0} / S{1}{4} · {2:N0} samples · {3}", Path.GetFileName(data.Metadata.SourcePath), data.Metadata.Sweep.Number, data.SampleCount, data.Metadata.Diagnosis?.Decision ?? "진단 정보 없음", data.Metadata.Sweep.SelectionDescription.Length > 0 ? " (" + data.Metadata.Sweep.SelectionDescription + ")" : "");
        private async void Save(string target)
        {
            var data = target == "Ref" ? reference : signal;
            var dialog = new SaveFileDialog { Filter = "CSV|*.csv", DefaultExt = ".csv", AddExtension = true, OverwritePrompt = true,
                FileName = target + "_" + Path.GetFileNameWithoutExtension(data.Metadata.SourcePath) + "_sweep" + data.Metadata.Sweep.Number.ToString("D2") + ".csv" };
            if (dialog.ShowDialog() == true) await ExportAsync(target, dialog.FileName, true);
        }
        public async Task ExportAsync(string target, string path, bool overwrite = false)
        {
            if (!Ready) return; var data = target == "Ref" ? reference : signal; if (data == null) return;
            await RunAsync("sweep_export", async token =>
            {
                foreach (var sourcePath in new[] { RefPath, SignalPath, reference?.Metadata.SourcePath, signal?.Metadata.SourcePath })
                    if (!string.IsNullOrWhiteSpace(sourcePath) && string.Equals(Path.GetFullPath(path), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase)) throw new IOException("취득 원본 파일을 덮어쓸 수 없습니다.");
                await Task.Run(() => new PreparedSweepCsv().Write(data, path, overwrite, token)); Status = "CSV와 metadata 저장 완료: " + path;
            }, new Dictionary<string, string> { ["target_role"] = target, ["csv_path"] = Path.GetFullPath(path), ["overwrite"] = overwrite.ToString() });
        }
        private async void Import(string target)
        {
            var dialog = new OpenFileDialog { Filter = "생성 스윕 CSV|*.csv", Title = target + " 생성 데이터 불러오기 (metadata 필요)" };
            if (dialog.ShowDialog() == true) await ImportAsync(target, dialog.FileName);
        }
        public async Task ImportAsync(string target, string path)
        {
            if (!Ready) return;
            await RunAsync("sweep_import", async token =>
            {
                var data = await Task.Run(() => new PreparedSweepCsv().Read(path, target, token)); token.ThrowIfCancellationRequested(); SetPrepared(target, data);
                var block = PreparedSweepQcPolicy.BlockReason(data.Metadata, target, ProcessingLimit());
                Status = target + " 생성 데이터 읽기 완료: " + Describe(data) + (block == null ? "" : "\n분석 적용 차단: " + block);
            }, new Dictionary<string, string> { ["target_role"] = target, ["input_csv_path"] = Path.GetFullPath(path) });
        }
        private async Task RunAsync(string operation, Func<CancellationToken, Task> action, Dictionary<string, string> configuration = null)
        {
            if (!Ready) return;
            cancellation?.Dispose(); cancellation = new CancellationTokenSource(); idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); IsBusy = true;
            SweepRunRecorder log = null; Exception failure = null;
            try
            {
                configuration = configuration ?? new Dictionary<string, string>(); configuration["active_role"] = ActiveRole; configuration["ref_path"] = RefPath; configuration["signal_path"] = SignalPath; configuration["sweep"] = SelectedSweep?.ToString() ?? "";
                configuration["use_recording_start_as_sweep_start"] = UseRecordingStartAsSweepStart.ToString();
                configuration["sweep_selection_mode"] = selectionMode.ToString();
                configuration["mode_hop_exclusion_tail_ms"] = ModeHopExclusionTailText;
                configuration["mode_hop_exclusion_lead_ms"] = ModeHopExclusionLeadText;
                configuration["mode_hop_exclusion_applied"] = UseModeHopExclusion.ToString();
                log = new SweepRunRecorder(operation, configuration);
                if (parent.LoadedRecording != null) log.Source(parent.LoadedRecording);
                await action(cancellation.Token);
                if (reference != null) JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "ref_preparation.json"), reference.Metadata);
                if (signal != null) JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "signal_preparation.json"), signal.Metadata);
            }
            catch (Exception ex) { failure = ex; Status = ex is OperationCanceledException ? "작업을 취소했습니다. 확정 입력은 유지합니다." : "실패: " + ex.Message; }
            finally
            {
                try { log?.Finish(failure); } catch (Exception ex) { Status += " / 기록 실패: " + ex.Message; }
                IsBusy = false; idle.TrySetResult(true);
            }
        }
        public void RefreshCommands()
        {
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(AnalysisBlockReason)); OnPropertyChanged(nameof(CanAnalyzeConfirmed)); OnPropertyChanged(nameof(AnalysisReadiness));
            OnPropertyChanged(nameof(DiagnosticMode));
            foreach (var command in new[] { BrowseRefCommand, BrowseSignalCommand, LoadCommand, ReviewCommand, ConfirmCommand, ApplyCommand, SetNotebookCropCommand, NotebookPresetCommand, SaveRefCommand, SaveSignalCommand, ImportRefCommand, ImportSignalCommand }) command?.RaiseCanExecuteChanged();
        }
        public void Cancel() { cancellation?.Cancel(); }
        public Task CancelAndWaitAsync() { Cancel(); return idle?.Task ?? Task.CompletedTask; }
        public void Dispose() { disposed = true; Cancel(); activeSource = null; reference = null; signal = null; }
    }
}
