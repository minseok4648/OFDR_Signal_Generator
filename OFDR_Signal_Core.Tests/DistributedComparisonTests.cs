using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OFDR_Signal_Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
namespace OFDR_Signal_Core.Tests
{
    [TestClass]
    public class DistributedComparisonTests
    {
        private static DistributedComparisonResult Fixture()
        {
            var r=new DistributedComparisonResult{Status="COMPLETED",Config=DistributedComparisonEngine.DefaultConfig()};
            foreach(var pair in new[]{"상온 반복","가열 비교"})foreach(var mode in new[]{"B","C"})
                r.Curves.Add(new DistributedCurve{Pair=pair,Condition=mode,Points=new List<StageQualityPoint>{new StageQualityPoint{DistanceMeters=1,CandidateShiftPm=double.NaN,PeakZncc=double.NaN,Status="INSUFFICIENT_VARIANCE_OR_OVERLAP"},new StageQualityPoint{DistanceMeters=2,CandidateShiftPm=10,PeakZncc=.2,Status="LOW_CORRELATION"}}});
            return r;
        }
        [TestMethod]public void InvalidCandidatesRemainMissingAndLowCorrelationIsNotAccepted()
        {var r=Fixture();DistributedComparisonEngine.Validate(r);Assert.IsTrue(double.IsNaN(r.Curves[0].Points[0].CandidateShiftPm));Assert.AreEqual(0,r.Curves[0].Accepted);Assert.AreEqual(.2,r.Curves[0].MedianZncc,1e-12);}
        [TestMethod]public void DuplicateOrNonmonotonicProfilesAreRejected()
        {var r=Fixture();r.Curves[1].Condition="B";Assert.ThrowsExactly<ArgumentException>(()=>DistributedComparisonEngine.Validate(r));r=Fixture();r.Curves[0].Points[1].DistanceMeters=.5;Assert.ThrowsExactly<ArgumentException>(()=>DistributedComparisonEngine.Validate(r));}
        [TestMethod]public void PartialRunAndInvalidCorrelationCannotLoadAsSuccess()
        {var r=Fixture();r.Status="FAILED";Assert.ThrowsExactly<ArgumentException>(()=>DistributedComparisonEngine.Validate(r));r=Fixture();r.Curves[0].Points[1].PeakZncc=4;Assert.ThrowsExactly<ArgumentException>(()=>DistributedComparisonEngine.Validate(r));}
        [TestMethod] public void ReferenceSignalPairCanSaveAndLoadWithoutInventedRepeatCapture()
        {
            var r=Fixture();r.PairOnly=true;r.Curves=r.Curves.Take(2).ToList();foreach(var curve in r.Curves)curve.Pair="Ref / Signal";
            DistributedComparisonEngine.Validate(r);
            var path=Path.GetTempFileName();
            try { JsonContractSerializer.Write(path,r);var loaded=DistributedComparisonEngine.Load(path);Assert.IsTrue(loaded.PairOnly);Assert.AreEqual(2,loaded.Curves.Count);Assert.AreEqual("Ref / Signal",loaded.Curves[1].Pair); }
            finally {File.Delete(path);}
        }
        [TestMethod] public void PairOnlyRejectsMissingOrMislabeledConditions()
        {
            var r=Fixture();r.PairOnly=true;
            Assert.ThrowsExactly<ArgumentException>(()=>DistributedComparisonEngine.Validate(r));
            r.Curves=r.Curves.Take(2).ToList();
            Assert.ThrowsExactly<ArgumentException>(()=>DistributedComparisonEngine.Validate(r));
            foreach(var curve in r.Curves)curve.Pair="Ref / Signal";r.Curves[1].Condition="B";
            Assert.ThrowsExactly<ArgumentException>(()=>DistributedComparisonEngine.Validate(r));
        }
    }
}
