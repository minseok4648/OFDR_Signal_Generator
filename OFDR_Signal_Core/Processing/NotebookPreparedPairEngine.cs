using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Processing
{
    // Diagnostic branch of the normal Run/Inspect API. Inputs are the user's confirmed
    // raw crops; no stored profile, source-file reread or second trigger crop is used.
    public sealed class NotebookPreparedPairEngine
    {
        private readonly FftEngine fft = new FftEngine();
        private readonly NotebookTemperatureReproductionEngine notebook = new NotebookTemperatureReproductionEngine();

        internal static NotebookTemperatureConfig Settings(IndependentOfdrProcessingConfig c) => new NotebookTemperatureConfig
        {
            ResampledSamples = c.NotebookResampledSamples, AuxiliarySliceStart = c.NotebookAuxiliarySliceStart,
            MeasurementSliceStart = c.NotebookMeasurementSliceStart, SegmentSize = c.SegmentSize,
            SegmentStep = c.SegmentHop, ZeroPadFactor = c.SegmentIfftZeroPadFactor, StopBin = c.NotebookResampledSamples / 2
        };

        public IndependentOfdrProcessingResult Run(IndependentOfdrInput input, IndependentOfdrProcessingConfig config,
            CancellationToken token, IProgress<OperationProgress> progress)
        {
            if (!config.AllowFailedPhaseQcForDiagnostics || config.UsePhysicalAxes)
                throw new ArgumentException("노트북 비교는 진단 실행을 켜고 물리 거리/strain 축을 꺼야 합니다.");
            var c = Settings(config); c.Validate(); notebook.RequireNativeProvider();
            if (config.WindowFunction != "Hann" || config.NormalizeCrossCorrelation)
                throw new ArgumentException("노트북 비교 조건은 전체 Hann 및 상관 정규화 해제입니다. 다른 설정은 일반 처리 모드에서 비교하세요.");
            if ((c.StopBin - 1) / c.SegmentStep + 1 > 10000)
                throw new ArgumentException("노트북 비교 UI는 최대 10,000개 프로파일을 지원합니다. Segment 이동 간격을 늘리세요.");
            var clock = Stopwatch.StartNew();
            var rd = new KLinearizationDiagnostics(); var sd = new KLinearizationDiagnostics();
            progress?.Report(new OperationProgress { Stage = "노트북 비교", Detail = "확정 Ref의 CH3/CH4 보간", Percent = 5 });
            var reference = Prepare(input.Auxiliary.Values, input.Reference.Values, c, config, rd, token);
            progress?.Report(new OperationProgress { Stage = "노트북 비교", Detail = "확정 Signal의 CH3/CH4 보간", Percent = 25 });
            var signal = Prepare(input.SignalAuxiliary.Values, input.Signal.Values, c, config, sd, token);
            var profile = notebook.Compare(reference.Spectrum, signal.Spectrum, c, token,
                (done, total) => progress?.Report(new OperationProgress { Stage = "노트북 비교", Detail = "Segment → IFFT → magnitude correlation", Percent = 45 + done * 40.0 / total }));
            var self = notebook.Compare(reference.Spectrum, reference.Spectrum, c, token);
            var valid = Enumerable.Range(0, profile.Shifts.Length).Where(i => ModeHopSettings.IsFinite(profile.Shifts[i])).ToArray();
            if (valid.Length == 0) throw new InvalidOperationException("완전한 local segment가 없습니다.");
            var result = new IndependentOfdrProcessingResult
            {
                AuxiliaryId = input.Auxiliary.MeasurementId, SignalAuxiliaryId = input.SignalAuxiliary.MeasurementId,
                ReferenceId = input.Reference.MeasurementId, SignalId = input.Signal.MeasurementId, Polarization = "CH4 / notebook lag diagnostic",
                SampleCount = c.ResampledSamples, Config = config, FftProvider = fft.ProviderDescription,
                ReferenceRaw = PreviewReducer.FromReal(input.Reference.Values, config.MaxPreviewPoints, "확정 crop sample", "V"),
                SignalRaw = PreviewReducer.FromReal(input.Signal.Values, config.MaxPreviewPoints, "확정 crop sample", "V"),
                KLinearization = Metrics(reference.Metrics, config), SignalKLinearization = Metrics(signal.Metrics, config), KDiagnostics = rd, SignalKDiagnostics = sd,
                ReferenceSpectrumValues = reference.Spectrum, SignalSpectrumValues = signal.Spectrum,
                ReferenceSpectrum = Db(reference.Spectrum, config), SignalSpectrum = Db(signal.Spectrum, config),
                PeakLagProfile = new PreviewSeries { Values = valid.Select(i => profile.Shifts[i]).ToArray(), XStart = valid.First(), XEnd = valid.Last(), XAxisTitle = "프로파일 순번", YAxisTitle = "상관 lag (sample)" },
                PhysicalAxis = new PhysicalAxisCalibrationResult { Enabled = false, LocalSpectrumLength = c.SegmentSize * c.ZeroPadFactor,
                    Note = "노트북 조건 비교: 독립 endpoint phase span, 전체 Hann, local Window 없음. 물리 거리/strain 미교정." },
                SegmentStarts = valid.Select(i => profile.Positions[i] - c.SegmentSize / 2).ToArray(),
                SegmentPeakLags = valid.Select(i => profile.Shifts[i]).ToArray(), SegmentPeakIndices = valid.Select(i => (int)Math.Round(profile.Shifts[i] + c.SegmentSize * c.ZeroPadFactor / 2.0)).ToArray(),
                SegmentPeakMagnitudes = new double[valid.Length], IsDiagnosticAnalysis = true,
                InputPhaseQcPassed = reference.Metrics.NonmonotonicRatio <= config.MaxAuxNonmonotonicRatio && signal.Metrics.NonmonotonicRatio <= config.MaxAuxNonmonotonicRatio,
                NotebookProfile = profile, NotebookReferenceProfile = self
            };
            result.PhaseQcSummary = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "노트북 재현 진단 · raw phase 역행 Ref {0:P3} / Signal {1:P3} · QC 통과로 승격하지 않음 · 공통 span 정렬 없음", reference.Metrics.NonmonotonicRatio, signal.Metrics.NonmonotonicRatio);
            result.SpectralShiftProfile = result.PeakLagProfile;
            result.PreKReferenceSpectrum = RawDb(input.Reference.Values, config, token);
            result.PreKSignalSpectrum = RawDb(input.Signal.Values, config, token);
            result.WindowedReference = WindowPreview(reference.Values, config); result.WindowedSignal = WindowPreview(signal.Values, config);
            result.SelectedSegment = Inspect(result, valid.Length / 2);
            result.PerformanceMetrics.Add(new NamedMetric { Name = "notebook_processing_seconds", Value = clock.Elapsed.TotalSeconds, Unit = "s" });
            result.PerformanceMetrics.Add(new NamedMetric { Name = "reference_confirmed_samples", Value = input.Reference.Values.Length, Unit = "samples" });
            result.PerformanceMetrics.Add(new NamedMetric { Name = "signal_confirmed_samples", Value = input.Signal.Values.Length, Unit = "samples" });
            progress?.Report(new OperationProgress { Stage = "노트북 비교", Detail = "확정 raw 입력 전체 처리 완료", Percent = 100 });
            return result;
        }

        private NotebookTemperatureSweep Prepare(double[] aux, double[] measurement, NotebookTemperatureConfig c,
            IndependentOfdrProcessingConfig config, KLinearizationDiagnostics diagnostics, CancellationToken token)
        {
            var n = aux.Length - c.AuxiliarySliceStart - c.MeasurementSliceStart;
            if (aux.Length != measurement.Length || n < 4) throw new ArgumentException("확정 CH3/CH4 길이 또는 채널 지연을 확인하세요.");
            var a = new double[n]; var m = new double[n];
            Array.Copy(aux, c.AuxiliarySliceStart, a, 0, n); Array.Copy(measurement, c.MeasurementSliceStart, m, 0, n);
            double first, last, ratio;
            var values = notebook.LinearizeLikeNotebook(a, m, c.ResampledSamples, token, out first, out last, out ratio, diagnostics, config.MaxPreviewPoints);
            return new NotebookTemperatureSweep { Values = values, Spectrum = notebook.BuildSpectrum(values),
                Metrics = new NotebookSweepMetrics { SourceSamples = aux.Length, UsedSamples = n, FirstPhaseRadians = first, LastPhaseRadians = last, NonmonotonicRatio = ratio } };
        }

        private static KLinearizationMetrics Metrics(NotebookSweepMetrics m, IndependentOfdrProcessingConfig c) => new KLinearizationMetrics
        {
            SampleCount = m.UsedSamples, AcceptedAnchorCount = m.UsedSamples, PhaseSpanRadians = m.LastPhaseRadians - m.FirstPhaseRadians,
            SweepDirection = m.LastPhaseRadians >= m.FirstPhaseRadians ? 1 : -1, NonmonotonicRatio = m.NonmonotonicRatio,
            PhaseQcPassed = m.NonmonotonicRatio <= c.MaxAuxNonmonotonicRatio, DiagnosticOnly = true,
            PhaseQcFailureReason = "raw adjacent phase increments <= 0; all samples stable-sorted as in the notebook. Anchor QC is not applied."
        };

        private PreviewSeries Db(Complex[] spectrum, IndependentOfdrProcessingConfig c)
        {
            var maximum = Math.Max(1e-300, spectrum.Max(v => v.Magnitude));
            return PreviewReducer.FromSelector(spectrum.Length, c.MaxPreviewPoints,
                i => Math.Max(c.FftDbFloor, 20 * Math.Log10(Math.Max(1e-300, spectrum[i].Magnitude) / maximum)), "FFT bin", "dB (normalized)");
        }
        private PreviewSeries RawDb(double[] values, IndependentOfdrProcessingConfig c, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var a = values.Select(v => new Complex(v, 0)).ToArray(); fft.ForwardInPlace(a);
            Array.Resize(ref a, a.Length / 2); return Db(a, c);
        }
        private static PreviewSeries WindowPreview(double[] values, IndependentOfdrProcessingConfig c)
        {
            var mean = values.Average();
            return PreviewReducer.FromSelector(values.Length, c.MaxPreviewPoints, i => (values[i] - mean) * (.5 - .5 * Math.Cos(2 * Math.PI * i / (values.Length - 1))), "k-linearized sample", "V (global Hann)");
        }

        public SegmentCorrelationView Inspect(IndependentOfdrProcessingResult result, int index)
        {
            var c = Settings(result.Config); var start = result.SegmentStarts[index]; var n = c.SegmentSize * c.ZeroPadFactor;
            var a = new Complex[n]; var b = new Complex[n];
            Array.Copy(result.ReferenceSpectrumValues, start, a, 0, c.SegmentSize); Array.Copy(result.SignalSpectrumValues, start, b, 0, c.SegmentSize);
            var refSegment = PreviewReducer.FromSelector(c.SegmentSize, result.Config.MaxPreviewPoints, i => a[i].Magnitude, "FFT bin", "Magnitude");
            var sigSegment = PreviewReducer.FromSelector(c.SegmentSize, result.Config.MaxPreviewPoints, i => b[i].Magnitude, "FFT bin", "Magnitude");
            refSegment.XStart = sigSegment.XStart = start; refSegment.XEnd = sigSegment.XEnd = start + c.SegmentSize - 1;
            fft.InverseInPlace(a); fft.InverseInPlace(b);
            var refIfft = PreviewReducer.FromMagnitude(a, result.Config.MaxPreviewPoints, "local sample", "Magnitude");
            var sigIfft = PreviewReducer.FromMagnitude(b, result.Config.MaxPreviewPoints, "local sample", "Magnitude");
            for (var i = 0; i < n; i++) { a[i] = new Complex(a[i].Magnitude, 0); b[i] = new Complex(b[i].Magnitude, 0); }
            fft.ForwardInPlace(a); fft.ForwardInPlace(b);
            for (var i = 0; i < n; i++) b[i] *= Complex.Conjugate(a[i]);
            fft.InverseInPlace(b);
            var lag = NotebookTemperatureReproductionEngine.FindShift(b); var peak = Math.Max(0, Math.Min(n - 1, (int)Math.Round(lag + n / 2.0)));
            var corr = PreviewReducer.FromSelector(n, result.Config.MaxPreviewPoints, i => b[(i + n / 2) % n].Real, "lag (sample)", "Correlation");
            corr.XStart = -n / 2; corr.XEnd = n / 2 - 1;
            var low = Math.Max(0, peak - 128); var high = Math.Min(n - 1, peak + 128);
            var zoom = PreviewReducer.FromSelector(high - low + 1, result.Config.MaxPreviewPoints, i => b[(low + i + n / 2) % n].Real, "lag (sample)", "Correlation");
            zoom.XStart = low - n / 2; zoom.XEnd = high - n / 2;
            return new SegmentCorrelationView { SegmentIndex = index, SegmentStartFftBin = start, ReferenceSegment = refSegment, SignalSegment = sigSegment,
                WindowedReferenceSegment = refSegment, WindowedSignalSegment = sigSegment, ReferenceIfft = refIfft, SignalIfft = sigIfft,
                Correlation = corr, CorrelationZoom = zoom, PeakIndex = peak, ZeroLagIndex = n / 2, PeakLagBin = (int)Math.Round(lag), PeakLagBins = lag,
                PeakMagnitude = b[(peak + n / 2) % n].Real, ZoomPeakIndex = peak - low };
        }
    }
}
