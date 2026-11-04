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

        private ModeHopRecording ReadInternal(string path, CancellationToken token, IProgress<OperationProgress> progress, bool firstSweepOnly)
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
                    var lineNumber = 0;
                    while ((line = reader.ReadLine()) != null)
                    {
                        token.ThrowIfCancellationRequested();
                        lineNumber++;
                        var cells = line.Split(',').Select(value => value.Trim()).ToArray();
                        if (cells.Length > 0 && string.Equals(cells[0], "TIME", StringComparison.OrdinalIgnoreCase)) { columns = cells; break; }
                        if (cells.Length > 1 && cells[0].Length > 0) header[cells[0]] = string.Join(",", cells.Skip(1)).TrimEnd(',');
                    }
                    if (columns == null) throw new InvalidDataException("Tektronix TIME 머리글이 없습니다.");
                    var triggerColumn = Array.FindIndex(columns, value => string.Equals(value, "CH2", StringComparison.OrdinalIgnoreCase));
                    var auxiliaryColumn = Array.FindIndex(columns, value => string.Equals(value, "CH3", StringComparison.OrdinalIgnoreCase));
                    if (triggerColumn < 0 || auxiliaryColumn < 0) throw new InvalidDataException("CH2 트리거와 CH3 보조간섭계 열이 모두 필요합니다.");
                    var sweepColumn = Array.FindIndex(columns, value => value.Equals("CH1", StringComparison.OrdinalIgnoreCase));
                    var measurementColumn = Array.FindIndex(columns, value => value.Equals("CH4", StringComparison.OrdinalIgnoreCase));
                    if ((sweepColumn < 0) != (measurementColumn < 0)) throw new InvalidDataException("4채널 입력에는 CH1과 CH4가 모두 필요합니다.");
                    if (columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Length) throw new InvalidDataException("중복 CSV 열입니다.");
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
                    var risingEdges = 0;
                    var row = 0;
                    double firstTime = 0, previousTime = double.NegativeInfinity;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if ((row & 0x3fff) == 0) token.ThrowIfCancellationRequested();
                        lineNumber++;
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        if (row >= count) throw new InvalidDataException("데이터 행 수가 Record Length보다 많습니다.");
                        var cells = line.Split(',');
                        if (cells.Length != columns.Length) throw new InvalidDataException("CSV 열 수 오류: " + lineNumber + "행");
                        var time = Number(cells[0], lineNumber);
                        if (time < previousTime) throw new InvalidDataException("CSV 시간축 역행: " + lineNumber + "행");
                        if (row == 0) firstTime = time;
                        previousTime = time;
                        trigger[row] = Number(cells[triggerColumn], lineNumber);
                        auxiliary[row] = Number(cells[auxiliaryColumn], lineNumber);
                        if (sweepTrigger != null)
                        {
                            sweepTrigger[row] = Number(cells[sweepColumn], lineNumber);
                            measurement[row] = Number(cells[measurementColumn], lineNumber);
                            if (row > 0 && sweepTrigger[row - 1] <= 1.8 && sweepTrigger[row] > 1.8) risingEdges++;
                        }
                        row++;
                        if (firstSweepOnly && risingEdges == 2) break;
                        if ((row & 0x1ffff) == 0) progress?.Report(new OperationProgress { Stage = "Tek CSV 읽기", Completed = row, Total = count, Percent = 90.0 * row / count });
                    }
                    if (!firstSweepOnly && row != count) throw new InvalidDataException("데이터 행 수가 Record Length와 다릅니다: " + row + " / " + count);
                    if (firstSweepOnly)
                    {
                        if (risingEdges < 2) throw new InvalidDataException("완전한 스윕을 위한 CH1 상승 에지가 두 개 필요합니다.");
                        Array.Resize(ref trigger, row); Array.Resize(ref auxiliary, row);
                        Array.Resize(ref sweepTrigger, row); Array.Resize(ref measurement, row);
                    }
                    result = new ModeHopRecording { SourcePath = Path.GetFullPath(path), Header = header, SourceStartSeconds = firstTime, SampleIntervalSeconds = interval, Trigger = trigger, Auxiliary = auxiliary, SweepTrigger = sweepTrigger, Measurement = measurement, IsPartialRead = firstSweepOnly };
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

        private static double Number(string text, int line)
        {
            double value;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !ModeHopSettings.IsFinite(value))
                throw new InvalidDataException("숫자 오류: " + line + "행 (" + text + ")");
            return value;
        }
    }
}
