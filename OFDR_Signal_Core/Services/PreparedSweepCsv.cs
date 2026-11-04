using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Services
{
    public sealed class PreparedSweepCsv
    {
        public const string Header = "Index,Time_s,Auxiliary_V,Signal_V,SourceIndex,SourceTime_s,IsJoin";
        private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
        private static string F(double value) => value.ToString("G17", Culture);
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        public void Write(PreparedSweepData data, string destination, bool overwrite, CancellationToken token, IProgress<OperationProgress> progress = null)
        {
            token.ThrowIfCancellationRequested(); data.Validate();
            var csv = Path.GetFullPath(destination); var meta = Path.ChangeExtension(csv, ".metadata.json");
            if (!csv.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("CSV 확장자를 선택하세요.");
            if (string.Equals(csv, Path.GetFullPath(data.Metadata.SourcePath), StringComparison.OrdinalIgnoreCase)) throw new IOException("원본 파일을 덮어쓸 수 없습니다.");
            if (!overwrite && (File.Exists(csv) || File.Exists(meta))) throw new IOException("출력 파일이 이미 있습니다.");
            Directory.CreateDirectory(Path.GetDirectoryName(csv));
            var suffix = "." + Guid.NewGuid().ToString("N");
            var pc = csv + suffix + ".partial"; var pm = meta + suffix + ".partial";
            var bc = csv + suffix + ".backup"; var bm = meta + suffix + ".backup";
            bool publishedCsv = false, publishedMeta = false, cleanBackups = false;
            var oldHash = data.Metadata.CsvSha256;
            try
            {
                using (var file = new FileStream(pc, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
                using (var sha = SHA256.Create())
                using (var crypto = new CryptoStream(file, sha, CryptoStreamMode.Write))
                using (var writer = new StreamWriter(crypto, new UTF8Encoding(false), 1024 * 1024))
                {
                    writer.WriteLine(Header); var m = data.Metadata;
                    for (var i = 0; i < data.SampleCount; i++)
                    {
                        if ((i & 0x3fff) == 0) token.ThrowIfCancellationRequested();
                        var original = data.OriginalIndices[i];
                        writer.WriteLine(i.ToString(Culture) + "," + F(i * m.SampleIntervalSeconds) + "," + F(data.Auxiliary[i]) + "," + F(data.Measurement[i]) + "," + original.ToString(Culture)
                            + "," + F(m.SourceStartSeconds + original * m.SampleIntervalSeconds) + "," + (i > 0 && original != data.OriginalIndices[i - 1] + 1 ? "1" : "0"));
                        if ((i & 0xffff) == 0) progress?.Report(new OperationProgress { Stage = "스윕 CSV 저장", Completed = i, Total = data.SampleCount, Percent = 100.0 * i / data.SampleCount });
                    }
                    writer.Flush(); crypto.FlushFinalBlock(); data.Metadata.CsvSha256 = Hex(sha.Hash);
                }
                JsonContractSerializer.Write(pm, data.Metadata); token.ThrowIfCancellationRequested();
                AuxiliaryCsvExporter.Publish(pc, csv, bc, overwrite); publishedCsv = true;
                AuxiliaryCsvExporter.Publish(pm, meta, bm, overwrite); publishedMeta = true; cleanBackups = true;
            }
            catch (Exception error)
            {
                data.Metadata.CsvSha256 = oldHash;
                try
                {
                    if (publishedMeta) AuxiliaryCsvExporter.Restore(meta, bm);
                    if (publishedCsv) AuxiliaryCsvExporter.Restore(csv, bc);
                    cleanBackups = true;
                }
                catch (Exception rollback) { throw new AggregateException("CSV 복구 실패: backup을 보존합니다.", error, rollback); }
                throw;
            }
            finally
            {
                AuxiliaryCsvExporter.DeleteTemporary(pc); AuxiliaryCsvExporter.DeleteTemporary(pm);
                if (cleanBackups) { AuxiliaryCsvExporter.DeleteTemporary(bc); AuxiliaryCsvExporter.DeleteTemporary(bm); }
            }
        }

        public PreparedSweepData Read(string path, string expectedRole, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var csv = Path.GetFullPath(path); var metaPath = Path.ChangeExtension(csv, ".metadata.json");
            var before = new FileInfo(csv); var bytes = before.Length; var modified = before.LastWriteTimeUtc;
            var metaBefore = new FileInfo(metaPath); var metaBytes = metaBefore.Length; var metaModified = metaBefore.LastWriteTimeUtc;
            var m = JsonContractSerializer.Read<PreparedSweepMetadata>(metaPath);
            if (m == null || m.Role != expectedRole || m.RowCount < 4 || m.RowCount > 10000000) throw new InvalidDataException("스윕 역할 또는 샘플 수가 올바르지 않습니다.");
            var result = new PreparedSweepData { Metadata = m, Auxiliary = new double[m.RowCount], Measurement = new double[m.RowCount], OriginalIndices = new int[m.RowCount] };
            using (var file = new FileStream(csv, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024))
            {
                using (var reader = new StreamReader(file, Encoding.UTF8, true, 1024 * 1024, true))
                {
                    if (reader.ReadLine() != Header) throw new InvalidDataException("생성 스윕 CSV 머리글이 다릅니다.");
                    for (var i = 0; i < m.RowCount; i++)
                    {
                        if ((i & 0x3fff) == 0) token.ThrowIfCancellationRequested();
                        var line = reader.ReadLine(); if (line == null) throw new InvalidDataException("CSV 샘플 수가 metadata보다 적습니다.");
                        var cells = line.Split(','); int index, original;
                        if (cells.Length != 7 || !int.TryParse(cells[0], NumberStyles.Integer, Culture, out index) || index != i || !int.TryParse(cells[4], NumberStyles.Integer, Culture, out original)) throw new InvalidDataException("CSV 행/인덱스 오류: " + (i + 2));
                        result.Auxiliary[i] = Number(cells[2]); result.Measurement[i] = Number(cells[3]); result.OriginalIndices[i] = original;
                        var expectedJoin = i > 0 && original != result.OriginalIndices[i - 1] + 1;
                        if (Math.Abs(Number(cells[1]) - i * m.SampleIntervalSeconds) > 1e-10 || Math.Abs(Number(cells[5]) - (m.SourceStartSeconds + original * m.SampleIntervalSeconds)) > 1e-10 || cells[6] != (expectedJoin ? "1" : "0")) throw new InvalidDataException("CSV 시간축 또는 접합점 불일치: " + (i + 2));
                    }
                    if (reader.ReadLine() != null) throw new InvalidDataException("CSV 샘플 수가 metadata보다 많습니다.");
                }
                file.Position = 0;
                using (var sha = SHA256.Create())
                {
                    var buffer = new byte[1024 * 1024]; int read;
                    while ((read = file.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); sha.TransformBlock(buffer, 0, read, buffer, 0); }
                    sha.TransformFinalBlock(buffer, 0, 0);
                    if (!string.Equals(Hex(sha.Hash), m.CsvSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("CSV SHA-256이 metadata와 다릅니다.");
                }
            }
            var after = new FileInfo(csv); var metaAfter = new FileInfo(metaPath);
            if (bytes != after.Length || modified != after.LastWriteTimeUtc || metaBytes != metaAfter.Length || metaModified != metaAfter.LastWriteTimeUtc) throw new IOException("읽는 동안 생성 데이터가 변경되었습니다.");
            token.ThrowIfCancellationRequested(); result.Validate(); return result;
        }

        private static double Number(string text)
        {
            double value;
            if (!double.TryParse(text, NumberStyles.Float, Culture, out value) || !ModeHopSettings.IsFinite(value)) throw new InvalidDataException("유효하지 않은 CSV 값입니다.");
            return value;
        }
    }
}
