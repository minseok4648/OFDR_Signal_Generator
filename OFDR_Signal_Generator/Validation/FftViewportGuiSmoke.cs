using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
using Smoke = OFDR_Signal_Generator.Validation.ModeHopGuiSmoke;

namespace OFDR_Signal_Generator.Validation
{
    internal static class FftViewportGuiSmoke
    {
        public static async Task RunAsync(MainWindow window, OfdrWorkspaceViewModel workspace)
        {
            var args = Environment.GetCommandLineArgs(); var flag = Array.IndexOf(args, "--fft-viewport-smoke");
            var directory = Path.GetFullPath(Path.Combine(args[flag + 1], DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_fft_viewport_gui"));
            Directory.CreateDirectory(Path.Combine(directory, "artifacts"));
            var metrics = new Smoke.SmokeMetrics { Status = "FAILED", Checks = new Dictionary<string, bool>(), BindingErrors = Smoke.BindingErrors };
            var watch = Stopwatch.StartNew();
            JsonContractSerializer.Write(Path.Combine(directory, "config.json"), new Dictionary<string, string>
            { ["reference"] = args[flag + 2], ["signal"] = args[flag + 3], ["scope"] = "Full CSV -> prepared CH3/CH4 -> native FFT -> WPF original-bin viewport", ["process"] = Environment.Is64BitProcess ? "x64" : "x86" });
            try
            {
                window.Show(); await Smoke.Settle(window);
                Smoke.Check(metrics, "x64_window_live", Environment.Is64BitProcess && window.IsVisible && new WindowInteropHelper(window).Handle != IntPtr.Zero);
                Smoke.Check(metrics, "native_fft", new FftEngine().NativeProviderAvailable);
                workspace.AllowFailedPhaseQcForDiagnostics = true;
                var preparation = workspace.ModeHopPreview.SweepPreparation;
                foreach (var role in new[] { "Ref", "Signal" })
                {
                    await preparation.LoadForRoleAsync(role, args[flag + (role == "Ref" ? 2 : 3)]);
                    preparation.UseWholeRecording = true;
                    await preparation.ReviewAsync(); await preparation.Quality.AnalyzeAsync(); await preparation.ConfirmAsync();
                    Smoke.Check(metrics, role + "_confirmed", role == "Ref" ? preparation.PreparedReference != null : preparation.PreparedSignal != null);
                }
                JsonContractSerializer.Write(Path.Combine(directory, "dataset_manifest.json"), new[] { preparation.PreparedReference.Metadata, preparation.PreparedSignal.Metadata });
                Smoke.Check(metrics, "apply_command_ready", preparation.ApplyCommand.CanExecute(null));
                preparation.ApplyCommand.Execute(null);
                workspace.DocumentPhasePresetCommand.Execute(null); workspace.UsePhysicalAxes = true;
                workspace.CenterWavelengthNm = 1550;
                workspace.Stages.Document.MaximumShiftPm = preparation.PreparedReference.SampleCount > 1000000 ? 40 : 2;
                workspace.Stages.Document.PeakExclusionPm = preparation.PreparedReference.SampleCount > 1000000 ? 8 : .5;
                await workspace.RunProcessingAsync(); await Smoke.Settle(window);
                Smoke.Check(metrics, "processing_completed", workspace.ProcessingResult != null && workspace.FftPlotSource != null);
                JsonContractSerializer.Write(Path.Combine(directory, "processing_config.json"), workspace.ProcessingResult.Config);
                JsonContractSerializer.Write(Path.Combine(directory, "processing_metrics.json"), workspace.ProcessingResult);
                var original = workspace.ProcessingResult;
                var signalBefore = original.SignalSpectrumValues.ToArray();
                var referenceBefore = original.ReferenceSpectrumValues.ToArray();
                var shiftsBefore = original.SegmentPeakLags.ToArray();
                window.WorkspaceTabs.SelectedIndex = 1; await Smoke.Settle(window);
                Smoke.Find<TabControl>(window, "OfdrResultTabs").SelectedIndex = 1; await Smoke.Settle(window);
                var plot = Smoke.Find<SignalPlot>(window, "DistanceFftPlot");
                plot.BringIntoView(); await Smoke.Settle(window);
                Smoke.Check(metrics, "fft_source_binding_active", BindingOperations.GetBindingExpression(plot, SignalPlot.FftSourceProperty)?.Status == BindingStatus.Active
                    && ReferenceEquals(plot.FftSource, workspace.FftPlotSource));
                Smoke.Check(metrics, "overview_bounded", plot.DisplayedFft != null && plot.DisplayedFft.Bins.Length <= 10000 && plot.DisplayedFft.IsReduced);
                var startBox = (TextBox)plot.FindName("FftRangeStart"); var endBox = (TextBox)plot.FindName("FftRangeEnd");
                startBox.Text = "16.5"; endBox.Text = "18.8";
                Smoke.Find<Button>(plot, "ApplyFftRange").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Smoke.Settle(window);
                var detail = plot.DisplayedFft;
                Smoke.Check(metrics, "numeric_range_original_bins", detail != null && !detail.IsReduced && detail.Bins.Length > 2
                    && detail.Bins.Zip(detail.Bins.Skip(1), (a, b) => b == a + 1).All(x => x));
                Smoke.Check(metrics, "range_covers_requested_distance", plot.FftSource.XAt(detail.Bins.First()) <= 16.5 && plot.FftSource.XAt(detail.Bins.Last()) >= 18.8);
                for (var i = 0; i < detail.Bins.Length; i++)
                {
                    if (detail.Signal[i] != plot.FftSource.ValueAt(detail.Bins[i]) || detail.Reference[i] != plot.FftSource.ValueAt(detail.Bins[i], true))
                        throw new InvalidOperationException("Viewport does not match original FFT.");
                }
                Smoke.Check(metrics, "values_match_original_fft", true);
                Smoke.Snapshot(window, directory, "distance_fft_original_bins.png");
                var bounds = plot.ZoomBounds; var area = plot.InteractionArea;
                Smoke.Check(metrics, "wheel_zoom", plot.ZoomAt(new Point(area.X + area.Width / 2, area.Y + area.Height / 2), 120));
                Smoke.Check(metrics, "wheel_changes_original_bin_range", plot.ZoomBounds.Width < bounds.Width);
                plot.SetFftRange(16.5, 18.8); area = plot.InteractionArea;
                Smoke.Check(metrics, "drag_zoom", plot.ZoomSelection(new Point(area.X + area.Width * .1, area.Y + area.Height * .1), new Point(area.X + area.Width * .9, area.Y + area.Height * .9)));
                var validBounds = plot.ZoomBounds;
                startBox.Text = "NaN"; endBox.Text = "18.8";
                Smoke.Find<Button>(plot, "ApplyFftRange").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Smoke.Check(metrics, "invalid_range_preserves_view", plot.ZoomBounds == validBounds);
                plot.ResetZoom(); await Smoke.Settle(window);
                Smoke.Check(metrics, "reset_restores_full_original_range", plot.ZoomBounds.X == 0 && plot.ZoomBounds.Width == plot.FftSource.Count - 1);
                workspace.SelectedLargeGraph = "3. FFT 거리 데이터 (K 보정 후)";
                var large = Smoke.Find<SignalPlot>(window, "LargeGraphPlot"); large.BringIntoView(); await Smoke.Settle(window);
                Smoke.Check(metrics, "large_graph_source_binding", ReferenceEquals(large.FftSource, workspace.FftPlotSource));
                large.SetFftRange(16.5, 18.8); await Smoke.Settle(window);
                Smoke.Check(metrics, "large_graph_full_bins", large.DisplayedFft != null && !large.DisplayedFft.IsReduced);
                Smoke.Snapshot(window, directory, "large_fft_original_bins.png");
                workspace.SelectedLargeGraph = "1. Raw 데이터"; await Smoke.Settle(window);
                Smoke.Check(metrics, "non_fft_graph_restored", large.FftSource == null && large.DisplayedFft == null);
                workspace.SelectedLargeGraph = "3. FFT 거리 데이터 (K 보정 후)"; await Smoke.Settle(window);
                Smoke.Check(metrics, "source_restored_after_graph_switch", large.FftSource != null && large.DisplayedFft.IsReduced);
                Smoke.Check(metrics, "spectra_and_correlation_unchanged", signalBefore.SequenceEqual(original.SignalSpectrumValues)
                    && referenceBefore.SequenceEqual(original.ReferenceSpectrumValues) && shiftsBefore.SequenceEqual(original.SegmentPeakLags));
                Smoke.Check(metrics, "no_runtime_binding_errors", Smoke.BindingErrors.Count == 0);
                Smoke.Check(metrics, "window_stays_connected", window.IsVisible && ReferenceEquals(window.DataContext, workspace));
                metrics.Status = "COMPLETED";
            }
            catch (Exception ex) { metrics.Error = ex.ToString(); }
            finally
            {
                metrics.ElapsedSeconds = watch.Elapsed.TotalSeconds; metrics.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
                JsonContractSerializer.Write(Path.Combine(directory, "metrics.json"), metrics);
                File.WriteAllText(Path.Combine(directory, "summary.md"), "# FFT viewport GUI validation\n\nStatus: " + metrics.Status
                    + "\n\nActual x64 WPF startup, full CSV input workflow, native FFT, original-bin numeric/wheel/drag zoom, graph switching and binding checks. Spectra and correlation arrays compared before/after zoom. Scientific validity is recorded separately in processing metrics; this validates display behavior, not absolute calibration.\n\n" + metrics.Error);
                Application.Current.Shutdown(metrics.Status == "COMPLETED" ? 0 : 1);
            }
        }
    }
}
