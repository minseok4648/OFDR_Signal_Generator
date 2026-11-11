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
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Services;
using OFDR_Signal_Generator.Controls;
using OFDR_Signal_Generator.ViewModels;
using Smoke = OFDR_Signal_Generator.Validation.ModeHopGuiSmoke;

namespace OFDR_Signal_Generator.Validation
{
    internal static class UnifiedAnalysisGuiSmoke
    {
        public static async Task RunAsync(MainWindow window, OfdrWorkspaceViewModel workspace)
        {
            var args=Environment.GetCommandLineArgs(); var flag=Array.IndexOf(args,"--unified-smoke");
            var directory=Path.GetFullPath(Path.Combine(args[flag+1],DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff")+"_unified_gui"));
            Directory.CreateDirectory(Path.Combine(directory,"artifacts"));
            var metrics=new Smoke.SmokeMetrics {Status="FAILED",Checks=new Dictionary<string,bool>(),BindingErrors=Smoke.BindingErrors};
            var watch=Stopwatch.StartNew();
            JsonContractSerializer.Write(Path.Combine(directory,"config.json"),new Dictionary<string,string>{["reference"]=args[flag+2],["signal"]=args[flag+3],["scope"]="Actual x64 WPF single command: full-record general + all-range reflectogram + reference-signal B/C ZNCC"});
            try {
                window.Show();await Smoke.Settle(window);
                Smoke.Check(metrics,"x64_window_live",Environment.Is64BitProcess&&window.IsVisible&&new WindowInteropHelper(window).Handle!=IntPtr.Zero);
                var vm=workspace.Unified;vm.ResultRoot=Path.Combine(directory,"processing");
                var reference=Smoke.Find<TextBox>(window,"UnifiedReferencePath");var signal=Smoke.Find<TextBox>(window,"UnifiedSignalPath");
                reference.Text=args[flag+2];reference.GetBindingExpression(TextBox.TextProperty).UpdateSource();
                signal.Text=args[flag+2];signal.GetBindingExpression(TextBox.TextProperty).UpdateSource();
                Smoke.Check(metrics,"self_comparison_rejected",!vm.RunCommand.CanExecute(null));
                signal.Text=args[flag+3];signal.GetBindingExpression(TextBox.TextProperty).UpdateSource();
                Smoke.Check(metrics,"two_files_enable_run",vm.RunCommand.CanExecute(null));
                var run=vm.RunAsync();
                Smoke.Check(metrics,"single_command_busy_gate",vm.IsBusy&&!vm.RunCommand.CanExecute(null)&&!workspace.LegacyControlsEnabled&&!workspace.Reflectogram.RunCommand.CanExecute(null)&&!workspace.DistributedComparison.RunCommand.CanExecute(null));
                if(Array.IndexOf(args,"--cancel-unified")>=0) {
                    vm.CancelCommand.Execute(null);await run;await Smoke.Settle(window);
                    Smoke.Check(metrics,"cancelled_workflow_logged",vm.RunStatus=="CANCELED"&&File.Exists(Path.Combine(vm.ResultDirectory,"metrics.json")));
                    Smoke.Check(metrics,"no_stale_results_after_cancel",!workspace.HasResult&&workspace.Reflectogram.ReferenceResult==null&&workspace.DistributedComparison.Result==null);
                    Smoke.Check(metrics,"cancellation_restores_controls",!vm.IsBusy&&workspace.LegacyControlsEnabled);
                } else {
                    await run;await Smoke.Settle(window);
                    Smoke.Check(metrics,"all_three_completed",vm.RunStatus=="COMPLETED"&&vm.StageSummary.Split(new[]{"완료"},StringSplitOptions.None).Length==4);
                    Smoke.Check(metrics,"existing_general_result",workspace.ProcessingResult!=null&&workspace.ProcessingResult.SelectedSegment!=null&&workspace.FftPlotSource!=null);
                    var generalDirectory=Directory.GetDirectories(Path.Combine(vm.ResultDirectory,"general")).Single();
                    var refMetadata=JsonContractSerializer.Read<PreparedSweepMetadata>(Path.Combine(generalDirectory,"ref_preparation.json"));
                    var sigMetadata=JsonContractSerializer.Read<PreparedSweepMetadata>(Path.Combine(generalDirectory,"signal_preparation.json"));
                    Smoke.Check(metrics,"full_inputs_not_shortened",refMetadata.Sweep.EndIndexExclusive>=5000000&&sigMetadata.Sweep.EndIndexExclusive>=5000000);
                    Smoke.Check(metrics,"diagnostic_labels_preserved",workspace.AllowFailedPhaseQcForDiagnostics&&workspace.ProcessingResult.IsDiagnosticAnalysis);
                    Smoke.Check(metrics,"reflectogram_both_inputs",workspace.Reflectogram.ReferenceResult!=null&&workspace.Reflectogram.SignalResult!=null);
                    var zncc=workspace.DistributedComparison.Result;
                    Smoke.Check(metrics,"real_pair_zncc_two_curves",zncc?.PairOnly==true&&zncc.Curves.Count==2&&zncc.Curves.Select(c=>c.Condition).OrderBy(x=>x).SequenceEqual(new[]{"B","C"}));
                    Smoke.Check(metrics,"same_pair_all_results",workspace.Reflectogram.ReferenceResult.SourcePath==Path.GetFullPath(args[flag+2])&&workspace.Reflectogram.SignalResult.SourcePath==Path.GetFullPath(args[flag+3])&&zncc.Curves.All(c=>c.ReferencePath==args[flag+2]&&c.SignalPath==args[flag+3]));
                    Smoke.Check(metrics,"zncc_profiles_bounded",zncc.Curves.All(c=>c.Points.Count>0&&c.Points.Count<=10000));
                    vm.ShowGeneralCommand.Execute(null);await Smoke.Settle(window);
                    Smoke.Find<TabControl>(window,"OfdrResultTabs").SelectedIndex=1;await Smoke.Settle(window);
                    var fft=Smoke.Find<SignalPlot>(window,"DistanceFftPlot");fft.BringIntoView();await Smoke.Settle(window);
                    Smoke.Check(metrics,"general_fft_binding",ReferenceEquals(fft.FftSource,workspace.FftPlotSource)&&fft.DisplayedFft!=null&&fft.DisplayedFft.Bins.Length<=10000);
                    Smoke.Snapshot(window,directory,"all_general_result.png");
                    vm.ShowReflectogramCommand.Execute(null);await Smoke.Settle(window);
                    var reflect=Smoke.Find<SignalPlot>(window,"ReflectogramRefPlot");reflect.BringIntoView();await Smoke.Settle(window);
                    Smoke.Check(metrics,"reflectogram_binding",ReferenceEquals(reflect.FftSource,workspace.Reflectogram.ReferencePlot)&&reflect.DisplayedFft!=null);
                    Smoke.Snapshot(window,directory,"all_reflectogram.png");
                    vm.ShowZnccCommand.Execute(null);await Smoke.Settle(window);
                    var profile=Smoke.Find<DistributedProfilePlot>(window,"RoomZnccPlot");profile.BringIntoView();await Smoke.Settle(window);
                    Smoke.Check(metrics,"zncc_pair_plot_binding",ReferenceEquals(profile.Result,zncc)&&profile.Pair=="Ref / Signal"&&profile.IsVisible);
                    Smoke.Check(metrics,"no_fake_room_or_heat_pair",!workspace.DistributedComparison.ShowSecondPair&&!Smoke.Find<DistributedProfilePlot>(window,"HeatedZnccPlot").IsVisible);
                    Smoke.Snapshot(window,directory,"all_zncc.png");
                    var saved=Path.Combine(workspace.DistributedComparison.ResultDirectory,"comparison.json");workspace.DistributedComparison.Load(saved);await Smoke.Settle(window);
                    Smoke.Check(metrics,"two_curve_result_reload",workspace.DistributedComparison.Result.PairOnly&&workspace.DistributedComparison.Result.Curves.Count==2);
                    Smoke.Check(metrics,"one_result_root",workspace.Reflectogram.ResultDirectory.StartsWith(vm.ResultDirectory)&&workspace.DistributedComparison.ResultDirectory.StartsWith(vm.ResultDirectory)&&Directory.GetFiles(Path.Combine(vm.ResultDirectory,"general"),"result.json",SearchOption.AllDirectories).Length==1);
                }
                Smoke.Check(metrics,"runtime_bindings_clean",Smoke.BindingErrors.Count==0);
                Smoke.Check(metrics,"window_and_context_alive",window.IsVisible&&ReferenceEquals(window.DataContext,workspace));
                metrics.Status="COMPLETED";
            } catch(Exception ex){metrics.Error=ex.ToString();}
            finally {
                metrics.ElapsedSeconds=watch.Elapsed.TotalSeconds;metrics.PeakWorkingSetBytes=Process.GetCurrentProcess().PeakWorkingSet64;
                JsonContractSerializer.Write(Path.Combine(directory,"metrics.json"),metrics);
                File.WriteAllText(Path.Combine(directory,"summary.md"),"# Unified workflow WPF validation\n\nStatus: "+metrics.Status+"\n\nReal two-file selection, one sequential run, three result families, independent capture provenance, bounded plots, cancellation/restore path when requested, pair-result reload and active runtime bindings. Scientific QC remains separately labelled.\n\n"+metrics.Error);
                Application.Current.Shutdown(metrics.Status=="COMPLETED"?0:1);
            }
        }
    }
}
