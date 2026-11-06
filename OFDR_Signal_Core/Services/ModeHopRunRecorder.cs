using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Services
{
    public sealed class ModeHopRunRecorder
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        public string DirectoryPath { get; }

        public ModeHopRunRecorder(string root, string sourcePath, ModeHopSettings settings, string kind)
        {
            DirectoryPath = Path.GetFullPath(Path.Combine(root, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + kind + "_" + Guid.NewGuid().ToString("N").Substring(0, 6)));
            Directory.CreateDirectory(DirectoryPath);
            JsonContractSerializer.Write(Path.Combine(DirectoryPath, "config.json"), new ModeHopRunConfig { SourcePath = sourcePath, Settings = settings });
            JsonContractSerializer.Write(Path.Combine(DirectoryPath, "metrics.json"), new ModeHopRunMetrics { Status = "RUNNING", Stage = "initialization" });
            File.WriteAllText(Path.Combine(DirectoryPath, "summary.md"), "# Mode hop preview\n\n- Status: RUNNING\n- Decision: NEEDS_MORE_TEST if the process ends before finalization.\n", new UTF8Encoding(false));
        }

        public void Finish(ModeHopRecording recording, ModeHopAnalysis analysis, ModeHopViewport viewport, Exception error, string stage)
        {
            watch.Stop();
            var status = error == null ? "COMPLETED" : error is OperationCanceledException ? "CANCELLED" : "FAILED";
            var metrics = new ModeHopRunMetrics
            {
                Status = status, Stage = stage, Error = error?.ToString(), ElapsedSeconds = watch.Elapsed.TotalSeconds,
                PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                ManagedMemoryBytes = GC.GetTotalMemory(false), Sha256 = recording?.Sha256, Header = recording?.Header,
                SampleCount = recording?.SampleCount ?? 0, SampleIntervalSeconds = recording?.SampleIntervalSeconds ?? 0,
                SourceStartSeconds = recording?.SourceStartSeconds ?? 0, DurationSeconds = recording?.DurationSeconds ?? 0,
                IgnoredMeasurementSamples = recording?.InvalidMeasurementCount ?? 0,
                IgnoredSamples = recording?.InvalidSampleCount ?? 0,
                ExcludedSamples = analysis?.ExcludedSamples ?? 0, ExcludedSeconds = analysis?.ExcludedSeconds ?? 0, ExclusionLeadSeconds = analysis?.ExclusionLeadSeconds ?? 0, ExclusionTailSeconds = analysis?.ExclusionTailSeconds ?? 0,
                ExcludedPercent = analysis?.ExcludedPercent ?? 0, RegionCount = analysis?.Ranges.Count ?? 0,
                AuxiliaryPreviewPoints = viewport?.Auxiliary.Values.Length ?? 0, TriggerPreviewPoints = viewport?.Trigger.Values.Length ?? 0
            };
            JsonContractSerializer.Write(Path.Combine(DirectoryPath, "metrics.json"), metrics);
            if (analysis != null) JsonContractSerializer.Write(Path.Combine(DirectoryPath, "artifacts", "excluded_ranges.json"), analysis.Ranges);
            if (analysis?.IgnoredMeasurementRanges != null) JsonContractSerializer.Write(Path.Combine(DirectoryPath, "artifacts", "ignored_ch4_ranges.json"), analysis.IgnoredMeasurementRanges);
            if (analysis?.IgnoredSampleRanges != null) JsonContractSerializer.Write(Path.Combine(DirectoryPath, "artifacts", "ignored_all_channel_ranges.json"), analysis.IgnoredSampleRanges);
            File.WriteAllText(Path.Combine(DirectoryPath, "summary.md"),
                "# Mode hop preview\n\n- Status: " + status + "\n- Stage: " + stage + "\n- Samples: " + metrics.SampleCount +
                "\n- Regions: " + metrics.RegionCount + "\n- Excluded samples: " + metrics.ExcludedSamples +
                "\n- Elapsed seconds: " + metrics.ElapsedSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                "\n- Error: " + (error?.Message ?? "none") + "\n\n## Decision\n\n" + (error == null ? "COMPLETED; numerical and GUI acceptance are verified separately." : "REJECT; retry with the same input after fixing the cause.") + "\n", new UTF8Encoding(false));
        }
    }

    [DataContract]
    public sealed class ModeHopRunConfig
    {
        [DataMember] public string SourcePath { get; set; }
        [DataMember] public string TriggerRule { get; set; } = "Finite CH2 > ThresholdVoltage; extend pulse start backward by ExclusionLeadSeconds and pulse end forward by ExclusionTailSeconds (ceil to samples); union CH1/CH2/CH3/CH4 +/-Inf indices without additional padding; union overlapping/touching ranges; clip to record; half-open sample ranges; CH3/CH4 retain identical source indices; CH1 edges require adjacent finite samples";
        [DataMember] public ModeHopSettings Settings { get; set; }
    }

    [DataContract]
    public sealed class ModeHopRunMetrics
    {
        [DataMember] public string Status { get; set; }
        [DataMember] public string Stage { get; set; }
        [DataMember] public string Error { get; set; }
        [DataMember] public double ElapsedSeconds { get; set; }
        [DataMember] public long PeakWorkingSetBytes { get; set; }
        [DataMember] public long ManagedMemoryBytes { get; set; }
        [DataMember] public string Sha256 { get; set; }
        [DataMember] public Dictionary<string, string> Header { get; set; }
        [DataMember] public int SampleCount { get; set; }
        [DataMember] public int IgnoredMeasurementSamples { get; set; }
        [DataMember] public int IgnoredSamples { get; set; }
        [DataMember] public double SampleIntervalSeconds { get; set; }
        [DataMember] public double SourceStartSeconds { get; set; }
        [DataMember] public double DurationSeconds { get; set; }
        [DataMember] public int RegionCount { get; set; }
        [DataMember] public long ExcludedSamples { get; set; }
        [DataMember] public double ExcludedSeconds { get; set; }
        [DataMember] public double ExclusionTailSeconds { get; set; }
        [DataMember] public double ExclusionLeadSeconds { get; set; }
        [DataMember] public double ExcludedPercent { get; set; }
        [DataMember] public int AuxiliaryPreviewPoints { get; set; }
        [DataMember] public int TriggerPreviewPoints { get; set; }
    }
}
