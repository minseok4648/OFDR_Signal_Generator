using System;
using System.Linq;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;

namespace OFDR_Signal_Core.Tests
{
    [TestClass]
    public sealed class FftSpectrumViewportTests
    {
        private static IndependentOfdrProcessingResult Fixture(int bins, bool fullSpectrum = false)
        {
            return new IndependentOfdrProcessingResult
            {
                SampleCount = bins * 2, Config = new IndependentOfdrProcessingConfig(),
                SignalSpectrumValues = Enumerable.Repeat(new Complex(.01, 0), fullSpectrum ? bins * 2 : bins).ToArray(),
                ReferenceSpectrumValues = Enumerable.Repeat(new Complex(.02, 0), fullSpectrum ? bins * 2 : bins).ToArray(),
                SignalSpectrum = new PreviewSeries { XStart = 3, XEnd = 3 + (bins - 1) * .001, XAxisTitle = "거리 (m)" }
            };
        }

        [TestMethod] public void NarrowPeaksKeepOriginalBinsAndZoomRecoversEveryBin()
        {
            using (var result = Fixture(100001))
            {
                result.SignalSpectrumValues[17431] = 10;
                result.ReferenceSpectrumValues[17435] = 20;
                var source = new FftSpectrumViewport(result);
                var overview = source.Read(0, 100000, 200);
                Assert.IsTrue(overview.Bins.Length <= 200 && overview.IsReduced);
                CollectionAssert.Contains(overview.Bins, 17431); CollectionAssert.Contains(overview.Bins, 17435);
                Assert.AreEqual(20.431, source.XAt(17431), 1e-10);
                var detail = source.Read(17420, 17450, 200);
                CollectionAssert.AreEqual(Enumerable.Range(17420, 31).ToArray(), detail.Bins);
                Assert.IsFalse(detail.IsReduced);
                Assert.AreEqual(0, detail.Signal[11], 1e-10); Assert.AreEqual(0, detail.Reference[15], 1e-10);
                Assert.AreEqual(-60, detail.Signal[0], 1e-10);
            }
        }
        [TestMethod] public void ViewportDoesNotRenormalizeOrModifySpectra()
        {
            using (var result = Fixture(1000, true))
            {
                result.SignalSpectrumValues[900] = 100;
                result.SignalSpectrumValues[100] = 10;
                result.SignalSpectrumValues[1500] = 1000000; // negative half must not normalize the positive trace
                var before = result.SignalSpectrumValues.ToArray();
                var source = new FftSpectrumViewport(result);
                Assert.AreEqual(1000, source.Count);
                Assert.AreEqual(-20, source.Read(100, 110).Signal[0], 1e-10);
                CollectionAssert.AreEqual(before, result.SignalSpectrumValues);
                Assert.AreEqual(-20, source.ValueAt(100), 1e-10);
            }
        }
        [TestMethod] public void HugeRequestsStayBoundedAndDisposeReleasesSource()
        {
            using (var result = Fixture(2500000))
            {
                var source = new FftSpectrumViewport(result);
                var samples = source.Read(-100, int.MaxValue, int.MaxValue);
                Assert.IsTrue(samples.Bins.Length <= 10000);
                Assert.AreEqual(0, samples.Bins.First()); Assert.AreEqual(2499999, samples.Bins.Last());
                Assert.IsTrue(samples.Bins.Zip(samples.Bins.Skip(1), (a, b) => a < b).All(x => x));
                result.Dispose();
                Assert.AreEqual(0, source.Count); Assert.AreEqual(0, source.Read(0, 100).Bins.Length);
                Assert.IsTrue(double.IsNaN(source.ValueAt(0)));
            }
        }
    }
}
