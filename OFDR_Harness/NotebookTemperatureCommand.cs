using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;

namespace OFDR_Harness
{
    internal static class NotebookTemperatureCommand
    {
        internal static int Run(IDictionary<string, string> options)
        {
            string value;
            var input = options.TryGetValue("input", out value) ? Path.GetFullPath(value) : @"D:\project\OFDR\signal processing\261106_RAW";
            var config = options.TryGetValue("config", out value) ? JsonContractSerializer.Read<NotebookTemperatureConfig>(value) : new NotebookTemperatureConfig();
            var root = options.TryGetValue("results", out value) ? value : Path.Combine("harness", "results");
            var id = options.TryGetValue("experiment", out value) ? value : DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_261106_notebook_temperature";
            if (id != Path.GetFileName(id) || id == "." || id == "..") throw new ArgumentException("Experiment must be a single directory name.");
            var output = Path.GetFullPath(Path.Combine(root, id));
            if (Directory.Exists(output)) throw new IOException("Experiment already exists: " + output);
            Directory.CreateDirectory(output);
            JsonContractSerializer.Write(Path.Combine(output, "config.json"), config);
            var metrics = new NotebookTemperatureRunMetrics { Status = "RUNNING", InputDirectory = input };
            var clock = Stopwatch.StartNew();
            try
            {
                config.Validate();
                var engine = new NotebookTemperatureReproductionEngine();
                engine.RequireNativeProvider();
                metrics.Provider = engine.ProviderDescription;
                var reference = ReadAndPrepare(config.ReferenceFile, input, output, "reference", engine, config, metrics);
                for (var i = 0; i < config.MeasurementFiles.Length; i++)
                {
                    var key = "measurement_" + i;
                    var signal = ReadAndPrepare(config.MeasurementFiles[i], input, output, key, engine, config, metrics);
                    Console.WriteLine("Correlating {0}...", config.Labels[i]);
                    var profile = engine.Compare(reference.Spectrum, signal.Spectrum, config, CancellationToken.None,
                        (done, total) => Console.WriteLine("  {0}/{1}", done, total));
                    using (var writer = new StreamWriter(Path.Combine(output, key + "_profile.csv"), false, new UTF8Encoding(false)))
                    {
                        writer.WriteLine("profile_index,fft_bin,shift_samples");
                        for (var j = 0; j < profile.Positions.Length; j++) writer.WriteLine(j + "," + profile.Positions[j] + "," + profile.Shifts[j].ToString("R", CultureInfo.InvariantCulture));
                    }
                    var finite = profile.Shifts.Where(ModeHopSettings.IsFinite).ToArray();
                    metrics.Profiles.Add(new NotebookProfileMetrics { Label = config.Labels[i], File = key + "_profile.csv",
                        Points = profile.Shifts.Length, ValidPoints = finite.Length, MinimumLag = finite.Min(), MaximumLag = finite.Max() });
                }
                metrics.Status = "COMPLETED";
            }
            catch (Exception ex)
            {
                metrics.Status = "FAILED"; metrics.Error = ex.ToString();
                Console.Error.WriteLine(ex);
            }
            finally
            {
                metrics.ElapsedSeconds = clock.Elapsed.TotalSeconds;
                JsonContractSerializer.Write(Path.Combine(output, "metrics.json"), metrics);
                JsonContractSerializer.Write(Path.Combine(output, "dataset_manifest.json"), metrics.Sources);
                File.WriteAllText(Path.Combine(output, "summary.md"), "# " + metrics.Status + "\n\n" +
                    "30-CSV-TO_OFDR.ipynb cells 28, 35-37 reproduction. CH1=start, CH2=mode-hop, CH3=auxiliary, CH4=measurement.\n\n" +
                    "Axes: profile index / local-spectrum lag samples. Independent endpoint phase spans, global Hann, no mode-hop mask, no common-span alignment. " +
                    "Historical diagnostic only; not calibrated distance, GHz, strain or temperature. Production common-span processing is unchanged.\n\n" +
                    "Status describes execution; independent Python comparison is recorded separately by Validate-NotebookTemperature.py.\n\n" +
                    (metrics.Error ?? "All complete input records were read; each configured crop and all profile positions were processed.") + "\n", Encoding.UTF8);
            }
            Console.WriteLine(output);
            return metrics.Status == "COMPLETED" ? 0 : 1;
        }

        private static NotebookTemperatureSweep ReadAndPrepare(string file, string input, string output, string key,
            NotebookTemperatureReproductionEngine engine, NotebookTemperatureConfig config, NotebookTemperatureRunMetrics metrics)
        {
            var path = Path.GetFullPath(Path.Combine(input, file));
            Console.WriteLine("Reading complete record: " + path);
            var recording = new TekScopeCsvReader().Read(path, CancellationToken.None);
            var source = new NotebookSourceMetadata { Key = key, Path = path, Sha256 = recording.Sha256, Header = recording.Header,
                Samples = recording.SampleCount, SampleIntervalSeconds = recording.SampleIntervalSeconds };
            metrics.Sources.Add(source);
            Console.WriteLine("Preparing CH3/CH4: " + file);
            var sweep = engine.Prepare(recording, config, CancellationToken.None);
            source.Processing = sweep.Metrics;
            using (var writer = new BinaryWriter(File.Create(Path.Combine(output, key + "_klinear.bin"))))
                foreach (var item in sweep.Values) writer.Write(item);
            return sweep;
        }
    }

    [DataContract]
    internal sealed class NotebookTemperatureRunMetrics
    {
        [DataMember] public string Status { get; set; }
        [DataMember] public string InputDirectory { get; set; }
        [DataMember] public string Provider { get; set; }
        [DataMember] public string Error { get; set; }
        [DataMember] public double ElapsedSeconds { get; set; }
        [DataMember] public List<NotebookSourceMetadata> Sources { get; set; } = new List<NotebookSourceMetadata>();
        [DataMember] public List<NotebookProfileMetrics> Profiles { get; set; } = new List<NotebookProfileMetrics>();
    }

    [DataContract]
    internal sealed class NotebookSourceMetadata
    {
        [DataMember] public string Key { get; set; }
        [DataMember] public string Path { get; set; }
        [DataMember] public string Sha256 { get; set; }
        [DataMember] public Dictionary<string, string> Header { get; set; }
        [DataMember] public int Samples { get; set; }
        [DataMember] public double SampleIntervalSeconds { get; set; }
        [DataMember] public NotebookSweepMetrics Processing { get; set; }
    }

    [DataContract]
    internal sealed class NotebookProfileMetrics
    {
        [DataMember] public string Label { get; set; }
        [DataMember] public string File { get; set; }
        [DataMember] public int Points { get; set; }
        [DataMember] public int ValidPoints { get; set; }
        [DataMember] public double MinimumLag { get; set; }
        [DataMember] public double MaximumLag { get; set; }
    }
}
