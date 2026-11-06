using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;

namespace OFDR_Harness
{
    internal static class PartialAcquisitionCommand
    {
        public static int Run(IDictionary<string, string> options)
        {
            string value;
            var config = options.TryGetValue("config", out value) ? JsonContractSerializer.Read<PartialAcquisitionConfig>(value) : new PartialAcquisitionConfig();
            var root = options.TryGetValue("results", out value) ? value : Path.Combine("harness", "results");
            var log = new SweepRunRecorder("partial_acquisition", new Dictionary<string, string> {
                ["reference"] = config.ReferencePath, ["signal"] = config.SignalPath,
                ["role_meaning"] = config.AcquisitionState ?? "Comparison order only; both wet/cold, no known temperature baseline or delta",
                ["scope"] = "At most 250000 original samples per file; three explicit partial-record windows; full-byte SHA separately",
                ["channel_map"] = "CH1 sweep trigger; CH2 mode hop; CH3 auxiliary; CH4 measurement",
                ["qc_limit"] = "1e-5, unchanged", ["production_algorithm_modified"] = "false",
                ["virtual_filter"] = "Diagnostic Blackman sinc, cutoff 0.45 * target rate, physical half-support 3.2 us; not a product correction",
                ["exclusion"] = "Finite CH2 > 1.8 V and any-channel infinity, longest continuous Low selection, no extra tail or inferred pre-window state" }, root);
            var metrics = new Dictionary<string, double>(); Exception failure = null;
            using (var cts = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler cancel = (s, e) => { e.Cancel = true; cts.Cancel(); }; Console.CancelKeyPress += cancel;
                try
                {
                    config.Validate();
                    if (options.TryGetValue("cancel-after-ms", out value)) cts.CancelAfter(int.Parse(value, CultureInfo.InvariantCulture));
                    JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "experiment_config.json"), config);
                    var assemblies = new[] { typeof(PartialAcquisitionCommand).Assembly.Location, typeof(AcquisitionDiagnostics).Assembly.Location };
                    JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "software.json"), assemblies.ToDictionary(Path.GetFileName, Hash));
                    var reader = new AcquisitionDiagnosticReader(); var diagnostic = new AcquisitionDiagnostics(); var token = cts.Token;
                    File.WriteAllText(Path.Combine(log.DirectoryPath, "fft_provider.txt"), diagnostic.Provider);
                    var sources = new AcquisitionDiagnosticSource[2]; var paths = new[] { config.ReferencePath, config.SignalPath };
                    for (var role = 0; role < 2; role++)
                    {
                        Console.WriteLine("Reading bounded ranges: " + paths[role]);
                        sources[role] = reader.ReadCsvRanges(paths[role], config.StartIndices, config.WindowSamples, token);
                        JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "source_" + role + ".json"), sources[role]);
                        if (Math.Abs(1 / sources[role].SampleIntervalSeconds / config.ExpectedSampleRate - 1) > 1e-9)
                            throw new InvalidDataException("Header sample rate differs from the explicitly configured rate.");
                        metrics["source_" + role + "_numeric_samples"] = sources[role].NumericSamples;
                        metrics["source_" + role + "_declared_duration_s"] = sources[role].DeclaredSamples * sources[role].SampleIntervalSeconds;
                        for (var w = 0; w < sources[role].Blocks.Count; w++)
                            AnalyzeWindow("file" + role + "_w" + (w + 1), sources[role].Blocks[w], diagnostic, log.DirectoryPath, metrics, token);
                    }
                    for (var w = 0; w < config.StartIndices.Length; w++)
                    {
                        token.ThrowIfCancellationRequested(); var name = "pair_w" + (w + 1);
                        AcquisitionDiagnosticBlock a, b;
                        try
                        {
                            var ca = AcquisitionDiagnostics.Exclude(sources[0].Blocks[w], 0, true, token);
                            var cb = AcquisitionDiagnostics.Exclude(sources[1].Blocks[w], 0, true, token);
                            // A common time-index interval avoids comparing different cropped supports.
                            // This does not establish the same absolute optical-frequency interval.
                            var start = Math.Max(ca.SourceIndices[0], cb.SourceIndices[0]);
                            var end = Math.Min(ca.SourceIndices.Last(), cb.SourceIndices.Last()) + 1;
                            a = AcquisitionDiagnostics.Select(ca, Enumerable.Range(0, ca.Auxiliary.Length).Where(i => ca.SourceIndices[i] >= start && ca.SourceIndices[i] < end).ToArray());
                            b = AcquisitionDiagnostics.Select(cb, Enumerable.Range(0, cb.Auxiliary.Length).Where(i => cb.SourceIndices[i] >= start && cb.SourceIndices[i] < end).ToArray());
                        }
                        catch (InvalidOperationException ex) { SaveFailure(log.DirectoryPath, name + "_selection", ex, metrics); continue; }
                        SavePair(name + "_raw" + RateLabel(1 / a.SampleIntervalSeconds), a, b, config, diagnostic, log.DirectoryPath, metrics, token);
                        SavePair(name + "_self_control", a, a, config, diagnostic, log.DirectoryPath, metrics, token);
                        foreach (var rate in new[] { 50e6, 20e6 })
                        {
                            var convertedA = Convert(a, rate, token); var convertedB = Convert(b, rate, token);
                            SavePair(name + "_filtered" + RateLabel(rate), convertedA, convertedB, config, diagnostic, log.DirectoryPath, metrics, token);
                        }
                    }
                    if (config.StartIndices.Length > 1)
                    {
                        var a = AcquisitionDiagnostics.Exclude(sources[0].Blocks[0], 0, true, token);
                        var b = AcquisitionDiagnostics.Exclude(sources[1].Blocks.Last(), 0, true, token);
                        SavePair("control_mismatched_time_filtered20", Convert(a, 20e6, token), Convert(b, 20e6, token), config, diagnostic, log.DirectoryPath, metrics, token);
                    }
                    var lambda = config.CenterWavelengthNm * 1e-9; var opticalRate = 299792458.0 * config.SweepRateNmPerSecond * 1e-9 / (lambda * lambda);
                    metrics["expected_auxiliary_fringe_hz"] = config.GroupIndex * config.PhysicalPathMeters * config.SweepRateNmPerSecond * 1e-9 / (lambda * lambda);
                    metrics["nominal_samples_per_fringe"] = config.ExpectedSampleRate / metrics["expected_auxiliary_fringe_hz"];
                    metrics["ideal_optical_span_per_window_ghz"] = config.WindowSamples / config.ExpectedSampleRate * opticalRate / 1e9;
                    metrics["ideal_optical_span_in_full_header_record_ghz"] = sources[0].DeclaredSamples / config.ExpectedSampleRate * opticalRate / 1e9;
                    metrics["minimum_time_for_plusminus180ghz_s"] = 360e9 / opticalRate;
                    metrics["minimum_samples_for_plusminus180ghz"] = Math.Ceiling(360e9 / opticalRate * config.ExpectedSampleRate);
                    metrics["full_record_numerically_processed"] = 0; metrics["temperature_delta_known"] = 0;
                    metrics["absolute_optical_alignment_verified"] = 0;
                }
                catch (Exception ex) { failure = ex; Console.Error.WriteLine(ex); }
                finally { Console.CancelKeyPress -= cancel; log.Finish(failure, metrics); }
            }
            File.AppendAllText(Path.Combine(log.DirectoryPath, "summary.md"), "\nCompletion denotes experiment execution, not phase acceptance or calibrated strain. Individual failures are retained in *_failure.txt. Acquisition state: " + (config.AcquisitionState ?? "Both files wet/cold; temperature difference unknown") + ". No complete sweep is reconstructed, no missing phase is invented, no QC limit or production filter was changed. Full-record numeric processing and GUI changes were not performed.\n");
            Console.WriteLine(log.DirectoryPath); return failure == null ? 0 : failure is OperationCanceledException ? 2 : 1;
        }

        private static void AnalyzeWindow(string name, AcquisitionDiagnosticBlock block, AcquisitionDiagnostics diagnostic, string folder, Dictionary<string, double> metrics, CancellationToken token)
        {
            var rawLabel = RateLabel(1 / block.SampleIntervalSeconds);
            var raw = diagnostic.Analyze(block.Auxiliary, block.SampleIntervalSeconds, block.Joins, token);
            JsonContractSerializer.Write(Path.Combine(folder, name + "_raw" + rawLabel + "_aux.json"), raw);
            var context = new Dictionary<string, double> { ["start_index"] = block.StartIndex, ["end_index_exclusive"] = block.StartIndex + block.Auxiliary.Length,
                ["record_relative_start_s"] = block.StartIndex * block.SampleIntervalSeconds, ["mode_hop_high_fraction"] = block.Trigger.Count(v => v > 1.8) / (double)block.Trigger.Length,
                ["sweep_trigger_high_fraction"] = block.SweepTrigger.Count(v => v > 1.8) / (double)block.SweepTrigger.Length,
                ["measurement_inf_count"] = block.Measurement.Count(double.IsInfinity), ["aux_min_v"] = block.Auxiliary.Min(), ["aux_max_v"] = block.Auxiliary.Max(),
                ["aux_distinct_voltage_levels"] = block.Auxiliary.Distinct().Count(),
                ["measurement_distinct_voltage_levels"] = block.Measurement.Where(ModeHopSettings.IsFinite).Distinct().Count(),
                ["aux_minimum_voltage_step_v"] = MinimumVoltageStep(block.Auxiliary),
                ["measurement_minimum_voltage_step_v"] = MinimumVoltageStep(block.Measurement) };
            JsonContractSerializer.Write(Path.Combine(folder, name + "_context.json"), context);
            if (block.Measurement.All(ModeHopSettings.IsFinite)) JsonContractSerializer.Write(Path.Combine(folder, name + "_raw" + rawLabel + "_sig.json"), diagnostic.Spectrum(block.Measurement, block.SampleIntervalSeconds, token));
            AcquisitionDiagnosticBlock clean;
            try { clean = AcquisitionDiagnostics.Exclude(block, 0, true, token); }
            catch (InvalidOperationException ex) { SaveFailure(folder, name + "_clean", ex, metrics); return; }
            JsonContractSerializer.Write(Path.Combine(folder, name + "_clean" + rawLabel + "_aux.json"), diagnostic.Analyze(clean.Auxiliary, clean.SampleIntervalSeconds, clean.Joins, token));
            JsonContractSerializer.Write(Path.Combine(folder, name + "_clean_scope.json"), new Dictionary<string, double> { ["start_index"] = clean.StartIndex, ["samples"] = clean.Auxiliary.Length, ["joins"] = clean.Joins.Length });
            var sourceRate = 1 / clean.SampleIntervalSeconds; var radius = Radius(sourceRate);
            var converted = new Dictionary<double, AcquisitionDiagnosticBlock>();
            foreach (var rate in new[] { 50e6, 20e6, 2.5e6 })
            {
                token.ThrowIfCancellationRequested(); var id = name + "_filtered" + RateLabel(rate);
                var c = Convert(clean, rate, token); converted[rate] = c;
                var m = diagnostic.Analyze(c.Auxiliary, c.SampleIntervalSeconds, null, token);
                var baseline = diagnostic.Spectrum(clean.Auxiliary, clean.SampleIntervalSeconds, token);
                m.Values["ac_power_retained_fraction"] = Math.Pow(m.Values["ac_rms_v"] / baseline["ac_rms_v"], 2);
                JsonContractSerializer.Write(Path.Combine(folder, id + "_aux.json"), m);
                JsonContractSerializer.Write(Path.Combine(folder, id + "_sig.json"), diagnostic.Spectrum(c.Measurement, c.SampleIntervalSeconds, token));
                JsonContractSerializer.Write(Path.Combine(folder, id + "_phase_vs" + rawLabel + ".json"), diagnostic.CompareResampledPhase(clean.Auxiliary, c.Auxiliary, sourceRate, rate, token, radius));
                var step = checked((int)Math.Round(sourceRate / rate));
                var unfiltered = Enumerable.Range(0, c.Auxiliary.Length).Select(i => clean.Auxiliary[radius + i * step]).ToArray();
                JsonContractSerializer.Write(Path.Combine(folder, name + "_unfiltered" + RateLabel(rate) + "_aux.json"), diagnostic.Analyze(unfiltered, 1 / rate, null, token));
            }
            JsonContractSerializer.Write(Path.Combine(folder, name + "_phase20_vs50.json"), diagnostic.CompareResampledPhase(converted[50e6].Auxiliary, converted[20e6].Auxiliary, 50e6, 20e6, token, 0));
            var preview = new Dictionary<string, double[]>();
            foreach (var pair in new[] { new KeyValuePair<double, AcquisitionDiagnosticBlock>(sourceRate, clean) }.Concat(converted.Where(p => p.Key >= 20e6)))
            {
                var count = Math.Min(pair.Value.Auxiliary.Length, (int)Math.Ceiling(pair.Key * 10e-6));
                var prefix = RateLabel(pair.Key) + "MS"; var offset = pair.Key == sourceRate ? 0 : radius / sourceRate;
                preview[prefix + "_time_us"] = Enumerable.Range(0, count).Select(i => (offset + i / pair.Key) * 1e6).ToArray();
                preview[prefix + "_aux_v"] = pair.Value.Auxiliary.Take(count).ToArray();
                preview[prefix + "_sig_v"] = pair.Value.Measurement.Take(count).ToArray();
            }
            JsonContractSerializer.Write(Path.Combine(folder, name + "_waveform_preview.json"), preview);
        }

        private static string RateLabel(double rate) => (rate / 1e6).ToString("0.###", CultureInfo.InvariantCulture);
        private static double MinimumVoltageStep(double[] values)
        {
            var sorted = values.Where(ModeHopSettings.IsFinite).Distinct().OrderBy(v => v).ToArray();
            return sorted.Length < 2 ? 0 : Enumerable.Range(1, sorted.Length - 1).Min(i => sorted[i] - sorted[i - 1]);
        }
        private static int Radius(double sourceRate) => Math.Max(16, (int)Math.Ceiling(3.2e-6 * sourceRate));
        private static AcquisitionDiagnosticBlock Convert(AcquisitionDiagnosticBlock b, double rate, CancellationToken token)
        {
            var sourceRate = 1 / b.SampleIntervalSeconds; var radius = Radius(sourceRate);
            var aux = AcquisitionDiagnostics.Resample(b.Auxiliary, sourceRate, rate, token, radius);
            return new AcquisitionDiagnosticBlock { SourcePath = b.SourcePath, StartIndex = b.StartIndex + radius, SampleIntervalSeconds = 1 / rate,
                Auxiliary = aux, Measurement = AcquisitionDiagnostics.Resample(b.Measurement, sourceRate, rate, token, radius), Trigger = new double[aux.Length],
                SourceIndices = Enumerable.Range(0, aux.Length).Select(i => b.SourceIndices[checked(radius + (int)Math.Round(i * sourceRate / rate))]).ToArray() };
        }
        private static void SavePair(string name, AcquisitionDiagnosticBlock a, AcquisitionDiagnosticBlock b, PartialAcquisitionConfig config, AcquisitionDiagnostics diagnostic, string folder, Dictionary<string, double> metrics, CancellationToken token)
        {
            try { JsonContractSerializer.Write(Path.Combine(folder, name + ".json"), diagnostic.RunPair(a, b, config.PhysicalPathMeters, true, true, token)); metrics["completed_pair_variants"] = metrics.ContainsKey("completed_pair_variants") ? metrics["completed_pair_variants"] + 1 : 1; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { SaveFailure(folder, name, ex, metrics); }
        }
        private static void SaveFailure(string folder, string name, Exception ex, Dictionary<string, double> metrics)
        {
            File.WriteAllText(Path.Combine(folder, name + "_failure.txt"), ex.ToString());
            metrics["individual_diagnostic_failures"] = metrics.ContainsKey("individual_diagnostic_failures") ? metrics["individual_diagnostic_failures"] + 1 : 1;
        }
        private static string Hash(string path) { using (var s = File.OpenRead(path)) using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant(); }
    }

    [DataContract]
    public sealed class PartialAcquisitionConfig
    {
        [DataMember] public string ReferencePath { get; set; } = @"C:\Project\OFDR\Datas\OFDR Raw data\NoticeData\새 폴더\case_temp3\tek0028ALL.csv";
        [DataMember] public string SignalPath { get; set; } = @"C:\Project\OFDR\Datas\OFDR Raw data\NoticeData\새 폴더\case_temp3\tek0029ALL.csv";
        [DataMember] public int[] StartIndices { get; set; } = new[] { 2500000, 10000000, 17500000 };
        [DataMember] public int WindowSamples { get; set; } = 81920;
        [DataMember] public double ExpectedSampleRate { get; set; } = 500e6;
        [DataMember] public double PhysicalPathMeters { get; set; } = 200;
        [DataMember] public double CenterWavelengthNm { get; set; } = 1550;
        [DataMember] public double SweepRateNmPerSecond { get; set; } = 50;
        [DataMember] public double GroupIndex { get; set; } = 1.4682;
        [DataMember(EmitDefaultValue = false)] public string AcquisitionState { get; set; }
        public void Validate()
        {
            if (StartIndices == null || StartIndices.Length < 1 || WindowSamples < 8192 || (long)StartIndices.Length * WindowSamples > 250000 || (ExpectedSampleRate != 100e6 && ExpectedSampleRate != 500e6) || PhysicalPathMeters != 200 || CenterWavelengthNm != 1550 || SweepRateNmPerSecond != 50 || GroupIndex != 1.4682)
                throw new ArgumentException("Partial 100/500 MS/s comparison requires explicit bounded ranges and the recorded provisional optical settings.");
        }
    }
}
