using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;

namespace OFDR_Harness
{
    internal static class Program
    {
        private const string DefaultInput = @"C:\Project\OFDR\Datas\OFDR Raw data";

        private static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "--help" || args[0] == "-h") return PrintUsage();
            try
            {
                var command = args[0].ToLowerInvariant();
                var options = ParseOptions(args.Skip(1).ToArray());
                switch (command)
                {
                    case "scan": return Scan(options);
                    case "convert": return Convert(options);
                    case "convert-channel": return ConvertChannel(options);
                    case "run": return Run(options, false);
                    case "run-csv": return RunCsv(options);
                    case "run-pair": return RunPair(options);
                    case "evaluate": return Run(options, true);
                    default: Console.Error.WriteLine("Unknown command: " + command); return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        private static int Scan(IDictionary<string, string> options)
        {
            var input = Get(options, "input", DefaultInput);
            var output = Get(options, "manifest", Path.Combine("harness", "datasets", "manifests", "scan_latest.json"));
            var manifest = new OfdrDatasetScanner().Scan(input);
            JsonContractSerializer.Write(output, manifest);
            Console.WriteLine("Scanned {0} sets ({1} valid) -> {2}", manifest.Sets.Count, manifest.Sets.Count(value => value.IsValid), Path.GetFullPath(output));
            return manifest.Sets.All(value => value.IsValid) ? 0 : 1;
        }

        private static int Convert(IDictionary<string, string> options)
        {
            var input = Get(options, "input", DefaultInput);
            var output = Get(options, "output", Path.Combine(input, "converted_csv"));
            var overwrite = options.ContainsKey("overwrite");
            var requestedSet = Get(options, "set", null);
            var manifest = new OfdrDatasetScanner().Scan(input);
            var sets = manifest.Sets.Where(value => value.IsValid && (requestedSet == null || string.Equals(value.SetId, requestedSet, StringComparison.OrdinalIgnoreCase))).ToList();
            if (sets.Count == 0) throw new InvalidOperationException("No matching valid channel sets were found.");
            var converter = new OfdrSetCsvConverter();
            foreach (var set in sets)
            {
                var result = converter.Convert(set, output, overwrite, CancellationToken.None, ConsoleProgress());
                Console.WriteLine("{0}: {1} ({2:N0} rows, {3:F2}s)", set.SetId, result.Skipped ? "up-to-date" : "converted", result.RowCount, result.Elapsed.TotalSeconds);
            }
            return 0;
        }

        private static int ConvertChannel(IDictionary<string, string> options)
        {
            var input = Get(options, "input", null);
            if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("convert-channel requires --input <isf path>.");
            var output = Get(options, "output", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input)) ?? string.Empty, "converted_channels"));
            var overwrite = options.ContainsKey("overwrite");
            var result = new OfdrChannelCsvConverter().Convert(input, output, overwrite, CancellationToken.None, ConsoleProgress());
            Console.WriteLine("{0}: {1} ({2:N0} rows, {3:F2}s)", Path.GetFileNameWithoutExtension(input), result.Skipped ? "up-to-date" : "converted", result.RowCount, result.Elapsed.TotalSeconds);
            return 0;
        }

        private static int Run(IDictionary<string, string> options, bool evaluateAll)
        {
            var input = Get(options, "input", DefaultInput);
            var requestedSet = Get(options, "set", null);
            var resultsRoot = Get(options, "results", Path.Combine("harness", "results"));
            var configPath = Get(options, "config", null);
            var config = configPath == null ? new OfdrProcessingConfig() : JsonContractSerializer.Read<OfdrProcessingConfig>(configPath);
            var sets = new OfdrDatasetScanner().Scan(input).Sets.Where(value => value.IsValid).ToList();
            if (!evaluateAll)
            {
                if (string.IsNullOrWhiteSpace(requestedSet)) throw new ArgumentException("run requires --set <set_id>.");
                sets = sets.Where(value => string.Equals(value.SetId, requestedSet, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (sets.Count == 0) throw new InvalidOperationException("No matching valid channel sets were found.");

            var exitCode = 0;
            foreach (var set in sets)
            {
                var experimentId = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + set.SetId;
                var resultFolder = Path.Combine(resultsRoot, experimentId);
                Directory.CreateDirectory(resultFolder);
                JsonContractSerializer.Write(Path.Combine(resultFolder, "config.json"), config);
                try
                {
                    var engine = new OfdrProcessingEngine();
                    using (var result = engine.RunSet(set, config, CancellationToken.None, ConsoleProgress()))
                    {
                        JsonContractSerializer.Write(Path.Combine(resultFolder, "metrics.json"), result);
                        WriteSummary(Path.Combine(resultFolder, "summary.md"), set, "COMPLETED", "NEEDS_MORE_TEST", null, result);
                    }
                    Console.WriteLine("Completed {0} -> {1}", set.SetId, resultFolder);
                }
                catch (Exception ex)
                {
                    exitCode = 1;
                    JsonContractSerializer.Write(Path.Combine(resultFolder, "metrics.json"), new ExperimentFailureMetrics
                    {
                        Status = "FAILED",
                        SetId = set.SetId,
                        ErrorType = ex.GetType().FullName,
                        ErrorMessage = ex.Message,
                        RecordedUtc = DateTime.UtcNow
                    });
                    WriteSummary(Path.Combine(resultFolder, "summary.md"), set, "FAILED", null, ex, null);
                    Console.Error.WriteLine("Failed {0}: {1}", set.SetId, ex.Message);
                }
            }
            return exitCode;
        }

        private static int RunCsv(IDictionary<string, string> options)
        {
            var csvPath = Get(options, "csv", null);
            if (string.IsNullOrWhiteSpace(csvPath)) throw new ArgumentException("run-csv requires --csv <path>.");
            csvPath = Path.GetFullPath(csvPath);
            if (!File.Exists(csvPath)) throw new FileNotFoundException("Converted CSV file was not found.", csvPath);

            var resultsRoot = Get(options, "results", Path.Combine("harness", "results"));
            var configPath = Get(options, "config", null);
            var config = configPath == null ? new OfdrProcessingConfig() : JsonContractSerializer.Read<OfdrProcessingConfig>(configPath);
            var setId = Path.GetFileNameWithoutExtension(csvPath);
            var experimentId = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_csv_" + setId;
            var resultFolder = Path.Combine(resultsRoot, experimentId);
            Directory.CreateDirectory(resultFolder);
            JsonContractSerializer.Write(Path.Combine(resultFolder, "config.json"), config);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var data = new ConvertedOfdrCsvReader().Read(csvPath, CancellationToken.None, ConsoleProgress());
                var csvReadSeconds = stopwatch.Elapsed.TotalSeconds;
                var engine = new OfdrProcessingEngine();
                using (var result = engine.RunConvertedCsv(data, config, CancellationToken.None, ConsoleProgress()))
                {
                    stopwatch.Stop();
                    JsonContractSerializer.Write(Path.Combine(resultFolder, "metrics.json"), result);
                    WriteCsvSummary(Path.Combine(resultFolder, "summary.md"), csvPath, "COMPLETED", null, result, csvReadSeconds, stopwatch.Elapsed.TotalSeconds);
                }
                Console.WriteLine("Completed CSV {0} -> {1}", setId, resultFolder);
                return 0;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                JsonContractSerializer.Write(Path.Combine(resultFolder, "metrics.json"), new ExperimentFailureMetrics
                {
                    Status = "FAILED",
                    SetId = setId,
                    ErrorType = ex.GetType().FullName,
                    ErrorMessage = ex.Message,
                    RecordedUtc = DateTime.UtcNow
                });
                WriteCsvSummary(Path.Combine(resultFolder, "summary.md"), csvPath, "FAILED", ex, null, 0.0, stopwatch.Elapsed.TotalSeconds);
                Console.Error.WriteLine("Failed CSV {0}: {1}", setId, ex.Message);
                return 1;
            }
        }

        private static void WriteCsvSummary(string path, string csvPath, string status, Exception error, OfdrSetProcessingResult result, double csvReadSeconds, double wallSeconds)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# CSV " + Path.GetFileNameWithoutExtension(csvPath));
            builder.AppendLine();
            builder.AppendLine("- Status: `" + status + "`");
            builder.AppendLine("- Input type: converted integrated CSV");
            builder.AppendLine("- Input path: `" + csvPath + "`");
            builder.AppendLine("- Input bytes: " + new FileInfo(csvPath).Length.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("- Recorded: " + DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
            builder.AppendLine("- Wall time: " + wallSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " s");
            if (result != null)
            {
                builder.AppendLine("- CSV read time: " + csvReadSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " s");
                builder.AppendLine("- FFT provider: " + result.FftProvider.Replace(Environment.NewLine, " "));
                builder.AppendLine("- Rows: " + result.XPolarization.SampleCount.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("- CH1 nonmonotonic ratio: " + result.KLinearization.NonmonotonicRatio.ToString("G9", CultureInfo.InvariantCulture));
                builder.AppendLine("- Xpol segments: " + result.XPolarization.SegmentCount.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("- Ypol segments: " + result.YPolarization.SegmentCount.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine();
                builder.AppendLine("## Interpretation");
                builder.AppendLine();
                builder.AppendLine("The converted CSV completed through the same k-linearization, FFT, segmentation, IFFT, metric, and peak-detection path used by the GUI.");
            }
            if (error != null)
            {
                builder.AppendLine();
                builder.AppendLine("## Failure");
                builder.AppendLine();
                builder.AppendLine("```");
                builder.AppendLine(error.ToString());
                builder.AppendLine("```");
            }
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }

        private static int RunPair(IDictionary<string, string> options)
        {
            var auxiliaryPath = Get(options, "aux", null);
            var signalAuxiliaryPath = Get(options, "sig-aux", null);
            var referencePath = Get(options, "ref", null);
            var signalPath = Get(options, "sig", null);
            if (string.IsNullOrWhiteSpace(auxiliaryPath) || string.IsNullOrWhiteSpace(signalAuxiliaryPath)
                || string.IsNullOrWhiteSpace(referencePath) || string.IsNullOrWhiteSpace(signalPath))
                throw new ArgumentException("run-pair requires --aux, --sig-aux, --ref, and --sig paths.");
            var channelText = Get(options, "channel", "2");
            int polarizationChannel;
            if (!int.TryParse(channelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out polarizationChannel) || (polarizationChannel != 2 && polarizationChannel != 3))
                throw new ArgumentException("--channel must be 2 or 3.");
            var configPath = Get(options, "config", null);
            var config = configPath == null ? new IndependentOfdrProcessingConfig() : JsonContractSerializer.Read<IndependentOfdrProcessingConfig>(configPath);
            var resultsRoot = Get(options, "results", Path.Combine("harness", "results"));
            var signalIdentity = OfdrChannelFileReader.ParseIdentity(signalPath, polarizationChannel);
            var experimentId = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_pair_" + signalIdentity.MeasurementId;
            var resultFolder = Path.Combine(resultsRoot, experimentId);
            Directory.CreateDirectory(resultFolder);
            JsonContractSerializer.Write(Path.Combine(resultFolder, "config.json"), config);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var reader = new OfdrChannelFileReader();
                var input = new IndependentOfdrInput
                {
                    Auxiliary = reader.Read(auxiliaryPath, 1, CancellationToken.None, ConsoleProgress()),
                    SignalAuxiliary = reader.Read(signalAuxiliaryPath, 1, CancellationToken.None, ConsoleProgress()),
                    Reference = reader.Read(referencePath, polarizationChannel, CancellationToken.None, ConsoleProgress()),
                    Signal = reader.Read(signalPath, polarizationChannel, CancellationToken.None, ConsoleProgress())
                };
                var readSeconds = stopwatch.Elapsed.TotalSeconds;
                using (var result = new IndependentOfdrProcessingEngine().Run(input, config, CancellationToken.None, ConsoleProgress()))
                {
                    stopwatch.Stop();
                    JsonContractSerializer.Write(Path.Combine(resultFolder, "metrics.json"), result);
                    WritePairSummary(Path.Combine(resultFolder, "summary.md"), input, result, "COMPLETED", null, readSeconds, stopwatch.Elapsed.TotalSeconds);
                }
                Console.WriteLine("Completed pair {0} -> {1}", signalIdentity.MeasurementId, resultFolder);
                return 0;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                JsonContractSerializer.Write(Path.Combine(resultFolder, "metrics.json"), new ExperimentFailureMetrics
                {
                    Status = "FAILED", SetId = signalIdentity.MeasurementId, ErrorType = ex.GetType().FullName, ErrorMessage = ex.Message, RecordedUtc = DateTime.UtcNow
                });
                WritePairSummary(Path.Combine(resultFolder, "summary.md"), null, null, "FAILED", ex, 0.0, stopwatch.Elapsed.TotalSeconds);
                Console.Error.WriteLine("Failed pair {0}: {1}", signalIdentity.MeasurementId, ex.Message);
                return 1;
            }
        }

        private static void WritePairSummary(string path, IndependentOfdrInput input, IndependentOfdrProcessingResult result, string status, Exception error, double readSeconds, double wallSeconds)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# Dual-sweep CH1 Ref/Sig processing");
            builder.AppendLine();
            builder.AppendLine("- Status: `" + status + "`");
            builder.AppendLine("- Recorded: " + DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
            builder.AppendLine("- Wall time: " + wallSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " s");
            if (result != null && input != null)
            {
                builder.AppendLine("- Aux: `" + input.Auxiliary.SourcePath + "`");
                builder.AppendLine("- Signal Aux: `" + input.SignalAuxiliary.SourcePath + "`");
                builder.AppendLine("- Ref: `" + input.Reference.SourcePath + "`");
                builder.AppendLine("- Sig: `" + input.Signal.SourcePath + "`");
                builder.AppendLine("- Polarization: " + result.Polarization);
                builder.AppendLine("- Samples: " + result.SampleCount.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("- Pre-K FFT preview points (Ref / Sig): " + result.PreKReferenceSpectrum.Values.Length.ToString(CultureInfo.InvariantCulture) + " / " + result.PreKSignalSpectrum.Values.Length.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("- K-linearized distance FFT preview points (Ref / Sig): " + result.ReferenceSpectrum.Values.Length.ToString(CultureInfo.InvariantCulture) + " / " + result.SignalSpectrum.Values.Length.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("- Segment count: " + result.SegmentStarts.Length.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("- Read time: " + readSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " s");
                builder.AppendLine("- CH1 nonmonotonic ratio: " + result.KLinearization.NonmonotonicRatio.ToString("G9", CultureInfo.InvariantCulture));
                builder.AppendLine("- Signal CH1 nonmonotonic ratio: " + result.SignalKLinearization.NonmonotonicRatio.ToString("G9", CultureInfo.InvariantCulture));
                builder.AppendLine("- Selected peak lag: " + result.SelectedSegment.PeakLagBins.ToString("G9", CultureInfo.InvariantCulture) + " bin");
                builder.AppendLine("- Selected spectral shift: " + result.SelectedSegment.PeakSpectralShiftGHz.ToString("G9", CultureInfo.InvariantCulture) + " GHz");
                builder.AppendLine("- Selected segment center: " + result.SelectedSegment.SegmentCenterDistanceMeters.ToString("G9", CultureInfo.InvariantCulture) + " m");
                builder.AppendLine("- Selected peak strain: " + result.SelectedSegment.PeakStrainMicrostrain.ToString("G9", CultureInfo.InvariantCulture) + " microstrain");
                builder.AppendLine("- Expected applied strain from stage displacement: " + result.PhysicalAxis.ExpectedAppliedStrainMicrostrain.ToString("G9", CultureInfo.InvariantCulture) + " microstrain");
                builder.AppendLine("- Distance scale: " + result.PhysicalAxis.DistanceMetersPerFftBin.ToString("G9", CultureInfo.InvariantCulture) + " m/FFT bin");
                builder.AppendLine("- Strain scale: " + result.PhysicalAxis.StrainMicrostrainPerLag.ToString("G9", CultureInfo.InvariantCulture) + " microstrain/lag bin");
                builder.AppendLine("- Spectral shift scale: " + result.PhysicalAxis.SpectralShiftGHzPerLag.ToString("G9", CultureInfo.InvariantCulture) + " GHz/lag bin");
                builder.AppendLine("- Calibrated sweep span: " + result.PhysicalAxis.SweepSpanGHz.ToString("G9", CultureInfo.InvariantCulture) + " GHz");
                builder.AppendLine("- FFT provider: " + result.FftProvider.Replace(Environment.NewLine, " "));
                builder.AppendLine();
                builder.AppendLine("## Interpretation");
                builder.AppendLine();
                builder.AppendLine("Reference CH1/Ref and signal CH1/Sig were independently phase-linearized, cropped to a common relative-phase span, and completed through normalized-dB distance FFT, spatial segmentation, segment windowing, local-spectrum IFFT, restricted Ref/Sig magnitude cross correlation, sub-bin peak interpolation, spectral-shift profile, and strain profile generation.");
                builder.AppendLine("Distance and strain values use the recorded calibration factors. Center wavelength, fiber strain coefficient, and distance geometry factor must be confirmed before treating the values as an absolute calibration.");
            }
            if (error != null)
            {
                builder.AppendLine();
                builder.AppendLine("## Failure");
                builder.AppendLine();
                builder.AppendLine("```");
                builder.AppendLine(error.ToString());
                builder.AppendLine("```");
            }
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }

        private static void WriteSummary(string path, OfdrChannelSet set, string status, string decision, Exception error, OfdrSetProcessingResult result)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# " + set.SetId);
            builder.AppendLine();
            builder.AppendLine("- Status: `" + status + "`");
            if (decision != null) builder.AppendLine("- Decision: `" + decision + "`");
            builder.AppendLine("- Displacement: " + set.DisplacementMm.ToString("0.0", CultureInfo.InvariantCulture) + " mm");
            builder.AppendLine("- Repetition: " + set.Repetition);
            builder.AppendLine("- Recorded: " + DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
            if (result != null)
            {
                builder.AppendLine("- FFT provider: " + result.FftProvider.Replace(Environment.NewLine, " "));
                builder.AppendLine("- CH1 phase span: " + result.KLinearization.PhaseSpanRadians.ToString("G9", CultureInfo.InvariantCulture) + " rad");
                builder.AppendLine("- CH1 nonmonotonic ratio: " + result.KLinearization.NonmonotonicRatio.ToString("G9", CultureInfo.InvariantCulture));
                builder.AppendLine("- Xpol segments: " + result.XPolarization.SegmentCount);
                builder.AppendLine("- Ypol segments: " + result.YPolarization.SegmentCount);
                builder.AppendLine();
                builder.AppendLine("## Interpretation");
                builder.AppendLine();
                builder.AppendLine("Baseline completed. Physical distance calibration is not applied; inspect normalized-k/FFT/lag-bin repeatability before accepting an algorithm change.");
            }
            if (error != null)
            {
                builder.AppendLine();
                builder.AppendLine("## Failure");
                builder.AppendLine();
                builder.AppendLine("```");
                builder.AppendLine(error.ToString());
                builder.AppendLine("```");
            }
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }

        private static IProgress<OperationProgress> ConsoleProgress()
        {
            return new ConsoleOperationProgress();
        }

        private sealed class ConsoleOperationProgress : IProgress<OperationProgress>
        {
            private readonly object sync = new object();

            public void Report(OperationProgress value)
            {
                if (value == null) return;
                lock (sync)
                {
                    Console.Write("\r{0,-22} {1,6:F1}% {2,-28}", value.Stage, value.Percent, value.Detail ?? string.Empty);
                    if (value.Percent >= 100.0) Console.WriteLine();
                }
            }
        }

        [DataContract]
        private sealed class ExperimentFailureMetrics
        {
            [DataMember(Order = 1)] public int SchemaVersion { get; set; } = 1;
            [DataMember(Order = 2)] public string Status { get; set; }
            [DataMember(Order = 3)] public string SetId { get; set; }
            [DataMember(Order = 4)] public string ErrorType { get; set; }
            [DataMember(Order = 5)] public string ErrorMessage { get; set; }
            [DataMember(Order = 6)] public DateTime RecordedUtc { get; set; }
        }

        private static Dictionary<string, string> ParseOptions(string[] args)
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Expected option, got " + args[i]);
                var key = args[i].Substring(2);
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) options[key] = args[++i];
                else options[key] = "true";
            }
            return options;
        }

        private static string Get(IDictionary<string, string> values, string key, string defaultValue)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : defaultValue;
        }

        private static int PrintUsage()
        {
            Console.WriteLine("OFDR_Harness scan [--input folder] [--manifest path]");
            Console.WriteLine("OFDR_Harness convert [--input folder] [--output folder] [--set id] [--overwrite]");
            Console.WriteLine("OFDR_Harness convert-channel --input isf [--output folder] [--overwrite]");
            Console.WriteLine("OFDR_Harness run --set id [--input folder] [--config path] [--results folder]");
            Console.WriteLine("OFDR_Harness run-csv --csv path [--config path] [--results folder]");
            Console.WriteLine("OFDR_Harness run-pair --aux reference_ch1 --sig-aux signal_ch1 --ref path --sig path [--channel 2|3] [--config path] [--results folder]");
            Console.WriteLine("OFDR_Harness evaluate [--input folder] [--config path] [--results folder]");
            return 0;
        }
    }
}
