using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;

namespace OFDR_Harness
{
    internal static class StageComparisonCommand
    {
        internal static int Run(IDictionary<string, string> options)
        {
            if (options.ContainsKey("resampling-control")) return RunResamplingControl(options);
            string text;
            var inputDirectory = options.TryGetValue("input", out text) ? text : @"D:\project\OFDR\signal processing\261106_RAW";
            var output = Path.GetFullPath(options.TryGetValue("output", out text) ? text : "harness/results/20261109_stage_ablation");
            if (Directory.Exists(output)) throw new IOException("Experiment already exists: " + output);
            Directory.CreateDirectory(output);
            var cancellation = new CancellationTokenSource(); Console.CancelKeyPress += (s, e) => { e.Cancel = true; cancellation.Cancel(); };
            var token = cancellation.Token; var clock = Stopwatch.StartNew(); var completed = new List<string>();
            var sources = new List<NotebookSourceMetadata>(); var status = "RUNNING"; string error = null;
            JsonContractSerializer.Write(Path.Combine(output, "config.json"), new Dictionary<string, string>
            { ["input_directory"] = inputDirectory, ["crop"] = "first CH1 > 3 V + 15000; 17000000 samples",
                ["mask"] = "No mode-hop removal; reject nonfinite selected input", ["primary_design"] = "2 AUX DC x 2 phase order x 2 grid x 2 window, fixed remaining notebook controls",
                ["roi_profile_index"] = "2500..3000 inclusive", ["roi_fft_bins"] = "125000..150000", ["segment_hop_padding"] = "4096/50/8",
                ["note"] = "Full raw phase extraction, bounded <=5243 profile positions per case; no absolute temperature/strain claim." });
            File.WriteAllText(Path.Combine(output, "summary.md"), "# RUNNING\n\nStage ablation; incomplete cases require investigation.\n");
            try
            {
                var reference = ReadCrop(Path.Combine(inputDirectory, "tek0002ALL.csv"), sources, token);
                foreach (var pair in new[] { Tuple.Create("60C", "tek0003ALL.csv"), Tuple.Create("80C", "tek0004ALL.csv"), Tuple.Create("100C", "tek0005ALL.csv") })
                {
                    var signal = ReadCrop(Path.Combine(inputDirectory, pair.Item2), sources, token);
                    JsonContractSerializer.Write(Path.Combine(output, "dataset_manifest.json"), sources);
                    var input = new IndependentOfdrInput { Auxiliary = reference.Item1, Reference = reference.Item2, SignalAuxiliary = signal.Item1, Signal = signal.Item2 };
                    var engine = new StageComparisonEngine();
                    foreach (var dc in new[] { false, true }) foreach (var policy in new[] { AuxiliaryPhaseOrder.StablePhaseSort, AuxiliaryPhaseOrder.IncreasingAnchors })
                    {
                        var basis = Config(); basis.StageComparison.RemoveAuxiliaryMean = dc; basis.StageComparison.PhaseOrder = policy;
                        Console.WriteLine(pair.Item1 + " phase: DC=" + dc + " " + policy);
                        using (var r = engine.Prepare(input.Auxiliary.Values, input.Reference.Values, basis.StageComparison, 2048, token))
                        using (var s = engine.Prepare(input.SignalAuxiliary.Values, input.Signal.Values, basis.StageComparison, 2048, token))
                        {
                            foreach (var grid in new[] { RelativePhaseGrid.IndependentEndpoints, RelativePhaseGrid.CommonSpan })
                            foreach (var window in new[] { DiagnosticWindowPlacement.GlobalSignal, DiagnosticWindowPlacement.DistanceSegment })
                            {
                                var c = JsonContractSerializer.Clone(basis); c.StageComparison.Grid = grid; c.StageComparison.WindowPlacement = window;
                                Execute(output, pair.Item1, "dc" + (dc ? "1" : "0") + "_" + (policy == AuxiliaryPhaseOrder.StablePhaseSort ? "sort" : "anchor") + "_" + (grid == RelativePhaseGrid.CommonSpan ? "common" : "ind") + "_" + (window == DiagnosticWindowPlacement.GlobalSignal ? "global" : "local"),
                                    input, c, engine, r, s, token, completed);
                            }
                            if (!dc && policy == AuxiliaryPhaseOrder.StablePhaseSort)
                            {
                                foreach (var window in new[] { DiagnosticWindowPlacement.None, DiagnosticWindowPlacement.Both })
                                { var c = JsonContractSerializer.Clone(basis); c.StageComparison.WindowPlacement = window; Execute(output, pair.Item1, "nb_window_" + window, input, c, engine, r, s, token, completed); }
                                var mean = JsonContractSerializer.Clone(basis); mean.StageComparison.RemoveMeasurementMean = false;
                                Execute(output, pair.Item1, "nb_measurement_dc_retained", input, mean, engine, r, s, token, completed);
                                var corr = JsonContractSerializer.Clone(basis); corr.StageComparison.CenterLocalMagnitudes = true; corr.NormalizeCrossCorrelation = true;
                                Execute(output, pair.Item1, "nb_center_normalize_correlation", input, corr, engine, r, s, token, completed);
                                var points = JsonContractSerializer.Clone(basis); points.StageComparison.OutputSamples = 17000000; points.StageComparison.StopFftBin = 262144;
                                Execute(output, pair.Item1, "nb_output_17M", input, points, engine, r, s, token, completed);
                            }
                            if (dc && policy == AuxiliaryPhaseOrder.IncreasingAnchors)
                            {
                                var c = JsonContractSerializer.Clone(basis); c.StageComparison.Grid = RelativePhaseGrid.CommonSpanTwoPass; c.StageComparison.WindowPlacement = DiagnosticWindowPlacement.DistanceSegment;
                                Execute(output, pair.Item1, "dc1_anchor_twopass_local", input, c, engine, r, s, token, completed);
                            }
                        }
                    }
                    foreach (var legacy in new[] { false, true })
                    {
                        var c = Config(); c.StageComparison.AuxiliaryOffset = c.StageComparison.MeasurementOffset = 0;
                        if (legacy)
                        {
                            c.StageComparison.RemoveAuxiliaryMean = true; c.StageComparison.PhaseOrder = AuxiliaryPhaseOrder.IncreasingAnchors;
                            c.StageComparison.Grid = RelativePhaseGrid.CommonSpanTwoPass; c.StageComparison.WindowPlacement = DiagnosticWindowPlacement.DistanceSegment;
                            c.StageComparison.RemoveMeasurementMean = false; c.StageComparison.CenterLocalMagnitudes = true; c.NormalizeCrossCorrelation = true;
                        }
                        using (var r = engine.Prepare(input.Auxiliary.Values, input.Reference.Values, c.StageComparison, 2048, token))
                        using (var s = engine.Prepare(input.SignalAuxiliary.Values, input.Signal.Values, c.StageComparison, 2048, token))
                        {
                            if (legacy)
                            {
                                var ghzPerLag = 299792458.0 * Math.Min(r.Span, s.Span) / (2 * Math.PI * 1.4682 * 200 * 32768 * 1e9);
                                c.StageComparison.MaximumLagSamples = (int)Math.Ceiling(180 / ghzPerLag);
                            }
                            Execute(output, pair.Item1, legacy ? "legacy_controls_output_524k" : "nb_offsets_0_0", input, c, engine, r, s, token, completed);
                            if (legacy)
                            { c.StageComparison.OutputSamples = 17000000; c.StageComparison.StopFftBin = 262144; Execute(output, pair.Item1, "legacy_controls_output_17M", input, c, engine, r, s, token, completed); }
                        }
                    }
                    GC.Collect(); GC.WaitForPendingFinalizers();
                }
                status = "COMPLETED";
            }
            catch (Exception ex) { status = ex is OperationCanceledException ? "CANCELLED" : "FAILED"; error = ex.ToString(); Console.Error.WriteLine(error); }
            finally
            {
                JsonContractSerializer.Write(Path.Combine(output, "metrics.json"), new Dictionary<string, object> { ["status"] = status, ["error"] = error,
                    ["elapsed_seconds"] = clock.Elapsed.TotalSeconds, ["peak_working_set_bytes"] = Process.GetCurrentProcess().PeakWorkingSet64, ["completed_cases"] = string.Join("\n", completed) });
                JsonContractSerializer.Write(Path.Combine(output, "dataset_manifest.json"), sources);
                File.WriteAllText(Path.Combine(output, "summary.md"), "# " + status + "\n\nStage selection experiments on identical raw crops; executed " + completed.Count + " cases. Primary factorial fixes output count, offsets, measurement DC and correlation settings; additional controls are separate. No conclusion of physical validity from a desired profile shape.\n\n" + (error ?? "Numerical interpretation is recorded in the report and analysis metrics.") + "\n");
            }
            return status == "COMPLETED" ? 0 : 1;
        }

        private static IndependentOfdrProcessingConfig Config() => new IndependentOfdrProcessingConfig
        {
            StageComparisonEnabled = true, AllowFailedPhaseQcForDiagnostics = true, UsePhysicalAxes = false, NormalizeCrossCorrelation = false,
            WindowFunction = "Hann", SegmentSize = 4096, SegmentHop = 50, SegmentIfftZeroPadFactor = 8, MaxPreviewPoints = 2048,
            StageComparison = new StageComparisonSettings { RemoveAuxiliaryMean = false, PhaseOrder = AuxiliaryPhaseOrder.StablePhaseSort,
                Grid = RelativePhaseGrid.IndependentEndpoints, WindowPlacement = DiagnosticWindowPlacement.GlobalSignal,
                RemoveMeasurementMean = true, CenterLocalMagnitudes = false, OutputSamples = 524288, AuxiliaryOffset = 40, MeasurementOffset = 1 }
        };
        private static Tuple<OfdrChannelData, OfdrChannelData> ReadCrop(string path, List<NotebookSourceMetadata> sources, CancellationToken token)
        {
            Console.WriteLine("Reading " + path); var recording = new TekScopeCsvReader().Read(path, token);
            var trigger = Array.FindIndex(recording.SweepTrigger, v => v > 3); var first = trigger + 15000; const int count = 17000000;
            if (trigger < 0 || (long)first + count > recording.SampleCount || recording.InvalidSampleIndices.Any(i => i >= first && i < first + count)) throw new ArgumentException("Continuous finite 17M crop unavailable: " + path);
            var a = new double[count]; var m = new double[count]; Array.Copy(recording.Auxiliary, first, a, 0, count); Array.Copy(recording.Measurement, first, m, 0, count);
            sources.Add(new NotebookSourceMetadata { Key = Path.GetFileName(path), Path = path, Sha256 = recording.Sha256, Header = recording.Header,
                Samples = recording.SampleCount, SampleIntervalSeconds = recording.SampleIntervalSeconds,
                Processing = new NotebookSweepMetrics { SourceSamples = recording.SampleCount, CropStartIndex = first, UsedSamples = count, TriggerIndex = trigger, SampleIntervalSeconds = recording.SampleIntervalSeconds } });
            return Tuple.Create(new OfdrChannelData { SourcePath = path, MeasurementId = Path.GetFileName(path) + ":CH3", ChannelNumber = 3, Values = a, SampleIntervalSeconds = recording.SampleIntervalSeconds },
                new OfdrChannelData { SourcePath = path, MeasurementId = Path.GetFileName(path) + ":CH4", ChannelNumber = 4, Values = m, SampleIntervalSeconds = recording.SampleIntervalSeconds });
        }
        private static void Execute(string root, string label, string name, IndependentOfdrInput input, IndependentOfdrProcessingConfig c,
            StageComparisonEngine engine, StageComparisonEngine.PhasePlan r, StageComparisonEngine.PhasePlan s, CancellationToken token, List<string> completed)
        {
            var folder = Path.Combine(root, label, name); Directory.CreateDirectory(folder); var watch = Stopwatch.StartNew();
            Console.WriteLine(label + " / " + name); File.WriteAllText(Path.Combine(root, "progress.txt"), label + "/" + name);
            JsonContractSerializer.Write(Path.Combine(folder, "config.json"), c); File.WriteAllText(Path.Combine(folder, "summary.md"), "# RUNNING\n");
            try
            {
                using (var result = engine.RunPrepared(input, c, r, s, token, includeSelfAndRawFft: false))
                {
                    using (var w = new StreamWriter(Path.Combine(folder, "profile.csv")))
                    { w.WriteLine("profile_index,fft_bin,lag_samples"); for (var i = 0; i < result.NotebookProfile.Shifts.Length; i++) w.WriteLine(i + "," + result.NotebookProfile.Positions[i] + "," + result.NotebookProfile.Shifts[i].ToString("R", CultureInfo.InvariantCulture)); }
                    JsonContractSerializer.Write(Path.Combine(folder, "result.json"), result);
                    foreach (var point in new[] { 1000, 2500, 2750, 3000, 3500 })
                    {
                        var targetStart = point * c.SegmentHop - c.SegmentSize / 2; var index = Array.IndexOf(result.SegmentStarts, targetStart);
                        if (index >= 0) JsonContractSerializer.Write(Path.Combine(folder, "segment_" + point + ".json"), engine.Inspect(result, index));
                    }
                }
                completed.Add(label + "/" + name);
                JsonContractSerializer.Write(Path.Combine(folder, "metrics.json"), new Dictionary<string, object> { ["status"] = "COMPLETED", ["elapsed_seconds"] = watch.Elapsed.TotalSeconds, ["peak_working_set_bytes"] = Process.GetCurrentProcess().PeakWorkingSet64 });
                File.WriteAllText(Path.Combine(folder, "summary.md"), "# COMPLETED\n\n" + c.StageComparison.Description + "\n\nDiagnostic processing only; phase failures are preserved.\n");
            }
            catch (Exception ex)
            {
                JsonContractSerializer.Write(Path.Combine(folder, "metrics.json"), new Dictionary<string, string> { ["status"] = ex is OperationCanceledException ? "CANCELLED" : "FAILED", ["error"] = ex.ToString() });
                File.WriteAllText(Path.Combine(folder, "summary.md"), "# FAILED/CANCELLED\n\n" + ex); throw;
            }
        }

        // Research-only anti-alias control. Never used by the application processing path.
        private static int RunResamplingControl(IDictionary<string, string> options)
        {
            var output = Path.GetFullPath(options["output"]); if (Directory.Exists(output)) throw new IOException("Experiment already exists: " + output);
            Directory.CreateDirectory(output); var watch = Stopwatch.StartNew(); var sources = new List<NotebookSourceMetadata>();
            var cancellation = new CancellationTokenSource(); Console.CancelKeyPress += (s, e) => { e.Cancel = true; cancellation.Cancel(); }; var token = cancellation.Token;
            var status = "RUNNING"; string error = null; var metrics = new Dictionary<string, double>();
            JsonContractSerializer.Write(Path.Combine(output, "config.json"), new Dictionary<string, string> {
                ["input"] = options["input"], ["crop"] = "first CH1 > 3 + 15000, continuous 17M, mode-hop OFF",
                ["processing"] = "Notebook phase/DC/offset/independent span/global Hann; segment4096 hop50 pad8",
                ["control"] = "Uniform 17M k signal, FFT low-pass pass<=0.90 target Nyquist; cosine transition to zero at target Nyquist; inverse FFT; same endpoint linear resampling to 524288. Unfiltered two-pass control included.",
                ["limitation"] = "Diagnostic experiment only. Does not recover original optical phase or validate the physical heated location." });
            File.WriteAllText(Path.Combine(output, "summary.md"), "# RUNNING\n");
            try
            {
                var reference = ReadCrop(Path.Combine(options["input"], "tek0002ALL.csv"), sources, token); var c = Config(); var engine = new StageComparisonEngine();
                using (var rp = engine.Prepare(reference.Item1.Values, reference.Item2.Values, c.StageComparison, 2048, token))
                {
                    var rf = SamplingSpectra(engine, rp, c, token, "Ref", metrics);
                    foreach (var pair in new[] { Tuple.Create("60C", "tek0003ALL.csv"), Tuple.Create("80C", "tek0004ALL.csv"), Tuple.Create("100C", "tek0005ALL.csv") })
                    {
                        Console.WriteLine("Sampling control " + pair.Item1); var signal = ReadCrop(Path.Combine(options["input"], pair.Item2), sources, token);
                        JsonContractSerializer.Write(Path.Combine(output, "dataset_manifest.json"), sources);
                        using (var sp = engine.Prepare(signal.Item1.Values, signal.Item2.Values, c.StageComparison, 2048, token))
                        {
                            var sf = SamplingSpectra(engine, sp, c, token, pair.Item1, metrics);
                            for (var mode = 0; mode < 2; mode++)
                            {
                                var name = mode == 0 ? "dense_then_524k_unfiltered" : "dense_then_524k_lowpass";
                                var folder = Path.Combine(output, pair.Item1, name); Directory.CreateDirectory(folder);
                                JsonContractSerializer.Write(Path.Combine(folder, "config.json"), c);
                                var p = engine.Compare(rf[mode], sf[mode], c, token);
                                using (var w = new StreamWriter(Path.Combine(folder, "profile.csv"))) {
                                    w.WriteLine("profile_index,fft_bin,lag_samples"); for (var i = 0; i < p.Shifts.Length; i++) w.WriteLine(i + "," + p.Positions[i] + "," + p.Shifts[i].ToString("R", CultureInfo.InvariantCulture)); }
                                File.WriteAllText(Path.Combine(folder, "summary.md"), "# COMPLETED\n\n" + name + "\n");
                            }
                        }
                        GC.Collect();
                    }
                }
                status = "COMPLETED";
            }
            catch (Exception ex) { status = ex is OperationCanceledException ? "CANCELLED" : "FAILED"; error = ex.ToString(); Console.Error.WriteLine(error); }
            finally
            {
                JsonContractSerializer.Write(Path.Combine(output, "dataset_manifest.json"), sources);
                metrics["elapsed_seconds"] = watch.Elapsed.TotalSeconds; metrics["peak_working_set_bytes"] = Process.GetCurrentProcess().PeakWorkingSet64;
                JsonContractSerializer.Write(Path.Combine(output, "metrics.json"), metrics);
                JsonContractSerializer.Write(Path.Combine(output, "status.json"), new Dictionary<string, string> { ["status"] = status, ["error"] = error });
                File.WriteAllText(Path.Combine(output, "summary.md"), "# " + status + "\n\nAnti-alias research control, no product filter added.\n\n" + error);
            }
            return status == "COMPLETED" ? 0 : 1;
        }

        private static Complex[][] SamplingSpectra(StageComparisonEngine engine, StageComparisonEngine.PhasePlan phase, IndependentOfdrProcessingConfig c,
            CancellationToken token, string label, Dictionary<string, double> metrics)
        {
            const int denseCount = 17000000; var target = c.StageComparison.OutputSamples;
            var dense = engine.Interpolate(phase, denseCount, phase.Span, token);
            var unfiltered = engine.Spectrum(ResampleDense(dense, target, token), c, token);
            var fft = new FftEngine(); var a = new Complex[dense.Length]; double mean = dense.Average();
            for (var i = 0; i < dense.Length; i++) a[i] = new Complex(dense[i] - mean, 0);
            fft.ForwardInPlace(a); token.ThrowIfCancellationRequested();
            // Dense and sparse endpoint grids share phase span, but their FFT periods differ by N/(N-1).
            var nyquistBin = (target - 1.0) * denseCount / (2 * (denseCount - 1.0)); double total = 0, above = 0;
            for (var i = 1; i < a.Length / 2; i++) { var energy = a[i].Magnitude * a[i].Magnitude; total += energy; if (i >= nyquistBin) above += energy; }
            metrics[label + "_dense_energy_above_target_nyquist_fraction"] = above / Math.Max(1e-300, total);
            for (var i = 0; i < a.Length; i++)
            {
                if ((i & 0x3ffff) == 0) token.ThrowIfCancellationRequested();
                var bin = Math.Min(i, a.Length - i); var weight = bin <= .9 * nyquistBin ? 1 : bin >= nyquistBin ? 0 : .5 * (1 + Math.Cos(Math.PI * (bin / nyquistBin - .9) / .1)); a[i] *= weight;
            }
            fft.InverseInPlace(a); token.ThrowIfCancellationRequested();
            for (var i = 0; i < dense.Length; i++) dense[i] = a[i].Real;
            var filtered = engine.Spectrum(ResampleDense(dense, target, token), c, token);
            return new[] { unfiltered, filtered };
        }
        private static double[] ResampleDense(double[] dense, int count, CancellationToken token)
        {
            var output = new double[count];
            for (var i = 0; i < count; i++) {
                if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
                var position = i * (dense.Length - 1.0) / (count - 1.0); var left = Math.Min(dense.Length - 1, (int)position); var right = Math.Min(dense.Length - 1, left + 1);
                output[i] = dense[left] + (dense[right] - dense[left]) * (position - left);
            }
            return output;
        }
    }
}
