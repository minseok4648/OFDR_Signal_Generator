using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Services
{
    public sealed class OfdrChannelCsvConverter
    {
        public const string CurrentConverterVersion = "ofdr-channel-csv-1.0.0";
        private readonly IsfWaveformReader reader = new IsfWaveformReader();

        public ChannelConversionResult Convert(string isfPath, string outputFolder, bool overwrite, CancellationToken token, IProgress<OperationProgress> progress = null)
        {
            if (string.IsNullOrWhiteSpace(isfPath) || !File.Exists(isfPath)) throw new FileNotFoundException("변환할 ISF 파일을 찾을 수 없습니다.", isfPath);
            if (!string.Equals(Path.GetExtension(isfPath), ".isf", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("채널 CSV 변환 입력은 ISF여야 합니다.");
            var identity = OfdrChannelFileReader.ParseIdentity(isfPath, 0);
            var header = reader.ReadHeader(isfPath);
            Directory.CreateDirectory(outputFolder);
            var csvPath = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(isfPath) + ".csv");
            var metadataPath = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(isfPath) + ".metadata.json");
            var sourceHash = OfdrSetCsvConverter.ComputeSha256(isfPath, token);
            if (File.Exists(csvPath) && File.Exists(metadataPath) && IsCurrent(metadataPath, csvPath, sourceHash, token))
                return new ChannelConversionResult { CsvPath = csvPath, MetadataPath = metadataPath, RowCount = header.PointCount, Skipped = true };
            if ((File.Exists(csvPath) || File.Exists(metadataPath)) && !overwrite) throw new IOException("기존 채널 CSV가 최신 상태가 아닙니다. 덮어쓰기를 선택하세요: " + csvPath);

            var partialCsv = csvPath + ".partial";
            var partialMetadata = metadataPath + ".partial";
            DeleteIfExists(partialCsv);
            DeleteIfExists(partialMetadata);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                string csvHash;
                using (var samples = reader.OpenSampleStream(isfPath))
                using (var file = new FileStream(partialCsv, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                using (var sha = SHA256.Create())
                using (var crypto = new CryptoStream(file, sha, CryptoStreamMode.Write))
                using (var writer = new StreamWriter(crypto, new UTF8Encoding(false), 1024 * 1024))
                {
                    writer.WriteLine("Index,Time_s,Voltage_V");
                    const int blockSize = 32768;
                    var buffer = new double[blockSize];
                    var completed = 0;
                    while (completed < header.PointCount)
                    {
                        token.ThrowIfCancellationRequested();
                        var count = Math.Min(blockSize, header.PointCount - completed);
                        if (samples.ReadSamples(buffer, 0, count) != count) throw new EndOfStreamException("ISF payload가 예상보다 먼저 끝났습니다.");
                        for (var i = 0; i < count; i++)
                        {
                            writer.Write(completed + i);
                            writer.Write(',');
                            writer.Write(header.GetTimeSeconds(completed + i).ToString("F9", CultureInfo.InvariantCulture));
                            writer.Write(',');
                            writer.WriteLine(buffer[i].ToString("F12", CultureInfo.InvariantCulture));
                        }
                        completed += count;
                        if (completed == header.PointCount || completed % (blockSize * 8) == 0)
                            progress?.Report(new OperationProgress { Stage = "채널 CSV 변환", Detail = Path.GetFileName(isfPath), Completed = completed, Total = header.PointCount, Percent = 100.0 * completed / header.PointCount });
                    }
                    writer.Flush();
                    crypto.FlushFinalBlock();
                    csvHash = ToHex(sha.Hash);
                }
                var metadata = new ChannelCsvMetadata
                {
                    MeasurementId = identity.MeasurementId,
                    DisplacementMm = identity.DisplacementMm,
                    Repetition = identity.Repetition,
                    ChannelNumber = identity.ChannelNumber,
                    RowCount = header.PointCount,
                    CsvFileName = Path.GetFileName(csvPath),
                    CsvSha256 = csvHash,
                    ConvertedUtc = DateTime.UtcNow,
                    ConverterVersion = CurrentConverterVersion,
                    Source = new IsfSourceMetadata { Channel = identity.ChannelNumber, Path = Path.GetFullPath(isfPath), FileSize = new FileInfo(isfPath).Length, Sha256 = sourceHash, Header = header }
                };
                JsonContractSerializer.Write(partialMetadata, metadata);
                MoveIntoPlace(partialCsv, csvPath, overwrite);
                MoveIntoPlace(partialMetadata, metadataPath, overwrite);
                stopwatch.Stop();
                return new ChannelConversionResult { CsvPath = csvPath, MetadataPath = metadataPath, RowCount = header.PointCount, Elapsed = stopwatch.Elapsed };
            }
            catch
            {
                DeleteIfExists(partialCsv);
                DeleteIfExists(partialMetadata);
                throw;
            }
        }

        private static bool IsCurrent(string metadataPath, string csvPath, string sourceHash, CancellationToken token)
        {
            try
            {
                var metadata = JsonContractSerializer.Read<ChannelCsvMetadata>(metadataPath);
                return metadata != null && metadata.Source != null
                    && string.Equals(metadata.ConverterVersion, CurrentConverterVersion, StringComparison.Ordinal)
                    && string.Equals(metadata.Source.Sha256, sourceHash, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(metadata.CsvSha256, OfdrSetCsvConverter.ComputeSha256(csvPath, token), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void MoveIntoPlace(string source, string destination, bool overwrite)
        {
            if (File.Exists(destination))
            {
                if (!overwrite) throw new IOException("출력 파일이 이미 있습니다: " + destination);
                File.Replace(source, destination, null);
            }
            else File.Move(source, destination);
        }

        private static string ToHex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant(); }
        private static void DeleteIfExists(string path) { if (File.Exists(path)) File.Delete(path); }
    }
}
