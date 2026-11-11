using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Serialization;
using Microsoft.Win32;
using OFDR_Signal_Core.Services;
using OFDR_Signal_Generator.Infrastructure;

namespace OFDR_Signal_Generator.ViewModels
{
    public sealed class UnifiedAnalysisViewModel : ObservableObject, IDisposable
    {
        private readonly OfdrWorkspaceViewModel workspace;
        private string referencePath = "", signalPath = "", status = "Ref·Signal CSV를 선택하고 전체 분석 실행을 누르세요.", stageSummary = "기존 분석: 대기  |  Reflectogram: 대기  |  ZNCC: 대기";
        private bool busy;
        private CancellationTokenSource cancellation;
        private Task task = Task.CompletedTask;
        private int stage;
        public string ReferencePath { get => referencePath; set { if (SetProperty(ref referencePath, value)) Refresh(); } }
        public string SignalPath { get => signalPath; set { if (SetProperty(ref signalPath, value)) Refresh(); } }
        public bool IsBusy { get => busy; private set { if (SetProperty(ref busy, value)) Refresh(); } }
        public bool ControlsEnabled => !IsBusy && Idle();
        public string Status { get => status; private set => SetProperty(ref status, value); }
        public string StageSummary { get => stageSummary; private set => SetProperty(ref stageSummary, value); }
        public string ResultRoot { get; set; }
        public string ResultDirectory { get; private set; }
        public string RunStatus { get; private set; }
        public RelayCommand BrowseReferenceCommand { get; }
        public RelayCommand BrowseSignalCommand { get; }
        public RelayCommand UseCurrentSelectionCommand { get; }
        public RelayCommand RunCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand OpenResultsCommand { get; }
        public RelayCommand ShowGeneralCommand { get; }
        public RelayCommand ShowReflectogramCommand { get; }
        public RelayCommand ShowZnccCommand { get; }

        public UnifiedAnalysisViewModel(OfdrWorkspaceViewModel workspace)
        {
            this.workspace = workspace;
            ResultRoot = workspace.Reflectogram.ResultRoot;
            BrowseReferenceCommand = new RelayCommand(() => Browse(true), () => ControlsEnabled);
            BrowseSignalCommand = new RelayCommand(() => Browse(false), () => ControlsEnabled);
            UseCurrentSelectionCommand = new RelayCommand(UseCurrentSelection, () => ControlsEnabled);
            RunCommand = new RelayCommand(() => { _ = RunAsync(); }, CanRun);
            CancelCommand = new RelayCommand(Cancel, () => IsBusy);
            OpenResultsCommand = new RelayCommand(() => { try { Process.Start(new ProcessStartInfo(ResultDirectory) { UseShellExecute = true }); } catch (Exception ex) { Status = ex.Message; } }, () => !IsBusy && Directory.Exists(ResultDirectory));
            ShowGeneralCommand = new RelayCommand(() => workspace.WorkspaceSelectedTab = 1, () => !IsBusy && workspace.HasResult);
            ShowReflectogramCommand = new RelayCommand(() => workspace.WorkspaceSelectedTab = 2, () => !IsBusy && workspace.Reflectogram.ReferenceResult != null);
            ShowZnccCommand = new RelayCommand(() => workspace.WorkspaceSelectedTab = 3, () => !IsBusy && workspace.DistributedComparison.Result != null);
            workspace.PropertyChanged += (s,e) => { if (IsBusy && stage == 1 && e.PropertyName == nameof(workspace.ProgressText)) Status = "1/3 기존 분석 · " + workspace.ProgressText; };
            workspace.Reflectogram.PropertyChanged += (s,e) => { if (IsBusy && stage == 2 && e.PropertyName == nameof(workspace.Reflectogram.Status)) Status = "2/3 Reflectogram · " + workspace.Reflectogram.Status; };
            workspace.DistributedComparison.PropertyChanged += (s,e) => { if (IsBusy && stage == 3 && e.PropertyName == nameof(workspace.DistributedComparison.Status)) Status = "3/3 ZNCC · " + workspace.DistributedComparison.Status; };
        }
        private bool Idle() => !workspace.IsBusy && !workspace.ModeHopPreview.IsBusy && !workspace.FinalComparison.IsBusy && !workspace.Reflectogram.IsBusy && !workspace.DistributedComparison.IsBusy;
        private bool CanRun() => !IsBusy && Idle() && File.Exists(ReferencePath) && File.Exists(SignalPath)
            && !string.Equals(Path.GetFullPath(ReferencePath), Path.GetFullPath(SignalPath), StringComparison.OrdinalIgnoreCase);
        private void Browse(bool reference)
        {
            var dialog = new OpenFileDialog { Filter = "4채널 CSV (Tek / Index·Time_s)|*.csv", Title = reference ? "전체 분석 기준 Ref CSV" : "전체 분석 측정 Signal CSV" };
            if (dialog.ShowDialog() == true) { if (reference) ReferencePath = dialog.FileName; else SignalPath = dialog.FileName; }
        }
        public void UseCurrentSelection()
        {
            var r = workspace.Reflectogram.ReferencePath; var s = workspace.Reflectogram.SignalPath;
            if (!File.Exists(r)) r = workspace.ModeHopPreview.SweepPreparation.RefPath;
            if (!File.Exists(r)) r = workspace.ModeHopPreview.SourcePath;
            if (!File.Exists(s)) s = workspace.ModeHopPreview.SweepPreparation.SignalPath;
            ReferencePath = r ?? ""; SignalPath = s ?? "";
            Status = "현재 선택을 가져왔습니다. 서로 다른 Ref·Signal 두 취득 파일인지 확인하고 실행하세요.";
        }
        public Task RunAsync()
        {
            if (!CanRun()) return task;
            task = RunCoreAsync(); return task;
        }
        private async Task RunCoreAsync()
        {
            IsBusy = true; RunStatus = "RUNNING";
            cancellation?.Dispose(); cancellation = new CancellationTokenSource(); var token = cancellation.Token;
            var timer = Stopwatch.StartNew(); var states = new Dictionary<string,string> { ["general"] = "PENDING", ["reflectogram"] = "PENDING", ["zncc"] = "PENDING" };
            var errors = new Dictionary<string,string>();
            var r = ReferencePath; var s = SignalPath;
            string referenceHash = null, signalHash = null;
            var reflectRoot = workspace.Reflectogram.ResultRoot; var znccRoot = workspace.DistributedComparison.ResultRoot;
            ResultDirectory = Path.Combine(ResultRoot, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_all_" + Guid.NewGuid().ToString("N").Substring(0,6));
            OnPropertyChanged(nameof(ResultDirectory));
            try {
                Directory.CreateDirectory(ResultDirectory);
                JsonContractSerializer.Write(Path.Combine(ResultDirectory,"config.json"), new Dictionary<string,string> {
                    ["reference"] = r, ["signal"] = s, ["execution"] = "Sequential: existing general / reflectogram / Ref-Signal B-C ZNCC",
                    ["common_mask"] = "CH2 > 1.8V + 100us tail; complete input recordings", ["general_scope"] = "Whole masked record, original general FFT/segment/window/IFFT/correlation using current main settings; diagnostic QC retained",
                    ["reflectogram_scope"] = "All continuous ranges >=1ms, current reflectogram settings",
                    ["zncc_scope"] = "Longest continuous range per capture; current ZNCC settings; Ref-Signal pair only",
                    ["diagnostic_mode"] = "true; QC failures never relabelled as passes" });
                workspace.ResetForUnified(); workspace.Reflectogram.ClearResults(); workspace.DistributedComparison.ClearResults();
                await Stage("general",1,async () => {
                    workspace.AllowFailedPhaseQcForDiagnostics = true; workspace.NotebookReproduction = false; workspace.StageComparisonEnabled = false;
                    Status = "1/3 Ref 전체 CSV·위상 진단 준비";
                    var progress = new Progress<OFDR_Signal_Core.Models.OperationProgress>(p => { if (IsBusy && stage == 1) Status = "1/3 " + p.Stage; });
                    var reference = await Task.Run(() => FourChannelCsvPreparation.Read(r,"Ref",workspace.MaxNonmonotonicRatio,token,progress),token);
                    GC.Collect();
                    Status = "1/3 Signal 전체 CSV·위상 진단 준비";
                    var signal = await Task.Run(() => FourChannelCsvPreparation.Read(s,"Signal",workspace.MaxNonmonotonicRatio,token,progress),token);
                    FourChannelCsvPreparation.ValidateDistinct(reference,signal); token.ThrowIfCancellationRequested();
                    referenceHash = reference.Metadata.SourceSha256; signalHash = signal.Metadata.SourceSha256;
                    workspace.ApplyPreparedSweeps(reference,signal);
                    if (workspace.IsProcessingQcBlocked) throw new InvalidOperationException(workspace.ProcessingQcBlockReason);
                    await workspace.RunUnifiedProcessingAsync(Path.Combine(ResultDirectory,"general"));
                    token.ThrowIfCancellationRequested();
                    if (!workspace.HasResult) throw new InvalidOperationException(workspace.ProgressText);
                },states,errors,token);
                GC.Collect();
                await Stage("reflectogram",2,async () => {
                    workspace.Reflectogram.ReferencePath = r; workspace.Reflectogram.SignalPath = s;
                    workspace.Reflectogram.ResultRoot = Path.Combine(ResultDirectory,"reflectogram");
                    await workspace.Reflectogram.RunForUnifiedAsync(); token.ThrowIfCancellationRequested();
                    if (workspace.Reflectogram.RunStatus != "COMPLETED") throw new InvalidOperationException(workspace.Reflectogram.Status);
                    if (referenceHash != null && (referenceHash != workspace.Reflectogram.ReferenceResult.Sha256 || signalHash != workspace.Reflectogram.SignalResult.Sha256))
                        throw new IOException("분석 단계 사이에 입력 CSV 내용이 변경되었습니다. 저장 완료된 파일로 다시 실행하세요.");
                    referenceHash = workspace.Reflectogram.ReferenceResult.Sha256; signalHash = workspace.Reflectogram.SignalResult.Sha256;
                },states,errors,token);
                GC.Collect();
                await Stage("zncc",3,async () => {
                    workspace.DistributedComparison.ReferencePath = r; workspace.DistributedComparison.HeatedPath = s;
                    workspace.DistributedComparison.ResultRoot = Path.Combine(ResultDirectory,"zncc");
                    await workspace.DistributedComparison.RunForUnifiedAsync(); token.ThrowIfCancellationRequested();
                    if (workspace.DistributedComparison.RunStatus != "COMPLETED") throw new InvalidOperationException(workspace.DistributedComparison.Status);
                    if (referenceHash != null && (referenceHash != workspace.DistributedComparison.Result.Sources[0].SourceSha256 || signalHash != workspace.DistributedComparison.Result.Sources[1].SourceSha256))
                        throw new IOException("ZNCC 입력이 앞 단계의 CSV와 다릅니다. 입력 파일을 변경하지 말고 다시 실행하세요.");
                },states,errors,token);
                RunStatus = errors.Count == 0 ? "COMPLETED" : errors.Count == 3 ? "FAILED" : "PARTIAL";
                Status = errors.Count == 0 ? "전체 분석 완료 · 아래 결과 버튼 또는 각 탭에서 기존 결과 / Reflectogram / ZNCC를 확인하세요."
                    : "일부 분석 실패 · 완료된 결과는 유지하며 실패 사유는 결과 폴더의 metrics.json에 기록했습니다.";
            }
            catch (OperationCanceledException) { RunStatus = "CANCELED"; Status = "전체 분석을 취소했습니다. 완료·부분 결과는 보존했습니다."; }
            catch (Exception ex) { RunStatus = "FAILED"; errors["workflow"] = ex.ToString(); Status = "전체 분석 실패: " + ex.Message; }
            finally {
                stage = 0; workspace.Reflectogram.ResultRoot = reflectRoot; workspace.DistributedComparison.ResultRoot = znccRoot;
                try {
                    Directory.CreateDirectory(ResultDirectory);
                    JsonContractSerializer.Write(Path.Combine(ResultDirectory,"metrics.json"), new UnifiedAnalysisMetrics {
                        Status = RunStatus, Stages = states, Errors = errors, ElapsedSeconds = timer.Elapsed.TotalSeconds,
                        GeneralPhaseQc = workspace.ProcessingResult?.PhaseQcSummary,
                        ReflectogramResult = workspace.Reflectogram.ResultDirectory, ZnccResult = workspace.DistributedComparison.ResultDirectory });
                    File.WriteAllText(Path.Combine(ResultDirectory,"summary.md"), "# Unified Ref/Signal analysis\n\nStatus: " + RunStatus + "\n\n" + StageSummary
                        + "\n\nGeneral uses the whole masked recording; reflectogram averages all normal continuous ranges; ZNCC uses the longest continuous range in each capture. Each stage records its own config and provenance. Results retain diagnostic/QC labels.\n\n" + string.Join("\n",errors.Values));
                } catch (Exception ex) { RunStatus = "FAILED"; Status += " / 기록 실패: " + ex.Message; }
                IsBusy = false;
                if (workspace.HasResult) workspace.WorkspaceSelectedTab = 1;
            }
        }
        private async Task Stage(string key,int index,Func<Task> action,Dictionary<string,string> states,Dictionary<string,string> errors,CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); stage=index; states[key]="RUNNING"; UpdateStages(states);
            try { await action(); states[key]="COMPLETED"; }
            catch(OperationCanceledException) { states[key]="CANCELED"; throw; }
            catch(Exception ex) { states[key]="FAILED"; errors[key]=ex.ToString(); }
            finally { UpdateStages(states); }
        }
        private void UpdateStages(Dictionary<string,string> states)
        {
            Func<string,string> label = v => v=="COMPLETED"?"완료":v=="RUNNING"?"실행 중":v=="FAILED"?"실패":v=="CANCELED"?"취소":"대기";
            StageSummary="기존 분석: "+label(states["general"])+"  |  Reflectogram: "+label(states["reflectogram"])+"  |  ZNCC: "+label(states["zncc"]);
        }
        private void Cancel()
        {
            cancellation?.Cancel();
            _ = workspace.CancelAndWaitAsync(); _ = workspace.Reflectogram.CancelAndWaitAsync(); _ = workspace.DistributedComparison.CancelAndWaitAsync();
        }
        public async Task CancelAndWaitAsync() { Cancel(); await task; }
        public void Refresh()
        {
            OnPropertyChanged(nameof(ControlsEnabled));
            foreach(var command in new[]{BrowseReferenceCommand,BrowseSignalCommand,UseCurrentSelectionCommand,RunCommand,CancelCommand,OpenResultsCommand,ShowGeneralCommand,ShowReflectogramCommand,ShowZnccCommand}) command?.RaiseCanExecuteChanged();
        }
        public void Dispose() { cancellation?.Cancel(); cancellation?.Dispose(); }
    }
    [DataContract]
    internal sealed class UnifiedAnalysisMetrics
    {
        [DataMember] public string Status { get; set; }
        [DataMember] public Dictionary<string,string> Stages { get; set; }
        [DataMember] public Dictionary<string,string> Errors { get; set; }
        [DataMember] public double ElapsedSeconds { get; set; }
        [DataMember] public string GeneralPhaseQc { get; set; }
        [DataMember] public string ReflectogramResult { get; set; }
        [DataMember] public string ZnccResult { get; set; }
    }
}
