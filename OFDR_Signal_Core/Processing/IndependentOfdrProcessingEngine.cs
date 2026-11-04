using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Processing
{
    public sealed class IndependentOfdrProcessingEngine
    {
        public static readonly string[] SupportedWindows = { "Rectangular", "Hann", "Hamming", "Blackman" };
        private readonly FftEngine fft;
        private readonly AuxKLinearizer linearizer;

        public IndependentOfdrProcessingEngine(FftEngine fftEngine = null)
        {
            fft = fftEngine ?? new FftEngine();
            linearizer = new AuxKLinearizer(fft);
        }

        public bool NativeFftAvailable { get { return fft.NativeProviderAvailable; } }
        public string FftProviderDescription { get { return fft.ProviderDescription; } }

        public static void ValidateInput(IndependentOfdrInput input)
        {
            if (input == null || input.Auxiliary == null || input.SignalAuxiliary == null || input.Reference == null || input.Signal == null)
                throw new ArgumentNullException(nameof(input), "기준 CH1, 측정 CH1, Ref, Sig 입력이 모두 필요합니다.");
            if (new[] { input.Auxiliary, input.SignalAuxiliary, input.Reference, input.Signal }.Any(c => c.Preparation != null))
            {
                ValidatePreparedPair(input.Auxiliary, input.Reference, "Ref");
                ValidatePreparedPair(input.SignalAuxiliary, input.Signal, "Signal");
                var dt = input.Auxiliary.SampleIntervalSeconds;
                if (Math.Abs(input.SignalAuxiliary.SampleIntervalSeconds - dt) > Math.Max(1e-15, dt * 1e-9)) throw new InvalidOperationException("Ref/Signal 샘플 간격이 다릅니다.");
                return;
            }
            if (input.Auxiliary.ChannelNumber != 1 || input.SignalAuxiliary.ChannelNumber != 1)
                throw new InvalidOperationException("기준/측정 보조간섭계 입력은 CH1이어야 합니다.");
            if (Math.Abs(input.Auxiliary.DisplacementMm) > 1e-12) throw new InvalidOperationException("기준 보조간섭계 입력은 0.0 mm 데이터여야 합니다.");
            if (Math.Abs(input.Reference.DisplacementMm) > 1e-12) throw new InvalidOperationException("Ref 입력은 변화를 주지 않은 0.0 mm 데이터여야 합니다.");
            if (input.Signal.DisplacementMm <= 0.0) throw new InvalidOperationException("Sig 입력은 0.0 mm보다 큰 변형 데이터여야 합니다.");
            if (Math.Abs(input.SignalAuxiliary.DisplacementMm - input.Signal.DisplacementMm) > 1e-12)
                throw new InvalidOperationException("측정 보조간섭계 CH1은 Sig와 같은 변형량 파일이어야 합니다.");
            if (input.Reference.ChannelNumber != input.Signal.ChannelNumber || (input.Reference.ChannelNumber != 2 && input.Reference.ChannelNumber != 3))
                throw new InvalidOperationException("Ref와 Sig는 동일한 편광 채널(CH2 또는 CH3)이어야 합니다.");
            if (!string.Equals(input.Auxiliary.Repetition, input.Reference.Repetition, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("기준 보조간섭계와 Ref의 반복 번호가 같아야 합니다.");
            if (!string.Equals(input.SignalAuxiliary.Repetition, input.Signal.Repetition, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("측정 보조간섭계와 Sig의 반복 번호가 같아야 합니다.");
            var count = input.Auxiliary.Values == null ? 0 : input.Auxiliary.Values.Length;
            if (count < 4 || input.SignalAuxiliary.Values == null || input.Reference.Values == null || input.Signal.Values == null
                || input.SignalAuxiliary.Values.Length < 4 || input.Reference.Values.Length != count || input.Signal.Values.Length != input.SignalAuxiliary.Values.Length)
                throw new InvalidOperationException("기준 CH1, 측정 CH1, Ref, Sig의 포인트 수가 같아야 합니다.");
            var tolerance = Math.Max(1e-15, Math.Abs(input.Auxiliary.SampleIntervalSeconds) * 1e-9);
            if (Math.Abs(input.SignalAuxiliary.SampleIntervalSeconds - input.Auxiliary.SampleIntervalSeconds) > tolerance
                || Math.Abs(input.Reference.SampleIntervalSeconds - input.Auxiliary.SampleIntervalSeconds) > tolerance
                || Math.Abs(input.Signal.SampleIntervalSeconds - input.Auxiliary.SampleIntervalSeconds) > tolerance)
                throw new InvalidOperationException("기준 CH1, 측정 CH1, Ref, Sig의 시간축이 일치해야 합니다.");
        }

        public IndependentOfdrProcessingResult Run(IndependentOfdrInput input, IndependentOfdrProcessingConfig config, CancellationToken token, IProgress<OperationProgress> progress = null)
        {
            ValidateInput(input);
            config = config ?? new IndependentOfdrProcessingConfig();
            ValidateConfig(config);
            var qcBlock = PreparedSweepQcPolicy.InputBlockReason(input, config.MaxAuxNonmonotonicRatio);
            if (qcBlock != null) throw new InvalidOperationException("분석 전 위상 QC 차단:\n" + qcBlock);
            if (config.SegmentSize > Math.Min(input.Auxiliary.Values.Length, input.SignalAuxiliary.Values.Length) / 2) throw new ArgumentOutOfRangeException(nameof(config.SegmentSize), "Segment 크기는 양의 거리 FFT 구간보다 작아야 합니다.");

            var total = Stopwatch.StartNew();
            var performance = new List<NamedMetric>();
            var stage = Stopwatch.StartNew();

            progress?.Report(new OperationProgress { Stage = "FFT 비교", Detail = "K-linearization 전 원본 FFT", Percent = 0.0 });
            var referenceRaw = PreviewReducer.FromReal(input.Reference.Values, config.MaxPreviewPoints, "원본 샘플", "전압 (V)");
            var signalRaw = PreviewReducer.FromReal(input.Signal.Values, config.MaxPreviewPoints, "원본 샘플", "전압 (V)");
            var preKReferenceSpectrum = BuildSpectrumDbPreview(input.Reference.Values, config.MaxPreviewPoints, config.FftDbFloor, token, "FFT bin (K 보정 전)");
            var preKSignalSpectrum = BuildSpectrumDbPreview(input.Signal.Values, config.MaxPreviewPoints, config.FftDbFloor, token, "FFT bin (K 보정 전)");
            performance.Add(Metric("pre_k_ref_sig_fft_sec", stage.Elapsed.TotalSeconds, "s"));

            stage.Restart();
            var detailed = linearizer.LinearizeSweepsDetailed(input.Auxiliary.Values, input.Reference.Values, input.SignalAuxiliary.Values, input.Signal.Values,
                config.MaxAuxNonmonotonicRatio, config.MaxPreviewPoints, token, ScaleProgress(progress, "K-linearization", 8.0, 35.0));
            performance.Add(Metric("k_linearization_sec", stage.Elapsed.TotalSeconds, "s"));

            stage.Restart();
            progress?.Report(new OperationProgress { Stage = "Ref/Sig FFT", Detail = "K-linearization 후 거리 FFT", Percent = 36.0 });
            var referenceSpectrum = BuildSpectrum(detailed.Reference, token);
            var signalSpectrum = BuildSpectrum(detailed.Signal, token);
            var positiveSpectrumLength = Math.Max(config.SegmentSize, referenceSpectrum.Length / 2);
            performance.Add(Metric("linearized_ref_sig_fft_sec", stage.Elapsed.TotalSeconds, "s"));
            detailed.Reference = null;
            detailed.Signal = null;

            var physical = BuildPhysicalCalibration(input, config, detailed.CommonPhaseSpanRadians);
            stage.Restart();
            var starts = BuildSegmentStarts(positiveSpectrumLength, config.SegmentSize, config.SegmentHop);
            if (config.UsePhysicalAxes && config.RestrictToMeasurementRange)
            {
                starts = starts.Where(start =>
                {
                    var center = config.DistanceOffsetMeters
                        + ((start + ((config.SegmentSize - 1) / 2.0)) * physical.DistanceMetersPerFftBin);
                    return center >= config.AnalysisStartMeters && center <= config.AnalysisEndMeters;
                }).ToArray();
                if (starts.Length == 0)
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                        "분석 구간 {0:G6}~{1:G6} m에 포함되는 Segment가 없습니다. OPD, 거리 기하 계수, Offset 또는 분석 구간을 확인하세요.",
                        config.AnalysisStartMeters, config.AnalysisEndMeters));
            }
            var peakIndices = new int[starts.Length];
            var peakMagnitudes = new double[starts.Length];
            var peakLags = new double[starts.Length];
            var maximumLag = Math.Max(1, Math.Min((int)(physical.LocalSpectrumLength / 2.0) - 2,
                (int)Math.Ceiling(config.MaximumSpectralShiftGHz / Math.Max(1e-12, Math.Abs(physical.SpectralShiftGHzPerLag)))));
            ProcessSegments(referenceSpectrum, signalSpectrum, starts, peakIndices, peakMagnitudes, peakLags, maximumLag, config, token,
                ScaleProgress(progress, "S pol", 55.0, 96.0));
            performance.Add(Metric("segment_window_ifft_correlation_sec", stage.Elapsed.TotalSeconds, "s"));

            var selectedSegment = 0;
            if (config.UsePhysicalAxes && starts.Length > 0)
            {
                // 기본 상세 화면은 상관 크기가 우연히 큰 외부 구간이 아니라 사용자가 지정한
                // 변형 구간 중앙을 보여준다. 이후 UI에서 원하는 segment를 직접 선택할 수 있다.
                var targetDistance = (config.MeasurementStartMeters + config.MeasurementEndMeters) / 2.0;
                var minimumDistance = double.PositiveInfinity;
                for (var i = 0; i < starts.Length; i++)
                {
                    var center = config.DistanceOffsetMeters
                        + ((starts[i] + ((config.SegmentSize - 1) / 2.0)) * physical.DistanceMetersPerFftBin);
                    var distance = Math.Abs(center - targetDistance);
                    if (distance < minimumDistance) { minimumDistance = distance; selectedSegment = i; }
                }
            }
            else
            {
                var maximumPeak = double.NegativeInfinity;
                for (var i = 0; i < peakMagnitudes.Length; i++)
                {
                    if (peakMagnitudes[i] > maximumPeak) { maximumPeak = peakMagnitudes[i]; selectedSegment = i; }
                }
            }

            var centerDistances = new double[starts.Length];
            var strains = new double[starts.Length];
            var spectralShifts = new double[starts.Length];
            for (var i = 0; i < starts.Length; i++)
            {
                centerDistances[i] = config.DistanceOffsetMeters + ((starts[i] + ((config.SegmentSize - 1) / 2.0)) * physical.DistanceMetersPerFftBin);
                strains[i] = config.StrainOffsetMicrostrain + (peakLags[i] * physical.StrainMicrostrainPerLag);
                spectralShifts[i] = peakLags[i] * physical.SpectralShiftGHzPerLag;
            }

            total.Stop();
            performance.Add(Metric("total_processing_sec", total.Elapsed.TotalSeconds, "s"));
            performance.Add(Metric("peak_working_set_bytes", Process.GetCurrentProcess().PeakWorkingSet64, "bytes"));
            performance.Add(Metric("segment_count", starts.Length, "count"));
            performance.Add(Metric("distance_m_per_fft_bin", physical.DistanceMetersPerFftBin, "m/bin"));
            performance.Add(Metric("strain_microstrain_per_lag", physical.StrainMicrostrainPerLag, "microstrain/bin"));
            performance.Add(Metric("spectral_shift_ghz_per_lag", physical.SpectralShiftGHzPerLag, "GHz/bin"));
            performance.Add(Metric("maximum_correlation_search_lag", maximumLag, "bin"));
            if (physical.ExpectedAppliedStrainMicrostrain.HasValue) performance.Add(Metric("expected_applied_strain_microstrain", physical.ExpectedAppliedStrainMicrostrain.Value, "microstrain"));
            performance.Add(Metric("reference_input_samples", input.Reference.Values.Length, "count"));
            performance.Add(Metric("signal_input_samples", input.Signal.Values.Length, "count"));
            performance.Add(Metric("common_grid_samples", referenceSpectrum.Length, "count"));

            var referenceSpectrumPreview = BuildSpectrumDbPreview(referenceSpectrum, positiveSpectrumLength, config.MaxPreviewPoints,
                config.FftDbFloor, config.UsePhysicalAxes ? "거리 (m)" : "FFT bin");
            var signalSpectrumPreview = BuildSpectrumDbPreview(signalSpectrum, positiveSpectrumLength, config.MaxPreviewPoints,
                config.FftDbFloor, config.UsePhysicalAxes ? "거리 (m)" : "FFT bin");
            if (config.UsePhysicalAxes)
            {
                referenceSpectrumPreview.XStart = signalSpectrumPreview.XStart = config.DistanceOffsetMeters;
                referenceSpectrumPreview.XEnd = signalSpectrumPreview.XEnd = config.DistanceOffsetMeters + ((positiveSpectrumLength - 1) * physical.DistanceMetersPerFftBin);
            }

            var strainProfile = PreviewReducer.FromReal(config.UsePhysicalAxes ? strains : peakLags, config.MaxPreviewPoints,
                config.UsePhysicalAxes ? "거리 (m)" : "Segment 번호", config.UsePhysicalAxes ? "변형률 (με)" : "Peak 지연 (bin)");
            var spectralShiftProfile = PreviewReducer.FromReal(spectralShifts, config.MaxPreviewPoints,
                config.UsePhysicalAxes ? "거리 (m)" : "Segment 번호", "Rayleigh spectral shift (GHz)");
            if (config.UsePhysicalAxes && centerDistances.Length > 0)
            {
                strainProfile.XStart = centerDistances[0];
                strainProfile.XEnd = centerDistances[centerDistances.Length - 1];
                spectralShiftProfile.XStart = centerDistances[0];
                spectralShiftProfile.XEnd = centerDistances[centerDistances.Length - 1];
            }

            var result = new IndependentOfdrProcessingResult
            {
                AuxiliaryId = input.Auxiliary.MeasurementId,
                SignalAuxiliaryId = input.SignalAuxiliary.MeasurementId,
                ReferenceId = input.Reference.MeasurementId,
                SignalId = input.Signal.MeasurementId,
                Polarization = input.Signal.Preparation != null ? "CH4 측정 신호 (편광 미지정)" : input.Signal.ChannelNumber == 2 ? "Xpol (CH2)" : "Ypol (CH3)",
                SampleCount = referenceSpectrum.Length,
                KLinearization = detailed.ReferenceMetrics,
                KDiagnostics = detailed.ReferenceDiagnostics,
                SignalKLinearization = detailed.SignalMetrics,
                SignalKDiagnostics = detailed.SignalDiagnostics,
                ReferenceRaw = referenceRaw,
                SignalRaw = signalRaw,
                PreKReferenceSpectrum = preKReferenceSpectrum,
                PreKSignalSpectrum = preKSignalSpectrum,
                ReferenceSpectrum = referenceSpectrumPreview,
                SignalSpectrum = signalSpectrumPreview,
                PeakLagProfile = PreviewReducer.FromReal(peakLags, config.MaxPreviewPoints, "Segment 번호", "Peak 지연 (bin)"),
                StrainProfile = strainProfile,
                SpectralShiftProfile = spectralShiftProfile,
                PhysicalAxis = physical,
                ReferenceSpectrumValues = referenceSpectrum,
                SignalSpectrumValues = signalSpectrum,
                SegmentStarts = starts,
                SegmentPeakIndices = peakIndices,
                SegmentPeakMagnitudes = peakMagnitudes,
                SegmentPeakLags = peakLags,
                SegmentCenterDistancesMeters = centerDistances,
                Config = config,
                PerformanceMetrics = performance,
                FftProvider = fft.ProviderDescription
            };
            result.SelectedSegment = InspectSegment(result, selectedSegment);
            progress?.Report(new OperationProgress { Stage = "완료", Detail = input.Signal.MeasurementId, Percent = 100.0 });
            return result;
        }

        public SegmentCorrelationView InspectSegment(IndependentOfdrProcessingResult result, int segmentIndex)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (result.ReferenceSpectrumValues == null || result.SignalSpectrumValues == null) throw new InvalidOperationException("Segment 확인용 spectrum 데이터가 해제되었습니다.");
            if (segmentIndex < 0 || segmentIndex >= result.SegmentStarts.Length) throw new ArgumentOutOfRangeException(nameof(segmentIndex));

            var start = result.SegmentStarts[segmentIndex];
            var stages = BuildSegmentStages(result.ReferenceSpectrumValues, result.SignalSpectrumValues, start, result.Config);
            var zero = stages.Correlation.Length / 2;
            var maximumLag = Math.Max(1, Math.Min(zero - 2,
                (int)Math.Ceiling(result.Config.MaximumSpectralShiftGHz / Math.Max(1e-12, Math.Abs(result.PhysicalAxis.SpectralShiftGHzPerLag)))));
            var peak = FindPeak(stages.Correlation, maximumLag);
            var searchStart = zero - maximumLag;
            var searchEnd = zero + maximumLag;
            var correlationValues = stages.Correlation.Skip(searchStart).Take(searchEnd - searchStart + 1).Select(value => value.Real).ToArray();
            var peakInSearch = peak.Index - searchStart;
            var radius = Math.Min(256, Math.Max(32, stages.Correlation.Length / 64));
            var zoomStart = Math.Max(searchStart, peak.Index - radius);
            var zoomEnd = Math.Min(searchEnd, peak.Index + radius);
            var zoom = stages.Correlation.Skip(zoomStart).Take(zoomEnd - zoomStart + 1).Select(value => value.Real).ToArray();

            var signalPreview = PreviewReducer.FromMagnitude(stages.SignalSegment, result.Config.SegmentSize, "FFT bin", "크기");
            var referencePreview = PreviewReducer.FromMagnitude(stages.ReferenceSegment, result.Config.SegmentSize, "FFT bin", "크기");
            var windowedSignalPreview = PreviewReducer.FromMagnitude(stages.WindowedSignalSegment, result.Config.SegmentSize, "FFT bin", "Window 적용 크기");
            var windowedReferencePreview = PreviewReducer.FromMagnitude(stages.WindowedReferenceSegment, result.Config.SegmentSize, "FFT bin", "Window 적용 크기");
            var xStart = (double)start;
            var xEnd = start + result.Config.SegmentSize - 1.0;
            var centerDistance = result.Config.DistanceOffsetMeters + ((start + ((result.Config.SegmentSize - 1) / 2.0)) * result.PhysicalAxis.DistanceMetersPerFftBin);
            if (result.Config.UsePhysicalAxes)
            {
                xStart = result.Config.DistanceOffsetMeters + (start * result.PhysicalAxis.DistanceMetersPerFftBin);
                xEnd = result.Config.DistanceOffsetMeters + ((start + result.Config.SegmentSize - 1) * result.PhysicalAxis.DistanceMetersPerFftBin);
                signalPreview.XAxisTitle = referencePreview.XAxisTitle = windowedSignalPreview.XAxisTitle = windowedReferencePreview.XAxisTitle = "거리 (m)";
            }
            signalPreview.XStart = referencePreview.XStart = windowedSignalPreview.XStart = windowedReferencePreview.XStart = xStart;
            signalPreview.XEnd = referencePreview.XEnd = windowedSignalPreview.XEnd = windowedReferencePreview.XEnd = xEnd;

            return new SegmentCorrelationView
            {
                SegmentIndex = segmentIndex,
                SegmentStartFftBin = start,
                SignalSegment = signalPreview,
                ReferenceSegment = referencePreview,
                WindowedSignalSegment = windowedSignalPreview,
                WindowedReferenceSegment = windowedReferencePreview,
                SignalIfft = PreviewReducer.FromMagnitude(stages.SignalIfft, result.Config.MaxPreviewPoints, "국부 스펙트럼 샘플", "크기"),
                ReferenceIfft = PreviewReducer.FromMagnitude(stages.ReferenceIfft, result.Config.MaxPreviewPoints, "국부 스펙트럼 샘플", "크기"),
                Correlation = new PreviewSeries { Values = correlationValues, XStart = -maximumLag, XEnd = maximumLag, XAxisTitle = "지연 bin", YAxisTitle = "정규화 Correlation" },
                CorrelationZoom = new PreviewSeries { Values = zoom, XStart = zoomStart - zero, XEnd = zoomEnd - zero, XAxisTitle = "지연 bin", YAxisTitle = "Correlation 크기" },
                PeakIndex = peakInSearch,
                ZeroLagIndex = maximumLag,
                PeakLagBin = (int)Math.Round(peak.FractionalIndex - zero),
                PeakLagBins = peak.FractionalIndex - zero,
                PeakMagnitude = peak.Magnitude,
                ZoomPeakIndex = peak.Index - zoomStart,
                SegmentCenterDistanceMeters = centerDistance,
                PeakStrainMicrostrain = result.Config.StrainOffsetMicrostrain + ((peak.FractionalIndex - zero) * result.PhysicalAxis.StrainMicrostrainPerLag),
                PeakSpectralShiftGHz = (peak.FractionalIndex - zero) * result.PhysicalAxis.SpectralShiftGHzPerLag
            };
        }

        private Complex[] BuildSpectrum(double[] values, CancellationToken token)
        {
            var spectrum = new Complex[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                if ((i & 0x3ffff) == 0) token.ThrowIfCancellationRequested();
                spectrum[i] = new Complex(values[i], 0.0);
            }
            fft.ForwardInPlace(spectrum);
            return spectrum;
        }

        private PreviewSeries BuildSpectrumDbPreview(double[] values, int maxPreviewPoints, double floorDb, CancellationToken token, string xAxisTitle)
        {
            var spectrum = BuildSpectrum(values, token);
            var positiveLength = Math.Max(2, spectrum.Length / 2);
            return BuildSpectrumDbPreview(spectrum, positiveLength, maxPreviewPoints, floorDb, xAxisTitle);
        }

        private static PreviewSeries BuildSpectrumDbPreview(Complex[] spectrum, int positiveLength, int maxPreviewPoints, double floorDb, string xAxisTitle)
        {
            var maximum = 0.0;
            for (var i = 0; i < positiveLength; i++) if (spectrum[i].Magnitude > maximum) maximum = spectrum[i].Magnitude;
            maximum = Math.Max(maximum, 1e-300);
            return PreviewReducer.FromSelector(positiveLength, maxPreviewPoints,
                index => Math.Max(floorDb, 20.0 * Math.Log10(Math.Max(1e-300, spectrum[index].Magnitude) / maximum)),
                xAxisTitle, "반사 신호 (dB, 최대값 기준)");
        }

        private static void ProcessSegments(Complex[] reference, Complex[] signal, int[] starts, int[] peaks, double[] peakMagnitudes, double[] lags,
            int maximumLag, IndependentOfdrProcessingConfig config, CancellationToken token, IProgress<OperationProgress> progress)
        {
            var completed = 0;
            var sync = new object();
            var options = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
            Parallel.For(0, starts.Length, options, segmentIndex =>
            {
                var correlation = BuildEquivalentLocalSpectrumCorrelation(reference, signal, starts[segmentIndex], config);
                var peak = FindPeak(correlation, maximumLag);
                peaks[segmentIndex] = peak.Index;
                peakMagnitudes[segmentIndex] = peak.Magnitude;
                lags[segmentIndex] = peak.FractionalIndex - (correlation.Length / 2.0);
                var now = Interlocked.Increment(ref completed);
                if (now == starts.Length || now % 2048 == 0)
                {
                    lock (sync) progress?.Report(new OperationProgress
                    {
                        Stage = "Segment → Window → IFFT → Cross Correlation",
                        Detail = "Ref / Sig",
                        Completed = now,
                        Total = starts.Length,
                        Percent = 100.0 * now / starts.Length
                    });
                }
            });
        }

        private static SegmentStages BuildSegmentStages(Complex[] reference, Complex[] signal, int start, IndependentOfdrProcessingConfig config)
        {
            var size = config.SegmentSize;
            var paddedLength = Radix2Fft.NextPowerOfTwo(size * config.SegmentIfftZeroPadFactor);
            var refSegment = new Complex[size];
            var sigSegment = new Complex[size];
            var windowedRef = new Complex[size];
            var windowedSig = new Complex[size];
            var refIfft = new Complex[paddedLength];
            var sigIfft = new Complex[paddedLength];
            for (var i = 0; i < size; i++)
            {
                refSegment[i] = reference[start + i];
                sigSegment[i] = signal[start + i];
                var window = WindowValue(config.WindowFunction, i, size);
                windowedRef[i] = refSegment[i] * window;
                windowedSig[i] = sigSegment[i] * window;
                refIfft[i] = windowedRef[i];
                sigIfft[i] = windowedSig[i];
            }
            Radix2Fft.InverseInPlace(refIfft);
            Radix2Fft.InverseInPlace(sigIfft);
            return new SegmentStages
            {
                ReferenceSegment = refSegment,
                SignalSegment = sigSegment,
                WindowedReferenceSegment = windowedRef,
                WindowedSignalSegment = windowedSig,
                ReferenceIfft = refIfft,
                SignalIfft = sigIfft,
                Correlation = BuildCrossCorrelationFromLocalSpectra(refIfft, sigIfft, config)
            };
        }

        private static Complex[] BuildCrossCorrelationFromLocalSpectra(Complex[] referenceLocalSpectrum, Complex[] signalLocalSpectrum, IndependentOfdrProcessingConfig config)
        {
            var referenceFrequency = BuildMeanRemovedMagnitudeTrace(referenceLocalSpectrum);
            var signalFrequency = BuildMeanRemovedMagnitudeTrace(signalLocalSpectrum);
            Radix2Fft.ForwardInPlace(referenceFrequency);
            Radix2Fft.ForwardInPlace(signalFrequency);
            return BuildShiftedCorrelationFromFrequency(referenceFrequency, signalFrequency, config);
        }

        private static Complex[] BuildEquivalentLocalSpectrumCorrelation(Complex[] reference, Complex[] signal, int start, IndependentOfdrProcessingConfig config)
        {
            // Every segment follows the same visible processing contract: distance-domain Window,
            // IFFT to the local Rayleigh spectrum, then correlation of the mean-removed spectral
            // magnitude patterns. Arrays are local to this iteration and are not retained.
            var length = Radix2Fft.NextPowerOfTwo(config.SegmentSize * config.SegmentIfftZeroPadFactor);
            var referenceLocalSpectrum = new Complex[length];
            var signalLocalSpectrum = new Complex[length];
            for (var i = 0; i < config.SegmentSize; i++)
            {
                var window = WindowValue(config.WindowFunction, i, config.SegmentSize);
                referenceLocalSpectrum[i] = reference[start + i] * window;
                signalLocalSpectrum[i] = signal[start + i] * window;
            }
            Radix2Fft.InverseInPlace(referenceLocalSpectrum);
            Radix2Fft.InverseInPlace(signalLocalSpectrum);
            return BuildCrossCorrelationFromLocalSpectra(referenceLocalSpectrum, signalLocalSpectrum, config);
        }

        private static Complex[] BuildMeanRemovedMagnitudeTrace(Complex[] localSpectrum)
        {
            var trace = new Complex[localSpectrum.Length];
            var mean = 0.0;
            for (var i = 0; i < localSpectrum.Length; i++) mean += localSpectrum[i].Magnitude;
            mean /= localSpectrum.Length;
            for (var i = 0; i < localSpectrum.Length; i++) trace[i] = new Complex(localSpectrum[i].Magnitude - mean, 0.0);
            return trace;
        }

        private static Complex[] BuildShiftedCorrelationFromFrequency(Complex[] referenceFrequency, Complex[] signalFrequency, IndependentOfdrProcessingConfig config)
        {
            var buffer = new Complex[referenceFrequency.Length];
            var referenceEnergy = 0.0;
            var signalEnergy = 0.0;
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = signalFrequency[i] * Complex.Conjugate(referenceFrequency[i]) * config.CrossCorrelationScale;
                referenceEnergy += (referenceFrequency[i].Real * referenceFrequency[i].Real)
                    + (referenceFrequency[i].Imaginary * referenceFrequency[i].Imaginary);
                signalEnergy += (signalFrequency[i].Real * signalFrequency[i].Real)
                    + (signalFrequency[i].Imaginary * signalFrequency[i].Imaginary);
            }
            Radix2Fft.InverseInPlace(buffer);
            if (config.NormalizeCrossCorrelation)
            {
                // Parseval for the Matlab-scaled FFT: local-spectrum energy is
                // sum(|FFT|^2) / N. One global energy denominator preserves useful
                // spectral amplitudes; the former bin-wise normalization amplified
                // low-energy-bin phase noise and selected unstable lag peaks.
                var denominator = Math.Sqrt(referenceEnergy * signalEnergy) / buffer.Length;
                if (denominator <= 1e-20) Array.Clear(buffer, 0, buffer.Length);
                else for (var i = 0; i < buffer.Length; i++) buffer[i] /= denominator;
            }
            var shifted = new Complex[buffer.Length];
            var shift = buffer.Length / 2;
            for (var i = 0; i < buffer.Length; i++) shifted[i] = buffer[(i + shift) % buffer.Length];
            return shifted;
        }

        private static PhysicalAxisCalibrationResult BuildPhysicalCalibration(IndependentOfdrInput input, IndependentOfdrProcessingConfig config, double commonPhaseSpanRadians)
        {
            var phaseSpan = Math.Abs(commonPhaseSpanRadians);
            var localLength = Radix2Fft.NextPowerOfTwo(config.SegmentSize * config.SegmentIfftZeroPadFactor);
            var distancePerBin = Math.PI * config.AuxiliaryPathDifferenceMeters * config.DistanceGeometryFactor / phaseSpan;
            const double speedOfLight = 299792458.0;
            var sweepSpanHz = speedOfLight * phaseSpan / (2.0 * Math.PI * config.AuxiliaryGroupIndex * config.AuxiliaryPathDifferenceMeters);
            var spectralShiftHzPerLag = sweepSpanHz / localLength;
            var spectralShiftGHzPerLag = spectralShiftHzPerLag / 1e9;
            var localDeltaK = 2.0 * Math.PI * spectralShiftHzPerLag / speedOfLight;
            var centerFrequencyHz = speedOfLight / (config.CenterWavelengthNm * 1e-9);
            var strainPerLag = spectralShiftHzPerLag / (centerFrequencyHz * config.StrainCoefficient) * 1e6 * config.StrainScaleFactor;
            double? displacement = input.Signal.Preparation == null ? input.Signal.DisplacementMm : input.Signal.Preparation.KnownDisplacementMm;
            var expected = displacement * 1000.0 / config.DeformationLengthMeters;
            return new PhysicalAxisCalibrationResult
            {
                Enabled = config.UsePhysicalAxes,
                DistanceMetersPerFftBin = distancePerBin,
                LocalSpectrumDeltaKRadPerMeter = localDeltaK,
                StrainMicrostrainPerLag = strainPerLag,
                SpectralShiftGHzPerLag = spectralShiftGHzPerLag,
                SweepSpanGHz = sweepSpanHz / 1e9,
                AuxiliaryGroupIndex = config.AuxiliaryGroupIndex,
                ExpectedAppliedStrainMicrostrain = expected,
                LocalSpectrumLength = localLength,
                Note = "18.2703 m는 보조간섭계의 물리 경로차로 사용하고 spectral 축에는 group index를 적용합니다. 거리식은 같은 광섬유의 굴절률이 상쇄되는 구성을 가정합니다. 장비 기하, 중심 파장과 strain 계수는 확인해야 합니다."
            };
        }

        private static void ValidatePreparedPair(OfdrChannelData auxiliary, OfdrChannelData measurement, string role)
        {
            var a = auxiliary.Preparation; var m = measurement.Preparation;
            if (a == null || m == null || a.Role != role || m.Role != role || !ReferenceEquals(a, m)
                || auxiliary.ChannelNumber != 3 || measurement.ChannelNumber != 4 || a.AuxiliaryChannel != 3 || a.MeasurementChannel != 4
                || auxiliary.Values == null || measurement.Values == null || auxiliary.Values.Length < 4 || auxiliary.Values.Length != measurement.Values.Length
                || auxiliary.Values.Length != a.RowCount || !ModeHopSettings.IsFinite(a.SampleIntervalSeconds) || a.SampleIntervalSeconds <= 0
                || auxiliary.SampleIntervalSeconds != a.SampleIntervalSeconds || measurement.SampleIntervalSeconds != a.SampleIntervalSeconds)
                throw new InvalidOperationException(role + "의 동시 취득 CH3/CH4 스윕 대응을 확인하세요.");
        }

        private static PeakEstimate FindPeak(Complex[] values, int maximumLag)
        {
            var zero = values.Length / 2;
            var start = Math.Max(1, zero - maximumLag);
            var end = Math.Min(values.Length - 2, zero + maximumLag);
            var peak = start;
            var maximum = values[start].Real;
            for (var i = start + 1; i <= end; i++) if (values[i].Real > maximum) { maximum = values[i].Real; peak = i; }
            var left = values[peak - 1].Real;
            var center = values[peak].Real;
            var right = values[peak + 1].Real;
            var denominator = left - (2.0 * center) + right;
            var offset = Math.Abs(denominator) <= 1e-20 ? 0.0 : 0.5 * (left - right) / denominator;
            offset = Math.Max(-0.5, Math.Min(0.5, offset));
            return new PeakEstimate { Index = peak, FractionalIndex = peak + offset, Magnitude = center };
        }

        private static int[] BuildSegmentStarts(int length, int size, int hop)
        {
            var last = Math.Max(0, length - size);
            var starts = new List<int>();
            for (var start = 0; start <= last; start += hop) starts.Add(start);
            if (starts.Count == 0 || starts[starts.Count - 1] != last) starts.Add(last);
            return starts.ToArray();
        }

        public static double WindowValue(string name, int index, int length)
        {
            if (length <= 1 || string.Equals(name, "Rectangular", StringComparison.OrdinalIgnoreCase)) return 1.0;
            var angle = 2.0 * Math.PI * index / (length - 1.0);
            if (string.Equals(name, "Hamming", StringComparison.OrdinalIgnoreCase)) return 0.54 - (0.46 * Math.Cos(angle));
            if (string.Equals(name, "Blackman", StringComparison.OrdinalIgnoreCase)) return 0.42 - (0.5 * Math.Cos(angle)) + (0.08 * Math.Cos(2.0 * angle));
            return 0.5 - (0.5 * Math.Cos(angle));
        }

        private static void ValidateConfig(IndependentOfdrProcessingConfig config)
        {
            if (!SupportedWindows.Any(value => string.Equals(value, config.WindowFunction, StringComparison.OrdinalIgnoreCase))) config.WindowFunction = "Hann";
            if (config.SegmentSize < 4) throw new ArgumentOutOfRangeException(nameof(config.SegmentSize));
            if (config.SegmentHop < 1) throw new ArgumentOutOfRangeException(nameof(config.SegmentHop));
            if (config.SegmentIfftZeroPadFactor < 1) throw new ArgumentOutOfRangeException(nameof(config.SegmentIfftZeroPadFactor));
            if (config.MaxAuxNonmonotonicRatio < 0.0 || config.MaxAuxNonmonotonicRatio > 1.0) throw new ArgumentOutOfRangeException(nameof(config.MaxAuxNonmonotonicRatio));
            if (config.AuxiliaryPathDifferenceMeters <= 0.0) throw new ArgumentOutOfRangeException(nameof(config.AuxiliaryPathDifferenceMeters));
            if (config.AuxiliaryGroupIndex <= 0.0) throw new ArgumentOutOfRangeException(nameof(config.AuxiliaryGroupIndex));
            if (config.DistanceGeometryFactor <= 0.0) throw new ArgumentOutOfRangeException(nameof(config.DistanceGeometryFactor));
            if (config.CenterWavelengthNm <= 0.0) throw new ArgumentOutOfRangeException(nameof(config.CenterWavelengthNm));
            if (config.StrainCoefficient <= 0.0) throw new ArgumentOutOfRangeException(nameof(config.StrainCoefficient));
            if (config.StrainScaleFactor == 0.0) throw new ArgumentOutOfRangeException(nameof(config.StrainScaleFactor));
            if (config.DeformationLengthMeters <= 0.0) throw new ArgumentOutOfRangeException(nameof(config.DeformationLengthMeters));
            if (config.MeasurementEndMeters <= config.MeasurementStartMeters) throw new ArgumentOutOfRangeException(nameof(config.MeasurementEndMeters));
            if (config.AnalysisEndMeters <= config.AnalysisStartMeters) throw new ArgumentOutOfRangeException(nameof(config.AnalysisEndMeters));
            if (config.MaximumSpectralShiftGHz <= 0.0) throw new ArgumentOutOfRangeException(nameof(config.MaximumSpectralShiftGHz));
            if (config.FftDbFloor >= 0.0 || double.IsNaN(config.FftDbFloor) || double.IsInfinity(config.FftDbFloor)) throw new ArgumentOutOfRangeException(nameof(config.FftDbFloor));
            if (config.SweepRangeNm <= 0.0) throw new ArgumentOutOfRangeException(nameof(config.SweepRangeNm));
            if (config.SweepRateNmPerSecond <= 0.0) throw new ArgumentOutOfRangeException(nameof(config.SweepRateNmPerSecond));
            if (config.SamplingRateSamplesPerSecond <= 0.0) throw new ArgumentOutOfRangeException(nameof(config.SamplingRateSamplesPerSecond));
            config.MaxPreviewPoints = Math.Max(16, Math.Min(10000, config.MaxPreviewPoints));
        }

        private static NamedMetric Metric(string name, double value, string unit) { return new NamedMetric { Name = name, Value = value, Unit = unit }; }

        private static IProgress<OperationProgress> ScaleProgress(IProgress<OperationProgress> target, string stage, double start, double end)
        {
            if (target == null) return null;
            return new InlineProgress(value => target.Report(new OperationProgress
            {
                Stage = stage,
                Detail = value == null ? string.Empty : value.Detail,
                Completed = value == null ? 0 : value.Completed,
                Total = value == null ? 0 : value.Total,
                Percent = start + ((end - start) * (value == null ? 0.0 : value.Percent) / 100.0)
            }));
        }

        private sealed class SegmentStages
        {
            public Complex[] ReferenceSegment { get; set; }
            public Complex[] SignalSegment { get; set; }
            public Complex[] WindowedReferenceSegment { get; set; }
            public Complex[] WindowedSignalSegment { get; set; }
            public Complex[] ReferenceIfft { get; set; }
            public Complex[] SignalIfft { get; set; }
            public Complex[] Correlation { get; set; }
        }

        private sealed class PeakEstimate
        {
            public int Index { get; set; }
            public double FractionalIndex { get; set; }
            public double Magnitude { get; set; }
        }

        private sealed class InlineProgress : IProgress<OperationProgress>
        {
            private readonly Action<OperationProgress> report;
            public InlineProgress(Action<OperationProgress> value) { report = value; }
            public void Report(OperationProgress value) { report(value); }
        }
    }
}
