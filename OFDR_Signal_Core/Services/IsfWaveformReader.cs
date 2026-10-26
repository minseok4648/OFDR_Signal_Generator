using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Services
{
    public sealed class IsfWaveformReader
    {
        private static readonly byte[] CurveMarker = Encoding.ASCII.GetBytes(":CURVE #");
        private const int MaximumPreambleBytes = 1024 * 1024;

        public IsfWaveformHeader ReadHeader(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("ISF path is required.", nameof(path));
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
            {
                var probeLength = (int)Math.Min(stream.Length, MaximumPreambleBytes);
                var probe = new byte[probeLength];
                ReadExactly(stream, probe, 0, probe.Length);
                var markerIndex = IndexOf(probe, CurveMarker);
                if (markerIndex < 0) throw new InvalidDataException("ISF does not contain a :CURVE binary block marker.");

                var countDigitIndex = markerIndex + CurveMarker.Length;
                if (countDigitIndex >= probe.Length || probe[countDigitIndex] < (byte)'1' || probe[countDigitIndex] > (byte)'9')
                    throw new InvalidDataException("ISF CURVE block length digit is invalid.");

                var lengthDigitCount = probe[countDigitIndex] - (byte)'0';
                var lengthStart = countDigitIndex + 1;
                if (lengthStart + lengthDigitCount > probe.Length) throw new InvalidDataException("ISF CURVE block length is truncated.");
                long payloadLength;
                if (!long.TryParse(Encoding.ASCII.GetString(probe, lengthStart, lengthDigitCount), NumberStyles.None, CultureInfo.InvariantCulture, out payloadLength))
                    throw new InvalidDataException("ISF CURVE payload length is invalid.");

                var preamble = Encoding.ASCII.GetString(probe, 0, markerIndex);
                var fields = ParsePreamble(preamble);
                var header = new IsfWaveformHeader
                {
                    PointCount = GetInt(fields, "NR_PT"),
                    BytesPerSample = GetInt(fields, "BYT_NR"),
                    BitCount = GetInt(fields, "BIT_NR"),
                    Encoding = GetString(fields, "ENCDG"),
                    BinaryFormat = GetString(fields, "BN_FMT"),
                    ByteOrder = GetString(fields, "BYT_OR"),
                    WaveformId = GetString(fields, "WFID"),
                    XUnit = GetString(fields, "XUNIT"),
                    XIncrement = GetDouble(fields, "XINCR"),
                    XZero = GetDouble(fields, "XZERO"),
                    PointOffset = GetDouble(fields, "PT_OFF"),
                    YUnit = GetString(fields, "YUNIT"),
                    YMultiplier = GetDouble(fields, "YMULT"),
                    YOffset = GetDouble(fields, "YOFF"),
                    YZero = GetDouble(fields, "YZERO"),
                    PayloadOffset = lengthStart + lengthDigitCount,
                    PayloadLength = payloadLength,
                    RawPreamble = preamble
                };
                Validate(header, stream.Length);
                return header;
            }
        }

        public double[] ReadAllVoltages(string path, CancellationToken cancellationToken = default(CancellationToken), IProgress<OperationProgress> progress = null)
        {
            using (var reader = OpenSampleStream(path))
            {
                var output = new double[reader.Header.PointCount];
                const int blockSize = 131072;
                var completed = 0;
                while (completed < output.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(blockSize, output.Length - completed);
                    var read = reader.ReadSamples(output, completed, count);
                    if (read != count) throw new EndOfStreamException("ISF payload ended before NR_PT samples were read.");
                    completed += read;
                    if (progress != null && (completed == output.Length || completed % (blockSize * 8) == 0))
                        progress.Report(new OperationProgress { Stage = "Read ISF", Detail = Path.GetFileName(path), Completed = completed, Total = output.Length, Percent = 100.0 * completed / output.Length });
                }
                return output;
            }
        }

        public IsfSampleStream OpenSampleStream(string path)
        {
            return new IsfSampleStream(path, ReadHeader(path));
        }

        private static void Validate(IsfWaveformHeader header, long fileLength)
        {
            if (header.PointCount <= 0) throw new InvalidDataException("ISF NR_PT must be positive.");
            if (header.BytesPerSample != 2 || header.BitCount != 16) throw new NotSupportedException("Only 16-bit, 2-byte ISF samples are supported.");
            if (!string.Equals(header.Encoding, "BINARY", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Only BINARY ISF encoding is supported.");
            if (!string.Equals(header.BinaryFormat, "RI", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Only signed integer (RI) ISF samples are supported.");
            if (!string.Equals(header.ByteOrder, "MSB", StringComparison.OrdinalIgnoreCase) && !string.Equals(header.ByteOrder, "LSB", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("ISF byte order must be MSB or LSB.");
            var expected = checked((long)header.PointCount * header.BytesPerSample);
            if (header.PayloadLength != expected) throw new InvalidDataException("ISF payload length does not equal NR_PT * BYT_NR.");
            if (header.PayloadOffset + header.PayloadLength > fileLength) throw new InvalidDataException("ISF payload extends beyond the file length.");
            if (header.XIncrement <= 0.0 || header.YMultiplier == 0.0) throw new InvalidDataException("ISF axis scale is invalid.");
        }

        private static Dictionary<string, string> ParsePreamble(string preamble)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in preamble.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = raw.Trim().TrimStart(':');
                var separator = token.IndexOf(' ');
                if (separator <= 0) continue;
                var key = token.Substring(0, separator);
                var colon = key.LastIndexOf(':');
                if (colon >= 0) key = key.Substring(colon + 1);
                result[key] = token.Substring(separator + 1).Trim();
            }
            return result;
        }

        private static string GetString(IDictionary<string, string> fields, string key)
        {
            string value;
            if (!fields.TryGetValue(key, out value)) throw new InvalidDataException("ISF preamble is missing " + key + ".");
            return value;
        }

        private static int GetInt(IDictionary<string, string> fields, string key)
        {
            int value;
            if (!int.TryParse(GetString(fields, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) throw new InvalidDataException("ISF " + key + " is not an integer.");
            return value;
        }

        private static double GetDouble(IDictionary<string, string> fields, string key)
        {
            double value;
            if (!double.TryParse(GetString(fields, key), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) throw new InvalidDataException("ISF " + key + " is not numeric.");
            return value;
        }

        private static int IndexOf(byte[] data, byte[] pattern)
        {
            for (var i = 0; i <= data.Length - pattern.Length; i++)
            {
                var matched = true;
                for (var j = 0; j < pattern.Length; j++) if (data[i + j] != pattern[j]) { matched = false; break; }
                if (matched) return i;
            }
            return -1;
        }

        internal static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var read = stream.Read(buffer, offset, count);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
                count -= read;
            }
        }
    }

    public sealed class IsfSampleStream : IDisposable
    {
        private readonly FileStream stream;
        private readonly byte[] byteBuffer;

        internal IsfSampleStream(string path, IsfWaveformHeader header)
        {
            Header = header;
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            stream.Position = header.PayloadOffset;
            byteBuffer = new byte[1024 * 1024];
        }

        public IsfWaveformHeader Header { get; private set; }
        public int SamplesRead { get; private set; }

        public int ReadSamples(double[] destination, int offset, int count)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (offset < 0 || count < 0 || offset + count > destination.Length) throw new ArgumentOutOfRangeException();
            count = Math.Min(count, Header.PointCount - SamplesRead);
            var completed = 0;
            while (completed < count)
            {
                var samplesThisBlock = Math.Min(count - completed, byteBuffer.Length / 2);
                var bytes = samplesThisBlock * 2;
                IsfWaveformReader.ReadExactly(stream, byteBuffer, 0, bytes);
                var msb = string.Equals(Header.ByteOrder, "MSB", StringComparison.OrdinalIgnoreCase);
                for (var i = 0; i < samplesThisBlock; i++)
                {
                    var first = byteBuffer[i * 2];
                    var second = byteBuffer[(i * 2) + 1];
                    var raw = msb ? (short)((first << 8) | second) : (short)((second << 8) | first);
                    destination[offset + completed + i] = Header.GetVoltage(raw);
                }
                completed += samplesThisBlock;
            }
            SamplesRead += completed;
            return completed;
        }

        public void Dispose()
        {
            stream.Dispose();
        }
    }
}
