using System;
using System.Linq;
using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;

namespace OFDR_Signal_Core.Tests
{
    [TestClass]
    public sealed class SweepTimeCropTests
    {
        [TestMethod]
        [DataRow(25000000)]
        [DataRow(50000000)]
        [DataRow(100000000)]
        [DataRow(500000000)]
        public void MillisecondsFollowHeaderSamplingRate(int rate)
        {
            var count = rate / 1000;
            var sweep = new SweepRange { StartIndex = 101, EndIndexExclusive = 101 + count };
            var result = SweepTimeCrop.Create(sweep, 1.0 / rate, Enumerable.Range(101, count).ToArray(), .15, .5);
            Assert.AreEqual(101 + rate * 3 / 20000, result.SourceStartIndex);
            Assert.AreEqual(rate / 2000, result.RetainedCount);
            Assert.AreEqual(result.SourceStartIndex - 101, result.StitchedStartIndex);
        }
        [TestMethod]
        public void CroppingUsesRawTimeAcrossRemovedIntervals()
        {
            var sweep = new SweepRange { StartIndex = 100, EndIndexExclusive = 200 };
            var indices = Enumerable.Range(100, 100).Where(i => i < 110 || i >= 120).Where(i => i != 140).ToArray();
            var result = SweepTimeCrop.Create(sweep, .001, indices, 15, 30);
            Assert.AreEqual(115, result.SourceStartIndex);
            Assert.AreEqual(145, result.SourceEndIndexExclusive);
            CollectionAssert.AreEqual(Enumerable.Range(120, 25).Where(i => i != 140).ToArray(),
                indices.Skip(result.StitchedStartIndex).Take(result.RetainedCount).ToArray());
        }
        [TestMethod]
        public void BlankLengthIncludesLastSurvivingSampleAndHonorsSweepOrigin()
        {
            var sweep = new SweepRange { StartIndex = 1000, EndIndexExclusive = 1100 };
            var result = SweepTimeCrop.Create(sweep, .001, Enumerable.Range(1000, 99).ToArray(), 20, null);
            Assert.AreEqual(1020, result.SourceStartIndex);
            Assert.AreEqual(1100, result.SourceEndIndexExclusive);
            Assert.AreEqual(79, result.RetainedCount);
        }
        [TestMethod]
        public void OffsetAndDurationRoundToNearestSample()
        {
            var sweep = new SweepRange { EndIndexExclusive = 100 };
            var result = SweepTimeCrop.Create(sweep, .001, Enumerable.Range(0, 100).ToArray(), 1.5, 4.5);
            Assert.AreEqual(2, result.SourceStartIndex);
            Assert.AreEqual(7, result.SourceEndIndexExclusive);
        }
        [TestMethod]
        [DataRow(-1, 5)]
        [DataRow(0, 0)]
        [DataRow(90, 11)]
        [DataRow(100, 4)]
        [DataRow(0, .01)]
        [DataRow(double.NaN, 10)]
        [DataRow(0, double.PositiveInfinity)]
        public void InvalidOrExcessiveWindowIsRejected(double offset, double length)
        {
            Assert.ThrowsExactly<ArgumentException>(() => SweepTimeCrop.Create(new SweepRange { EndIndexExclusive = 100 },
                .001, Enumerable.Range(0, 100).ToArray(), offset, length));
        }
        [TestMethod]
        public void AllRemovedOrTooFewSurvivorsIsRejected()
        {
            var sweep = new SweepRange { EndIndexExclusive = 100 };
            Assert.ThrowsExactly<ArgumentException>(() => SweepTimeCrop.Create(sweep, .001, new[] { 0, 1, 2, 90, 91 }, 10, 30));
            Assert.ThrowsExactly<ArgumentException>(() => SweepTimeCrop.Create(sweep, .001, new[] { 0, 1, 2, 90, 91 }, 0, 30));
        }
        [TestMethod]
        public void LargeSampleIndicesSurviveUiTimeRoundTripWithoutExtraSample()
        {
            foreach (var rate in new[] { 25000000, 50000000, 100000000, 500000000, 123456789 })
            {
                var dt = 1.0 / rate;
                foreach (var index in new[] { 0, 16806199, 17032703, 19999999, 2000000001 })
                {
                    var text = (index * dt * 1000).ToString("G17", CultureInfo.InvariantCulture);
                    var seconds = double.Parse(text, CultureInfo.InvariantCulture) / 1000;
                    Assert.AreEqual(index, AuxiliaryInspectionSettings.SampleIndexAtOrAfter(seconds, dt));
                    Assert.AreEqual(index + 1, AuxiliaryInspectionSettings.SampleIndexAtOrAfter((index + .01) * dt, dt));
                }
            }
        }
    }
}
