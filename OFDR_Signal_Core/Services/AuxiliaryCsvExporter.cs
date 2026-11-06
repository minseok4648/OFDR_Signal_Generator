using System;
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
    public sealed class AuxiliaryCsvExporter
    {
        public const string Header = "Index,Time_s,Voltage_V,SourceIndex,SourceTime_s,IsJoin";

        public AuxiliaryCsvExportResult Export(ModeHopRecording source, StitchedAuxiliarySignal signal, string destination, bool entireSignal, double threshold,
            AuxiliaryQualityMetrics diagnosis, AuxiliaryInspectionSettings diagnosisSettings, bool overwrite, CancellationToken token, IProgress<OperationProgress> progress = null)
        {
            token.ThrowIfCancellationRequested();
            if (source == null || signal == null || signal.Values == null || signal.OriginalIndices == null || signal.Values.Length != signal.OriginalIndices.Length) throw new ArgumentException("저장할 접합 신호가 없습니다.");
            if (!entireSignal && diagnosis == null) throw new InvalidOperationException("먼저 선택 구간의 위상 분석을 완료하세요.");
            var first = entireSignal ? 0 : diagnosis.SelectedStartIndex;
            var end = entireSignal ? signal.Values.Length : diagnosis.SelectedEndIndexExclusive;
            if (first < 0 || end <= first || end > signal.Values.Length) throw new ArgumentException("저장 범위가 접합 신호를 벗어났습니다.");
            var csv = Path.GetFullPath(destination);
            if (!string.Equals(Path.GetExtension(csv), ".csv", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("출력 파일 확장자는 .csv여야 합니다.");
            if (string.Equals(csv, Path.GetFullPath(source.SourcePath), StringComparison.OrdinalIgnoreCase)) throw new IOException("원본 취득 CSV에는 덮어쓸 수 없습니다. 다른 이름을 선택하세요.");
            var metadataPath = Path.ChangeExtension(csv, ".metadata.json");
            if (!overwrite && (File.Exists(csv) || File.Exists(metadataPath))) throw new IOException("출력 파일이 이미 있습니다.");
            Directory.CreateDirectory(Path.GetDirectoryName(csv));
            var suffix = "." + Guid.NewGuid().ToString("N");
            var partialCsv = csv + suffix + ".partial"; var partialMetadata = metadataPath + suffix + ".partial";
            var backupCsv = csv + suffix + ".backup"; var backupMetadata = metadataPath + suffix + ".backup";
            var publishedCsv = false; var publishedMetadata = false; var cleanupBackups = false;
            var watch = Stopwatch.StartNew();
            try
            {
                var metadata = new AuxiliaryCsvExportMetadata { SourcePath = source.SourcePath, SourceSha256 = source.Sha256, SourceHeader = source.Header, SourceStartSeconds = source.SourceStartSeconds,
                    SampleIntervalSeconds = signal.SampleIntervalSeconds, ThresholdVoltage = threshold, EntireStitchedSignal = entireSignal, StartIndex = first, EndIndexExclusive = end,
                    ExclusionTailSeconds = signal.Exclusion?.ExclusionTailSeconds ?? 0,
                    ExclusionLeadSeconds = signal.Exclusion?.ExclusionLeadSeconds ?? 0,
                    IgnoredSampleRanges = signal.Exclusion?.IgnoredSampleRanges?.Select(r => new ModeHopRange { Number = r.Number,
                        StartIndex = source.FirstSampleIndex + r.StartIndex, EndIndexExclusive = source.FirstSampleIndex + r.EndIndexExclusive,
                        StartSeconds = (source.FirstSampleIndex + r.StartIndex) * source.SampleIntervalSeconds,
                        EndSeconds = (source.FirstSampleIndex + r.EndIndexExclusive) * source.SampleIntervalSeconds }).ToList(),
                    IgnoredMeasurementRanges = signal.Exclusion?.IgnoredMeasurementRanges?.Select(r => new ModeHopRange { Number = r.Number,
                        StartIndex = source.FirstSampleIndex + r.StartIndex, EndIndexExclusive = source.FirstSampleIndex + r.EndIndexExclusive,
                        StartSeconds = (source.FirstSampleIndex + r.StartIndex) * source.SampleIntervalSeconds,
                        EndSeconds = (source.FirstSampleIndex + r.EndIndexExclusive) * source.SampleIntervalSeconds }).ToList(),
                    RowCount = end - first, LastDiagnosis = diagnosis, LastDiagnosisSettings = diagnosisSettings, ExportedUtc = DateTime.UtcNow };
                using (var file = new FileStream(partialCsv, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                using (var sha = SHA256.Create())
                using (var crypto = new CryptoStream(file, sha, CryptoStreamMode.Write))
                using (var writer = new StreamWriter(crypto, new UTF8Encoding(false), 1024 * 1024))
                {
                    writer.WriteLine(Header);
                    for (var i = first; i < end; i++)
                    {
                        if (((i - first) & 0x3fff) == 0) token.ThrowIfCancellationRequested();
                        var original = signal.OriginalIndices[i];
                        var isJoin = i > 0 && original != signal.OriginalIndices[i - 1] + 1;
                        if (isJoin) metadata.JoinRows++;
                        writer.Write(i.ToString(CultureInfo.InvariantCulture)); writer.Write(',');
                        writer.Write((i * signal.SampleIntervalSeconds).ToString("G17", CultureInfo.InvariantCulture)); writer.Write(',');
                        writer.Write(signal.Values[i].ToString("G17", CultureInfo.InvariantCulture)); writer.Write(',');
                        writer.Write(original.ToString(CultureInfo.InvariantCulture)); writer.Write(',');
                        writer.Write((source.SourceStartSeconds + original * source.SampleIntervalSeconds).ToString("G17", CultureInfo.InvariantCulture)); writer.Write(',');
                        writer.WriteLine(isJoin ? "1" : "0");
                        var done = i - first + 1;
                        if ((done & 0xffff) == 0 || i == end - 1) progress?.Report(new OperationProgress { Stage = "접합 CSV 저장", Completed = done, Total = end - first, Percent = 100.0 * done / (end - first) });
                    }
                    writer.Flush(); crypto.FlushFinalBlock();
                    metadata.CsvSha256 = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                }
                JsonContractSerializer.Write(partialMetadata, metadata);
                token.ThrowIfCancellationRequested();
                Publish(partialCsv, csv, backupCsv, overwrite); publishedCsv = true;
                Publish(partialMetadata, metadataPath, backupMetadata, overwrite); publishedMetadata = true;
                cleanupBackups = true;
                return new AuxiliaryCsvExportResult { CsvPath = csv, MetadataPath = metadataPath, Metadata = metadata, ElapsedSeconds = watch.Elapsed.TotalSeconds };
            }
            catch (Exception error)
            {
                try
                {
                    if (publishedMetadata) Restore(metadataPath, backupMetadata);
                    if (publishedCsv) Restore(csv, backupCsv);
                    cleanupBackups = true;
                }
                catch (Exception rollbackError) { throw new AggregateException("저장 복구에 실패했습니다. backup 파일을 보존합니다.", error, rollbackError); }
                throw;
            }
            finally
            {
                DeleteTemporary(partialCsv); DeleteTemporary(partialMetadata);
                if (cleanupBackups) { DeleteTemporary(backupCsv); DeleteTemporary(backupMetadata); }
            }
        }
        internal static void Publish(string partial, string destination, string backup, bool overwrite)
        {
            if (File.Exists(destination) && overwrite) File.Replace(partial, destination, backup);
            else File.Move(partial, destination);
        }
        internal static void Restore(string destination, string backup)
        {
            if (File.Exists(backup)) File.Replace(backup, destination, null);
            else File.Delete(destination);
        }
        internal static void DeleteTemporary(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
