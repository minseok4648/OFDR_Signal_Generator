using System;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OFDR_Signal_Core.Processing;

namespace OFDR_Signal_Core.Tests
{
    [TestClass]
    public sealed class ReflectogramTests
    {
        [TestMethod]
        public void DensityIntegratesToWindowWeightedMeanSquareWithOrWithoutPadding()
        {
            var engine = new ReflectogramEngine();
            foreach (int count in new[] { 63, 64 })
                foreach (int padding in new[] { 1, 4 })
                    foreach (string window in new[] { "Rectangular", "Hann" }) {
                        var values = Enumerable.Range(0, count).Select(i => .25 + Math.Sin(2 * Math.PI * 7 * i / count) + .1 * Math.Cos(Math.PI * i)).ToArray();
                        var settings = new ReflectogramSettings { Window = window, ZeroPadFactor = padding };
                        double dz; var power = engine.SpectrumPower(values, 1e6, settings, out dz);
                        double weighted = 0, energy = 0;
                        for (int i = 0; i < count; i++) {
                            var w = window == "Hann" ? .5 * (1 - Math.Cos(2 * Math.PI * i / (count - 1))) : 1;
                            weighted += values[i] * values[i] * w * w; energy += w * w;
                        }
                        Assert.AreEqual(weighted / energy, power.Sum() * dz, 1e-10, count + "/" + padding + "/" + window);
                    }
        }
        [TestMethod]
        public void KnownOpticalDelayMapsToSinglePassDistanceIndependentOfPadding()
        {
            const int count = 1024, peak = 137; const double opticalStep = 1e7;
            var x = Enumerable.Range(0, count).Select(i => Math.Cos(2 * Math.PI * peak * i / count)).ToArray();
            var engine = new ReflectogramEngine();
            foreach (int padding in new[] { 1, 4 }) {
                var settings = new ReflectogramSettings { ZeroPadFactor = padding }; double dz;
                var power = engine.SpectrumPower(x, opticalStep, settings, out dz);
                int bin = Array.IndexOf(power, power.Max());
                Assert.AreEqual(peak * ReflectogramEngine.LightSpeed / (2 * settings.GroupIndex * opticalStep * count), bin * dz, 1e-10);
            }
        }
        [TestMethod]
        public void MeanIsTakenInLinearPowerAndRejectsOutOfBandGrid()
        {
            var sum = new double[3];
            ReflectogramEngine.AddOnGrid(sum, new[] { 1.0, 1, 1 }, 1, 1);
            ReflectogramEngine.AddOnGrid(sum, new[] { 100.0, 100, 100 }, 1, 1);
            Assert.AreEqual(17.0329137811866, ReflectogramEngine.Db(sum[1] / 2), 1e-10);
            Assert.ThrowsExactly<ArgumentException>(() => ReflectogramEngine.AddOnGrid(new double[4], new[] { 1.0, 2, 3 }, 1, 1));
        }
        [TestMethod]
        public void PowerMeanReducesNoiseVarianceAcrossIndependentRecords()
        {
            var random = new Random(44); var engine = new ReflectogramEngine(); var settings = new ReflectogramSettings { ZeroPadFactor = 1 };
            double[] first = null, sum = null;
            for (int repeat = 0; repeat < 39; repeat++) {
                var values = Enumerable.Range(0, 4096).Select(i => Math.Sqrt(-2 * Math.Log(Math.Max(1e-10, random.NextDouble()))) * Math.Cos(2 * Math.PI * random.NextDouble())).ToArray();
                double dz; var power = engine.SpectrumPower(values, 1e6, settings, out dz);
                if (repeat == 0) { first = power; sum = new double[power.Length]; }
                for (int i = 0; i < power.Length; i++) sum[i] += power[i] / 39;
            }
            Func<double[], double> width = p => { var sorted = p.Skip(10).Select(ReflectogramEngine.Db).OrderBy(v => v).ToArray(); return sorted[(int)(sorted.Length * .95)] - sorted[(int)(sorted.Length * .05)]; };
            Assert.IsTrue(width(first) > 14); Assert.IsTrue(width(sum) < 3.5);
        }
        [TestMethod]
        public void DensityViewportPreservesUnitsExtremaAndDetailWithoutRenormalization()
        {
            var record = new ReflectogramRecord { GridMeters = .001, CorrectedPower = Enumerable.Repeat(1.0, 36001).ToArray(), NominalPower = Enumerable.Repeat(100.0, 36001).ToArray() };
            record.CorrectedPower[18001] = 1e-8; record.NominalPower[18002] = 1e4;
            var source = new PowerDensityViewport(record); var overview = source.Read(0, source.Count - 1);
            Assert.IsTrue(overview.Bins.Length <= 10000); Assert.IsTrue(overview.Bins.Contains(18001)); Assert.IsTrue(overview.Bins.Contains(18002));
            var detail = source.Read(18000, 18100);
            Assert.AreEqual(101, detail.Bins.Length); Assert.AreEqual(-80, detail.Signal[1], 1e-12); Assert.AreEqual(40, detail.Reference[2], 1e-12);
            Assert.AreEqual(18.001, source.XAt(18001), 1e-12); Assert.AreEqual(40, source.DbCeiling, 1e-12);
        }
        [TestMethod]
        public void PrecancelledRunDoesNotReadOrCreateExperimentDirectory()
        {
            var token = new CancellationToken(true);
            Assert.ThrowsExactly<OperationCanceledException>(() => new ReflectogramEngine().Run("missing.csv", new ReflectogramSettings(), "must_not_create", token));
        }
    }
}
