using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;

namespace OFDR_Harness
{
    // Bounded experiment only. Controls never become GUI/OFDR input files.
    internal static class StateDiscriminationCommand
    {
        public static int Run(IDictionary<string, string> options)
        {
            string value;
            if (!options.TryGetValue("config", out value)) throw new ArgumentException("An explicit experiment config is required.");
            var config = JsonContractSerializer.Read<StateDiscriminationConfig>(value);
            var log = new SweepRunRecorder("state_discrimination", new Dictionary<string, string> {
                ["scope"] = options.ContainsKey("controls-only") ? "Synthetic known local shift only; no original samples read" : "One common continuous original window per file, <=250000 numeric samples/file; full-byte hashes separately",
                ["goal"] = "Compare within-state and between-state pairs, self control and destroyed-fingerprint controls",
                ["physical_path"] = "200 m provisional travel-path difference; calibrated FUT origin/end unknown",
                ["filter"] = "Diagnostic 9 MHz Blackman-sinc then 20 MS/s; not a product correction",
                ["qc"] = "1e-5 unchanged; diagnostic continuation retains failures",
                ["temperature"] = "User reports at least 15 C difference across approximately 2 m; location unknown",
                ["full_record_processing"] = "false", ["product_changes"] = "false" }, options.TryGetValue("results", out value) ? value : null);
            Exception failure = null; var metrics = new Dictionary<string, double>();
            using (var cts = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler cancel = (s, e) => { e.Cancel = true; cts.Cancel(); }; Console.CancelKeyPress += cancel;
                try
                {
                    config.Validate(); var token = cts.Token;
                    if (options.TryGetValue("cancel-after-ms", out value)) cts.CancelAfter(int.Parse(value, CultureInfo.InvariantCulture));
                    JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "experiment_config.json"), config);
                    JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "software.json"), new[] { typeof(StateDiscriminationCommand).Assembly.Location, typeof(AcquisitionDiagnostics).Assembly.Location }.ToDictionary(Path.GetFileName, Hash));
                    var fft = new FftEngine(); if (!fft.NativeProviderAvailable) throw new InvalidOperationException("This recorded experiment requires native MKL: " + fft.NativeProviderFailure);
                    var diagnostic = new AcquisitionDiagnostics(fft); File.WriteAllText(Path.Combine(log.DirectoryPath, "fft_provider.txt"), fft.ProviderDescription);
                    if (options.ContainsKey("controls-only"))
                    {
                        KnownLocalShiftControl(diagnostic, fft, log.DirectoryPath, metrics, token);
                        Console.WriteLine(log.DirectoryPath); return 0;
                    }
                    var blocks = new List<AcquisitionDiagnosticBlock>(); var converted = new List<AcquisitionDiagnosticBlock>();
                    for (var i = 0; i < config.Files.Length; i++)
                    {
                        Console.WriteLine("Reading bounded window: " + config.Files[i]);
                        var source = new AcquisitionDiagnosticReader().ReadCsvRanges(config.Files[i], new[] { config.StartIndex }, config.WindowSamples, token);
                        JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "source_" + i + ".json"), source);
                        if (Math.Abs(source.SampleIntervalSeconds / 2e-8 - 1) > 1e-9) throw new InvalidDataException("This experiment requires a 50 MS/s header.");
                        var b = source.Blocks.Single();
                        if (b.Trigger.Any(v => !ModeHopSettings.IsFinite(v) || v > 1.8) || b.Measurement.Any(v => !ModeHopSettings.IsFinite(v))
                            || b.Auxiliary.Any(v => !ModeHopSettings.IsFinite(v)) || b.SweepTrigger.Any(v => !ModeHopSettings.IsFinite(v)))
                            throw new InvalidDataException("Configured continuous window contains mode hop or invalid channel samples. No silent cropping/concatenation allowed.");
                        blocks.Add(b); converted.Add(Convert(b, token));
                        JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "file" + i + "_raw_aux.json"), diagnostic.Analyze(b.Auxiliary, b.SampleIntervalSeconds, null, token));
                        JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "file" + i + "_filtered20_aux.json"), diagnostic.Analyze(converted[i].Auxiliary, converted[i].SampleIntervalSeconds, null, token));
                        JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "file" + i + "_raw_sig.json"), diagnostic.Spectrum(b.Measurement, b.SampleIntervalSeconds, token));
                        var levels = b.Measurement.Distinct().OrderBy(v => v).ToArray();
                        var stats = InspectDistanceEnergy(b, fft, token);
                        stats["measurement_voltage_levels"] = levels.Length;
                        stats["measurement_minimum_voltage_step"] = levels.Length < 2 ? 0 : Enumerable.Range(1, levels.Length - 1).Min(j => levels[j] - levels[j - 1]);
                        stats["measurement_min_v"] = levels.First(); stats["measurement_max_v"] = levels.Last();
                        stats["original_start_index"] = config.StartIndex; stats["numeric_samples"] = source.NumericSamples;
                        JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "file" + i + "_energy_context.json"), stats);
                        metrics["file" + i + "_numeric_samples"] = source.NumericSamples;
                    }
                    for (var a = 0; a < blocks.Count; a++) for (var b = a + 1; b < blocks.Count; b++)
                    {
                        var id = "pair_" + a + "_" + b;
                        SavePair(id + "_raw", blocks[a], blocks[b], diagnostic, log.DirectoryPath, metrics, token);
                        SavePair(id + "_filtered20", converted[a], converted[b], diagnostic, log.DirectoryPath, metrics, token);
                    }
                    SavePair("control_self", blocks[0], blocks[0], diagnostic, log.DirectoryPath, metrics, token);
                    var shuffled = (double[])blocks[0].Measurement.Clone(); var random = new Random(20261106);
                    for (var i = shuffled.Length - 1; i > 0; i--) { var j = random.Next(i + 1); var t = shuffled[i]; shuffled[i] = shuffled[j]; shuffled[j] = t; }
                    SavePair("control_shuffled_measurement", blocks[0], WithSignal(blocks[0], shuffled), diagnostic, log.DirectoryPath, metrics, token);
                    // Independent Gaussian sequences contain no common Rayleigh fingerprint.
                    // A shared envelope is a null control for apparently stable zero-shift profiles.
                    var envelope = RmsEnvelope(blocks[0].Measurement, 1024);
                    var noiseA = Gaussian(blocks[0].Measurement.Length, 311); var noiseB = Gaussian(noiseA.Length, 917);
                    SavePair("control_independent_stationary_noise", WithSignal(blocks[0], noiseA), WithSignal(blocks[0], noiseB), diagnostic, log.DirectoryPath, metrics, token);
                    SavePair("control_independent_common_envelope_noise", WithSignal(blocks[0], noiseA.Select((v, i) => v * envelope[i]).ToArray()),
                        WithSignal(blocks[0], noiseB.Select((v, i) => v * envelope[i]).ToArray()), diagnostic, log.DirectoryPath, metrics, token);
                    metrics["original_files"] = blocks.Count; metrics["full_record_processed"] = 0;
                    metrics["temperature_difference_minimum_reported_c"] = 15; metrics["heated_length_reported_m"] = 2;
                    metrics["heated_position_known"] = 0; metrics["absolute_optical_alignment_verified"] = 0;
                    metrics["control_noise_raw_pearson"] = Pearson(noiseA, noiseB);
                }
                catch (Exception ex) { failure = ex; Console.Error.WriteLine(ex); }
                finally { Console.CancelKeyPress -= cancel; log.Finish(failure, metrics); }
            }
            File.AppendAllText(Path.Combine(log.DirectoryPath, "summary.md"), options.ContainsKey("controls-only") ? "\nSynthetic positive control failed or was cancelled; original CSVs were not read.\n" : "\nExecution success is not accepted temperature/strain measurement. One 4.6 ms no-join window is much narrower than the saved 15 ms GUI input; its limited optical span must be considered before interpreting cross-state failure. Controls replace CH4 only in diagnostic memory. No source CSV, production algorithm, UI or QC criterion was modified.\n");
            Console.WriteLine(log.DirectoryPath); return failure == null ? 0 : failure is OperationCanceledException ? 2 : 1;
        }

        private static void KnownLocalShiftControl(AcquisitionDiagnostics diagnostic, FftEngine fft, string folder, Dictionary<string, double> metrics, CancellationToken token)
        {
            const int count = 196608, cycles = 60000;
            const double expected = 18.7, path = 200;
            var phaseSpan = 2 * Math.PI * cycles * (count - 1.0) / count;
            var opticalSpan = 299792458.0 * phaseSpan / (2 * Math.PI * 1.4682 * path) / 1e9;
            var distanceBin = Math.PI * path / phaseSpan;
            var shiftSamples = expected / opticalSpan * (count - 1.0);
            var a = new Complex[count]; var b = new Complex[count]; var rng = new Random(7391);
            for (var i = 1; i < count / 2; i++)
            {
                var z = i * distanceBin; if (z < 6 || z > 24) continue;
                a[i] = Complex.FromPolarCoordinates(.5 + rng.NextDouble(), 2 * Math.PI * rng.NextDouble());
                b[i] = z >= 12 && z < 14 ? a[i] * Complex.FromPolarCoordinates(1, -2 * Math.PI * i * shiftSamples / count) : a[i];
                a[count - i] = Complex.Conjugate(a[i]); b[count - i] = Complex.Conjugate(b[i]);
            }
            fft.InverseInPlace(a); fft.InverseInPlace(b);
            var block = new AcquisitionDiagnosticBlock { SourcePath = "synthetic-known-local-shift", StartIndex = 0, SampleIntervalSeconds = 2e-8,
                Auxiliary = Enumerable.Range(0, count).Select(i => Math.Cos(2 * Math.PI * cycles * i / count)).ToArray(),
                Measurement = a.Select(v => v.Real).ToArray(), Trigger = new double[count], SourceIndices = Enumerable.Range(0, count).ToArray() };
            var report = diagnostic.RunPair(block, WithSignal(block, b.Select(v => v.Real).ToArray()), path, true, true, token);
            JsonContractSerializer.Write(Path.Combine(folder, "synthetic_known_local_shift.json"), report);
            var inside = report.SpectralShiftGHz.Where((v, i) => report.DistancesMeters[i] >= 12.3 && report.DistancesMeters[i] <= 13.7).OrderBy(v => v).ToArray();
            var outside = report.SpectralShiftGHz.Where((v, i) => (report.DistancesMeters[i] >= 8 && report.DistancesMeters[i] <= 11) || (report.DistancesMeters[i] >= 15 && report.DistancesMeters[i] <= 22)).Select(Math.Abs).OrderBy(v => v).ToArray();
            metrics["synthetic_samples"] = count; metrics["original_numeric_samples"] = 0; metrics["expected_local_shift_ghz"] = expected;
            metrics["expected_region_start_m"] = 12; metrics["expected_region_end_m"] = 14;
            metrics["synthetic_optical_span_ghz"] = opticalSpan;
            metrics["recovered_inside_median_ghz"] = AcquisitionDiagnostics.Quantile(inside, .5);
            metrics["outside_abs_shift_p90_ghz"] = AcquisitionDiagnostics.Quantile(outside, .9);
            metrics["phase_qc_passed"] = report.Metrics["phase_qc_passed"];
            if (inside.Length == 0 || outside.Length == 0 || Math.Abs(metrics["recovered_inside_median_ghz"] - expected) > .1 || metrics["outside_abs_shift_p90_ghz"] > .1 || report.Metrics["phase_qc_passed"] != 1)
                throw new InvalidOperationException("Known local +18.7 GHz synthetic control failed its predeclared 0.1 GHz inside/outside tolerances.");
        }

        private static void SavePair(string id, AcquisitionDiagnosticBlock a, AcquisitionDiagnosticBlock b, AcquisitionDiagnostics diagnostic, string folder, Dictionary<string, double> metrics, CancellationToken token)
        {
            Console.WriteLine(id); token.ThrowIfCancellationRequested();
            try { JsonContractSerializer.Write(Path.Combine(folder, id + ".json"), diagnostic.RunPair(a, b, 200, true, true, token)); metrics["pair_variants_completed"] = metrics.ContainsKey("pair_variants_completed") ? metrics["pair_variants_completed"] + 1 : 1; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { File.WriteAllText(Path.Combine(folder, id + "_failure.txt"), ex.ToString()); metrics["pair_variants_failed"] = metrics.ContainsKey("pair_variants_failed") ? metrics["pair_variants_failed"] + 1 : 1; }
        }

        private static Dictionary<string, double> InspectDistanceEnergy(AcquisitionDiagnosticBlock b, FftEngine fft, CancellationToken token)
        {
            var linearized = new AuxKLinearizer(fft).LinearizeDetailed(b.Auxiliary, b.Measurement, b.Measurement, 1e-5, 0, token, null, true).Linearized;
            var spectrum = linearized.XPolarization.Select(v => new Complex(v, 0)).ToArray(); fft.ForwardInPlace(spectrum);
            var energy = spectrum.Take(spectrum.Length / 2).Select(v => v.Magnitude * v.Magnitude).ToArray(); energy[0] = 0;
            var total = energy.Sum(); var peak = Array.IndexOf(energy, energy.Max()); var step = Math.PI * 200 / linearized.Metrics.PhaseSpanRadians;
            var result = new Dictionary<string, double> { ["distance_bin_m"] = step, ["strongest_peak_m"] = peak * step,
                ["positive_fft_energy_excluding_dc"] = total, ["phase_span_rad"] = linearized.Metrics.PhaseSpanRadians };
            foreach (var width in new[] { .25, 1.0 }) result["energy_fraction_within_peak_plusminus_" + width.ToString(CultureInfo.InvariantCulture) + "m"] = energy.Where((v, i) => Math.Abs(i - peak) * step <= width).Sum() / total;
            foreach (var range in new[] { new[] { 0, 8 }, new[] { 8, 16 }, new[] { 16, 20 }, new[] { 20, 24 }, new[] { 24, 48 } })
                result["energy_fraction_" + range[0] + "_" + range[1] + "m"] = energy.Where((v, i) => i * step >= range[0] && i * step < range[1]).Sum() / total;
            return result;
        }

        private static AcquisitionDiagnosticBlock Convert(AcquisitionDiagnosticBlock b, CancellationToken token)
        {
            const int radius = 160; const double target = 20e6;
            var aux = AcquisitionDiagnostics.Resample(b.Auxiliary, 50e6, target, token, radius);
            return new AcquisitionDiagnosticBlock { SourcePath = b.SourcePath, StartIndex = b.StartIndex + radius, SampleIntervalSeconds = 1 / target,
                Auxiliary = aux, Measurement = AcquisitionDiagnostics.Resample(b.Measurement, 50e6, target, token, radius), Trigger = new double[aux.Length],
                SourceIndices = Enumerable.Range(0, aux.Length).Select(i => b.SourceIndices[radius + (int)Math.Round(i * 2.5)]).ToArray() };
        }
        private static AcquisitionDiagnosticBlock WithSignal(AcquisitionDiagnosticBlock b, double[] values) => new AcquisitionDiagnosticBlock {
            SourcePath = b.SourcePath + "#diagnostic-control", StartIndex = b.StartIndex, SampleIntervalSeconds = b.SampleIntervalSeconds,
            Auxiliary = b.Auxiliary, Measurement = values, SourceIndices = b.SourceIndices, Trigger = b.Trigger, SweepTrigger = b.SweepTrigger };
        private static double[] RmsEnvelope(double[] values, int radius)
        {
            var mean = values.Average(); var sums = new double[values.Length + 1];
            for (var i = 0; i < values.Length; i++) sums[i + 1] = sums[i] + Math.Pow(values[i] - mean, 2);
            return Enumerable.Range(0, values.Length).Select(i => { var a = Math.Max(0, i - radius); var b = Math.Min(values.Length, i + radius + 1); return Math.Sqrt((sums[b] - sums[a]) / (b - a)); }).ToArray();
        }
        private static double[] Gaussian(int count, int seed)
        {
            var rng = new Random(seed); return Enumerable.Range(0, count).Select(i => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble())).ToArray();
        }
        private static double Pearson(double[] a, double[] b)
        {
            var ma = a.Average(); var mb = b.Average(); var aa = 0.0; var bb = 0.0; var ab = 0.0;
            for (var i = 0; i < a.Length; i++) { var x = a[i] - ma; var y = b[i] - mb; aa += x * x; bb += y * y; ab += x * y; }
            return ab / Math.Sqrt(aa * bb);
        }
        private static string Hash(string path) { using (var s = File.OpenRead(path)) using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant(); }
    }

    [DataContract]
    internal sealed class StateDiscriminationConfig
    {
        [DataMember] public string[] Files { get; set; }
        [DataMember] public string[] States { get; set; }
        [DataMember] public int StartIndex { get; set; }
        [DataMember] public int WindowSamples { get; set; }
        public void Validate()
        {
            if (Files == null || States == null || Files.Length != 5 || States.Length != Files.Length || Files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Files.Length || StartIndex < 0 || WindowSamples < 8192 || WindowSamples > 250000)
                throw new ArgumentException("Five unique, explicitly labelled sources and a bounded numeric range are required.");
        }
    }
}
