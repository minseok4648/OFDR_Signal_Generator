using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;

namespace OFDR_Signal_Core.Tests
{
    [TestClass]
    public sealed class NotebookTemperatureTests
    {
        [TestMethod]
        public void CircularLagHasNotebookSignAndFractionalPeak()
        {
            foreach (var lag in new[] { -17.25, 13.4 })
            {
                var correlation = new Complex[128];
                for (var i = 0; i < correlation.Length; i++)
                {
                    var x = i > 63 ? i - 128 : i;
                    correlation[i] = new Complex(1000 - (x - lag) * (x - lag), 0);
                }
                Assert.AreEqual(lag, NotebookTemperatureReproductionEngine.FindShift(correlation), 1e-10);
            }
        }

        [TestMethod]
        public void CircularPeakTieUsesFirstShiftedIndexLikeNumpy()
        {
            var correlation = Enumerable.Repeat(Complex.One, 128).ToArray();
            Assert.AreEqual(-64.0, NotebookTemperatureReproductionEngine.FindShift(correlation));
        }

        [TestMethod]
        public void HilbertInterpolationPreservesKnownLinearPhaseForOddAndEvenLengths()
        {
            foreach (var n in new[] { 1024, 1025 })
            {
                var aux = Enumerable.Range(0, n).Select(i => Math.Cos(2 * Math.PI * 32 * i / n)).ToArray();
                var signal = Enumerable.Range(0, n).Select(i => Math.Sin(2 * Math.PI * 4 * i / n)).ToArray();
                double first, last, reversed;
                var values = new NotebookTemperatureReproductionEngine().LinearizeLikeNotebook(aux, signal, 257,
                    CancellationToken.None, out first, out last, out reversed);
                Assert.AreEqual(2 * Math.PI * 32 * (n - 1) / n, last - first, 1e-9);
                Assert.AreEqual(0, reversed);
                for (var i = 0; i < values.Length; i++)
                {
                    var position = i * (n - 1.0) / (values.Length - 1);
                    var lo = Math.Min(n - 2, (int)position);
                    var expected = signal[lo] + (position - lo) * (signal[lo + 1] - signal[lo]);
                    Assert.AreEqual(expected, values[i], 1e-10);
                }
            }
        }

        [TestMethod]
        public void ProfileKeepsInvalidEdgePositionsAndZeroSelfLag()
        {
            var config = new NotebookTemperatureConfig { ResampledSamples = 128, SegmentSize = 16,
                SegmentStep = 4, ZeroPadFactor = 4, StopBin = 64 };
            var engine = new NotebookTemperatureReproductionEngine();
            var values = Enumerable.Range(0, 128).Select(i => Math.Sin(i * .731) + .3 * Math.Cos(i * 1.299)).ToArray();
            var spectrum = engine.BuildSpectrum(values);
            var result = engine.Compare(spectrum, spectrum, config, CancellationToken.None);
            Assert.AreEqual(16, result.Positions.Length);
            Assert.IsTrue(double.IsNaN(result.Shifts[0]));
            Assert.IsTrue(double.IsNaN(result.Shifts[1]));
            Assert.IsTrue(double.IsNaN(result.Shifts[15]));
            foreach (var lag in result.Shifts.Where(x => !double.IsNaN(x))) Assert.AreEqual(0, lag, 1e-9);
        }

        [TestMethod]
        public void ConfirmedRawPairRunsWithoutSecondTriggerCropAndInspectorMatchesLag()
        {
            var n = 8192;
            var aux = Enumerable.Range(0, n).Select(i => .05 + Math.Cos(2 * Math.PI * (30.0 * i / n + 3.0 * i * i / (n * (double)n)))).ToArray();
            var signal = Enumerable.Range(0, n).Select(i => Math.Sin(i * .137) + .4 * Math.Cos(i * .311)).ToArray();
            var input = new IndependentOfdrInput
            {
                Auxiliary = new OfdrChannelData { Values = aux }, SignalAuxiliary = new OfdrChannelData { Values = aux },
                Reference = new OfdrChannelData { Values = signal }, Signal = new OfdrChannelData { Values = signal }
            };
            var c = new IndependentOfdrProcessingConfig { NotebookReproduction = true, NotebookResampledSamples = 4096,
                SegmentSize = 128, SegmentHop = 32, SegmentIfftZeroPadFactor = 8, MaxPreviewPoints = 512,
                UsePhysicalAxes = false, AllowFailedPhaseQcForDiagnostics = true, NormalizeCrossCorrelation = false };
            var engine = new NotebookPreparedPairEngine();
            using (var result = engine.Run(input, c, CancellationToken.None, null))
            {
                Assert.AreEqual(n - 41, result.KLinearization.SampleCount);
                Assert.AreEqual(4096, result.SampleCount);
                Assert.IsFalse(result.PhysicalAxis.Enabled);
                Assert.IsTrue(result.IsDiagnosticAnalysis);
                Assert.IsTrue(result.NotebookProfile.Shifts.Where(v => !double.IsNaN(v)).All(v => Math.Abs(v) < 1e-8));
                Assert.IsTrue(result.KDiagnostics.UnwrappedPhase.Values.Length <= 512);
                var index = result.SegmentStarts.Length / 2;
                Assert.AreEqual(result.SegmentPeakLags[index], engine.Inspect(result, index).PeakLagBins, 1e-8);
            }
        }

        [TestMethod]
        public void NotebookPairCannotSilentlyPublishPhysicalStrainOrBypassDiagnosticFlag()
        {
            var engine = new NotebookPreparedPairEngine();
            Assert.ThrowsExactly<ArgumentException>(() => engine.Run(null, new IndependentOfdrProcessingConfig { NotebookReproduction = true }, CancellationToken.None, null));
            Assert.ThrowsExactly<ArgumentException>(() => engine.Run(null, new IndependentOfdrProcessingConfig { NotebookReproduction = true, AllowFailedPhaseQcForDiagnostics = true, UsePhysicalAxes = true }, CancellationToken.None, null));
        }

        [TestMethod]
        public void InvalidDimensionsAndNonfiniteInputsAreRejected()
        {
            Assert.ThrowsExactly<ArgumentException>(() => new NotebookTemperatureConfig { SegmentStep = 0 }.Validate());
            Assert.ThrowsExactly<ArgumentException>(() => new NotebookTemperatureConfig { MeasurementSliceStart = int.MaxValue }.Validate());
            double first = 0, last = 0, ratio = 0;
            Assert.ThrowsExactly<ArgumentException>(() => new NotebookTemperatureReproductionEngine().LinearizeLikeNotebook(
                new[] { 1.0, double.PositiveInfinity, 0, -1 }, new double[4], 4, CancellationToken.None, out first, out last, out ratio));
        }
    }
}
