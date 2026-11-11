using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Services
{
    public sealed class TekScopeCsvReader
    {
        public ModeHopRecording Read(string path, CancellationToken token, IProgress<OperationProgress> progress = null)
        { return ReadInternal(path, token, progress, false); }

        // Bounded harness validation; the UI always reads a strict complete record.
        public ModeHopRecording ReadUntilSecondSweepStart(string path, CancellationToken token)
        { return ReadInternal(path, token, null, true); }

        // Validation-only bounded read; the normal GUI reader still validates the entire record.
        public ModeHopRecording ReadUntilFirstSweepEnd(string path, CancellationToken token, bool useRecordingStart)
        { return ReadInternal(path, token, null, true, useRecordingStart); }

        private ModeHopRecording ReadInternal(string path, CancellationToken token, IProgress<OperationProgress> progress, bool firstSweepOnly, bool useRecordingStart = false)
        {
            token.ThrowIfCancellationRequested();
            var before = new FileInfo(path);
            var length = before.Length;
            var modified = before.LastWriteTimeUtc;
            var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024))
            {
                ModeHopRecording result;
                using (var reader = new StreamReader(stream, Encoding.UTF8, true, 1024 * 1024, true))
                {
                    string line;
                    string[] columns = null;
                    string[] sourceColumns = null;
                    var lineNumber = 0;
                    while ((line = reader.ReadLine()) != null)
                    {
                        token.ThrowIfCancellationRequested();
                        lineNumber++;
                        var cells = line.Split(',').Select(value => value.Trim().TrimStart('\uFEFF')).ToArray();
                        var canonical = cells.Select(CanonicalColumn).ToArray();
                        if (canonical.Contains("TIME") && canonical.Any(value => value == "CH2" || value == "CH3"))
                        { sourceColumns = cells; columns = canonical; break; }
                        if (cells.Length > 1 && cells[0].Length > 0) header[cells[0]] = string.Join(",", cells.Skip(1)).TrimEnd(',');
                    }
                    if (columns == null) throw new InvalidDataException("지원 CSV 머리글이 없습니다. TIME,CH1..CH4 또는 Index,Time_s,CH1_Ref_V,CH2_Xpol_V,CH3_Ypol_V,CH4_V 형식을 사용하세요.");
                    var timeColumn = Array.IndexOf(columns, "TIME");
                    var indexColumn = Array.IndexOf(columns, "INDEX");
                    var indexed = sourceColumns.Any(value => value.Equals("Time_s", StringComparison.OrdinalIgnoreCase));
                    var triggerColumn = Array.FindIndex(columns, value => string.Equals(value, "CH2", StringComparison.OrdinalIgnoreCase));
                    var auxiliaryColumn = Array.FindIndex(columns, value => string.Equals(value, "CH3", StringComparison.OrdinalIgnoreCase));
                    if (triggerColumn < 0 || auxiliaryColumn < 0) throw new InvalidDataException("CH2 트리거와 CH3 보조간섭계 열이 모두 필요합니다.");
                    var sweepColumn = Array.FindIndex(columns, value => value.Equals("CH1", StringComparison.OrdinalIgnoreCase));
                    var measurementColumn = Array.FindIndex(columns, value => value.Equals("CH4", StringComparison.OrdinalIgnoreCase));
                    if ((sweepColumn < 0) != (measurementColumn < 0)) throw new InvalidDataException("4채널 입력에는 CH1과 CH4가 모두 필요합니다.");
                    if (columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Length) throw new InvalidDataException("중복 CSV 열입니다.");
                    if (indexed)
                    {
                        if (indexColumn < 0 || sweepColumn < 0 || measurementColumn < 0)
                            throw new InvalidDataException("통합 4채널 CSV에는 Index, Time_s와 CH1~CH4 전압 열이 모두 필요합니다.");
                        InferIndexedMetadata(reader, columns.Length, timeColumn, header, token, progress);
                        stream.Position = 0; reader.DiscardBufferedData();
                        for (var skipped = 0; skipped < lineNumber; skipped++) reader.ReadLine();
                    }
                    header["CSV Format"] = indexed ? "Indexed four-channel voltage CSV" : "Tektronix scope CSV";
                    header["CSV Source Columns"] = string.Join(",", sourceColumns);
                    header["Channel Roles"] = "CH1=sweep trigger; CH2=mode-hop; CH3=auxiliary; CH4=measurement (physical channel numbers; label suffixes do not remap roles)";
                    if (firstSweepOnly && sweepColumn < 0) throw new InvalidDataException("CH1 시작 트리거가 필요합니다.");
                    var interval = HeaderNumber(header, "Sample Interval");
                    var declared = HeaderNumber(header, "Record Length");
                    if (interval <= 0 || declared < 2 || declared > int.MaxValue || declared != Math.Truncate(declared))
                        throw new InvalidDataException("Sample Interval 또는 Record Length가 올바르지 않습니다.");
                    string units;
                    if (header.TryGetValue("Horizontal Units", out units) && units != "s") throw new InvalidDataException("시간 단위 s가 필요합니다.");
                    if (header.TryGetValue("Vertical Units", out units) && units.Split(',').Any(value => value.Trim() != "V")) throw new InvalidDataException("채널 단위 V가 필요합니다.");
                    var count = (int)declared;
                    var trigger = new double[count];
                    var auxiliary = new double[count];
                    var sweepTrigger = sweepColumn < 0 ? null : new double[count];
                    var measurement = measurementColumn < 0 ? null : new double[count];
                    var invalidMeasurement = new List<int>();
                    var invalidSamples = new List<int>();
                    var risingEdges = 0;
                    var row = 0;
                    double firstTime = 0, previousTime = double.NegativeInfinity;
                    long firstIndex = 0;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if ((row & 0x3fff) == 0) token.ThrowIfCancellationRequested();
                        lineNumber++;
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        if (row >= count) throw new InvalidDataException("데이터 행 수가 Record Length보다 많습니다.");
                        var cells = line.Split(',');
                        if (cells.Length != columns.Length) throw new InvalidDataException("CSV 열 수 오류: " + lineNumber + "행");
                        var time = Number(cells[timeColumn], lineNumber);
                        if (time < previousTime) throw new InvalidDataException("CSV 시간축 역행: " + lineNumber + "행");
                        if (row == 0) firstTime = time;
                        if (indexed)
                        {
                            long sourceIndex;
                            if (!long.TryParse(cells[indexColumn].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out sourceIndex) || sourceIndex < 0)
                                throw new InvalidDataException("CSV Index는 0 이상의 정수여야 합니다: " + lineNumber + "행");
                            if (row == 0) { firstIndex = sourceIndex; header["First Source Index"] = firstIndex.ToString(CultureInfo.InvariantCulture); }
                            if (sourceIndex - firstIndex != row) throw new InvalidDataException("CSV Index 누락/중복/역행: " + lineNumber + "행");
                            var expectedTime = firstTime + row * interval;
                            if (Math.Abs(time - expectedTime) > Math.Max(interval * 1e-5, 1e-12))
                                throw new InvalidDataException("Time_s가 균일한 샘플 간격과 일치하지 않습니다: " + lineNumber + "행");
                        }
                        previousTime = time;
                        trigger[row] = ChannelValue(cells[triggerColumn], lineNumber);
                        auxiliary[row] = ChannelValue(cells[auxiliaryColumn], lineNumber);
                        var invalid = double.IsInfinity(trigger[row]) || double.IsInfinity(auxiliary[row]);
                        if (sweepTrigger != null)
                        {
                            sweepTrigger[row] = ChannelValue(cells[sweepColumn], lineNumber);
                            measurement[row] = ChannelValue(cells[measurementColumn], lineNumber);
                            if (double.IsInfinity(measurement[row])) invalidMeasurement.Add(row);
                            invalid |= double.IsInfinity(sweepTrigger[row]) || double.IsInfinity(measurement[row]);
                            if (row > 0 && ModeHopSettings.IsRisingEdge(sweepTrigger[row - 1], sweepTrigger[row], 1.8)) risingEdges++;
                        }
                        if (invalid) invalidSamples.Add(row);
                        row++;
                        if (firstSweepOnly && risingEdges == (useRecordingStart ? 1 : 2)) break;
                        if ((row & 0x1ffff) == 0) progress?.Report(new OperationProgress { Stage = indexed ? "통합 CSV 읽기" : "Tek CSV 읽기", Completed = row, Total = count,
                            Percent = indexed ? 10 + 80.0 * row / count : 90.0 * row / count });
                    }
                    if (!firstSweepOnly && row != count) throw new InvalidDataException("데이터 행 수가 Record Length와 다릅니다: " + row + " / " + count);
                    if (firstSweepOnly)
                    {
                        if (risingEdges < (useRecordingStart ? 1 : 2)) throw new InvalidDataException(useRecordingStart ? "기록 시작 수동 스윕의 끝을 위한 CH1 상승 에지가 필요합니다." : "완전한 스윕을 위한 CH1 상승 에지가 두 개 필요합니다.");
                        Array.Resize(ref trigger, row); Array.Resize(ref auxiliary, row);
                        Array.Resize(ref sweepTrigger, row); Array.Resize(ref measurement, row);
                    }
                    result = new ModeHopRecording { SourcePath = Path.GetFullPath(path), Header = header, SourceStartSeconds = firstTime, SampleIntervalSeconds = interval, Trigger = trigger, Auxiliary = auxiliary, SweepTrigger = sweepTrigger, Measurement = measurement, InvalidMeasurementIndices = invalidMeasurement.ToArray(), InvalidSampleIndices = invalidSamples.ToArray(), IsPartialRead = firstSweepOnly };
                }
                token.ThrowIfCancellationRequested();
                stream.Position = 0;
                using (var sha = SHA256.Create())
                {
                    var buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        sha.TransformBlock(buffer, 0, read, buffer, 0);
                    }
                    sha.TransformFinalBlock(buffer, 0, 0);
                    result.Sha256 = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                }
                var after = new FileInfo(path);
                if (after.Length != length || after.LastWriteTimeUtc != modified) throw new IOException("읽는 동안 CSV가 변경되었습니다. 저장 완료 후 다시 읽으세요.");
                progress?.Report(new OperationProgress { Stage = "Tek CSV 읽기 완료", Completed = result.SampleCount, Total = result.SampleCount, Percent = 100 });
                return result;
            }
        }

        private static double HeaderNumber(IDictionary<string, string> header, string name)
        {
            string value;
            if (!header.TryGetValue(name, out value)) throw new InvalidDataException("필수 헤더 누락: " + name);
            return Number(value, 0);
        }

        // Match physical channel numbers only. Acquisition-export suffixes describe labels,
        // not the confirmed wiring used by the four-channel GUI.
        internal static string CanonicalColumn(string value)
        {
            value = value.Trim().TrimStart('\uFEFF');
            if (value.Equals("TIME", StringComparison.OrdinalIgnoreCase) || value.Equals("Time_s", StringComparison.OrdinalIgnoreCase)) return "TIME";
            if (value.Equals("Index", StringComparison.OrdinalIgnoreCase)) return "INDEX";
            var aliases = new[] { "CH1_Ref_V", "CH2_Xpol_V", "CH3_Ypol_V", "CH4_V" };
            for (var i = 1; i <= 4; i++)
                if (value.Equals("CH" + i, StringComparison.OrdinalIgnoreCase) || value.Equals("CH" + i + "_V", StringComparison.OrdinalIgnoreCase)
                    || value.Equals(aliases[i - 1], StringComparison.OrdinalIgnoreCase)) return "CH" + i;
            return value;
        }

        // No acquisition preamble is present in indexed exports. Count actual data rows
        // and derive dt from Time_s; never trust the sample count/rate in the filename.
        private static void InferIndexedMetadata(StreamReader reader, int columns, int timeColumn, IDictionary<string, string> header,
            CancellationToken token, IProgress<OperationProgress> progress)
        {
            string line, lastDataLine = null; int count = 0; double first = 0, second = 0;
            while ((line = reader.ReadLine()) != null)
            {
                if ((count & 0x3fff) == 0) token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                // Count all rows without allocating four full channel lists or splitting all rows twice.
                if (count < 2) {
                    var cells = line.Split(',');
                    if (cells.Length != columns) throw new InvalidDataException("CSV 열 수 오류: metadata 사전 읽기");
                    var time = Number(cells[timeColumn], count + 2);
                    if (count == 0) first = time; else second = time;
                }
                lastDataLine = line;
                count = checked(count + 1);
                if ((count & 0xfffff) == 0) progress?.Report(new OperationProgress { Stage = "통합 CSV 행 수·시간축 확인 (" + count.ToString("N0", CultureInfo.InvariantCulture) + "행)", Completed = count,
                    Percent = 10.0 * reader.BaseStream.Position / Math.Max(1, reader.BaseStream.Length) });
            }
            if (count < 2 || second <= first) throw new InvalidDataException("통합 CSV에서 유효한 Time_s 간격을 구할 수 없습니다.");
            var finalCells = lastDataLine.Split(',');
            if (finalCells.Length != columns) throw new InvalidDataException("통합 CSV 마지막 행의 열 수가 올바르지 않습니다.");
            var interval = (Number(finalCells[timeColumn], count + 1) - first) / (count - 1);
            if (interval <= 0 || !ModeHopSettings.IsFinite(interval)) throw new InvalidDataException("통합 CSV에서 유효한 Time_s 간격을 구할 수 없습니다.");
            string declared;
            if (header.TryGetValue("Record Length", out declared) && Number(declared, 0) != count) throw new InvalidDataException("통합 CSV 행 수와 선언된 Record Length가 다릅니다.");
            if (header.TryGetValue("Sample Interval", out declared) && Math.Abs(Number(declared, 0) - interval) > Math.Max(interval * 1e-5, 1e-12))
                throw new InvalidDataException("통합 CSV Time_s와 선언된 Sample Interval이 다릅니다.");
            header["Record Length"] = count.ToString(CultureInfo.InvariantCulture);
            header["Sample Interval"] = interval.ToString("R", CultureInfo.InvariantCulture);
            header["Horizontal Units"] = "s"; header["Vertical Units"] = "V,V,V,V";
            header["Record Length Origin"] = "Counted actual nonempty data rows";
            header["Sample Interval Origin"] = "(last Time_s - first Time_s)/(actual rows - 1); full axis and Index validated during reading";
        }

        private static bool IsInfinityToken(string text)
        {
            if (text.StartsWith("+", StringComparison.Ordinal) || text.StartsWith("-", StringComparison.Ordinal)) text = text.Substring(1);
            return text.Equals("inf", StringComparison.OrdinalIgnoreCase) || text.Equals("infinity", StringComparison.OrdinalIgnoreCase);
        }

        internal static double ChannelValue(string text, int line)
        {
            text = text.Trim();
            if (IsInfinityToken(text)) return text.StartsWith("-", StringComparison.Ordinal) ? double.NegativeInfinity : double.PositiveInfinity;
            return Number(text, line);
        }

        private static double Number(string text, int line)
        {
            double value;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !ModeHopSettings.IsFinite(value))
                throw new InvalidDataException("숫자 오류: " + line + "행 (" + text + ")");
            return value;
        }
    }
}
