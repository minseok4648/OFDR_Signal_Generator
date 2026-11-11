using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;

namespace OFDR_Harness
{
    internal static class DocumentStageCommand
    {
        public static int Run(IDictionary<string, string> args)
        {
            var root = args.ContainsKey("results") ? args["results"] : "harness/results";
            var folder = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_document_stages"); Directory.CreateDirectory(folder);
            var timer = Stopwatch.StartNew(); string error = null; var sources = new List<PreparedSweepMetadata>(); var completed = new List<Dictionary<string, string>>();
            var config = new IndependentOfdrProcessingConfig { StageComparisonEnabled = true, AllowFailedPhaseQcForDiagnostics = true, UsePhysicalAxes = false,
                SegmentSize = 128, SegmentHop = 32, SegmentIfftZeroPadFactor = 8, MaxPreviewPoints = 2048,
                StageComparison = new StageComparisonSettings { OutputSamples = 65536, Document = new DocumentStageSettings { Enabled = true, PhysicalWindow = true } } };
            try
            {
                if (args.ContainsKey("config")) config = JsonContractSerializer.Read<IndependentOfdrProcessingConfig>(args["config"]);
                JsonContractSerializer.Write(Path.Combine(folder, "config.json"), config);
                JsonContractSerializer.Write(Path.Combine(folder, "arguments.json"), args);
                if (!args.ContainsKey("ref") || !args.ContainsKey("sig")) throw new ArgumentException("--ref/--sig 4채널 원본 CSV가 필요합니다.");
                var fft = new FftEngine(); if (!Environment.Is64BitProcess || !fft.NativeProviderAvailable) throw new InvalidOperationException("실제 전체 기록 검증은 x64 MKL이 필요합니다.");
                var reference = Read(args["ref"], "Ref", sources); var signal = Read(args["sig"], "Signal", sources);
                JsonContractSerializer.Write(Path.Combine(folder, "dataset_manifest.json"), sources);
                var input = new IndependentOfdrInput { Auxiliary = reference.Item1, Reference = reference.Item2, SignalAuxiliary = signal.Item1, Signal = signal.Item2 };
                var engine = new StageComparisonEngine();
                foreach (var condition in new[] { "filter_off", "B_nominal", "C_phase", "D_delay", "self_C" })
                {
                    var c = JsonContractSerializer.Clone(config); var d = c.StageComparison.Document;
                    d.BandpassEnabled = condition != "filter_off"; d.UseNominalGrid = condition == "B_nominal";
                    d.DelayMode = condition == "D_delay" ? StageDelayMode.ReflectionDistance : StageDelayMode.None;
                    var pair = condition == "self_C" ? new IndependentOfdrInput { Auxiliary = reference.Item1, Reference = reference.Item2, SignalAuxiliary = reference.Item1, Signal = reference.Item2 } : input;
                    if (condition == "self_C") d.SignalRangeIndex = d.ReferenceRangeIndex;
                    Execute(folder, condition, pair, c, engine, completed);
                }
                if (args.ContainsKey("all-ranges"))
                {
                    var rr = DocumentStagePreprocessor.Ranges(input.Auxiliary); var sr = DocumentStagePreprocessor.Ranges(input.SignalAuxiliary);
                    for (var i = 0; i < Math.Min(rr.Count, sr.Count); i++)
                    {
                        if (Math.Min(rr[i].Count, sr[i].Count) < config.StageComparison.Document.MinimumRangeSamples + 1024) continue;
                        var c = JsonContractSerializer.Clone(config); c.StageComparison.Document.BandpassEnabled = true;
                        c.StageComparison.Document.ReferenceRangeIndex = c.StageComparison.Document.SignalRangeIndex = i;
                        Execute(folder, "C_range_" + i, input, c, engine, completed);
                    }
                }
            }
            catch (Exception ex) { error = ex.ToString(); Console.Error.WriteLine(error); }
            finally
            {
                JsonContractSerializer.Write(Path.Combine(folder, "cases.json"), completed);
                JsonContractSerializer.Write(Path.Combine(folder, "metrics.json"), new Dictionary<string, string> { ["Status"] = error == null ? "COMPLETED" : "FAILED", ["Error"] = error,
                    ["ElapsedSeconds"] = timer.Elapsed.TotalSeconds.ToString("R", CultureInfo.InvariantCulture), ["PeakWorkingSetBytes"] = Process.GetCurrentProcess().PeakWorkingSet64.ToString(), ["Cases"] = completed.Count.ToString() });
                File.WriteAllText(Path.Combine(folder, "summary.md"), "# Document stage comparison\n\nStatus: " + (error == null ? "COMPLETED" : "FAILED")
                    + "\n\nFull CSV read; identical CH2 > 1.8 V mask + 100 us tail for B/C/D/filter-off; all retained samples represented in provenance. Each condition processes the complete selected continuous range. --all-ranges processes eligible same-ordinal ranges independently; this is not absolute wavelength registration or an averaged profile. Relative distance/shift only; no calibrated strain.\n\n" + (error ?? "No execution error. Scientific quality is recorded separately in each point/status.") + "\n");
                Console.WriteLine("RESULT=" + Path.GetFullPath(folder));
            }
            return error == null ? 0 : 1;
        }
        private static void Execute(string folder, string name, IndependentOfdrInput input, IndependentOfdrProcessingConfig config, StageComparisonEngine engine, List<Dictionary<string, string>> completed)
        {
            var output = Path.Combine(folder, name); Directory.CreateDirectory(output); JsonContractSerializer.Write(Path.Combine(output, "config.json"), config);
            var timer = Stopwatch.StartNew();
            try
            {
                using (var result = engine.Run(input, config, CancellationToken.None))
                {
                    var points = result.DocumentStages.Points;
                    if (name == "self_C" && points.Any(p => !ModeHopSettings.IsFinite(p.CandidateLag) || Math.Abs(p.CandidateLag) > 1e-6)) throw new InvalidOperationException("Self comparison did not produce zero lag.");
                    JsonContractSerializer.Write(Path.Combine(output, "effective_config.json"), result.Config);
                    JsonContractSerializer.Write(Path.Combine(output, "metrics.json"), result);
                    using (var w = new StreamWriter(Path.Combine(output, "profile.csv")))
                    {
                        w.WriteLine("distance_m,candidate_shift_pm,lag,peak_zncc,second_peak,separation,overlap,status,window_start_m,window_end_m,candidate_strain_microstrain,accepted_strain_microstrain");
                        foreach (var p in points) w.WriteLine(string.Join(",", new[] { p.DistanceMeters, p.CandidateShiftPm, p.CandidateLag, p.PeakZncc, p.SecondPeak, p.Separation, p.Overlap }.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "," + p.Status + "," + string.Join(",", new[] { p.WindowStartMeters, p.WindowEndMeters, p.CandidateStrainMicrostrain ?? double.NaN, p.AcceptedStrainMicrostrain ?? double.NaN }.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));
                    }
                    File.WriteAllText(Path.Combine(output, "summary.md"), "# COMPLETED\n\n" + result.DocumentStages.Summary);
                    completed.Add(new Dictionary<string, string> { ["Name"] = name, ["Seconds"] = timer.Elapsed.TotalSeconds.ToString("R", CultureInfo.InvariantCulture),
                        ["Points"] = points.Count.ToString(), ["Accepted"] = points.Count(p => p.Valid).ToString(), ["Provider"] = result.FftProvider,
                        ["MaximumAbsoluteLag"] = points.Where(p => ModeHopSettings.IsFinite(p.CandidateLag)).Select(p => Math.Abs(p.CandidateLag)).DefaultIfEmpty(double.NaN).Max().ToString("R", CultureInfo.InvariantCulture) });
                    Console.WriteLine(name + ": " + points.Count + " points; accepted=" + points.Count(p => p.Valid));
                }
            }
            catch (Exception ex)
            {
                JsonContractSerializer.Write(Path.Combine(output, "metrics.json"), new Dictionary<string, string> { ["Status"] = "FAILED", ["Error"] = ex.ToString() });
                File.WriteAllText(Path.Combine(output, "summary.md"), "# FAILED\n\n" + ex); throw;
            }
        }
        private static Tuple<OfdrChannelData, OfdrChannelData> Read(string path, string role, List<PreparedSweepMetadata> sources)
        {
            var token = CancellationToken.None; Console.WriteLine("Reading " + path);
            var raw = new TekScopeCsvReader().Read(path, token);
            if (!raw.HasFourChannels || raw.IsPartialRead) throw new ArgumentException("전체 4채널 원본이 필요합니다.");
            var mask = new ModeHopPreviewEngine().Detect(raw, new ModeHopSettings { ThresholdVoltage = 1.8, ExclusionTailSeconds = .0001 }, token);
            var stitched = new AuxiliaryQualityEngine().Stitch(raw, mask, token); var m = new double[stitched.Values.Length];
            for (var i = 0; i < m.Length; i++) m[i] = raw.Measurement[stitched.OriginalIndices[i]];
            var metadata = new PreparedSweepMetadata { Role = role, SourcePath = path, SourceSha256 = raw.Sha256, SourceHeader = raw.Header,
                ModeHopThresholdVoltage = 1.8, ModeHopExclusionTailSeconds = .0001, SourceStartSeconds = raw.SourceStartSeconds,
                SampleIntervalSeconds = raw.SampleIntervalSeconds, RowCount = m.Length, SelectedStartIndex = 0, SelectedEndIndexExclusive = m.Length,
                Sweep = new SweepRange { StartIndex = 0, EndIndexExclusive = raw.SampleCount }, ExcludedRanges = mask.Ranges };
            sources.Add(metadata);
            Func<double[], int, OfdrChannelData> channel = (values, number) => new OfdrChannelData { Values = values, Preparation = metadata,
                SourcePath = path, ChannelNumber = number, MeasurementId = role + ":" + Path.GetFileName(path), SampleIntervalSeconds = raw.SampleIntervalSeconds };
            return Tuple.Create(channel(stitched.Values, 3), channel(m, 4));
        }
    }
}
