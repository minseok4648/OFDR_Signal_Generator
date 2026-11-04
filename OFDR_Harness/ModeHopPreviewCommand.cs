using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;

namespace OFDR_Harness
{
    internal static class ModeHopPreviewCommand
    {
        public static int Run(IDictionary<string, string> options)
        {
            string path;
            if (!options.TryGetValue("csv", out path)) throw new ArgumentException("preview-mode-hop requires --csv <Tektronix CSV>.");
            string configPath, resultsRoot;
            var settings = options.TryGetValue("config", out configPath) ? JsonContractSerializer.Read<ModeHopSettings>(configPath) : new ModeHopSettings();
            if (!options.TryGetValue("results", out resultsRoot)) resultsRoot = Path.Combine("harness", "results");
            var log = new ModeHopRunRecorder(resultsRoot, path, settings, "mode_hop_cli");
            ModeHopRecording recording = null;
            ModeHopAnalysis analysis = null;
            ModeHopViewport viewport = null;
            Exception failure = null;
            var stage = "CSV read";
            try
            {
                settings.Validate();
                recording = new TekScopeCsvReader().Read(path, CancellationToken.None);
                stage = "High region detection";
                var engine = new ModeHopPreviewEngine();
                analysis = engine.Detect(recording, settings, CancellationToken.None);
                stage = "Full preview";
                viewport = engine.Preview(recording, analysis, settings, 0, recording.DurationSeconds, CancellationToken.None);
                JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "artifacts", "preview.json"), viewport);
                if (options.ContainsKey("golden"))
                {
                    stage = "Golden acceptance";
                    if (recording.Sha256 != "c8837dc614ae7cf3702e51c1fe7de5307be79063a8ee6eeb09501e1425f25a34" ||
                        recording.SampleCount != 5000000 || Math.Abs(recording.DurationSeconds - 4) > 1e-12 ||
                        settings.ThresholdVoltage != 1.8 || analysis.Ranges.Count != 755 || analysis.ExcludedSamples != 471902 ||
                        viewport.Auxiliary.Values.Length > 10000 || viewport.Trigger.Values.Length > 10000)
                        throw new InvalidOperationException("Mode hop golden dataset/config/metrics mismatch.");
                }
                Console.WriteLine("Samples={0}; Duration={1:G9}s; Regions={2}; ExcludedSamples={3}; ExcludedPercent={4:F5}", recording.SampleCount, recording.DurationSeconds, analysis.Ranges.Count, analysis.ExcludedSamples, analysis.ExcludedPercent);
                if (options.ContainsKey("inspect"))
                {
                    stage = "Auxiliary stitching and phase inspection";
                    string qualityConfig;
                    var inspection = options.TryGetValue("quality-config", out qualityConfig) ? JsonContractSerializer.Read<AuxiliaryInspectionSettings>(qualityConfig) : new AuxiliaryInspectionSettings();
                    var qualityLog = new AuxiliaryQualityRunRecorder(recording, settings.ThresholdVoltage, inspection, "aux_quality_cli", resultsRoot);
                    StitchedAuxiliarySignal stitched = null; AuxiliaryQualityResult quality = null; Exception qualityFailure = null;
                    try
                    {
                        var qualityEngine = new AuxiliaryQualityEngine();
                        stitched = qualityEngine.Stitch(recording, analysis, CancellationToken.None);
                        quality = qualityEngine.Analyze(stitched, inspection, CancellationToken.None);
                        if (options.ContainsKey("golden") && stitched.Values.Length != 4528098) throw new InvalidOperationException("Golden retained sample count mismatch.");
                        Console.WriteLine("Retained={0}; Joins={1}; Decision={2}; NonmonotonicRatio={3:G9}; PhaseSpan={4:G9}", stitched.Values.Length, stitched.JoinIndices.Length, quality.Metrics.Decision, quality.Metrics.Phase.NonmonotonicRatio, quality.Metrics.Phase.PhaseSpanRadians);
                    }
                    catch (Exception ex) { qualityFailure = ex; throw; }
                    finally { qualityLog.Finish(stitched, quality, qualityFailure); Console.WriteLine("QUALITY_RESULT=" + qualityLog.DirectoryPath); }
                }
                return 0;
            }
            catch (Exception ex) { failure = ex; Console.Error.WriteLine(ex); return 1; }
            finally { log.Finish(recording, analysis, viewport, failure, stage); Console.WriteLine("RESULT=" + log.DirectoryPath); }
        }
    }
}
