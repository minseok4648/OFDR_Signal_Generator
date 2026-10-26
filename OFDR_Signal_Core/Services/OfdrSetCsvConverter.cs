using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Services
{
    public sealed class OfdrSetCsvConverter
    {
        private readonly IsfWaveformReader reader;
        private static readonly string CurrentConverterVersion = typeof(OfdrSetCsvConverter).Assembly.GetName().Version.ToString();

        public OfdrSetCsvConverter(IsfWaveformReader reader = null)
        {
            this.reader = reader ?? new IsfWaveformReader();
        }

        public ConversionResult Convert(OfdrChannelSet set, string outputFolder, bool overwrite, CancellationToken cancellationToken, IProgress<OperationProgress> progress = null)
        {
            if (set == null) throw new ArgumentNullException(nameof(set));
            if (!set.IsValid) throw new InvalidOperationException("Cannot convert invalid set " + set.SetId + ": " + set.ValidationMessage);
            if (string.IsNullOrWhiteSpace(outputFolder)) throw new ArgumentException("Output folder is required.", nameof(outputFolder));
            Directory.CreateDirectory(outputFolder);

            var stopwatch = Stopwatch.StartNew();
            var csvPath = Path.Combine(outputFolder, set.SetId + ".csv");
            var metadataPath = Path.Combine(outputFolder, set.SetId + ".metadata.json");
            var sourcePaths = new[] { set.Channel1Path, set.Channel2Path, set.Channel3Path };
            var sources = new List<IsfSourceMetadata>();
            for (var index = 0; index < sourcePaths.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new OperationProgress { Stage = "Hash sources", Detail = Path.GetFileName(sourcePaths[index]), Percent = (index * 10.0) / sourcePaths.Length });
                sources.Add(new IsfSourceMetadata
                {
                    Channel = index + 1,
                    Path = Path.GetFullPath(sourcePaths[index]),
                    FileSize = new FileInfo(sourcePaths[index]).Length,
                    Sha256 = ComputeSha256(sourcePaths[index], cancellationToken),
                    Header = reader.ReadHeader(sourcePaths[index])
                });
            }

            if (File.Exists(csvPath) && File.Exists(metadataPath) && IsCurrent(metadataPath, csvPath, sources, cancellationToken))
            {
                return new ConversionResult { SetId = set.SetId, CsvPath = csvPath, MetadataPath = metadataPath, RowCount = set.PointCount, OutputBytes = new FileInfo(csvPath).Length, Elapsed = stopwatch.Elapsed, Skipped = true };
            }
            if ((File.Exists(csvPath) || File.Exists(metadataPath)) && !overwrite)
                throw new IOException("Stale output exists for " + set.SetId + ". Enable overwrite to replace it.");

            EnsureFreeSpace(outputFolder, set.PointCount);
            var partialCsv = csvPath + ".partial";
            var partialMetadata = metadataPath + ".partial";
            DeleteIfExists(partialCsv);
            DeleteIfExists(partialMetadata);
            string csvHash;
            try
            {
                csvHash = WriteCsv(set, partialCsv, cancellationToken, progress);
                var metadata = new CsvSetMetadata
                {
                    SetId = set.SetId,
                    DisplacementMm = set.DisplacementMm,
                    Repetition = set.Repetition,
                    RowCount = set.PointCount,
                    CsvFileName = Path.GetFileName(csvPath),
                    CsvSha256 = csvHash,
                    ConvertedUtc = DateTime.UtcNow,
                    ConverterVersion = CurrentConverterVersion,
                    Sources = sources
                };
                JsonContractSerializer.Write(partialMetadata, metadata);
                MoveIntoPlace(partialCsv, csvPath, overwrite);
                MoveIntoPlace(partialMetadata, metadataPath, overwrite);
                stopwatch.Stop();
                return new ConversionResult { SetId = set.SetId, CsvPath = csvPath, MetadataPath = metadataPath, RowCount = set.PointCount, OutputBytes = new FileInfo(csvPath).Length, Elapsed = stopwatch.Elapsed };
            }
            catch
            {
                DeleteIfExists(partialCsv);
                DeleteIfExists(partialMetadata);
                throw;
            }
        }

        private string WriteCsv(OfdrChannelSet set, string path, CancellationToken cancellationToken, IProgress<OperationProgress> progress)
        {
            using (var ch1 = reader.OpenSampleStream(set.Channel1Path))
            using (var ch2 = reader.OpenSampleStream(set.Channel2Path))
            using (var ch3 = reader.OpenSampleStream(set.Channel3Path))
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
            using (var sha = SHA256.Create())
            using (var crypto = new CryptoStream(file, sha, CryptoStreamMode.Write))
            using (var writer = new StreamWriter(crypto, new UTF8Encoding(false), 1024 * 1024))
            {
                writer.WriteLine("Index,Time_s,CH1_Ref_V,CH2_Xpol_V,CH3_Ypol_V");
                const int blockSize = 32768;
                var a = new double[blockSize];
                var b = new double[blockSize];
                var c = new double[blockSize];
                var completed = 0;
                while (completed < set.PointCount)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(blockSize, set.PointCount - completed);
                    if (ch1.ReadSamples(a, 0, count) != count || ch2.ReadSamples(b, 0, count) != count || ch3.ReadSamples(c, 0, count) != count)
                        throw new EndOfStreamException("A channel ended before the expected point count.");
                    for (var i = 0; i < count; i++)
                    {
                        writer.Write(completed + i);
                        writer.Write(',');
                        writer.Write(ch1.Header.GetTimeSeconds(completed + i).ToString("F9", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(a[i].ToString("F12", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(b[i].ToString("F12", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.WriteLine(c[i].ToString("F12", CultureInfo.InvariantCulture));
                    }
                    completed += count;
                    if (completed == set.PointCount || completed % (blockSize * 8) == 0)
                        progress?.Report(new OperationProgress { Stage = "Convert CSV", Detail = set.SetId, Completed = completed, Total = set.PointCount, Percent = 10.0 + (90.0 * completed / set.PointCount) });
                }
                writer.Flush();
                crypto.FlushFinalBlock();
                return ToHex(sha.Hash);
            }
        }

        private static bool IsCurrent(string metadataPath, string csvPath, IList<IsfSourceMetadata> current, CancellationToken cancellationToken)
        {
            try
            {
                var previous = JsonContractSerializer.Read<CsvSetMetadata>(metadataPath);
                if (previous == null || previous.SchemaVersion != 1 || previous.Sources == null || previous.Sources.Count != current.Count) return false;
                if (!string.Equals(previous.ConverterVersion, CurrentConverterVersion, StringComparison.Ordinal)) return false;
                if (!current.All(source => previous.Sources.Any(old => old.Channel == source.Channel && old.FileSize == source.FileSize && string.Equals(old.Sha256, source.Sha256, StringComparison.OrdinalIgnoreCase)))) return false;
                return !string.IsNullOrWhiteSpace(previous.CsvSha256)
                    && string.Equals(previous.CsvSha256, ComputeSha256(csvPath, cancellationToken), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static string ComputeSha256(string path, CancellationToken cancellationToken)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, read, null, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return ToHex(sha.Hash);
            }
        }

        private static string ToHex(byte[] bytes)
        {
            return string.Concat(bytes.Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static void EnsureFreeSpace(string outputFolder, int pointCount)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(outputFolder));
                var drive = new DriveInfo(root);
                var estimated = checked((long)pointCount * 86L);
                if (drive.IsReady && drive.AvailableFreeSpace < estimated + (estimated / 10))
                    throw new IOException(string.Format(CultureInfo.InvariantCulture, "Insufficient free space. Estimated CSV size is {0:N0} bytes.", estimated));
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }

        private static void MoveIntoPlace(string source, string destination, bool overwrite)
        {
            if (File.Exists(destination))
            {
                if (!overwrite) throw new IOException("Output already exists: " + destination);
                File.Replace(source, destination, null);
            }
            else File.Move(source, destination);
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
