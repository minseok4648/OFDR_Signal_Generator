using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Services
{
    public sealed class AuxiliaryQualityRunRecorder
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        public string DirectoryPath { get; }
        public AuxiliaryQualityRunRecorder(ModeHopRecording recording, double threshold, AuxiliaryInspectionSettings settings, string kind, string resultsRoot = null, double exclusionTailSeconds = 0, double exclusionLeadSeconds = 0)
        {
            DirectoryPath = Path.GetFullPath(Path.Combine(resultsRoot ?? Path.Combine("harness", "results"), DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + kind + "_" + Guid.NewGuid().ToString("N").Substring(0, 6)));
            Directory.CreateDirectory(DirectoryPath);
            JsonContractSerializer.Write(Path.Combine(DirectoryPath, "config.json"), new AuxiliaryQualityConfig { SourcePath = recording.SourcePath, Sha256 = recording.Sha256, Header = recording.Header, ThresholdVoltage = threshold, ExclusionLeadSeconds = exclusionLeadSeconds, ExclusionTailSeconds = exclusionTailSeconds, IgnoredMeasurementSamples = recording.InvalidMeasurementCount, IgnoredSamples = recording.InvalidSampleCount, Settings = settings });
            JsonContractSerializer.Write(Path.Combine(DirectoryPath, "metrics.json"), new AuxiliaryQualityRunMetrics { Status = "RUNNING" });
            File.WriteAllText(Path.Combine(DirectoryPath, "summary.md"), "# Auxiliary quality experiment\n\nRUNNING; incomplete runs require investigation.\n");
        }
        public void Finish(StitchedAuxiliarySignal signal, AuxiliaryQualityResult quality, Exception error)
        {
            var status = error == null ? "COMPLETED" : error is OperationCanceledException ? "CANCELLED" : "FAILED";
            var metrics = new AuxiliaryQualityRunMetrics { Status = status, Error = error?.ToString(), RetainedSamples = signal?.Values.Length ?? 0, JoinCount = signal?.JoinIndices.Length ?? 0, CompressedDurationSeconds = signal?.DurationSeconds ?? 0,
                ElapsedSeconds = watch.Elapsed.TotalSeconds, PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64, Quality = quality?.Metrics };
            JsonContractSerializer.Write(Path.Combine(DirectoryPath, "metrics.json"), metrics);
            if (signal != null) JsonContractSerializer.Write(Path.Combine(DirectoryPath, "artifacts", "join_indices.json"), signal.JoinIndices);
            if (quality != null) JsonContractSerializer.Write(Path.Combine(DirectoryPath, "artifacts", "quality_plots.json"), quality.Plots);
            File.WriteAllText(Path.Combine(DirectoryPath, "summary.md"), "# Auxiliary quality experiment\n\n- Status: " + status + "\n- Retained samples: " + metrics.RetainedSamples + "\n- Joins: " + metrics.JoinCount +
                "\n- Quality decision: " + (quality?.Metrics.Decision ?? "NOT_EVALUATED") + "\n- Reason: " + (quality?.Metrics.Reason ?? error?.Message ?? "Stitching only") +
                "\n\nUniform phase grid is constructed linear; it does not prove signal quality. Source indices are preserved. The time axis after concatenation compresses removed intervals.\n", new UTF8Encoding(false));
        }
    }
    [DataContract] public sealed class AuxiliaryQualityConfig
    {
        [DataMember] public string SourcePath { get; set; }
        [DataMember] public string Sha256 { get; set; }
        [DataMember] public System.Collections.Generic.Dictionary<string, string> Header { get; set; }
        [DataMember] public double ThresholdVoltage { get; set; }
        [DataMember] public double ExclusionTailSeconds { get; set; }
        [DataMember] public double ExclusionLeadSeconds { get; set; }
        [DataMember] public int IgnoredMeasurementSamples { get; set; }
        [DataMember] public int IgnoredSamples { get; set; }
        [DataMember] public AuxiliaryInspectionSettings Settings { get; set; }
        [DataMember] public string Operation { get; set; } = "Remove finite CH2 High with pre-pulse lead and post-pulse tail samples plus any-channel +/-Inf samples; union overlaps, clip to selected sweep; concatenate unchanged CH3 samples; select by compressed time; no phase stitching correction";
    }
    [DataContract] public sealed class AuxiliaryQualityRunMetrics
    {
        [DataMember] public string Status { get; set; }
        [DataMember] public string Error { get; set; }
        [DataMember] public int RetainedSamples { get; set; }
        [DataMember] public int JoinCount { get; set; }
        [DataMember] public double CompressedDurationSeconds { get; set; }
        [DataMember] public double ElapsedSeconds { get; set; }
        [DataMember] public long PeakWorkingSetBytes { get; set; }
        [DataMember] public AuxiliaryQualityMetrics Quality { get; set; }
    }
}
