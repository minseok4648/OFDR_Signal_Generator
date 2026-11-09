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
            var timeCropSourceFlag = Array.IndexOf(args, "--time-crop-source");
            JsonContractSerializer.Write(Path.Combine(directory, "config.json"), new Dictionary<string, string> { ["fixture"] = "CH1/2/3/4 synthetic, Ref 4096 / Signal 3072; crop 65536 per sweep at 25/50/100/500 MS/s", ["full_data_validation"] = "false", ["process"] = Environment.Is64BitProcess ? "x64" : "x86",
                ["optional_real_crop_source"] = timeCropSourceFlag >= 0 && timeCropSourceFlag + 1 < args.Length ? args[timeCropSourceFlag + 1] : "none", ["real_phase_diagnosis_sample_limit"] = "100000" });
            var parent = workspace.ModeHopPreview; var vm = parent.SweepPreparation;
            try
            {
                var refPath = Fixture(directory, "ref_fixture.csv", 4096, broadbandMeasurement: true); var sigPath = Fixture(directory, "signal_fixture.csv", 3072, broadbandMeasurement: true);
                window.Show(); await Smoke.Settle(window);
                Smoke.Check(metrics, "native_x64_window", Environment.Is64BitProcess && new WindowInteropHelper(window).Handle != IntPtr.Zero && window.IsVisible);
                Smoke.Check(metrics, "normal_start_keeps_strict_qc", !workspace.AllowFailedPhaseQcForDiagnostics);
                workspace.UseSavedCsvFilesCommand.Execute(null); await Smoke.Settle(window);
                Smoke.Check(metrics, "current_state_preset_selects_diagnostic_mode", workspace.UseSavedCsvFileInputs && workspace.AllowFailedPhaseQcForDiagnostics && Smoke.Find<CheckBox>(window, "DiagnosticExecution").IsChecked == true);
                workspace.AllowFailedPhaseQcForDiagnostics = false; workspace.UseLegacyInputsCommand.Execute(null); workspace.WorkspaceSelectedTab = 0; await Smoke.Settle(window);
                var view = Smoke.Descendants(window).OfType<ModeHopPreviewView>().Single();
                var tabs = (TabControl)view.FindName("ModeHopTabs"); tabs.SelectedIndex = 2; await Smoke.Settle(window);
                var preparationView = Smoke.Descendants(view).OfType<SweepPreparationView>().Single();
                Smoke.Check(metrics, "preparation_datacontext", ReferenceEquals(preparationView.DataContext, vm));
                Smoke.Check(metrics, "ref_path_binding_active", BindingOperations.GetBindingExpression(Smoke.Find<TextBox>(preparationView, "SweepRefPath"), TextBox.TextProperty)?.Status == BindingStatus.Active);
                Smoke.Check(metrics, "missing_diagnosis_disables_apply", !vm.ApplyCommand.CanExecute(null));
                var manualFixture = Fixture(directory, "manual_record_start.csv", 4096, recordStart: true);
                await vm.LoadForRoleAsync("Ref", manualFixture); await Smoke.Settle(window);
                var manualStartBox = Smoke.Find<RadioButton>(preparationView, "SweepUseRecordingStart");
                var automaticBox = Smoke.Find<RadioButton>(preparationView, "SweepUseCompleteSweeps");
                Smoke.Check(metrics, "manual_record_start_is_opt_in", manualStartBox.IsChecked == false && vm.Sweeps.Count == 0 && !vm.ReviewCommand.CanExecute(null));
                manualStartBox.IsChecked = true; await Smoke.Settle(window);
                Smoke.Check(metrics, "manual_record_start_binding_and_range", vm.UseRecordingStartAsSweepStart && vm.Sweeps.Count == 1 && vm.SelectedSweep.StartIndex == 0 && vm.SelectedSweep.EndIndexExclusive == 4096 && vm.SelectedSweep.StartSpecifiedAtRecordStart && vm.ReviewCommand.CanExecute(null));
                await vm.ReviewAsync(); await vm.Quality.AnalyzeAsync(); await vm.ConfirmAsync(); await Smoke.Settle(window);
                Smoke.Check(metrics, "manual_record_start_review_and_confirm", vm.PreparedReference?.SampleCount == 4096 && vm.PreparedReference.Metadata.Sweep.StartSpecifiedAtRecordStart && vm.PreparedReference.OriginalIndices[0] == 0 && vm.Quality.MeasurementPlot?.Primary.Values.Length <= 10000);
                var manualCsv = Path.Combine(directory, "manual_prepared.csv"); await vm.ExportAsync("Ref", manualCsv);
                var manualConfirmed = vm.PreparedReference;
                Smoke.Snapshot(window, directory, "manual_record_start.png");
                automaticBox.IsChecked = true; await Smoke.Settle(window);
                Smoke.Check(metrics, "manual_record_start_toggle_invalidates_only_active_diagnosis", vm.Sweeps.Count == 0 && vm.Quality.Quality == null && ReferenceEquals(manualConfirmed, vm.PreparedReference));
                await vm.ImportAsync("Ref", manualCsv);
                Smoke.Check(metrics, "manual_record_start_csv_reimport", vm.PreparedReference.Metadata.Sweep.StartSpecifiedAtRecordStart && vm.PreparedReference.SampleCount == 4096);
                var endFixture = Fixture(directory, "first_edge_file_end.csv", 4096, firstEdgeOnly: true);
                await vm.LoadForRoleAsync("Ref", endFixture); await Smoke.Settle(window);
                var fileEndBox = Smoke.Find<RadioButton>(preparationView, "SweepUseFirstEdgeToEnd");
                Smoke.Check(metrics, "file_end_mode_is_opt_in", automaticBox.IsChecked == true && fileEndBox.IsChecked == false && vm.Sweeps.Count == 0);
                fileEndBox.IsChecked = true; await Smoke.Settle(window);
                Smoke.Check(metrics, "file_end_mode_binding_and_range", vm.UseFirstRisingEdgeToRecordEnd && !vm.UseRecordingStartAsSweepStart && !vm.UseCompleteSweeps && manualStartBox.IsChecked == false && automaticBox.IsChecked == false && vm.Sweeps.Count == 1 && vm.SelectedSweep.StartIndex == 5 && vm.SelectedSweep.EndIndexExclusive == 4101 && vm.SelectedSweep.EndSpecifiedAtRecordEnd);
                await vm.ReviewAsync(); await vm.Quality.AnalyzeAsync(); await vm.ConfirmAsync(); await Smoke.Settle(window);
                Smoke.Check(metrics, "file_end_review_diagnosis_and_last_sample", vm.PreparedReference?.SampleCount == 4096 && vm.PreparedReference.OriginalIndices[0] == 5 && vm.PreparedReference.OriginalIndices.Last() == 4100 && vm.Quality.MeasurementPlot.Primary.Values.Length <= 10000);
                var endCsv = Path.Combine(directory, "file_end_prepared.csv"); await vm.ExportAsync("Ref", endCsv); await vm.ImportAsync("Ref", endCsv);
                Smoke.Check(metrics, "file_end_csv_metadata_roundtrip", vm.PreparedReference.Metadata.Sweep.EndSpecifiedAtRecordEnd && vm.RefSummary.Contains("파일 끝") && vm.PreparedReference.SampleCount == 4096);
                var endConfirmed = vm.PreparedReference; Smoke.Snapshot(window, directory, "first_edge_to_file_end.png");
                manualStartBox.IsChecked = true; await Smoke.Settle(window);
                Smoke.Check(metrics, "boundary_modes_exclusive_and_diagnosis_invalidated", vm.UseRecordingStartAsSweepStart && !vm.UseFirstRisingEdgeToRecordEnd && fileEndBox.IsChecked == false && vm.Quality.Quality == null && ReferenceEquals(endConfirmed, vm.PreparedReference));
                fileEndBox.IsChecked = true; await vm.LoadForRoleAsync("Signal", sigPath); await Smoke.Settle(window);
                Smoke.Check(metrics, "file_end_range_includes_later_edges_for_signal", vm.UseFirstRisingEdgeToRecordEnd && vm.Sweeps.Count == 1 && vm.SelectedSweep.StartIndex == 5 && vm.SelectedSweep.EndIndexExclusive == 6156);
                automaticBox.IsChecked = true; await Smoke.Settle(window);
                Smoke.Check(metrics, "automatic_ranges_restore_after_file_end", vm.UseCompleteSweeps && vm.Sweeps.Count == 2 && vm.SelectedSweep.SampleCount == 3072 && !vm.SelectedSweep.EndSpecifiedAtRecordEnd);
                var noEdgeFixture = Path.Combine(directory, "no_edge.csv"); File.WriteAllText(noEdgeFixture, File.ReadAllText(endFixture).Replace(",3.4,", ",1.8,"));
                fileEndBox.IsChecked = true; await vm.LoadForRoleAsync("Ref", noEdgeFixture); await Smoke.Settle(window);
                Smoke.Check(metrics, "file_end_without_edge_disables_review", vm.Sweeps.Count == 0 && !vm.ReviewCommand.CanExecute(null) && vm.Status.Contains("상승 엣지가 없어") && ReferenceEquals(endConfirmed, vm.PreparedReference));
                automaticBox.IsChecked = true; await Smoke.Settle(window);
                var infFixture = Fixture(directory, "ch4_infinity_fixture.csv", 4096, recordStart: true, modeHopPulse: true, ch4Infinity: true);
                await vm.LoadForRoleAsync("Ref", infFixture); await Smoke.Settle(window);
                Smoke.Check(metrics, "ch4_infinity_load_and_finite_preview", parent.LoadedRecording.InvalidMeasurementCount == 3 && parent.LoadedRecording.SampleCount == 4108 && parent.Viewport.Measurement.Values.All(OFDR_Signal_Core.Models.ModeHopSettings.IsFinite) && parent.Metadata.Contains("전 채널 inf 무시 3"));
                Smoke.Check(metrics, "ch4_infinity_union_with_mode_hop", parent.AppliedExclusion.ExcludedSamples == 7 && parent.AppliedExclusion.IgnoredMeasurementSamples == 3);
                tabs.SelectedIndex = 0; await Smoke.Settle(window);
                var infRawPlot = Smoke.Descendants(view).OfType<AuxiliaryDiagnosticPlot>().First(p => p.IsVisible && ReferenceEquals(p.Plot?.Primary, parent.Viewport.Measurement));
                Smoke.Check(metrics, "ch4_infinity_raw_graph_renders", infRawPlot.ActualHeight > 0 && infRawPlot.Plot.Primary.Values.All(OFDR_Signal_Core.Models.ModeHopSettings.IsFinite));
                Smoke.Snapshot(window, directory, "ch4_inf_ignored.png"); tabs.SelectedIndex = 2; await Smoke.Settle(window);
                manualStartBox.IsChecked = true; await vm.ReviewAsync(); await vm.Quality.AnalyzeAsync(); await vm.ConfirmAsync();
                Smoke.Check(metrics, "ch4_infinity_paired_input_is_finite_and_aligned", vm.PreparedReference.SampleCount == 4089 && vm.PreparedReference.Measurement.All(OFDR_Signal_Core.Models.ModeHopSettings.IsFinite) && !vm.PreparedReference.OriginalIndices.Any(i => i == 101 || i == 200 || i == 201) && vm.PreparedReference.Metadata.IgnoredMeasurementRanges.Sum(r => r.SampleCount) == 3);
                var infCsv = Path.Combine(directory, "infinity_prepared.csv"); await vm.ExportAsync("Ref", infCsv); await vm.ImportAsync("Ref", infCsv);
                Smoke.Check(metrics, "ch4_infinity_csv_metadata_roundtrip", vm.PreparedReference.Metadata.IgnoredMeasurementRanges.Sum(r => r.SampleCount) == 3 && vm.PreparedReference.SampleCount == 4089);
                var allInfFixture = Fixture(directory, "all_channel_infinity_fixture.csv", 4096, recordStart: true, modeHopPulse: true, allChannelInfinity: true);
                await vm.LoadForRoleAsync("Ref", allInfFixture); tabs.SelectedIndex = 0; await Smoke.Settle(window);
                Smoke.Check(metrics, "all_channel_infinity_finite_preview_and_count", parent.LoadedRecording.InvalidSampleCount == 4 && parent.LoadedRecording.InvalidMeasurementCount == 2
                    && parent.Metadata.Contains("전 채널 inf 무시 4") && new[] { parent.Viewport.Auxiliary, parent.Viewport.Trigger, parent.Viewport.SweepTrigger, parent.Viewport.Measurement }.All(s => s.Values.All(OFDR_Signal_Core.Models.ModeHopSettings.IsFinite)));
                Smoke.Check(metrics, "all_channel_infinity_union_and_no_fake_sweep", parent.AppliedExclusion.ExcludedSamples == 9 && parent.AppliedExclusion.IgnoredSamples == 4
                    && vm.Sweeps.Count == 1 && vm.SelectedSweep.EndIndexExclusive == 4096);
                Smoke.Snapshot(window, directory, "all_channel_inf_ignored.png"); tabs.SelectedIndex = 2;
                await vm.ReviewAsync(); await vm.Quality.AnalyzeAsync(); await vm.ConfirmAsync(); await Smoke.Settle(window);
                Smoke.Check(metrics, "all_channel_infinity_paired_input", vm.PreparedReference.SampleCount == 4087
                    && vm.PreparedReference.Auxiliary.All(OFDR_Signal_Core.Models.ModeHopSettings.IsFinite) && vm.PreparedReference.Measurement.All(OFDR_Signal_Core.Models.ModeHopSettings.IsFinite)
                    && !vm.PreparedReference.OriginalIndices.Any(i => i >= 300 && i <= 303));
                var allInfCsv = Path.Combine(directory, "all_channel_infinity_prepared.csv"); await vm.ExportAsync("Ref", allInfCsv); await vm.ImportAsync("Ref", allInfCsv);
                Smoke.Check(metrics, "all_channel_infinity_csv_metadata", vm.PreparedReference.Metadata.IgnoredSampleRanges.Sum(r => r.SampleCount) == 4
                    && vm.PreparedReference.Metadata.IgnoredMeasurementRanges.Sum(r => r.SampleCount) == 2 && vm.PreparedReference.SampleCount == 4087);
                var removalBox = Smoke.Find<CheckBox>(preparationView, "SweepUseModeHopExclusion");
                removalBox.IsChecked = false; await Smoke.Settle(window); await parent.ApplyAsync();
                Smoke.Check(metrics, "removal_off_keeps_inf_mask", !parent.UseModeHopExclusion && parent.AppliedExclusion.ModeHopExclusionBypassed && parent.AppliedExclusion.ExcludedSamples == 4);
                await vm.ReviewAsync(); await vm.Quality.AnalyzeAsync(); await vm.ConfirmAsync();
                Smoke.Check(metrics, "removal_off_paired_indices", vm.PreparedReference.SampleCount == 4092 && vm.PreparedReference.Metadata.ModeHopExclusionBypassed
                    && vm.PreparedReference.OriginalIndices.Contains(101) && !vm.PreparedReference.OriginalIndices.Contains(300));
                var noHopCsv = Path.Combine(directory, "no_modehop_prepared.csv"); await vm.ExportAsync("Ref", noHopCsv); await vm.ImportAsync("Ref", noHopCsv);
                Smoke.Check(metrics, "removal_off_csv_metadata", vm.PreparedReference.Metadata.ModeHopExclusionBypassed && vm.PreparedReference.Metadata.IgnoredSampleRanges.Sum(r => r.SampleCount) == 4);
                removalBox.IsChecked = true; await Smoke.Settle(window); await vm.ReviewAsync(); await vm.Quality.AnalyzeAsync(); await vm.ConfirmAsync();
                Smoke.Check(metrics, "removal_on_restores_exclusions", vm.PreparedReference.SampleCount == 4087 && !vm.PreparedReference.Metadata.ModeHopExclusionBypassed);
                var finiteConfirmed = vm.PreparedReference;
                var emptyAuxFixture = Path.Combine(directory, "entire_ch3_infinity.csv"); var emptyLines = File.ReadAllLines(allInfFixture);
                var emptyHeader = Array.FindIndex(emptyLines, line => line.StartsWith("TIME,"));
                for (var row = emptyHeader + 1; row < emptyLines.Length; row++) { var cells = emptyLines[row].Split(','); cells[3] = "inf"; emptyLines[row] = string.Join(",", cells); }
                File.WriteAllLines(emptyAuxFixture, emptyLines);
                await vm.LoadForRoleAsync("Ref", emptyAuxFixture); tabs.SelectedIndex = 0; await Smoke.Settle(window);
                Smoke.Check(metrics, "all_infinity_auxiliary_graph_stays_alive", parent.Viewport.Auxiliary.Values.Length == 0 && parent.AppliedExclusion.ExcludedSamples == 4108 && window.IsVisible);
                Smoke.Snapshot(window, directory, "entire_ch3_inf.png"); tabs.SelectedIndex = 2;
                await vm.ReviewAsync(); await Smoke.Settle(window);
                Smoke.Check(metrics, "all_infinity_does_not_replace_confirmed_input", vm.Quality.Stitched == null && ReferenceEquals(finiteConfirmed, vm.PreparedReference) && !vm.ConfirmCommand.CanExecute(null));
                automaticBox.IsChecked = true;
                var pulseFixture = Fixture(directory, "pulse_tail_fixture.csv", 4096, recordStart: true, modeHopPulse: true);
                await vm.LoadForRoleAsync("Ref", pulseFixture); tabs.SelectedIndex = 0; await Smoke.Settle(window);
                var tailBox = Smoke.Find<TextBox>(view, "ModeHopExclusionTail"); tailBox.Text = "0.1";
                await parent.ApplyAsync(); await Smoke.Settle(window);
                Smoke.Check(metrics, "pulse_tail_raw_binding_and_shading", parent.ExclusionTailText == "0.1" && parent.Viewport.Ranges.Count == 1 && parent.Viewport.Ranges[0].StartIndex == 100 && parent.Viewport.Ranges[0].EndIndexExclusive == 205 && parent.AppliedExclusion.ExcludedSamples == 105);
                var tailSettings = Path.Combine(directory, "pulse_tail_settings.json"); parent.SaveSettingsTo(tailSettings);
                parent.ExclusionTailText = "0"; parent.LoadSettingsFrom(tailSettings); await Smoke.Settle(window);
                Smoke.Check(metrics, "pulse_tail_settings_roundtrip", parent.ExclusionTailText == "0.1" && parent.Summary.Contains("0.1 ms"));
                Smoke.Snapshot(window, directory, "pulse_tail_raw.png");
                tabs.SelectedIndex = 2; await Smoke.Settle(window);
                Smoke.Check(metrics, "pulse_tail_shared_between_tabs", vm.ModeHopExclusionTailText == "0.1" && Smoke.Find<TextBox>(preparationView, "SweepModeHopExclusionTail").Text == "0.1");
                manualStartBox.IsChecked = true; await vm.ReviewAsync(); await vm.Quality.AnalyzeAsync(); await vm.ConfirmAsync();
                var tailPrepared = vm.PreparedReference;
                Smoke.Check(metrics, "pulse_tail_same_ch3_ch4_mask", tailPrepared.SampleCount == 3991 && tailPrepared.Metadata.ModeHopExclusionTailSeconds == 0.0001 && !tailPrepared.OriginalIndices.Any(i => i >= 100 && i < 205) && vm.Quality.Stitched.Values.Length == 3991);
                var tailCsv = Path.Combine(directory, "tail_prepared.csv"); await vm.ExportAsync("Ref", tailCsv); await vm.ImportAsync("Ref", tailCsv);
                Smoke.Check(metrics, "pulse_tail_paired_csv_metadata_roundtrip", vm.PreparedReference.Metadata.ModeHopExclusionTailSeconds == 0.0001 && vm.PreparedReference.SampleCount == 3991);
                var tailAuxCsv = Path.Combine(directory, "tail_auxiliary.csv"); await vm.Quality.ExportCsvAsync(tailAuxCsv, false);
                Smoke.Check(metrics, "pulse_tail_auxiliary_export_and_four_file_preview", DiagnosedCsvInputReader.Preview(tailAuxCsv, "Ref").ModeHopExclusionTailSeconds == 0.0001);
                var confirmedTail = vm.PreparedReference; vm.ModeHopExclusionTailText = "0.2";
                Smoke.Check(metrics, "pulse_tail_edit_invalidates_active_diagnosis", vm.Quality.Quality == null && ReferenceEquals(confirmedTail, vm.PreparedReference));
                tabs.SelectedIndex = 0; await Smoke.Settle(window);
                var leadBox = Smoke.Find<TextBox>(view, "ModeHopExclusionLead"); leadBox.Text = "0.05"; tailBox.Text = "0.1";
                await parent.ApplyAsync(); await Smoke.Settle(window);
                Smoke.Check(metrics, "pulse_lead_raw_binding_and_shading", parent.ExclusionLeadText == "0.05"
                    && parent.Viewport.Ranges.Count == 1 && parent.Viewport.Ranges[0].StartIndex == 50
                    && parent.Viewport.Ranges[0].EndIndexExclusive == 205 && parent.AppliedExclusion.ExcludedSamples == 155);
                var leadSettings = Path.Combine(directory, "pulse_lead_tail_settings.json"); parent.SaveSettingsTo(leadSettings);
                parent.ExclusionLeadText = "0"; parent.LoadSettingsFrom(leadSettings); await Smoke.Settle(window);
                Smoke.Check(metrics, "pulse_lead_settings_roundtrip", parent.ExclusionLeadText == "0.05" && parent.ExclusionTailText == "0.1" && parent.Summary.Contains("시작 전 추가 제외 0.05 ms"));
                Smoke.Snapshot(window, directory, "pulse_lead_tail_raw.png");
                var appliedLead = parent.AppliedExclusion; leadBox.Text = "-0.1";
                var invalidLeadApplied = await parent.ApplyAsync(); await Smoke.Settle(window);
                Smoke.Check(metrics, "pulse_lead_rejects_negative_preserves_applied_mask", !invalidLeadApplied && ReferenceEquals(appliedLead, parent.AppliedExclusion));
                leadBox.Text = "0.05";
                tabs.SelectedIndex = 2; await Smoke.Settle(window);
                Smoke.Check(metrics, "pulse_lead_shared_between_tabs", vm.ModeHopExclusionLeadText == "0.05"
                    && Smoke.Find<TextBox>(preparationView, "SweepModeHopExclusionLead").Text == "0.05");
                await vm.ReviewAsync(); await vm.Quality.AnalyzeAsync(); await vm.ConfirmAsync(); await Smoke.Settle(window);
                var leadPrepared = vm.PreparedReference;
                Smoke.Check(metrics, "pulse_lead_same_ch3_ch4_mask", leadPrepared.SampleCount == 3941
                    && leadPrepared.Metadata.ModeHopExclusionLeadSeconds == 0.00005 && leadPrepared.Metadata.ModeHopExclusionTailSeconds == 0.0001
                    && !leadPrepared.OriginalIndices.Any(i => i >= 50 && i < 205) && vm.Quality.Stitched.Values.Length == 3941);
                var leadCsv = Path.Combine(directory, "lead_tail_prepared.csv"); await vm.ExportAsync("Ref", leadCsv); await vm.ImportAsync("Ref", leadCsv);
                Smoke.Check(metrics, "pulse_lead_paired_csv_metadata_roundtrip", vm.PreparedReference.Metadata.ModeHopExclusionLeadSeconds == 0.00005
                    && vm.PreparedReference.Metadata.ModeHopExclusionTailSeconds == 0.0001 && vm.PreparedReference.SampleCount == 3941);
                var leadAuxCsv = Path.Combine(directory, "lead_tail_auxiliary.csv"); await vm.Quality.ExportCsvAsync(leadAuxCsv, false);
                var leadAuxMetadata = DiagnosedCsvInputReader.Preview(leadAuxCsv, "Ref");
                Smoke.Check(metrics, "pulse_lead_auxiliary_export_and_four_file_preview", leadAuxMetadata.ModeHopExclusionLeadSeconds == 0.00005 && leadAuxMetadata.ModeHopExclusionTailSeconds == 0.0001);
                Smoke.Snapshot(window, directory, "pulse_lead_tail_prepared.png");
                var confirmedLead = vm.PreparedReference; Smoke.Find<TextBox>(preparationView, "SweepModeHopExclusionLead").Text = "0.08";
                Smoke.Check(metrics, "pulse_lead_edit_invalidates_diagnosis_preserves_confirmed", parent.ExclusionLeadText == "0.08" && vm.Quality.Quality == null && ReferenceEquals(confirmedLead, vm.PreparedReference));
                parent.ExclusionLeadText = "0";
                parent.ExclusionTailText = "0"; automaticBox.IsChecked = true;
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
                await CheckTimeCrop(window, preparationView, vm, directory, metrics);
                await vm.LoadForRoleAsync("Ref", refPath); await vm.ReviewAsync(); await vm.Quality.AnalyzeAsync(); await vm.ConfirmAsync();
                var refAuxiliaryCsv = Path.Combine(directory, "reference_auxiliary_diagnosed.csv");
                await vm.Quality.ExportCsvAsync(refAuxiliaryCsv, false);
                var previousRef = vm.PreparedReference;
                await vm.LoadForRoleAsync("Signal", sigPath); await vm.ReviewAsync(); await vm.Quality.AnalyzeAsync(); await vm.ConfirmAsync(); await Smoke.Settle(window);
                Smoke.Check(metrics, "signal_confirmed_ref_preserved", ReferenceEquals(previousRef, vm.PreparedReference) && vm.PreparedSignal?.SampleCount == 3072);
                Smoke.Check(metrics, "last_diagnosis_settings_retained", vm.PreparedSignal.Metadata.DiagnosisSettings != null);
                var sigAuxiliaryCsv = Path.Combine(directory, "signal_auxiliary_diagnosed.csv");
                await vm.Quality.ExportCsvAsync(sigAuxiliaryCsv, false);
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
                workspace.SamplingRateSamplesPerSecond = 12345;
                workspace.ConfigureDiagnosedCsvFiles(refAuxiliaryCsv, sigAuxiliaryCsv, refPath, sigPath); await Smoke.Settle(window);
                Smoke.Check(metrics, "four_file_sampling_rate_uses_metadata", workspace.SamplingRateSamplesPerSecond == 1000000);
                Smoke.Check(metrics, "four_file_paths_configured", workspace.UseSavedCsvFileInputs && !workspace.UsesLegacyChannelMapping && workspace.AuxiliaryPath == refAuxiliaryCsv && workspace.SignalAuxiliaryPath == sigAuxiliaryCsv && workspace.ReferencePath == refPath && workspace.SignalPath == sigPath);
                await workspace.RunProcessingAsync(); await Smoke.Settle(window);
                Smoke.Check(metrics, "saved_csv_four_file_analysis_completed", workspace.ProcessingResult?.SampleCount == 3072 && workspace.ProcessingResult.InputPhaseQcPassed && !workspace.ProcessingResult.IsDiagnosticAnalysis);
                Smoke.Snapshot(window, directory, "four_file_inputs.png");
                var noisyRef = Fixture(directory, "noisy_reference.csv", 4096, true); var noisySig = Fixture(directory, "noisy_signal.csv", 3072, true);
                var noisyRefAux = Path.Combine(directory, "noisy_ref_auxiliary.csv"); var noisySigAux = Path.Combine(directory, "noisy_sig_auxiliary.csv");
                await vm.LoadForRoleAsync("Ref", noisyRef); await vm.ReviewAsync(); await vm.Quality.AnalyzeAsync(); await vm.Quality.ExportCsvAsync(noisyRefAux, false);
                await vm.LoadForRoleAsync("Signal", noisySig); await vm.ReviewAsync(); await vm.Quality.AnalyzeAsync(); await vm.Quality.ExportCsvAsync(noisySigAux, false);
                workspace.ConfigureDiagnosedCsvFiles(noisyRefAux, noisySigAux, noisyRef, noisySig); await Smoke.Settle(window);
                Smoke.Check(metrics, "saved_failed_qc_blocks_normal_mode", workspace.IsProcessingQcBlocked && !workspace.RunProcessingCommand.CanExecute(null));
                var diagnosticOption = Smoke.Find<CheckBox>(window, "DiagnosticExecution"); diagnosticOption.SetCurrentValue(CheckBox.IsCheckedProperty, true); await Smoke.Settle(window);
                Smoke.Check(metrics, "explicit_diagnostic_option_enables_execution", workspace.AllowFailedPhaseQcForDiagnostics && workspace.RunProcessingCommand.CanExecute(null));
                await workspace.RunProcessingAsync(); await Smoke.Settle(window);
                Smoke.Check(metrics, "diagnostic_analysis_keeps_rejected_qc", workspace.ProcessingResult?.IsDiagnosticAnalysis == true && !workspace.ProcessingResult.InputPhaseQcPassed && workspace.ProcessingResult.KLinearization.DiagnosticOnly && workspace.ProcessingResult.SignalKLinearization.DiagnosticOnly);
                Smoke.Check(metrics, "diagnostic_result_is_labeled", Smoke.Find<TextBlock>(window, "AnalysisPurpose").Text.Contains("진단용") && workspace.ProgressText.Contains("진단 처리 완료"));
                Smoke.Check(metrics, "diagnostic_banner_visible_above_plots", Smoke.Find<TextBlock>(window, "ResultPurposeBanner").IsVisible && Smoke.Find<TextBlock>(window, "ResultPurposeBanner").Text.Contains("진단용"));
                Smoke.Snapshot(window, directory, "diagnostic_four_file_analysis.png");
                diagnosticOption.SetCurrentValue(CheckBox.IsCheckedProperty, false); await Smoke.Settle(window);
                Smoke.Check(metrics, "normal_qc_restored_when_diagnostic_off", workspace.IsProcessingQcBlocked && workspace.ProcessingResult.IsDiagnosticAnalysis && workspace.AnalysisPurposeSummary.Contains("진단용"));
                var comparison = workspace.FinalComparison;
                Smoke.Check(metrics, "comparison_rejects_nonphysical_result", !comparison.RecordCommand.CanExecute(null) && comparison.Entries.Count == 0);
                workspace.UsePhysicalAxes = true; workspace.RestrictToMeasurementRange = false;
                workspace.ConfigureDiagnosedCsvFiles(refAuxiliaryCsv, sigAuxiliaryCsv, refPath, sigPath);
                await workspace.RunProcessingAsync(); await Smoke.Settle(window);
                workspace.WorkspaceSelectedTab = 1;
                var resultTabs = Smoke.Find<TabControl>(window, "OfdrResultTabs"); resultTabs.SelectedIndex = 2; await Smoke.Settle(window);
                var comparisonPlot = Smoke.Find<StrainComparisonPlot>(window, "FinalComparisonPlot");
                Smoke.Check(metrics, "comparison_tab_binding", comparisonPlot.IsVisible && ReferenceEquals(Smoke.Find<Button>(window, "ComparisonRecord").Command, comparison.RecordCommand));
                comparison.RecordName = "Ref / Signal"; comparison.RecordCommand.Execute(null); await Smoke.Settle(window);
                var firstComparison = comparison.Entries.Single().Snapshot; var firstValues = firstComparison.StrainMicrostrain.ToArray();
                Smoke.Check(metrics, "comparison_records_completed_strain", comparisonPlot.RenderedCurveCount == 1 && firstComparison.SourceSegmentCount == workspace.ProcessingResult.SegmentPeakLags.Length && firstComparison.DistancesMeters.Length <= 10000);
                workspace.ConfigureDiagnosedCsvFiles(refAuxiliaryCsv, refAuxiliaryCsv, refPath, refPath);
                await workspace.RunProcessingAsync(); await Smoke.Settle(window);
                Smoke.Check(metrics, "comparison_survives_next_analysis_without_auto_record", comparison.Entries.Count == 1 && firstValues.SequenceEqual(firstComparison.StrainMicrostrain));
                comparison.RecordName = "Ref / Ref"; comparison.RecordCommand.Execute(null); await Smoke.Settle(window);
                Smoke.Check(metrics, "comparison_overlays_ref_ref_and_ref_signal", comparisonPlot.RenderedCurveCount == 2 && comparison.Entries[0].Signal != comparison.Entries[1].Signal && comparison.Entries[1].Snapshot.StrainMicrostrain.All(v => Math.Abs(v) < 1e-7));
                workspace.ConfigureDiagnosedCsvFiles(noisyRefAux, noisySigAux, noisyRef, noisySig); workspace.AllowFailedPhaseQcForDiagnostics = true;
                await workspace.RunProcessingAsync(); comparison.RecordName = "QC diagnostic"; comparison.RecordCommand.Execute(null); await Smoke.Settle(window);
                Smoke.Check(metrics, "comparison_keeps_per_curve_failed_qc", comparison.Entries.Count == 3 && comparison.Entries[2].Snapshot.IsDiagnosticAnalysis && !comparison.Entries[2].Snapshot.InputPhaseQcPassed && comparison.Entries[2].Quality.Contains("불합격") && comparison.Entries[0].Quality.Contains("통과"));
                var displayBoxes = Enumerable.Range(1, 3).Select(i => Smoke.Find<CheckBox>(window, "ComparisonDisplayEnable_" + i)).ToArray();
                Smoke.Check(metrics, "comparison_display_enabled_by_default", comparison.Entries.All(e => e.DisplayEnabled) && displayBoxes.All(b => b.IsEnabled && b.IsChecked == true));
                Smoke.Check(metrics, "comparison_display_checkbox_binding", displayBoxes.All(b => BindingOperations.GetBindingExpression(b, CheckBox.IsCheckedProperty)?.Status == BindingStatus.Active));
                displayBoxes[0].SetCurrentValue(CheckBox.IsCheckedProperty, false); await Smoke.Settle(window);
                Smoke.Check(metrics, "comparison_hides_unchecked_curve_without_deleting", !comparison.Entries[0].DisplayEnabled && comparison.Entries.Count == 3 && comparisonPlot.RenderedCurveCount == 2 && firstValues.SequenceEqual(firstComparison.StrainMicrostrain) && comparison.CountDescription.Contains("표시 2개"));
                displayBoxes[1].SetCurrentValue(CheckBox.IsCheckedProperty, false); displayBoxes[2].SetCurrentValue(CheckBox.IsCheckedProperty, false); await Smoke.Settle(window);
                Smoke.Check(metrics, "comparison_all_hidden_preserves_records", comparisonPlot.RenderedCurveCount == 0 && comparison.Entries.Count == 3 && comparison.ClearCommand.CanExecute(null) && comparison.CountDescription.Contains("표시 0개"));
                Smoke.Snapshot(window, directory, "comparison_all_hidden.png");
                displayBoxes[0].SetCurrentValue(CheckBox.IsCheckedProperty, true); await Smoke.Settle(window);
                Smoke.Check(metrics, "comparison_reenable_restores_only_selected_curve", comparisonPlot.RenderedCurveCount == 1 && comparison.Entries[0].DisplayEnabled && !comparison.Entries[1].DisplayEnabled && !comparison.Entries[2].DisplayEnabled && firstValues.SequenceEqual(firstComparison.StrainMicrostrain));
                Smoke.Snapshot(window, directory, "comparison_display_selected.png");
                Smoke.Check(metrics, "comparison_wheel_zoom", comparisonPlot.ZoomAt(Smoke.InArea(comparisonPlot.InteractionArea, 0.5, 0.5), 120));
                Smoke.Check(metrics, "comparison_drag_zoom", comparisonPlot.ZoomSelection(Smoke.InArea(comparisonPlot.InteractionArea, 0.2, 0.2), Smoke.InArea(comparisonPlot.InteractionArea, 0.8, 0.8)));
                comparisonPlot.ResetZoom(); await Smoke.Settle(window); Smoke.Snapshot(window, directory, "final_strain_comparison.png");
                foreach (var entry in comparison.Entries) entry.DisplayEnabled = true;
                await Smoke.Settle(window);
                Smoke.Check(metrics, "comparison_display_model_updates_checkboxes", comparisonPlot.RenderedCurveCount == 3 && displayBoxes.All(b => b.IsChecked == true));
                JsonContractSerializer.Write(Path.Combine(directory, "comparison_snapshots.json"), comparison.Entries.Select(e => e.Snapshot).ToArray());
                var currentResult = workspace.ProcessingResult;
                comparison.RecordCommand.Execute(null); await Smoke.Settle(window);
                Smoke.Check(metrics, "comparison_repeated_record_appends", comparison.Entries.Count == 4 && comparisonPlot.RenderedCurveCount == 4);
                comparison.Entries[0].DisplayEnabled = false; comparison.RecordCommand.Execute(null); await Smoke.Settle(window);
                Smoke.Check(metrics, "comparison_new_record_does_not_reset_hidden_selection", comparison.Entries.Count == 5 && comparisonPlot.RenderedCurveCount == 4 && !comparison.Entries[0].DisplayEnabled && comparison.Entries[4].DisplayEnabled);
                var hiddenEntry = comparison.Entries[0]; var preservedColor = comparison.Entries[1].LineBrush;
                var deleteButton = Smoke.Find<Button>(window, "ComparisonDelete_1");
                Smoke.Check(metrics, "comparison_delete_button_bound", ReferenceEquals(deleteButton.Command, hiddenEntry.DeleteCommand));
                deleteButton.Command.Execute(null); await Smoke.Settle(window);
                Smoke.Check(metrics, "comparison_deletes_hidden_record_only", comparison.Entries.Count == 4 && comparisonPlot.RenderedCurveCount == 4
                    && comparison.Entries[0].Number == 2 && ReferenceEquals(preservedColor, comparison.Entries[0].LineBrush) && !hiddenEntry.DeleteCommand.CanExecute(null));
                comparison.Entries[0].DeleteCommand.Execute(null); await Smoke.Settle(window);
                Smoke.Check(metrics, "comparison_deletes_visible_curve", comparison.Entries.Count == 3 && comparisonPlot.RenderedCurveCount == 3 && ReferenceEquals(currentResult, workspace.ProcessingResult));
                for (var i = 0; i < 15; i++) comparison.RecordCommand.Execute(null);
                Smoke.Check(metrics, "strain_colors_unique_beyond_palette", comparison.Entries.Select(e => e.LineBrush.ToString()).Distinct().Count() == comparison.Entries.Count);
                Smoke.Find<Button>(window, "ComparisonClear").Command.Execute(null); await Smoke.Settle(window);
                Smoke.Check(metrics, "comparison_clear_only_removes_records", comparison.Entries.Count == 0 && comparisonPlot.RenderedCurveCount == 0 && !comparison.ClearCommand.CanExecute(null) && ReferenceEquals(currentResult, workspace.ProcessingResult) && comparison.RecordCommand.CanExecute(null));
                comparison.RecordCommand.Execute(null); await Smoke.Settle(window);
                Smoke.Check(metrics, "comparison_record_after_clear_restarts_number", comparison.Entries.Single().Number == 1 && comparisonPlot.RenderedCurveCount == 1);
                comparison.ClearCommand.Execute(null);
                workspace.WorkspaceSelectedTab = 1;
                Smoke.Find<TabControl>(window, "AlgorithmInputTabs").SelectedIndex = 1; await Smoke.Settle(window);
                  Smoke.Check(metrics, "stage_options_disabled_when_off", !Smoke.Find<ComboBox>(window, "StageGrid").IsEnabled);
                Smoke.Find<Button>(window, "StageNotebookPreset").Command.Execute(null); await Smoke.Settle(window);
                Smoke.Check(metrics, "stage_preset_bindings", workspace.StageComparisonEnabled && !workspace.NotebookReproduction && !workspace.UsePhysicalAxes
                      && workspace.Stages.GridIndex == 1 && Smoke.Find<CheckBox>(window, "StageComparisonEnabled").IsChecked == true && Smoke.Find<ComboBox>(window, "StageGrid").IsEnabled);
                workspace.Stages.OutputSamples = 4096; workspace.SegmentSize = 128; workspace.SegmentHop = 32;
                await workspace.RunProcessingAsync(); await Smoke.Settle(window);
                  Smoke.Check(metrics, "stage_notebook_combo_processed", workspace.ProcessingResult?.Config.StageComparisonEnabled == true && workspace.ProcessingResult.NotebookProfile != null && workspace.ProcessingResult.StrainProfile == null
                      && workspace.ReferencePhaseIncrementSummary.Contains("미산출"));
                comparison.RecordName = "단계: 독립 / 전체 Hann"; comparison.RecordCommand.Execute(null);
                var phaseBox = Smoke.Find<ComboBox>(window, "StagePhaseOrder"); var gridBox = Smoke.Find<ComboBox>(window, "StageGrid"); var windowBox = Smoke.Find<ComboBox>(window, "StageWindow");
                Smoke.Find<CheckBox>(window, "StageRemoveAuxMean").IsChecked = true;
                phaseBox.SelectedIndex = 0; gridBox.SelectedIndex = 2; windowBox.SelectedIndex = 0; await Smoke.Settle(window);
                Smoke.Check(metrics, "stage_selectors_bound", workspace.Stages.RemoveAuxiliaryMean && workspace.Stages.PhaseOrderIndex == 0 && workspace.Stages.GridIndex == 2 && workspace.Stages.WindowIndex == 0);
                await workspace.RunProcessingAsync(); await Smoke.Settle(window);
                Smoke.Check(metrics, "stage_changed_pipeline_processed", workspace.ProcessingResult?.Config.StageComparison.Grid == OFDR_Signal_Core.Models.RelativePhaseGrid.CommonSpanTwoPass
                    && workspace.ProcessingResult.Config.StageComparison.WindowPlacement == OFDR_Signal_Core.Models.DiagnosticWindowPlacement.DistanceSegment);
                workspace.SelectedSegmentIndex = 20; workspace.InspectSegmentCommand.Execute(null);
                Smoke.Check(metrics, "stage_inspector_matches_result", Math.Abs(workspace.SelectedSegment.PeakLagBins - workspace.ProcessingResult.SegmentPeakLags[20]) < 1e-8);
                comparison.RecordName = "단계: 공통 / 국부 Hann"; comparison.RecordCommand.Execute(null); await Smoke.Settle(window);
                Smoke.Check(metrics, "stage_record_keeps_conditions", comparison.Entries.Count == 3 && comparison.Entries.Last().ProcessingConditions.Contains("CommonSpanTwoPass")
                      && comparison.Entries[1].ProcessingConditions.Contains("IndependentEndpoints") && comparison.Entries.Last().ProcessingConditions.Contains("hop=32")
                      && comparison.Entries.Last().Quality.Contains("QC 불합격") && !comparison.XAxisTitle.Contains("× 50"));
                Smoke.Snapshot(window, directory, "stage_selectors.png");
                var baseline = comparison.Entries.First(e => e.IsReference); baseline.DeleteCommand.Execute(null);
                for (var i = 0; i < 18; i++) comparison.RecordCommand.Execute(null);
                await Smoke.Settle(window);
                Smoke.Check(metrics, "notebook_colors_unique_and_deleted_baseline_stays_deleted", comparison.Entries.Count == 20
                    && comparison.Entries.All(e => !e.IsReference) && comparison.Entries.Select(e => e.LineBrush.ToString()).Distinct().Count() == 20
                    && comparison.Entries.Select(e => e.Number).Distinct().Count() == 20);
                Smoke.Snapshot(window, directory, "unique_comparison_colors.png");
                foreach (var entry in comparison.Entries.ToArray()) entry.DeleteCommand.Execute(null);
                await Smoke.Settle(window);
                Smoke.Check(metrics, "delete_all_records_clears_plot", comparison.Entries.Count == 0 && comparisonPlot.RenderedCurveCount == 0 && !comparison.ClearCommand.CanExecute(null));
                comparison.RecordCommand.Execute(null);
                Smoke.Check(metrics, "record_after_delete_has_new_number", comparison.Entries.Single().Number == 22);
                comparison.ClearCommand.Execute(null); comparison.RecordCommand.Execute(null);
                Smoke.Check(metrics, "clear_resets_reference_baseline", comparison.Entries.Count == 2 && comparison.Entries[0].IsReference && comparison.Entries[0].Number == 1);
                var priorStage = workspace.ProcessingResult; workspace.Stages.OutputSamples = 524288;
                var cancellingStage = workspace.RunProcessingAsync(); await workspace.CancelAndWaitAsync(); await cancellingStage;
                Smoke.Check(metrics, "stage_cancel_keeps_completed_result", !workspace.IsBusy && ReferenceEquals(priorStage, workspace.ProcessingResult));
                workspace.NotebookReproduction = true;
                  await Smoke.Settle(window);
                Smoke.Check(metrics, "stage_and_fixed_notebook_are_exclusive", !workspace.StageComparisonEnabled && workspace.NotebookReproduction);
                  Smoke.Check(metrics, "stage_options_disabled_after_fixed_preset", !Smoke.Find<ComboBox>(window, "StageGrid").IsEnabled);
                workspace.NotebookReproduction = false;
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
                File.WriteAllText(Path.Combine(directory, "summary.md"), "# Four-channel WPF smoke\n\nStatus: " + metrics.Status + "\nChecks: " + metrics.Checks.Count + "\nBinding errors: " + Smoke.BindingErrors.Count + "\nError: " + (metrics.Error ?? "none") + "\n\nSynthetic fixtures plus optional short real CSV (see config; phase diagnosis limited to 100,000 samples). Actual x64 WPF startup, bindings, commands, plots, pairing, crop, comparison deletion/colors, save/reload and cancellation exercised. OS file-dialog navigation and full real-data processing were not tested.\n");
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
        private static async Task CheckTimeCrop(MainWindow window, SweepPreparationView view, SweepPreparationViewModel vm, string directory, Smoke.SmokeMetrics metrics)
        {
            var offset = Smoke.Find<TextBox>(view, "SweepCropOffset"); var length = Smoke.Find<TextBox>(view, "SweepCropLength");
            Smoke.Check(metrics, "time_crop_controls_bound_and_old_preset_present", BindingOperations.GetBindingExpression(offset, TextBox.TextProperty)?.Status == BindingStatus.Active
                && ReferenceEquals(Smoke.Find<Button>(view, "SweepTimeCrop").Command, vm.SetTimeCropCommand)
                && ReferenceEquals(Smoke.Find<Button>(view, "SweepNotebookCrop").Command, vm.SetNotebookCropCommand));
            var prior = vm.PreparedReference; var priorDiagnosis = vm.Quality.Quality;
            offset.SetCurrentValue(TextBox.TextProperty, "0.1"); length.SetCurrentValue(TextBox.TextProperty, "170"); await vm.SetTimeCropAsync();
            Smoke.Check(metrics, "overrun_crop_preserves_diagnosis_and_confirmed_input", ReferenceEquals(prior, vm.PreparedReference)
                && ReferenceEquals(priorDiagnosis, vm.Quality.Quality) && vm.Status.Contains("사용 가능 길이"));
            foreach (var rate in new[] { 25000000, 50000000, 100000000, 500000000 })
            {
                var path = Fixture(directory, "crop_" + rate + ".csv", 65536, sampleInterval: 1.0 / rate);
                await vm.LoadForRoleAsync("Ref", path); await vm.ReviewAsync();
                offset.SetCurrentValue(TextBox.TextProperty, "0.01"); length.SetCurrentValue(TextBox.TextProperty, "0.04");
                await vm.SetTimeCropAsync();
                Smoke.Check(metrics, "time_crop_requires_new_diagnosis_" + rate, vm.Quality.Quality == null && !vm.ConfirmCommand.CanExecute(null));
                await vm.Quality.AnalyzeAsync(); await vm.ConfirmAsync();
                var expectedStart = 5 + rate / 100000; var count = rate / 25000;
                Smoke.Check(metrics, "crop_sampling_rate_and_pairing_" + rate, vm.PreparedReference.SampleCount == count
                    && vm.PreparedReference.OriginalIndices[0] == expectedStart && vm.PreparedReference.OriginalIndices.Last() == expectedStart + count - 1);
            }
            var maskPath = Fixture(directory, "crop_modehop.csv", 4096, modeHopPulse: true);
            await vm.LoadForRoleAsync("Ref", maskPath); await vm.ReviewAsync();
            vm.CropOffsetText = "0.09"; vm.CropLengthText = "0.03"; await vm.SetTimeCropAsync();
            await vm.Quality.AnalyzeAsync(); await vm.ConfirmAsync();
            Smoke.Check(metrics, "crop_raw_time_with_exclusion_keeps_paired_indices", vm.PreparedReference.SampleCount == 25
                && vm.PreparedReference.OriginalIndices.SequenceEqual(Enumerable.Range(95, 30).Where(i => i < 100 || i >= 105)));
            vm.CropOffsetText = "0"; vm.CropLengthText = ""; await vm.SetTimeCropAsync();
            Smoke.Check(metrics, "blank_crop_length_reaches_end", Math.Abs(double.Parse(vm.Quality.EndText, CultureInfo.InvariantCulture) - 4.091) < 1e-10);
            var beforePreset = vm.Quality.EndText; vm.SetNotebookCropCommand.Execute(null);
            Smoke.Check(metrics, "fixed_261106_crop_still_requires_17m", vm.Status.Contains("261106 crop을 만들 수 없습니다") && vm.Quality.EndText == beforePreset);
            Smoke.Snapshot(window, directory, "time_crop_controls.png");
            var args = Environment.GetCommandLineArgs(); var realFlag = Array.IndexOf(args, "--time-crop-source");
            if (realFlag >= 0 && realFlag + 1 < args.Length)
            {
                vm.UseFirstRisingEdgeToRecordEnd = true; vm.UseModeHopExclusion = false;
                await vm.LoadForRoleAsync("Ref", args[realFlag + 1]); await vm.ReviewAsync();
                vm.CropOffsetText = "0.15"; vm.CropLengthText = "2"; await vm.SetTimeCropAsync();
                await vm.Quality.AnalyzeAsync(); await vm.ConfirmAsync();
                var metadata = vm.PreparedReference.Metadata;
                Smoke.Check(metrics, "real_short_record_50msps_crop_100k", vm.PreparedReference.SampleCount == 100000
                    && vm.PreparedReference.OriginalIndices[0] == vm.SelectedSweep.StartIndex + 7500
                    && metadata.SourcePath == args[realFlag + 1]);
                JsonContractSerializer.Write(Path.Combine(directory, "real_time_crop_preparation.json"), metadata);
                var realCropCsv = Path.Combine(directory, "real_time_crop.csv"); await vm.ExportAsync("Ref", realCropCsv); await vm.ImportAsync("Ref", realCropCsv);
                Smoke.Check(metrics, "real_time_crop_csv_roundtrip", vm.PreparedReference.SampleCount == 100000
                    && vm.PreparedReference.Metadata.SourceSha256 == metadata.SourceSha256 && vm.PreparedReference.OriginalIndices[0] == vm.SelectedSweep.StartIndex + 7500);
                Smoke.Snapshot(window, directory, "real_short_record_time_crop.png");
                vm.UseCompleteSweeps = true; vm.UseModeHopExclusion = true;
            }
        }
        private static string Fixture(string folder, string name, int period, bool nonmonotonic = false, bool recordStart = false, bool modeHopPulse = false, bool broadbandMeasurement = false, bool ch4Infinity = false, bool firstEdgeOnly = false, bool allChannelInfinity = false, double sampleInterval = 1e-6)
        {
            var path = Path.Combine(folder, name);
            var count = firstEdgeOnly ? period + 5 : recordStart ? period + 12 : period * 2 + 12;
            using (var writer = new StreamWriter(path))
            {
                uint randomState = 137;
                writer.WriteLine("Model,DPO4104B\nSample Interval," + sampleInterval.ToString("G17", CultureInfo.InvariantCulture) + "\nRecord Length," + count + "\nVertical Units,V,V,V,V\nHorizontal Units,s\nTIME,CH1,CH2,CH3,CH4");
                for (var i = 0; i < count; i++)
                {
                    var j = ((i - (recordStart ? 0 : 5)) % period + period) % period;
                    var trigger = (firstEdgeOnly ? i == 5 : recordStart ? i < 2 || i == period : i == 5 || i == 5 + period || i == 5 + 2 * period) ? "3.4" : "0.1";
                    var phase = 2 * Math.PI * 32 * j / period;
                    var modeHop = modeHopPulse && i >= 100 && i < 105 ? "3.4" : "0";
                    randomState = unchecked(randomState * 1664525 + 1013904223);
                    var measurement = broadbandMeasurement ? randomState / (double)uint.MaxValue - 0.5 : Math.Sin(2 * Math.PI * 64 * j / period);
                    var measurementText = ch4Infinity && (i == 101 || i == 200 || i == 201) ? i == 200 ? "-inf" : "inf" : measurement.ToString("G17", CultureInfo.InvariantCulture);
                    var auxiliaryText = (Math.Cos(phase) + (nonmonotonic ? .6 * Math.Cos(3 * phase) : 0)).ToString("G17", CultureInfo.InvariantCulture);
                    if (allChannelInfinity)
                    {
                        if (i == 300) trigger = "+inf";
                        if (i == 301) modeHop = "-inf";
                        if (i == 302) auxiliaryText = "Infinity";
                        if (i == 300 || i == 303) measurementText = "-Infinity";
                    }
                    writer.WriteLine((-2 + i * sampleInterval).ToString("F3", CultureInfo.InvariantCulture) + "," + trigger + "," + modeHop + "," + auxiliaryText + "," + measurementText);
                }
            }
            return path;
        }
    }
}
