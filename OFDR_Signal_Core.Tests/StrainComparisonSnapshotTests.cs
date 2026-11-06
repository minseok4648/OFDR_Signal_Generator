using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Tests
{
    [TestClass]
    public sealed class StrainComparisonSnapshotTests
    {
        private static IndependentOfdrProcessingResult Result(double[] distance, double[] lag) => new IndependentOfdrProcessingResult
        {
            ReferenceId = "Ref A", SignalId = "Ref B", AuxiliaryId = "aux A", SignalAuxiliaryId = "aux B",
            PhysicalAxis = new PhysicalAxisCalibrationResult { Enabled = true, StrainMicrostrainPerLag = 2, DistanceMetersPerFftBin = 0.01 },
            Config = new IndependentOfdrProcessingConfig { StrainOffsetMicrostrain = 3 },
            SegmentCenterDistancesMeters = distance, SegmentPeakLags = lag,
            IsDiagnosticAnalysis = true, InputPhaseQcPassed = false, PhaseQcSummary = "REJECT_PHASE_QC"
        };
        [TestMethod] public void CapturedStrainAndCoordinatesSurviveSourceMutationAndDisposal()
        {
            var result = Result(new[] { 1.0, 2.5, 7.0 }, new[] { 0.0, 1.0, -2.0 });
            var snapshot = StrainComparisonSnapshot.Capture(result, DateTime.UtcNow);
            result.ReferenceId = "changed"; result.SegmentPeakLags[0] = 999; result.SegmentCenterDistancesMeters[1] = 999;
            result.Config.StrainOffsetMicrostrain = 999; result.PhysicalAxis.StrainMicrostrainPerLag = 999; result.Dispose();
            CollectionAssert.AreEqual(new[] { 1.0, 2.5, 7.0 }, snapshot.DistancesMeters);
            CollectionAssert.AreEqual(new[] { 3.0, 5.0, -1.0 }, snapshot.StrainMicrostrain);
            Assert.AreEqual("Ref A", snapshot.ReferenceId); Assert.AreEqual("Ref B", snapshot.SignalId);
            Assert.IsTrue(snapshot.IsDiagnosticAnalysis); Assert.IsFalse(snapshot.InputPhaseQcPassed);
            Assert.AreEqual("REJECT_PHASE_QC", snapshot.PhaseQcSummary); Assert.AreEqual(2, snapshot.StrainMicrostrainPerLag);
        }
        [TestMethod] public void LargeProfilesKeepExtremaAtActualDistancesWithinTenThousandPoints()
        {
            var x = Enumerable.Range(0, 40001).Select(i => 3.0 + i * 0.0001).ToArray(); var y = new double[x.Length];
            y[1234] = 100; y[19876] = -50;
            var result = Result(x, y); var snapshot = StrainComparisonSnapshot.Capture(result, DateTime.UtcNow);
            Assert.IsTrue(snapshot.DistancesMeters.Length <= 10000); Assert.AreEqual(x.Length, snapshot.SourceSegmentCount);
            var peak = Array.IndexOf(snapshot.StrainMicrostrain, 203); var trough = Array.IndexOf(snapshot.StrainMicrostrain, -97);
            Assert.IsTrue(peak >= 0 && trough >= 0); Assert.AreEqual(x[1234], snapshot.DistancesMeters[peak]); Assert.AreEqual(x[19876], snapshot.DistancesMeters[trough]);
            for (var i = 1; i < snapshot.DistancesMeters.Length; i++) Assert.IsTrue(snapshot.DistancesMeters[i] > snapshot.DistancesMeters[i - 1]);
        }
        [TestMethod] public void NonPhysicalIncompleteAndInvalidProfilesAreRejected()
        {
            Assert.IsFalse(StrainComparisonSnapshot.CanCapture(null));
            var result = Result(new[] { 1.0, 2.0 }, new[] { 0.0, 0.0 }); result.PhysicalAxis.Enabled = false;
            Assert.Throws<InvalidOperationException>(() => StrainComparisonSnapshot.Capture(result, DateTime.UtcNow));
            result.PhysicalAxis.Enabled = true; result.SegmentPeakLags[1] = double.NaN;
            Assert.Throws<InvalidOperationException>(() => StrainComparisonSnapshot.Capture(result, DateTime.UtcNow));
            result.SegmentPeakLags[1] = 0; result.SegmentCenterDistancesMeters[1] = 0;
            Assert.Throws<InvalidOperationException>(() => StrainComparisonSnapshot.Capture(result, DateTime.UtcNow));
            result.SegmentCenterDistancesMeters[1] = 2;
            Assert.Throws<ArgumentOutOfRangeException>(() => StrainComparisonSnapshot.Capture(result, DateTime.UtcNow, 10001));
        }
    }
}
