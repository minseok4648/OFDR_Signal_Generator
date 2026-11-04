using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Processing
{
    public sealed class AuxiliaryQualityEngine
    {
        public StitchedAuxiliarySignal Stitch(ModeHopRecording source, ModeHopAnalysis exclusion, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var remaining = checked(source.SampleCount - (int)exclusion.ExcludedSamples);
            if (remaining < 4) throw new InvalidOperationException("제외 후 남은 샘플이 4개 미만입니다.");
            var values = new double[remaining]; var indices = new int[remaining]; var joins = new List<int>();
            var cursor = 0; var output = 0;
            foreach (var range in exclusion.Ranges)
            {
                if (range.StartIndex < cursor || range.EndIndexExclusive <= range.StartIndex || range.EndIndexExclusive > source.SampleCount) throw new ArgumentException("제외 구간 인덱스가 올바르지 않습니다.");
                CopyKept(source, cursor, range.StartIndex, values, indices, joins, ref output, token);
                cursor = range.EndIndexExclusive;
            }
            CopyKept(source, cursor, source.SampleCount, values, indices, joins, ref output, token);
            if (output != remaining) throw new ArgumentException("제외 샘플 수와 구간 목록이 일치하지 않습니다.");
            return new StitchedAuxiliarySignal { Values = values, OriginalIndices = indices, JoinIndices = joins.ToArray(), SampleIntervalSeconds = source.SampleIntervalSeconds };
        }

        private static void CopyKept(ModeHopRecording source, int first, int end, double[] values, int[] indices, List<int> joins, ref int output, CancellationToken token)
        {
            if (end <= first) return;
            if (output > 0 && indices[output - 1] + 1 != source.FirstSampleIndex + first) joins.Add(output);
            for (var i = first; i < end; i++)
            {
                if ((i & 0x3fff) == 0) token.ThrowIfCancellationRequested();
                if (output >= values.Length) throw new ArgumentException("제외 샘플 수가 올바르지 않습니다.");
                values[output] = source.Auxiliary[i]; indices[output++] = source.FirstSampleIndex + i;
            }
        }

        public DiagnosticPlotData SignalPreview(StitchedAuxiliarySignal signal, int maximum, CancellationToken token)
        {
            return new DiagnosticPlotData { Title = "제외 후 직접 접합한 보조간섭계 · 노란 선: 접합점", XAxis = "접합 후 환산 시간 (s; 제거 시간이 압축됨)", YAxis = "Voltage (V)", XStart = 0, XEnd = signal.DurationSeconds,
                Primary = Reduce(signal.Values.Length, i => signal.Values[i], 0, signal.SampleIntervalSeconds, maximum, token), Markers = signal.JoinIndices.Select(i => i * signal.SampleIntervalSeconds).ToArray() };
        }

        public AuxiliaryQualityResult Analyze(StitchedAuxiliarySignal signal, AuxiliaryInspectionSettings settings, CancellationToken token)
        {
            settings.Validate(); token.ThrowIfCancellationRequested();
            var endSeconds = settings.EndSeconds ?? signal.DurationSeconds;
            if (endSeconds > signal.DurationSeconds || settings.StartSeconds >= signal.DurationSeconds) throw new ArgumentException("분석 범위가 접합 신호를 벗어났습니다.");
            // Half-open sample selection by sample times, with roundoff tolerance at exact boundaries.
            var first = (int)Math.Ceiling(settings.StartSeconds / signal.SampleIntervalSeconds - 1e-9);
            var end = Math.Min(signal.Values.Length, (int)Math.Ceiling(endSeconds / signal.SampleIntervalSeconds - 1e-9));
            var n = end - first;
            if (n < 4) throw new ArgumentException("분석 구간에는 최소 4개 샘플이 필요합니다.");
            var selected = new double[n]; Array.Copy(signal.Values, first, selected, 0, n);
            var fft = new FftEngine(); fft.EnsureSupportedLength(n);
            var phase = AuxiliaryPhaseExtractor.Extract(fft, selected, 0, token, keepAnalytic: true);
            var metrics = new AuxiliaryQualityMetrics { Provider = fft.ProviderDescription, SelectedStartIndex = first, SelectedEndIndexExclusive = end, SelectedStartSeconds = first * signal.SampleIntervalSeconds, SelectedEndSeconds = end * signal.SampleIntervalSeconds,
                SourceStartIndex = signal.OriginalIndices[first], SourceEndIndexInclusive = signal.OriginalIndices[end - 1], Phase = phase.Metrics };
            var failure = phase.Failure(settings.MaximumNonmonotonicRatio);
            metrics.Decision = failure == null ? "PASS_PHASE_QC" : "REJECT_PHASE_QC";
            metrics.Reason = failure == null ? "선택 구간이 기존 위상 단조성/span QC를 통과했습니다. 접합점과 재추출 위상 잔차를 함께 확인하세요." : "선택 구간이 기존 위상 QC 한계를 벗어났습니다. " + failure.Replace("CH1", "Auxiliary");
            var joins = signal.JoinIndices.Where(i => i > first && i < end).Select(i => i - first).ToArray();
            metrics.JoinCount = joins.Length;
            foreach (var join in joins)
            {
                metrics.MaximumJoinVoltageStep = Math.Max(metrics.MaximumJoinVoltageStep, Math.Abs(selected[join] - selected[join - 1]));
                metrics.MaximumJoinPhaseStep = Math.Max(metrics.MaximumJoinPhaseStep, Math.Abs(phase.Phase[join] - phase.Phase[join - 1]));
            }
            var plots = new List<DiagnosticPlotData>();
            var maximum = settings.MaximumPreviewPoints;
            Func<string, string, Func<int, double>, DiagnosticPlotData> plot = (title, unit, selector) => new DiagnosticPlotData { Title = title, XAxis = "선택 구간 샘플 index", YAxis = unit, XStart = 0, XEnd = n - 1, Primary = Reduce(n, selector, 0, 1, maximum, token), Markers = joins.Select(i => (double)i).ToArray() };
            plots.Add(plot("선택한 접합 신호", "V", i => selected[i]));
            plots.Add(plot("Hilbert analytic magnitude", "V", i => phase.Analytic[i].Magnitude));
            plots.Add(plot("Wrapped phase", "rad", i => Math.Atan2(phase.Analytic[i].Imaginary, phase.Analytic[i].Real)));
            var gridStart = phase.Phase[0]; var span = phase.Metrics.PhaseSpanRadians;
            metrics.UniformGridStepRadians = span / (n - 1);
            var unwrapped = plot("추출 위상(파랑) / 생성한 균일 Grid(주황)", "rad", i => phase.Phase[i]);
            unwrapped.Secondary = Reduce(n, i => gridStart + span * i / (n - 1.0), 0, 1, maximum, token);
            unwrapped.SecondaryLabel = "생성 Grid: 직선 자체는 품질 판정 아님"; plots.Add(unwrapped);
            plots.Add(new DiagnosticPlotData { Title = "추출 위상 증가량 · 접합점 포함", XAxis = "선택 구간 샘플 index", YAxis = "rad/sample", XStart = 0, XEnd = n - 1,
                Primary = Reduce(n - 1, i => phase.Phase[i + 1] - phase.Phase[i], 1, 1, maximum, token), Markers = joins.Select(i => (double)i).ToArray() });
            var mean = phase.Phase.Average(); var center = (n - 1) / 2.0; double xx = 0, xy = 0, yy = 0;
            for (var i = 0; i < n; i++) { var x = i - center; var y = phase.Phase[i] - mean; xx += x * x; xy += x * y; yy += y * y; }
            var slope = xy / xx; double residualSquares = 0;
            for (var i = 0; i < n; i++) { var residual = phase.Phase[i] - (mean + slope * (i - center)); residualSquares += residual * residual; }
            metrics.PhaseFitResidualRmsRadians = Math.Sqrt(residualSquares / n); metrics.PhaseFitR2 = yy > 0 ? 1 - residualSquares / yy : 0;
            phase.Analytic = null;
            if (phase.Metrics.AcceptedAnchorCount >= 2 && span > 0)
            {
                var corrected = new double[n]; var anchorCursor = 0; var accepted = phase.Metrics.AcceptedAnchorCount;
                for (var i = 0; i < n; i++)
                {
                    if ((i & 0x3fff) == 0) token.ThrowIfCancellationRequested();
                    var target = gridStart + span * i / (n - 1.0);
                    while (anchorCursor + 1 < accepted && phase.Phase[phase.Anchors[anchorCursor + 1]] < target) anchorCursor++;
                    var left = phase.Anchors[Math.Min(anchorCursor, accepted - 2)]; var right = phase.Anchors[Math.Min(anchorCursor + 1, accepted - 1)];
                    var denominator = phase.Phase[right] - phase.Phase[left]; var fraction = denominator <= 0 ? 0 : (target - phase.Phase[left]) / denominator;
                    corrected[i] = selected[left] + (selected[right] - selected[left]) * fraction;
                }
                var correctedPlot = plot("균일 위상 Grid로 보간한 보조간섭계 (검토용)", "V", i => corrected[i]); correctedPlot.Markers = null; plots.Add(correctedPlot);
                var checkedPhase = AuxiliaryPhaseExtractor.Extract(fft, corrected, 0, token);
                metrics.ResampledPhase = checkedPhase.Metrics;
                var errorOffset = checkedPhase.Phase.Select((value, i) => value - (gridStart + span * i / (n - 1.0))).Average();
                Func<int, double> error = i => checkedPhase.Phase[i] - (gridStart + span * i / (n - 1.0)) - errorOffset;
                double errorSquares = 0;
                for (var i = 0; i < n; i++) { var value = error(i); errorSquares += value * value; }
                metrics.ResampledPhaseResidualRmsRadians = Math.Sqrt(errorSquares / n);
                var checkedPlot = plot("보간 신호에서 다시 추출한 위상 / 생성 Grid", "rad", i => checkedPhase.Phase[i] - errorOffset);
                checkedPlot.Secondary = unwrapped.Secondary; checkedPlot.SecondaryLabel = "균일 위상 Grid"; checkedPlot.Markers = null; plots.Add(checkedPlot);
                var errorPlot = plot("재추출 위상 − Grid (상수 offset만 제거)", "rad", error); errorPlot.Markers = null; plots.Add(errorPlot);
            }
            token.ThrowIfCancellationRequested();
            return new AuxiliaryQualityResult { Metrics = metrics, Plots = plots };
        }

        public static TimedPreview Reduce(int length, Func<int, double> selector, double origin, double step, int maximum, CancellationToken token)
        {
            if (maximum < 4 || maximum > 10000) throw new ArgumentException("Preview limit must be 4..10000");
            var indices = new List<int>();
            var buckets = length <= maximum ? length : maximum / 2;
            for (var b = 0; b < buckets; b++)
            {
                token.ThrowIfCancellationRequested();
                var first = (int)((long)b * length / buckets); var end = (int)((long)(b + 1) * length / buckets);
                var min = first; var max = first;
                for (var i = first + 1; i < end; i++) { if ((i & 0x3fff) == 0) token.ThrowIfCancellationRequested(); if (selector(i) < selector(min)) min = i; if (selector(i) > selector(max)) max = i; }
                indices.Add(Math.Min(min, max)); if (max != min) indices.Add(Math.Max(min, max));
            }
            return new TimedPreview { Seconds = indices.Select(i => origin + i * step).ToArray(), Values = indices.Select(selector).ToArray() };
        }
    }
}
