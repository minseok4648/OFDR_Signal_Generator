using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.Serialization;
using System.Threading;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Services;

namespace OFDR_Signal_Core.Processing
{
    [DataContract]
    public sealed class ReflectogramSettings
    {
        [DataMember] public double ThresholdVolts { get; set; } = 1.8;
        [DataMember] public double TailMicroseconds { get; set; } = 100;
        [DataMember] public double MinimumRangeMilliseconds { get; set; } = 1;
        [DataMember] public double AuxiliaryDelayMicroseconds { get; set; } = .979;
        [DataMember] public double GroupIndex { get; set; } = 1.4682;
        [DataMember] public double WavelengthNm { get; set; } = 1550;
        [DataMember] public double SweepRateNmPerSecond { get; set; } = 50;
        [DataMember] public string Window { get; set; } = "Rectangular";
        [DataMember] public int ResampleCount { get; set; } = 65536;
        [DataMember] public int ZeroPadFactor { get; set; } = 4;
        [DataMember] public double GridMeters { get; set; } = .001;
        [DataMember] public double EndMeters { get; set; } = 36;
        [DataMember] public double PhaseQcLimit { get; set; } = 1e-5;
        [DataMember] public double BandpassLowMHz { get; set; } = 1;
        [DataMember] public double BandpassHighMHz { get; set; } = 12;
        [DataMember] public bool AutoLimitBandpass { get; set; } = true;
        [DataMember] public string Method { get; set; } = "Independent continuous ranges; equal linear-power mean; AUX requested band in config, actual band in each record (Nyquist auto cap if enabled); forward/backward filter, 2 us guard; no delay compensation. PDF script/window/weights unavailable.";
        public void Validate()
        {
            var positive = new[] { MinimumRangeMilliseconds, AuxiliaryDelayMicroseconds, GroupIndex, WavelengthNm, SweepRateNmPerSecond, GridMeters, EndMeters };
            if (positive.Any(x => !ModeHopSettings.IsFinite(x) || x <= 0) || !ModeHopSettings.IsFinite(ThresholdVolts)
                || !ModeHopSettings.IsFinite(BandpassLowMHz) || !ModeHopSettings.IsFinite(BandpassHighMHz) || BandpassLowMHz <= 0 || BandpassHighMHz <= BandpassLowMHz
                || !ModeHopSettings.IsFinite(TailMicroseconds) || TailMicroseconds < 0 || !ModeHopSettings.IsFinite(PhaseQcLimit) || PhaseQcLimit < 0
                || EndMeters < 36 || EndMeters / GridMeters > 200000 || ResampleCount < 1024 || ResampleCount > 1048576
                || ZeroPadFactor < 1 || ZeroPadFactor > 8 || (Window != "Rectangular" && Window != "Hann"))
                throw new ArgumentException("Reflectogram 설정 범위를 확인하세요 (거리 36 m 이상, grid 최대 200,001점).");
        }
    }

    [DataContract]
    public sealed class ReflectogramRangeMetric
    {
        [DataMember] public int StartIndex { get; set; }
        [DataMember] public int EndIndexExclusive { get; set; }
        [DataMember] public double PhaseSpanRadians { get; set; }
        [DataMember] public double RejectedAnchorRatio { get; set; }
        [DataMember] public bool PhaseQcPassed { get; set; }
        [DataMember] public double CorrectedNativeBinMeters { get; set; }
        [DataMember] public double NominalNativeBinMeters { get; set; }
    }

    [DataContract]
    public sealed class ReflectogramRecord
    {
        [DataMember] public string SourcePath { get; set; }
        [DataMember] public string Sha256 { get; set; }
        [DataMember] public int SampleCount { get; set; }
        [DataMember] public double SampleIntervalSeconds { get; set; }
        [DataMember] public int MaskedSampleCount { get; set; }
        [DataMember] public int ShortRangeSampleCount { get; set; }
        [DataMember] public List<ReflectogramRangeMetric> Ranges { get; set; } = new List<ReflectogramRangeMetric>();
        [DataMember] public double GridMeters { get; set; }
        [DataMember] public double AppliedBandpassLowMHz { get; set; }
        [DataMember] public double AppliedBandpassHighMHz { get; set; }
        [DataMember] public Dictionary<string, double> Metrics { get; set; }
        // Only aggregate arrays survive processing. They are exported separately, never bound as full UI arrays.
        public double[] NominalPower { get; set; }
        public double[] CorrectedPower { get; set; }
    }

    public sealed class ReflectogramEngine
    {
        public const double LightSpeed = 299792458;
        private readonly FftEngine fft = new FftEngine();
        public string ProviderDescription => fft.ProviderDescription;

        public ReflectogramRecord Run(string path, ReflectogramSettings settings, string directory, CancellationToken token, Action<string> progress = null)
        {
            settings.Validate(); token.ThrowIfCancellationRequested();
            if (!Environment.Is64BitProcess || !fft.NativeProviderAvailable) throw new InvalidOperationException("Reflectogram 전체 처리는 x64 native MKL이 필요합니다.");
            Directory.CreateDirectory(directory);
            progress?.Invoke("CSV 전체 읽기: " + Path.GetFileName(path));
            var raw = new TekScopeCsvReader().Read(path, token);
            if (raw.IsPartialRead || !raw.HasFourChannels) throw new ArgumentException("CH1~CH4가 포함된 전체 Tek CSV가 필요합니다.");
            JsonContractSerializer.Write(Path.Combine(directory, "header.json"), raw.Header);
            var record = new ReflectogramRecord { SourcePath = Path.GetFullPath(path), Sha256 = raw.Sha256, SampleCount = raw.SampleCount,
                SampleIntervalSeconds = raw.SampleIntervalSeconds, GridMeters = settings.GridMeters };
            JsonContractSerializer.Write(Path.Combine(directory, "manifest.json"), new Dictionary<string, string> {
                ["SourcePath"] = record.SourcePath, ["Sha256"] = record.Sha256, ["SampleCount"] = record.SampleCount.ToString(CultureInfo.InvariantCulture),
                ["SampleIntervalSeconds"] = record.SampleIntervalSeconds.ToString("R", CultureInfo.InvariantCulture),
                ["SourceStartSeconds"] = raw.SourceStartSeconds.ToString("R", CultureInfo.InvariantCulture),
                ["CsvFormat"] = raw.Header.TryGetValue("CSV Format", out var format) ? format : "CSV" });
            var preprocessing = new DocumentStageSettings { Enabled = true, BandpassEnabled = true, LowMHz = settings.BandpassLowMHz,
                HighMHz = settings.BandpassHighMHz, AutoLimitBandpass = settings.AutoLimitBandpass };
            record.AppliedBandpassLowMHz = preprocessing.LowMHz;
            record.AppliedBandpassHighMHz = preprocessing.ResolveHighMHz(raw.SampleIntervalSeconds);
            var mask = new ModeHopPreviewEngine().Detect(raw, new ModeHopSettings { ThresholdVoltage = settings.ThresholdVolts,
                ExclusionTailSeconds = settings.TailMicroseconds * 1e-6 }, token);
            JsonContractSerializer.Write(Path.Combine(directory, "mask.json"), mask);
            var ranges = new List<Tuple<int, int>>(); int cursor = 0;
            Action<int, int> accept = (start, end) => {
                if ((end - start) * raw.SampleIntervalSeconds >= settings.MinimumRangeMilliseconds * .001) ranges.Add(Tuple.Create(start, end));
                else record.ShortRangeSampleCount += end - start;
            };
            foreach (var range in mask.Ranges) { accept(cursor, range.StartIndex); record.MaskedSampleCount += range.EndIndexExclusive - range.StartIndex; cursor = range.EndIndexExclusive; }
            accept(cursor, raw.SampleCount);
            if (ranges.Count == 0) throw new InvalidOperationException("1 ms 이상의 정상 연속 구간이 없습니다. mask/최소 구간 길이를 확인하세요.");
            var size = (int)Math.Floor(settings.EndMeters / settings.GridMeters) + 1;
            record.NominalPower = new double[size]; record.CorrectedPower = new double[size];
            var stage = new StageComparisonEngine();
            foreach (var range in ranges)
            {
                token.ThrowIfCancellationRequested();
                progress?.Invoke(Path.GetFileName(path) + " · 정상 구간 " + (record.Ranges.Count + 1) + "/" + ranges.Count);
                var length = range.Item2 - range.Item1;
                var aux = new double[length]; var measurement = new double[length];
                Array.Copy(raw.Auxiliary, range.Item1, aux, 0, length); Array.Copy(raw.Measurement, range.Item1, measurement, 0, length);
                var pair = DocumentStagePreprocessor.Process(new OfdrChannelData { Values = aux, SampleIntervalSeconds = raw.SampleIntervalSeconds },
                    new OfdrChannelData { Values = measurement, SampleIntervalSeconds = raw.SampleIntervalSeconds },
                    preprocessing, true, 16, token);
                using (var plan = stage.Prepare(pair.Auxiliary, pair.Measurement, new StageComparisonSettings(), 16, token))
                {
                    var corrected = stage.Interpolate(plan, settings.ResampleCount, plan.Span, token);
                    var opticalStep = Math.Abs(plan.Span) / (2 * Math.PI * settings.AuxiliaryDelayMicroseconds * 1e-6) / (corrected.Length - 1);
                    double cBin, bBin;
                    var cPower = SpectrumPower(corrected, opticalStep, settings, out cBin);
                    var nominalStep = LightSpeed / Math.Pow(settings.WavelengthNm * 1e-9, 2) * settings.SweepRateNmPerSecond * 1e-9 * raw.SampleIntervalSeconds;
                    var bPower = SpectrumPower(pair.Measurement, nominalStep, settings, out bBin);
                    AddOnGrid(record.CorrectedPower, cPower, cBin, settings.GridMeters);
                    AddOnGrid(record.NominalPower, bPower, bBin, settings.GridMeters);
                    record.Ranges.Add(new ReflectogramRangeMetric { StartIndex = range.Item1, EndIndexExclusive = range.Item2,
                        PhaseSpanRadians = plan.Span, RejectedAnchorRatio = plan.Metrics.NonmonotonicRatio,
                        PhaseQcPassed = plan.Metrics.NonmonotonicRatio <= settings.PhaseQcLimit,
                        CorrectedNativeBinMeters = cBin * settings.ZeroPadFactor, NominalNativeBinMeters = bBin * settings.ZeroPadFactor });
                    JsonContractSerializer.Write(Path.Combine(directory, "ranges.json"), record.Ranges);
                }
            }
            for (int i = 0; i < size; i++) { record.NominalPower[i] /= ranges.Count; record.CorrectedPower[i] /= ranges.Count; }
            record.Metrics = new Dictionary<string, double>();
            Measure(record.NominalPower, record.GridMeters, "B", record.Metrics);
            Measure(record.CorrectedPower, record.GridMeters, "C", record.Metrics);
            record.Metrics["phase_qc_failed_ranges"] = record.Ranges.Count(r => !r.PhaseQcPassed);
            JsonContractSerializer.Write(Path.Combine(directory, "metrics.json"), record);
            using (var writer = new StreamWriter(Path.Combine(directory, "power_density.csv")))
            {
                writer.WriteLine("distance_m,B_V2_per_m,C_V2_per_m,B_dB_re_1V2_per_m,C_dB_re_1V2_per_m");
                for (var i = 0; i < size; i++) {
                    token.ThrowIfCancellationRequested();
                    writer.WriteLine(string.Join(",", new[] { i * record.GridMeters, record.NominalPower[i], record.CorrectedPower[i], Db(record.NominalPower[i]), Db(record.CorrectedPower[i]) }.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));
                }
            }
            return record;
        }

        public double[] SpectrumPower(double[] values, double opticalFrequencyStepHz, ReflectogramSettings settings, out double distanceBin)
        {
            if (values == null || values.Length < 4 || !ModeHopSettings.IsFinite(opticalFrequencyStepHz) || opticalFrequencyStepHz <= 0) throw new ArgumentException("유효한 신호와 광주파수 간격이 필요합니다.");
            settings.Validate();
            var nfft = checked(values.Length * settings.ZeroPadFactor);
            var spectrum = new Complex[nfft]; double energy = 0;
            for (int i = 0; i < values.Length; i++) {
                if (!ModeHopSettings.IsFinite(values[i])) throw new ArgumentException("신호에 비유한 값이 있습니다.");
                var w = settings.Window == "Hann" ? IndependentOfdrProcessingEngine.WindowValue("Hann", i, values.Length) : 1;
                spectrum[i] = values[i] * w; energy += w * w;
            }
            fft.ForwardInPlace(spectrum);
            distanceBin = LightSpeed / (2 * settings.GroupIndex * opticalFrequencyStepHz * nfft);
            var power = new double[nfft / 2 + 1];
            for (int i = 0; i < power.Length; i++) {
                var factor = i == 0 || (nfft % 2 == 0 && i == nfft / 2) ? 1 : 2;
                power[i] = factor * (spectrum[i].Real * spectrum[i].Real + spectrum[i].Imaginary * spectrum[i].Imaginary) / (nfft * energy * distanceBin);
            }
            return power;
        }

        public static void AddOnGrid(double[] sum, double[] power, double binMeters, double gridMeters)
        {
            if (sum == null || power == null || power.Length < 2 || !ModeHopSettings.IsFinite(binMeters) || binMeters <= 0
                || !ModeHopSettings.IsFinite(gridMeters) || gridMeters <= 0 || (sum.Length - 1) * gridMeters > (power.Length - 1) * binMeters)
                throw new ArgumentException("공통 거리 grid가 FFT의 Nyquist 범위를 벗어납니다. 보간 점수를 늘리세요.");
            for (int i = 0; i < sum.Length; i++) {
                var x = i * gridMeters / binMeters; var left = Math.Min(power.Length - 2, (int)x);
                sum[i] += power[left] + (power[left + 1] - power[left]) * (x - left);
            }
        }
        public static double Db(double power) => 10 * Math.Log10(Math.Max(1e-30, power));
        private static void Measure(double[] power, double step, string prefix, Dictionary<string, double> metrics)
        {
            var candidates = Enumerable.Range(0, power.Length).Where(i => i * step >= .5 && i * step <= 25).ToArray();
            var peak = candidates.OrderByDescending(i => power[i]).First();
            var noise = Enumerable.Range(0, power.Length).Where(i => i * step >= 26 && i * step <= 36).Select(i => Db(power[i])).OrderBy(v => v).ToArray();
            metrics[prefix + "_peak_m"] = peak * step;
            metrics[prefix + "_peak_dB"] = Db(power[peak]);
            metrics[prefix + "_noise_median_dB"] = noise[noise.Length / 2];
            metrics[prefix + "_noise_P95_P05_dB"] = noise[(int)(.95 * (noise.Length - 1))] - noise[(int)(.05 * (noise.Length - 1))];
            metrics[prefix + "_noise_max_min_dB"] = noise.Last() - noise.First();
            metrics[prefix + "_peak_above_noise_dB"] = Db(power[peak]) - noise[noise.Length / 2];
        }
    }
}
