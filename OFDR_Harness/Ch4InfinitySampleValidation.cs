using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;

namespace OFDR_Harness
{
    internal static class Ch4InfinitySampleValidation
    {
        public static int Run(IDictionary<string, string> options)
        {
            string path, root; if (!options.TryGetValue("input", out path)) throw new ArgumentException("--input is required.");
            if (!options.TryGetValue("results", out root)) root = Path.Combine("harness", "results");
            var log = new SweepRunRecorder("ch4_infinity_sample_validation", new Dictionary<string, string>
            {
                ["input"] = Path.GetFullPath(path), ["scope"] = "Through second CH1 rising edge; first complete sweep only; up to 250000 retained quality samples around first Inf exclusion; full file byte SHA only",
                ["ch4_policy"] = "Preserve +/-Inf in raw model; union indices into the shared CH3/CH4 exclusion mask; finite output only",
                ["full_5m_processing"] = "false", ["golden_regression"] = "omitted by user sample-based scope"
            }, root);
            var metrics = new Dictionary<string, double>(); Exception failure = null;
            try
            {
                var token = CancellationToken.None; var source = new TekScopeCsvReader().ReadUntilSecondSweepStart(path, token); log.Source(source);
                if (source.InvalidMeasurementCount == 0) throw new InvalidOperationException("Expected CH4 Inf samples in the bounded input.");
                var detector = new ModeHopPreviewEngine(); var mask = detector.Detect(source, new ModeHopSettings(), token);
                if (source.InvalidMeasurementIndices.Any(i => !mask.Ranges.Any(r => i >= r.StartIndex && i < r.EndIndexExclusive))) throw new InvalidOperationException("Inf not excluded.");
                var preview = detector.Preview(source, mask, new ModeHopSettings(), 0, source.DurationSeconds, token);
                if (!preview.Measurement.Values.All(ModeHopSettings.IsFinite) || preview.Measurement.Values.Length > 10000) throw new InvalidOperationException("Raw preview is invalid.");
                var engine = new SweepPreparationEngine(); var sweep = engine.DetectSweeps(source, token).First(); var part = engine.Slice(source, sweep, token);
                var sweepMask = detector.ClipToSamples(mask, sweep.StartIndex, sweep.EndIndexExclusive, source.SampleIntervalSeconds, token);
                var quality = new AuxiliaryQualityEngine(); var stitched = quality.Stitch(part, sweepMask, token);
                var firstInvalid = source.InvalidMeasurementIndices.First(i => i >= sweep.StartIndex && i < sweep.EndIndexExclusive);
                var pivot = Array.BinarySearch(stitched.OriginalIndices, firstInvalid); pivot = pivot < 0 ? ~pivot : pivot;
                var first = Math.Max(0, pivot - 125000); var end = Math.Min(stitched.Values.Length, first + 250000);
                var settings = new AuxiliaryInspectionSettings { StartSeconds = first * source.SampleIntervalSeconds, EndSeconds = end * source.SampleIntervalSeconds };
                var diagnosis = quality.Analyze(stitched, settings, token);
                var prepared = engine.Prepare(source, sweep, "Ref", stitched, diagnosis.Metrics, settings, 1.8, token);
                if (prepared.SampleCount > 250000 || !prepared.Measurement.All(ModeHopSettings.IsFinite)) throw new InvalidOperationException("Output finite/sample limit failed.");
                for (var i = 0; i < prepared.SampleCount; i++)
                    if (prepared.Auxiliary[i] != source.Auxiliary[prepared.OriginalIndices[i]] || prepared.Measurement[i] != source.Measurement[prepared.OriginalIndices[i]]) throw new InvalidOperationException("CH3/CH4 correspondence failed.");
                var pairedCsv = Path.Combine(log.DirectoryPath, "finite_prepared.csv"); var io = new PreparedSweepCsv(); io.Write(prepared, pairedCsv, false, token);
                var copy = io.Read(pairedCsv, "Ref", token);
                if (!copy.Measurement.SequenceEqual(prepared.Measurement) || !copy.OriginalIndices.SequenceEqual(prepared.OriginalIndices)) throw new InvalidOperationException("Paired CSV roundtrip failed.");
                var auxCsv = Path.Combine(log.DirectoryPath, "finite_auxiliary.csv"); new AuxiliaryCsvExporter().Export(part, stitched, auxCsv, false, 1.8, diagnosis.Metrics, settings, false, token);
                var fourFile = new DiagnosedCsvInputReader().ReadPair(auxCsv, path, "Ref", token);
                if (!fourFile.Measurement.SequenceEqual(prepared.Measurement) || !fourFile.OriginalIndices.SequenceEqual(prepared.OriginalIndices)) throw new InvalidOperationException("Four-file source index alignment failed.");
                metrics["parsed_samples"] = source.SampleCount; metrics["ignored_ch4_samples_in_read_scope"] = source.InvalidMeasurementCount;
                metrics["ignored_ch4_samples_in_sweep"] = sweepMask.IgnoredMeasurementSamples;
                metrics["ignored_ch4_outside_trigger_high"] = source.InvalidMeasurementIndices.Count(i => source.Trigger[i] <= 1.8);
                metrics["sweep_start_index"] = sweep.StartIndex; metrics["sweep_end_exclusive"] = sweep.EndIndexExclusive;
                metrics["retained_sweep_samples"] = stitched.Values.Length; metrics["excluded_sweep_samples"] = sweepMask.ExcludedSamples;
                metrics["selected_start_stitched_index"] = diagnosis.Metrics.SelectedStartIndex; metrics["selected_quality_samples"] = prepared.SampleCount;
                metrics["preview_points"] = preview.Measurement.Values.Length; metrics["phase_nonmonotonic_ratio"] = diagnosis.Metrics.Phase.NonmonotonicRatio;
                metrics["phase_qc_passed"] = diagnosis.Metrics.Decision == "PASS_PHASE_QC" ? 1 : 0; metrics["paired_csv_roundtrip_passed"] = 1; metrics["four_file_read_passed"] = 1;
                JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "ignored_ch4_ranges.json"), mask.IgnoredMeasurementRanges);
                JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "quality.json"), diagnosis.Metrics);
                Console.WriteLine("Read {0} samples; CH4 Inf {1}; finite paired range {2} samples; QC {3}", source.SampleCount, source.InvalidMeasurementCount, prepared.SampleCount, diagnosis.Metrics.Decision);
            }
            catch (Exception ex) { failure = ex; Console.Error.WriteLine(ex); }
            finally
            {
                log.Finish(failure, metrics);
                File.AppendAllText(Path.Combine(log.DirectoryPath, "summary.md"), "\nBounded first-sweep validation only; CH3/CH4 finite aligned samples and both CSV routes verified. No full 5M/10M or real-data end-to-end OFDR/plateau validation; QC criteria unchanged.\n");
                Console.WriteLine(log.DirectoryPath);
            }
            return failure == null ? 0 : 1;
        }
    }
}
