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
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Services;
using OFDR_Signal_Generator.Controls;
using OFDR_Signal_Generator.ViewModels;
using Smoke = OFDR_Signal_Generator.Validation.ModeHopGuiSmoke;

namespace OFDR_Signal_Generator.Validation
{
    internal static class LegacyModeHopComparisonSmoke
    {
        internal static async Task RunAsync(MainWindow window, OfdrWorkspaceViewModel workspace)
        {
            var args = Environment.GetCommandLineArgs(); var flag = Array.IndexOf(args, "--legacy-no-mode-hop-smoke");
            var input = Path.GetFullPath(args[flag + 1]); var directory = Path.GetFullPath(args[flag + 2]);
            if (Directory.Exists(directory)) throw new IOException("Experiment already exists: " + directory);
            Directory.CreateDirectory(Path.Combine(directory, "artifacts"));
            var watch = Stopwatch.StartNew();
            var metrics = new Smoke.SmokeMetrics { Status = "FAILED", Checks = new Dictionary<string, bool>(), BindingErrors = Smoke.BindingErrors };
            var parent = workspace.ModeHopPreview; var prep = parent.SweepPreparation;
            JsonContractSerializer.Write(Path.Combine(directory, "config.json"), new Dictionary<string, string>
            {
                ["input"] = input, ["crop"] = "first CH1 > 3 V + 15000 samples; 17000000 samples",
                ["segment_bins"] = "4096", ["hop_bins"] = "50", ["ifft_padding"] = "8",
                ["legacy_range_m"] = "0..50", ["provisional_round_trip_opd_m"] = "200",
                ["mode_hop_removal"] = "OFF; nonfinite mask retained", ["diagnostic_qc"] = "failure retained",
                ["methods"] = "existing production engine and notebook engine; same confirmed raw crops"
            });
            File.WriteAllText(Path.Combine(directory, "summary.md"), "# RUNNING\n\nActual x64 WPF raw-to-final comparison.\n");
            try
            {
                window.Show(); window.Width = 1850; window.Height = 1000; await Smoke.Settle(window);
                Smoke.Check(metrics, "native_x64_window", Environment.Is64BitProcess && window.IsVisible && new WindowInteropHelper(window).Handle != IntPtr.Zero);
                var view = Smoke.Descendants(window).OfType<ModeHopPreviewView>().Single();
                var tabs = (TabControl)view.FindName("ModeHopTabs"); tabs.SelectedIndex = 0; await Smoke.Settle(window);
                var box = Smoke.Find<CheckBox>(view, "ModeHopUseExclusion");
                Smoke.Check(metrics, "default_removal_enabled", box.IsChecked == true && prep.UseModeHopExclusion);
                box.IsChecked = false; await Smoke.Settle(window);
                Smoke.Check(metrics, "checkbox_keeps_legacy_algorithm", !prep.UseModeHopExclusion && !workspace.NotebookReproduction);
                var settingsPath = Path.Combine(directory, "mode_hop_settings.json"); parent.SaveSettingsTo(settingsPath);
                parent.UseModeHopExclusion = true; parent.LoadSettingsFrom(settingsPath);
                Smoke.Check(metrics, "removal_setting_roundtrip", !parent.UseModeHopExclusion && !prep.UseModeHopExclusion);
                tabs.SelectedIndex = 2; await Smoke.Settle(window);
                Smoke.Check(metrics, "sweep_checkbox_synchronized", Smoke.Find<CheckBox>(view, "SweepUseModeHopExclusion").IsChecked == false);
                prep.UseFirstRisingEdgeToRecordEnd = true; ConfigureLegacy(workspace);
                await Prepare(prep, "Ref", Path.Combine(input, "tek0002ALL.csv"));
                Smoke.Check(metrics, "ref_confirmed_raw_17M", prep.PreparedReference?.SampleCount == 17000000 && prep.PreparedReference.Metadata.ModeHopExclusionBypassed);
                JsonContractSerializer.Write(Path.Combine(directory, "reference_preparation.json"), prep.PreparedReference.Metadata);
                parent.UseModeHopExclusion = true; await Smoke.Settle(window);
                Smoke.Check(metrics, "toggle_invalidates_only_diagnosis", prep.Quality.Quality == null && prep.PreparedReference.SampleCount == 17000000);
                parent.UseModeHopExclusion = false;
                var files = new[] { "tek0002ALL.csv", "tek0003ALL.csv", "tek0004ALL.csv", "tek0005ALL.csv" };
                var labels = new[] { "Ref", "60C", "80C", "100C" };
                for (var i = 0; i < files.Length; i++)
                {
                    var label = labels[i]; File.WriteAllText(Path.Combine(directory, "progress.txt"), label + ": prepare");
                    await Prepare(prep, "Signal", Path.Combine(input, files[i]));
                    Smoke.Check(metrics, label + "_continuous_raw_17M", prep.PreparedSignal?.SampleCount == 17000000
                        && prep.PreparedSignal.Metadata.ModeHopExclusionBypassed && prep.PreparedSignal.Metadata.ExcludedRanges.Count == 0
                        && prep.PreparedSignal.OriginalIndices.Last() - prep.PreparedSignal.OriginalIndices[0] == 16999999);
                    JsonContractSerializer.Write(Path.Combine(directory, label + "_preparation.json"), prep.PreparedSignal.Metadata);
                    prep.ApplyCommand.Execute(null); ConfigureLegacy(workspace);
                    File.WriteAllText(Path.Combine(directory, "progress.txt"), label + ": legacy processing");
                    await workspace.RunProcessingAsync(); await Smoke.Settle(window);
                    var legacy = workspace.ProcessingResult;
                    Smoke.Check(metrics, label + "_legacy_full_run", legacy != null && legacy.NotebookProfile == null && !legacy.Config.NotebookReproduction && legacy.SampleCount == 17000000);
                    Export(directory, label + "_legacy", legacy);
                    Smoke.Check(metrics, label + "_preview_bounded", legacy.ReferenceRaw.Values.Length <= 10000 && legacy.SignalSpectrum.Values.Length <= 10000);
                    if (i == 0) Smoke.Check(metrics, "reference_self_zero", legacy.SegmentPeakLags.All(v => Math.Abs(v) < 1e-8));
                    workspace.FinalComparison.RecordName = label; workspace.FinalComparison.RecordCommand.Execute(null);
                    if (i > 0)
                    {
                        workspace.NotebookReproduction = true;
                        File.WriteAllText(Path.Combine(directory, "progress.txt"), label + ": notebook processing");
                        await workspace.RunProcessingAsync(); var notebook = workspace.ProcessingResult;
                        Smoke.Check(metrics, label + "_notebook_same_inputs", notebook?.NotebookProfile != null && notebook.SampleCount == 524288);
                        Export(directory, label + "_notebook", notebook);
                    }
                    GC.Collect(); GC.WaitForPendingFinalizers();
                }
                ConfigureLegacy(workspace); workspace.WorkspaceSelectedTab = 1;
                Smoke.Find<TabControl>(window, "OfdrResultTabs").SelectedIndex = 2; await Smoke.Settle(window);
                Smoke.Check(metrics, "four_legacy_curves", workspace.FinalComparison.Entries.Count == 4 && Smoke.Find<StrainComparisonPlot>(window, "FinalComparisonPlot").RenderedCurveCount == 4);
                Smoke.Check(metrics, "bindings_alive", ReferenceEquals(window.DataContext, workspace) && window.IsVisible && Smoke.BindingErrors.Count == 0);
                Smoke.Snapshot(window, directory, "legacy_final_comparison.png"); metrics.Status = "COMPLETED";
            }
            catch (Exception ex) { metrics.Error = ex + "\nPreparation: " + prep.Status + "\nProcessing: " + workspace.ProgressText; Smoke.Snapshot(window, directory, "failure.png"); }
            finally
            {
                metrics.ElapsedSeconds = watch.Elapsed.TotalSeconds; metrics.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
                JsonContractSerializer.Write(Path.Combine(directory, "metrics.json"), metrics);
                File.WriteAllText(Path.Combine(directory, "summary.md"), "# " + metrics.Status + "\n\nActual x64 WPF, shared removal checkbox OFF, raw 17M crops, Ref self and 60/80/100C legacy processing and same-input notebook comparisons. 4096-bin segment / 50-bin hop / 8x IFFT padding. Legacy 0..50 m uses provisional 200 m OPD; strain is not calibrated temperature. Phase QC failures retained.\n\n" + (metrics.Error ?? "No execution error. Numerical comparison recorded separately; execution completion is not a quality pass.") + "\n");
                Application.Current.Shutdown(metrics.Status == "COMPLETED" ? 0 : 1);
            }
        }

        private static void ConfigureLegacy(OfdrWorkspaceViewModel vm)
        {
            vm.NotebookReproduction = false; vm.AllowFailedPhaseQcForDiagnostics = true;
            vm.SegmentSize = 4096; vm.SegmentHop = 50; vm.ZeroPadFactor = 8; vm.SelectedWindowFunction = "Hann";
            vm.NormalizeCorrelation = true; vm.UsePhysicalAxes = true; vm.RestrictToMeasurementRange = true;
            vm.AnalysisStartMeters = 0; vm.AnalysisEndMeters = 50; vm.AuxiliaryPathDifferenceMeters = 200;
            vm.SamplingRateSamplesPerSecond = 100000000;
        }
        private static async Task Prepare(SweepPreparationViewModel prep, string role, string path)
        {
            await prep.LoadForRoleAsync(role, path); await prep.ReviewAsync();
            if (!prep.SetNotebookCropCommand.CanExecute(null)) throw new InvalidOperationException(prep.Status);
            prep.SetNotebookCropCommand.Execute(null); await prep.Quality.AnalyzeAsync(); await prep.ConfirmAsync();
        }
        private static void Export(string directory, string key, IndependentOfdrProcessingResult result)
        {
            JsonContractSerializer.Write(Path.Combine(directory, key + "_config.json"), result.Config);
            JsonContractSerializer.Write(Path.Combine(directory, key + "_result.json"), result);
            using (var writer = new StreamWriter(Path.Combine(directory, key + "_profile.csv")))
            {
                writer.WriteLine("profile_index,fft_bin,distance_m,lag_samples,spectral_shift_ghz,strain_microstrain,peak_magnitude");
                if (result.NotebookProfile != null)
                {
                    for (var i = 0; i < result.NotebookProfile.Positions.Length; i++)
                        writer.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},NaN,{2:R},NaN,NaN,NaN", i, result.NotebookProfile.Positions[i], result.NotebookProfile.Shifts[i]));
                }
                else for (var i = 0; i < result.SegmentStarts.Length; i++)
                    writer.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:R},{2:R},{3:R},{4:R},{5:R},{6:R}", i,
                        result.SegmentStarts[i] + (result.Config.SegmentSize - 1) / 2.0, result.SegmentCenterDistancesMeters[i], result.SegmentPeakLags[i],
                        result.SegmentPeakLags[i] * result.PhysicalAxis.SpectralShiftGHzPerLag,
                        result.Config.StrainOffsetMicrostrain + result.SegmentPeakLags[i] * result.PhysicalAxis.StrainMicrostrainPerLag, result.SegmentPeakMagnitudes[i]));
            }
        }
    }
}
