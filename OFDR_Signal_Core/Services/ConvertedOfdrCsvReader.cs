using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Services
{
    public sealed class ConvertedOfdrCsvReader
    {
        public ConvertedOfdrCsvData Read(string csvPath, CancellationToken cancellationToken, IProgress<OperationProgress> progress = null)
        {
            if (string.IsNullOrWhiteSpace(csvPath)) throw new ArgumentException("CSV path is required.", nameof(csvPath));
            if (!File.Exists(csvPath)) throw new FileNotFoundException("Converted CSV file was not found.", csvPath);

            var fileInfo = new FileInfo(csvPath);
            var capacity = EstimateCapacity(csvPath);
            var time = new List<double>(capacity);
            var reference = new List<double>(capacity);
            var xpol = new List<double>(capacity);
            var ypol = new List<double>(capacity);

            using (var stream = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
            using (var reader = new StreamReader(stream))
            {
                var header = reader.ReadLine();
                if (header == null) throw new InvalidDataException("CSV header is missing.");
                var columns = BuildColumnMap(header);
                var rowIndex = 0;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (line.Length == 0) continue;
                    var values = line.Split(',');
                    time.Add(Parse(values, columns, "Time_s", rowIndex));
                    reference.Add(Parse(values, columns, "CH1_Ref_V", rowIndex));
                    xpol.Add(Parse(values, columns, "CH2_Xpol_V", rowIndex));
                    ypol.Add(Parse(values, columns, "CH3_Ypol_V", rowIndex));
                    rowIndex++;
                    if (rowIndex == 1 || (rowIndex % 262144) == 0)
                    {
                        progress?.Report(new OperationProgress
                        {
                            Stage = "Read CSV",
                            Detail = Path.GetFileName(csvPath),
                            Completed = rowIndex,
                            Total = 0,
                            Percent = fileInfo.Length == 0 ? 0.0 : Math.Min(99.0, 100.0 * stream.Position / fileInfo.Length)
                        });
                    }
                }
            }

            if (reference.Count == 0) throw new InvalidDataException("CSV contains no data rows.");
            if (reference.Count != xpol.Count || reference.Count != ypol.Count || reference.Count != time.Count)
                throw new InvalidDataException("CSV channel lengths do not match.");

            progress?.Report(new OperationProgress { Stage = "Read CSV", Detail = Path.GetFileName(csvPath), Completed = reference.Count, Total = reference.Count, Percent = 100.0 });
            return new ConvertedOfdrCsvData
            {
                SetId = Path.GetFileNameWithoutExtension(csvPath),
                CsvPath = Path.GetFullPath(csvPath),
                RowCount = reference.Count,
                TimeSeconds = time.ToArray(),
                Reference = reference.ToArray(),
                XPolarization = xpol.ToArray(),
                YPolarization = ypol.ToArray()
            };
        }

        private static Dictionary<string, int> BuildColumnMap(string header)
        {
            var names = header.Split(',');
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < names.Length; i++)
            {
                var name = (names[i] ?? string.Empty).Trim();
                if (name.Length > 0 && !result.ContainsKey(name)) result.Add(name, i);
            }

            Require(result, "Index");
            Require(result, "Time_s");
            Require(result, "CH1_Ref_V");
            Require(result, "CH2_Xpol_V");
            Require(result, "CH3_Ypol_V");
            return result;
        }

        private static void Require(Dictionary<string, int> columns, string name)
        {
            if (!columns.ContainsKey(name)) throw new InvalidDataException("Required CSV column is missing: " + name);
        }

        private static double Parse(string[] values, Dictionary<string, int> columns, string name, int rowIndex)
        {
            var index = columns[name];
            if (index >= values.Length) throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "CSV row {0:N0} is missing {1}.", rowIndex + 2, name));
            double value;
            if (!double.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "CSV row {0:N0} has invalid {1}: {2}", rowIndex + 2, name, values[index]));
            return value;
        }

        private static int EstimateCapacity(string csvPath)
        {
            try
            {
                var metadataPath = Path.Combine(Path.GetDirectoryName(csvPath) ?? string.Empty, Path.GetFileNameWithoutExtension(csvPath) + ".metadata.json");
                if (File.Exists(metadataPath))
                {
                    var metadata = JsonContractSerializer.Read<CsvSetMetadata>(metadataPath);
                    if (metadata != null && metadata.RowCount > 0) return (int)Math.Min(metadata.RowCount, int.MaxValue);
                }
            }
            catch
            {
            }
            return 1024 * 1024;
        }
    }
}
