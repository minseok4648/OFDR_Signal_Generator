using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;
using OFDR_Signal_Generator.Infrastructure;

namespace OFDR_Signal_Generator.ViewModels
{
    public sealed class ReflectogramViewModel : ObservableObject, IDisposable
    {
        private readonly Func<bool> idle;
        private string referencePath = "", signalPath = "", status = "Ref에 4채널 CSV를 선택하고 실행하세요. Signal은 선택 사항입니다. CH2 mask → 정상 구간별 FFT → 전력 평균으로 B/C를 비교합니다.";
        private string window = "Rectangular";
        private double bandpassLowMHz = 1, bandpassHighMHz = 12;
        private bool autoLimitBandpass = true;
        private bool busy;
        private CancellationTokenSource cancellation;
        private Task task = Task.CompletedTask;
        public string ReferencePath { get => referencePath; set { if (SetProperty(ref referencePath, value)) Refresh(); } }
        public string SignalPath { get => signalPath; set { if (SetProperty(ref signalPath, value)) Refresh(); } }
        public string Window { get => window; set => SetProperty(ref window, value); }
        public double BandpassLowMHz { get => bandpassLowMHz; set => SetProperty(ref bandpassLowMHz, value); }
        public double BandpassHighMHz { get => bandpassHighMHz; set => SetProperty(ref bandpassHighMHz, value); }
        public bool AutoLimitBandpass { get => autoLimitBandpass; set => SetProperty(ref autoLimitBandpass, value); }
        public string[] Windows { get; } = { "Rectangular", "Hann" };
        public bool IsBusy { get => busy; private set { if (SetProperty(ref busy, value)) Refresh(); } }
        public bool ControlsEnabled => !IsBusy;
        public string Status { get => status; private set => SetProperty(ref status, value); }
        public string Summary { get; private set; } = "거리 = 광경로차의 단일 통과 등가 거리. 0 m는 물리적 케이블 입구로 교정되지 않았습니다.\n전력밀도: dB re 1 V²/m. 파란 실선 C = AUX 보정 후, 주황 점선 B = nominal sweep 보정 전.";
        public string ResultDirectory { get; private set; }
        public string ResultRoot { get; set; } = FindResultRoot();
        public string RunStatus { get; private set; }
        public ReflectogramRecord ReferenceResult { get; private set; }
        public ReflectogramRecord SignalResult { get; private set; }
        public bool HasSignalResult => SignalResult != null;
        public PowerDensityViewport ReferencePlot { get; private set; }
        public PowerDensityViewport SignalPlot { get; private set; }
        public RelayCommand BrowseReferenceCommand { get; }
        public RelayCommand BrowseSignalCommand { get; }
        public RelayCommand RunCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand OpenResultsCommand { get; }

        public ReflectogramViewModel(Func<bool> idle)
        {
            this.idle = idle;
            BrowseReferenceCommand = new RelayCommand(() => Browse(true), () => !IsBusy);
            BrowseSignalCommand = new RelayCommand(() => Browse(false), () => !IsBusy);
            RunCommand = new RelayCommand(() => { _ = RunAsync(); }, CanRun);
            CancelCommand = new RelayCommand(() => cancellation?.Cancel(), () => IsBusy);
            OpenResultsCommand = new RelayCommand(() => {
                try { Process.Start(new ProcessStartInfo(ResultDirectory) { UseShellExecute = true }); }
                catch (Exception ex) { Status = ex.Message; }
            }, () => !IsBusy && Directory.Exists(ResultDirectory));
        }
        private void Browse(bool reference)
        {
            var dialog = new OpenFileDialog { Title = reference ? "Ref 4채널 CSV (Tek / Index·Time_s 통합)" : "Signal 4채널 CSV (Tek / Index·Time_s 통합)", Filter = "4채널 CSV (*.csv)|*.csv" };
            if (dialog.ShowDialog() == true) { if (reference) ReferencePath = dialog.FileName; else SignalPath = dialog.FileName; }
        }
        private bool CanRun() => !IsBusy && idle() && File.Exists(ReferencePath) && (string.IsNullOrWhiteSpace(SignalPath) || File.Exists(SignalPath));
        public Task RunAsync()
        {
            if (!CanRun()) return task;
            task = RunCoreAsync(); return task;
        }
        internal Task RunForUnifiedAsync()
        {
            if (IsBusy) throw new InvalidOperationException("Reflectogram이 실행 중입니다.");
            if (!File.Exists(ReferencePath) || !File.Exists(SignalPath)) throw new FileNotFoundException("Ref·Signal 두 파일을 선택하세요.");
            task = RunCoreAsync(); return task;
        }
        internal void ClearResults()
        {
            ResultDirectory = null; RunStatus = "PENDING"; OnPropertyChanged(nameof(ResultDirectory));
            ReferenceResult = null; SignalResult = null; ReferencePlot = null; SignalPlot = null;
            OnPropertyChanged(nameof(ReferencePlot)); OnPropertyChanged(nameof(SignalPlot)); OnPropertyChanged(nameof(HasSignalResult));
            Summary = "새 통합 실행 결과 대기"; OnPropertyChanged(nameof(Summary));
        }
        private async Task RunCoreAsync()
        {
            IsBusy = true; RunStatus = "RUNNING";
            cancellation?.Dispose(); cancellation = new CancellationTokenSource();
            var token = cancellation.Token; var watch = Stopwatch.StartNew();
            var settings = new ReflectogramSettings { Window = Window, BandpassLowMHz = BandpassLowMHz, BandpassHighMHz = BandpassHighMHz, AutoLimitBandpass = AutoLimitBandpass };
            var refPath = ReferencePath; var sigPath = SignalPath;
            string error = null; string provider = null;
            ReferenceResult = null; SignalResult = null; ReferencePlot = null; SignalPlot = null;
            OnPropertyChanged(nameof(HasSignalResult));
            OnPropertyChanged(nameof(ReferencePlot)); OnPropertyChanged(nameof(SignalPlot));
            Summary = "처리 중 · 이전 결과는 각 실행의 결과 폴더에 보존됩니다."; OnPropertyChanged(nameof(Summary));
            ResultDirectory = Path.Combine(ResultRoot, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_reflectogram_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            OnPropertyChanged(nameof(ResultDirectory));
            try
            {
                Directory.CreateDirectory(ResultDirectory);
                JsonContractSerializer.Write(Path.Combine(ResultDirectory, "config.json"), settings);
                JsonContractSerializer.Write(Path.Combine(ResultDirectory, "inputs.json"), new Dictionary<string, string> { ["reference"] = refPath, ["signal"] = sigPath });
                var progress = new Progress<string>(text => Status = text);
                var pair = await Task.Run(() => {
                    var engine = new ReflectogramEngine(); provider = engine.ProviderDescription;
                    var reference = engine.Run(refPath, settings, Path.Combine(ResultDirectory, "reference"), token, ((IProgress<string>)progress).Report);
                    // Reader's full acquisition arrays are no longer retained before loading the second recording.
                    GC.Collect();
                    var signal = string.IsNullOrWhiteSpace(sigPath) ? null : engine.Run(sigPath, settings, Path.Combine(ResultDirectory, "signal"), token, ((IProgress<string>)progress).Report);
                    return Tuple.Create(reference, signal);
                }, token);
                token.ThrowIfCancellationRequested();
                ReferenceResult = pair.Item1; SignalResult = pair.Item2;
                ReferencePlot = new PowerDensityViewport(ReferenceResult); SignalPlot = SignalResult == null ? null : new PowerDensityViewport(SignalResult);
                OnPropertyChanged(nameof(HasSignalResult));
                OnPropertyChanged(nameof(ReferencePlot)); OnPropertyChanged(nameof(SignalPlot));
                Summary = Describe("Ref", ReferenceResult) + (SignalResult == null ? "\n단일 파일 Reflectogram · Signal 파일을 추가하면 두 기록을 함께 비교합니다." : "\n" + Describe("Signal", SignalResult))
                    + "\n위상 QC 미통과 구간도 진단 목적으로 포함합니다. 띠 폭 감소는 전력 추정의 분산 감소이며, 절대 거리·strain 교정의 통과를 뜻하지 않습니다.";
                OnPropertyChanged(nameof(Summary));
                RunStatus = "COMPLETED"; Status = "완료 · B/C 전력밀도 비교. 전체 보기 / 거리 범위 입력 / 휠 확대를 사용할 수 있습니다.";
            }
            catch (OperationCanceledException ex) { RunStatus = "CANCELLED"; error = ex.Message; Status = "취소됨 · 부분 결과와 실행 기록을 보존했습니다."; }
            catch (Exception ex) { RunStatus = "FAILED"; error = ex.ToString(); Status = "실패: " + ex.Message; }
            finally
            {
                try {
                    Directory.CreateDirectory(ResultDirectory);
                    JsonContractSerializer.Write(Path.Combine(ResultDirectory, "metrics.json"), new Dictionary<string, string> {
                        ["status"] = RunStatus, ["error"] = error, ["provider"] = provider,
                        ["elapsed_seconds"] = watch.Elapsed.TotalSeconds.ToString("R", CultureInfo.InvariantCulture),
                        ["peak_working_set_bytes"] = Process.GetCurrentProcess().PeakWorkingSet64.ToString(CultureInfo.InvariantCulture) });
                    File.WriteAllText(Path.Combine(ResultDirectory, "summary.md"), "# Reflectogram power average\n\nStatus: " + RunStatus
                        + "\n\n" + Summary + "\n\n" + settings.Method + "\n\nWindow=" + settings.Window + "; equal segment power mean BEFORE 10log10; zero padding only interpolates the spectrum, not physical resolution.\n\n" + error);
                } catch (Exception ex) { RunStatus = "FAILED"; Status += " / 결과 기록 실패: " + ex.Message; }
                IsBusy = false;
            }
        }
        private static string Describe(string name, ReflectogramRecord r) => string.Format(CultureInfo.InvariantCulture,
            "{0}: {1:N0}점 · {2}구간 평균 · peak B {3:F3} → C {4:F3} m · C 잡음 띠 P95–P05 {5:F2} dB / 전체 폭 {6:F2} dB (26–36 m) · 위상 QC 미통과 {7}/{2}",
            name, r.SampleCount, r.Ranges.Count, r.Metrics["B_peak_m"], r.Metrics["C_peak_m"], r.Metrics["C_noise_P95_P05_dB"], r.Metrics["C_noise_max_min_dB"], r.Metrics["phase_qc_failed_ranges"])
            + string.Format(CultureInfo.InvariantCulture, " · Fs={0:G7}MS/s · 실제 AUX 대역 {1:G6}–{2:G6}MHz", 1e-6 / r.SampleIntervalSeconds, r.AppliedBandpassLowMHz, r.AppliedBandpassHighMHz);
        public void Refresh()
        {
            OnPropertyChanged(nameof(ControlsEnabled)); RunCommand?.RaiseCanExecuteChanged(); CancelCommand?.RaiseCanExecuteChanged();
            BrowseReferenceCommand?.RaiseCanExecuteChanged(); BrowseSignalCommand?.RaiseCanExecuteChanged(); OpenResultsCommand?.RaiseCanExecuteChanged();
        }
        public async Task CancelAndWaitAsync() { cancellation?.Cancel(); await task; }
        public void Dispose() { cancellation?.Cancel(); cancellation?.Dispose(); }
        private static string FindResultRoot()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null) {
                if (Directory.Exists(Path.Combine(directory.FullName, "OFDR_Signal_Core"))) return Path.Combine(directory.FullName, "harness", "results");
                directory = directory.Parent;
            }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "harness", "results");
        }
    }
}
