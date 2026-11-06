using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Processing
{
    // Read-only experiment operations. None of these controls are applied by the GUI processing path.
    public sealed class AcquisitionDiagnostics
    {
        private readonly FftEngine fft;
        public AcquisitionDiagnostics(FftEngine engine = null) { fft = engine ?? new FftEngine(); }
        public string Provider => fft.ProviderDescription;

        public AcquisitionSignalMetrics Analyze(double[] values, double dt, int[] joins, CancellationToken token)
        {
            Validate(values, dt); token.ThrowIfCancellationRequested();
            var phase = AuxiliaryPhaseExtractor.Extract(fft, values, 0, token, null, true);
            var m = Spectrum(values, dt, token); var n = values.Length;
            var amplitude = phase.Analytic.Select(v => v.Magnitude).OrderBy(v => v).ToArray();
            var increments = new double[n - 1];
            var edge = Math.Min(n / 8, Math.Max(8, (int)Math.Ceiling(10.24e-6 / dt)));
            var joinRadius = Math.Max(1, (int)Math.Ceiling(10.24e-6 / dt));
            var last = phase.Phase[0]; var badInterior = 0; var interior = 0; var near = 0; var badNear = 0; var away = 0; var badAway = 0;
            var maxJoinPhase = 0.0; var maxJoinVoltage = 0.0;
            joins = joins ?? new int[0]; var j = 0;
            for (var i = 1; i < n; i++)
            {
                if ((i & 8191) == 0) token.ThrowIfCancellationRequested();
                var rejected = phase.Phase[i] <= last; if (!rejected) last = phase.Phase[i];
                increments[i - 1] = phase.Phase[i] - phase.Phase[i - 1];
                while (j < joins.Length && joins[j] < i - joinRadius) j++;
                var nearJoin = j < joins.Length && Math.Abs(joins[j] - i) <= joinRadius;
                if (i >= edge && i < n - edge)
                {
                    interior++; if (rejected) badInterior++;
                    if (nearJoin) { near++; if (rejected) badNear++; } else { away++; if (rejected) badAway++; }
                }
            }
            foreach (var join in joins.Where(i => i > 0 && i < n))
            {
                maxJoinPhase = Math.Max(maxJoinPhase, Math.Abs(phase.Phase[join] - phase.Phase[join - 1]));
                maxJoinVoltage = Math.Max(maxJoinVoltage, Math.Abs(values[join] - values[join - 1]));
            }
            Array.Sort(increments);
            m["samples"] = n; m["sample_rate_hz"] = 1 / dt; m["duration_s"] = n * dt;
            m["phase_average_hz"] = phase.Metrics.PhaseSpanRadians / (2 * Math.PI * (n - 1) * dt);
            m["phase_increment_p01_rad"] = Quantile(increments, .01); m["phase_increment_p50_rad"] = Quantile(increments, .5); m["phase_increment_p99_rad"] = Quantile(increments, .99);
            m["instantaneous_frequency_p99_hz"] = Quantile(increments, .99) / (2 * Math.PI * dt);
            m["amplitude_p01_v"] = Quantile(amplitude, .01); m["amplitude_p50_v"] = Quantile(amplitude, .5); m["amplitude_p99_v"] = Quantile(amplitude, .99);
            m["edge_samples_excluded_each_side"] = edge; m["interior_samples"] = interior; m["interior_nonmonotonic_ratio"] = badInterior / (double)Math.Max(1, interior);
            m["join_count"] = joins.Length; m["join_near_samples"] = near; m["join_near_rejected"] = badNear;
            m["join_away_samples"] = away; m["join_away_nonmonotonic_ratio"] = badAway / (double)Math.Max(1, away);
            m["maximum_join_phase_step_rad"] = maxJoinPhase; m["maximum_join_voltage_step_v"] = maxJoinVoltage;
            var failure = phase.Failure(1e-5);
            return new AcquisitionSignalMetrics { Values = m, Phase = phase.Metrics, Decision = failure == null ? "PASS_PHASE_QC" : "REJECT_PHASE_QC", Provider = fft.ProviderDescription };
        }

        public Dictionary<string, double> Spectrum(double[] values, double dt, CancellationToken token)
        {
            Validate(values, dt); token.ThrowIfCancellationRequested();
            var n = values.Length; var mean = values.Average(); var spectrum = new Complex[n]; var squares = 0.0;
            for (var i = 0; i < n; i++) { var v = values[i] - mean; squares += v * v; spectrum[i] = new Complex(v * (.5 - .5 * Math.Cos(2 * Math.PI * i / (n - 1))), 0); }
            fft.ForwardInPlace(spectrum);
            var energy = new double[n / 2 + 1]; var sum = 0.0; var peak = 1; var above1250 = 0.0; var above10 = 0.0;
            for (var i = 1; i < energy.Length; i++)
            {
                energy[i] = spectrum[i].Magnitude * spectrum[i].Magnitude; sum += energy[i];
                if (energy[i] > energy[peak]) peak = i;
                if (i / (dt * n) > 1.25e6) above1250 += energy[i];
                if (i / (dt * n) > 10e6) above10 += energy[i];
            }
            var cumulative = 0.0; var p95 = 0; var p99 = 0;
            for (var i = 1; i < energy.Length; i++) { cumulative += energy[i]; if (p95 == 0 && cumulative >= .95 * sum) p95 = i; if (p99 == 0 && cumulative >= .99 * sum) p99 = i; }
            return new Dictionary<string, double> { ["mean_v"] = mean, ["ac_rms_v"] = Math.Sqrt(squares / n), ["spectral_peak_hz"] = peak / (dt * n),
                ["spectral_p95_hz"] = p95 / (dt * n), ["spectral_p99_hz"] = p99 / (dt * n), ["energy_above_1_25mhz_fraction"] = sum > 0 ? above1250 / sum : 0,
                ["energy_above_10mhz_fraction"] = sum > 0 ? above10 / sum : 0, ["hann_energy"] = sum };
        }

        public static AcquisitionDiagnosticBlock Exclude(AcquisitionDiagnosticBlock block, double tailSeconds, bool longestContinuous, CancellationToken token)
        {
            if (block?.Auxiliary == null || block.Auxiliary.Length < 16 || block.Auxiliary.Length > 250000
                || !ModeHopSettings.IsFinite(block.SampleIntervalSeconds) || block.SampleIntervalSeconds <= 0
                || !ModeHopSettings.IsFinite(tailSeconds) || tailSeconds < 0 || block.Trigger?.Length != block.Auxiliary.Length || block.Measurement?.Length != block.Auxiliary.Length
                || (block.SweepTrigger != null && block.SweepTrigger.Length != block.Auxiliary.Length))
                throw new ArgumentException("Invalid paired diagnostic block/tail.");
            var scaledTail = tailSeconds / block.SampleIntervalSeconds;
            if (scaledTail > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(tailSeconds));
            var tail = tailSeconds == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(scaledTail - 1e-9));
            var end = (long)block.PreviousHighEndIndex; var kept = new List<int>(); var best = new List<int>(); var run = new List<int>();
            for (var i = 0; i < block.Auxiliary.Length; i++)
            {
                if ((i & 8191) == 0) token.ThrowIfCancellationRequested();
                if (double.IsNaN(block.Trigger[i]) || double.IsNaN(block.Measurement[i]) || double.IsNaN(block.Auxiliary[i]) || (block.SweepTrigger != null && double.IsNaN(block.SweepTrigger[i]))) throw new ArgumentException("Invalid channel NaN.");
                var high = ModeHopSettings.IsFinite(block.Trigger[i]) && block.Trigger[i] > 1.8; if (high) end = (long)block.StartIndex + i + 1;
                var reject = high || (long)block.StartIndex + i < end + tail || double.IsInfinity(block.Measurement[i]) || double.IsInfinity(block.Auxiliary[i])
                    || double.IsInfinity(block.Trigger[i]) || (block.SweepTrigger != null && double.IsInfinity(block.SweepTrigger[i]));
                if (!reject) { kept.Add(i); run.Add(i); }
                else { if (run.Count > best.Count) best = run; run = new List<int>(); }
            }
            if (run.Count > best.Count) best = run;
            return Select(block, longestContinuous ? best.ToArray() : kept.ToArray());
        }

        public static AcquisitionDiagnosticBlock Select(AcquisitionDiagnosticBlock block, int[] indices)
        {
            if (indices.Length < 16) throw new InvalidOperationException("Fewer than 16 samples remain in diagnostic selection.");
            var original = indices.Select(i => block.SourceIndices[i]).ToArray(); var joins = new List<int>();
            for (var i = 1; i < original.Length; i++) if (original[i] != original[i - 1] + 1) joins.Add(i);
            return new AcquisitionDiagnosticBlock { SourcePath = block.SourcePath, StartIndex = original[0], SampleIntervalSeconds = block.SampleIntervalSeconds,
                Auxiliary = indices.Select(i => block.Auxiliary[i]).ToArray(), Measurement = indices.Select(i => block.Measurement[i]).ToArray(),
                Trigger = indices.Select(i => block.Trigger[i]).ToArray(), SweepTrigger = block.SweepTrigger == null ? null : indices.Select(i => block.SweepTrigger[i]).ToArray(),
                SourceIndices = original, Joins = joins.ToArray() };
        }

        // Windowed-sinc rate conversion. Common symmetric support avoids padding/edge transients.
        // A virtual 20 MS/s acquisition is a feasibility control, not proof about a physical ADC.
        public static double[] Resample(double[] values, double sourceRate, double targetRate, CancellationToken token, int radius = 160)
        {
            Validate(values, 1 / sourceRate);
            if (!ModeHopSettings.IsFinite(targetRate) || targetRate <= 0 || targetRate > sourceRate || radius < 16 || values.Length <= 2 * radius + 16) throw new ArgumentException("Invalid diagnostic resampling scope.");
            var step = sourceRate / targetRate; var count = (int)Math.Floor((values.Length - 1 - 2 * radius) / step) + 1;
            var output = new double[count]; var cutoff = .45 * targetRate / sourceRate; var kernels = new Dictionary<long, double[]>();
            for (var i = 0; i < count; i++)
            {
                if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                var position = radius + i * step; var center = (int)Math.Floor(position); var fraction = position - center;
                var key = (long)Math.Round(fraction * 1e9); double[] kernel;
                if (!kernels.TryGetValue(key, out kernel))
                {
                    kernel = new double[2 * radius + 1]; var norm = 0.0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        var x = k - fraction; var sinc = Math.Abs(x) < 1e-12 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
                        var window = Math.Abs(x) <= radius ? .42 + .5 * Math.Cos(Math.PI * x / radius) + .08 * Math.Cos(2 * Math.PI * x / radius) : 0;
                        kernel[k + radius] = sinc * window; norm += kernel[k + radius];
                    }
                    for (var k = 0; k < kernel.Length; k++) kernel[k] /= norm;
                    kernels[key] = kernel;
                }
                var sum = 0.0; for (var k = -radius; k <= radius; k++) sum += values[center + k] * kernel[k + radius]; output[i] = sum;
            }
            return output;
        }

        public Dictionary<string, double> CompareResampledPhase(double[] source, double[] converted, double sourceRate, double targetRate, CancellationToken token, int firstSourceIndex = 160)
        {
            Validate(source, 1 / sourceRate); Validate(converted, 1 / targetRate);
            var step = sourceRate / targetRate;
            if (firstSourceIndex < 0 || firstSourceIndex + (converted.Length - 1) * step > source.Length - 1) throw new ArgumentException("Converted phase coordinates exceed the source window.");
            var a = AuxiliaryPhaseExtractor.Extract(fft, source, 0, token).Phase;
            var b = AuxiliaryPhaseExtractor.Extract(fft, converted, 0, token).Phase;
            var edge = Math.Min(converted.Length / 8, Math.Max(8, (int)Math.Ceiling(10.24e-6 * targetRate)));
            var errors = new double[converted.Length - 2 * edge];
            for (var i = edge; i < converted.Length - edge; i++)
            {
                var x = firstSourceIndex + i * step; var left = (int)Math.Floor(x); var right = Math.Min(a.Length - 1, left + 1);
                errors[i - edge] = b[i] - (a[left] + (a[right] - a[left]) * (x - left));
            }
            var offset = errors.Average(); var residual = errors.Select(v => Math.Abs(v - offset)).OrderBy(v => v).ToArray();
            return new Dictionary<string, double> { ["phase_error_constant_offset_rad"] = offset,
                ["phase_error_rms_rad"] = Math.Sqrt(residual.Select(v => v * v).Average()), ["phase_error_p95_abs_rad"] = Quantile(residual, .95),
                ["phase_comparison_samples"] = errors.Length, ["phase_comparison_slope_removed"] = 0 };
        }

        public AcquisitionPairReport RunPair(AcquisitionDiagnosticBlock reference, AcquisitionDiagnosticBlock signal, double pathMeters,
            bool notice, bool physicalGauge, CancellationToken token)
        {
            var refPhase = Analyze(reference.Auxiliary, reference.SampleIntervalSeconds, reference.Joins, token).Phase;
            var sigPhase = Analyze(signal.Auxiliary, signal.SampleIntervalSeconds, signal.Joins, token).Phase;
            var span = Math.Min(refPhase.PhaseSpanRadians, sigPhase.PhaseSpanRadians);
            var distancePerBin = Math.PI * pathMeters / span;
            var config = new IndependentOfdrProcessingConfig { AuxiliaryPathDifferenceMeters = pathMeters, CenterWavelengthNm = 1550, AuxiliaryGroupIndex = 1.4682,
                SamplingRateSamplesPerSecond = 1 / reference.SampleIntervalSeconds, SweepRangeNm = notice ? 50 : 16, SweepRateNmPerSecond = notice ? 50 : 100,
                AllowFailedPhaseQcForDiagnostics = true, AnalysisStartMeters = notice ? 0 : 3.5, AnalysisEndMeters = notice ? 50 : 8.7,
                SegmentSize = physicalGauge ? Math.Max(4, (int)Math.Round(.5 / distancePerBin)) : 1024,
                SegmentHop = physicalGauge ? Math.Max(1, (int)Math.Round(.125 / distancePerBin)) : 256, MaxPreviewPoints = 10000 };
            var input = Input(reference, signal, refPhase, sigPhase, notice);
            var engine = new IndependentOfdrProcessingEngine(fft);
            using (var result = engine.Run(input, config, token))
            {
                var count = result.SegmentPeakLags.Length; var clarity = new double[count]; var clarityAvailable = new bool[count]; var boundary = new bool[count];
                var maximumLag = result.PerformanceMetrics.First(v => v.Name == "maximum_correlation_search_lag").Value;
                for (var i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested(); var detail = engine.InspectSegment(result, i); var curve = detail.Correlation;
                    var secondary = -1.0;
                    for (var j = 0; j < curve.Values.Length; j++)
                    {
                        var lag = curve.XStart + (curve.XEnd - curve.XStart) * j / Math.Max(1, curve.Values.Length - 1);
                        if (Math.Abs(lag - detail.PeakLagBins) > 2 * config.SegmentIfftZeroPadFactor) secondary = Math.Max(secondary, curve.Values[j]);
                    }
                    // A tiny distance segment can leave no candidate outside the exclusion
                    // radius. The old -1 sentinel must not become a fictitious >1 separation.
                    clarityAvailable[i] = secondary >= 0;
                    clarity[i] = clarityAvailable[i] ? detail.PeakMagnitude - secondary : 0;
                    boundary[i] = Math.Abs(detail.PeakLagBins) >= maximumLag - 1;
                }
                var sorted = result.SegmentPeakLags.Select(v => v * result.PhysicalAxis.SpectralShiftGHzPerLag).OrderBy(v => v).ToArray();
                var peaks = result.SegmentPeakMagnitudes.OrderBy(v => v).ToArray(); var separation = clarity.Where((v, i) => clarityAvailable[i]).OrderBy(v => v).ToArray();
                var available = result.PhysicalAxis.SweepSpanGHz;
                return new AcquisitionPairReport { Status = "COMPLETED_DIAGNOSTIC", Interpretation = "Relative-phase grids do not establish absolute optical-frequency alignment. No calibrated temperature, plateau or outside-zero acceptance is inferred. Clarity uses the full correlation search transiently; only scalar segment metrics and <=10000-point distance FFT previews are retained.",
                    Config = config, DistancesMeters = result.SegmentCenterDistancesMeters.ToArray(), SpectralShiftGHz = result.SegmentPeakLags.Select(v => v * result.PhysicalAxis.SpectralShiftGHzPerLag).ToArray(),
                    CorrelationPeaks = result.SegmentPeakMagnitudes.ToArray(), PeakSeparation = clarity, PeakSeparationAvailable = clarityAvailable, SearchBoundary = boundary, ReferenceDistanceFft = result.ReferenceSpectrum, SignalDistanceFft = result.SignalSpectrum,
                    Metrics = new Dictionary<string, double> {
                        ["reference_phase_qc_ratio"] = refPhase.NonmonotonicRatio, ["signal_phase_qc_ratio"] = sigPhase.NonmonotonicRatio,
                        ["phase_qc_passed"] = result.InputPhaseQcPassed ? 1 : 0, ["reference_samples"] = reference.Auxiliary.Length, ["signal_samples"] = signal.Auxiliary.Length,
                        ["common_phase_span_rad"] = span, ["available_optical_span_ghz"] = available, ["effective_maximum_shift_ghz"] = maximumLag * result.PhysicalAxis.SpectralShiftGHzPerLag,
                        ["requested_shift_range_covered"] = maximumLag * result.PhysicalAxis.SpectralShiftGHzPerLag >= 179.999 ? 1 : 0,
                        ["gauge_m"] = config.SegmentSize * distancePerBin, ["hop_m"] = config.SegmentHop * distancePerBin, ["distance_per_bin_m"] = distancePerBin,
                        ["spectral_shift_per_lag_ghz"] = result.PhysicalAxis.SpectralShiftGHzPerLag, ["segments"] = count,
                        ["correlation_peak_p10"] = Quantile(peaks, .1), ["correlation_peak_median"] = Quantile(peaks, .5), ["peak_separation_median"] = Quantile(separation, .5),
                        ["peak_separation_available_segments"] = separation.Length,
                        ["boundary_peak_fraction"] = boundary.Count(v => v) / (double)count,
                        ["shift_p10_ghz"] = Quantile(sorted, .1), ["shift_median_ghz"] = Quantile(sorted, .5), ["shift_p90_ghz"] = Quantile(sorted, .9),
                        ["absolute_frequency_alignment_verified"] = 0, ["full_record_processed"] = 0 } };
            }
        }

        private static IndependentOfdrInput Input(AcquisitionDiagnosticBlock reference, AcquisitionDiagnosticBlock signal, KLinearizationMetrics rm, KLinearizationMetrics sm, bool notice)
        {
            var input = new IndependentOfdrInput { Auxiliary = Channel(reference, "Ref", true, rm, notice), Reference = Channel(reference, "Ref", false, rm, notice),
                SignalAuxiliary = Channel(signal, "Signal", true, sm, notice), Signal = Channel(signal, "Signal", false, sm, notice) };
            input.Reference.Preparation = input.Auxiliary.Preparation; input.Signal.Preparation = input.SignalAuxiliary.Preparation;
            return input;
        }
        private static OfdrChannelData Channel(AcquisitionDiagnosticBlock block, string role, bool auxiliary, KLinearizationMetrics phase, bool notice)
        {
            var n = block.Auxiliary.Length;
            return new OfdrChannelData { SourcePath = block.SourcePath, SourceType = "BoundedAcquisitionDiagnostic", MeasurementId = role + ":" + block.SourcePath + ":" + block.StartIndex,
                ChannelNumber = notice ? (auxiliary ? 3 : 4) : (auxiliary ? 1 : 2), Repetition = "0000", DisplacementMm = role == "Ref" ? 0 : .4,
                SampleIntervalSeconds = block.SampleIntervalSeconds, Values = auxiliary ? block.Auxiliary : block.Measurement,
                Preparation = !notice ? null : new PreparedSweepMetadata { Role = role, SourcePath = block.SourcePath, RowCount = n, SampleIntervalSeconds = block.SampleIntervalSeconds,
                    SelectedStartIndex = 0, SelectedEndIndexExclusive = n, Sweep = new SweepRange { Number = 1 },
                    DiagnosisSettings = new AuxiliaryInspectionSettings { StartSeconds = 0, EndSeconds = n * block.SampleIntervalSeconds },
                    Diagnosis = new AuxiliaryQualityMetrics { Phase = phase, SelectedStartIndex = 0, SelectedEndIndexExclusive = n, Decision = phase.NonmonotonicRatio <= 1e-5 ? "PASS_PHASE_QC" : "REJECT_PHASE_QC" } } };
        }
        public static double Quantile(double[] sorted, double fraction) => sorted.Length == 0 ? 0 : sorted[(int)((sorted.Length - 1) * fraction)];
        private static void Validate(double[] values, double dt)
        {
            if (values == null || values.Length < 16 || values.Length > 250000 || !ModeHopSettings.IsFinite(dt) || dt <= 0 || values.Any(v => !ModeHopSettings.IsFinite(v))) throw new ArgumentException("Finite bounded samples and a positive interval are required.");
        }
    }

}
