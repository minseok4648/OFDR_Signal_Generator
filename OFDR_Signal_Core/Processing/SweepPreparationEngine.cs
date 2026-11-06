using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Processing
{
    public sealed class SweepPreparationEngine
    {
        public DiagnosticPlotData MeasurementPreview(ModeHopRecording source, StitchedAuxiliarySignal stitched, CancellationToken token)
        {
            var n = stitched.Values.Length; var indices = new List<int>();
            for (var b = 0; b < Math.Min(5000, n); b++)
            {
                token.ThrowIfCancellationRequested();
                var left = (int)((long)b * n / Math.Min(5000, n)); var right = (int)((long)(b + 1) * n / Math.Min(5000, n));
                var min = left; var max = left;
                for (var i = left + 1; i < right; i++)
                {
                    var value = source.Measurement[stitched.OriginalIndices[i]];
                    if (value < source.Measurement[stitched.OriginalIndices[min]]) min = i;
                    if (value > source.Measurement[stitched.OriginalIndices[max]]) max = i;
                }
                indices.Add(Math.Min(min, max)); if (min != max) indices.Add(Math.Max(min, max));
            }
            var dt = stitched.SampleIntervalSeconds;
            return new DiagnosticPlotData { Title = "CH4 접합 측정 신호 · CH3와 동일한 제외/선택 인덱스", XAxis = "접합 환산 시간 (s)", YAxis = "V", XStart = 0, XEnd = stitched.DurationSeconds,
                Primary = new TimedPreview { Seconds = indices.Select(i => i * dt).ToArray(), Values = indices.Select(i => source.Measurement[stitched.OriginalIndices[i]]).ToArray() }, Markers = stitched.JoinIndices.Select(i => i * dt).ToArray() };
        }

        public List<SweepRange> DetectSweeps(ModeHopRecording source, CancellationToken token, bool useRecordingStart = false)
            => DetectSweeps(source, token, useRecordingStart ? SweepSelectionMode.RecordingStartToFirstEdge : SweepSelectionMode.CompleteSweeps);

        public List<SweepRange> DetectSweeps(ModeHopRecording source, CancellationToken token, SweepSelectionMode mode)
        {
            token.ThrowIfCancellationRequested();
            if (source == null || !source.HasFourChannels) throw new ArgumentException("CH1~4 입력이 필요합니다.");
            if (!Enum.IsDefined(typeof(SweepSelectionMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            if (mode == SweepSelectionMode.FirstEdgeToRecordingEnd && (source.IsPartialRead || source.FirstSampleIndex != 0))
                throw new ArgumentException("파일 끝을 지정하려면 일부 구간이 아닌 원본 파일 전체를 읽어야 합니다.");
            if (source.CompleteSweeps == null)
            {
                var edges = new List<int>();
                for (var i = 1; i < source.SampleCount; i++)
                {
                    if ((i & 0x3fff) == 0) token.ThrowIfCancellationRequested();
                    if (ModeHopSettings.IsRisingEdge(source.SweepTrigger[i - 1], source.SweepTrigger[i], 1.8)) edges.Add(i);
                }
                token.ThrowIfCancellationRequested();
                var detected = new List<SweepRange>();
                for (var i = 0; i + 1 < edges.Count; i++) detected.Add(new SweepRange { Number = i + 1, StartIndex = edges[i], EndIndexExclusive = edges[i + 1], StartSeconds = edges[i] * source.SampleIntervalSeconds, EndSeconds = edges[i + 1] * source.SampleIntervalSeconds });
                source.FirstSweepRisingIndex = edges.Count > 0 ? (int?)edges[0] : null;
                source.CompleteSweeps = detected;
            }
            if (mode == SweepSelectionMode.CompleteSweeps) return source.CompleteSweeps;
            if (!source.FirstSweepRisingIndex.HasValue) return new List<SweepRange>();
            if (mode == SweepSelectionMode.FirstEdgeToRecordingEnd)
                return new List<SweepRange> { new SweepRange { Number = 1, StartIndex = source.FirstSweepRisingIndex.Value,
                    EndIndexExclusive = source.SampleCount, StartSeconds = source.FirstSweepRisingIndex.Value * source.SampleIntervalSeconds,
                    EndSeconds = source.DurationSeconds, EndSpecifiedAtRecordEnd = true } };
            var sweeps = new List<SweepRange> { new SweepRange { Number = 1, StartIndex = 0, EndIndexExclusive = source.FirstSweepRisingIndex.Value,
                StartSeconds = 0, EndSeconds = source.FirstSweepRisingIndex.Value * source.SampleIntervalSeconds, StartSpecifiedAtRecordStart = true } };
            sweeps.AddRange(source.CompleteSweeps.Select(s => new SweepRange { Number = s.Number + 1, StartIndex = s.StartIndex,
                EndIndexExclusive = s.EndIndexExclusive, StartSeconds = s.StartSeconds, EndSeconds = s.EndSeconds }));
            return sweeps;
        }

        public ModeHopRecording Slice(ModeHopRecording source, SweepRange sweep, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (source == null || !source.HasFourChannels || sweep == null || sweep.StartIndex < 0 || sweep.EndIndexExclusive > source.SampleCount || sweep.SampleCount < 4) throw new ArgumentException("스윕 범위를 확인하세요.");
            var auxiliary = new double[sweep.SampleCount]; var trigger = new double[sweep.SampleCount];
            Array.Copy(source.Auxiliary, sweep.StartIndex, auxiliary, 0, auxiliary.Length);
            Array.Copy(source.Trigger, sweep.StartIndex, trigger, 0, trigger.Length);
            token.ThrowIfCancellationRequested();
            return new ModeHopRecording { SourcePath = source.SourcePath, Sha256 = source.Sha256, Header = source.Header, SourceStartSeconds = source.SourceStartSeconds,
                SampleIntervalSeconds = source.SampleIntervalSeconds, Auxiliary = auxiliary, Trigger = trigger, FirstSampleIndex = sweep.StartIndex, IsPartialRead = source.IsPartialRead,
                InvalidSampleIndices = source.InvalidSampleIndices?.Where(i => i >= sweep.StartIndex && i < sweep.EndIndexExclusive).Select(i => i - sweep.StartIndex).ToArray(),
                InvalidMeasurementIndices = source.InvalidMeasurementIndices?.Where(i => i >= sweep.StartIndex && i < sweep.EndIndexExclusive).Select(i => i - sweep.StartIndex).ToArray() };
        }

        public PreparedSweepData Prepare(ModeHopRecording source, SweepRange sweep, string role, StitchedAuxiliarySignal stitched,
            AuxiliaryQualityMetrics diagnosis, AuxiliaryInspectionSettings settings, double threshold, CancellationToken token)
        {
            if (source == null || !source.HasFourChannels || stitched == null || diagnosis == null || settings == null) throw new ArgumentException("선택 스윕의 위상 진단을 먼저 완료하세요.");
            var first = diagnosis.SelectedStartIndex; var end = diagnosis.SelectedEndIndexExclusive;
            if (first < 0 || end > stitched.Values.Length || end - first < 4) throw new ArgumentException("진단 구간이 올바르지 않습니다.");
            var result = new PreparedSweepData { Auxiliary = new double[end - first], Measurement = new double[end - first], OriginalIndices = new int[end - first] };
            for (var i = first; i < end; i++)
            {
                if ((i & 0x3fff) == 0) token.ThrowIfCancellationRequested();
                var original = stitched.OriginalIndices[i];
                if (original < sweep.StartIndex || original >= sweep.EndIndexExclusive) throw new ArgumentException("진단한 원본과 선택 스윕이 다릅니다.");
                result.Auxiliary[i - first] = stitched.Values[i]; result.Measurement[i - first] = source.Measurement[original]; result.OriginalIndices[i - first] = original;
            }
            var excluded = stitched.Exclusion ?? throw new ArgumentException("접합에 사용한 제외 구간이 없습니다.");
            result.Metadata = new PreparedSweepMetadata { Role = role, SourcePath = source.SourcePath, SourceSha256 = source.Sha256, SourceHeader = new Dictionary<string, string>(source.Header),
                SourceStartSeconds = source.SourceStartSeconds, SampleIntervalSeconds = source.SampleIntervalSeconds, ModeHopThresholdVoltage = threshold, Sweep = sweep,
                ModeHopExclusionTailSeconds = excluded.ExclusionTailSeconds,
                ModeHopExclusionLeadSeconds = excluded.ExclusionLeadSeconds,
                IgnoredSampleRanges = excluded.IgnoredSampleRanges?.Select(r => new ModeHopRange { Number = r.Number, StartIndex = sweep.StartIndex + r.StartIndex,
                    EndIndexExclusive = sweep.StartIndex + r.EndIndexExclusive, StartSeconds = sweep.StartSeconds + r.StartSeconds, EndSeconds = sweep.StartSeconds + r.EndSeconds }).ToList(),
                IgnoredMeasurementRanges = excluded.IgnoredMeasurementRanges?.Select(r => new ModeHopRange { Number = r.Number, StartIndex = sweep.StartIndex + r.StartIndex,
                    EndIndexExclusive = sweep.StartIndex + r.EndIndexExclusive, StartSeconds = sweep.StartSeconds + r.StartSeconds, EndSeconds = sweep.StartSeconds + r.EndSeconds }).ToList(),
                SelectedStartIndex = first, SelectedEndIndexExclusive = end, RowCount = end - first, Diagnosis = diagnosis, DiagnosisSettings = settings,
                ExcludedRanges = excluded.Ranges.Select(r => new ModeHopRange { Number = r.Number, StartIndex = sweep.StartIndex + r.StartIndex, EndIndexExclusive = sweep.StartIndex + r.EndIndexExclusive,
                    StartSeconds = sweep.StartSeconds + r.StartSeconds, EndSeconds = sweep.StartSeconds + r.EndSeconds }).ToList() };
            token.ThrowIfCancellationRequested(); result.Validate(); return result;
        }
    }
}
