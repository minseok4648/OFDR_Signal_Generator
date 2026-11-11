using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;

namespace OFDR_Signal_Core.Tests
{
    [TestClass]
    public sealed class DocumentStageTests
    {
        private static OfdrChannelData Channel(double[] values, double dt = 2e-8, PreparedSweepMetadata m = null)
            => new OfdrChannelData { Values = values, SampleIntervalSeconds = dt, Preparation = m };
        [TestMethod] public void BandpassRejectsOutOfBandAndRetainsInBandWithoutDelay()
        {
            const int n = 65536; const double dt = 2e-8;
            var x = Enumerable.Range(0, n).Select(i => 3 + Math.Sin(2 * Math.PI * .2e6 * i * dt) + Math.Sin(2 * Math.PI * 5e6 * i * dt) + Math.Sin(2 * Math.PI * 18e6 * i * dt)).ToArray();
            var y = DocumentStagePreprocessor.Bandpass(x, dt, 1e6, 12e6, CancellationToken.None);
            Func<double, double> amplitude = frequency => 2 * Enumerable.Range(1024, n - 2048).Sum(i => y[i] * Math.Sin(2 * Math.PI * frequency * i * dt)) / (n - 2048);
            Assert.IsTrue(Math.Abs(amplitude(.2e6)) < .01); Assert.IsTrue(amplitude(5e6) > .85); Assert.IsTrue(Math.Abs(amplitude(18e6)) < .1);
            var quadrature = 2 * Enumerable.Range(1024, n - 2048).Sum(i => y[i] * Math.Cos(2 * Math.PI * 5e6 * i * dt)) / (n - 2048);
            Assert.IsTrue(Math.Abs(quadrature) < .001);
            Assert.Throws<ArgumentException>(() => DocumentStagePreprocessor.Bandpass(x, dt, 1e6, 25e6, CancellationToken.None));
            Assert.Throws<OperationCanceledException>(() => DocumentStagePreprocessor.Bandpass(x, dt, 1e6, 12e6, new CancellationToken(true)));
        }
        [TestMethod] public void FractionalDelayUsesHeaderTimeAndHasCorrectDirection()
        {
            var x = Enumerable.Range(0, 128).Select(i => (double)i).ToArray();
            foreach (var dt in new[] { 2e-8, 4e-8 })
            foreach (var sign in new[] { -1, 1 })
            {
                var d = new DocumentStageSettings { Enabled = true, MinimumRangeSamples = 16, EdgeGuardMicroseconds = 0,
                    DelayMode = StageDelayMode.Microseconds, DelayMicroseconds = sign * .205 };
                var p = DocumentStagePreprocessor.Process(Channel(x, dt), Channel(x, dt), d, true, 64, CancellationToken.None);
                Assert.AreEqual(sign * .205e-6 / dt, p.Auxiliary[0] - p.Measurement[0], 1e-10);
                Assert.AreEqual(128 - (int)Math.Ceiling(.205e-6 / dt), p.Auxiliary.Length);
                Assert.AreEqual(127, x.Last());
            }
        }
        [TestMethod] public void ContinuousRangesRespectCropAndNeverFilterAcrossGaps()
        {
            var m = new PreparedSweepMetadata { RowCount = 100, Sweep = new SweepRange { StartIndex = 0, EndIndexExclusive = 300 },
                SelectedStartIndex = 50, SelectedEndIndexExclusive = 150, ExcludedRanges = new List<ModeHopRange> { new ModeHopRange { StartIndex = 100, EndIndexExclusive = 200 } } };
            var x = Enumerable.Range(0, 100).Select(i => (double)i).ToArray(); var channel = Channel(x, 2e-8, m);
            var ranges = DocumentStagePreprocessor.Ranges(channel);
            Assert.AreEqual(2, ranges.Count); Assert.AreEqual(50, ranges[0].SourceStart); Assert.AreEqual(200, ranges[1].SourceStart); Assert.AreEqual(50, ranges[1].ArrayStart);
            var d = new DocumentStageSettings { Enabled = true, MinimumRangeSamples = 16, ReferenceRangeIndex = 1, EdgeGuardMicroseconds = 0 };
            var p = DocumentStagePreprocessor.Process(channel, channel, d, true, 64, CancellationToken.None);
            Assert.AreEqual(50, p.Auxiliary.Length); Assert.AreEqual(50, p.Auxiliary[0]);
            d.SelectContinuousRange = false; d.BandpassEnabled = true;
            Assert.Throws<ArgumentException>(() => DocumentStagePreprocessor.Process(channel, channel, d, true, 64, CancellationToken.None));
        }
        private static double Direct(double[] a, double[] b, int lag)
        {
            var first = Math.Max(0, -lag); var end = Math.Min(a.Length, a.Length - lag);
            var x = Enumerable.Range(first, end - first).Select(i => a[i]).ToArray(); var y = Enumerable.Range(first, end - first).Select(i => b[i + lag]).ToArray();
            var mx = x.Average(); var my = y.Average();
            return x.Zip(y, (u, v) => (u - mx) * (v - my)).Sum() / Math.Sqrt(x.Sum(u => (u - mx) * (u - mx)) * y.Sum(v => (v - my) * (v - my)));
        }
        [TestMethod] public void LinearZnccMatchesDirectOverlapFormulaAndKnownTranslation()
        {
            var random = new Random(513); var x = Enumerable.Range(0, 256).Select(i => 2 + random.NextDouble()).ToArray();
            var y = Enumerable.Range(0, 256).Select(i => i < 7 ? 8 + random.NextDouble() : 3 * x[i - 7] + 4).ToArray();
            var a = x.Select(v => new Complex(v, 0)).ToArray(); var b = y.Select(v => new Complex(v, 0)).ToArray();
            StageZncc.Correlate(a, b, StageCorrelationMode.OverlapZncc, .5, new FftEngine(), CancellationToken.None);
            for (var lag = -80; lag <= 80; lag++) Assert.AreEqual(Direct(x, y, lag), b[(lag + 256) % 256].Real, 1e-10);
            var p = StageZncc.Evaluate(b, 80, 3, new DocumentStageSettings());
            Assert.AreEqual(7, p.CandidateLag, .02); Assert.AreEqual(1, p.PeakZncc, 1e-10); Assert.AreEqual(249.0 / 256, p.Overlap, 1e-12);
        }
        [TestMethod] public void ConstantAndLowOverlapDoNotBecomeZeroChange()
        {
            var a = Enumerable.Repeat(new Complex(2, 0), 128).ToArray(); var b = (Complex[])a.Clone();
            StageZncc.Correlate(a, b, StageCorrelationMode.OverlapZncc, .99, new FftEngine(), CancellationToken.None);
            var p = StageZncc.Evaluate(b, 50, 2, new DocumentStageSettings());
            Assert.IsTrue(double.IsNaN(p.CandidateLag)); Assert.AreEqual("INSUFFICIENT_VARIANCE_OR_OVERLAP", p.Status);
        }
        [TestMethod] public void DocumentSelfPairKeepsQualitySeparateAndDoesNotMutateConfig()
        {
            const int n = 16384; const double dt = 4e-8;
            var aux = Enumerable.Range(0, n).Select(i => Math.Cos(2 * Math.PI * 2e6 * i * dt)).ToArray();
            var random = new Random(9); var measurement = Enumerable.Range(0, n).Select(i => random.NextDouble() - .5).ToArray();
            var input = new IndependentOfdrInput { Auxiliary = Channel(aux, dt), SignalAuxiliary = Channel(aux, dt), Reference = Channel(measurement, dt), Signal = Channel(measurement, dt) };
            var c = new IndependentOfdrProcessingConfig { StageComparisonEnabled = true, UsePhysicalAxes = false, AllowFailedPhaseQcForDiagnostics = true,
                SegmentSize = 64, SegmentHop = 32, SegmentIfftZeroPadFactor = 8, MaxPreviewPoints = 128,
                StageComparison = new StageComparisonSettings { OutputSamples = 4096, Document = new DocumentStageSettings { Enabled = true, EdgeGuardMicroseconds = 0,
                    PeakExclusionPm = .2, MinimumSeparation = 0, MaximumShiftPm = 4 } } };
            var engine = new StageComparisonEngine();
            using (var result = engine.Run(input, c, CancellationToken.None))
            {
                Assert.IsNotNull(result.DocumentStages); Assert.IsNull(result.StrainProfile);
                Assert.AreEqual(0, c.StageComparison.MaximumLagSamples);
                Assert.IsTrue(result.DocumentStages.Points.All(p => Math.Abs(p.CandidateLag) < 1e-8 && Math.Abs(p.PeakZncc - 1) < 1e-8));
                Assert.IsTrue(result.DocumentStages.AcceptedShift.Values.Length <= 128);
                Assert.IsFalse(result.DocumentStages.AcceptedShift.Values.Any(double.IsInfinity));
                Assert.AreEqual(result.SegmentPeakLags[10], engine.Inspect(result, 10).PeakLagBins, 1e-9);
                var copy = JsonContractSerializer.Clone(result.DocumentStages);
                Assert.AreEqual(result.DocumentStages.Points.Count, copy.Points.Count);
            }
            c.StageComparison.Grid = RelativePhaseGrid.IndependentEndpoints;
            Assert.Throws<ArgumentException>(() => engine.Run(input, c, CancellationToken.None));
        }

        [TestMethod] public void NonlinearSweepRecoversInteriorPlateauOutsideZeroAndDistanceSlope()
        {
            const int n = 262144; const double dt = 4e-8, bandwidth = 20e9, tau = .979e-6, ng = 1.4682;
            var dz = 299792458.0 / (2 * ng * bandwidth); var random = new Random(1183); var fft = new FftEngine();
            var referenceFft = new Complex[n];
            for (var k = (int)(3.3 / dz); k < (int)(9 / dz); k++)
            {
                var value = Complex.FromPolarCoordinates(.5 + random.NextDouble(), 2 * Math.PI * random.NextDouble());
                referenceFft[k] = value; referenceFft[n - k] = Complex.Conjugate(value);
            }
            foreach (var meters in new[] { 4.0, 8.0 })
            { var k = (int)Math.Round(meters / dz); referenceFft[k] *= 100; referenceFft[n - k] = Complex.Conjugate(referenceFft[k]); }
            var refTime = (Complex[])referenceFft.Clone(); fft.InverseInPlace(refTime);
            var positions = Enumerable.Range(0, n).Select(i => i + .015 * n / (2 * Math.PI) * Math.Sin(2 * Math.PI * i / n)).ToArray();
            Func<Complex[], double[]> sample = x => positions.Select(p => { var k = (int)Math.Floor(p); var f = p - k; return x[k].Real + f * (x[Math.Min(n - 1, k + 1)].Real - x[k].Real); }).ToArray();
            var aux = positions.Select(p => Math.Cos(2 * Math.PI * bandwidth * tau * p / n)).ToArray(); var r = sample(refTime);
            foreach (var sign in new[] { -1, 1 })
            {
                var sigFft = (Complex[])referenceFft.Clone(); var sampleShift = sign * 100e6 / bandwidth * n;
                for (var k = (int)Math.Ceiling(5.5 / dz); k <= (int)Math.Floor(6.5 / dz); k++)
                { sigFft[k] *= Complex.FromPolarCoordinates(1, -2 * Math.PI * k * sampleShift / n); sigFft[n - k] = Complex.Conjugate(sigFft[k]); }
                fft.InverseInPlace(sigFft);
                var input = new IndependentOfdrInput { Auxiliary = Channel(aux, dt), SignalAuxiliary = Channel(aux, dt), Reference = Channel(r, dt), Signal = Channel(sample(sigFft), dt) };
                var c = new IndependentOfdrProcessingConfig { StageComparisonEnabled = true, UsePhysicalAxes = true, AllowFailedPhaseQcForDiagnostics = true,
                    SegmentSize = 32, SegmentHop = 8, SegmentIfftZeroPadFactor = 64, MaxPreviewPoints = 1024,
                    StageComparison = new StageComparisonSettings { OutputSamples = n, Document = new DocumentStageSettings { Enabled = true,
                        EdgeGuardMicroseconds = 0, PhysicalWindow = true, GaugeMeters = .1, HopMeters = .025, StartMeters = 3.5, EndMeters = 8.7,
                        MaximumShiftPm = 4, PeakExclusionPm = .3, MinimumSeparation = 0, Correlation = StageCorrelationMode.CircularZncc } } };
                using (var result = new StageComparisonEngine().Run(input, c, CancellationToken.None))
                {
                    var points = result.DocumentStages.Points;
                    var expected = sign * Math.Pow(1550e-9, 2) / 299792458.0 * 100e6 * 1e12;
                    var inside = points.Where(p => p.DistanceMeters >= 5.65 && p.DistanceMeters <= 6.35).Select(p => p.CandidateShiftPm).OrderBy(v => v).ToArray();
                    var plateau = inside[inside.Length / 2];
                    var outside = points.Where(p => (p.DistanceMeters >= 4.3 && p.DistanceMeters <= 5.2) || (p.DistanceMeters >= 6.8 && p.DistanceMeters <= 7.7)).Max(p => Math.Abs(p.CandidateShiftPm));
                    Func<double, double> peakDistance = center => Enumerable.Range((int)((center - .1) / dz), (int)(.2 / dz)).OrderByDescending(k => result.ReferenceSpectrumValues[k].Magnitude).First() * result.DocumentStages.DistancePerBin;
                    var slope = (peakDistance(8) - peakDistance(4)) / 4;
                    Console.WriteLine("DOCUMENT_GOLDEN sign={0}, expected_pm={1:R}, plateau_pm={2:R}, outside_max_pm={3:R}, distance_slope={4:R}", sign, expected, plateau, outside, slope);
                    Assert.AreEqual(expected, plateau, .04); Assert.IsTrue(outside < .04); Assert.AreEqual(1, slope, .003);
                    Assert.IsTrue(result.PhysicalAxis.Enabled && result.DocumentStages.StrainEnabled);
                    var strain = points.Where(p => p.DistanceMeters >= 5.65 && p.DistanceMeters <= 6.35).Select(p => p.CandidateStrainMicrostrain.Value).OrderBy(v => v).ToArray();
                    var expectedStrain = expected * 1000 / (1550 * .78);
                    Assert.AreEqual(expectedStrain, strain[strain.Length / 2], .04);
                    Console.WriteLine("DOCUMENT_STRAIN expected={0:R}, plateau={1:R}", expectedStrain, strain[strain.Length / 2]);
                }
            }
        }

        [TestMethod] public void DocumentPhysicalAxesPreserveLagQualityAndExposePositionAndStrain()
        {
            const int n = 16384; const double dt = 4e-8;
            var aux = Enumerable.Range(0, n).Select(i => Math.Cos(2 * Math.PI * 2e6 * i * dt)).ToArray();
            var random = new Random(29); var values = Enumerable.Range(0, n).Select(i => random.NextDouble() - .5).ToArray();
            var input = new IndependentOfdrInput { Auxiliary = Channel(aux, dt), SignalAuxiliary = Channel(aux, dt), Reference = Channel(values, dt), Signal = Channel(values, dt) };
            var c = new IndependentOfdrProcessingConfig { StageComparisonEnabled = true, UsePhysicalAxes = false, AllowFailedPhaseQcForDiagnostics = true,
                SegmentSize = 64, SegmentHop = 32, SegmentIfftZeroPadFactor = 8, MaxPreviewPoints = 128,
                StageComparison = new StageComparisonSettings { OutputSamples = 4096, Document = new DocumentStageSettings { Enabled = true, EdgeGuardMicroseconds = 0, PeakExclusionPm = .2, MinimumSeparation = 0, MaximumShiftPm = 4 } } };
            var engine = new StageComparisonEngine();
            using (var raw = engine.Run(input, c, CancellationToken.None))
            {
                c.UsePhysicalAxes = true; c.DistanceGeometryFactor = 2; c.DistanceOffsetMeters = 1.25; c.StrainScaleFactor = -2; c.StrainOffsetMicrostrain = 3;
                using (var result = engine.Run(input, c, CancellationToken.None))
                {
                    CollectionAssert.AreEqual(raw.SegmentPeakLags, result.SegmentPeakLags);
                    CollectionAssert.AreEqual(raw.DocumentStages.Points.Select(p => p.Status).ToArray(), result.DocumentStages.Points.Select(p => p.Status).ToArray());
                    Assert.AreEqual(raw.DocumentStages.DistancePerBin * 2, result.PhysicalAxis.DistanceMetersPerFftBin, 1e-12);
                    Assert.AreEqual(1.25, result.ReferenceSpectrum.XStart, 1e-12);
                    foreach (var p in result.DocumentStages.Points)
                    {
                        Assert.AreEqual((p.WindowStartMeters + p.WindowEndMeters) / 2, p.DistanceMeters, 1e-12);
                        Assert.AreEqual(3, p.CandidateStrainMicrostrain.Value, 1e-8);
                    }
                    var point = result.DocumentStages.Points[2]; var inspected = engine.Inspect(result, 2);
                    Assert.AreEqual(point.DistanceMeters, inspected.SegmentCenterDistanceMeters, 1e-12);
                    Assert.AreEqual(point.WindowStartMeters, inspected.ReferenceSegment.XStart, 1e-12);
                    Assert.AreEqual(point.CandidateStrainMicrostrain.Value, inspected.PeakStrainMicrostrain, 1e-12);
                    point.Status = "PHASE_QC_FAILED"; DocumentPhysicalAxis.Apply(result);
                    Assert.IsNull(point.AcceptedStrainMicrostrain); Assert.IsNotNull(point.CandidateStrainMicrostrain);
                    var cloned = JsonContractSerializer.Clone(result.DocumentStages);
                    Assert.AreEqual(point.CandidateStrainMicrostrain, cloned.Points[2].CandidateStrainMicrostrain);
                }
            }
            c.StrainCoefficient = 0;
            Assert.Throws<ArgumentException>(() => engine.Run(input, c, CancellationToken.None));
        }
    }
}
