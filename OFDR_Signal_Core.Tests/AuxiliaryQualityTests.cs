using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;

namespace OFDR_Signal_Core.Tests
{
    [TestClass]
    public sealed class AuxiliaryQualityTests
    {
        private string folder;
        [TestInitialize] public void Initialize() { folder = Path.Combine(Path.GetTempPath(), "OFDR_AuxQuality_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); }
        [TestCleanup] public void Cleanup() { Directory.Delete(folder, true); }
        private ModeHopRecording Recording(double[] auxiliary, double[] trigger = null)
        {
            var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".csv");
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("Sample Interval,0.001\nRecord Length," + auxiliary.Length + "\nTIME,CH2,CH3");
                for (var i = 0; i < auxiliary.Length; i++) writer.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0:G17},{1:G17},{2:G17}", i * 0.001, trigger == null ? 0 : trigger[i], auxiliary[i]));
            }
            return new TekScopeCsvReader().Read(path, CancellationToken.None);
        }
        private StitchedAuxiliarySignal Stitch(ModeHopRecording recording)
        {
            var excluded = new ModeHopPreviewEngine().Detect(recording, new ModeHopSettings(), CancellationToken.None);
            return new AuxiliaryQualityEngine().Stitch(recording, excluded, CancellationToken.None);
        }
        [TestMethod]
        public void StitchPreservesSamplesSourceIndicesAndEveryJoinWithoutCorrection()
        {
            var recording = Recording(Enumerable.Range(0, 9).Select(i => (double)i).ToArray(), new[] { 3.4, 0, 0, 3.4, 0, 0, 3.4, 0, 0 });
            var stitched = Stitch(recording);
            CollectionAssert.AreEqual(new[] { 1, 2, 4, 5, 7, 8 }, stitched.OriginalIndices);
            CollectionAssert.AreEqual(new[] { 1.0, 2, 4, 5, 7, 8 }, stitched.Values);
            CollectionAssert.AreEqual(new[] { 2, 4 }, stitched.JoinIndices);
            Assert.AreEqual(0.006, stitched.DurationSeconds, 1e-14);
            Assert.AreEqual(9, recording.SampleCount); Assert.AreEqual(3.0, recording.Auxiliary[3]);
        }
        [TestMethod]
        public void AllExcludedIsRejectedAndNoExclusionsKeepIdentity()
        {
            var good = Recording(new[] { 0.0, 1, 2, 3 });
            CollectionAssert.AreEqual(good.Auxiliary, Stitch(good).Values);
            Assert.AreEqual(0, Stitch(good).JoinIndices.Length);
            var bad = Recording(new[] { 0.0, 1, 2, 3 }, new[] { 3.4, 3.4, 3.4, 3.4 });
            Assert.ThrowsExactly<InvalidOperationException>(() => Stitch(bad));
        }
        [TestMethod]
        public void FlatPhaseIsRejectedEvenThoughGeneratedGridIsLinear()
        {
            var stitched = Stitch(Recording(new double[128]));
            var result = new AuxiliaryQualityEngine().Analyze(stitched, new AuxiliaryInspectionSettings(), CancellationToken.None);
            Assert.AreEqual("REJECT_PHASE_QC", result.Metrics.Decision);
            Assert.AreEqual(0, result.Metrics.UniformGridStepRadians);
            Assert.AreEqual(1.0, result.Metrics.Phase.NonmonotonicRatio);
            Assert.IsNull(result.Metrics.ResampledPhaseResidualRmsRadians);
        }
        [TestMethod]
        public void ChirpedAuxiliarySharesProductionPhaseMetricsAndChecksResampledPhase()
        {
            const int n = 8192;
            var values = Enumerable.Range(0, n).Select(i => Math.Cos(2 * Math.PI * 128 * i / n + 4 * Math.Sin(2 * Math.PI * i / n))).ToArray();
            var stitched = Stitch(Recording(values));
            var settings = new AuxiliaryInspectionSettings { MaximumPreviewPoints = 100 };
            var result = new AuxiliaryQualityEngine().Analyze(stitched, settings, CancellationToken.None);
            var production = new AuxKLinearizer(new FftEngine()).Linearize(values, values, values, 1e-5, CancellationToken.None);
            Assert.AreEqual("PASS_PHASE_QC", result.Metrics.Decision);
            Assert.AreEqual(production.Metrics.PhaseSpanRadians, result.Metrics.Phase.PhaseSpanRadians, 1e-10);
            Assert.AreEqual(production.Metrics.NonmonotonicRatio, result.Metrics.Phase.NonmonotonicRatio);
            Assert.IsNotNull(result.Metrics.ResampledPhaseResidualRmsRadians);
            Assert.IsLessThan(0.2, result.Metrics.ResampledPhaseResidualRmsRadians.Value);
            foreach (var plot in result.Plots) { Assert.IsLessThanOrEqualTo(100, plot.Primary.Values.Length); if (plot.Secondary != null) Assert.IsLessThanOrEqualTo(100, plot.Secondary.Values.Length); }
        }
        [TestMethod]
        public void SelectedRangeUsesCompressedSampleTimesAndOriginalIndexMapping()
        {
            var source = Recording(Enumerable.Range(0, 1000).Select(i => Math.Cos(i * 0.2)).ToArray(), Enumerable.Range(0, 1000).Select(i => i >= 10 && i < 20 ? 3.4 : 0).ToArray());
            var stitched = Stitch(source);
            var result = new AuxiliaryQualityEngine().Analyze(stitched, new AuxiliaryInspectionSettings { StartSeconds = 0.02, EndSeconds = 0.08 }, CancellationToken.None);
            Assert.AreEqual(20, result.Metrics.SelectedStartIndex); Assert.AreEqual(80, result.Metrics.SelectedEndIndexExclusive);
            Assert.AreEqual(30, result.Metrics.SourceStartIndex); Assert.AreEqual(89, result.Metrics.SourceEndIndexInclusive);
        }
        [TestMethod]
        public void InvalidRangeAndCancellationAreNotSilentlyAccepted()
        {
            var recording = Recording(new double[100]); var stitched = Stitch(recording); var engine = new AuxiliaryQualityEngine();
            Assert.ThrowsExactly<ArgumentException>(() => engine.Analyze(stitched, new AuxiliaryInspectionSettings { EndSeconds = 10 }, CancellationToken.None));
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                Assert.ThrowsExactly<OperationCanceledException>(() => engine.Stitch(recording, new ModeHopAnalysis(), cancel.Token));
                Assert.ThrowsExactly<OperationCanceledException>(() => engine.Analyze(stitched, new AuxiliaryInspectionSettings(), cancel.Token));
            }
        }
    }
}
