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
            token.ThrowIfCancellationRequested();
            var ranges = new List<ModeHopRange>();
            var start = -1;
            var lead = PaddingSamples(settings.ExclusionLeadSeconds, recording);
            var tail = PaddingSamples(settings.ExclusionTailSeconds, recording);
            long excluded = 0;
            for (var i = 0; i < recording.SampleCount; i++)
            {
                if ((i & 0x3fff) == 0) token.ThrowIfCancellationRequested();
                if (!settings.ModeHopExclusionBypassed && ModeHopSettings.IsFinite(recording.Trigger[i]) && recording.Trigger[i] > settings.ThresholdVoltage) { if (start < 0) start = i; }
                else if (start >= 0) { excluded += AddRange(ranges, Math.Max(0, start - lead), (int)Math.Min(recording.SampleCount, (long)i + tail), recording.SampleIntervalSeconds); start = -1; }
            }
            if (start >= 0) excluded += AddRange(ranges, Math.Max(0, start - lead), recording.SampleCount, recording.SampleIntervalSeconds);
            var invalid = new List<ModeHopRange>();
            foreach (var index in recording.InvalidSampleIndices ?? new int[0])
            {
                token.ThrowIfCancellationRequested(); AddRange(invalid, index, index + 1, recording.SampleIntervalSeconds);
            }
            if (invalid.Count > 0)
            {
                var union = new List<ModeHopRange>(); var left = 0; var right = 0; excluded = 0;
                while (left < ranges.Count || right < invalid.Count)
                {
                    token.ThrowIfCancellationRequested();
                    var next = right >= invalid.Count || (left < ranges.Count && ranges[left].StartIndex <= invalid[right].StartIndex) ? ranges[left++] : invalid[right++];
                    excluded += AddRange(union, next.StartIndex, next.EndIndexExclusive, recording.SampleIntervalSeconds);
                }
                ranges = union;
            }
            var measurementInvalid = new List<ModeHopRange>();
            foreach (var index in recording.InvalidMeasurementIndices ?? new int[0])
            {
                token.ThrowIfCancellationRequested(); AddRange(measurementInvalid, index, index + 1, recording.SampleIntervalSeconds);
            }
            return new ModeHopAnalysis { ModeHopExclusionBypassed = settings.ModeHopExclusionBypassed, ExclusionLeadSeconds = settings.ExclusionLeadSeconds, ExclusionTailSeconds = settings.ExclusionTailSeconds, IgnoredSampleRanges = invalid, IgnoredSamples = recording.InvalidSampleCount,
                IgnoredMeasurementRanges = measurementInvalid, IgnoredMeasurementSamples = recording.InvalidMeasurementCount,
                Ranges = ranges, ExcludedSamples = excluded, ExcludedSeconds = excluded * recording.SampleIntervalSeconds, ExcludedPercent = 100.0 * excluded / recording.SampleCount };
        }

        public ModeHopAnalysis ClipToSamples(ModeHopAnalysis analysis, int first, int end, double dt, CancellationToken token)
        {
            if (analysis == null || first < 0 || end <= first || !ModeHopSettings.IsFinite(dt) || dt <= 0) throw new ArgumentException("제외 구간의 선택 범위를 확인하세요.");
            token.ThrowIfCancellationRequested();
            var ranges = new List<ModeHopRange>(); long excluded = 0;
            foreach (var range in analysis.Ranges)
            {
                token.ThrowIfCancellationRequested();
                var left = Math.Max(first, range.StartIndex); var right = Math.Min(end, range.EndIndexExclusive);
                if (right > left) excluded += AddRange(ranges, left - first, right - first, dt);
            }
            var invalid = new List<ModeHopRange>(); var ignored = 0;
            foreach (var range in analysis.IgnoredMeasurementRanges ?? new List<ModeHopRange>())
            {
                token.ThrowIfCancellationRequested(); var left = Math.Max(first, range.StartIndex); var right = Math.Min(end, range.EndIndexExclusive);
                if (right > left) ignored += AddRange(invalid, left - first, right - first, dt);
            }
            var allInvalid = new List<ModeHopRange>(); var allIgnored = 0;
            foreach (var range in analysis.IgnoredSampleRanges ?? new List<ModeHopRange>())
            {
                token.ThrowIfCancellationRequested(); var left = Math.Max(first, range.StartIndex); var right = Math.Min(end, range.EndIndexExclusive);
                if (right > left) allIgnored += AddRange(allInvalid, left - first, right - first, dt);
            }
            return new ModeHopAnalysis { ModeHopExclusionBypassed = analysis.ModeHopExclusionBypassed, ExclusionLeadSeconds = analysis.ExclusionLeadSeconds, ExclusionTailSeconds = analysis.ExclusionTailSeconds, IgnoredSampleRanges = allInvalid, IgnoredSamples = allIgnored,
                IgnoredMeasurementRanges = invalid, IgnoredMeasurementSamples = ignored, Ranges = ranges, ExcludedSamples = excluded,
                ExcludedSeconds = excluded * dt, ExcludedPercent = 100.0 * excluded / (end - first) };
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
                Auxiliary = Reduce(recording.Auxiliary, first, end, recording.SampleIntervalSeconds, settings.MaximumPreviewPoints, token, true),
                Trigger = Reduce(recording.Trigger, first, end, recording.SampleIntervalSeconds, settings.MaximumPreviewPoints, token, true),
                SweepTrigger = recording.HasFourChannels ? Reduce(recording.SweepTrigger, first, end, recording.SampleIntervalSeconds, settings.MaximumPreviewPoints, token, true) : null,
                Measurement = recording.HasFourChannels ? Reduce(recording.Measurement, first, end, recording.SampleIntervalSeconds, settings.MaximumPreviewPoints, token, true) : null,
                Ranges = analysis.Ranges.Where(range => range.EndSeconds > startSeconds && range.StartSeconds < endSeconds).ToList(),
                SweepBoundaries = new List<SweepBoundary>()
            };
            if (recording.HasFourChannels)
            {
                var sweeps = new SweepPreparationEngine().DetectSweeps(recording, token);
                foreach (var sweep in sweeps)
                    if (sweep.StartSeconds >= startSeconds && sweep.StartSeconds <= endSeconds) result.SweepBoundaries.Add(new SweepBoundary { Number = sweep.Number, Seconds = sweep.StartSeconds });
                if (sweeps.Count > 0 && sweeps[sweeps.Count - 1].EndSeconds >= startSeconds && sweeps[sweeps.Count - 1].EndSeconds <= endSeconds)
                    result.SweepBoundaries.Add(new SweepBoundary { Number = sweeps.Count + 1, Seconds = sweeps[sweeps.Count - 1].EndSeconds });
            }
            else if (settings.SweepDurationSeconds.HasValue)
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

        private static int PaddingSamples(double seconds, ModeHopRecording recording)
        {
            var requested = Math.Min(recording.SampleCount, seconds / recording.SampleIntervalSeconds);
            // Treat integer sample counts within floating-point roundoff as exact (e.g. 0.1 ms / 0.4 us).
            var nearest = Math.Round(requested);
            if (Math.Abs(requested - nearest) <= 8 * 2.2204460492503131e-16 * Math.Max(1, requested)) requested = nearest;
            return seconds == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(requested));
        }

        private static int AddRange(List<ModeHopRange> ranges, int start, int end, double dt)
        {
            if (ranges.Count > 0 && ranges[ranges.Count - 1].EndIndexExclusive >= start)
            {
                var last = ranges[ranges.Count - 1]; var previousEnd = last.EndIndexExclusive;
                last.EndIndexExclusive = Math.Max(previousEnd, end); last.EndSeconds = last.EndIndexExclusive * dt;
                return last.EndIndexExclusive - previousEnd;
            }
            ranges.Add(new ModeHopRange { Number = ranges.Count + 1, StartIndex = start, EndIndexExclusive = end, StartSeconds = start * dt, EndSeconds = end * dt });
            return end - start;
        }

        public static TimedPreview Reduce(double[] source, int first, int end, double dt, int maximum, CancellationToken token, bool finiteOnly = false)
        {
            var indices = new List<int>(maximum);
            var length = end - first;
            if (length <= maximum) for (var i = first; i < end; i++) { if (!finiteOnly || ModeHopSettings.IsFinite(source[i])) indices.Add(i); }
            else
            {
                var buckets = maximum / 2;
                for (var b = 0; b < buckets; b++)
                {
                    token.ThrowIfCancellationRequested();
                    var left = first + (int)((long)b * length / buckets);
                    var right = first + (int)((long)(b + 1) * length / buckets);
                    var min = -1; var max = -1;
                    for (var i = left; i < right; i++)
                    {
                        if ((i & 0x3fff) == 0) token.ThrowIfCancellationRequested();
                        if (finiteOnly && !ModeHopSettings.IsFinite(source[i])) continue;
                        if (min < 0) { min = max = i; continue; }
                        if (source[i] < source[min]) min = i;
                        if (source[i] > source[max]) max = i;
                    }
                    if (min < 0) continue;
                    indices.Add(Math.Min(min, max));
                    if (min != max) indices.Add(Math.Max(min, max));
                }
            }
            token.ThrowIfCancellationRequested();
            return new TimedPreview { Seconds = indices.Select(index => index * dt).ToArray(), Values = indices.Select(index => source[index]).ToArray() };
        }
    }
}
