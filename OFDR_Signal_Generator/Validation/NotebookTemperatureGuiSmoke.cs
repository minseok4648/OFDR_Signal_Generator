using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OFDR_Signal_Core.Services;
using OFDR_Signal_Generator.Controls;
using OFDR_Signal_Generator.ViewModels;
using Smoke = OFDR_Signal_Generator.Validation.ModeHopGuiSmoke;

namespace OFDR_Signal_Generator.Validation
{
    internal static class NotebookTemperatureGuiSmoke
    {
        internal static async Task RunAsync(MainWindow window, OfdrWorkspaceViewModel workspace)
        {
            var args = Environment.GetCommandLineArgs(); var flag = Array.IndexOf(args, "--notebook-temperature-smoke");
            var source = args[flag + 1]; var root = args[flag + 2];
            var directory = Path.GetFullPath(Path.Combine(root, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_notebook_temperature_gui"));
            Directory.CreateDirectory(Path.Combine(directory, "artifacts"));
            var metrics = new Smoke.SmokeMetrics { Status = "FAILED", Checks = new Dictionary<string, bool>(), BindingErrors = Smoke.BindingErrors };
            var clock = Stopwatch.StartNew();
            JsonContractSerializer.Write(Path.Combine(directory, "config.json"), new Dictionary<string, string>
            { ["source_result_directory"] = Path.GetFullPath(source), ["axes"] = "profile index / local-spectrum lag samples", ["reference"] = "C# native MKL self-correlation", ["renderer"] = "WPF DrawingContext" });
            try
            {
                window.Show(); window.Width = 1850; window.Height = 1000;
                window.Title = "OFDR 독립 채널 분석 도구 · 261106 Ref / 60 / 80 / 100";
                workspace.WorkspaceSelectedTab = 1; await Smoke.Settle(window);
                Smoke.Check(metrics, "x64_main_window_alive", Environment.Is64BitProcess && window.IsVisible && new WindowInteropHelper(window).Handle != IntPtr.Zero && ReferenceEquals(window.DataContext, workspace));
                var tabs = Smoke.Find<TabControl>(window, "OfdrResultTabs"); tabs.SelectedIndex = 2; await Smoke.Settle(window);
                var vm = workspace.FinalComparison;
                vm.NotebookResultDirectory = source;
                var button = Smoke.Find<Button>(window, "LoadNotebookTemperature");
                Smoke.Check(metrics, "load_command_runtime_binding", ReferenceEquals(button.Command, vm.LoadNotebookCommand) && button.IsEnabled);
                button.Command.Execute(null);
                await vm.LoadNotebookAsync(); await Smoke.Settle(window);
                Smoke.Check(metrics, "temperature_result_loaded", vm.Entries.Count == 4 && !vm.IsBusy);
                tabs.SelectedIndex = 2; await Smoke.Settle(window);
                var plot = Smoke.Find<StrainComparisonPlot>(window, "FinalComparisonPlot");
                Smoke.Check(metrics, "four_curves_rendered", vm.IsNotebookComparison && vm.Entries.Count == 4 && plot.RenderedCurveCount == 4 && ReferenceEquals(plot.Entries, vm.Entries));
                Smoke.Check(metrics, "labels_ref_60_80_100", vm.Entries.Select(e => e.Name).SequenceEqual(new[] { "Ref", "60C", "80C", "100C" }));
                Smoke.Check(metrics, "index_lag_axes_bound", plot.XAxisTitle.Contains("순번") && plot.YAxisTitle == "상관 lag (sample)" && !plot.YAxisTitle.Contains("strain"));
                Smoke.Check(metrics, "all_5243_positions_preserved", vm.Entries.All(e => e.XValues.Length == 5243 && e.YValues.Length == 5243 && e.XValues[0] == 0 && e.XValues.Last() == 5242));
                Smoke.Check(metrics, "nan_edges_preserved", vm.Entries.All(e => e.YValues.Count(double.IsNaN) == 82));
                Smoke.Check(metrics, "reference_self_lag_zero", vm.Entries[0].YValues.Where(v => !double.IsNaN(v)).All(v => Math.Abs(v) < 1e-9));
                var expected = new[] { -923.3022283843776, -2126.777335512877, -3136.279178515252 };
                for (var i = 0; i < 3; i++)
                {
                    var plateau = vm.Entries[i + 1].YValues.Skip(2600).Take(301).OrderBy(v => v).ToArray();
                    Smoke.Check(metrics, "original_plateau_" + vm.Entries[i + 1].Name, Math.Abs(plateau[150] - expected[i]) < 1e-7);
                }
                var boxes = Smoke.Descendants(window).OfType<CheckBox>().Where(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b).StartsWith("ComparisonDisplayEnable_")).ToArray();
                Smoke.Check(metrics, "four_bound_display_checkboxes", boxes.Length == 4 && boxes.All(b => b.IsChecked == true));
                boxes[3].SetCurrentValue(CheckBox.IsCheckedProperty, false); await Smoke.Settle(window);
                Smoke.Check(metrics, "checkbox_hides_only_100C", plot.RenderedCurveCount == 3 && !vm.Entries[3].DisplayEnabled && vm.Entries.Take(3).All(e => e.DisplayEnabled));
                boxes[3].SetCurrentValue(CheckBox.IsCheckedProperty, true); await Smoke.Settle(window);
                Smoke.Check(metrics, "checkbox_restores_100C", plot.RenderedCurveCount == 4 && vm.Entries[3].DisplayEnabled);
                Smoke.Check(metrics, "wheel_zoom", plot.ZoomAt(Smoke.InArea(plot.InteractionArea, .5, .5), 120));
                plot.ResetZoom();
                Smoke.Check(metrics, "drag_zoom", plot.ZoomSelection(Smoke.InArea(plot.InteractionArea, .2, .2), Smoke.InArea(plot.InteractionArea, .8, .8)));
                plot.ResetZoom(); vm.ComparisonModeIndex = 0; await Smoke.Settle(window);
                Smoke.Check(metrics, "strain_mode_keeps_separate_axes_and_records", vm.Entries.Count == 0 && plot.RenderedCurveCount == 0 && plot.XAxisTitle == "거리 (m)" && plot.YAxisTitle == "strain (με)");
                vm.ComparisonModeIndex = 1; await Smoke.Settle(window);
                Smoke.Check(metrics, "temperature_records_survive_mode_switch", vm.Entries.Count == 4 && plot.RenderedCurveCount == 4);
                await Task.Delay(1000); await Smoke.Settle(window);
                Smoke.Check(metrics, "main_window_stays_alive", window.IsVisible && !vm.IsBusy && plot.IsVisible);
                Smoke.Check(metrics, "runtime_binding_errors_zero", Smoke.BindingErrors.Count == 0);
                Smoke.Snapshot(window, directory, "final_comparison_window.png");
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(plot.ActualWidth), (int)Math.Ceiling(plot.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(plot);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(directory, "artifacts", "final_comparison_chart.png"))) encoder.Save(stream);
                metrics.Status = "COMPLETED";
            }
            catch (Exception ex)
            {
                metrics.Error = ex + "\nComparison status: " + workspace.FinalComparison.Status;
                Smoke.Snapshot(window, directory, "failure_window.png");
            }
            finally
            {
                metrics.ElapsedSeconds = clock.Elapsed.TotalSeconds; metrics.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
                JsonContractSerializer.Write(Path.Combine(directory, "metrics.json"), metrics);
                File.WriteAllText(Path.Combine(directory, "summary.md"), "# " + metrics.Status + "\n\nActual x64 WPF startup, DataContext, commands, four curves, NaN edges, reference self-correlation, plateau values, checkbox display, zoom, mode switching and runtime bindings checked.\n\nSource C# result: " + Path.GetFullPath(source) + "\n\nError: " + (metrics.Error ?? "none") + "\n");
                if (Array.IndexOf(args, "--keep-open") >= 0) Application.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;
                else Application.Current.Shutdown(metrics.Status == "COMPLETED" ? 0 : 1);
            }
        }
    }
}
