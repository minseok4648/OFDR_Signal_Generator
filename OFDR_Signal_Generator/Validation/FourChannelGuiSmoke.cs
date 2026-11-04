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
using OFDR_Signal_Core.Services;
using OFDR_Signal_Generator.Controls;
using OFDR_Signal_Generator.ViewModels;
using Smoke = OFDR_Signal_Generator.Validation.ModeHopGuiSmoke;

namespace OFDR_Signal_Generator.Validation
{
    internal static class FourChannelGuiSmoke
    {
        public static async Task RunAsync(MainWindow window, OfdrWorkspaceViewModel workspace)
        {
            var args = Environment.GetCommandLineArgs(); var flag = Array.IndexOf(args, "--four-channel-smoke");
            var root = flag + 1 < args.Length ? args[flag + 1] : Path.Combine("harness", "results");
            var directory = Path.GetFullPath(Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_four_channel_gui_smoke"));
            Directory.CreateDirectory(Path.Combine(directory, "artifacts"));
            var metrics = new Smoke.SmokeMetrics { Status = "FAILED", Checks = new Dictionary<string, bool>(), BindingErrors = Smoke.BindingErrors };
            var watch = Stopwatch.StartNew();
            JsonContractSerializer.Write(Path.Combine(directory, "config.json"), new Dictionary<string, string> { ["fixture"] = "CH1/2/3/4 synthetic, Ref 4096 / Signal 3072 samples per sweep", ["full_data_validation"] = "false", ["process"] = Environment.Is64BitProcess ? "x64" : "x86" });
            var parent = workspace.ModeHopPreview; var vm = parent.SweepPreparation;
            try
            {
                var refPath = Fixture(directory, "ref_fixture.csv", 4096); var sigPath = Fixture(directory, "signal_fixture.csv", 3072);
                window.Show(); await Smoke.Settle(window);
                Smoke.Check(metrics, "native_x64_window", Environment.Is64BitProcess && new WindowInteropHelper(window).Handle != IntPtr.Zero && window.IsVisible);
                var view = Smoke.Descendants(window).OfType<ModeHopPreviewView>().Single();
                var tabs = (TabControl)view.FindName("ModeHopTabs"); tabs.SelectedIndex = 2; await Smoke.Settle(window);
                var preparationView = Smoke.Descendants(view).OfType<SweepPreparationView>().Single();
                Smoke.Check(metrics, "preparation_datacontext", ReferenceEquals(preparationView.DataContext, vm));
                Smoke.Check(metrics, "ref_path_binding_active", BindingOperations.GetBindingExpression(Smoke.Find<TextBox>(preparationView, "SweepRefPath"), TextBox.TextProperty)?.Status == BindingStatus.Active);
                Smoke.Check(metrics, "missing_diagnosis_disables_apply", !vm.ApplyCommand.CanExecute(null));
                await vm.LoadForRoleAsync("Ref", refPath); await Smoke.Settle(window);
                Smoke.Check(metrics, "complete_sweep_default_first", vm.Sweeps.Count == 2 && vm.SelectedSweep.Number == 1);
                Smoke.Check(metrics, "four_channel_preview", parent.Viewport.SweepTrigger != null && parent.MeasurementPlot != null && parent.Viewport.SweepBoundaries.Count == 3);
                Smoke.Check(metrics, "review_command_bound", ReferenceEquals(Smoke.Find<Button>(preparationView, "SweepReview").Command, vm.ReviewCommand));
                parent.ThresholdText = "2.1"; await Smoke.Settle(window);
                Smoke.Check(metrics, "trigger_threshold_shared_between_tabs", vm.ModeHopThresholdText == "2.1" && Smoke.Find<TextBox>(preparationView, "SweepModeHopThreshold").Text == "2.1");
                vm.ModeHopThresholdText = "1.8";
                await vm.ReviewAsync(); await Smoke.Settle(window);
                Smoke.Check(metrics, "ref_stitched_pair_preview", vm.Quality.Stitched?.Values.Length == 4096 && vm.Quality.MeasurementPlot?.Primary.Values.Length <= 10000);
                var auxPlot = Smoke.Descendants(preparationView).OfType<AuxiliaryDiagnosticPlot>().Single(p => ReferenceEquals(p.Plot, vm.Quality.SignalPlot));
                var sigPlot = Smoke.Descendants(preparationView).OfType<AuxiliaryDiagnosticPlot>().Single(p => ReferenceEquals(p.Plot, vm.Quality.MeasurementPlot));
                var auxTop = auxPlot.TransformToAncestor(preparationView).Transform(new Point());
                var sigTop = sigPlot.TransformToAncestor(preparationView).Transform(new Point());
                Smoke.Check(metrics, "stitched_graphs_have_space_without_overlap", auxPlot.ActualHeight >= 220 && sigPlot.ActualHeight >= 220 && sigTop.Y >= auxTop.Y + auxPlot.ActualHeight - 1);
                Smoke.Snapshot(window, directory, "ref_stitched_pair.png");
                await vm.Quality.AnalyzeAsync(); await vm.ConfirmAsync();
                Smoke.Check(metrics, "ref_confirmed", vm.PreparedReference?.SampleCount == 4096 && vm.PreparedReference.Metadata.Diagnosis.Decision == "PASS_PHASE_QC");
                var previousRef = vm.PreparedReference;
                await vm.LoadForRoleAsync("Signal", sigPath); await vm.ReviewAsync(); await vm.Quality.AnalyzeAsync(); await vm.ConfirmAsync(); await Smoke.Settle(window);
                Smoke.Check(metrics, "signal_confirmed_ref_preserved", ReferenceEquals(previousRef, vm.PreparedReference) && vm.PreparedSignal?.SampleCount == 3072);
                Smoke.Check(metrics, "last_diagnosis_settings_retained", vm.PreparedSignal.Metadata.DiagnosisSettings != null);
                Smoke.Snapshot(window, directory, "phase_diagnostics.png");
                var exportRef = Path.Combine(directory, "ref_prepared.csv"); var exportSig = Path.Combine(directory, "signal_prepared.csv");
                await vm.ExportAsync("Ref", exportRef); await vm.ExportAsync("Signal", exportSig);
                Smoke.Check(metrics, "paired_csv_and_metadata_saved", File.Exists(exportRef) && File.Exists(Path.ChangeExtension(exportSig, ".metadata.json")));
                await vm.ImportAsync("Ref", exportRef); await vm.ImportAsync("Signal", exportSig);
                Smoke.Check(metrics, "paired_csv_reloaded", vm.PreparedReference.SampleCount == 4096 && vm.PreparedSignal.SampleCount == 3072 && vm.PreparedSignal.Metadata.CsvSha256.Length == 64);
                var rejectedRefPath = RejectedCopy(exportRef, directory, "ref_rejected", .14);
                var rejectedSigPath = RejectedCopy(exportSig, directory, "signal_rejected", .13);
                await vm.ImportAsync("Ref", rejectedRefPath); await vm.ImportAsync("Signal", rejectedSigPath); await Smoke.Settle(window);
                Smoke.Check(metrics, "rejected_pair_disables_apply", !vm.ApplyCommand.CanExecute(null) && !Smoke.Find<Button>(preparationView, "SweepApply").IsEnabled);
                var readiness = Smoke.Find<TextBlock>(preparationView, "SweepQcReadiness").Text;
                Smoke.Check(metrics, "rejection_shows_roles_channel_range_and_ratio", readiness.Contains("Ref") && readiness.Contains("Signal") && readiness.Contains("CH3") && readiness.Contains("ms") && readiness.Contains("14%") && readiness.Contains("13%"));
                Smoke.Check(metrics, "rejected_pair_remains_exportable", vm.SaveRefCommand.CanExecute(null) && vm.SaveSignalCommand.CanExecute(null));
                await vm.ExportAsync("Ref", Path.Combine(directory, "rejected_ref_roundtrip.csv"));
                Smoke.Check(metrics, "rejected_csv_saved", File.Exists(Path.Combine(directory, "rejected_ref_roundtrip.csv")));
                vm.ApplyCommand.Execute(null); await Smoke.Settle(window);
                Smoke.Check(metrics, "direct_apply_command_is_guarded", workspace.IsLegacyInput && vm.Status.Contains("차단"));
                workspace.ApplyPreparedSweeps(vm.PreparedReference, vm.PreparedSignal);
                Smoke.Check(metrics, "direct_apply_input_is_guarded", workspace.IsLegacyInput && workspace.InputValidationText.Contains("14%"));
                Smoke.Snapshot(window, directory, "qc_rejected_pair.png");
                await vm.ImportAsync("Ref", exportRef); await vm.ImportAsync("Signal", exportSig);
                Smoke.Check(metrics, "passing_reimport_reenables_apply", vm.ApplyCommand.CanExecute(null));
                var savedRef = vm.PreparedReference;
                var importing = vm.ImportAsync("Ref", exportRef); vm.Cancel(); await importing;
                Smoke.Check(metrics, "cancelled_import_preserves_input", ReferenceEquals(savedRef, vm.PreparedReference) && vm.Status.Contains("취소"));
                vm.Quality.StartText = "0.1";
                Smoke.Check(metrics, "range_edit_invalidates_diagnosis", vm.Quality.Quality == null && !vm.ConfirmCommand.CanExecute(null));
                vm.ApplyCommand.Execute(null); await Smoke.Settle(window);
                Smoke.Check(metrics, "prepared_pair_applied", !workspace.IsLegacyInput && workspace.WorkspaceSelectedTab == 1 && workspace.SamplingRateSamplesPerSecond == 1000000);
                // Synthetic saved metric tests a stricter analysis limit after applying an input.
                var originalRatio = vm.PreparedReference.Metadata.Diagnosis.Phase.NonmonotonicRatio;
                vm.PreparedReference.Metadata.Diagnosis.Phase.NonmonotonicRatio = 1e-6;
                workspace.MaxNonmonotonicRatio = 5e-7; await Smoke.Settle(window);
                Smoke.Check(metrics, "changed_analysis_limit_disables_run", !workspace.RunProcessingCommand.CanExecute(null) && workspace.IsProcessingQcBlocked && !vm.ApplyCommand.CanExecute(null));
                await workspace.RunProcessingAsync(); await Smoke.Settle(window);
                Smoke.Check(metrics, "direct_run_is_guarded_before_processing", workspace.ProcessingResult == null && !workspace.IsBusy && workspace.ProgressText.Contains("처리를 시작하지 않았습니다"));
                Smoke.Snapshot(window, directory, "qc_blocked_processing.png");
                vm.PreparedReference.Metadata.Diagnosis.Phase.NonmonotonicRatio = originalRatio;
                workspace.MaxNonmonotonicRatio = 1e-5; await Smoke.Settle(window);
                Smoke.Check(metrics, "valid_input_reenables_run", workspace.RunProcessingCommand.CanExecute(null));
                workspace.UsePhysicalAxes = false; workspace.RestrictToMeasurementRange = false; workspace.SegmentSize = 128; workspace.SegmentHop = 128; workspace.ZeroPadFactor = 4;
                await workspace.RunProcessingAsync(); await Smoke.Settle(window);
                Smoke.Check(metrics, "existing_analysis_completed", workspace.ProcessingResult?.SampleCount == 3072 && workspace.ProcessingResult.SignalKLinearization.SampleCount == 3072);
                Smoke.Check(metrics, "unknown_displacement_not_fabricated", workspace.ProcessingResult.PhysicalAxis.ExpectedAppliedStrainMicrostrain == null);
                Smoke.Snapshot(window, directory, "prepared_existing_analysis.png");
                workspace.UseLegacyInputsCommand.Execute(null); await Smoke.Settle(window);
                Smoke.Check(metrics, "legacy_input_mode_available", workspace.IsLegacyInput && workspace.PolarizationOptions.Count == 2);
                workspace.WorkspaceSelectedTab = 0; tabs.SelectedIndex = 0; await Smoke.Settle(window);
                var overlay = Smoke.Descendants(view).OfType<ModeHopOverlayPlot>().Single();
                var center = Smoke.InArea(overlay.InteractionArea, 0.5, 0.5);
                Smoke.Check(metrics, "overlay_wheel_zoom", overlay.ZoomAt(center, 120));
                await NavigationIdle(parent, view); await Smoke.Settle(window);
                Smoke.Check(metrics, "raw_ch4_tracks_time_range", parent.MeasurementPlot.XStart == parent.Viewport.StartSeconds && parent.MeasurementPlot.XEnd == parent.Viewport.EndSeconds);
                var measurement = Smoke.Descendants(view).OfType<AuxiliaryDiagnosticPlot>().First(p => p.IsVisible && ReferenceEquals(p.Plot?.Primary, parent.Viewport.Measurement));
                Smoke.Check(metrics, "ch4_wheel_zoom", measurement.ZoomAt(Smoke.InArea(measurement.InteractionArea, 0.5, 0.5), 120));
                Smoke.Check(metrics, "ch4_drag_zoom", measurement.ZoomSelection(Smoke.InArea(measurement.InteractionArea, 0.2, 0.2), Smoke.InArea(measurement.InteractionArea, 0.8, 0.8)));
                await NavigationIdle(parent, view); await Smoke.Settle(window);
                Smoke.Snapshot(window, directory, "raw_four_channels.png");
                Smoke.Check(metrics, "runtime_binding_errors_zero", Smoke.BindingErrors.Count == 0);
                var closingLoad = parent.LoadAsync(refPath); window.Close(); await closingLoad; await Task.Delay(20);
                Smoke.Check(metrics, "close_cancels_and_flushes", !window.IsVisible && !parent.IsBusy && JsonContractSerializer.Read<ModeHopRunMetrics>(Path.Combine(parent.ResultDirectory, "metrics.json")).Status == "CANCELLED");
                metrics.Status = "COMPLETED";
            }
            catch (Exception ex) { metrics.Error = ex.ToString(); }
            finally
            {
                await parent.CancelAndWaitAsync(); metrics.ElapsedSeconds = watch.Elapsed.TotalSeconds; metrics.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
                JsonContractSerializer.Write(Path.Combine(directory, "metrics.json"), metrics);
                File.WriteAllText(Path.Combine(directory, "summary.md"), "# Four-channel WPF smoke\n\nStatus: " + metrics.Status + "\nChecks: " + metrics.Checks.Count + "\nBinding errors: " + Smoke.BindingErrors.Count + "\nError: " + (metrics.Error ?? "none") + "\n\nSynthetic sample fixtures only. Actual x64 WPF startup, bindings, commands, plots, pairing, save/reload and cancellation exercised. OS file-dialog navigation and full real-data processing were not tested.\n");
                Application.Current.Shutdown(metrics.Status == "COMPLETED" ? 0 : 1);
            }
        }
        private static async Task NavigationIdle(ModeHopPreviewViewModel vm, ModeHopPreviewView view)
        {
            var watch = Stopwatch.StartNew(); while (vm.IsBusy || view.IsNavigating) { if (watch.Elapsed.TotalSeconds > 10) throw new TimeoutException("Navigation did not settle"); await Task.Delay(10); }
        }
        private static string RejectedCopy(string originalCsv, string folder, string name, double ratio)
        {
            var csv = Path.Combine(folder, name + ".csv"); File.Copy(originalCsv, csv);
            var metadata = JsonContractSerializer.Read<OFDR_Signal_Core.Models.PreparedSweepMetadata>(Path.ChangeExtension(originalCsv, ".metadata.json"));
            metadata.Diagnosis.Decision = "REJECT_PHASE_QC"; metadata.Diagnosis.Phase.NonmonotonicRatio = ratio;
            JsonContractSerializer.Write(Path.ChangeExtension(csv, ".metadata.json"), metadata); return csv;
        }
        private static string Fixture(string folder, string name, int period)
        {
            var path = Path.Combine(folder, name);
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("Model,DPO4104B\nSample Interval,1e-6\nRecord Length," + (period * 2 + 12) + "\nVertical Units,V,V,V,V\nHorizontal Units,s\nTIME,CH1,CH2,CH3,CH4");
                for (var i = 0; i < period * 2 + 12; i++)
                {
                    var j = ((i - 5) % period + period) % period;
                    var trigger = i == 5 || i == 5 + period || i == 5 + 2 * period ? "3.4" : "0.1";
                    writer.WriteLine((-2 + i * 1e-6).ToString("F3", CultureInfo.InvariantCulture) + "," + trigger + ",0," + Math.Cos(2 * Math.PI * 32 * j / period).ToString("G17", CultureInfo.InvariantCulture) + "," + Math.Sin(2 * Math.PI * 64 * j / period).ToString("G17", CultureInfo.InvariantCulture));
                }
            }
            return path;
        }
    }
}
