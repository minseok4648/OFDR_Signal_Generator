using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Processing
{
    public static class DocumentStagePreprocessor
    {
        public sealed class Range
        {
            public int ArrayStart { get; set; }
            public int SourceStart { get; set; }
            public int Count { get; set; }
        }
        public sealed class Pair
        {
            public double[] Auxiliary, Measurement;
            public PreviewSeries RawPreview;
            public double SampleInterval;
            public string Description;
        }
        // Reconstruct continuous acquisition ranges from the saved exclusion/crop contract.
        public static List<Range> Ranges(OfdrChannelData channel)
        {
            var m = channel.Preparation;
            if (m == null) return new List<Range> { new Range { Count = channel.Values.Length } };
            if (m.Sweep == null || m.ExcludedRanges == null || m.RowCount != channel.Values.Length)
                throw new ArgumentException("정상 구간 provenance가 입력 배열과 다릅니다.");
            var ranges = new List<Range>(); var cursor = m.Sweep.StartIndex; var kept = 0;
            Action<int, int> add = (first, end) =>
            {
                var lo = Math.Max(kept, m.SelectedStartIndex); var hi = Math.Min(kept + end - first, m.SelectedEndIndexExclusive);
                if (hi > lo) ranges.Add(new Range { ArrayStart = lo - m.SelectedStartIndex, SourceStart = first + lo - kept, Count = hi - lo });
                kept += end - first;
            };
            foreach (var excluded in m.ExcludedRanges)
            {
                if (excluded.StartIndex < cursor || excluded.EndIndexExclusive > m.Sweep.EndIndexExclusive) throw new ArgumentException("제외 구간 provenance 오류입니다.");
                add(cursor, excluded.StartIndex); cursor = excluded.EndIndexExclusive;
            }
            add(cursor, m.Sweep.EndIndexExclusive);
            if (ranges.Sum(r => r.Count) != channel.Values.Length) throw new ArgumentException("원본 구간과 확정 crop의 길이가 다릅니다.");
            return ranges;
        }

        public static Pair Process(OfdrChannelData auxiliary, OfdrChannelData measurement, DocumentStageSettings settings, bool reference, int preview, CancellationToken token)
        {
            settings.Validate(); token.ThrowIfCancellationRequested();
            var dt = auxiliary.SampleIntervalSeconds;
            if (dt <= 0 || !ModeHopSettings.IsFinite(dt) || Math.Abs(measurement.SampleIntervalSeconds - dt) > dt * 1e-9
                || auxiliary.Values.Length != measurement.Values.Length) throw new ArgumentException("문서 단계에는 동일 취득 CH3/CH4와 헤더 sample interval이 필요합니다.");
            new FftEngine().EnsureSupportedLength(auxiliary.Values.Length);
            var ranges = Ranges(auxiliary); var measurementRanges = Ranges(measurement);
            if (ranges.Count != measurementRanges.Count || ranges.Where((r, i) => r.ArrayStart != measurementRanges[i].ArrayStart || r.SourceStart != measurementRanges[i].SourceStart || r.Count != measurementRanges[i].Count).Any())
                throw new ArgumentException("AUX/측정 정상 구간이 일치하지 않습니다.");
            var index = reference ? settings.ReferenceRangeIndex : settings.SignalRangeIndex;
            Range chosen;
            if (settings.SelectContinuousRange)
            {
                if (index == -1) index = ranges.Select((r, i) => new { r.Count, Index = i }).OrderByDescending(r => r.Count).First().Index;
                if (index >= ranges.Count) throw new ArgumentException("정상 구간 번호가 범위를 벗어났습니다. 사용 가능: 0~" + (ranges.Count - 1));
                chosen = ranges[index];
            }
            else
            {
                if (ranges.Count > 1 && (settings.BandpassEnabled || settings.DelayMode != StageDelayMode.None || settings.PhysicalWindow || settings.UseNominalGrid))
                    throw new ArgumentException("필터/시간 지연/물리축은 접합 경계를 가로지를 수 없습니다. 독립 정상 구간 선택을 켜세요.");
                chosen = new Range { SourceStart = ranges[0].SourceStart, Count = auxiliary.Values.Length };
            }
            if (chosen.Count < settings.MinimumRangeSamples) throw new ArgumentException("선택 정상 구간이 최소 샘플 수보다 짧습니다.");
            var a = new double[chosen.Count]; var m = new double[chosen.Count];
            Array.Copy(auxiliary.Values, chosen.ArrayStart, a, 0, a.Length); Array.Copy(measurement.Values, chosen.ArrayStart, m, 0, m.Length);
            for (var i = 0; i < a.Length; i++)
            {
                if ((i & 0x3ffff) == 0) token.ThrowIfCancellationRequested();
                if (!ModeHopSettings.IsFinite(a[i]) || !ModeHopSettings.IsFinite(m[i])) throw new ArgumentException("정상 구간에 비유한 채널 값이 있습니다.");
            }
            var raw = PreviewReducer.FromReal(a, preview, "선택 정상 구간 sample", "AUX raw (V)");
            var appliedHigh = settings.BandpassEnabled ? settings.ResolveHighMHz(dt) : settings.HighMHz;
            if (settings.BandpassEnabled) a = Bandpass(a, dt, settings.LowMHz * 1e6, appliedHigh * 1e6, token);
            var delay = settings.AppliedDelaySeconds / dt;
            var guardValue = Math.Ceiling(settings.EdgeGuardMicroseconds * 1e-6 / dt);
            if (guardValue > a.Length || Math.Abs(delay) > a.Length) throw new ArgumentException("지연/경계 제외가 정상 구간보다 큽니다.");
            var guard = (int)guardValue;
            var count = a.Length - 2 * guard - (int)Math.Ceiling(Math.Abs(delay));
            if (count < settings.MinimumRangeSamples) throw new ArgumentException("필터 guard/지연 적용 후 정상 구간이 너무 짧습니다.");
            var aa = new double[count]; var mm = new double[count];
            for (var i = 0; i < count; i++)
            {
                if ((i & 0x3ffff) == 0) token.ThrowIfCancellationRequested();
                aa[i] = Sample(a, guard + i + Math.Max(0, delay)); mm[i] = Sample(m, guard + i + Math.Max(0, -delay));
            }
            return new Pair { Auxiliary = aa, Measurement = mm, RawPreview = raw, SampleInterval = dt,
                Description = string.Format(CultureInfo.InvariantCulture, "{0}: 구간 {1}/{2} 원본 [{3},{4}) → {5} samples · Fs={6:G8}MS/s · AUX advance={7:G8} samples ({8:G8}us) · guard={9} samples/끝 · {10} · BPF {11} {12:G8}–{13:G8}MHz (요청 상한 {14:G8}, 자동 {15})",
                    reference ? "Ref" : "Signal", index, ranges.Count, chosen.SourceStart, chosen.SourceStart + chosen.Count, count, 1e-6 / dt, delay, delay * dt * 1e6, guard,
                    auxiliary.Preparation?.ModeHopExclusionBypassed == true ? "CH2 제외 OFF: 구간 내 홉 가능" : auxiliary.Preparation == null ? "mask provenance 없음: 입력 전체를 연속으로 가정" : "저장된 mask/crop 적용",
                    settings.BandpassEnabled ? "ON" : "OFF", settings.LowMHz, appliedHigh, settings.HighMHz, settings.AutoLimitBandpass) };
        }

        private static double Sample(double[] x, double position)
        {
            var i = (int)Math.Floor(position); var f = position - i;
            if (i < 0 || i >= x.Length || (f > 1e-12 && i + 1 >= x.Length)) throw new ArgumentException("지연 보간이 정상 구간 밖으로 나갑니다.");
            return f < 1e-12 ? x[i] : x[i] + f * (x[i + 1] - x[i]);
        }

        // Explicit design: one 2nd-order Butterworth HP and LP, forward/backward,
        // reflected boundaries. This is a selectable diagnostic design, not an inferred PDF filter.
        public static double[] Bandpass(double[] input, double dt, double low, double high, CancellationToken token)
        {
            if (dt <= 0 || low <= 0 || high <= low || high >= .5 / dt) throw new ArgumentException("AUX 대역은 0 < 하한 < 상한 < Fs/2여야 합니다.");
            var pad = (int)Math.Min(input.Length - 1, Math.Max(12, Math.Ceiling(4 / (low * dt))));
            var x = new double[input.Length + 2 * pad];
            Array.Copy(input, 0, x, pad, input.Length);
            for (var i = 0; i < pad; i++) { x[pad - 1 - i] = input[i + 1]; x[pad + input.Length + i] = input[input.Length - 2 - i]; }
            Filter(x, dt, low, true, token); Filter(x, dt, high, false, token);
            Array.Reverse(x); Filter(x, dt, low, true, token); Filter(x, dt, high, false, token); Array.Reverse(x);
            var result = new double[input.Length]; Array.Copy(x, pad, result, 0, result.Length); return result;
        }
        private static void Filter(double[] x, double dt, double frequency, bool highpass, CancellationToken token)
        {
            var omega = 2 * Math.PI * frequency * dt; var cos = Math.Cos(omega); var alpha = Math.Sin(omega) / Math.Sqrt(2);
            var a0 = 1 + alpha; var b0 = (highpass ? 1 + cos : 1 - cos) / (2 * a0);
            var b1 = (highpass ? -(1 + cos) : 1 - cos) / a0; var b2 = b0; var a1 = -2 * cos / a0; var a2 = (1 - alpha) / a0;
            double z1 = 0, z2 = 0;
            for (var i = 0; i < x.Length; i++)
            {
                if ((i & 0x3ffff) == 0) token.ThrowIfCancellationRequested();
                var value = x[i]; var y = b0 * value + z1; z1 = b1 * value - a1 * y + z2; z2 = b2 * value - a2 * y; x[i] = y;
            }
        }
    }
}
