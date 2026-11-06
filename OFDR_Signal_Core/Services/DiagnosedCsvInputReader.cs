using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Services
{
    // Four-file adapter. Auxiliary CSV coordinates, never filename conventions, define CH4 selection.
    public sealed class DiagnosedCsvInputReader
    {
        private static readonly CultureInfo C = CultureInfo.InvariantCulture;
        public static bool IsAuxiliaryCsv(string path)
        {
            try { using (var file = Open(path)) using (var reader = new StreamReader(file)) return reader.ReadLine() == AuxiliaryCsvExporter.Header; }
            catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; } catch (ArgumentException) { return false; }
        }
        public static PreparedSweepMetadata Preview(string path, string role)
        {
            var original = ReadMetadata(path);
            return Metadata(path, role, original, original.RowCount, null, false);
        }
        public IndependentOfdrInput Read(string refAuxiliary, string sigAuxiliary, string reference, string signal, CancellationToken token, IProgress<OperationProgress> progress = null, int maximumSamplesForValidation = 0)
        {
            if (maximumSamplesForValidation != 0 && (maximumSamplesForValidation < 4 || maximumSamplesForValidation > 250000)) throw new ArgumentOutOfRangeException(nameof(maximumSamplesForValidation));
            var r = ReadPair(refAuxiliary, reference, "Ref", token, progress, maximumSamplesForValidation);
            var s = ReadPair(sigAuxiliary, signal, "Signal", token, progress, maximumSamplesForValidation);
            return PreparedSweepData.ToInput(r, s);
        }
        public PreparedSweepData ReadPair(string auxiliaryCsv, string measurementPath, string role, CancellationToken token, IProgress<OperationProgress> progress = null, int maximumSamplesForValidation = 0)
        {
            token.ThrowIfCancellationRequested();
            if (maximumSamplesForValidation != 0 && (maximumSamplesForValidation < 4 || maximumSamplesForValidation > 250000)) throw new ArgumentOutOfRangeException(nameof(maximumSamplesForValidation));
            auxiliaryCsv = Path.GetFullPath(auxiliaryCsv); measurementPath = Path.GetFullPath(measurementPath);
            var before = Snapshot(auxiliaryCsv); var sidecar = Path.ChangeExtension(auxiliaryCsv, ".metadata.json"); var beforeMetadata = Snapshot(sidecar);
            var original = ReadMetadata(auxiliaryCsv);
            var count = maximumSamplesForValidation > 0 ? Math.Min(original.RowCount, maximumSamplesForValidation) : original.RowCount;
            var partial = count != original.RowCount; var auxiliary = new double[count]; var indices = new int[count];
            using (var file = Open(auxiliaryCsv)) using (var reader = new StreamReader(file))
            {
                if (reader.ReadLine() != AuxiliaryCsvExporter.Header) throw new InvalidDataException("보조간섭계 입력에는 진단 구간 CSV(Index,Time_s,Voltage_V,SourceIndex,SourceTime_s,IsJoin)를 선택하세요.");
                for (var i = 0; i < count; i++)
                {
                    if ((i & 0x3fff) == 0) { token.ThrowIfCancellationRequested(); progress?.Report(new OperationProgress { Stage = role + " 진단 CSV 읽기", Completed = i, Total = count, Percent = 100.0 * i / count }); }
                    var line = reader.ReadLine(); if (line == null) throw new InvalidDataException("CSV 샘플 수가 metadata보다 적습니다.");
                    var cells = line.Split(','); int index, sourceIndex;
                    if (cells.Length != 6 || !int.TryParse(cells[0], NumberStyles.Integer, C, out index) || index != original.StartIndex + i || !int.TryParse(cells[3], NumberStyles.Integer, C, out sourceIndex)
                        || sourceIndex < 0 || (i > 0 && sourceIndex <= indices[i - 1])) throw new InvalidDataException("진단 CSV의 인덱스가 올바르지 않습니다: " + (i + 2));
                    auxiliary[i] = Number(cells[2]); indices[i] = sourceIndex;
                    if (Math.Abs(Number(cells[1]) - index * original.SampleIntervalSeconds) > 1e-10 || Math.Abs(Number(cells[4]) - (original.SourceStartSeconds + sourceIndex * original.SampleIntervalSeconds)) > 1e-10
                        || (cells[5] != "0" && cells[5] != "1") || (i > 0 && cells[5] != (sourceIndex != indices[i - 1] + 1 ? "1" : "0"))) throw new InvalidDataException("진단 CSV 시간축 또는 접합점 불일치: " + (i + 2));
                }
                if (!partial && reader.ReadLine() != null) throw new InvalidDataException("CSV 샘플 수가 metadata보다 많습니다.");
            }
            var csvHash = Hash(auxiliaryCsv, token);
            if (!string.Equals(csvHash, original.CsvSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("진단 CSV의 SHA-256이 metadata와 다릅니다.");
            Unchanged(auxiliaryCsv, before); Unchanged(sidecar, beforeMetadata);
            var measurementBefore = Snapshot(measurementPath); double[] measurement; string measurementHash;
            using (var file = Open(measurementPath)) using (var reader = new StreamReader(file))
            {
                var firstLine = reader.ReadLine();
                if (firstLine == PreparedSweepCsv.Header)
                {
                    if (partial) throw new InvalidOperationException("통합 CSV의 제한 샘플 검증에는 원본 CH4 CSV를 선택하세요.");
                    var pair = new PreparedSweepCsv().Read(measurementPath, role, token);
                    if (pair.SampleCount != original.RowCount || !string.Equals(pair.Metadata.SourceSha256, original.SourceSha256, StringComparison.OrdinalIgnoreCase) || pair.Metadata.SampleIntervalSeconds != original.SampleIntervalSeconds)
                        throw new InvalidDataException(role + " 통합 측정 CSV가 보조간섭계의 원본·길이·샘플 간격과 다릅니다.");
                    if (!indices.SequenceEqual(pair.OriginalIndices)) throw new InvalidDataException("보조/측정 CSV의 원본 인덱스가 다릅니다.");
                    measurement = pair.Measurement; measurementHash = pair.Metadata.CsvSha256;
                }
                else
                {
                    if (firstLine == AuxiliaryCsvExporter.Header) throw new InvalidDataException("선택한 측정 CSV는 CH3 보조간섭계만 포함합니다. CH4가 있는 원본 Tektronix CSV 또는 통합 CSV를 선택하세요.");
                    measurement = ReadRawMeasurement(reader, firstLine, original, indices, auxiliary, role, token, progress);
                    measurementHash = Hash(measurementPath, token);
                    if (!string.Equals(measurementHash, original.SourceSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(role + " 측정 원본 SHA-256이 진단 CSV의 취득 원본과 다릅니다.");
                }
            }
            Unchanged(measurementPath, measurementBefore);
            Unchanged(auxiliaryCsv, before); Unchanged(sidecar, beforeMetadata);
            var meta = Metadata(auxiliaryCsv, role, original, count, indices, partial);
            meta.FileInputs.AuxiliaryCsvSha256 = csvHash; meta.FileInputs.MeasurementPath = measurementPath; meta.FileInputs.MeasurementSha256 = measurementHash;
            var output = new PreparedSweepData { Metadata = meta, Auxiliary = auxiliary, Measurement = measurement, OriginalIndices = indices };
            token.ThrowIfCancellationRequested(); output.Validate(); return output;
        }
        private static double[] ReadRawMeasurement(StreamReader reader, string firstLine, AuxiliaryCsvExportMetadata original, int[] indices, double[] auxiliary, string role, CancellationToken token, IProgress<OperationProgress> progress)
        {
            var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); string line = firstLine; string[] columns = null;
            for (var h = 0; line != null && h < 1000; h++, line = reader.ReadLine())
            {
                var cells = line.Split(',');
                if (cells[0].Trim() == "TIME") { columns = cells.Select(s => s.Trim()).ToArray(); break; }
                if (cells.Length > 1) header[cells[0].Trim()] = cells[1].Trim();
            }
            if (columns == null || !header.ContainsKey("Sample Interval") || !header.ContainsKey("Record Length")) throw new InvalidDataException("측정 입력에는 CH4가 있는 Tektronix 원본 CSV를 선택하세요.");
            var auxColumn = Array.IndexOf(columns, "CH3"); var sigColumn = Array.IndexOf(columns, "CH4");
            var dt = Number(header["Sample Interval"]); var declared = Number(header["Record Length"]);
            if (auxColumn < 0 || sigColumn < 0 || Math.Abs(dt - original.SampleIntervalSeconds) > Math.Max(1e-15, dt * 1e-9) || declared <= indices[indices.Length - 1]) throw new InvalidDataException("측정 원본의 CH3/CH4, 샘플 간격 또는 길이가 진단 CSV와 맞지 않습니다.");
            var values = new double[indices.Length]; var row = 0; var output = 0;
            while ((line = reader.ReadLine()) != null)
            {
                if ((row & 0x3fff) == 0) token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (row == indices[output])
                {
                    var cells = line.Split(','); if (cells.Length != columns.Length) throw new InvalidDataException("측정 원본 열 수 오류입니다.");
                    if (Number(cells[auxColumn]) != auxiliary[output]) throw new InvalidDataException(role + " 보조간섭계 전압이 같은 SourceIndex의 원본 CH3와 다릅니다: " + row);
                    values[output] = Number(cells[sigColumn]); output++;
                    if ((output & 0x1ffff) == 0) progress?.Report(new OperationProgress { Stage = role + " CH4 원본 인덱스 정렬", Completed = output, Total = values.Length, Percent = 100.0 * output / values.Length });
                    if (output == values.Length) return values;
                }
                row++;
            }
            throw new InvalidDataException("측정 원본에 필요한 SourceIndex 샘플이 없습니다.");
        }
        private static AuxiliaryCsvExportMetadata ReadMetadata(string path)
        {
            if (!IsAuxiliaryCsv(path)) throw new InvalidDataException("저장된 CH3 진단 CSV를 선택하세요.");
            var m = JsonContractSerializer.Read<AuxiliaryCsvExportMetadata>(Path.ChangeExtension(path, ".metadata.json"));
            if (m == null || m.SchemaVersion != 1 || m.RowCount < 4 || m.RowCount > 10000000 || m.EndIndexExclusive - m.StartIndex != m.RowCount || m.StartIndex < 0
                || !ModeHopSettings.IsFinite(m.SampleIntervalSeconds) || m.SampleIntervalSeconds <= 0 || !ModeHopSettings.IsFinite(m.SourceStartSeconds) || !ModeHopSettings.IsFinite(m.ThresholdVoltage)
                || !ModeHopSettings.IsFinite(m.ExclusionTailSeconds) || m.ExclusionTailSeconds < 0
                || !ModeHopSettings.IsFinite(m.ExclusionLeadSeconds) || m.ExclusionLeadSeconds < 0
                || string.IsNullOrWhiteSpace(m.SourcePath) || m.SourceSha256?.Length != 64 || m.CsvSha256?.Length != 64 || m.SourceHeader == null)
                throw new InvalidDataException("진단 CSV metadata의 원본·좌표·샘플 수가 올바르지 않습니다.");
            return m;
        }
        private static PreparedSweepMetadata Metadata(string csv, string role, AuxiliaryCsvExportMetadata original, int count, int[] indices, bool partial)
        {
            var matching = !partial && original.LastDiagnosis?.Phase != null && original.LastDiagnosis.SelectedStartIndex == original.StartIndex && original.LastDiagnosis.SelectedEndIndexExclusive == original.EndIndexExclusive && original.LastDiagnosis.Phase.SampleCount == count;
            var diagnosis = matching ? JsonContractSerializer.Clone(original.LastDiagnosis) : null;
            var settings = matching && original.LastDiagnosisSettings != null ? JsonContractSerializer.Clone(original.LastDiagnosisSettings) : null;
            if (diagnosis != null) { diagnosis.SelectedStartIndex = 0; diagnosis.SelectedEndIndexExclusive = count; diagnosis.SelectedStartSeconds = 0; diagnosis.SelectedEndSeconds = count * original.SampleIntervalSeconds; }
            if (settings != null) { settings.StartSeconds = 0; settings.EndSeconds = count * original.SampleIntervalSeconds; }
            var ranges = new List<ModeHopRange>();
            if (indices != null) for (var i = 1; i < indices.Length; i++) if (indices[i] != indices[i - 1] + 1)
                ranges.Add(new ModeHopRange { Number = ranges.Count + 1, StartIndex = indices[i - 1] + 1, EndIndexExclusive = indices[i], StartSeconds = (indices[i - 1] + 1) * original.SampleIntervalSeconds, EndSeconds = indices[i] * original.SampleIntervalSeconds });
            return new PreparedSweepMetadata { Role = role, SourcePath = original.SourcePath, SourceSha256 = original.SourceSha256, SourceHeader = original.SourceHeader,
                SourceStartSeconds = original.SourceStartSeconds, SampleIntervalSeconds = original.SampleIntervalSeconds, ModeHopThresholdVoltage = original.ThresholdVoltage,
                ModeHopExclusionTailSeconds = original.ExclusionTailSeconds,
                ModeHopExclusionLeadSeconds = original.ExclusionLeadSeconds,
                IgnoredMeasurementRanges = original.IgnoredMeasurementRanges,
                IgnoredSampleRanges = original.IgnoredSampleRanges,
                Sweep = indices == null ? null : new SweepRange { Number = 0, StartIndex = indices[0], EndIndexExclusive = indices[indices.Length - 1] + 1, StartSeconds = indices[0] * original.SampleIntervalSeconds, EndSeconds = (indices[indices.Length - 1] + 1) * original.SampleIntervalSeconds },
                SelectedStartIndex = 0, SelectedEndIndexExclusive = count, RowCount = count, Diagnosis = diagnosis, DiagnosisSettings = settings, ExcludedRanges = ranges,
                FileInputs = new SavedCsvInputFiles { AuxiliaryCsvPath = Path.GetFullPath(csv), AuxiliaryCsvSha256 = original.CsvSha256, OriginalExport = original, LoadedRows = count, PartialValidationInput = partial } };
        }
        private static FileStream Open(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024);
        private static Tuple<long, DateTime> Snapshot(string path) { var f = new FileInfo(path); return Tuple.Create(f.Length, f.LastWriteTimeUtc); }
        private static void Unchanged(string path, Tuple<long, DateTime> before) { var now = Snapshot(path); if (now.Item1 != before.Item1 || now.Item2 != before.Item2) throw new IOException("읽는 동안 입력 파일이 변경되었습니다: " + path); }
        private static string Hash(string path, CancellationToken token)
        {
            using (var file = Open(path)) using (var sha = SHA256.Create()) { var buffer = new byte[1024 * 1024]; int read; while ((read = file.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); sha.TransformBlock(buffer, 0, read, buffer, 0); } sha.TransformFinalBlock(buffer, 0, 0); return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant(); }
        }
        private static double Number(string text) { double value; if (!double.TryParse(text, NumberStyles.Float, C, out value) || !ModeHopSettings.IsFinite(value)) throw new InvalidDataException("유효하지 않은 숫자입니다."); return value; }
    }
}
