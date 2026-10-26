using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Services
{
    public sealed class OfdrDatasetScanner
    {
        private static readonly Regex FilePattern = new Regex("^(?<disp>\\d+_\\d+)mm(?<rep>\\d{4})CH(?<ch>[123])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly IsfWaveformReader reader;

        public OfdrDatasetScanner(IsfWaveformReader reader = null)
        {
            this.reader = reader ?? new IsfWaveformReader();
        }

        public OfdrDatasetManifest Scan(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) throw new DirectoryNotFoundException("ISF input folder was not found: " + folder);
            var groups = new Dictionary<string, Builder>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.GetFiles(folder, "*.isf", SearchOption.TopDirectoryOnly))
            {
                var match = FilePattern.Match(Path.GetFileNameWithoutExtension(path));
                if (!match.Success) continue;
                var setId = match.Groups["disp"].Value + "mm" + match.Groups["rep"].Value;
                Builder builder;
                if (!groups.TryGetValue(setId, out builder))
                {
                    builder = new Builder
                    {
                        Set = new OfdrChannelSet
                        {
                            SetId = setId,
                            DisplacementMm = double.Parse(match.Groups["disp"].Value.Replace('_', '.'), CultureInfo.InvariantCulture),
                            Repetition = match.Groups["rep"].Value
                        }
                    };
                    groups.Add(setId, builder);
                }
                builder.Add(int.Parse(match.Groups["ch"].Value, CultureInfo.InvariantCulture), path);
            }

            var sets = groups.Values.Select(value => Validate(value)).OrderBy(value => value.DisplacementMm).ThenBy(value => value.Repetition, StringComparer.Ordinal).ToList();
            return new OfdrDatasetManifest { InputRoot = Path.GetFullPath(folder), ScannedUtc = DateTime.UtcNow, Sets = sets };
        }

        private OfdrChannelSet Validate(Builder builder)
        {
            var set = builder.Set;
            var errors = new List<string>(builder.Errors);
            if (!set.HasChannel1) errors.Add("CH1 missing");
            if (!set.HasChannel2) errors.Add("CH2 missing");
            if (!set.HasChannel3) errors.Add("CH3 missing");
            if (errors.Count == 0)
            {
                try
                {
                    var headers = new[] { reader.ReadHeader(set.Channel1Path), reader.ReadHeader(set.Channel2Path), reader.ReadHeader(set.Channel3Path) };
                    var first = headers[0];
                    if (headers.Any(header => header.PointCount != first.PointCount)) errors.Add("channel point counts differ");
                    if (headers.Any(header =>
                        Math.Abs(header.XIncrement - first.XIncrement) > Math.Abs(first.XIncrement) * 1e-9
                        || Math.Abs(header.XZero - first.XZero) > Math.Abs(first.XIncrement) * 1e-6
                        || Math.Abs(header.PointOffset - first.PointOffset) > 1e-9))
                        errors.Add("channel time axes differ");
                    set.PointCount = first.PointCount;
                    set.SampleSpacingSeconds = first.XIncrement;
                }
                catch (Exception ex)
                {
                    errors.Add(ex.Message);
                }
            }
            set.IsValid = errors.Count == 0;
            set.ValidationMessage = set.IsValid ? "Ready" : string.Join("; ", errors);
            return set;
        }

        private sealed class Builder
        {
            public OfdrChannelSet Set { get; set; }
            public List<string> Errors { get; } = new List<string>();

            public void Add(int channel, string path)
            {
                string existing;
                switch (channel)
                {
                    case 1: existing = Set.Channel1Path; if (existing == null) Set.Channel1Path = path; break;
                    case 2: existing = Set.Channel2Path; if (existing == null) Set.Channel2Path = path; break;
                    default: existing = Set.Channel3Path; if (existing == null) Set.Channel3Path = path; break;
                }
                if (!string.IsNullOrWhiteSpace(existing)) Errors.Add("duplicate CH" + channel);
            }
        }
    }
}
