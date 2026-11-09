using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Services;

namespace OFDR_Signal_Core.Processing
{
    // Explicit diagnostic branch. Neither production defaults nor historical notebook code is replaced.
    public sealed class StageComparisonEngine
    {
        private readonly FftEngine fft = new FftEngine();
        public string ProviderDescription => fft.ProviderDescription;
        public sealed class PhasePlan : IDisposable
        {
            internal double[] Phase, Measurement;
            internal int[] Order;
            internal int Count, MeasurementOffset;
            internal string Key;
            public double First { get; internal set; }
            public double Span { get; internal set; }
            public double AdjacentReverseRatio { get; internal set; }
            public double SortedTimeReverseRatio { get; internal set; }
            public KLinearizationMetrics Metrics { get; internal set; }
            public KLinearizationDiagnostics Diagnostics { get; internal set; }
            public void Dispose() { Phase = null; Order = null; Measurement = null; }
        }

        public PhasePlan Prepare(double[] auxiliary, double[] measurement, StageComparisonSettings s, int preview, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (auxiliary == null || measurement == null || auxiliary.Length != measurement.Length || s.AuxiliaryOffset < 0 || s.MeasurementOffset < 0)
                throw new ArgumentException("각 sweep의 AUX/측정 길이와 offset을 확인하세요.");
            long used = (long)auxiliary.Length - s.AuxiliaryOffset - s.MeasurementOffset;
            if (used < 4) throw new ArgumentException("offset 적용 후 샘플이 부족합니다.");
            var n = (int)used; fft.EnsureSupportedLength(n);
            double mean = 0;
            for (var i = 0; i < n; i++)
            {
                if ((i & 0x3ffff) == 0) token.ThrowIfCancellationRequested();
                var a = auxiliary[i + s.AuxiliaryOffset]; var m = measurement[i + s.MeasurementOffset];
                if (!ModeHopSettings.IsFinite(a) || !ModeHopSettings.IsFinite(m)) throw new ArgumentException("단계 비교 입력에 비유한 값이 있습니다.");
                if (s.RemoveAuxiliaryMean) mean += a;
            }
            mean /= n;
            var analytic = new Complex[n];
            for (var i = 0; i < n; i++) analytic[i] = new Complex(auxiliary[i + s.AuxiliaryOffset] - mean, 0);
            fft.ForwardInPlace(analytic);
            for (var i = 1; i < n; i++)
                if (i < (n + 1) / 2) analytic[i] *= 2;
                else if (i > n / 2 || n % 2 != 0) analytic[i] = Complex.Zero;
            fft.InverseInPlace(analytic); token.ThrowIfCancellationRequested();
            var phase = new double[n]; var previous = Math.Atan2(analytic[0].Imaginary, analytic[0].Real); phase[0] = previous;
            double offset = 0;
            for (var i = 1; i < n; i++)
            {
                if ((i & 0x3ffff) == 0) token.ThrowIfCancellationRequested();
                var wrapped = Math.Atan2(analytic[i].Imaginary, analytic[i].Real); var delta = wrapped - previous;
                if (delta > Math.PI) offset -= 2 * Math.PI;
                else if (delta < -Math.PI) offset += 2 * Math.PI;
                phase[i] = wrapped + offset; previous = wrapped;
            }
            var direction = phase[n - 1] >= phase[0] ? 1 : -1;
            if (s.PhaseOrder == AuxiliaryPhaseOrder.IncreasingAnchors && direction < 0)
                for (var i = 0; i < n; i++) phase[i] = -phase[i];
            var reversed = 0;
            for (var i = 1; i < n; i++) if (phase[i] <= phase[i - 1]) reversed++;
            var order = new int[n]; order[0] = 0; var accepted = 1;
            if (s.PhaseOrder == AuxiliaryPhaseOrder.IncreasingAnchors)
            {
                for (var i = 1; i < n; i++)
                { if ((i & 0x3ffff) == 0) token.ThrowIfCancellationRequested(); if (phase[i] > phase[order[accepted - 1]]) order[accepted++] = i; }
            }
            else
            {
                for (var i = 0; i < n; i++) order[i] = i;
                Array.Sort(order, Comparer<int>.Create((a, b) => { var cmp = phase[a].CompareTo(phase[b]); return cmp != 0 ? cmp : a.CompareTo(b); }));
                accepted = n;
            }
            token.ThrowIfCancellationRequested();
            var first = phase[0]; var last = s.PhaseOrder == AuxiliaryPhaseOrder.IncreasingAnchors ? phase[order[accepted - 1]] : phase[n - 1];
            if (accepted < 2 || Math.Abs(last - first) < 2 * Math.PI) throw new ArgumentException("위상 범위/anchor가 부족합니다.");
            var timeReversed = 0;
            for (var i = 1; i < accepted; i++) if (order[i] < order[i - 1]) timeReversed++;
            var diagnostics = new KLinearizationDiagnostics
            {
                AuxiliaryRaw = PreviewReducer.FromSelector(n, preview, i => auxiliary[i + s.AuxiliaryOffset], "Selected sample", "V"),
                AuxiliaryMeanRemoved = PreviewReducer.FromSelector(n, preview, i => auxiliary[i + s.AuxiliaryOffset] - mean, "Selected sample", s.RemoveAuxiliaryMean ? "AUX DC removed (V)" : "AUX DC retained (V)"),
                AnalyticMagnitude = PreviewReducer.FromMagnitude(analytic, preview, "Selected sample", "Magnitude"),
                WrappedPhase = PreviewReducer.FromSelector(n, preview, i => Math.Atan2(analytic[i].Imaginary, analytic[i].Real), "Selected sample", "rad"),
                UnwrappedPhase = PreviewReducer.FromReal(phase, preview, "Selected sample", "rad"),
                PhaseIncrement = PreviewReducer.FromSelector(n - 1, preview, i => phase[i + 1] - phase[i], "Selected sample", "rad/sample")
            };
            return new PhasePlan { Phase = phase, Order = order, Count = accepted, Measurement = measurement, MeasurementOffset = s.MeasurementOffset,
                Key = s.PhaseKey, First = first, Span = last - first, Diagnostics = diagnostics,
                AdjacentReverseRatio = reversed / (double)(n - 1), SortedTimeReverseRatio = timeReversed / (double)(accepted - 1),
                Metrics = new KLinearizationMetrics { SampleCount = n, AcceptedAnchorCount = accepted, PhaseSpanRadians = last - first,
                    SweepDirection = direction, NonmonotonicRatio = (s.PhaseOrder == AuxiliaryPhaseOrder.IncreasingAnchors ? n - accepted : reversed) / (double)(n - 1), DiagnosticOnly = true } };
        }

        public double[] Interpolate(PhasePlan p, int count, double span, CancellationToken token)
        {
            if (count < 2 || !ModeHopSettings.IsFinite(span) || span == 0 || Math.Sign(span) != Math.Sign(p.Span) || Math.Abs(span) > Math.Abs(p.Span) + 1e-8)
                throw new ArgumentException("보간 grid가 해당 sweep의 상대 위상 범위를 벗어납니다.");
            var values = new double[count]; var step = span / (count - 1);
            for (var i = 0; i < count; i++)
            {
                if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
                var target = i == count - 1 ? p.First + span : p.First + i * step;
                var low = 0; var high = p.Count;
                while (low < high) { var mid = low + (high - low) / 2; if (p.Phase[p.Order[mid]] <= target) low = mid + 1; else high = mid; }
                var left = Math.Max(0, Math.Min(p.Count - 2, low - 1)); var a = p.Order[left]; var b = p.Order[left + 1];
                var pa = p.Phase[a]; var pb = p.Phase[b];
                values[i] = target == pb || pb == pa ? p.Measurement[b + p.MeasurementOffset] : p.Measurement[a + p.MeasurementOffset]
                    + (target - pa) * ((p.Measurement[b + p.MeasurementOffset] - p.Measurement[a + p.MeasurementOffset]) / (pb - pa));
            }
            return values;
        }

        public IndependentOfdrProcessingResult Run(IndependentOfdrInput input, IndependentOfdrProcessingConfig config, CancellationToken token, IProgress<OperationProgress> progress = null)
        {
            config.StageComparison.Validate(config);
            using (var r = Prepare(input.Auxiliary.Values, input.Reference.Values, config.StageComparison, config.MaxPreviewPoints, token))
            using (var s = Prepare(input.SignalAuxiliary.Values, input.Signal.Values, config.StageComparison, config.MaxPreviewPoints, token))
                return RunPrepared(input, config, r, s, token, progress);
        }

        public IndependentOfdrProcessingResult RunPrepared(IndependentOfdrInput input, IndependentOfdrProcessingConfig config, PhasePlan r, PhasePlan s,
            CancellationToken token, IProgress<OperationProgress> progress = null, bool includeSelfAndRawFft = true)
        {
            config = JsonContractSerializer.Clone(config); var options = config.StageComparison; options.Validate(config);
            if (r.Key != options.PhaseKey || s.Key != options.PhaseKey) throw new ArgumentException("위상 계획과 선택 설정이 다릅니다.");
            var watch = Stopwatch.StartNew();
            var rs = r.Span; var ss = s.Span;
            if (options.Grid != RelativePhaseGrid.IndependentEndpoints)
            {
                if (Math.Sign(rs) != Math.Sign(ss)) throw new ArgumentException("반대 방향 sweep의 공통 위상 grid는 지원하지 않습니다.");
                rs = ss = Math.Sign(rs) * Math.Min(Math.Abs(rs), Math.Abs(ss));
            }
            progress?.Report(new OperationProgress { Stage = "단계별 비교", Detail = options.Description, Percent = 20 });
            var rv = options.Grid == RelativePhaseGrid.CommonSpanTwoPass ? InterpolateTwoPass(r, options.OutputSamples, rs, token) : Interpolate(r, options.OutputSamples, rs, token);
            var sv = options.Grid == RelativePhaseGrid.CommonSpanTwoPass ? InterpolateTwoPass(s, options.OutputSamples, ss, token) : Interpolate(s, options.OutputSamples, ss, token);
            var rd = JsonContractSerializer.Clone(r.Diagnostics); var sd = JsonContractSerializer.Clone(s.Diagnostics);
            rd.UniformPhaseGrid = PreviewReducer.FromSelector(rv.Length, config.MaxPreviewPoints, i => rs * i / (rv.Length - 1), "Resampled index", "Relative phase (rad)");
            sd.UniformPhaseGrid = PreviewReducer.FromSelector(sv.Length, config.MaxPreviewPoints, i => ss * i / (sv.Length - 1), "Resampled index", "Relative phase (rad)");
            var rf = Spectrum(rv, config, token); var sf = Spectrum(sv, config, token);
            var profile = Compare(rf, sf, config, token, progress);
            var valid = Enumerable.Range(0, profile.Shifts.Length).Where(i => ModeHopSettings.IsFinite(profile.Shifts[i])).ToArray();
            if (valid.Length == 0) throw new ArgumentException("유효한 segment가 없습니다.");
            var rm = JsonContractSerializer.Clone(r.Metrics); var sm = JsonContractSerializer.Clone(s.Metrics);
            foreach (var m in new[] { rm, sm })
            { m.PhaseQcPassed = m.NonmonotonicRatio <= config.MaxAuxNonmonotonicRatio; m.PhaseQcFailureReason = "단계 진단: 선택한 위상 정책 기준. 정렬은 역행 품질을 교정하지 않습니다."; }
            var result = new IndependentOfdrProcessingResult
            {
                AuxiliaryId = input.Auxiliary.MeasurementId, SignalAuxiliaryId = input.SignalAuxiliary.MeasurementId, ReferenceId = input.Reference.MeasurementId,
                SignalId = input.Signal.MeasurementId, Polarization = "CH4 / 단계별 lag 진단", SampleCount = options.OutputSamples, Config = config,
                FftProvider = fft.ProviderDescription, KLinearization = rm, SignalKLinearization = sm, KDiagnostics = rd, SignalKDiagnostics = sd,
                ReferenceRaw = PreviewReducer.FromReal(input.Reference.Values, config.MaxPreviewPoints, "Confirmed raw sample", "V"),
                SignalRaw = PreviewReducer.FromReal(input.Signal.Values, config.MaxPreviewPoints, "Confirmed raw sample", "V"),
                WindowedReference = WindowPreview(rv, config), WindowedSignal = WindowPreview(sv, config),
                ReferenceSpectrumValues = rf, SignalSpectrumValues = sf, ReferenceSpectrum = Db(rf, config), SignalSpectrum = Db(sf, config),
                IsDiagnosticAnalysis = true, InputPhaseQcPassed = rm.PhaseQcPassed == true && sm.PhaseQcPassed == true,
                PhaseQcSummary = "단계별 진단 · " + options.Description + string.Format(" · 위상 거부 Ref {0:P3} / Sig {1:P3} · 미교정", rm.NonmonotonicRatio, sm.NonmonotonicRatio),
                NotebookProfile = profile, NotebookReferenceProfile = includeSelfAndRawFft ? Compare(rf, rf, config, token) : null,
                SegmentStarts = valid.Select(i => profile.Positions[i] - config.SegmentSize / 2).ToArray(),
                SegmentPeakLags = valid.Select(i => profile.Shifts[i]).ToArray(), SegmentPeakIndices = valid.Select(i => (int)Math.Round(profile.Shifts[i] + config.SegmentSize * config.SegmentIfftZeroPadFactor / 2.0)).ToArray(),
                SegmentPeakMagnitudes = new double[valid.Length], PhysicalAxis = new PhysicalAxisCalibrationResult { Enabled = false,
                    LocalSpectrumLength = config.SegmentSize * config.SegmentIfftZeroPadFactor, Note = "단계별 비교: profile 순번 / 상관 lag. 절대 거리·GHz·strain으로 해석하지 않습니다. " + options.Description },
                PeakLagProfile = new PreviewSeries { Values = valid.Select(i => profile.Shifts[i]).ToArray(), XStart = valid.First(), XEnd = valid.Last(), XAxisTitle = "프로파일 순번", YAxisTitle = "상관 lag (sample)" }
            };
            result.SpectralShiftProfile = result.PeakLagProfile;
            if (includeSelfAndRawFft) { result.PreKReferenceSpectrum = RawDb(input.Reference.Values, config, token); result.PreKSignalSpectrum = RawDb(input.Signal.Values, config, token); }
            result.SelectedSegment = Inspect(result, Math.Max(0, Math.Min(valid.Length - 1, 2750 - valid[0])));
            result.PerformanceMetrics.Add(new NamedMetric { Name = "stage_processing_seconds_excluding_phase", Value = watch.Elapsed.TotalSeconds, Unit = "s" });
            AddMetric(result, "reference_grid_span", rs); AddMetric(result, "signal_grid_span", ss);
            AddMetric(result, "reference_adjacent_reverse_ratio", r.AdjacentReverseRatio); AddMetric(result, "signal_adjacent_reverse_ratio", s.AdjacentReverseRatio);
            AddMetric(result, "reference_sorted_time_reverse_ratio", r.SortedTimeReverseRatio); AddMetric(result, "signal_sorted_time_reverse_ratio", s.SortedTimeReverseRatio);
            return result;
        }

        private static void AddMetric(IndependentOfdrProcessingResult r, string name, double value) => r.PerformanceMetrics.Add(new NamedMetric { Name = name, Value = value, Unit = "diagnostic" });
        private double[] InterpolateTwoPass(PhasePlan p, int count, double span, CancellationToken token)
        {
            var firstPass = Interpolate(p, p.Metrics.SampleCount, p.Span, token);
            if (count == firstPass.Length && Math.Abs(p.Span - span) <= Math.Max(1e-12, Math.Abs(p.Span) * 1e-12)) return firstPass;
            var output = new double[count];
            for (var i = 0; i < count; i++)
            {
                if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
                var position = (firstPass.Length - 1.0) * (span / p.Span) * i / (count - 1.0);
                var left = Math.Max(0, Math.Min(firstPass.Length - 1, (int)Math.Floor(position))); var right = Math.Min(firstPass.Length - 1, left + 1);
                output[i] = firstPass[left] + (firstPass[right] - firstPass[left]) * Math.Max(0, Math.Min(1, position - left));
            }
            return output;
        }
        private static bool Global(StageComparisonSettings s) => s.WindowPlacement == DiagnosticWindowPlacement.GlobalSignal || s.WindowPlacement == DiagnosticWindowPlacement.Both;
        private static bool Local(StageComparisonSettings s) => s.WindowPlacement == DiagnosticWindowPlacement.DistanceSegment || s.WindowPlacement == DiagnosticWindowPlacement.Both;
        private static double Mean(double[] x) { double m = 0; foreach (var v in x) m += v; return m / x.Length; }
        public Complex[] Spectrum(double[] values, IndependentOfdrProcessingConfig c, CancellationToken token)
        {
            var a = new Complex[values.Length]; var mean = c.StageComparison.RemoveMeasurementMean ? Mean(values) : 0;
            for (var i = 0; i < a.Length; i++)
            { if ((i & 0x3ffff) == 0) token.ThrowIfCancellationRequested(); a[i] = new Complex((values[i] - mean) * (Global(c.StageComparison) ? IndependentOfdrProcessingEngine.WindowValue(c.WindowFunction, i, a.Length) : 1), 0); }
            fft.ForwardInPlace(a); token.ThrowIfCancellationRequested(); Array.Resize(ref a, a.Length / 2); return a;
        }
        private PreviewSeries WindowPreview(double[] x, IndependentOfdrProcessingConfig c)
        {
            var mean = c.StageComparison.RemoveMeasurementMean ? Mean(x) : 0;
            return PreviewReducer.FromSelector(x.Length, c.MaxPreviewPoints, i => (x[i] - mean) * (Global(c.StageComparison) ? IndependentOfdrProcessingEngine.WindowValue(c.WindowFunction, i, x.Length) : 1), "k sample", "Before global FFT (V)");
        }
        private PreviewSeries Db(Complex[] x, IndependentOfdrProcessingConfig c)
        {
            var max = Math.Max(1e-300, x.Max(v => v.Magnitude));
            return PreviewReducer.FromSelector(x.Length, c.MaxPreviewPoints, i => Math.Max(c.FftDbFloor, 20 * Math.Log10(Math.Max(1e-300, x[i].Magnitude) / max)), "FFT bin", "dB (normalized)");
        }
        private PreviewSeries RawDb(double[] x, IndependentOfdrProcessingConfig c, CancellationToken token)
        { token.ThrowIfCancellationRequested(); var a = x.Select(v => new Complex(v, 0)).ToArray(); fft.ForwardInPlace(a); Array.Resize(ref a, a.Length / 2); return Db(a, c); }

        public NotebookTemperatureProfile Compare(Complex[] reference, Complex[] signal, IndependentOfdrProcessingConfig c, CancellationToken token, IProgress<OperationProgress> progress = null)
        {
            var stop = c.StageComparison.StopFftBin == 0 ? c.StageComparison.OutputSamples / 2 : c.StageComparison.StopFftBin;
            var count = (stop - 1) / c.SegmentHop + 1; var result = new NotebookTemperatureProfile { Positions = new int[count], Shifts = new double[count] };
            var length = c.SegmentSize * c.SegmentIfftZeroPadFactor; var a = new Complex[length]; var b = new Complex[length];
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested(); var center = i * c.SegmentHop; var start = center - c.SegmentSize / 2;
                result.Positions[i] = center; result.Shifts[i] = double.NaN;
                if (start < 0 || start + c.SegmentSize > Math.Min(reference.Length, signal.Length)) continue;
                FillLocal(a, b, reference, signal, start, c); fft.InverseInPlace(a); fft.InverseInPlace(b);
                Correlate(a, b, c); result.Shifts[i] = Peak(b, c.StageComparison.MaximumLagSamples);
                if (i % 500 == 0) progress?.Report(new OperationProgress { Stage = "단계별 비교", Detail = "segment " + i + "/" + count, Percent = 40 + 55.0 * i / count });
            }
            return result;
        }
        private static void FillLocal(Complex[] a, Complex[] b, Complex[] r, Complex[] s, int start, IndependentOfdrProcessingConfig c)
        {
            Array.Clear(a, 0, a.Length); Array.Clear(b, 0, b.Length);
            for (var j = 0; j < c.SegmentSize; j++)
            { var w = Local(c.StageComparison) ? IndependentOfdrProcessingEngine.WindowValue(c.WindowFunction, j, c.SegmentSize) : 1; a[j] = r[start + j] * w; b[j] = s[start + j] * w; }
        }
        private void Correlate(Complex[] a, Complex[] b, IndependentOfdrProcessingConfig c)
        {
            double ma = 0, mb = 0;
            if (c.StageComparison.CenterLocalMagnitudes) { for (var j = 0; j < a.Length; j++) { ma += a[j].Magnitude; mb += b[j].Magnitude; } ma /= a.Length; mb /= b.Length; }
            double ea = 0, eb = 0;
            for (var j = 0; j < a.Length; j++) { var av = a[j].Magnitude - ma; var bv = b[j].Magnitude - mb; ea += av * av; eb += bv * bv; a[j] = new Complex(av, 0); b[j] = new Complex(bv, 0); }
            fft.ForwardInPlace(a); fft.ForwardInPlace(b);
            for (var j = 0; j < a.Length; j++) b[j] *= Complex.Conjugate(a[j]);
            fft.InverseInPlace(b);
            var scale = c.NormalizeCrossCorrelation ? 1 / Math.Max(1e-300, Math.Sqrt(ea * eb)) : 1;
            if (c.NormalizeCrossCorrelation || c.CrossCorrelationScale != 1) for (var j = 0; j < b.Length; j++) b[j] *= scale * c.CrossCorrelationScale;
        }
        private static double Peak(Complex[] c, int maxLag)
        {
            if (maxLag == 0) return NotebookTemperatureReproductionEngine.FindShift(c);
            var middle = c.Length / 2; var p = middle - maxLag; var best = double.NegativeInfinity;
            for (var j = middle - maxLag; j <= middle + maxLag; j++) if (c[(j + middle) % c.Length].Real > best) { best = c[(j + middle) % c.Length].Real; p = j; }
            var l = c[(p - 1 + middle) % c.Length].Real; var r = c[(p + 1 + middle) % c.Length].Real; var denom = l - 2 * best + r;
            var offset = Math.Abs(denom) > 1e-15 ? .5 * (l - r) / denom : 0;
            return p - middle + Math.Max(-.5, Math.Min(.5, offset));
        }
        public SegmentCorrelationView Inspect(IndependentOfdrProcessingResult result, int index)
        {
            var c = result.Config; var start = result.SegmentStarts[index]; var n = c.SegmentSize * c.SegmentIfftZeroPadFactor;
            var a = new Complex[n]; var b = new Complex[n]; FillLocal(a, b, result.ReferenceSpectrumValues, result.SignalSpectrumValues, start, c);
            var wr = PreviewReducer.FromSelector(c.SegmentSize, c.MaxPreviewPoints, i => a[i].Magnitude, "FFT bin", "Windowed magnitude");
            var ws = PreviewReducer.FromSelector(c.SegmentSize, c.MaxPreviewPoints, i => b[i].Magnitude, "FFT bin", "Windowed magnitude");
            var rawr = PreviewReducer.FromSelector(c.SegmentSize, c.MaxPreviewPoints, i => result.ReferenceSpectrumValues[start + i].Magnitude, "FFT bin", "Magnitude");
            var raws = PreviewReducer.FromSelector(c.SegmentSize, c.MaxPreviewPoints, i => result.SignalSpectrumValues[start + i].Magnitude, "FFT bin", "Magnitude");
            foreach (var v in new[] { wr, ws, rawr, raws }) { v.XStart = start; v.XEnd = start + c.SegmentSize - 1; }
            fft.InverseInPlace(a); fft.InverseInPlace(b);
            var ri = PreviewReducer.FromMagnitude(a, c.MaxPreviewPoints, "local sample", "Magnitude"); var si = PreviewReducer.FromMagnitude(b, c.MaxPreviewPoints, "local sample", "Magnitude");
            Correlate(a, b, c); var lag = Peak(b, c.StageComparison.MaximumLagSamples); var peak = Math.Max(0, Math.Min(n - 1, (int)Math.Round(lag + n / 2.0)));
            var corr = PreviewReducer.FromSelector(n, c.MaxPreviewPoints, i => b[(i + n / 2) % n].Real, "lag (sample)", "Correlation"); corr.XStart = -n / 2; corr.XEnd = n / 2 - 1;
            var low = Math.Max(0, peak - 128); var high = Math.Min(n - 1, peak + 128);
            var zoom = PreviewReducer.FromSelector(high - low + 1, c.MaxPreviewPoints, i => b[(low + i + n / 2) % n].Real, "lag (sample)", "Correlation"); zoom.XStart = low - n / 2; zoom.XEnd = high - n / 2;
            return new SegmentCorrelationView { SegmentIndex = index, SegmentStartFftBin = start, ReferenceSegment = rawr, SignalSegment = raws,
                WindowedReferenceSegment = wr, WindowedSignalSegment = ws, ReferenceIfft = ri, SignalIfft = si, Correlation = corr, CorrelationZoom = zoom,
                PeakIndex = peak, ZeroLagIndex = n / 2, PeakLagBin = (int)Math.Round(lag), PeakLagBins = lag, PeakMagnitude = b[(peak + n / 2) % n].Real, ZoomPeakIndex = peak - low };
        }
    }
}
