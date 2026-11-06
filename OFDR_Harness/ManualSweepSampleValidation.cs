using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;

namespace OFDR_Harness
{
    internal static class ManualSweepSampleValidation
    {
        public static int Run(IDictionary<string, string> options)
        {
            string path, root, tailText;
            if (!options.TryGetValue("input", out path)) throw new ArgumentException("--input is required.");
            if (!options.TryGetValue("results", out root)) root = Path.Combine("harness", "results");
            if (!options.TryGetValue("tail-ms", out tailText)) tailText = "0";
            var log = new SweepRunRecorder("manual_record_start_sample_validation", new Dictionary<string, string>
            {
                ["input"] = Path.GetFullPath(path), ["use_recording_start_as_sweep_start"] = "true",
                ["mode_hop_exclusion_tail_ms"] = tailText,
                ["raw_read_scope"] = "Through first CH1 rising edge only; full byte SHA-256, not full recording numerical validation.",
                ["maximum_quality_samples"] = "250000", ["full_recording_ofdr"] = "false", ["5m_golden_regression"] = "omitted by user request"
            }, root);
            var metrics = new Dictionary<string, double>(); Exception failure = null;
            try
            {
                var token = CancellationToken.None; var engine = new SweepPreparationEngine();
                var source = new TekScopeCsvReader().ReadUntilFirstSweepEnd(path, token, true); log.Source(source);
                var automatic = engine.DetectSweeps(source, token);
                var sweep = engine.DetectSweeps(source, token, true).Single();
                if (automatic.Count != 0 || !sweep.StartSpecifiedAtRecordStart || sweep.StartIndex != 0
                    || sweep.EndIndexExclusive != source.SampleCount - 1 || engine.DetectSweeps(source, token).Count != 0)
                    throw new InvalidOperationException("Manual boundary or automatic cache changed.");
                var part = engine.Slice(source, sweep, token); var detector = new ModeHopPreviewEngine();
                var maskSettings = new ModeHopSettings { ExclusionTailSeconds = double.Parse(tailText, CultureInfo.InvariantCulture) / 1000 }; maskSettings.Validate();
                var baseline = detector.ClipToSamples(detector.Detect(source, new ModeHopSettings(), token), sweep.StartIndex, sweep.EndIndexExclusive, source.SampleIntervalSeconds, token);
                var excluded = detector.ClipToSamples(detector.Detect(source, maskSettings, token), sweep.StartIndex, sweep.EndIndexExclusive, source.SampleIntervalSeconds, token);
                var quality = new AuxiliaryQualityEngine(); var stitched = quality.Stitch(part, excluded, token);
                var settings = new AuxiliaryInspectionSettings { EndSeconds = Math.Min(250000, stitched.Values.Length) * source.SampleIntervalSeconds };
                var diagnosis = quality.Analyze(stitched, settings, token);
                var prepared = engine.Prepare(source, sweep, "Ref", stitched, diagnosis.Metrics, settings, 1.8, token);
                if (prepared.SampleCount > 250000 || !prepared.Metadata.Sweep.StartSpecifiedAtRecordStart)
                    throw new InvalidOperationException("Validation scope or provenance mismatch.");
                for (var i = 0; i < prepared.SampleCount; i++)
                    if (prepared.Auxiliary[i] != source.Auxiliary[prepared.OriginalIndices[i]]
                        || prepared.Measurement[i] != source.Measurement[prepared.OriginalIndices[i]])
                        throw new InvalidOperationException("CH3/CH4 source index mismatch.");
                var preview = engine.MeasurementPreview(source, stitched, token);
                if (preview.Primary.Values.Length > 10000) throw new InvalidOperationException("Preview limit exceeded.");
                var csv = Path.Combine(log.DirectoryPath, "manual_selected.csv"); var io = new PreparedSweepCsv();
                io.Write(prepared, csv, false, token); var reloaded = io.Read(csv, "Ref", token);
                if (!reloaded.Metadata.Sweep.StartSpecifiedAtRecordStart || reloaded.SampleCount != prepared.SampleCount || reloaded.Metadata.ModeHopExclusionTailSeconds != maskSettings.ExclusionTailSeconds)
                    throw new InvalidOperationException("Manual CSV provenance was lost.");
                metrics["parsed_samples"] = source.SampleCount; metrics["sweep_start_index"] = sweep.StartIndex;
                metrics["sweep_end_exclusive"] = sweep.EndIndexExclusive; metrics["sweep_seconds"] = sweep.DurationSeconds;
                metrics["retained_sweep_samples"] = stitched.Values.Length; metrics["excluded_samples"] = excluded.ExcludedSamples;
                metrics["baseline_excluded_samples"] = baseline.ExcludedSamples; metrics["additional_excluded_samples"] = excluded.ExcludedSamples - baseline.ExcludedSamples;
                metrics["exclusion_tail_seconds"] = maskSettings.ExclusionTailSeconds;
                metrics["selected_quality_samples"] = prepared.SampleCount; metrics["nonmonotonic_ratio"] = diagnosis.Metrics.Phase.NonmonotonicRatio;
                metrics["phase_qc_passed"] = diagnosis.Metrics.Decision == "PASS_PHASE_QC" ? 1 : 0;
                metrics["measurement_preview_points"] = preview.Primary.Values.Length; metrics["manual_csv_roundtrip_passed"] = 1;
                JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "quality.json"), diagnosis.Metrics);
                Console.WriteLine("Manual S1 [0,{0}), {1:G10} s; kept {2}; diagnosed {3}: {4}", sweep.EndIndexExclusive, sweep.DurationSeconds, stitched.Values.Length, prepared.SampleCount, diagnosis.Metrics.Decision);
            }
            catch (Exception ex) { failure = ex; Console.Error.WriteLine(ex); }
            finally
            {
                log.Finish(failure, metrics);
                File.AppendAllText(Path.Combine(log.DirectoryPath, "summary.md"), "\nScope: first CH1 rising edge only; CH3 quality <=250000 samples; CSV roundtrip and CH3/CH4 alignment. Full recording, real-data OFDR correlation and 5M golden regression were not run. QC failure is recorded without relaxing criteria.\n");
                Console.WriteLine(log.DirectoryPath);
            }
            return failure == null ? 0 : 1;
        }
    }
}
