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
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;
using OFDR_Signal_Generator.Controls;
using OFDR_Signal_Generator.ViewModels;
using Smoke = OFDR_Signal_Generator.Validation.ModeHopGuiSmoke;

namespace OFDR_Signal_Generator.Validation
{
    internal static class IndexedCsvGuiSmoke
    {
        public static async Task RunAsync(MainWindow window, OfdrWorkspaceViewModel workspace)
        {
            var args = Environment.GetCommandLineArgs(); var flag = Array.IndexOf(args, "--indexed-csv-smoke");
            var directory = Path.GetFullPath(Path.Combine(args[flag + 1], DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_indexed_csv_gui"));
            Directory.CreateDirectory(Path.Combine(directory, "artifacts"));
            var metrics = new Smoke.SmokeMetrics { Status = "FAILED", Checks = new Dictionary<string, bool>(), BindingErrors = Smoke.BindingErrors };
            var watch = Stopwatch.StartNew();
            JsonContractSerializer.Write(Path.Combine(directory, "config.json"), new Dictionary<string, string> { ["source"] = args[flag + 2],
                ["scope"] = "Full indexed CSV -> mode-hop GUI -> single-file B/C reflectogram; physical CH1 sweep / CH2 mode-hop / CH3 AUX / CH4 measurement" });
            try {
                window.Show(); await Smoke.Settle(window);
                Smoke.Check(metrics, "x64_main_window_live", Environment.Is64BitProcess && window.IsVisible && new WindowInteropHelper(window).Handle != IntPtr.Zero);
                Smoke.Check(metrics, "native_fft_available", new FftEngine().NativeProviderAvailable);
                window.WorkspaceTabs.SelectedIndex = 0;
                await workspace.ModeHopPreview.LoadAsync(args[flag + 2]); await Smoke.Settle(window);
                var raw = workspace.ModeHopPreview.LoadedRecording;
                Smoke.Check(metrics, "full_record_loaded", raw != null && !raw.IsPartialRead && raw.HasFourChannels);
                JsonContractSerializer.Write(Path.Combine(directory, "source_header.json"), raw.Header);
                JsonContractSerializer.Write(Path.Combine(directory, "source_manifest.json"), new Dictionary<string, string> { ["path"] = raw.SourcePath,
                    ["sha256"] = raw.Sha256, ["samples"] = raw.SampleCount.ToString(), ["dt"] = raw.SampleIntervalSeconds.ToString("R", CultureInfo.InvariantCulture),
                    ["start_seconds"] = raw.SourceStartSeconds.ToString("R", CultureInfo.InvariantCulture), ["modehop_result_directory"] = workspace.ModeHopPreview.ResultDirectory });
                var stats = new Dictionary<string, Dictionary<string, double>>();
                var arrays = new[] { raw.SweepTrigger, raw.Trigger, raw.Auxiliary, raw.Measurement };
                for (var ch = 0; ch < arrays.Length; ch++) {
                    double lo = double.PositiveInfinity, hi = double.NegativeInfinity, sum = 0, energy = 0; long finite = 0, high = 0;
                    foreach (var value in arrays[ch]) if (ModeHopSettings.IsFinite(value)) { lo = Math.Min(lo, value); hi = Math.Max(hi, value); sum += value; energy += value * value; finite++; if (value > 1.8) high++; }
                    stats["CH" + (ch + 1)] = new Dictionary<string, double> { ["min"] = lo, ["max"] = hi, ["mean"] = sum / finite, ["rms"] = Math.Sqrt(energy / finite), ["finite_samples"] = finite, ["above_1_8V_samples"] = high };
                }
                JsonContractSerializer.Write(Path.Combine(directory, "channel_statistics.json"), stats);
                Smoke.Check(metrics, "actual_twenty_million_samples", raw.SampleCount == 20000000);
                Smoke.Check(metrics, "actual_ten_msps_two_seconds", Math.Abs(raw.SampleIntervalSeconds - 1e-7) < 1e-16 && Math.Abs(raw.DurationSeconds - 2) < 1e-8);
                Smoke.Check(metrics, "channel_aliases_did_not_remap_wiring", raw.SweepTrigger[0] == .2 && raw.Trigger[0] == .12 && raw.Auxiliary[0] == .12 && raw.Measurement[0] == -1.36);
                Smoke.Check(metrics, "metadata_format_visible", workspace.ModeHopPreview.Metadata.Contains("Indexed four-channel") && workspace.ModeHopPreview.Metadata.Contains("10 MS/s"));
                var preview = workspace.ModeHopPreview.Viewport;
                Smoke.Check(metrics, "all_four_previews_bounded", new[] { preview.SweepTrigger, preview.Trigger, preview.Auxiliary, preview.Measurement }.All(p => p != null && p.Values.Length <= 10000));
                var overlay = Smoke.Find<ModeHopOverlayPlot>(window, "ModeHopPlot");
                Smoke.Check(metrics, "runtime_preview_binding", BindingOperations.GetBindingExpression(overlay, ModeHopOverlayPlot.ViewportProperty)?.Status == BindingStatus.Active);
                Smoke.Snapshot(window, directory, "indexed_full_record_preview.png");
                var reflectogram = workspace.Reflectogram; reflectogram.ReferencePath = args[flag + 2]; reflectogram.SignalPath = "";
                reflectogram.ResultRoot = Path.Combine(directory, "processing");
                window.WorkspaceTabs.SelectedIndex = 2; await Smoke.Settle(window);
                Smoke.Check(metrics, "single_file_run_enabled", reflectogram.RunCommand.CanExecute(null));
                await reflectogram.RunAsync(); await Smoke.Settle(window);
                Smoke.Check(metrics, "full_reflectogram_completed", reflectogram.RunStatus == "COMPLETED" && reflectogram.ReferenceResult != null);
                var result = reflectogram.ReferenceResult;
                Smoke.Check(metrics, "same_full_input_sha256", result.SampleCount == raw.SampleCount && result.Sha256 == raw.Sha256);
                Smoke.Check(metrics, "nyquist_band_recorded", Math.Abs(result.AppliedBandpassHighMHz - 4.5) < 1e-9 && reflectogram.Summary.Contains("4.5MHz"));
                Smoke.Check(metrics, "single_file_has_no_fake_signal", !reflectogram.HasSignalResult && reflectogram.SignalPlot == null);
                var plot = Smoke.Find<SignalPlot>(window, "ReflectogramRefPlot"); plot.BringIntoView(); await Smoke.Settle(window);
                Smoke.Check(metrics, "reflectogram_binding_active", ReferenceEquals(plot.FftSource, reflectogram.ReferencePlot) && plot.DisplayedFft != null && plot.DisplayedFft.Bins.Length <= 10000);
                Smoke.Check(metrics, "density_csv_saved", File.Exists(Path.Combine(reflectogram.ResultDirectory, "reference", "power_density.csv")));
                Smoke.Check(metrics, "numeric_distance_zoom", plot.SetFftRange(0, 1)); await Smoke.Settle(window);
                Smoke.Check(metrics, "zoom_uses_all_grid_points", !plot.DisplayedFft.IsReduced && plot.DisplayedFft.Bins.Length >= 1001);
                plot.ResetZoom(); await Smoke.Settle(window); Smoke.Snapshot(window, directory, "indexed_reflectogram.png");
                Smoke.Check(metrics, "runtime_binding_errors_zero", Smoke.BindingErrors.Count == 0);
                Smoke.Check(metrics, "window_stays_connected", window.IsVisible && ReferenceEquals(window.DataContext, workspace));
                metrics.Status = "COMPLETED";
            }
            catch (Exception ex) { metrics.Error = ex.ToString(); }
            finally {
                metrics.ElapsedSeconds = watch.Elapsed.TotalSeconds; metrics.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
                JsonContractSerializer.Write(Path.Combine(directory, "metrics.json"), metrics);
                File.WriteAllText(Path.Combine(directory, "summary.md"), "# Indexed four-channel CSV GUI validation\n\nStatus: " + metrics.Status + "\n\nActual WPF startup, complete 20M input read with header/time/index validation, bounded four-channel preview, full single-file B/C reflectogram and actual sampling-rate-aware filter. Phase QC is retained separately; successful format processing is not absolute scientific calibration.\n\n" + metrics.Error);
                Application.Current.Shutdown(metrics.Status == "COMPLETED" ? 0 : 1);
            }
        }
    }
}
