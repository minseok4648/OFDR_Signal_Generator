using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Services
{
    public sealed class SweepRunRecorder
    {
        public string DirectoryPath { get; }
        private readonly Dictionary<string, string> config;
        private readonly Stopwatch watch = Stopwatch.StartNew();
        public SweepRunRecorder(string operation, Dictionary<string, string> configuration = null, string resultsRoot = null)
        {
            DirectoryPath = Path.GetFullPath(Path.Combine(resultsRoot ?? Path.Combine("harness", "results"), DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + operation + "_" + Guid.NewGuid().ToString("N").Substring(0, 6)));
            config = configuration ?? new Dictionary<string, string>(); config["operation"] = operation;
            JsonContractSerializer.Write(Path.Combine(DirectoryPath, "config.json"), config);
        }
        public void Source(ModeHopRecording source)
        {
            config["source_path"] = source.SourcePath; config["source_sha256"] = source.Sha256;
            config["parsed_samples"] = source.SampleCount.ToString(); config["partial_read"] = source.IsPartialRead.ToString();
            JsonContractSerializer.Write(Path.Combine(DirectoryPath, "config.json"), config);
            JsonContractSerializer.Write(Path.Combine(DirectoryPath, "source_header.json"), source.Header);
        }
        public void Finish(Exception error = null, Dictionary<string, double> metrics = null)
        {
            metrics = metrics ?? new Dictionary<string, double>(); metrics["elapsed_seconds"] = watch.Elapsed.TotalSeconds;
            metrics["peak_working_set_bytes"] = Process.GetCurrentProcess().PeakWorkingSet64;
            JsonContractSerializer.Write(Path.Combine(DirectoryPath, "metrics.json"), metrics);
            File.WriteAllText(Path.Combine(DirectoryPath, "summary.md"), "# " + config["operation"] + "\n\nStatus: " + (error == null ? "PASS" : error is OperationCanceledException ? "CANCELLED" : "FAILED") + "\n\n" + (error == null ? "Completed within the recorded sample/config scope." : error.ToString()) + "\n");
        }

        public static string RecordQcBlock(string stage, PreparedSweepMetadata reference, PreparedSweepMetadata signal, double limit, string reason)
        {
            var log = new SweepRunRecorder("prepared_qc_blocked", new Dictionary<string, string> { ["stage"] = stage, ["processing_limit"] = limit.ToString("G17", System.Globalization.CultureInfo.InvariantCulture) });
            if (reference != null) JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "ref_preparation.json"), reference);
            if (signal != null) JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "signal_preparation.json"), signal);
            log.Finish(new InvalidOperationException(reason), new Dictionary<string, double> { ["blocked_before_processing"] = 1, ["fft_started"] = 0 });
            return log.DirectoryPath;
        }
    }
}
