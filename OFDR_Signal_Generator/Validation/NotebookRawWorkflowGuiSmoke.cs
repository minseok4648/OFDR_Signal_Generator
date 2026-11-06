using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    internal static class NotebookRawWorkflowGuiSmoke
    {
        internal static async Task RunAsync(MainWindow window, OfdrWorkspaceViewModel workspace)
        {
            var args = Environment.GetCommandLineArgs(); var flag = Array.IndexOf(args, "--notebook-raw-workflow-smoke");
            var input = Path.GetFullPath(args[flag + 1]); var golden = Path.GetFullPath(args[flag + 2]); var root = Path.GetFullPath(args[flag + 3]);
            var directory = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_notebook_raw_workflow_gui");
            Directory.CreateDirectory(Path.Combine(directory, "artifacts"));
            var metrics = new Smoke.SmokeMetrics { Status = "FAILED", Checks = new Dictionary<string, bool>(), BindingErrors = Smoke.BindingErrors };
            var watch = Stopwatch.StartNew(); var evidence = new Dictionary<string, double>();
            var prep = workspace.ModeHopPreview.SweepPreparation;
            JsonContractSerializer.Write(Path.Combine(directory, "config.json"), new Dictionary<string, string>
            { ["raw_directory"] = input, ["golden_profiles"] = golden, ["workflow"] = "Load raw -> review sweep -> 17M crop -> analyze -> confirm Ref/Signal -> Apply -> normal RunProcessingAsync -> record live result", ["stored_result_loader_used"] = "false" });
            try
            {
                window.Show(); window.Width = 1850; window.Height = 1000;
                window.Title = "OFDR · 261106 Raw → 역할 확정 → 전체 처리";
                await Smoke.Settle(window);
                Smoke.Check(metrics, "x64_native_window", Environment.Is64BitProcess && window.IsVisible && new WindowInteropHelper(window).Handle != IntPtr.Zero);
                prep.NotebookPresetCommand.Execute(null);
                Smoke.Check(metrics, "preset_preserves_raw_and_sets_diagnostic_mode", workspace.NotebookReproduction && workspace.AllowFailedPhaseQcForDiagnostics && !workspace.UsePhysicalAxes && !prep.UseModeHopExclusion && prep.UseFirstRisingEdgeToRecordEnd);
                await Prepare(prep, "Ref", Path.Combine(input, "tek0002ALL.csv"));
                Smoke.Check(metrics, "reference_confirmed_17M_raw", prep.PreparedReference?.SampleCount == 17000000 && prep.PreparedReference.OriginalIndices[0] == 167008 && prep.PreparedReference.Metadata.ModeHopExclusionBypassed);
                var sources = new[] { "tek0003ALL.csv", "tek0004ALL.csv", "tek0005ALL.csv" };
                for (var i = 0; i < sources.Length; i++)
                {
                    var label = new[] { "60C", "80C", "100C" }[i];
                    await Prepare(prep, "Signal", Path.Combine(input, sources[i]));
                    var expectedStart = i == 2 ? 167007 : 167008;
                    Smoke.Check(metrics, label + "_confirmed_exact_raw_crop", prep.PreparedSignal?.SampleCount == 17000000 && prep.PreparedSignal.OriginalIndices[0] == expectedStart && prep.PreparedSignal.OriginalIndices.Last() == expectedStart + 16999999 && prep.PreparedSignal.Metadata.ExcludedRanges.Count == 0);
                    Smoke.Check(metrics, label + "_apply_command_enabled", prep.ApplyCommand.CanExecute(null));
                    prep.ApplyCommand.Execute(null);
                    Smoke.Check(metrics, label + "_existing_full_run_command_enabled", workspace.RunProcessingCommand.CanExecute(null));
                    await workspace.RunProcessingAsync(); await Smoke.Settle(window);
                    var result = workspace.ProcessingResult;
                    Smoke.Check(metrics, label + "_full_raw_processing_completed", result?.NotebookProfile != null && result.Config.NotebookReproduction && !workspace.IsBusy && result.SampleCount == 524288);
                    var expected = File.ReadLines(Path.Combine(golden, "measurement_" + i + "_profile.csv")).Skip(1)
                        .Select(line => double.Parse(line.Split(',')[2], CultureInfo.InvariantCulture)).ToArray();
                    var actual = result.NotebookProfile.Shifts;
                    var maxError = actual.Zip(expected, (a, b) => double.IsNaN(a) && double.IsNaN(b) ? 0 : Math.Abs(a - b)).Max();
                    evidence[label + "_max_lag_error_samples"] = maxError;
                    Smoke.Check(metrics, label + "_all_positions_match_python_golden", actual.Length == expected.Length && actual.Zip(expected, (a, b) => double.IsNaN(a) == double.IsNaN(b)).All(x => x) && maxError < 1e-7);
                    Smoke.Check(metrics, label + "_stage_previews_bounded", result.ReferenceRaw.Values.Length <= 10000 && result.KDiagnostics.UnwrappedPhase.Values.Length <= 10000 && result.SignalSpectrum.Values.Length <= 10000 && result.SelectedSegment.Correlation.Values.Length <= 10000);
                    workspace.SelectedSegmentIndex = result.SegmentStarts.Length / 2; workspace.InspectSegmentCommand.Execute(null);
                    Smoke.Check(metrics, label + "_segment_inspector_matches_profile", Math.Abs(workspace.SelectedSegment.PeakLagBins - result.SegmentPeakLags[workspace.SelectedSegmentIndex]) < 1e-7);
                    workspace.FinalComparison.RecordName = label;
                    Smoke.Check(metrics, label + "_live_record_command_enabled", workspace.FinalComparison.RecordCommand.CanExecute(null));
                    workspace.FinalComparison.RecordCommand.Execute(null);
                    JsonContractSerializer.Write(Path.Combine(directory, label + "_preparation.json"), prep.PreparedSignal.Metadata);
                }
                var tabs = Smoke.Find<TabControl>(window, "OfdrResultTabs"); tabs.SelectedIndex = 2; await Smoke.Settle(window);
                var plot = Smoke.Find<StrainComparisonPlot>(window, "FinalComparisonPlot");
                Smoke.Check(metrics, "four_live_curves_rendered", plot.RenderedCurveCount == 4 && workspace.FinalComparison.Entries.Select(e => e.Name).SequenceEqual(new[] { "Ref", "60C", "80C", "100C" }));
                Smoke.Check(metrics, "reference_live_self_lag_zero", workspace.FinalComparison.Entries[0].YValues.Where(v => !double.IsNaN(v)).All(v => Math.Abs(v) < 1e-9));
                await Task.Delay(1000); await Smoke.Settle(window);
                Smoke.Check(metrics, "main_window_and_runtime_bindings_alive", window.IsVisible && ReferenceEquals(window.DataContext, workspace) && Smoke.BindingErrors.Count == 0);
                Smoke.Snapshot(window, directory, "raw_workflow_final_comparison.png");
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(plot.ActualWidth), (int)Math.Ceiling(plot.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(plot), null, new Rect(0, 0, plot.ActualWidth, plot.ActualHeight));
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(directory, "artifacts", "raw_workflow_chart.png"))) encoder.Save(stream);
                metrics.Status = "COMPLETED";
            }
            catch (Exception ex)
            {
                metrics.Error = ex + "\nPreparation: " + prep.Status + "\nProcessing: " + workspace.ProgressText;
                Smoke.Snapshot(window, directory, "failure.png");
            }
            finally
            {
                metrics.ElapsedSeconds = watch.Elapsed.TotalSeconds; metrics.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
                JsonContractSerializer.Write(Path.Combine(directory, "metrics.json"), metrics); JsonContractSerializer.Write(Path.Combine(directory, "numeric_comparison.json"), evidence);
                File.WriteAllText(Path.Combine(directory, "summary.md"), "# " + metrics.Status + "\n\nReal raw 20M/channel recordings were read through the existing sweep view model; selected 17M Ref/Signal crops were diagnosed, confirmed and applied. All three pairs ran through RunProcessingAsync without loading cached profiles. All profile positions were compared with the archived Python-validated golden. Actual WPF bindings and final curves were checked.\n\n" + (metrics.Error ?? "No error.") + "\n");
                if (Array.IndexOf(args, "--keep-open") >= 0) Application.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;
                else Application.Current.Shutdown(metrics.Status == "COMPLETED" ? 0 : 1);
            }
        }

        private static async Task Prepare(SweepPreparationViewModel vm, string role, string path)
        {
            await vm.LoadForRoleAsync(role, path);
            await vm.ReviewAsync();
            if (!vm.SetNotebookCropCommand.CanExecute(null)) throw new InvalidOperationException("Raw sweep review failed: " + vm.Status);
            vm.SetNotebookCropCommand.Execute(null);
            await vm.Quality.AnalyzeAsync();
            await vm.ConfirmAsync();
        }
    }
}
