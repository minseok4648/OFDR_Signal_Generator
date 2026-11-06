using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;

namespace OFDR_Signal_Core.Tests
{
    [TestClass]
    public sealed class AcquisitionDiagnosticsTests
    {
        private static double[] Tone(int count, double rate, double frequency) => Enumerable.Range(0, count).Select(i => Math.Cos(2 * Math.PI * frequency * i / rate)).ToArray();
        private static AcquisitionDiagnosticBlock Block(int n) => new AcquisitionDiagnosticBlock { SourcePath = "diagnostic-fixture", StartIndex = 1000, SampleIntervalSeconds = 1e-6,
            SourceIndices = Enumerable.Range(1000, n).ToArray(), Auxiliary = Enumerable.Range(0, n).Select(i => (double)i).ToArray(), Measurement = Enumerable.Range(0, n).Select(i => 2.0 * i).ToArray(), Trigger = new double[n] };

        [TestMethod] public void FilteringPreservesSixMHzAtTwentyMSAndRemovesItBeforeTwoPointFiveMS()
        {
            var values = Tone(65536, 50e6, 6e6); var diagnostics = new AcquisitionDiagnostics();
            var twenty = AcquisitionDiagnostics.Resample(values, 50e6, 20e6, CancellationToken.None);
            var low = AcquisitionDiagnostics.Resample(values, 50e6, 2.5e6, CancellationToken.None);
            var metric = diagnostics.Analyze(twenty, 1 / 20e6, null, CancellationToken.None);
            Assert.IsTrue(Math.Abs(metric.Values["spectral_peak_hz"] / 6e6 - 1) < .001);
            Assert.IsTrue(metric.Values["interior_nonmonotonic_ratio"] < 1e-4);
            Assert.IsTrue(twenty.Select(v => v * v).Average() > .45);
            Assert.IsTrue(low.Select(v => v * v).Average() < 1e-6);
            Assert.AreEqual(65536, values.Length);
        }

        [TestMethod] public void AliasedPureToneCanPassMonotonicQcButHasTheWrongFrequency()
        {
            var original = Tone(50000, 50e6, 6e6); var alias = original.Where((v, i) => i % 20 == 0).ToArray(); var diagnostics = new AcquisitionDiagnostics();
            var source = diagnostics.Analyze(original, 1 / 50e6, null, CancellationToken.None);
            var down = diagnostics.Analyze(alias, 1 / 2.5e6, null, CancellationToken.None);
            Assert.IsTrue(down.Values["interior_nonmonotonic_ratio"] < 1e-5);
            Assert.IsTrue(Math.Abs(down.Values["phase_average_hz"] / 1e6 - 1) < .001);
            Assert.IsTrue(source.Values["phase_average_hz"] > 5.9e6);
        }

        [TestMethod] public void TwentyMSResamplingRejectsAboveNyquistContentAtCorrectFractionalTimes()
        {
            var a = Tone(32768, 50e6, 6e6); var b = Tone(32768, 50e6, 12e6); var mixed = a.Zip(b, (x, y) => x + y).ToArray();
            var converted = AcquisitionDiagnostics.Resample(mixed, 50e6, 20e6, CancellationToken.None);
            var error = converted.Select((v, i) => v - Math.Cos(2 * Math.PI * 6e6 * (160 + 2.5 * i) / 50e6)).Select(v => v * v).Average();
            Assert.IsTrue(error < 1e-6);
            var comparison = new AcquisitionDiagnostics().CompareResampledPhase(a, AcquisitionDiagnostics.Resample(a, 50e6, 20e6, CancellationToken.None), 50e6, 20e6, CancellationToken.None);
            Assert.IsTrue(comparison["phase_error_rms_rad"] < .001);
            Assert.AreEqual(0.0, comparison["phase_comparison_slope_removed"]);
        }

        [TestMethod] public void ExclusionPreservesPairedIndicesPreviousTailAndInfinityMask()
        {
            var block = Block(64); block.PreviousHighEndIndex = 995; block.Trigger[10] = block.Trigger[11] = 3; block.Trigger[30] = 1.8; block.Measurement[24] = double.PositiveInfinity;
            var result = AcquisitionDiagnostics.Exclude(block, 10e-6, false, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { 1005, 1006, 1007, 1008, 1009, 1022, 1023 }.Concat(Enumerable.Range(1025, 39)).ToArray(), result.SourceIndices);
            for (var i = 0; i < result.Auxiliary.Length; i++) { Assert.AreEqual(result.SourceIndices[i] - 1000, result.Auxiliary[i]); Assert.AreEqual(2 * result.Auxiliary[i], result.Measurement[i]); }
            CollectionAssert.AreEqual(new[] { 5, 7 }, result.Joins);
            var continuous = AcquisitionDiagnostics.Exclude(block, 10e-6, true, CancellationToken.None);
            Assert.AreEqual(1025, continuous.SourceIndices[0]); Assert.AreEqual(0, continuous.Joins.Length);
        }

        [TestMethod] public void DiagnosticPairUsesProductionPipelineAndPathScaling()
        {
            var block = Block(32768); block.StartIndex = 0; block.SourceIndices = Enumerable.Range(0, 32768).ToArray();
            block.Auxiliary = Tone(32768, 1e6, 100000);
            block.Measurement = Enumerable.Range(0, 32768).Select(i => Math.Cos(.01 * i) + .3 * Math.Cos(.04 * i + .3)).ToArray();
            var diagnostics = new AcquisitionDiagnostics();
            var a = diagnostics.RunPair(block, block, 100, true, false, CancellationToken.None);
            var b = diagnostics.RunPair(block, block, 200, true, false, CancellationToken.None);
            Assert.AreEqual(2 * a.Metrics["distance_per_bin_m"], b.Metrics["distance_per_bin_m"], 1e-12);
            Assert.AreEqual(.5 * a.Metrics["spectral_shift_per_lag_ghz"], b.Metrics["spectral_shift_per_lag_ghz"], 1e-12);
            Assert.IsTrue(a.SpectralShiftGHz.All(v => Math.Abs(v) < 1e-6));
            Assert.IsTrue(a.CorrelationPeaks.All(v => v > .9999));
        }

        [TestMethod] public void CsvWindowReaderKeepsDuplicateTimesAndHashesOriginalFile()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
            try
            {
                var text = new StringBuilder("Sample Interval,1e-6\nRecord Length,4096\nTIME,CH1,CH2,CH3,CH4\n");
                for (var i = 0; i < 4096; i++) text.AppendFormat(CultureInfo.InvariantCulture, "0,0,{0},{1},{2}\n", (i >= 500 && i < 1000) || (i >= 2500 && i < 3000) ? 3 : 0, i, 2 * i);
                File.WriteAllText(path, text.ToString());
                var read = new AcquisitionDiagnosticReader().ReadCsv(path, 0, 4096, 2, 1024, CancellationToken.None);
                Assert.AreEqual(2048, read.NumericSamples); Assert.AreEqual(64, read.Sha256.Length); Assert.AreEqual(2, read.SourceRanges.Count);
                foreach (var block in read.Blocks) for (var i = 0; i < block.Auxiliary.Length; i++) { Assert.AreEqual(block.SourceIndices[i], block.Auxiliary[i]); Assert.AreEqual(2 * block.Auxiliary[i], block.Measurement[i]); }
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [TestMethod] public void TinyGaugeDoesNotReportSentinelAsAResolvedSecondaryPeak()
        {
            var block = Block(8192); block.Auxiliary = Tone(8192, 1e6, 100000);
            block.Measurement = Tone(8192, 1e6, 12000);
            var result = new AcquisitionDiagnostics().RunPair(block, block, 200, true, true, CancellationToken.None);
            Assert.AreEqual(4, result.Config.SegmentSize);
            Assert.AreEqual(0.0, result.Metrics["peak_separation_available_segments"]);
            Assert.IsTrue(result.PeakSeparationAvailable.All(v => !v));
            Assert.IsTrue(result.PeakSeparation.All(v => v == 0));
            Assert.IsTrue(result.CorrelationPeaks.All(v => v > .9999));
        }

        [TestMethod] public void BudgetAndCancellationAreEnforcedBeforeLargeWork()
        {
            Assert.ThrowsExactly<ArgumentException>(() => new AcquisitionDiagnosticReader().ReadCsv("unused.csv", 0, 1000000, 4, 81920, CancellationToken.None));
            var cancel = new CancellationToken(true);
            Assert.ThrowsExactly<OperationCanceledException>(() => new AcquisitionDiagnostics().Analyze(Tone(1024, 1e6, 100000), 1e-6, null, cancel));
            Assert.ThrowsExactly<OperationCanceledException>(() => AcquisitionDiagnostics.Resample(Tone(1024, 50e6, 6e6), 50e6, 20e6, cancel));
        }

        [TestMethod] public void PartialRangesDoNotRequireTriggersOrDistinctCsvTimes()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
            try
            {
                var text = new StringBuilder("Sample Interval,2e-9\nRecord Length,4096\nTIME,CH1,CH2,CH3,CH4\n");
                for (var i = 0; i < 4096; i++) text.AppendFormat(CultureInfo.InvariantCulture, "-0.00092,0,0,{0},{1}\n", i, i == 700 ? "inf" : (2 * i).ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(path, text.ToString()); var reader = new AcquisitionDiagnosticReader();
                var result = reader.ReadCsvRanges(path, new[] { 512, 2500 }, 1024, CancellationToken.None);
                Assert.AreEqual(2048, result.NumericSamples); Assert.AreEqual(2048, result.TriggerRowsScanned); Assert.AreEqual(64, result.Sha256.Length);
                Assert.AreEqual(512, result.Blocks[0].StartIndex); Assert.AreEqual(0.0, result.Blocks[0].SweepTrigger[0]);
                Assert.IsTrue(double.IsPositiveInfinity(result.Blocks[0].Measurement[188]));
                Assert.AreEqual(3523.0, result.Blocks[1].Auxiliary.Last()); Assert.AreEqual(7046.0, result.Blocks[1].Measurement.Last());
                Assert.ThrowsExactly<ArgumentException>(() => reader.ReadCsvRanges(path, new[] { 0, 100 }, 1024, CancellationToken.None));
                Assert.ThrowsExactly<ArgumentException>(() => reader.ReadCsvRanges(path, new[] { 0, 100000, 200000 }, 100000, CancellationToken.None));
                Assert.ThrowsExactly<OperationCanceledException>(() => reader.ReadCsvRanges(path, new[] { 0 }, 1024, new CancellationToken(true)));
                File.WriteAllText(path, text.ToString().Substring(0, text.ToString().IndexOf("-0.00092,0,0,3000,", StringComparison.Ordinal)));
                Assert.ThrowsExactly<InvalidDataException>(() => reader.ReadCsvRanges(path, new[] { 2500 }, 1024, CancellationToken.None));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [TestMethod] public void FiveHundredMSControlsPreserveCarrierAndSampleCoordinates()
        {
            const int radius = 1600; var carrier = Tone(81920, 500e6, 6e6); var noise = Tone(81920, 500e6, 80e6);
            var mixed = carrier.Zip(noise, (x, y) => x + .3 * y).ToArray(); var diagnostic = new AcquisitionDiagnostics();
            var a = AcquisitionDiagnostics.Resample(mixed, 500e6, 50e6, CancellationToken.None, radius);
            var b = AcquisitionDiagnostics.Resample(mixed, 500e6, 20e6, CancellationToken.None, radius);
            var expected = b.Select((v, i) => Math.Pow(v - Math.Cos(2 * Math.PI * 6e6 * (radius + 25 * i) / 500e6), 2)).Average();
            Assert.IsTrue(expected < 1e-6); Assert.AreEqual(7872, a.Length); Assert.AreEqual(3149, b.Length);
            var phase = diagnostic.CompareResampledPhase(a, b, 50e6, 20e6, CancellationToken.None, 0);
            Assert.IsTrue(phase["phase_error_rms_rad"] < .001);
            Assert.IsTrue(diagnostic.Analyze(b, 1 / 20e6, null, CancellationToken.None).Values["interior_nonmonotonic_ratio"] < 1e-5);
        }

        [TestMethod] public void DiagnosticReaderAndExclusionHandleInfinityInEveryChannel()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
            try
            {
                var text = new StringBuilder("Sample Interval,1e-6\nRecord Length,1024\nTIME,CH1,CH2,CH3,CH4\n");
                for (var i = 0; i < 1024; i++) text.AppendFormat(CultureInfo.InvariantCulture, "0,{0},{1},{2},{3}\n",
                    i == 20 ? "inf" : "0", i == 21 ? "+Infinity" : "0", i == 22 ? "-INF" : i.ToString(CultureInfo.InvariantCulture), i == 23 || i == 20 ? "-inf" : (2 * i).ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(path, text.ToString());
                var block = new AcquisitionDiagnosticReader().ReadCsvRanges(path, new[] { 0 }, 1024, CancellationToken.None).Blocks.Single();
                var finite = AcquisitionDiagnostics.Exclude(block, .0001, false, CancellationToken.None);
                Assert.AreEqual(1020, finite.Auxiliary.Length); Assert.IsFalse(finite.SourceIndices.Any(i => i >= 20 && i <= 23));
                Assert.IsTrue(finite.Auxiliary.All(ModeHopSettings.IsFinite) && finite.Measurement.All(ModeHopSettings.IsFinite) && finite.SweepTrigger.All(ModeHopSettings.IsFinite));
                for (var i = 0; i < finite.SourceIndices.Length; i++) Assert.AreEqual(finite.SourceIndices[i] * 2.0, finite.Measurement[i]);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [TestMethod] public void CsvDiagnosticRejectsMissingChannelInvalidAuxiliaryAndIncompleteWindows()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
            try
            {
                var reader = new AcquisitionDiagnosticReader();
                File.WriteAllText(path, "Sample Interval,1e-6\nRecord Length,4096\nTIME,CH1,CH2,CH3\n0,0,0,0\n");
                Assert.ThrowsExactly<InvalidDataException>(() => reader.ReadCsv(path, 0, 4096, 1, 1024, CancellationToken.None));
                var text = new StringBuilder("Sample Interval,1e-6\nRecord Length,4096\nTIME,CH1,CH2,CH3,CH4\n");
                for (var i = 0; i < 1700; i++) text.AppendFormat(CultureInfo.InvariantCulture, "0,0,{0},{1},0\n", i >= 500 && i < 1000 ? 3 : 0, i == 600 ? "NaN" : "0");
                File.WriteAllText(path, text.ToString());
                Assert.ThrowsExactly<InvalidDataException>(() => reader.ReadCsv(path, 0, 4096, 1, 1024, CancellationToken.None));
                File.WriteAllText(path, text.ToString().Replace("NaN", "0"));
                Assert.ThrowsExactly<InvalidDataException>(() => reader.ReadCsv(path, 0, 4096, 2, 1024, CancellationToken.None));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
