using System;
using System.Globalization;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Processing
{
    /// <summary>Maps a raw acquisition-time window to surviving paired samples. Ends are exclusive.</summary>
    public sealed class SweepTimeCrop
    {
        public int SourceStartIndex { get; private set; }
        public int SourceEndIndexExclusive { get; private set; }
        public int StitchedStartIndex { get; private set; }
        public int StitchedEndIndexExclusive { get; private set; }
        public int RetainedCount => StitchedEndIndexExclusive - StitchedStartIndex;

        public static SweepTimeCrop Create(SweepRange sweep, double sampleIntervalSeconds,
            int[] retainedSourceIndices, double startOffsetMilliseconds, double? lengthMilliseconds)
        {
            if (sweep == null || sweep.StartIndex < 0 || sweep.SampleCount <= 0
                || !ModeHopSettings.IsFinite(sampleIntervalSeconds) || sampleIntervalSeconds <= 0)
                throw new ArgumentException("스윕 범위와 샘플 간격을 확인하세요.");
            if (!ModeHopSettings.IsFinite(startOffsetMilliseconds) || startOffsetMilliseconds < 0
                || (lengthMilliseconds.HasValue && (!ModeHopSettings.IsFinite(lengthMilliseconds.Value) || lengthMilliseconds.Value <= 0)))
                throw new ArgumentException("시작 offset은 0 이상, 구간 길이는 0보다 큰 ms 값이어야 합니다. 길이를 비우면 스윕 끝까지 사용합니다.");
            var offsetSamples = startOffsetMilliseconds / 1000 / sampleIntervalSeconds;
            var lengthSamples = lengthMilliseconds.HasValue ? lengthMilliseconds.Value / 1000 / sampleIntervalSeconds : sweep.SampleCount - offsetSamples;
            if (offsetSamples >= sweep.SampleCount || lengthSamples > sweep.SampleCount - offsetSamples + 1e-7)
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                    "요청 범위가 선택 스윕을 벗어납니다. 사용 가능 길이 {0:G12} ms, offset 이후 {1:G12} ms입니다.",
                    sweep.SampleCount * sampleIntervalSeconds * 1000, Math.Max(0, sweep.SampleCount - offsetSamples) * sampleIntervalSeconds * 1000));
            // Offset and length are independently rounded to the nearest acquisition sample (half up).
            var first = sweep.StartIndex + (int)Math.Round(offsetSamples, MidpointRounding.AwayFromZero);
            var endValue = lengthMilliseconds.HasValue ? (long)first + (int)Math.Round(lengthSamples, MidpointRounding.AwayFromZero) : sweep.EndIndexExclusive;
            if (endValue > sweep.EndIndexExclusive) throw new ArgumentException("샘플 단위로 환산한 범위가 선택 스윕을 벗어납니다.");
            var end = (int)endValue;
            if (first >= end || end > sweep.EndIndexExclusive)
                throw new ArgumentException("샘플 단위로 환산한 범위가 비어 있거나 선택 스윕을 벗어납니다.");
            if (retainedSourceIndices == null || retainedSourceIndices.Length == 0)
                throw new ArgumentException("남은 샘플이 없습니다. 스윕 접합 / 검토를 먼저 실행하세요.");
            var result = new SweepTimeCrop { SourceStartIndex = first, SourceEndIndexExclusive = end,
                StitchedStartIndex = LowerBound(retainedSourceIndices, first), StitchedEndIndexExclusive = LowerBound(retainedSourceIndices, end) };
            if (result.RetainedCount < 4)
                throw new ArgumentException("지정한 원본 시간 범위에서 제외 후 남은 샘플이 4개 미만입니다.");
            return result;
        }

        private static int LowerBound(int[] values, int target)
        {
            var low = 0; var high = values.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (values[middle] < target) low = middle + 1; else high = middle;
            }
            return low;
        }
    }
}
