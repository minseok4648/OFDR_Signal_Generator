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
    public sealed class AcquisitionDiagnosticReader
    {
        public const int MaximumSamples = 250000;
        private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

        public AcquisitionDiagnosticSource ReadIsf(string path, int[] starts, int length, CancellationToken token)
        {
            Budget(starts.Length, length);
            var source = Begin(path); var header = new IsfWaveformReader().ReadHeader(path);
            source.Header = new Dictionary<string, string> { ["Preamble"] = header.RawPreamble };
            source.SampleIntervalSeconds = header.XIncrement; source.DeclaredSamples = header.PointCount;
            using (var stream = Open(path))
                foreach (var start in starts)
                {
                    token.ThrowIfCancellationRequested();
                    if (start < 0 || start > header.PointCount - length) throw new ArgumentOutOfRangeException(nameof(starts));
                    var bytes = new byte[length * 2]; stream.Position = header.PayloadOffset + start * 2L;
                    var read = 0; while (read < bytes.Length) { token.ThrowIfCancellationRequested(); var n = stream.Read(bytes, read, bytes.Length - read); if (n == 0) throw new EndOfStreamException(); read += n; }
                    var values = new double[length]; var msb = header.ByteOrder == "MSB";
                    for (var i = 0; i < length; i++) values[i] = header.GetVoltage(msb ? (short)((bytes[2 * i] << 8) | bytes[2 * i + 1]) : (short)((bytes[2 * i + 1] << 8) | bytes[2 * i]));
                    source.Blocks.Add(new AcquisitionDiagnosticBlock { SourcePath = path, StartIndex = start, SampleIntervalSeconds = header.XIncrement, Auxiliary = values,
                        SourceIndices = Enumerable.Range(start, length).ToArray(), Trigger = new double[length] });
                    source.SourceRanges.Add(new[] { start, start + length }); source.NumericSamples += length;
                }
            Finish(source, token); return source;
        }

        // CH2 is scanned to locate windows around rising edges; CH3/CH4 are parsed only within the budget.
        // The previous pulse end is retained so a tail entering a window is not lost.
        public AcquisitionDiagnosticSource ReadCsv(string path, int first, int end, int windowCount, int length, CancellationToken token)
        {
            Budget(windowCount, length);
            if (first < 0 || end <= first || end - first < length) throw new ArgumentOutOfRangeException(nameof(end));
            var source = Begin(path); source.Header = new Dictionary<string, string>();
            var beforeCount = length / 10; var pending = new Queue<Row>(); var selected = new List<Row>();
            var targets = Enumerable.Range(0, windowCount).Select(i => first + (int)((long)i * (end - first - length) / windowCount)).ToArray();
            var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var row = 0; var previousHigh = false; var lastEnd = int.MinValue; var active = false; var data = false;
            using (var stream = Open(path)) using (var reader = new StreamReader(stream))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if ((row & 8191) == 0) token.ThrowIfCancellationRequested();
                    if (!data)
                    {
                        if (line.TrimStart('\uFEFF').StartsWith("TIME,", StringComparison.OrdinalIgnoreCase))
                        {
                            var names = line.TrimStart('\uFEFF').Split(',');
                            for (var i = 0; i < names.Length; i++) columns.Add(names[i].Trim(), i);
                            foreach (var name in new[] { "TIME", "CH1", "CH2", "CH3", "CH4" }) if (!columns.ContainsKey(name)) throw new InvalidDataException("Missing " + name);
                            source.SampleIntervalSeconds = Number(source.Header["Sample Interval"]);
                            source.DeclaredSamples = checked((int)Number(source.Header["Record Length"]));
                            if (source.SampleIntervalSeconds <= 0 || end > source.DeclaredSamples) throw new InvalidDataException("Invalid interval/selected record range.");
                            data = true;
                        }
                        else { var split = line.IndexOf(','); if (split > 0) source.Header[line.Substring(0, split)] = line.Substring(split + 1).TrimEnd(','); }
                        continue;
                    }
                    if (row >= end) break;
                    var cells = line.Split(',');
                    if (cells.Length != columns.Count) throw new InvalidDataException("Column count at sample " + row);
                    var trigger = TekScopeCsvReader.ChannelValue(cells[columns["CH2"]], row + 1);
                    var high = ModeHopSettings.IsFinite(trigger) && trigger > 1.8;
                    if (previousHigh && !high) lastEnd = row;
                    var entry = new Row { Index = row, Cells = cells, PreviousHighEnd = lastEnd };
                    if (!active && source.Blocks.Count < windowCount && high && !previousHigh && row >= targets[source.Blocks.Count]
                        && pending.Count == beforeCount && pending.Peek().Index >= first && row - beforeCount + length <= end)
                    {
                        selected.AddRange(pending); active = true;
                    }
                    if (active)
                    {
                        selected.Add(entry);
                        if (selected.Count == length)
                        {
                            source.Blocks.Add(ParseBlock(path, selected, columns, source.SampleIntervalSeconds));
                            source.SourceRanges.Add(new[] { selected[0].Index, row + 1 }); source.NumericSamples += length;
                            selected.Clear(); active = false;
                        }
                    }
                    pending.Enqueue(entry); if (pending.Count > beforeCount) pending.Dequeue();
                    previousHigh = high; row++;
                    if (source.Blocks.Count == windowCount) break;
                }
            }
            source.TriggerRowsScanned = row;
            if (!data || source.Blocks.Count != windowCount) throw new InvalidDataException("Selected scope did not contain the requested complete pulse-centred windows.");
            Finish(source, token); return source;
        }

        // Explicit partial-record windows: no complete sweep or mode-hop edge is required.
        // Outside these ranges rows are skipped as text, not interpreted as signal samples.
        public AcquisitionDiagnosticSource ReadCsvRanges(string path, int[] starts, int length, CancellationToken token)
        {
            if (starts == null) throw new ArgumentNullException(nameof(starts));
            Budget(starts.Length, length); token.ThrowIfCancellationRequested();
            for (var i = 0; i < starts.Length; i++)
                if (starts[i] < 0 || (long)starts[i] + length > int.MaxValue || (i > 0 && starts[i] < (long)starts[i - 1] + length))
                    throw new ArgumentException("Ranges must be ordered, non-overlapping and nonnegative.");
            var source = Begin(path); source.Header = new Dictionary<string, string>();
            source.Scope = "Explicit bounded CH1..4 windows only; skipped rows not numerically parsed; original first TIME metadata and full-file SHA-256 retained. No full-sweep/row-count validation or pre-window pulse-tail inference.";
            var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var selected = new List<Row>(length); var row = 0; var data = false;
            using (var stream = Open(path)) using (var reader = new StreamReader(stream))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if ((row & 8191) == 0) token.ThrowIfCancellationRequested();
                    if (!data)
                    {
                        if (line.TrimStart('\uFEFF').StartsWith("TIME,", StringComparison.OrdinalIgnoreCase))
                        {
                            var names = line.TrimStart('\uFEFF').Split(',');
                            for (var i = 0; i < names.Length; i++) columns.Add(names[i].Trim(), i);
                            foreach (var name in new[] { "TIME", "CH1", "CH2", "CH3", "CH4" }) if (!columns.ContainsKey(name)) throw new InvalidDataException("Missing " + name);
                            source.SampleIntervalSeconds = Number(source.Header["Sample Interval"]);
                            var declared = Number(source.Header["Record Length"]);
                            source.DeclaredSamples = checked((int)declared);
                            if (declared != source.DeclaredSamples || source.SampleIntervalSeconds <= 0 || starts.Last() + length > source.DeclaredSamples)
                                throw new InvalidDataException("Invalid interval/selected record range.");
                            data = true;
                        }
                        else { var split = line.IndexOf(','); if (split > 0) source.Header[line.Substring(0, split)] = line.Substring(split + 1).TrimEnd(','); }
                        continue;
                    }
                    if (row == 0) source.Header["Diagnostic original first TIME (s)"] = Number(line.Split(',')[columns["TIME"]]).ToString("G17", Culture);
                    var start = starts[source.Blocks.Count];
                    if (row >= start)
                    {
                        var cells = line.Split(',');
                        if (cells.Length != columns.Count) throw new InvalidDataException("Column count at sample " + row);
                        selected.Add(new Row { Index = row, Cells = cells, PreviousHighEnd = int.MinValue });
                        if (selected.Count == length)
                        {
                            source.Blocks.Add(ParseBlock(path, selected, columns, source.SampleIntervalSeconds));
                            source.SourceRanges.Add(new[] { start, row + 1 }); source.NumericSamples += length;
                            selected.Clear();
                            if (source.Blocks.Count == starts.Length) break;
                        }
                    }
                    row++;
                }
            }
            source.TriggerRowsScanned = source.NumericSamples;
            if (!data || source.Blocks.Count != starts.Length) throw new InvalidDataException("Incomplete requested partial-record windows.");
            Finish(source, token); return source;
        }

        private static AcquisitionDiagnosticBlock ParseBlock(string path, List<Row> rows, Dictionary<string, int> columns, double dt)
        {
            var block = new AcquisitionDiagnosticBlock { SourcePath = path, StartIndex = rows[0].Index, PreviousHighEndIndex = rows[0].PreviousHighEnd,
                SampleIntervalSeconds = dt, Auxiliary = new double[rows.Count], Measurement = new double[rows.Count], Trigger = new double[rows.Count], SweepTrigger = new double[rows.Count], SourceIndices = new int[rows.Count] };
            for (var i = 0; i < rows.Count; i++)
            {
                var c = rows[i].Cells; Number(c[columns["TIME"]]);
                block.SweepTrigger[i] = TekScopeCsvReader.ChannelValue(c[columns["CH1"]], rows[i].Index + 1);
                block.Auxiliary[i] = TekScopeCsvReader.ChannelValue(c[columns["CH3"]], rows[i].Index + 1);
                block.Trigger[i] = TekScopeCsvReader.ChannelValue(c[columns["CH2"]], rows[i].Index + 1);
                block.Measurement[i] = TekScopeCsvReader.ChannelValue(c[columns["CH4"]], rows[i].Index + 1);
                block.SourceIndices[i] = rows[i].Index;
            }
            return block;
        }

        private static void Budget(int count, int length)
        {
            if (count < 1 || length < 1024 || (long)count * length > MaximumSamples) throw new ArgumentException("Diagnostic sample budget must be 1..250000, with at least 1024 samples per window.");
        }
        private static double Number(string text)
        {
            double value;
            if (!double.TryParse(text, NumberStyles.Float, Culture, out value) || !ModeHopSettings.IsFinite(value)) throw new InvalidDataException("Invalid finite numeric value: " + text);
            return value;
        }
        private static FileStream Open(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
        private static AcquisitionDiagnosticSource Begin(string path)
        {
            var info = new FileInfo(path); return new AcquisitionDiagnosticSource { Path = System.IO.Path.GetFullPath(path), Bytes = info.Length, LastWriteUtc = info.LastWriteTimeUtc };
        }
        private static void Finish(AcquisitionDiagnosticSource source, CancellationToken token)
        {
            using (var input = Open(source.Path)) using (var hash = SHA256.Create())
            {
                var bytes = new byte[65536]; int n;
                while ((n = input.Read(bytes, 0, bytes.Length)) != 0) { token.ThrowIfCancellationRequested(); hash.TransformBlock(bytes, 0, n, bytes, 0); }
                hash.TransformFinalBlock(new byte[0], 0, 0); source.Sha256 = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
            }
            var info = new FileInfo(source.Path);
            if (info.Length != source.Bytes || info.LastWriteTimeUtc != source.LastWriteUtc) throw new IOException("Source changed during diagnostic read/hash.");
        }
        private sealed class Row { public int Index; public string[] Cells; public int PreviousHighEnd; }
    }
}
