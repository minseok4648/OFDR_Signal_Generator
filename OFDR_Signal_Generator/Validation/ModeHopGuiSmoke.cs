using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Services;
using OFDR_Signal_Generator.Controls;
using OFDR_Signal_Generator.ViewModels;

namespace OFDR_Signal_Generator.Validation
{
    // Opt-in integration check in the real WPF process. It does not automate the Windows desktop.
    internal static class ModeHopGuiSmoke
    {
        private static readonly BindingTrace Trace = new BindingTrace();
        public static void AttachBindingTrace()
        {
            PresentationTraceSources.DataBindingSource.Listeners.Add(Trace);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        }

        public static async Task RunAsync(MainWindow window, OfdrWorkspaceViewModel workspace)
        {
            var args = Environment.GetCommandLineArgs();
            var flag = Array.IndexOf(args, "--mode-hop-smoke");
            var path = flag + 1 < args.Length ? args[flag + 1] : "";
            var root = flag + 2 < args.Length ? args[flag + 2] : Path.Combine("harness", "results");
            var directory = Path.GetFullPath(Path.Combine(root, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_mode_hop_gui_smoke"));
            Directory.CreateDirectory(Path.Combine(directory, "artifacts"));
            var metrics = new SmokeMetrics { Status = "FAILED", Checks = new Dictionary<string, bool>(), BindingErrors = Trace.Errors };
            var watch = Stopwatch.StartNew();
            JsonContractSerializer.Write(Path.Combine(directory, "config.json"), new ModeHopRunConfig { SourcePath = path, Settings = new ModeHopSettings() });
            var vm = workspace.ModeHopPreview;
            try
            {
                window.Show();
                await Settle(window);
                Check(metrics, "native_window", new WindowInteropHelper(window).Handle != IntPtr.Zero && window.IsVisible);
                var view = Descendants(window).OfType<ModeHopPreviewView>().Single();
                Check(metrics, "preview_datacontext", ReferenceEquals(view.DataContext, vm));
                Check(metrics, "source_binding_active", BindingOperations.GetBindingExpression(Find<TextBox>(view, "ModeHopSource"), TextBox.TextProperty)?.Status == BindingStatus.Active);
                Check(metrics, "progress_one_way", BindingOperations.GetBinding(Descendants(view).OfType<ProgressBar>().Single(), RangeBase.ValueProperty)?.Mode == BindingMode.OneWay);
                await vm.LoadAsync(path);
                await Settle(window);
                var run = JsonContractSerializer.Read<ModeHopRunMetrics>(Path.Combine(vm.ResultDirectory, "metrics.json"));
                if (run.Status != "COMPLETED") throw new InvalidOperationException("CSV load failed: " + run.Error);
                Check(metrics, "full_5m_golden", run.Status == "COMPLETED" && run.SampleCount == 5000000 && run.RegionCount == 755 && run.ExcludedSamples == 471902 && run.Sha256 == "c8837dc614ae7cf3702e51c1fe7de5307be79063a8ee6eeb09501e1425f25a34");
                Check(metrics, "preview_limit", vm.Viewport.Auxiliary.Values.Length <= 10000 && vm.Viewport.Trigger.Values.Length <= 10000);
                Check(metrics, "estimated_1200ms_grid", vm.Viewport.SweepBoundaries.Count == 4 && Math.Abs(vm.Viewport.SweepBoundaries[1].Seconds - 1.2) < 1e-12);
                var plot = Descendants(view).OfType<ModeHopOverlayPlot>().Single();
                Check(metrics, "overlay_bound", ReferenceEquals(plot.Viewport, vm.Viewport));
                Snapshot(window, directory, "full.png");
                var overlayArea = plot.InteractionArea;
                var overlayCenter = InArea(overlayArea, 0.5, 0.5);
                plot.ZoomAt(overlayCenter, 120); await NavigationIdle(vm, view); await Settle(window);
                Check(metrics, "overlay_wheel_xy_zoom", plot.ZoomBounds.Width < 1 && plot.ZoomBounds.Height < 1 && vm.Viewport.EndSeconds - vm.Viewport.StartSeconds < 4);
                plot.ZoomAt(overlayCenter, -120); await NavigationIdle(vm, view); await Settle(window);
                Check(metrics, "overlay_wheel_out", Math.Abs(plot.ZoomBounds.Width - 1) < 1e-8 && Math.Abs(vm.Viewport.EndSeconds - 4) < 1e-8);
                plot.ZoomAt(overlayCenter, 120); plot.ZoomAt(overlayCenter, 120); plot.ZoomAt(overlayCenter, 120); await NavigationIdle(vm, view); await Settle(window);
                Check(metrics, "overlay_fast_wheel_latest_preview", Math.Abs((vm.Viewport.EndSeconds - vm.Viewport.StartSeconds) - 4 * Math.Pow(0.8, 3)) < 1e-7);
                plot.ResetZoom(); await NavigationIdle(vm, view); await Settle(window);
                plot.ZoomSelection(InArea(overlayArea, 0.25, 0.25), InArea(overlayArea, 0.75, 0.75)); await NavigationIdle(vm, view); await Settle(window);
                Check(metrics, "overlay_drag_xy_zoom", Math.Abs(plot.ZoomBounds.Width - 0.5) < 1e-8 && Math.Abs(plot.ZoomBounds.Height - 0.5) < 1e-8);
                Snapshot(window, directory, "overlay_navigation.png");
                plot.PreviousZoom(); await NavigationIdle(vm, view); await Settle(window);
                Check(metrics, "overlay_previous_zoom", Math.Abs(plot.ZoomBounds.Width - 1) < 1e-8);
                plot.ResetZoom(); await NavigationIdle(vm, view); await Settle(window);
                Check(metrics, "overlay_clipped_empty_drag_ignored", !plot.ZoomSelection(new Point(overlayArea.Right, overlayArea.Top + 10), new Point(overlayArea.Right + 100, overlayArea.Bottom)) && plot.ZoomBounds.Right <= 1);
                var grid = Find<DataGrid>(view, "ModeHopRanges");
                grid.SelectedItem = vm.Ranges[0];
                await Idle(vm); await Settle(window);
                Check(metrics, "list_selection_zoom", vm.Viewport.EndSeconds - vm.Viewport.StartSeconds < 0.01 && vm.Viewport.Ranges.Count > 0);
                Snapshot(window, directory, "zoom.png");
                var back = Descendants(view).OfType<Button>().Single(button => (string)button.Content == "이전 범위");
                back.Command.Execute(null); await Idle(vm);
                Check(metrics, "back_to_full", vm.Viewport.StartSeconds == 0 && vm.Viewport.EndSeconds == 4);
                await vm.ShowRangeAsync(0.02, 0.04, true); await Settle(window);
                var full = Descendants(view).OfType<Button>().Single(button => (string)button.Content == "전체 보기");
                full.Command.Execute(null); await Idle(vm);
                Check(metrics, "full_view_command", vm.Viewport.StartSeconds == 0 && vm.Viewport.EndSeconds == 4);
                var modeTabs = (TabControl)view.FindName("ModeHopTabs"); modeTabs.SelectedIndex = 1; await Settle(window);
                var qualityView = Descendants(view).OfType<AuxiliaryQualityView>().Single();
                var qualityVm = vm.AuxiliaryQuality;
                Check(metrics, "quality_datacontext", ReferenceEquals(qualityView.DataContext, qualityVm));
                await qualityVm.StitchAsync(); await Settle(window);
                var stitchMetrics = JsonContractSerializer.Read<AuxiliaryQualityRunMetrics>(Path.Combine(qualityVm.ResultDirectory, "metrics.json"));
                Check(metrics, "stitched_4528098_samples", stitchMetrics.Status == "COMPLETED" && stitchMetrics.RetainedSamples == 4528098 && stitchMetrics.JoinCount == 755);
                Check(metrics, "stitched_preview_limit", qualityVm.SignalPlot.Primary.Values.Length <= 10000 && qualityVm.SignalPlot.Markers.Length == 755);
                Check(metrics, "csv_buttons_bound", ReferenceEquals(Find<Button>(qualityView, "AuxExportDiagnosed").Command, qualityVm.ExportDiagnosedCommand) && ReferenceEquals(Find<Button>(qualityView, "AuxExportEntire").Command, qualityVm.ExportEntireCommand));
                Check(metrics, "whole_export_available_before_diagnosis", qualityVm.ExportEntireCommand.CanExecute(null) && !qualityVm.ExportDiagnosedCommand.CanExecute(null));
                var fullCsv = await qualityVm.ExportCsvAsync(Path.Combine(directory, "artifacts", "full_stitched.csv"), true);
                Check(metrics, "full_csv_export_completed", fullCsv != null && fullCsv.Metadata.RowCount == 4528098 && fullCsv.Metadata.LastDiagnosis == null);
                ExportEvidence fullCsvEvidence;
                try { fullCsvEvidence = await VerifyExport(fullCsv); }
                finally { RemoveSmokeCsv(fullCsv, directory); }
                JsonContractSerializer.Write(Path.Combine(directory, "artifacts", "full_csv_check.json"), fullCsvEvidence);
                Check(metrics, "full_csv_all_samples_and_joins", fullCsvEvidence.Rows == 4528098 && fullCsvEvidence.JoinRows == 755 && fullCsvEvidence.LastSourceIndex == 4999999);
                Snapshot(window, directory, "stitched.png");
                var stitchedPlot = Descendants(qualityView).OfType<AuxiliaryDiagnosticPlot>().Single();
                var stitchedArea = stitchedPlot.InteractionArea;
                stitchedPlot.ZoomAt(InArea(stitchedArea, 0.5, 0.5), 120); await Settle(window);
                Check(metrics, "stitched_wheel_xy_zoom", stitchedPlot.ZoomBounds.Width < 1 && stitchedPlot.ZoomBounds.Height < 1);
                stitchedPlot.ZoomAt(InArea(stitchedArea, 0.5, 0.5), -120); await Settle(window);
                Check(metrics, "stitched_wheel_out", Math.Abs(stitchedPlot.ZoomBounds.Width - 1) < 1e-8);
                stitchedPlot.ZoomSelection(InArea(stitchedArea, 0.2, 0.2), InArea(stitchedArea, 0.8, 0.8)); await Settle(window);
                Check(metrics, "stitched_drag_zoom_and_range_selection", stitchedPlot.ZoomBounds.Width < 1 && double.Parse(qualityVm.StartText, System.Globalization.CultureInfo.InvariantCulture) > 0 && qualityVm.Quality == null);
                Snapshot(window, directory, "stitched_navigation.png");
                stitchedPlot.ResetZoom(); await Settle(window);
                Find<TextBox>(qualityView, "AuxStart").Text = "0"; Find<TextBox>(qualityView, "AuxEnd").Text = "1200";
                await qualityVm.AnalyzeAsync(); await Settle(window);
                Check(metrics, "quality_selected_interval", qualityVm.Quality?.Metrics.Phase.SampleCount == 1500000);
                Check(metrics, "quality_grid_not_false_pass", qualityVm.Quality.Metrics.Decision == "REJECT_PHASE_QC" && qualityVm.Quality.Metrics.Phase.NonmonotonicRatio > 1e-5);
                Check(metrics, "quality_resampled_phase_checked", qualityVm.Quality.Metrics.ResampledPhaseResidualRmsRadians.HasValue && qualityVm.Quality.Plots.Count == 8);
                Check(metrics, "diagnosed_csv_available_for_rejected_qc", qualityVm.Quality.Metrics.Decision == "REJECT_PHASE_QC" && qualityVm.ExportDiagnosedCommand.CanExecute(null));
                var diagnosedResult = qualityVm.Quality;
                Find<TextBox>(qualityView, "AuxStart").Text = "500"; Find<TextBox>(qualityView, "AuxEnd").Text = "600";
                var diagnosedCsv = await qualityVm.ExportCsvAsync(Path.Combine(directory, "artifacts", "diagnosed_stitched.csv"), false);
                Check(metrics, "csv_uses_completed_diagnosis_not_edited_fields", diagnosedCsv != null && diagnosedCsv.Metadata.StartIndex == 0 && diagnosedCsv.Metadata.EndIndexExclusive == 1500000 && diagnosedCsv.Metadata.LastDiagnosisSettings.EndSeconds == 1.2);
                ExportEvidence diagnosedCsvEvidence;
                try { diagnosedCsvEvidence = await VerifyExport(diagnosedCsv); }
                finally { RemoveSmokeCsv(diagnosedCsv, directory); }
                JsonContractSerializer.Write(Path.Combine(directory, "artifacts", "diagnosed_csv_check.json"), diagnosedCsvEvidence);
                Check(metrics, "diagnosed_csv_full_resolution_and_mapping", diagnosedCsvEvidence.Rows == 1500000 && diagnosedCsvEvidence.JoinRows == 249 && diagnosedCsvEvidence.LastSourceIndex == diagnosedResult.Metrics.SourceEndIndexInclusive);
                Check(metrics, "csv_preserves_diagnosis_and_plots", ReferenceEquals(diagnosedResult, qualityVm.Quality) && qualityVm.Quality.Plots.Count == 8);
                Snapshot(window, directory, "csv_export.png");
                var invalidExport = await qualityVm.ExportCsvAsync(path, false, true);
                Check(metrics, "csv_source_overwrite_blocked", invalidExport == null && ReferenceEquals(diagnosedResult, qualityVm.Quality));
                var exportFailure = JsonContractSerializer.Read<AuxiliaryQualityRunMetrics>(Path.Combine(qualityVm.ResultDirectory, "metrics.json"));
                Check(metrics, "csv_failure_logged", exportFailure.Status == "FAILED");
                var cancelledCsvPath = Path.Combine(directory, "artifacts", "cancelled_export.csv");
                var cancelledCsv = qualityVm.ExportCsvAsync(cancelledCsvPath, false); qualityVm.Cancel(); await cancelledCsv;
                var exportCancellation = JsonContractSerializer.Read<AuxiliaryQualityRunMetrics>(Path.Combine(qualityVm.ResultDirectory, "metrics.json"));
                Check(metrics, "csv_cancel_logged_and_no_final_file", exportCancellation.Status == "CANCELLED" && !File.Exists(cancelledCsvPath) && ReferenceEquals(diagnosedResult, qualityVm.Quality));
                qualityVm.StartText = "0"; qualityVm.EndText = "1200";
                Snapshot(window, directory, "quality.png");
                var qualityScroll = (ScrollViewer)qualityView.FindName("DiagnosticsScroll");
                qualityScroll.ScrollToEnd(); await Settle(window);
                Check(metrics, "quality_residual_scroll", qualityScroll.VerticalOffset > 0);
                Snapshot(window, directory, "quality_residual.png");
                var diagnosticPlots = Descendants(qualityView).OfType<AuxiliaryDiagnosticPlot>().ToList();
                Check(metrics, "all_eight_diagnostic_plots_present", diagnosticPlots.Count == 8);
                foreach (var diagnostic in diagnosticPlots)
                {
                    var diagnosticArea = diagnostic.InteractionArea;
                    diagnostic.ZoomAt(InArea(diagnosticArea, 0.5, 0.5), 120);
                    Check(metrics, "diagnostic_wheel_" + diagnostic.Plot.Title, diagnostic.ZoomBounds.Width < 1 && diagnostic.ZoomBounds.Height < 1);
                    diagnostic.ZoomAt(InArea(diagnosticArea, 0.5, 0.5), -120);
                    Check(metrics, "diagnostic_wheel_out_" + diagnostic.Plot.Title, Math.Abs(diagnostic.ZoomBounds.Width - 1) < 1e-8);
                    diagnostic.ZoomSelection(InArea(diagnosticArea, 0.25, 0.25), InArea(diagnosticArea, 0.75, 0.75));
                    Check(metrics, "diagnostic_drag_" + diagnostic.Plot.Title, diagnostic.ZoomBounds.Width < 1 && diagnostic.ZoomBounds.Height < 1);
                    diagnostic.ResetZoom();
                }
                var residualPlot = diagnosticPlots.Last();
                residualPlot.ZoomSelection(InArea(residualPlot.InteractionArea, 0.25, 0.25), InArea(residualPlot.InteractionArea, 0.75, 0.75)); await Settle(window);
                Snapshot(window, directory, "diagnostic_navigation.png");
                residualPlot.PreviousZoom(); await Settle(window);
                Check(metrics, "diagnostic_previous_zoom", Math.Abs(residualPlot.ZoomBounds.Width - 1) < 1e-8);
                Check(metrics, "diagnostic_clipped_empty_drag_ignored", !residualPlot.ZoomSelection(new Point(residualPlot.InteractionArea.Right, residualPlot.InteractionArea.Top + 10), new Point(residualPlot.InteractionArea.Right + 100, residualPlot.InteractionArea.Bottom)) && residualPlot.ZoomBounds.Right <= 1);
                var qualityStart = qualityVm.StartText; qualityVm.StartText = "-1"; await qualityVm.AnalyzeAsync();
                Check(metrics, "quality_invalid_range_preserves_result", qualityVm.Quality != null && !qualityVm.IsBusy); qualityVm.StartText = qualityStart;
                var cancelledQuality = qualityVm.AnalyzeAsync();
                Check(metrics, "quality_busy_blocks_parent_reload", vm.IsBusy && !vm.ReloadCommand.CanExecute(null));
                qualityVm.Cancel(); await cancelledQuality;
                var cancelledQualityMetrics = JsonContractSerializer.Read<AuxiliaryQualityRunMetrics>(Path.Combine(qualityVm.ResultDirectory, "metrics.json"));
                Check(metrics, "quality_cancel_logged", cancelledQualityMetrics.Status == "CANCELLED" && !qualityVm.IsBusy);
                modeTabs.SelectedIndex = 0; await Settle(window);
                Find<TextBox>(view, "ModeHopThreshold").Text = "4";
                Find<Button>(view, "ModeHopApply").Command.Execute(null); await Idle(vm);
                Check(metrics, "threshold_binding_and_recalculation", vm.Ranges.Count == 0 && vm.Viewport.ThresholdVoltage == 4);
                Check(metrics, "quality_invalidated_on_mask_change", qualityVm.Quality == null && qualityVm.SignalPlot == null);
                Find<TextBox>(view, "ModeHopThreshold").Text = "1.8";
                Find<TextBox>(view, "ModeHopSweep").Text = "";
                await vm.ApplyAsync();
                Check(metrics, "blank_period_disables_markers", vm.Ranges.Count == 755 && vm.Viewport.SweepBoundaries.Count == 0);
                Find<TextBox>(view, "ModeHopSweep").Text = "1200";
                Find<TextBox>(view, "ModeHopOrigin").Text = "100";
                await vm.ApplyAsync();
                Check(metrics, "offset_binding", Math.Abs(vm.Viewport.SweepBoundaries[0].Seconds - 0.1) < 1e-12);
                var saved = Path.Combine(directory, "artifacts", "saved_settings.json");
                vm.SaveSettingsTo(saved); vm.ThresholdText = "4"; vm.LoadSettingsFrom(saved);
                Check(metrics, "settings_roundtrip", vm.ThresholdText == "1.8" && vm.OriginText == "100");
                var reduced = Path.Combine(directory, "artifacts", "reduced_settings.json");
                var smallSettings = JsonContractSerializer.Read<ModeHopSettings>(saved);
                smallSettings.MaximumPreviewPoints = 1000; JsonContractSerializer.Write(reduced, smallSettings);
                vm.LoadSettingsFrom(reduced); await vm.ApplyAsync();
                Check(metrics, "settings_preview_limit_roundtrip", vm.Viewport.Auxiliary.Values.Length <= 1000 && vm.Viewport.Trigger.Values.Length <= 1000);
                var previous = vm.Viewport;
                vm.SweepText = "-1"; await vm.ApplyAsync();
                Check(metrics, "invalid_settings_keep_result", ReferenceEquals(previous, vm.Viewport) && !vm.IsBusy && vm.Status.Contains("양수"));
                vm.LoadSettingsFrom(saved);
                vm.OriginText = "0";
                await vm.ApplyAsync();
                var load = vm.LoadAsync(path);
                vm.CancelCommand.Execute(null); await load;
                var cancelled = JsonContractSerializer.Read<ModeHopRunMetrics>(Path.Combine(vm.ResultDirectory, "metrics.json"));
                Check(metrics, "cancel_logged", cancelled.Status == "CANCELLED" && vm.Viewport == null && !vm.IsBusy);
                await vm.LoadAsync(path + ".missing");
                var failed = JsonContractSerializer.Read<ModeHopRunMetrics>(Path.Combine(vm.ResultDirectory, "metrics.json"));
                Check(metrics, "failure_logged", failed.Status == "FAILED" && vm.Viewport == null && !vm.IsBusy);
                await vm.LoadAsync(path); await Settle(window);
                Check(metrics, "reload_after_cancel_and_failure", vm.Ranges.Count == 755 && vm.Viewport.EndSeconds == 4);
                window.WorkspaceTabs.SelectedIndex = 1;
                await Settle(window);
                Check(metrics, "existing_analysis_datacontext", ReferenceEquals(window.DataContext, workspace) && Descendants(window).OfType<TextBox>().Any(box => BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path?.Path == "AuxiliaryPath"));
                var legacyPlot = Descendants(window).OfType<SignalPlot>().First();
                var previousValues = legacyPlot.Values;
                legacyPlot.SetCurrentValue(SignalPlot.ValuesProperty, Enumerable.Range(0, 1024).Select(i => Math.Sin(i * 0.1)).ToArray()); await Settle(window);
                var legacyBounds = legacyPlot.ZoomBounds; var legacyArea = legacyPlot.InteractionArea;
                legacyPlot.ZoomAt(InArea(legacyArea, 0.5, 0.5), 120); await Settle(window);
                Check(metrics, "legacy_wheel_xy_zoom", legacyPlot.ZoomBounds.Width < legacyBounds.Width && legacyPlot.ZoomBounds.Height < legacyBounds.Height);
                legacyPlot.ZoomAt(InArea(legacyArea, 0.5, 0.5), -120); await Settle(window);
                Check(metrics, "legacy_wheel_out", legacyPlot.ZoomBounds.Width >= legacyBounds.Width - 2);
                legacyPlot.ZoomSelection(InArea(legacyArea, 0.25, 0.25), InArea(legacyArea, 0.75, 0.75)); await Settle(window);
                Check(metrics, "legacy_drag_xy_zoom", legacyPlot.ZoomBounds.Width < legacyBounds.Width && legacyPlot.ZoomBounds.Height < legacyBounds.Height);
                Snapshot(window, directory, "legacy_navigation.png");
                legacyPlot.SetCurrentValue(SignalPlot.ValuesProperty, new[] { 0.0, 0.0 }); await Settle(window);
                Check(metrics, "legacy_constant_two_samples_zoom_safe", legacyPlot.ZoomAt(InArea(legacyPlot.InteractionArea, 0.5, 0.5), 120) && legacyPlot.ZoomBounds.Height > 0 && legacyPlot.ZoomBounds.Width >= 1);
                legacyPlot.ResetZoom(); legacyPlot.SetCurrentValue(SignalPlot.ValuesProperty, previousValues); await Settle(window);
                Snapshot(window, directory, "existing_analysis.png");
                window.WorkspaceTabs.SelectedIndex = 0; await Settle(window);
                Snapshot(window, directory, "final.png");
                Check(metrics, "runtime_binding_errors_zero", Trace.Errors.Count == 0);
                var closingLoad = vm.LoadAsync(path);
                window.Close();
                await closingLoad;
                await Task.Delay(20);
                var closingMetrics = JsonContractSerializer.Read<ModeHopRunMetrics>(Path.Combine(vm.ResultDirectory, "metrics.json"));
                Check(metrics, "close_cancels_and_flushes_log", !window.IsVisible && closingMetrics.Status == "CANCELLED" && !vm.IsBusy);
                metrics.Status = "COMPLETED";
            }
            catch (Exception ex) { metrics.Error = ex.ToString(); }
            finally
            {
                metrics.ElapsedSeconds = watch.Elapsed.TotalSeconds;
                metrics.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
                JsonContractSerializer.Write(Path.Combine(directory, "metrics.json"), metrics);
                File.WriteAllText(Path.Combine(directory, "summary.md"), "# WPF Mode hop integration smoke\n\n- Status: " + metrics.Status + "\n- Checks: " + metrics.Checks.Count + "\n- Runtime binding errors: " + Trace.Errors.Count + "\n- Error: " + (metrics.Error ?? "none") + "\n\n## Interpretation\n\nActual x64 WPF window, bindings, rendering, commands and selection were exercised in-process. Desktop mouse injection and file-dialog navigation were not exercised.\n\n## Decision\n\n" + (metrics.Status == "COMPLETED" ? "ACCEPT" : "REJECT") + "\n");
                Application.Current.Shutdown(metrics.Status == "COMPLETED" ? 0 : 1);
            }
        }

        private static async Task Idle(ModeHopPreviewViewModel vm)
        {
            var watch = Stopwatch.StartNew();
            while (vm.IsBusy) { if (watch.Elapsed.TotalSeconds > 30) throw new TimeoutException("GUI operation exceeded 30 seconds"); await Task.Delay(10); }
        }
        private static Point InArea(Rect area, double x, double y) => new Point(area.Left + area.Width * x, area.Top + area.Height * y);
        private static async Task NavigationIdle(ModeHopPreviewViewModel vm, ModeHopPreviewView view)
        {
            var watch = Stopwatch.StartNew();
            while (vm.IsBusy || view.IsNavigating) { if (watch.Elapsed.TotalSeconds > 30) throw new TimeoutException("Graph navigation did not settle"); await Task.Delay(10); }
        }
        private static async Task Settle(Window window) { window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
        private static Task<ExportEvidence> VerifyExport(AuxiliaryCsvExportResult result) => Task.Run(() =>
        {
            var evidence = new ExportEvidence { Bytes = new FileInfo(result.CsvPath).Length, CsvRemovedAfterVerification = true };
            var previousSource = -1;
            using (var reader = new StreamReader(result.CsvPath))
            {
                if (reader.ReadLine() != AuxiliaryCsvExporter.Header) throw new InvalidDataException("Export CSV header mismatch");
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var cells = line.Split(',');
                    if (cells.Length != 6) throw new InvalidDataException("Export CSV column mismatch");
                    var index = int.Parse(cells[0], System.Globalization.CultureInfo.InvariantCulture);
                    var original = int.Parse(cells[3], System.Globalization.CultureInfo.InvariantCulture);
                    if (index != result.Metadata.StartIndex + evidence.Rows || (evidence.Rows > 0 && original <= previousSource)) throw new InvalidDataException("Export CSV index mismatch");
                    if (evidence.Rows > 0 && cells[5] != (original == previousSource + 1 ? "0" : "1")) throw new InvalidDataException("Export CSV join mismatch");
                    if (cells[5] == "1") evidence.JoinRows++;
                    if (evidence.Rows == 0) evidence.FirstIndex = index;
                    evidence.LastIndex = index; evidence.LastSourceIndex = original; previousSource = original; evidence.Rows++;
                }
            }
            using (var stream = File.OpenRead(result.CsvPath)) using (var sha = SHA256.Create()) evidence.Sha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            var metadata = JsonContractSerializer.Read<AuxiliaryCsvExportMetadata>(result.MetadataPath);
            if (evidence.Rows != metadata.RowCount || evidence.JoinRows != metadata.JoinRows || evidence.Sha256 != metadata.CsvSha256) throw new InvalidDataException("Export CSV sidecar verification failed");
            return evidence;
        });
        private static void RemoveSmokeCsv(AuxiliaryCsvExportResult result, string directory)
        {
            // Only generated smoke artifacts; keep metadata/hash evidence while limiting disk use.
            var prefix = Path.GetFullPath(Path.Combine(directory, "artifacts")) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(result.CsvPath);
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Refusing to remove CSV outside smoke artifacts");
            File.Delete(path);
        }
        [DataContract]
        public sealed class ExportEvidence
        {
            [DataMember] public int Rows { get; set; }
            [DataMember] public int JoinRows { get; set; }
            [DataMember] public int FirstIndex { get; set; }
            [DataMember] public int LastIndex { get; set; }
            [DataMember] public int LastSourceIndex { get; set; }
            [DataMember] public long Bytes { get; set; }
            [DataMember] public string Sha256 { get; set; }
            [DataMember] public bool CsvRemovedAfterVerification { get; set; }
        }
        private static void Check(SmokeMetrics metrics, string name, bool condition) { metrics.Checks[name] = condition; if (!condition) throw new InvalidOperationException("GUI check failed: " + name); }
        private static T Find<T>(DependencyObject parent, string id) where T : DependencyObject => Descendants(parent).OfType<T>().Single(item => AutomationProperties.GetAutomationId(item) == id);
        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            yield return parent;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
        }
        private static void Snapshot(Window window, string directory, string name)
        {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(directory, "artifacts", name))) encoder.Save(stream);
        }
        private sealed class BindingTrace : TraceListener
        {
            public List<string> Errors { get; } = new List<string>();
            public override void Write(string message) { if (!string.IsNullOrEmpty(message)) Errors.Add(message); }
            public override void WriteLine(string message) => Write(message);
        }
        [DataContract]
        public sealed class SmokeMetrics
        {
            [DataMember] public string Status { get; set; }
            [DataMember] public string Error { get; set; }
            [DataMember] public double ElapsedSeconds { get; set; }
            [DataMember] public long PeakWorkingSetBytes { get; set; }
            [DataMember] public Dictionary<string, bool> Checks { get; set; }
            [DataMember] public List<string> BindingErrors { get; set; }
        }
    }
}
