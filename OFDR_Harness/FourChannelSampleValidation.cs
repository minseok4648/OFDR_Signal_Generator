using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;

namespace OFDR_Harness
{
    internal static class FourChannelSampleValidation
    {
        public static int Run(IDictionary<string, string> options)
        {
            var root = Get(options, "results", Path.Combine("harness", "results"));
            var folder = @"C:\Project\OFDR\Datas\OFDR Raw data\NoticeData";
            var okay = Validate(Get(options, "ref", Path.Combine(folder, "tek0003ALL.csv")), "Ref", 2545198, root);
            GC.Collect(); GC.WaitForPendingFinalizers();
            okay &= Validate(Get(options, "sig", Path.Combine(folder, "tek0005ALL.csv")), "Signal", 2545199, root);
            return okay ? 0 : 1;
        }
        private static bool Validate(string path, string role, int expected, string root)
        {
            var log = new SweepRunRecorder("four_channel_sample_validation", new Dictionary<string, string> { ["role"] = role, ["input"] = path,
                ["raw_read_scope"] = "Through second CH1 rising edge only; SHA-256 covers full file bytes, not full numerical validation.",
                ["maximum_quality_samples"] = "250000", ["full_10m_validation"] = "false", ["5m_golden_regression"] = "omitted by user request" }, root);
            var metrics = new Dictionary<string, double>(); Exception failure = null; AuxiliaryQualityResult diagnosis = null;
            try
            {
                var token = CancellationToken.None; var watch = Stopwatch.StartNew();
                var source = new TekScopeCsvReader().ReadUntilSecondSweepStart(path, token); log.Source(source); metrics["parsed_samples"] = source.SampleCount;
                metrics["read_seconds"] = watch.Elapsed.TotalSeconds; metrics["sample_interval_seconds"] = source.SampleIntervalSeconds;
                var engine = new SweepPreparationEngine(); var sweeps = engine.DetectSweeps(source, token);
                if (sweeps.Count != 1 || !source.IsPartialRead) throw new InvalidOperationException("Expected one complete sweep from bounded read.");
                var sweep = sweeps[0]; metrics["sweep_start_index"] = sweep.StartIndex; metrics["sweep_end_exclusive"] = sweep.EndIndexExclusive; metrics["sweep_samples"] = sweep.SampleCount;
                var part = engine.Slice(source, sweep, token); var exclusions = new ModeHopPreviewEngine().Detect(part, new ModeHopSettings(), token);
                var quality = new AuxiliaryQualityEngine(); var stitched = quality.Stitch(part, exclusions, token);
                metrics["retained_samples"] = stitched.Values.Length; metrics["excluded_samples"] = exclusions.ExcludedSamples;
                if (stitched.Values.Length != expected) throw new InvalidOperationException("Retained count differs from observed first sweep: " + stitched.Values.Length);
                var settings = new AuxiliaryInspectionSettings { EndSeconds = Math.Min(stitched.Values.Length, 250000) * source.SampleIntervalSeconds };
                diagnosis = quality.Analyze(stitched, settings, token);
                var prepared = engine.Prepare(source, sweep, role, stitched, diagnosis.Metrics, settings, 1.8, token);
                metrics["quality_samples"] = prepared.SampleCount; metrics["nonmonotonic_ratio"] = diagnosis.Metrics.Phase.NonmonotonicRatio;
                metrics["phase_qc_passed"] = diagnosis.Metrics.Decision == "PASS_PHASE_QC" ? 1 : 0;
                if (prepared.SampleCount > 250000) throw new InvalidOperationException("Sample validation limit exceeded.");
                for (var i = 0; i < prepared.SampleCount; i++)
                    if (prepared.Auxiliary[i] != source.Auxiliary[prepared.OriginalIndices[i]] || prepared.Measurement[i] != source.Measurement[prepared.OriginalIndices[i]]) throw new InvalidOperationException("CH3/CH4 alignment mismatch.");
                var preview = engine.MeasurementPreview(source, stitched, token); metrics["measurement_preview_points"] = preview.Primary.Values.Length;
                if (preview.Primary.Values.Length > 10000) throw new InvalidOperationException("Preview limit exceeded.");
                JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "preparation.json"), prepared.Metadata);
                JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "quality.json"), diagnosis);
                Console.WriteLine("{0}: parsed {1:N0}, first sweep {2:N0}, kept {3:N0}, quality {4:N0}: {5}", role, source.SampleCount, sweep.SampleCount, stitched.Values.Length, prepared.SampleCount, diagnosis.Metrics.Decision);
            }
            catch (Exception ex) { failure = ex; Console.Error.WriteLine(ex); }
            finally
            {
                log.Finish(failure, metrics);
                if (diagnosis != null) File.AppendAllText(Path.Combine(log.DirectoryPath, "summary.md"), "\nPhase QC: " + diagnosis.Metrics.Decision + ". " + diagnosis.Metrics.Reason + "\nFull recording and real-data end-to-end OFDR analysis were not validated.\n");
                Console.WriteLine(log.DirectoryPath);
            }
            return failure == null;
        }
        private static string Get(IDictionary<string, string> options, string name, string fallback) { string value; return options.TryGetValue(name, out value) ? value : fallback; }
    }
}
