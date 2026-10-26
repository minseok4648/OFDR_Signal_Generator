using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Services
{
    public sealed class OfdrChannelFileReader
    {
        private static readonly Regex IdentityPattern = new Regex(@"^(?<disp>\d+_\d+)mm(?<rep>\d{4})(?:CH(?<ch>[123]))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private readonly IsfWaveformReader isfReader = new IsfWaveformReader();

        public OfdrChannelData Read(string path, int expectedChannel, CancellationToken cancellationToken, IProgress<OperationProgress> progress = null)
        {
            if (expectedChannel < 1 || expectedChannel > 3) throw new ArgumentOutOfRangeException(nameof(expectedChannel));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("입력 파일 경로가 필요합니다.", nameof(path));
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException("입력 파일을 찾을 수 없습니다.", path);
            var extension = Path.GetExtension(path);
            if (string.Equals(extension, ".isf", StringComparison.OrdinalIgnoreCase)) return ReadIsf(path, expectedChannel, cancellationToken, progress);
            if (string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase)) return ReadCsv(path, expectedChannel, cancellationToken, progress);
            throw new NotSupportedException("ISF 또는 CSV 파일만 지원합니다: " + extension);
        }

        private OfdrChannelData ReadIsf(string path, int expectedChannel, CancellationToken token, IProgress<OperationProgress> progress)
        {
            var header = isfReader.ReadHeader(path);
            var identity = ParseIdentity(path, expectedChannel);
            if (identity.ChannelNumber != expectedChannel)
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "선택한 파일은 CH{0}이지만 이 입력에는 CH{1}이 필요합니다.", identity.ChannelNumber, expectedChannel));
            var values = isfReader.ReadAllVoltages(path, token, progress);
            identity.SourcePath = path;
            identity.SourceType = "ISF";
            identity.SampleIntervalSeconds = header.XIncrement;
            identity.Values = values;
            return identity;
        }

        private static OfdrChannelData ReadCsv(string path, int expectedChannel, CancellationToken token, IProgress<OperationProgress> progress)
        {
            var identity = ParseIdentity(path, expectedChannel);
            if (identity.ChannelNumber != expectedChannel)
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "선택한 CSV는 CH{0}이지만 이 입력에는 CH{1}이 필요합니다.", identity.ChannelNumber, expectedChannel));

            var fileInfo = new FileInfo(path);
            var capacity = EstimateCapacity(path);
            var values = new List<double>(capacity);
            double firstTime = 0.0;
            double lastTime = 0.0;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
            using (var reader = new StreamReader(stream))
            {
                var headerLine = reader.ReadLine();
                if (headerLine == null) throw new InvalidDataException("CSV 머리글이 없습니다.");
                var columns = BuildColumnMap(headerLine);
                var timeColumn = Require(columns, "Time_s");
                int voltageColumn;
                if (!columns.TryGetValue("Voltage_V", out voltageColumn))
                {
                    var integratedName = expectedChannel == 1 ? "CH1_Ref_V" : expectedChannel == 2 ? "CH2_Xpol_V" : "CH3_Ypol_V";
                    voltageColumn = Require(columns, integratedName);
                }

                string line;
                var row = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    token.ThrowIfCancellationRequested();
                    if (line.Length == 0) continue;
                    var cells = line.Split(',');
                    var time = ParseCell(cells, timeColumn, "Time_s", row);
                    var voltage = ParseCell(cells, voltageColumn, "Voltage", row);
                    if (row == 0) firstTime = time;
                    lastTime = time;
                    values.Add(voltage);
                    row++;
                    if (row == 1 || (row & 0x3ffff) == 0)
                        progress?.Report(new OperationProgress { Stage = "CSV 읽기", Detail = Path.GetFileName(path), Completed = row, Percent = fileInfo.Length == 0 ? 0.0 : Math.Min(99.0, 100.0 * stream.Position / fileInfo.Length) });
                }
            }
            if (values.Count < 4) throw new InvalidDataException("CSV에는 최소 4개의 샘플이 필요합니다.");
            identity.SourcePath = path;
            identity.SourceType = "CSV";
            identity.SampleIntervalSeconds = (lastTime - firstTime) / Math.Max(1, values.Count - 1);
            identity.Values = values.ToArray();
            progress?.Report(new OperationProgress { Stage = "CSV 읽기", Detail = Path.GetFileName(path), Completed = values.Count, Total = values.Count, Percent = 100.0 });
            return identity;
        }

        public static OfdrChannelData ParseIdentity(string path, int fallbackChannel)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var match = IdentityPattern.Match(name);
            if (!match.Success) throw new InvalidDataException("OFDR 파일 이름을 해석할 수 없습니다: " + name);
            var displacement = double.Parse(match.Groups["disp"].Value.Replace('_', '.'), CultureInfo.InvariantCulture);
            var channel = match.Groups["ch"].Success ? int.Parse(match.Groups["ch"].Value, CultureInfo.InvariantCulture) : fallbackChannel;
            return new OfdrChannelData
            {
                MeasurementId = match.Groups["disp"].Value + "mm" + match.Groups["rep"].Value + "CH" + channel,
                DisplacementMm = displacement,
                Repetition = match.Groups["rep"].Value,
                ChannelNumber = channel
            };
        }

        private static Dictionary<string, int> BuildColumnMap(string header)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var cells = header.Split(',');
            for (var i = 0; i < cells.Length; i++)
            {
                var name = cells[i].Trim();
                if (name.Length > 0 && !result.ContainsKey(name)) result.Add(name, i);
            }
            return result;
        }

        private static int Require(IDictionary<string, int> columns, string name)
        {
            int index;
            if (!columns.TryGetValue(name, out index)) throw new InvalidDataException("필수 CSV 열이 없습니다: " + name);
            return index;
        }

        private static double ParseCell(string[] cells, int index, string name, int row)
        {
            if (index >= cells.Length) throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "CSV {0}행에 {1} 값이 없습니다.", row + 2, name));
            double value;
            if (!double.TryParse(cells[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "CSV {0}행의 {1} 값이 올바르지 않습니다.", row + 2, name));
            return value;
        }

        private static int EstimateCapacity(string path)
        {
            try
            {
                var metadataPath = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path) + ".metadata.json");
                if (!File.Exists(metadataPath)) return 1024 * 1024;
                try
                {
                    var channel = JsonContractSerializer.Read<ChannelCsvMetadata>(metadataPath);
                    if (channel != null && channel.RowCount > 0) return (int)Math.Min(channel.RowCount, int.MaxValue);
                }
                catch
                {
                    var set = JsonContractSerializer.Read<CsvSetMetadata>(metadataPath);
                    if (set != null && set.RowCount > 0) return (int)Math.Min(set.RowCount, int.MaxValue);
                }
            }
            catch { }
            return 1024 * 1024;
        }
    }
}
