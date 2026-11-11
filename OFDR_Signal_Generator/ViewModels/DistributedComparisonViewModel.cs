using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Generator.Infrastructure;

namespace OFDR_Signal_Generator.ViewModels
{
    public sealed class DistributedComparisonViewModel : ObservableObject, IDisposable
    {
        private const string DataRoot = @"C:\Project\OFDR\Datas\data\측정 시간_0.2s\100M_Sampling";
        private readonly Func<bool> idle;
        private string reference = Path.Combine(DataRoot,"ref.csv"), repeat = Path.Combine(DataRoot,"ref (2).csv"), heated = Path.Combine(DataRoot,"heating.csv");
        private string status = "기준·상온 반복·가열 원본을 선택하여 B/C를 처리하거나 저장된 comparison.json을 불러오세요.";
        private bool busy;
        private CancellationTokenSource cancellation;
        private Task task = Task.CompletedTask;
        private DistributedComparisonResult result;
        private DistributedCurve selectedCurve;
        public string ReferencePath { get => reference; set { if (SetProperty(ref reference,value)) Refresh(); } }
        public string RepeatPath { get => repeat; set { if (SetProperty(ref repeat,value)) Refresh(); } }
        public string HeatedPath { get => heated; set { if (SetProperty(ref heated,value)) Refresh(); } }
        public bool IsBusy { get => busy; private set { if(SetProperty(ref busy,value)) Refresh(); } }
        public bool ControlsEnabled => !IsBusy;
        public string Status { get => status; private set => SetProperty(ref status,value); }
        public string ResultDirectory { get; private set; }
        public string RunStatus { get; private set; }
        public string ResultRoot { get; set; } = FindRoot();
        public IndependentOfdrProcessingConfig Config { get; private set; } = DistributedComparisonEngine.DefaultConfig();
        public DistributedComparisonResult Result { get => result; private set => SetProperty(ref result,value); }
        public IList<DistributedCurve> Curves => Result?.Curves;
        public DistributedCurve SelectedCurve { get => selectedCurve; set { if(SetProperty(ref selectedCurve,value)) { OnPropertyChanged(nameof(Points)); OnPropertyChanged(nameof(CurveDescription)); } } }
        public IList<StageQualityPoint> Points => SelectedCurve?.Points;
        public string CurveDescription => SelectedCurve == null ? "" : SelectedCurve.Pair + " / " + SelectedCurve.Condition + " · " + SelectedCurve.ReferencePath + " ↔ " + SelectedCurve.SignalPath + "\n" + SelectedCurve.Summary;
        public string Summary => Result == null ? DistributedComparisonEngine.Method : Result.Method + "\n" + string.Join("  |  ",Result.Curves.Select(c => c.Pair + " " + c.Condition + " 중앙 ZNCC=" + c.MedianZncc.ToString("F3") + " · 단일쌍 통과 " + c.Accepted + "/" + c.Points.Count));
        public string FirstPairName => Result?.PairOnly == true ? "Ref / Signal" : "상온 반복";
        public bool ShowSecondPair => Result?.PairOnly != true;
        public int FirstPairColumnSpan => Result?.PairOnly == true ? 2 : 1;
        public string Title => Result?.PairOnly == true ? "Ref·Signal · B/C 파장 이동 / ZNCC" : "분포 파장 이동 · 8쪽 비교 보기";
        public string RunLabel => Result?.PairOnly == true ? "Ref·Signal B/C 다시 실행" : "원본 처리 → 네 그래프";
        public string ReferenceLabel => Result?.PairOnly == true ? "기준 Ref" : "상온 기준 Ref";
        public string SignalLabel => Result?.PairOnly == true ? "측정 Signal" : "가열 Signal";
        public RelayCommand BrowseReferenceCommand { get; }
        public RelayCommand BrowseRepeatCommand { get; }
        public RelayCommand BrowseHeatedCommand { get; }
        public RelayCommand RunCommand { get; }
        public RelayCommand LoadCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand OpenResultsCommand { get; }
        public DistributedComparisonViewModel(Func<bool> idle)
        {
            this.idle=idle;
            BrowseReferenceCommand=new RelayCommand(()=>Browse(p=>ReferencePath=p),()=>!IsBusy);
            BrowseRepeatCommand=new RelayCommand(()=>Browse(p=>RepeatPath=p),()=>!IsBusy);
            BrowseHeatedCommand=new RelayCommand(()=>Browse(p=>HeatedPath=p),()=>!IsBusy);
            RunCommand=new RelayCommand(()=>{_ = RunAsync();},CanRun);
            CancelCommand=new RelayCommand(()=>cancellation?.Cancel(),()=>IsBusy);
            LoadCommand=new RelayCommand(()=>{ var d=new OpenFileDialog { Filter="처리 결과 (comparison.json)|comparison.json",Title="저장된 분포 비교 결과" };
                if(d.ShowDialog()==true) { try { Load(d.FileName); } catch(Exception ex) { Status="불러오기 실패: "+ex.Message; } } },()=>!IsBusy);
            OpenResultsCommand=new RelayCommand(()=>{try {System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ResultDirectory){UseShellExecute=true});}catch(Exception ex){Status=ex.Message;}},()=>!IsBusy&&Directory.Exists(ResultDirectory));
        }
        private void Browse(Action<string> assign) { var d=new OpenFileDialog { Filter="4채널 원본 Tek CSV|*.csv" };if(d.ShowDialog()==true)assign(d.FileName); }
        private bool CanRun()=>!IsBusy&&idle()&&File.Exists(ReferencePath)&&(Result?.PairOnly == true || File.Exists(RepeatPath))&&File.Exists(HeatedPath);
        public Task RunAsync() { if(!CanRun())return task; task=RunCoreAsync(Result?.PairOnly == true);return task; }
        internal Task RunForUnifiedAsync()
        {
            if (IsBusy) throw new InvalidOperationException("ZNCC가 실행 중입니다.");
            task = RunCoreAsync(true); return task;
        }
        internal void ClearResults() { Result = null; SelectedCurve = null; ResultDirectory = null; RunStatus = "PENDING"; OnPropertyChanged(nameof(ResultDirectory)); OnPropertyChanged(nameof(Curves)); OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(FirstPairName)); OnPropertyChanged(nameof(ShowSecondPair)); OnPropertyChanged(nameof(FirstPairColumnSpan)); }
        private async Task RunCoreAsync(bool pairOnly = false)
        {
            IsBusy=true;RunStatus="RUNNING";cancellation=new CancellationTokenSource();
            Result=null;SelectedCurve=null;OnPropertyChanged(nameof(Curves));OnPropertyChanged(nameof(Summary));
            ResultDirectory=Path.Combine(ResultRoot,DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff")+"_distributed_"+Guid.NewGuid().ToString("N").Substring(0,6));
            OnPropertyChanged(nameof(ResultDirectory));
            var r=ReferencePath;var s=RepeatPath;var h=HeatedPath;
            try {
                var c=OFDR_Signal_Core.Services.JsonContractSerializer.Clone(Config);
                var progress=new Progress<string>(p=>Status=p);
                var loaded=await Task.Run(()=>pairOnly ? new DistributedComparisonEngine().RunReferenceSignal(r,h,c,ResultDirectory,cancellation.Token,progress)
                    : new DistributedComparisonEngine().Run(r,s,h,c,ResultDirectory,cancellation.Token,progress));
                Apply(loaded); RunStatus="COMPLETED";Status=(pairOnly ? "Ref·Signal B/C 처리 완료" : "네 그래프 처리 완료") + " · 각 비교 1쌍 · 원점 미등록 · 결과 저장: "+ResultDirectory;
            }
            catch(OperationCanceledException) {RunStatus="CANCELED";Status="처리를 취소했습니다. 실행 기록은 결과 폴더에 보존했습니다.";}
            catch(Exception ex) {RunStatus="FAILED";Status="처리 실패: "+ex.Message;}
            finally {cancellation.Dispose();cancellation=null;IsBusy=false;}
        }
        public void Load(string file)
        {
            if(IsBusy)throw new InvalidOperationException("처리 종료 후 불러오세요.");
            var loaded=DistributedComparisonEngine.Load(file);Apply(loaded);
            ResultDirectory=Directory.Exists(file)?Path.GetFullPath(file):Path.GetDirectoryName(Path.GetFullPath(file));
            OnPropertyChanged(nameof(ResultDirectory));RunStatus="COMPLETED";Status="저장된 처리 데이터 표시 완료 · 재계산 없음 · "+ResultDirectory;Refresh();
        }
        private void Apply(DistributedComparisonResult loaded)
        {
            Config=OFDR_Signal_Core.Services.JsonContractSerializer.Clone(loaded.Config);OnPropertyChanged(nameof(Config));
            var room=loaded.Curves.First(c=>c.Pair==(loaded.PairOnly ? "Ref / Signal" : "상온 반복")&&c.Condition=="B");
            var heat=loaded.PairOnly ? room : loaded.Curves.First(c=>c.Pair=="가열 비교"&&c.Condition=="B");
            ReferencePath=room.ReferencePath;RepeatPath=loaded.PairOnly ? "" : room.SignalPath;HeatedPath=heat.SignalPath;
            Result=loaded;OnPropertyChanged(nameof(Curves));SelectedCurve=loaded.Curves[0];OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(FirstPairName)); OnPropertyChanged(nameof(ShowSecondPair)); OnPropertyChanged(nameof(FirstPairColumnSpan));
            OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(RunLabel)); OnPropertyChanged(nameof(ReferenceLabel)); OnPropertyChanged(nameof(SignalLabel));
        }
        public void Refresh(){OnPropertyChanged(nameof(ControlsEnabled));RunCommand?.RaiseCanExecuteChanged();CancelCommand?.RaiseCanExecuteChanged();LoadCommand?.RaiseCanExecuteChanged();BrowseReferenceCommand?.RaiseCanExecuteChanged();BrowseRepeatCommand?.RaiseCanExecuteChanged();BrowseHeatedCommand?.RaiseCanExecuteChanged();OpenResultsCommand?.RaiseCanExecuteChanged();}
        public async Task CancelAndWaitAsync(){cancellation?.Cancel();await task;}
        public void Dispose(){cancellation?.Cancel();}
        private static string FindRoot(){var p=new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);while(p!=null){if(Directory.Exists(Path.Combine(p.FullName,"harness")))return Path.Combine(p.FullName,"harness","results");p=p.Parent;}return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"results");}
    }
}
