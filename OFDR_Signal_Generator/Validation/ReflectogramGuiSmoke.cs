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
using OFDR_Signal_Core.Services;
using OFDR_Signal_Generator.Controls;
using OFDR_Signal_Generator.ViewModels;
using Smoke = OFDR_Signal_Generator.Validation.ModeHopGuiSmoke;

namespace OFDR_Signal_Generator.Validation
{
    internal static class ReflectogramGuiSmoke
    {
        public static async Task RunAsync(MainWindow window, OfdrWorkspaceViewModel workspace)
        {
            var args = Environment.GetCommandLineArgs(); int flag = Array.IndexOf(args, "--reflectogram-smoke");
            var directory = Path.GetFullPath(Path.Combine(args[flag + 1], DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_reflectogram_gui"));
            Directory.CreateDirectory(Path.Combine(directory, "artifacts"));
            var metrics = new Smoke.SmokeMetrics { Status = "FAILED", Checks = new Dictionary<string, bool>(), BindingErrors = Smoke.BindingErrors };
            var watch = Stopwatch.StartNew();
            JsonContractSerializer.Write(Path.Combine(directory, "config.json"), new Dictionary<string, string> { ["reference"] = args[flag + 2], ["signal"] = args[flag + 3], ["scope"] = "Actual WPF x64 full CSV -> all continuous ranges -> B/C power mean -> bounded plot and zoom" });
            try {
                window.Show(); window.WorkspaceTabs.SelectedIndex = 2; await Smoke.Settle(window);
                Smoke.Check(metrics, "x64_main_window_live", Environment.Is64BitProcess && window.IsVisible && new WindowInteropHelper(window).Handle != IntPtr.Zero);
                var vm = workspace.Reflectogram; vm.ResultRoot = Path.Combine(directory, "processing");
                vm.ReferencePath = args[flag + 2]; vm.SignalPath = args[flag + 3];
                Smoke.Check(metrics, "run_command_enabled", vm.RunCommand.CanExecute(null));
                var run = vm.RunAsync();
                Smoke.Check(metrics, "busy_disables_other_workflow", vm.IsBusy && !workspace.LegacyControlsEnabled && !vm.RunCommand.CanExecute(null));
                await run; await Smoke.Settle(window);
                Smoke.Check(metrics, "full_processing_completed", vm.RunStatus == "COMPLETED" && vm.ReferenceResult != null && vm.SignalResult != null);
                Smoke.Check(metrics, "full_5m_or_larger_both_recordings", vm.ReferenceResult.SampleCount >= 5000000 && vm.SignalResult.SampleCount >= 5000000);
                Smoke.Check(metrics, "independent_phase_ranges", vm.ReferenceResult.Ranges.Count > 1 && vm.SignalResult.Ranges.Count > 1 && vm.ReferenceResult.Sha256 != vm.SignalResult.Sha256);
                Smoke.Check(metrics, "power_outputs_saved", File.Exists(Path.Combine(vm.ResultDirectory, "reference", "power_density.csv")) && File.Exists(Path.Combine(vm.ResultDirectory, "signal", "power_density.csv")));
                foreach (var id in new[] { "ReflectogramRefDetail", "ReflectogramSignalDetail" }) {
                    var detail = Smoke.Find<SignalPlot>(window, id);
                    Smoke.Check(metrics, id + "_automatic_peak_zoom", detail.DisplayedFft != null && !detail.DisplayedFft.IsReduced && detail.DisplayedFft.Bins.Length <= 803);
                }
                foreach (var id in new[] { "ReflectogramRefPlot", "ReflectogramSignalPlot" }) {
                    var plot = Smoke.Find<SignalPlot>(window, id); plot.BringIntoView(); await Smoke.Settle(window);
                    Smoke.Check(metrics, id + "_binding", BindingOperations.GetBindingExpression(plot, SignalPlot.FftSourceProperty)?.Status == BindingStatus.Active && plot.FftSource != null);
                    Smoke.Check(metrics, id + "_bounded_overview", plot.DisplayedFft != null && plot.DisplayedFft.IsReduced && plot.DisplayedFft.Bins.Length <= 10000);
                    Smoke.Snapshot(window, directory, id + "_overview.png");
                    ((TextBox)plot.FindName("FftRangeStart")).Text = "17.9";
                    ((TextBox)plot.FindName("FftRangeEnd")).Text = "18.4";
                    Smoke.Find<Button>(plot, "ApplyFftRange").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Smoke.Settle(window);
                    Smoke.Check(metrics, id + "_zoom_all_grid_points", !plot.DisplayedFft.IsReduced && plot.DisplayedFft.Bins.Length >= 501);
                    Smoke.Check(metrics, id + "_absolute_density_preserved", plot.DisplayedFft.Bins.Select((bin, i) => plot.DisplayedFft.Signal[i] == plot.FftSource.ValueAt(bin)).All(v => v));
                    Smoke.Snapshot(window, directory, id + "_peak.png"); plot.ResetZoom();
                }
                window.WorkspaceTabs.SelectedIndex = 0; await Smoke.Settle(window);
                window.WorkspaceTabs.SelectedIndex = 2; await Smoke.Settle(window);
                Smoke.Check(metrics, "results_survive_tab_switch", ReferenceEquals(Smoke.Find<SignalPlot>(window, "ReflectogramRefPlot").FftSource, vm.ReferencePlot));
                Smoke.Check(metrics, "no_runtime_binding_errors", Smoke.BindingErrors.Count == 0);
                Smoke.Check(metrics, "window_remains_connected", window.IsVisible && ReferenceEquals(window.DataContext, workspace));
                metrics.Status = "COMPLETED";
            }
            catch (Exception ex) { metrics.Error = ex.ToString(); }
            finally {
                metrics.ElapsedSeconds = watch.Elapsed.TotalSeconds; metrics.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
                JsonContractSerializer.Write(Path.Combine(directory, "metrics.json"), metrics);
                File.WriteAllText(Path.Combine(directory, "summary.md"), "# Reflectogram GUI full input validation\n\nStatus: " + metrics.Status + "\n\nActual x64 WPF startup, runtime binding, full input processing, power output export, overview and original grid zoom. Scientific phase QC remains separately reported in processing metrics.\n\n" + metrics.Error);
                Application.Current.Shutdown(metrics.Status == "COMPLETED" ? 0 : 1);
            }
        }
    }
}
