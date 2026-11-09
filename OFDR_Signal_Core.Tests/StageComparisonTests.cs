using System;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;

namespace OFDR_Signal_Core.Tests
{
    [TestClass]
    public sealed class StageComparisonTests
    {
        private static IndependentOfdrInput Input(bool self = false)
        {
            const int n = 8192;
            Func<double, double[]> aux = cycles => Enumerable.Range(0, n).Select(i => .12 + Math.Cos(2 * Math.PI * (cycles * i / n + 3.0 * i * i / (n * (double)n)))).ToArray();
            var measurement = Enumerable.Range(0, n).Select(i => .17 + Math.Sin(i * .137) + .4 * Math.Cos(i * .311)).ToArray();
            return new IndependentOfdrInput { Auxiliary = new OfdrChannelData { Values = aux(70) }, SignalAuxiliary = new OfdrChannelData { Values = aux(self ? 70 : 75) },
                Reference = new OfdrChannelData { Values = measurement }, Signal = new OfdrChannelData { Values = self ? measurement : measurement.Select((v, i) => v + .03 * Math.Sin(i * .4)).ToArray() } };
        }
        private static IndependentOfdrProcessingConfig Config() => new IndependentOfdrProcessingConfig
        {
            StageComparisonEnabled = true, AllowFailedPhaseQcForDiagnostics = true, UsePhysicalAxes = false, NormalizeCrossCorrelation = false,
            SegmentSize = 128, SegmentHop = 32, SegmentIfftZeroPadFactor = 8, MaxPreviewPoints = 512,
            StageComparison = new StageComparisonSettings { RemoveAuxiliaryMean = false, PhaseOrder = AuxiliaryPhaseOrder.StablePhaseSort,
                Grid = RelativePhaseGrid.IndependentEndpoints, WindowPlacement = DiagnosticWindowPlacement.GlobalSignal,
                RemoveMeasurementMean = true, CenterLocalMagnitudes = false, OutputSamples = 4096, AuxiliaryOffset = 40, MeasurementOffset = 1 }
        };
        [TestMethod]
        public void NotebookStageCombinationReproducesHistoricalEngine()
        {
            var input = Input(); var c = Config(); var historical = JsonContractSerializer.Clone(c);
            historical.StageComparisonEnabled = false; historical.NotebookReproduction = true; historical.NotebookResampledSamples = 4096;
            using (var expected = new NotebookPreparedPairEngine().Run(input, historical, CancellationToken.None, null))
            using (var actual = new StageComparisonEngine().Run(input, c, CancellationToken.None))
            {
                Assert.AreEqual(expected.KLinearization.PhaseSpanRadians, actual.KLinearization.PhaseSpanRadians, 1e-9);
                for (var i = 0; i < expected.NotebookProfile.Shifts.Length; i++)
                {
                    Assert.AreEqual(double.IsNaN(expected.NotebookProfile.Shifts[i]), double.IsNaN(actual.NotebookProfile.Shifts[i]));
                    if (!double.IsNaN(expected.NotebookProfile.Shifts[i])) Assert.AreEqual(expected.NotebookProfile.Shifts[i], actual.NotebookProfile.Shifts[i], 1e-7);
                }
            }
        }
        [TestMethod]
        public void CommonGridHasSameSpanAndIndependentGridRetainsBothSpans()
        {
            var input = Input(); var c = Config(); var e = new StageComparisonEngine();
            using (var r = e.Prepare(input.Auxiliary.Values, input.Reference.Values, c.StageComparison, 512, CancellationToken.None))
            using (var s = e.Prepare(input.SignalAuxiliary.Values, input.Signal.Values, c.StageComparison, 512, CancellationToken.None))
            {
                foreach (var grid in new[] { RelativePhaseGrid.CommonSpan, RelativePhaseGrid.CommonSpanTwoPass, RelativePhaseGrid.IndependentEndpoints })
                {
                    c.StageComparison.Grid = grid;
                    using (var result = e.RunPrepared(input, c, r, s, CancellationToken.None))
                    {
                        var rs = result.PerformanceMetrics.Single(m => m.Name == "reference_grid_span").Value;
                        var ss = result.PerformanceMetrics.Single(m => m.Name == "signal_grid_span").Value;
                        if (grid == RelativePhaseGrid.IndependentEndpoints) Assert.IsTrue(Math.Abs(rs - ss) > 1);
                        else Assert.AreEqual(rs, ss, 1e-12);
                        Assert.IsFalse(result.PhysicalAxis.Enabled); Assert.IsNull(result.StrainProfile);
                    }
                }
            }
        }
        [TestMethod]
        public void AllWindowPlacementsKeepSelfLagZeroAndInspectorMatches()
        {
            var input = Input(true); var c = Config(); c.StageComparison.RemoveAuxiliaryMean = true; c.StageComparison.PhaseOrder = AuxiliaryPhaseOrder.IncreasingAnchors;
            c.StageComparison.CenterLocalMagnitudes = true; c.NormalizeCrossCorrelation = true; c.StageComparison.MaximumLagSamples = 80;
            var e = new StageComparisonEngine();
            using (var r = e.Prepare(input.Auxiliary.Values, input.Reference.Values, c.StageComparison, 512, CancellationToken.None))
            {
                foreach (DiagnosticWindowPlacement window in Enum.GetValues(typeof(DiagnosticWindowPlacement)))
                {
                    c.StageComparison.WindowPlacement = window;
                    using (var result = e.RunPrepared(input, c, r, r, CancellationToken.None))
                    {
                        Assert.IsTrue(result.SegmentPeakLags.All(v => Math.Abs(v) < 1e-6));
                        var index = result.SegmentStarts.Length / 2;
                        Assert.AreEqual(result.SegmentPeakLags[index], e.Inspect(result, index).PeakLagBins, 1e-9);
                        Assert.IsTrue(result.KDiagnostics.UnwrappedPhase.Values.Length <= 512 && result.SelectedSegment.SignalIfft.Values.Length <= 512);
                        Assert.AreEqual(8192 - 41, result.KLinearization.SampleCount);
                    }
                }
            }
        }
        [TestMethod]
        public void DiagnosticsCannotPublishPhysicalAxesOrIgnoreInvalidSettingsAndCancellation()
        {
            var c = Config(); var e = new StageComparisonEngine(); var input = Input();
            c.UsePhysicalAxes = true; Assert.ThrowsExactly<ArgumentException>(() => e.Run(input, c, CancellationToken.None));
            c.UsePhysicalAxes = false; c.StageComparison.Grid = (RelativePhaseGrid)99; Assert.ThrowsExactly<ArgumentException>(() => e.Run(input, c, CancellationToken.None));
            c.StageComparison.Grid = RelativePhaseGrid.CommonSpan;
            Assert.ThrowsExactly<OperationCanceledException>(() => e.Run(input, c, new CancellationToken(true)));
            input.Auxiliary.Values[100] = double.NaN; Assert.ThrowsExactly<ArgumentException>(() => e.Run(input, c, CancellationToken.None));
        }
        [TestMethod]
        public void StageConfigRoundTripAndDefaultsKeepLegacyMode()
        {
            Assert.IsFalse(new IndependentOfdrProcessingConfig().StageComparisonEnabled);
            var c = Config(); c.StageComparison.Grid = RelativePhaseGrid.CommonSpanTwoPass;
            var copy = JsonContractSerializer.Clone(c);
            Assert.IsTrue(copy.StageComparisonEnabled); Assert.AreEqual(c.StageComparison.Description, copy.StageComparison.Description);
            Assert.AreNotSame(c.StageComparison, copy.StageComparison);
        }
    }
}
