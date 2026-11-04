using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Processing
{
    public sealed class ModeHopPreviewEngine
    {
        public ModeHopAnalysis Detect(ModeHopRecording recording, ModeHopSettings settings, CancellationToken token)
        {
            settings.Validate();
            var ranges = new List<ModeHopRange>();
            var start = -1;
            long excluded = 0;
            for (var i = 0; i < recording.SampleCount; i++)
            {
                if ((i & 0x3fff) == 0) token.ThrowIfCancellationRequested();
                if (recording.Trigger[i] > settings.ThresholdVoltage) { excluded++; if (start < 0) start = i; }
                else if (start >= 0) { AddRange(ranges, start, i, recording.SampleIntervalSeconds); start = -1; }
            }
            if (start >= 0) AddRange(ranges, start, recording.SampleCount, recording.SampleIntervalSeconds);
            return new ModeHopAnalysis { Ranges = ranges, ExcludedSamples = excluded, ExcludedSeconds = excluded * recording.SampleIntervalSeconds, ExcludedPercent = 100.0 * excluded / recording.SampleCount };
        }

        public ModeHopViewport Preview(ModeHopRecording recording, ModeHopAnalysis analysis, ModeHopSettings settings, double startSeconds, double endSeconds, CancellationToken token)
        {
            settings.Validate();
            if (!ModeHopSettings.IsFinite(startSeconds) || !ModeHopSettings.IsFinite(endSeconds) || startSeconds < 0 || endSeconds > recording.DurationSeconds || endSeconds <= startSeconds)
                throw new ArgumentException("표시 시간 범위가 올바르지 않습니다.");
            var first = Math.Max(0, (int)Math.Floor(startSeconds / recording.SampleIntervalSeconds));
            var end = (int)Math.Min(recording.SampleCount, Math.Ceiling(endSeconds / recording.SampleIntervalSeconds));
            var result = new ModeHopViewport
            {
                StartSeconds = startSeconds, EndSeconds = endSeconds, ThresholdVoltage = settings.ThresholdVoltage,
                Auxiliary = Reduce(recording.Auxiliary, first, end, recording.SampleIntervalSeconds, settings.MaximumPreviewPoints, token),
                Trigger = Reduce(recording.Trigger, first, end, recording.SampleIntervalSeconds, settings.MaximumPreviewPoints, token),
                Ranges = analysis.Ranges.Where(range => range.EndSeconds > startSeconds && range.StartSeconds < endSeconds).ToList(),
                SweepBoundaries = new List<SweepBoundary>()
            };
            if (settings.SweepDurationSeconds.HasValue)
            {
                var period = settings.SweepDurationSeconds.Value;
                if (period < recording.SampleIntervalSeconds) throw new ArgumentException("스윕 시간이 샘플 간격보다 짧습니다.");
                var firstBoundary = Math.Max(0, Math.Ceiling((startSeconds - settings.SweepOriginSeconds) / period));
                var lastBoundary = Math.Floor((endSeconds - settings.SweepOriginSeconds) / period);
                if (lastBoundary > long.MaxValue - 1) throw new ArgumentException("스윕 경계 수가 너무 많습니다.");
                var step = Math.Max(1, Math.Ceiling((lastBoundary - firstBoundary + 1) / 500));
                result.SweepMarkersDecimated = step > 1;
                for (var k = firstBoundary; k <= lastBoundary; k += step)
                    result.SweepBoundaries.Add(new SweepBoundary { Number = (long)k + 1, Seconds = settings.SweepOriginSeconds + k * period });
            }
            return result;
        }

        private static void AddRange(List<ModeHopRange> ranges, int start, int end, double dt)
        {
            ranges.Add(new ModeHopRange { Number = ranges.Count + 1, StartIndex = start, EndIndexExclusive = end, StartSeconds = start * dt, EndSeconds = end * dt });
        }

        private static TimedPreview Reduce(double[] source, int first, int end, double dt, int maximum, CancellationToken token)
        {
            var indices = new List<int>(maximum);
            var length = end - first;
            if (length <= maximum) for (var i = first; i < end; i++) indices.Add(i);
            else
            {
                var buckets = maximum / 2;
                for (var b = 0; b < buckets; b++)
                {
                    token.ThrowIfCancellationRequested();
                    var left = first + (int)((long)b * length / buckets);
                    var right = first + (int)((long)(b + 1) * length / buckets);
                    var min = left; var max = left;
                    for (var i = left + 1; i < right; i++)
                    {
                        if ((i & 0x3fff) == 0) token.ThrowIfCancellationRequested();
                        if (source[i] < source[min]) min = i;
                        if (source[i] > source[max]) max = i;
                    }
                    indices.Add(Math.Min(min, max));
                    if (min != max) indices.Add(Math.Max(min, max));
                }
            }
            token.ThrowIfCancellationRequested();
            return new TimedPreview { Seconds = indices.Select(index => index * dt).ToArray(), Values = indices.Select(index => source[index]).ToArray() };
        }
    }
}
