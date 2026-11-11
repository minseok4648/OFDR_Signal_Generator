using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;
using OFDR_Signal_Generator.Controls;
using OFDR_Signal_Generator.ViewModels;
using Smoke=OFDR_Signal_Generator.Validation.ModeHopGuiSmoke;

namespace OFDR_Signal_Generator.Validation
{
    internal static class DistributedComparisonGuiSmoke
    {
        public static async Task RunAsync(MainWindow window,OfdrWorkspaceViewModel workspace)
        {
            var args=Environment.GetCommandLineArgs();int flag=Array.IndexOf(args,"--distributed-smoke");
            var directory=Path.GetFullPath(Path.Combine(args[flag+1],DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff")+"_distributed_gui"));
            Directory.CreateDirectory(Path.Combine(directory,"artifacts"));
            var metrics=new Smoke.SmokeMetrics{Status="FAILED",Checks=new Dictionary<string,bool>(),BindingErrors=Smoke.BindingErrors};
            JsonContractSerializer.Write(Path.Combine(directory,"config.json"),new Dictionary<string,string>{["scope"]="x64 WPF full CSV B/C room/heat; bounded plots, bindings and saved-result reload",["reference"]=args[flag+2],["repeat"]=args[flag+3],["heated"]=args[flag+4]});
            var watch=Stopwatch.StartNew();
            try {
                window.Show();window.WorkspaceTabs.SelectedIndex=3;await Smoke.Settle(window);
                Smoke.Check(metrics,"x64_main_window",Environment.Is64BitProcess&&window.IsVisible&&new WindowInteropHelper(window).Handle!=IntPtr.Zero);
                var vm=workspace.DistributedComparison;vm.ResultRoot=Path.Combine(directory,"processing");
                vm.ReferencePath=args[flag+2];vm.RepeatPath=args[flag+3];vm.HeatedPath=args[flag+4];
                Smoke.Check(metrics,"run_available",vm.RunCommand.CanExecute(null));
                var loadFlag=Array.IndexOf(args,"--load-result");
                if(loadFlag>=0) vm.Load(args[loadFlag+1]);
                else {
                    var run=vm.RunAsync();Smoke.Check(metrics,"busy_gate",vm.IsBusy&&!workspace.LegacyControlsEnabled&&!vm.RunCommand.CanExecute(null));
                    await run;
                }
                await Smoke.Settle(window);
                Smoke.Check(metrics,"full_run_completed",vm.RunStatus=="COMPLETED"&&vm.Result?.Curves.Count==4);
                Smoke.Check(metrics,"actual_full_recordings",vm.Result.Sources.All(s=>s.Sweep.EndIndexExclusive>=5000000));
                Smoke.Check(metrics,"independent_room_capture",vm.Result.Sources[0].SourceSha256!=vm.Result.Sources[1].SourceSha256);
                Smoke.Check(metrics,"finite_candidates",vm.Result.Curves.All(c=>c.Points.Any(p=>!double.IsNaN(p.CandidateShiftPm))));
                Smoke.Check(metrics,"bounded_profiles",vm.Result.Curves.All(c=>c.Points.Count<=10000));
                foreach(var id in new[]{"RoomShiftPlot","HeatedShiftPlot","RoomZnccPlot","HeatedZnccPlot"}) {
                    var plot=Smoke.Find<DistributedProfilePlot>(window,id);
                    Smoke.Check(metrics,id+"_binding",ReferenceEquals(plot.Result,vm.Result)&&BindingOperations.GetBindingExpression(plot,DistributedProfilePlot.ResultProperty)?.Status==BindingStatus.Active);
                    Smoke.Check(metrics,id+"_visible",plot.IsVisible&&plot.ActualHeight>=200&&plot.ActualWidth>200);
                }
                Smoke.Snapshot(window,directory,"page8_actual_data.png");
                var expected=vm.Result.Curves[0].Points.Select(p=>p.CandidateShiftPm).ToArray();var saved=Path.Combine(vm.ResultDirectory,"comparison.json");
                vm.Load(saved);await Smoke.Settle(window);
                Smoke.Check(metrics,"reloaded_inputs_and_config",vm.ReferencePath==vm.Result.Curves[0].ReferencePath&&vm.RepeatPath==vm.Result.Curves[0].SignalPath&&vm.Config.StageComparison.Document.EndMeters==vm.Result.Config.StageComparison.Document.EndMeters);
                Smoke.Check(metrics,"reload_preserves_values",expected.SequenceEqual(vm.Result.Curves[0].Points.Select(p=>p.CandidateShiftPm)));
                vm.SelectedCurve=vm.Result.Curves[3];
                var view=Smoke.Descendants(window).OfType<DistributedComparisonView>().Single();
                Smoke.Descendants(view).OfType<Expander>().Last().IsExpanded=true;await Smoke.Settle(window);
                Smoke.Check(metrics,"data_table_bound",Smoke.Find<DataGrid>(window,"DistributedDataTable").Items.Count==vm.SelectedCurve.Points.Count);
                Smoke.Find<DataGrid>(window,"DistributedDataTable").BringIntoView(); await Smoke.Settle(window);
                Smoke.Snapshot(window,directory,"page8_quality_table.png");
                Smoke.Descendants(view).OfType<Expander>().Last().IsExpanded=false;
                window.WorkspaceTabs.SelectedIndex=0;await Smoke.Settle(window);window.WorkspaceTabs.SelectedIndex=3;await Smoke.Settle(window);
                Smoke.Check(metrics,"retained_after_tab_change",ReferenceEquals(Smoke.Find<DistributedProfilePlot>(window,"HeatedShiftPlot").Result,vm.Result));
                Smoke.Check(metrics,"runtime_bindings_clean",Smoke.BindingErrors.Count==0);
                Smoke.Check(metrics,"live_datacontext",window.IsVisible&&ReferenceEquals(window.DataContext,workspace));
                metrics.Status="COMPLETED";
            }
            catch(Exception ex){metrics.Error=ex.ToString();}
            finally {
                metrics.ElapsedSeconds=watch.Elapsed.TotalSeconds;metrics.PeakWorkingSetBytes=Process.GetCurrentProcess().PeakWorkingSet64;
                JsonContractSerializer.Write(Path.Combine(directory,"metrics.json"),metrics);
                File.WriteAllText(Path.Combine(directory,"summary.md"),"# WPF distributed comparison validation\n\nStatus: "+metrics.Status+"\n\n"+DistributedComparisonEngine.Method+"\n\n"+metrics.Error);
                Application.Current.Shutdown(metrics.Status=="COMPLETED"?0:1);
            }
        }
    }
}
