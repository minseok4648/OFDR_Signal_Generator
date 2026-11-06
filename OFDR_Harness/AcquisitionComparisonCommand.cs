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
    internal static class AcquisitionComparisonCommand
    {
        public static int Run(IDictionary<string, string> options)
        {
            string configPath, value;
            var config = options.TryGetValue("config", out configPath) ? JsonContractSerializer.Read<AcquisitionComparisonConfig>(configPath) : new AcquisitionComparisonConfig();
            if (options.TryGetValue("input", out value)) config.DataRoot = value;
            var root = options.TryGetValue("results", out value) ? value : Path.Combine("harness", "results");
            var log = new SweepRunRecorder("acquisition_comparison", new Dictionary<string, string> {
                ["config_file"] = configPath ?? "built-in defaults", ["maximum_numeric_samples_per_file"] = "250000", ["scope"] = "Bounded windows plus existing historical results; full-file byte SHA only",
                ["production_algorithm_modified"] = "false", ["full_5m_10m_regression"] = "not run; user sample-limited scope", ["maximum_physical_scope_sampling_hz"] = config.MaximumScopeSampleRate.ToString("G17", CultureInfo.InvariantCulture),
                ["virtual_resampling"] = "Diagnostic Blackman-windowed sinc; not measured ADC behaviour or a production correction", ["temperature_states"] = "Heating approximately steady; Cooling actively cooling",
                ["run_timezone"] = "UTC identifiers; report date Asia/Seoul" }, root);
            var metrics = new Dictionary<string, double>(); Exception failure = null;
            using (var cancellation = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler cancel = (s, e) => { e.Cancel = true; cancellation.Cancel(); };
                Console.CancelKeyPress += cancel;
                try
                {
                    config.Validate();
                    if (options.TryGetValue("cancel-after-ms", out value)) cancellation.CancelAfter(int.Parse(value, CultureInfo.InvariantCulture));
                    JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "experiment_config.json"), config);
                    var assemblies = new[] { typeof(AcquisitionComparisonCommand).Assembly.Location, typeof(AcquisitionDiagnostics).Assembly.Location };
                    JsonContractSerializer.Write(Path.Combine(log.DirectoryPath, "software.json"), assemblies.ToDictionary(p => Path.GetFileName(p), Hash));
                    var token = cancellation.Token; var reader = new AcquisitionDiagnosticReader(); var diagnostics = new AcquisitionDiagnostics();
                    File.WriteAllText(Path.Combine(log.DirectoryPath, "fft_provider.txt"), diagnostics.Provider);
                    var highHistory = ReviewHistory(config.HighHistory, "notice_high", log.DirectoryPath, metrics);
                    var lowHistory = ReviewHistory(config.LowHistory, "notice_low", log.DirectoryPath, metrics);
                    var etri = new[] { "0_0mm0000CH1.isf", "0_4mm0000CH1.isf", "0_0mm0000CH2.isf", "0_4mm0000CH2.isf" }
                        .Select(p => reader.ReadIsf(Path.Combine(config.DataRoot, p), new[] { 250000, 2500000, 4500000 }, config.WindowSamples, token)).ToArray();
                    for (var i = 0; i < etri.Length; i++) SaveSource(log.DirectoryPath, "etri_" + i, etri[i]);
                    for (var i = 0; i < 3; i++) { etri[0].Blocks[i].Measurement = etri[2].Blocks[i].Auxiliary; etri[1].Blocks[i].Measurement = etri[3].Blocks[i].Auxiliary; }
                    DiagnosePair("etri", etri[0], etri[1], false, config, diagnostics, log.DirectoryPath, metrics, token);
                    var noticeRoot = Path.Combine(config.DataRoot, "NoticeData", "새 폴더");
                    RunNotice("notice_high", Path.Combine(noticeRoot, "case_temp", "Ref.csv"), Path.Combine(noticeRoot, "case_temp", "Temp 160C.csv"), highHistory, config, reader, diagnostics, log.DirectoryPath, metrics, token);
                    RunNotice("notice_low", Path.Combine(noticeRoot, "case_temp2", "Heating.csv"), Path.Combine(noticeRoot, "case_temp2", "Cooling.csv"), lowHistory, config, reader, diagnostics, log.DirectoryPath, metrics, token);
                    var goldenPath = Path.Combine(config.GoldenDirectory, "metrics.json");
                    if (!File.Exists(goldenPath)) throw new FileNotFoundException("Existing golden metrics are required.", goldenPath);
                    File.Copy(goldenPath, Path.Combine(log.DirectoryPath, "existing_golden_metrics.json"));
                    File.Copy(Path.Combine(config.GoldenDirectory, "summary.md"), Path.Combine(log.DirectoryPath, "existing_golden_summary.md"));
                    var wavelength = config.CenterWavelengthNm * 1e-9; var speed = config.SweepRateNmPerSecond * 1e-9;
                    metrics["expected_auxiliary_fringe_hz"] = config.GroupIndex * config.NoticePhysicalPathMeters * speed / (wavelength * wavelength);
                    metrics["physical_scope_nyquist_hz"] = config.MaximumScopeSampleRate / 2;
                    metrics["nominal_samples_per_auxiliary_cycle_at_scope_limit"] = config.MaximumScopeSampleRate / metrics["expected_auxiliary_fringe_hz"];
                    metrics["idealized_24m_backscatter_fringe_hz"] = 2 * config.GroupIndex * 24 * speed / (wavelength * wavelength);
                    metrics["optical_span_needed_for_requested_plusminus_180ghz_lower_bound_ghz"] = 360;
                    var opticalRate = 299792458.0 * speed / (wavelength * wavelength);
                    metrics["continuous_time_for_360ghz_lower_bound_s"] = 360e9 / opticalRate;
                    metrics["samples_at_scope_limit_for_360ghz_lower_bound"] = Math.Ceiling(360e9 / opticalRate * config.MaximumScopeSampleRate);
                    metrics["unique_numeric_samples_per_file_upper_bound"] = 3 * config.WindowSamples;
                    metrics["production_settings_changed"] = 0; metrics["full_record_numeric_processing"] = 0; metrics["physical_20msps_acquisition_verified"] = 0;
                }
                catch (Exception ex) { failure = ex; Console.Error.WriteLine(ex); }
                finally { Console.CancelKeyPress -= cancel; log.Finish(failure, metrics); }
            }
            File.AppendAllText(Path.Combine(log.DirectoryPath, "summary.md"),
                "\n## Interpretation limits\n\nA completed experiment is not phase-QC acceptance. All original QC failures remain. 20 MS/s is the physical scope limit; 50 MS/s files are historical evidence. " +
                "Virtual 20 MS/s resampling includes an explicit anti-alias filter and cannot establish physical ADC performance. No absolute optical-frequency marker or known heated-fibre coordinates are available. " +
                "Heating is approximately steady and Cooling is transient. No zero-baseline subtraction, phase-gap filling or QC relaxation is applied. " +
                "Historical full-selection metrics are read, not numerically recomputed. No new full 5M/10M regression or GUI modification.\n");
            Console.WriteLine(log.DirectoryPath); return failure == null ? 0 : failure is OperationCanceledException ? 2 : 1;
        }

        private static void RunNotice(string name, string referencePath, string signalPath, PreparedSweepMetadata[] historical, AcquisitionComparisonConfig config,
            AcquisitionDiagnosticReader reader, AcquisitionDiagnostics diagnostics, string folder, Dictionary<string, double> metrics, CancellationToken token)
        {
            // Match roles by SHA rather than the old filename (the high-rate files were renamed).
            var range = historical[0].Sweep;
            var reference = reader.ReadCsv(referencePath, range.StartIndex, range.EndIndexExclusive, 3, config.WindowSamples, token);
            var signal = reader.ReadCsv(signalPath, range.StartIndex, range.EndIndexExclusive, 3, config.WindowSamples, token);
            SaveSource(folder, name + "_reference", reference); SaveSource(folder, name + "_signal", signal);
            if (!historical.Any(h => string.Equals(h.SourceSha256, reference.Sha256, StringComparison.OrdinalIgnoreCase)) || !historical.Any(h => string.Equals(h.SourceSha256, signal.Sha256, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Historical and current source SHA-256 differ for " + name);
            DiagnosePair(name, reference, signal, true, config, diagnostics, folder, metrics, token);
        }

        private static void DiagnosePair(string name, AcquisitionDiagnosticSource reference, AcquisitionDiagnosticSource signal, bool notice, AcquisitionComparisonConfig config,
            AcquisitionDiagnostics diagnostics, string folder, Dictionary<string, double> aggregate, CancellationToken token)
        {
            Console.WriteLine("{0}: {1:N0} numeric samples per file, Fs {2:G6} MS/s", name, reference.NumericSamples, 1e-6 / reference.SampleIntervalSeconds);
            var directory = Path.Combine(folder, name); Directory.CreateDirectory(directory);
            var stableProfiles = new List<AcquisitionPairReport>();
            for (var w = 0; w < reference.Blocks.Count; w++)
            {
                token.ThrowIfCancellationRequested(); var rawRef = reference.Blocks[w]; var rawSig = signal.Blocks[w];
                foreach (var role in new[] { "reference", "signal" })
                {
                    var block = role == "reference" ? rawRef : rawSig; var prefix = "w" + w + "_" + role;
                    WriteMetrics(directory, prefix + "_raw", diagnostics.Analyze(block.Auxiliary, block.SampleIntervalSeconds, null, token), aggregate, name);
                    var clean = notice ? AcquisitionDiagnostics.Exclude(block, .0003, true, token) : block;
                    var original = diagnostics.Analyze(clean.Auxiliary, clean.SampleIntervalSeconds, clean.Joins, token);
                    WriteMetrics(directory, prefix + "_continuous", original, aggregate, name);
                    JsonContractSerializer.Write(Path.Combine(directory, prefix + "_continuous_indices.json"), new[] { clean.SourceIndices.First(), clean.SourceIndices.Last() + 1 });
                    JsonContractSerializer.Write(Path.Combine(directory, prefix + "_measurement_spectrum.json"), diagnostics.Spectrum(clean.Measurement, clean.SampleIntervalSeconds, token));
                    if (1 / clean.SampleIntervalSeconds > config.MaximumScopeSampleRate && notice)
                    {
                        var rate = 1 / clean.SampleIntervalSeconds; const int radius = 160;
                        var comparisonOriginal = clean.Auxiliary.Skip(radius).Take(clean.Auxiliary.Length - 2 * radius).ToArray();
                        var matching = diagnostics.Analyze(comparisonOriginal, clean.SampleIntervalSeconds, null, token);
                        WriteMetrics(directory, prefix + "_sampling_reference", matching, aggregate, name);
                        foreach (var target in new[] { config.MaximumScopeSampleRate, 2.5e6 })
                        {
                            var converted = AcquisitionDiagnostics.Resample(clean.Auxiliary, rate, target, token);
                            var measurement = AcquisitionDiagnostics.Resample(clean.Measurement, rate, target, token);
                            var result = diagnostics.Analyze(converted, 1 / target, null, token);
                            result.Values["ac_power_retained_fraction"] = Math.Pow(result.Values["ac_rms_v"] / matching.Values["ac_rms_v"], 2);
                            result.Values["phase_span_ratio_to_source"] = result.Phase.PhaseSpanRadians / matching.Phase.PhaseSpanRadians;
                            result.Values["source_trim_each_side_samples"] = radius;
                            result.Values["cutoff_hz"] = .45 * target;
                            foreach (var item in diagnostics.CompareResampledPhase(clean.Auxiliary, converted, rate, target, token)) result.Values[item.Key] = item.Value;
                            var suffix = "_filtered_" + (target / 1e6).ToString("G", CultureInfo.InvariantCulture) + "ms";
                            WriteMetrics(directory, prefix + suffix, result, aggregate, name);
                            JsonContractSerializer.Write(Path.Combine(directory, prefix + suffix + "_measurement_spectrum.json"), diagnostics.Spectrum(measurement, 1 / target, token));
                        }
                        var factor = (int)Math.Round(rate / 2.5e6);
                        foreach (var offset in new[] { 0, factor / 4, factor / 2, 3 * factor / 4 }.Distinct())
                        {
                            var samples = clean.Auxiliary.Skip(radius + offset).Take(clean.Auxiliary.Length - 2 * radius - offset).Where((v, i) => i % factor == 0).ToArray();
                            WriteMetrics(directory, prefix + "_unfiltered_2.5ms_offset" + offset, diagnostics.Analyze(samples, clean.SampleIntervalSeconds * factor, null, token), aggregate, name);
                        }
                    }
                }
                var tails = notice ? new[] { 0.0, .0001, .0003 } : new[] { 0.0 };
                foreach (var tail in tails)
                {
                    var r = notice ? AcquisitionDiagnostics.Exclude(rawRef, tail, false, token) : rawRef;
                    var s = notice ? AcquisitionDiagnostics.Exclude(rawSig, tail, false, token) : rawSig;
                    var tag = "w" + w + "_tail" + (tail * 1000).ToString("G", CultureInfo.InvariantCulture);
                    foreach (var role in new[] { "reference", "signal" })
                    {
                        var selected = role == "reference" ? r : s;
                        WriteMetrics(directory, tag + "_" + role, diagnostics.Analyze(selected.Auxiliary, selected.SampleIntervalSeconds, selected.Joins, token), aggregate, name);
                        JsonContractSerializer.Write(Path.Combine(directory, tag + "_" + role + "_source_map.json"), new Dictionary<string, int[]> {
                            ["source_indices"] = selected.SourceIndices, ["join_indices"] = selected.Joins });
                    }
                    var path = notice ? config.NoticePhysicalPathMeters : 18.2703;
                    var report = diagnostics.RunPair(r, s, path, notice, true, token);
                    SavePair(directory, tag + "_gauge", report, aggregate, name);
                    if (w == 0 && (!notice || tail == .0003))
                    {
                        SavePair(directory, tag + "_legacy1024", diagnostics.RunPair(r, s, path, notice, false, token), aggregate, name);
                        SavePair(directory, tag + "_self_control", diagnostics.RunPair(r, r, path, notice, true, token), aggregate, name);
                        if (notice)
                        {
                            SavePair(directory, tag + "_path100_gauge", diagnostics.RunPair(r, s, 100, true, true, token), aggregate, name);
                            SavePair(directory, tag + "_path100_legacy1024", diagnostics.RunPair(r, s, 100, true, false, token), aggregate, name);
                        }
                    }
                }
                var continuousRef = notice ? AcquisitionDiagnostics.Exclude(rawRef, .0003, true, token) : rawRef;
                var continuousSig = notice ? AcquisitionDiagnostics.Exclude(rawSig, .0003, true, token) : rawSig;
                var continuous = diagnostics.RunPair(continuousRef, continuousSig, notice ? config.NoticePhysicalPathMeters : 18.2703, notice, true, token);
                SavePair(directory, "w" + w + "_continuous_gauge", continuous, aggregate, name); stableProfiles.Add(continuous);
                if (notice && 1 / continuousRef.SampleIntervalSeconds > config.MaximumScopeSampleRate)
                {
                    var matchingRef = AcquisitionDiagnostics.Select(continuousRef, Enumerable.Range(160, continuousRef.Auxiliary.Length - 320).ToArray());
                    var matchingSig = AcquisitionDiagnostics.Select(continuousSig, Enumerable.Range(160, continuousSig.Auxiliary.Length - 320).ToArray());
                    SavePair(directory, "w" + w + "_matched_source_gauge", diagnostics.RunPair(matchingRef, matchingSig, config.NoticePhysicalPathMeters, true, true, token), aggregate, name);
                    var converted = diagnostics.RunPair(ConvertRate(continuousRef, config.MaximumScopeSampleRate, token), ConvertRate(continuousSig, config.MaximumScopeSampleRate, token), config.NoticePhysicalPathMeters, true, true, token);
                    converted.Interpretation += " Virtual rate conversion of both simultaneously acquired channels with the same filter and sample positions; no physical 20 MS/s capture was performed.";
                    converted.Metrics["source_ref_first_index"] = continuousRef.SourceIndices[0] + 160;
                    converted.Metrics["source_signal_first_index"] = continuousSig.SourceIndices[0] + 160;
                    converted.Metrics["source_index_step"] = 1 / (continuousRef.SampleIntervalSeconds * config.MaximumScopeSampleRate);
                    SavePair(directory, "w" + w + "_virtual20msps_gauge", converted, aggregate, name);
                }
                Console.WriteLine("{0} window {1}/3 complete", name, w + 1);
            }
            for (var w = 1; w < stableProfiles.Count; w++)
            {
                var first = stableProfiles[0]; var other = stableProfiles[w]; var differences = new List<double>();
                for (var i = 0; i < first.DistancesMeters.Length; i++)
                {
                    if (notice && first.DistancesMeters[i] > 24) continue;
                    var closest = Enumerable.Range(0, other.DistancesMeters.Length).OrderBy(j => Math.Abs(first.DistancesMeters[i] - other.DistancesMeters[j])).First();
                    if (Math.Abs(first.DistancesMeters[i] - other.DistancesMeters[closest]) <= .125) differences.Add(Math.Abs(first.SpectralShiftGHz[i] - other.SpectralShiftGHz[closest]));
                }
                differences.Sort(); aggregate[name + "_continuous_w0_w" + w + "_median_absolute_shift_difference_ghz"] = AcquisitionDiagnostics.Quantile(differences.ToArray(), .5);
                aggregate[name + "_continuous_w0_w" + w + "_matched_distances"] = differences.Count;
            }
        }

        private static AcquisitionDiagnosticBlock ConvertRate(AcquisitionDiagnosticBlock source, double rate, CancellationToken token)
        {
            return new AcquisitionDiagnosticBlock { SourcePath = source.SourcePath + " [virtual rate conversion]", StartIndex = source.StartIndex + 160,
                SampleIntervalSeconds = 1 / rate, Auxiliary = AcquisitionDiagnostics.Resample(source.Auxiliary, 1 / source.SampleIntervalSeconds, rate, token),
                Measurement = AcquisitionDiagnostics.Resample(source.Measurement, 1 / source.SampleIntervalSeconds, rate, token),
                // Converted positions may be fractional source indices; never mislabel them as raw integer indices.
                SourceIndices = null, Trigger = null };
        }

        private static PreparedSweepMetadata[] ReviewHistory(string path, string name, string folder, Dictionary<string, double> metrics)
        {
            var output = Path.Combine(folder, "historical", name); Directory.CreateDirectory(output);
            foreach (var file in new[] { "ref_preparation.json", "signal_preparation.json", "processing_config.json" }) File.Copy(Path.Combine(path, file), Path.Combine(output, file));
            var a = JsonContractSerializer.Read<PreparedSweepMetadata>(Path.Combine(path, "ref_preparation.json"));
            var b = JsonContractSerializer.Read<PreparedSweepMetadata>(Path.Combine(path, "signal_preparation.json"));
            var result = JsonContractSerializer.Read<IndependentOfdrProcessingResult>(Path.Combine(path, "result.json"));
            var config = JsonContractSerializer.Read<IndependentOfdrProcessingConfig>(Path.Combine(path, "processing_config.json"));
            metrics[name + "_historical_original_selection_s"] = a.Sweep.DurationSeconds;
            metrics[name + "_historical_stitched_selection_s"] = a.RowCount * a.SampleIntervalSeconds;
            metrics[name + "_historical_reference_qc_ratio"] = a.Diagnosis.Phase.NonmonotonicRatio;
            metrics[name + "_historical_signal_qc_ratio"] = b.Diagnosis.Phase.NonmonotonicRatio;
            metrics[name + "_historical_gauge_m"] = config.SegmentSize * result.PhysicalAxis.DistanceMetersPerFftBin;
            metrics[name + "_historical_sweep_span_ghz"] = result.PhysicalAxis.SweepSpanGHz;
            metrics[name + "_historical_path_m"] = config.AuxiliaryPathDifferenceMeters;
            metrics[name + "_historical_full_selection_recomputed"] = 0;
            JsonContractSerializer.Write(Path.Combine(output, "spectral_shift_profile.json"), result.SpectralShiftProfile);
            JsonContractSerializer.Write(Path.Combine(output, "physical_axis.json"), result.PhysicalAxis);
            return new[] { a, b };
        }
        private static void SaveSource(string folder, string name, AcquisitionDiagnosticSource source) => JsonContractSerializer.Write(Path.Combine(folder, "sources", name + ".json"), source);
        private static void WriteMetrics(string folder, string name, AcquisitionSignalMetrics result, Dictionary<string, double> aggregate, string group)
        {
            JsonContractSerializer.Write(Path.Combine(folder, name + ".json"), result);
            aggregate[group + "_" + name + "_qc_ratio"] = result.Phase.NonmonotonicRatio;
            aggregate[group + "_" + name + "_interior_qc_ratio"] = result.Values["interior_nonmonotonic_ratio"];
            aggregate[group + "_" + name + "_phase_average_hz"] = result.Values["phase_average_hz"];
        }
        private static void SavePair(string folder, string name, AcquisitionPairReport report, Dictionary<string, double> metrics, string group)
        {
            JsonContractSerializer.Write(Path.Combine(folder, name + "_pair.json"), report);
            foreach (var metric in report.Metrics) metrics[group + "_" + name + "_" + metric.Key] = metric.Value;
        }
        private static string Hash(string path)
        { using (var input = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant(); }
    }

    [DataContract]
    public sealed class AcquisitionComparisonConfig
    {
        [DataMember] public string DataRoot { get; set; } = @"C:\Project\OFDR\Datas\OFDR Raw data";
        [DataMember] public string HighHistory { get; set; } = @"OFDR_Signal_Generator\bin\Debug\harness\results\20261105_091456_235_prepared_ofdr_analysis_76283c";
        [DataMember] public string LowHistory { get; set; } = @"OFDR_Signal_Generator\bin\Debug\harness\results\20261105_090952_295_prepared_ofdr_analysis_cea163";
        [DataMember] public string GoldenDirectory { get; set; } = @"harness\results\20260828_dual_aux_csharp_series_regression";
        [DataMember] public int WindowSamples { get; set; } = 81920;
        [DataMember] public double MaximumScopeSampleRate { get; set; } = 20e6;
        [DataMember] public double CenterWavelengthNm { get; set; } = 1550;
        [DataMember] public double SweepRateNmPerSecond { get; set; } = 50;
        [DataMember] public double NoticePhysicalPathMeters { get; set; } = 200;
        [DataMember] public double GroupIndex { get; set; } = 1.4682;
        public void Validate()
        {
            if (WindowSamples < 1024 || 3L * WindowSamples > AcquisitionDiagnosticReader.MaximumSamples || MaximumScopeSampleRate != 20e6 || CenterWavelengthNm != 1550 || SweepRateNmPerSecond != 50 || GroupIndex != 1.4682 || NoticePhysicalPathMeters != 200)
                throw new ArgumentException("This reproducible experiment requires <=250000 samples, the confirmed 20 MS/s/1550 nm/50 nm/s conditions and provisional 200 m, n=1.4682 calibration.");
        }
    }
}
