using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Services;
using OFDR_Signal_Generator.Infrastructure;

namespace OFDR_Signal_Generator.ViewModels
{
    public sealed class StrainComparisonEntry : ObservableObject
    {
        private bool displayEnabled = true;
        public bool DisplayEnabled { get => displayEnabled; set => SetProperty(ref displayEnabled, value); }
        public StrainComparisonSnapshot Snapshot { get; }
        public Brush LineBrush { get; }
        public RelayCommand DeleteCommand { get; internal set; }
        public int Number { get; }
        public string Name { get; }
        public double[] XValues { get; }
        public double[] YValues { get; }
        public bool IsNotebook { get; }
        public bool IsReference { get; }
        public string ProcessingConditions { get; }
        private readonly string notebookReference, notebookSignal;
        private readonly double notebookPhaseRatio;
        private readonly bool? notebookPhaseQcPassed;
        private readonly DateTime recordedAtUtc;
        public string RecordedTime => recordedAtUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        public string Reference => IsNotebook ? notebookReference : Snapshot.ReferenceId;
        public string Signal => IsNotebook ? notebookSignal : Snapshot.SignalId;
        public string Quality => IsNotebook ? string.Format(CultureInfo.InvariantCulture, "진단 · {0} · 위상 지표 {1:P2}", notebookPhaseQcPassed == true ? "QC 통과" : notebookPhaseQcPassed == false ? "QC 불합격" : "미교정", notebookPhaseRatio)
            : (Snapshot.IsDiagnosticAnalysis ? "진단 · " : "") + (Snapshot.InputPhaseQcPassed ? "QC 통과" : "QC 불합격");
        public string Range => string.Format(CultureInfo.InvariantCulture, "{0:G6}~{1:G6} {2}", XValues[0], XValues.Last(), IsNotebook ? "index" : "m");
        public StrainComparisonEntry(StrainComparisonSnapshot snapshot, int number, string name, Brush brush)
        {
            Snapshot = snapshot; Number = number; Name = string.IsNullOrWhiteSpace(name) ? "결과 " + number : name.Trim(); LineBrush = brush;
            XValues = snapshot.DistancesMeters; YValues = snapshot.StrainMicrostrain; recordedAtUtc = snapshot.RecordedAtUtc;
        }
        internal StrainComparisonEntry(NotebookComparisonCurve curve, int number, Brush brush)
            : this(curve.Profile, number, curve.Label, brush, Path.GetFileNameWithoutExtension(curve.Reference.Path), Path.GetFileNameWithoutExtension(curve.Source.Path), curve.Source.Processing.NonmonotonicRatio, number == 1) { }
        internal StrainComparisonEntry(NotebookTemperatureProfile profile, int number, string name, Brush brush,
            string reference, string signal, double phaseRatio, bool isReference, string processingConditions = null, bool? phaseQcPassed = null)
        {
            IsNotebook = true; IsReference = isReference; Number = number; Name = name;
            ProcessingConditions = processingConditions ?? "노트북 고정 조건 / 독립 span / 전체 Hann";
            notebookReference = reference; notebookSignal = signal; notebookPhaseRatio = phaseRatio;
            notebookPhaseQcPassed = phaseQcPassed;
            LineBrush = brush; recordedAtUtc = DateTime.UtcNow;
            XValues = Enumerable.Range(0, profile.Shifts.Length).Select(i => (double)i).ToArray();
            YValues = (double[])profile.Shifts.Clone();
        }
    }

    public sealed class FinalStrainComparisonViewModel : ObservableObject
    {
        private readonly Func<IndependentOfdrProcessingResult> currentResult;
        private readonly Func<bool> idle;
        private readonly ObservableCollection<StrainComparisonEntry> entries = new ObservableCollection<StrainComparisonEntry>();
        private readonly ObservableCollection<StrainComparisonEntry> notebookEntries = new ObservableCollection<StrainComparisonEntry>();
        private readonly ReadOnlyObservableCollection<StrainComparisonEntry> strainView, notebookView;
        private static readonly Brush[] Colors = { Brushes.DeepSkyBlue, Brushes.Orange, Brushes.LimeGreen, Brushes.Violet, Brushes.Gold, Brushes.HotPink, Brushes.Turquoise, Brushes.Coral, Brushes.CornflowerBlue, Brushes.YellowGreen };
        private readonly HashSet<Color> usedColors = new HashSet<Color>();
        private int colorSequence, notebookSequence;
        private bool notebookReferenceRecorded;
        private string recordName = "", status = "분석을 완료한 뒤 '현재 값 기록'을 누르세요. 기록별 삭제 또는 Clear로 지울 수 있으며, 앱을 닫으면 초기화됩니다.";
        private int sequence;
        private int comparisonModeIndex;
        private string notebookResultDirectory = DefaultNotebookDirectory();
        private bool isBusy;
        private CancellationTokenSource notebookCancellation;
        private Task notebookTask = Task.CompletedTask;
        public ReadOnlyObservableCollection<StrainComparisonEntry> Entries => IsNotebookComparison ? notebookView : strainView;
        public string[] ComparisonModes { get; } = new[] { "거리별 strain", "단계별·노트북 비교 (lag)" };
        public int ComparisonModeIndex
        {
            get => comparisonModeIndex;
            set
            {
                if (value < 0 || value > 1 || !SetProperty(ref comparisonModeIndex, value)) return;
                OnPropertyChanged(nameof(Entries)); OnPropertyChanged(nameof(PlotTitle)); OnPropertyChanged(nameof(XAxisTitle)); OnPropertyChanged(nameof(YAxisTitle)); Refresh();
            }
        }
        public bool IsNotebookComparison => ComparisonModeIndex == 1;
        public bool IsBusy { get => isBusy; private set { if (SetProperty(ref isBusy, value)) Refresh(); } }
        public bool ControlsEnabled => !IsBusy && idle();
        public string NotebookResultDirectory { get => notebookResultDirectory; set => SetProperty(ref notebookResultDirectory, value); }
        public string PlotTitle => IsNotebookComparison ? "처리 단계별·노트북 결과 비교 (미교정 lag)" : "거리별 strain 비교 (με)";
        public string XAxisTitle => IsNotebookComparison ? "프로파일 순번 (FFT bin = 순번 × 각 기록의 이동 간격)" : "거리 (m)";
        public string YAxisTitle => IsNotebookComparison ? "상관 lag (sample)" : "strain (με)";
        public RelayCommand RecordCommand { get; }
        public RelayCommand ClearCommand { get; }
        public RelayCommand LoadNotebookCommand { get; }
        public string RecordName { get => recordName; set => SetProperty(ref recordName, value); }
        public string Status { get => status; private set => SetProperty(ref status, value); }
        public string CountDescription => "누적 " + Entries.Count + "개 · 표시 " + Entries.Count(e => e.DisplayEnabled) + "개 · " + (IsNotebookComparison ? "순번 / lag (sample)" : "거리 (m) / strain (με)");
        public string RecordReadiness => currentResult()?.NotebookProfile != null ? "확정 Raw 입력의 전체 처리 결과를 '현재 값 기록'으로 누적할 수 있습니다. Ref 자기상관 기준선도 기록합니다. · 미교정 lag"
            : IsNotebookComparison ? "261106 C# 계산 결과 · Ref는 자기상관 0 기준선 · 60/80/100은 tek0002 대비 lag · 거리/strain 교정 전"
            : !idle() ? "현재 처리가 끝나면 기록할 수 있습니다."
            : StrainComparisonSnapshot.CanCapture(currentResult()) ? "현재 표시된 완료 결과를 기록합니다. 각 곡선의 QC와 거리 환산값은 기록 시점의 값입니다."
            : "거리별 strain 완료 결과가 필요합니다. 물리 거리축을 사용해 분석을 실행하세요.";
        public FinalStrainComparisonViewModel(Func<IndependentOfdrProcessingResult> result, Func<bool> isIdle)
        {
            currentResult = result; idle = isIdle; strainView = new ReadOnlyObservableCollection<StrainComparisonEntry>(entries);
            notebookView = new ReadOnlyObservableCollection<StrainComparisonEntry>(notebookEntries);
            RecordCommand = new RelayCommand(Record, () => ControlsEnabled && (currentResult()?.NotebookProfile != null || (!IsNotebookComparison && StrainComparisonSnapshot.CanCapture(currentResult()))));
            ClearCommand = new RelayCommand(Clear, () => ControlsEnabled && Entries.Count > 0);
            LoadNotebookCommand = new RelayCommand(() => { _ = LoadNotebookAsync(); }, () => ControlsEnabled);
        }
        public Task LoadNotebookAsync()
        {
            if (!ControlsEnabled) return notebookTask;
            notebookTask = LoadNotebookCoreAsync(); return notebookTask;
        }
        private async Task LoadNotebookCoreAsync()
        {
            IsBusy = true; notebookCancellation = new CancellationTokenSource();
            Status = "C# 프로파일을 읽고 Ref 자기상관을 계산하는 중입니다…";
            var directory = NotebookResultDirectory;
            try
            {
                var comparison = await Task.Run(() => new NotebookTemperatureComparisonLoader().Load(directory, notebookCancellation.Token));
                foreach (var entry in notebookEntries) entry.PropertyChanged -= EntryPropertyChanged;
                notebookEntries.Clear();
                notebookSequence = 0; notebookReferenceRecorded = true;
                for (var i = 0; i < comparison.Curves.Count; i++)
                {
                    var entry = new StrainComparisonEntry(comparison.Curves[i], ++notebookSequence, NextBrush(i == 0));
                    AddEntry(notebookEntries, entry);
                }
                ComparisonModeIndex = 1;
                Status = "Ref · 60C · 80C · 100C 표시 완료 · 각 " + notebookEntries[0].YValues.Length.ToString("N0") + "점 · " + comparison.ResultDirectory;
            }
            catch (OperationCanceledException) { Status = "온도 비교 불러오기를 취소했습니다."; }
            catch (Exception ex) { Status = "온도 비교 불러오기 실패: " + ex.Message; }
            finally { notebookCancellation.Dispose(); notebookCancellation = null; IsBusy = false; }
        }
        public async Task CancelAndWaitAsync() { notebookCancellation?.Cancel(); await notebookTask; }
        private static string DefaultNotebookDirectory()
        {
            foreach (var seed in new[] { AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory })
                for (var folder = new DirectoryInfo(seed); folder != null; folder = folder.Parent)
                {
                    var candidate = Path.Combine(folder.FullName, "harness", "results", "20261106_261106_notebook_temperature_verified");
                    if (Directory.Exists(candidate)) return candidate;
                }
            return Path.Combine("harness", "results", "20261106_261106_notebook_temperature_verified");
        }
        private void Record()
        {
            if (!RecordCommand.CanExecute(null)) { Status = RecordReadiness; return; }
            if (currentResult()?.NotebookProfile != null) { RecordNotebook(); return; }
            try
            {
                var snapshot = StrainComparisonSnapshot.Capture(currentResult(), DateTime.UtcNow);
                var number = ++sequence;
                var entry = new StrainComparisonEntry(snapshot, number, RecordName, NextBrush());
                AddEntry(entries, entry);
                Status = "결과 " + number + " 기록 완료 · " + snapshot.DistancesMeters.Length.ToString("N0", CultureInfo.InvariantCulture) + " points · " + entries[entries.Count - 1].Quality;
            }
            catch (Exception ex) { Status = "기록 실패: " + ex.Message; }
            Refresh();
        }
        private void RecordNotebook()
        {
            var result = currentResult();
            var conditions = result.Config?.StageComparisonEnabled == true ? result.Config.StageComparison.Description
                + " / CH4 mean=" + result.Config.StageComparison.RemoveMeasurementMean
                + " / local mean=" + result.Config.StageComparison.CenterLocalMagnitudes + " / corr norm=" + result.Config.NormalizeCrossCorrelation
                + " / max lag=" + result.Config.StageComparison.MaximumLagSamples + " / stop bin=" + result.Config.StageComparison.StopFftBin : "노트북 고정 조건 / 독립 span / 전체 Hann";
            conditions += " / " + result.Config.WindowFunction + " / segment=" + result.Config.SegmentSize + " / hop=" + result.Config.SegmentHop + " / padding=" + result.Config.SegmentIfftZeroPadFactor;
            if (!notebookReferenceRecorded)
            {
                var reference = new StrainComparisonEntry(result.NotebookReferenceProfile, ++notebookSequence, "Ref", NextBrush(true),
                    result.ReferenceId, result.ReferenceId, result.KLinearization.NonmonotonicRatio, true, conditions, result.KLinearization.PhaseQcPassed);
                AddEntry(notebookEntries, reference); notebookReferenceRecorded = true;
            }
            var name = string.IsNullOrWhiteSpace(RecordName) ? result.SignalId : RecordName.Trim();
            var entry = new StrainComparisonEntry(result.NotebookProfile, ++notebookSequence, name, NextBrush(),
                result.ReferenceId, result.SignalId, result.SignalKLinearization.NonmonotonicRatio, false, conditions, result.SignalKLinearization.PhaseQcPassed);
            AddEntry(notebookEntries, entry); ComparisonModeIndex = 1;
            Status = "현재 확정 Raw의 전체 처리 결과 기록: " + name + " · " + entry.YValues.Length.ToString("N0") + "점";
            Refresh();
        }
        private void EntryPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(StrainComparisonEntry.DisplayEnabled)) OnPropertyChanged(nameof(CountDescription));
        }
        private void AddEntry(ObservableCollection<StrainComparisonEntry> collection, StrainComparisonEntry entry)
        {
            entry.DeleteCommand = new RelayCommand(() =>
            {
                if (!ControlsEnabled || !collection.Remove(entry)) return;
                entry.PropertyChanged -= EntryPropertyChanged;
                entry.DeleteCommand.RaiseCanExecuteChanged();
                Status = "기록 " + entry.Number + " (" + entry.Name + ")을 삭제했습니다.";
                Refresh();
            }, () => ControlsEnabled && collection.Contains(entry));
            entry.PropertyChanged += EntryPropertyChanged; collection.Add(entry);
        }
        private Brush NextBrush(bool reference = false)
        {
            if (reference && usedColors.Add(System.Windows.Media.Colors.LightGray)) return Brushes.LightGray;
            while (true)
            {
                var index = colorSequence++;
                Color color;
                if (index < Colors.Length) color = ((SolidColorBrush)Colors[index]).Color;
                else
                {
                    // Golden-angle hue progression; reject rounded RGB collisions, including earlier palette colors.
                    var hue = ((index - Colors.Length) * 0.618033988749895 + 0.09) % 1 * 6;
                    var minimum = 0.16 + 0.12 * ((index * 0.414213562373095) % 1);
                    var chroma = 0.82 + 0.17 * ((index * 0.754877666246693) % 1) - minimum;
                    var x = chroma * (1 - Math.Abs(hue % 2 - 1));
                    double r = 0, g = 0, b = 0;
                    switch ((int)hue)
                    {
                        case 0: r = chroma; g = x; break;
                        case 1: r = x; g = chroma; break;
                        case 2: g = chroma; b = x; break;
                        case 3: g = x; b = chroma; break;
                        case 4: r = x; b = chroma; break;
                        default: r = chroma; b = x; break;
                    }
                    color = Color.FromRgb((byte)Math.Round((r + minimum) * 255), (byte)Math.Round((g + minimum) * 255), (byte)Math.Round((b + minimum) * 255));
                }
                if (!usedColors.Add(color)) continue;
                var brush = new SolidColorBrush(color); brush.Freeze(); return brush;
            }
        }
        private void Clear()
        {
            var active = IsNotebookComparison ? notebookEntries : entries;
            foreach (var entry in active) entry.PropertyChanged -= EntryPropertyChanged;
            active.Clear();
            if (!IsNotebookComparison) sequence = 0; else { notebookSequence = 0; notebookReferenceRecorded = false; }
            Status = "현재 비교 기록을 지웠습니다."; Refresh();
        }
        public void Refresh()
        {
            OnPropertyChanged(nameof(CountDescription)); OnPropertyChanged(nameof(RecordReadiness));
            OnPropertyChanged(nameof(ControlsEnabled));
            RecordCommand?.RaiseCanExecuteChanged(); ClearCommand?.RaiseCanExecuteChanged(); LoadNotebookCommand?.RaiseCanExecuteChanged();
            foreach (var entry in entries.Concat(notebookEntries)) entry.DeleteCommand?.RaiseCanExecuteChanged();
        }
    }
}
