using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;

namespace OFDR_Harness
{
    internal static class DiagnosedFilesSampleCommand
    {
        public static int Run(IDictionary<string, string> options)
        {
            var folder = @"C:\Project\OFDR\Datas\OFDR Raw data\NoticeData";
            var config = new Dictionary<string, string> {
                ["ref_aux"] = Get(options, "ref-aux", Path.Combine(folder, "tek0003ALL_stitched_diagnosed_0_2545000.csv")),
                ["sig_aux"] = Get(options, "sig-aux", Path.Combine(folder, "tek0005ALL_stitched_diagnosed_0_2545000.csv")),
                ["reference"] = Get(options, "ref", Path.Combine(folder, "tek0003ALL.csv")), ["signal"] = Get(options, "sig", Path.Combine(folder, "tek0005ALL.csv")),
                ["sample_limit"] = Get(options, "samples", "250000"), ["purpose"] = "diagnostic only; retain failed phase QC; no full real-data verification" };
            var count = int.Parse(config["sample_limit"], CultureInfo.InvariantCulture);
            var log = new SweepRunRecorder("diagnosed_files_sample", config, Get(options, "results", Path.Combine("harness", "results")));
            Exception failure = null; var metrics = new Dictionary<string, double>();
            try
            {
                var input = new DiagnosedCsvInputReader().Read(config["ref_aux"], config["sig_aux"], config["reference"], config["signal"], CancellationToken.None, null, count);
                JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "ref_input.json"), input.Reference.Preparation);
                JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "signal_input.json"), input.Signal.Preparation);
                var processing = new IndependentOfdrProcessingConfig { AllowFailedPhaseQcForDiagnostics = true, SamplingRateSamplesPerSecond = 1.0 / input.Auxiliary.SampleIntervalSeconds };
                JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "processing_config.json"), processing);
                using (var result = new IndependentOfdrProcessingEngine().Run(input, processing, CancellationToken.None))
                {
                    JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "result.json"), result);
                    metrics["ref_samples"] = input.Reference.Values.Length; metrics["signal_samples"] = input.Signal.Values.Length;
                    metrics["output_samples"] = result.SampleCount; metrics["segments"] = result.SegmentStarts.Length;
                    metrics["diagnostic_analysis"] = result.IsDiagnosticAnalysis ? 1 : 0; metrics["input_phase_qc_passed"] = result.InputPhaseQcPassed ? 1 : 0;
                    metrics["ref_nonmonotonic_ratio"] = result.KLinearization.NonmonotonicRatio; metrics["sig_nonmonotonic_ratio"] = result.SignalKLinearization.NonmonotonicRatio;
                    metrics["ref_preview_points"] = result.ReferenceRaw.Values.Length; metrics["sig_preview_points"] = result.SignalRaw.Values.Length;
                    if (result.SampleCount > 250000 || !result.IsDiagnosticAnalysis) throw new InvalidOperationException("Diagnostic/sample scope mismatch.");
                    Console.WriteLine(result.PhaseQcSummary); Console.WriteLine("Computed {0:N0} points, {1:N0} segments", result.SampleCount, result.SegmentStarts.Length);
                }
            }
            catch (Exception ex) { failure = ex; Console.Error.WriteLine(ex); }
            finally { log.Finish(failure, metrics); File.AppendAllText(Path.Combine(log.DirectoryPath, "summary.md"), "\nPurpose: current-state diagnostic calculation only. A computed result does not mean phase QC passed. Inputs are marked partial validation inputs; full 2.545M/10M analysis and golden regression were not validated.\n"); Console.WriteLine(log.DirectoryPath); }
            return failure == null ? 0 : 1;
        }
        private static string Get(IDictionary<string, string> options, string name, string fallback) { string value; return options.TryGetValue(name, out value) ? value : fallback; }
    }
}
