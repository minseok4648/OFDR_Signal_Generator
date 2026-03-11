using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace OFDR_Signal_Generator.Services
{
    public sealed class CsvSignalLoader
    {
        public LoadedSignalData Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("CSV path is required.", nameof(path));
            }

            var lines = File.ReadAllLines(path);
            if (lines.Length == 0)
            {
                throw new InvalidOperationException("CSV file is empty.");
            }

            string[] header = null;
            var dataRows = new List<string[]>();
            var delimiter = ',';

            foreach (var rawLine in lines)
            {
                var line = rawLine == null ? string.Empty : rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                {
                    continue;
                }

                if (header == null)
                {
                    delimiter = DetectDelimiter(line);
                    header = line.Split(delimiter).Select(item => item.Trim()).ToArray();
                    continue;
                }

                var parts = line.Split(delimiter).Select(item => item.Trim()).ToArray();
                if (parts.Length > 1)
                {
                    dataRows.Add(parts);
                }
            }

            if (header == null || dataRows.Count == 0)
            {
                throw new InvalidOperationException("CSV file does not contain a readable header and data rows.");
            }

            var headerLookup = header
                .Select((value, index) => new { value = value.ToLowerInvariant(), index })
                .ToDictionary(item => item.value, item => item.index);

            var amplitudeIndex = FindColumnIndex(headerLookup, "amplitude");
            var realIndex = FindColumnIndex(headerLookup, "i_re", "i_signal(re)", "i_signal", "re");
            var imaginaryIndex = FindColumnIndex(headerLookup, "q_im", "q_signal(im)", "q_signal", "im");
            var timeIndex = FindColumnIndex(headerLookup, "time_norm", "time_normalized(0-1)", "time", "time_normalized");

            if (amplitudeIndex < 0 && realIndex < 0)
            {
                throw new InvalidOperationException("CSV file must contain either an Amplitude column or an I_Re column.");
            }

            var samples = new List<double>();
            var complexSamples = new List<Complex>();
            var times = new List<double>();

            foreach (var row in dataRows)
            {
                var valueIndex = amplitudeIndex >= 0 ? amplitudeIndex : realIndex;
                if (valueIndex >= row.Length)
                {
                    continue;
                }

                double sample;
                if (!double.TryParse(row[valueIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out sample))
                {
                    continue;
                }

                samples.Add(sample);

                var real = sample;
                var imaginary = 0.0;

                if (realIndex >= 0 && realIndex < row.Length)
                {
                    double realValue;
                    if (double.TryParse(row[realIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out realValue))
                    {
                        real = realValue;
                    }
                }

                if (imaginaryIndex >= 0 && imaginaryIndex < row.Length)
                {
                    double imaginaryValue;
                    if (double.TryParse(row[imaginaryIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out imaginaryValue))
                    {
                        imaginary = imaginaryValue;
                    }
                }

                complexSamples.Add(new Complex(real, imaginary));

                if (timeIndex >= 0 && timeIndex < row.Length)
                {
                    double timeValue;
                    if (double.TryParse(row[timeIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out timeValue))
                    {
                        times.Add(timeValue);
                    }
                }
            }

            if (samples.Count == 0)
            {
                throw new InvalidOperationException("No numeric samples could be parsed from the CSV file.");
            }

            double? sampleSpacing = null;
            if (times.Count >= 2)
            {
                sampleSpacing = times.Zip(times.Skip(1), (first, second) => second - first)
                    .Where(delta => delta > 0)
                    .DefaultIfEmpty(0.0)
                    .Average();

                if (sampleSpacing <= 0.0)
                {
                    sampleSpacing = null;
                }
            }

            return new LoadedSignalData
            {
                Samples = samples.ToArray(),
                ComplexSamples = complexSamples.ToArray(),
                SampleSpacing = sampleSpacing,
                SourcePath = path,
                SampleColumnName = amplitudeIndex >= 0 ? header[amplitudeIndex] : header[realIndex],
                HasImaginaryComponent = imaginaryIndex >= 0
            };
        }

        private static int FindColumnIndex(IReadOnlyDictionary<string, int> headerLookup, params string[] names)
        {
            foreach (var name in names)
            {
                int index;
                if (headerLookup.TryGetValue(name, out index))
                {
                    return index;
                }
            }

            return -1;
        }

        private static char DetectDelimiter(string headerLine)
        {
            if (headerLine.Contains(";"))
            {
                return ';';
            }

            if (headerLine.Contains("\t"))
            {
                return '\t';
            }

            return ',';
        }
    }

    public sealed class LoadedSignalData
    {
        public double[] Samples { get; set; }

        public Complex[] ComplexSamples { get; set; }

        public double? SampleSpacing { get; set; }

        public string SourcePath { get; set; }

        public string SampleColumnName { get; set; }

        public bool HasImaginaryComponent { get; set; }
    }
}
