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
    public sealed class ModeHopTests
    {
        private string folder;
        private readonly ModeHopPreviewEngine engine = new ModeHopPreviewEngine();
        [TestInitialize] public void Initialize() { folder = Path.Combine(Path.GetTempPath(), "OFDR_ModeHop_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); }
        [TestCleanup] public void Cleanup() { Directory.Delete(folder, true); }

        private string Csv(double[] trigger, double dt = 0.001, double[] auxiliary = null)
        {
            var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".csv");
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("Model,DPO4104B\n\nSample Interval," + dt.ToString("G17", CultureInfo.InvariantCulture) + ",\nRecord Length," + trigger.Length.ToString("E", CultureInfo.InvariantCulture) + ",\nHorizontal Units,s,\nVertical Units,V,V\n,,\nTIME,CH2,CH3");
                for (var i = 0; i < trigger.Length; i++)
                    writer.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0:E6},{1:G17},{2:G17}", -2 + i * dt, trigger[i], auxiliary == null ? i * 0.001 : auxiliary[i]));
                writer.WriteLine();
            }
            return path;
        }

        [TestMethod]
        public void ReaderUsesHeaderTimeAndKeepsRoundedDuplicateRows()
        {
            var data = new TekScopeCsvReader().Read(Csv(new[] { 0.2, 3.4, 3.4, 0.2, 0.2 }, 8e-7), CancellationToken.None);
            Assert.AreEqual(5, data.SampleCount); Assert.AreEqual(-2, data.SourceStartSeconds);
            Assert.AreEqual(4e-6, data.DurationSeconds, 1e-15); Assert.AreEqual(64, data.Sha256.Length);
            Assert.AreEqual(3.4, data.Trigger[2]); Assert.AreEqual(0.002, data.Auxiliary[2]);
        }

        [TestMethod]
        public void RegionsAreStrictHighAndHalfOpenIncludingRecordEdges()
        {
            var data = new TekScopeCsvReader().Read(Csv(new[] { 3.4, 3.4, 1.8, 0.2, 3.4 }), CancellationToken.None);
            var result = engine.Detect(data, new ModeHopSettings(), CancellationToken.None);
            Assert.AreEqual(2, result.Ranges.Count); Assert.AreEqual(3L, result.ExcludedSamples);
            Assert.AreEqual(0, result.Ranges[0].StartIndex); Assert.AreEqual(2, result.Ranges[0].EndIndexExclusive);
            Assert.AreEqual(4, result.Ranges[1].StartIndex); Assert.AreEqual(5, result.Ranges[1].EndIndexExclusive);
            Assert.AreEqual(0.003, result.ExcludedSeconds, 1e-15);
        }

        [TestMethod]
        public void BypassKeepsHighAndPaddingButStillExcludesNonfiniteSamples()
        {
            var data = new TekScopeCsvReader().Read(Csv(new[] { 0.0, 3.4, 3.4, double.PositiveInfinity, 0.0, 0.0 }), CancellationToken.None);
            var settings = new ModeHopSettings { ModeHopExclusionBypassed = true, ExclusionLeadSeconds = .1, ExclusionTailSeconds = .1 };
            var result = engine.Detect(data, settings, CancellationToken.None);
            Assert.IsTrue(result.ModeHopExclusionBypassed);
            Assert.AreEqual(1L, result.ExcludedSamples);
            Assert.AreEqual(3, result.Ranges.Single().StartIndex);
            var stitched = new AuxiliaryQualityEngine().Stitch(data, result, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 4, 5 }, stitched.OriginalIndices);
            Assert.IsTrue(engine.ClipToSamples(result, 1, 5, .001, CancellationToken.None).ModeHopExclusionBypassed);
            settings.ModeHopExclusionBypassed = false;
            Assert.AreEqual(6L, engine.Detect(data, settings, CancellationToken.None).ExcludedSamples);
        }

        [TestMethod]
        public void BypassSettingsRoundTripAndOldSettingsKeepRemovalEnabled()
        {
            var path = Path.Combine(folder, "settings.json");
            JsonContractSerializer.Write(path, new ModeHopSettings { ModeHopExclusionBypassed = true });
            Assert.IsTrue(JsonContractSerializer.Read<ModeHopSettings>(path).ModeHopExclusionBypassed);
            File.WriteAllText(path, "{\"ThresholdVoltage\":1.8,\"MaximumPreviewPoints\":10000}");
            Assert.IsFalse(JsonContractSerializer.Read<ModeHopSettings>(path).ModeHopExclusionBypassed);
            var data = new TekScopeCsvReader().Read(Csv(new[] { 3.4, 3.4, 3.4, 3.4 }), CancellationToken.None);
            Assert.AreEqual(0L, engine.Detect(data, new ModeHopSettings { ModeHopExclusionBypassed = true }, CancellationToken.None).ExcludedSamples);
            Assert.ThrowsExactly<OperationCanceledException>(() => engine.Detect(data, new ModeHopSettings { ModeHopExclusionBypassed = true }, new CancellationToken(true)));
        }

        [TestMethod]
        public void NoHighAndAllHighAreValid()
        {
            var data = new TekScopeCsvReader().Read(Csv(new[] { 3.4, 3.4 }), CancellationToken.None);
            Assert.AreEqual(0, engine.Detect(data, new ModeHopSettings { ThresholdVoltage = 4 }, CancellationToken.None).Ranges.Count);
            var all = engine.Detect(data, new ModeHopSettings(), CancellationToken.None);
            Assert.AreEqual(1, all.Ranges.Count); Assert.AreEqual(100, all.ExcludedPercent);
        }

        [TestMethod]
        public void PulseTailAddsExactlyPointOneMillisecondsAtTwoPointFiveMsps()
        {
            var trigger = new double[2000]; for (var i = 100; i < 1350; i++) trigger[i] = 3.4;
            var source = new TekScopeCsvReader().Read(Csv(trigger, 4e-7), CancellationToken.None);
            var result = engine.Detect(source, new ModeHopSettings { ExclusionTailSeconds = 0.1 / 1000 }, CancellationToken.None);
            Assert.AreEqual(1, result.Ranges.Count); Assert.AreEqual(100, result.Ranges[0].StartIndex);
            Assert.AreEqual(1600, result.Ranges[0].EndIndexExclusive); Assert.AreEqual(1500L, result.ExcludedSamples);
            Assert.AreEqual(0.0006, result.ExcludedSeconds, 1e-15);
            Assert.AreEqual(1350, engine.Detect(source, new ModeHopSettings(), CancellationToken.None).Ranges[0].EndIndexExclusive);
        }

        [TestMethod]
        public void PulseTailMergesOverlapsAndClipsAtRecordEndWithoutDoubleCounting()
        {
            var source = new TekScopeCsvReader().Read(Csv(new[] { 3.4, 0, 0, 3.4, 0, 0, 0, 0, 0, 3.4 }), CancellationToken.None);
            var result = engine.Detect(source, new ModeHopSettings { ExclusionTailSeconds = 0.003 }, CancellationToken.None);
            Assert.AreEqual(2, result.Ranges.Count); Assert.AreEqual(0, result.Ranges[0].StartIndex);
            Assert.AreEqual(7, result.Ranges[0].EndIndexExclusive); Assert.AreEqual(9, result.Ranges[1].StartIndex);
            Assert.AreEqual(10, result.Ranges[1].EndIndexExclusive); Assert.AreEqual(8L, result.ExcludedSamples);
            Assert.AreEqual(80, result.ExcludedPercent, 1e-12);
            Assert.AreEqual(0L, engine.Detect(source, new ModeHopSettings { ThresholdVoltage = 4, ExclusionTailSeconds = 1 }, CancellationToken.None).ExcludedSamples);
            Assert.AreEqual(10L, engine.Detect(source, new ModeHopSettings { ThresholdVoltage = -1, ExclusionTailSeconds = 1 }, CancellationToken.None).ExcludedSamples);
        }

        [TestMethod]
        public void PulseTailRoundsFractionalSamplesUpAndHandlesLargeOffsets()
        {
            var source = new TekScopeCsvReader().Read(Csv(new[] { 0.0, 0, 3.4, 0, 0, 0, 0, 0 }), CancellationToken.None);
            Assert.AreEqual(5, engine.Detect(source, new ModeHopSettings { ExclusionTailSeconds = 0.0011 }, CancellationToken.None).Ranges[0].EndIndexExclusive);
            Assert.AreEqual(4, engine.Detect(source, new ModeHopSettings { ExclusionTailSeconds = double.Epsilon }, CancellationToken.None).Ranges[0].EndIndexExclusive);
            Assert.AreEqual(8, engine.Detect(source, new ModeHopSettings { ExclusionTailSeconds = double.MaxValue }, CancellationToken.None).Ranges[0].EndIndexExclusive);
        }

        [TestMethod]
        public void PulseTailCrossingSweepStartIsClippedFromFullRecordMask()
        {
            var source = new TekScopeCsvReader().Read(Csv(new[] { 0.0, 0, 3.4, 0, 0, 0, 0, 0, 0 }), CancellationToken.None);
            var full = engine.Detect(source, new ModeHopSettings { ExclusionTailSeconds = 0.004 }, CancellationToken.None);
            var clipped = engine.ClipToSamples(full, 4, 9, source.SampleIntervalSeconds, CancellationToken.None);
            Assert.AreEqual(0, clipped.Ranges[0].StartIndex); Assert.AreEqual(3, clipped.Ranges[0].EndIndexExclusive);
            Assert.AreEqual(3L, clipped.ExcludedSamples); Assert.AreEqual(0.004, clipped.ExclusionTailSeconds);
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); Assert.Throws<OperationCanceledException>(() => engine.ClipToSamples(full, 4, 9, source.SampleIntervalSeconds, cancel.Token));
            }
        }

        [TestMethod]
        public void PulseLeadAndTailAddIndependentExactSampleCounts()
        {
            var trigger = new double[3000]; for (var i = 500; i < 1750; i++) trigger[i] = 3.4;
            var source = new TekScopeCsvReader().Read(Csv(trigger, 4e-7), CancellationToken.None);
            var result = engine.Detect(source, new ModeHopSettings { ExclusionLeadSeconds = 0.0001, ExclusionTailSeconds = 0.0002 }, CancellationToken.None);
            Assert.AreEqual(1, result.Ranges.Count); Assert.AreEqual(250, result.Ranges[0].StartIndex);
            Assert.AreEqual(2250, result.Ranges[0].EndIndexExclusive); Assert.AreEqual(2000L, result.ExcludedSamples);
            Assert.AreEqual(0.0008, result.ExcludedSeconds, 1e-15);
            var preview = engine.Preview(source, result, new ModeHopSettings(), 0, source.DurationSeconds, CancellationToken.None);
            Assert.AreEqual(0.0001, preview.Ranges[0].StartSeconds, 1e-15);
            Assert.AreEqual(0.0009, preview.Ranges[0].EndSeconds, 1e-15);
        }

        [TestMethod]
        public void PulseLeadMergesTouchingRangesAndClipsAtBothRecordEdges()
        {
            var source = new TekScopeCsvReader().Read(Csv(new[] { 3.4, 0, 0, 0, 0, 3.4, 0, 0, 0, 0, 0, 3.4 }), CancellationToken.None);
            var settings = new ModeHopSettings { ExclusionLeadSeconds = 0.003, ExclusionTailSeconds = 0.002 };
            var result = engine.Detect(source, settings, CancellationToken.None);
            Assert.AreEqual(1, result.Ranges.Count); Assert.AreEqual(0, result.Ranges[0].StartIndex);
            Assert.AreEqual(12, result.Ranges[0].EndIndexExclusive); Assert.AreEqual(12L, result.ExcludedSamples);
            settings.ExclusionTailSeconds = 0;
            var leadOnly = engine.Detect(source, settings, CancellationToken.None);
            Assert.AreEqual(3, leadOnly.Ranges.Count); Assert.AreEqual(9L, leadOnly.ExcludedSamples);
            settings.ThresholdVoltage = 4; Assert.AreEqual(0L, engine.Detect(source, settings, CancellationToken.None).ExcludedSamples);
            settings.ThresholdVoltage = -1; Assert.AreEqual(12L, engine.Detect(source, settings, CancellationToken.None).ExcludedSamples);
        }

        [TestMethod]
        public void PulseLeadRoundsUpAndClipsLargeOffsets()
        {
            var source = new TekScopeCsvReader().Read(Csv(new[] { 0.0, 0, 0, 0, 3.4, 0 }), CancellationToken.None);
            Assert.AreEqual(2, engine.Detect(source, new ModeHopSettings { ExclusionLeadSeconds = 0.0011 }, CancellationToken.None).Ranges[0].StartIndex);
            Assert.AreEqual(3, engine.Detect(source, new ModeHopSettings { ExclusionLeadSeconds = double.Epsilon }, CancellationToken.None).Ranges[0].StartIndex);
            Assert.AreEqual(0, engine.Detect(source, new ModeHopSettings { ExclusionLeadSeconds = double.MaxValue }, CancellationToken.None).Ranges[0].StartIndex);
        }

        [TestMethod]
        public void PulseLeadFromFollowingSweepClipsToSelectedSweepEnd()
        {
            var source = new TekScopeCsvReader().Read(Csv(new[] { 0.0, 0, 0, 0, 0, 0, 0, 3.4, 0 }), CancellationToken.None);
            var settings = new ModeHopSettings { ExclusionLeadSeconds = 0.004 };
            var full = engine.Detect(source, settings, CancellationToken.None);
            var clipped = engine.ClipToSamples(full, 0, 5, source.SampleIntervalSeconds, CancellationToken.None);
            Assert.AreEqual(3, clipped.Ranges[0].StartIndex); Assert.AreEqual(5, clipped.Ranges[0].EndIndexExclusive);
            Assert.AreEqual(2L, clipped.ExcludedSamples); Assert.AreEqual(0.004, clipped.ExclusionLeadSeconds);
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); Assert.Throws<OperationCanceledException>(() => engine.Detect(source, settings, cancel.Token));
            }
        }

        [TestMethod]
        public void PulseLeadDoesNotPadInfinityOnlyExclusions()
        {
            var source = new TekScopeCsvReader().Read(Csv(new[] { 0.0, 0, double.PositiveInfinity, 0, 0 }), CancellationToken.None);
            var result = engine.Detect(source, new ModeHopSettings { ExclusionLeadSeconds = 1, ExclusionTailSeconds = 1 }, CancellationToken.None);
            Assert.AreEqual(1L, result.ExcludedSamples); Assert.AreEqual(2, result.Ranges[0].StartIndex);
            Assert.AreEqual(3, result.Ranges[0].EndIndexExclusive); Assert.AreEqual(1, result.IgnoredSamples);
        }

        [TestMethod]
        public void PulseTailSettingsRoundtripAndOlderSettingsDefaultToZero()
        {
            var path = Path.Combine(folder, "tail.json");
            JsonContractSerializer.Write(path, new ModeHopSettings { ExclusionTailSeconds = 0.0001, ExclusionLeadSeconds = 0.0002 });
            Assert.AreEqual(0.0001, JsonContractSerializer.Read<ModeHopSettings>(path).ExclusionTailSeconds);
            Assert.AreEqual(0.0002, JsonContractSerializer.Read<ModeHopSettings>(path).ExclusionLeadSeconds);
            File.WriteAllText(path, "{\"ThresholdVoltage\":1.8,\"MaximumPreviewPoints\":10000}");
            var old = JsonContractSerializer.Read<ModeHopSettings>(path); old.Validate(); Assert.AreEqual(0, old.ExclusionTailSeconds);
            Assert.AreEqual(0, old.ExclusionLeadSeconds);
            foreach (var invalid in new[] { -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                Assert.Throws<ArgumentException>(() => new ModeHopSettings { ExclusionLeadSeconds = invalid }.Validate());
        }

        [TestMethod]
        public void RecalculationPreservesRawAndDoesNotNeedSourceFile()
        {
            var path = Csv(new[] { 0.2, 1.9, 3.4, 0.2 });
            var data = new TekScopeCsvReader().Read(path, CancellationToken.None);
            var original = data.Auxiliary.ToArray();
            File.Delete(path);
            Assert.AreEqual(2L, engine.Detect(data, new ModeHopSettings(), CancellationToken.None).ExcludedSamples);
            Assert.AreEqual(1L, engine.Detect(data, new ModeHopSettings { ThresholdVoltage = 2 }, CancellationToken.None).ExcludedSamples);
            CollectionAssert.AreEqual(original, data.Auxiliary);
        }

        [TestMethod]
        public void PreviewPreservesExtremaActualPositionsAndZoomResamplesOriginal()
        {
            var aux = new double[20000]; aux[123] = 9; aux[456] = -7;
            var trigger = new double[20000]; trigger[333] = 3.4;
            var data = new TekScopeCsvReader().Read(Csv(trigger, 1e-5, aux), CancellationToken.None);
            var settings = new ModeHopSettings { MaximumPreviewPoints = 100 };
            var analysis = engine.Detect(data, settings, CancellationToken.None);
            var preview = engine.Preview(data, analysis, settings, 0, data.DurationSeconds, CancellationToken.None);
            Assert.IsLessThanOrEqualTo(100, preview.Auxiliary.Values.Length);
            var peak = Array.IndexOf(preview.Auxiliary.Values, 9);
            Assert.IsGreaterThanOrEqualTo(0, peak); Assert.AreEqual(0.00123, preview.Auxiliary.Seconds[peak], 1e-14);
            CollectionAssert.Contains(preview.Auxiliary.Values, -7.0);
            var zoom = engine.Preview(data, analysis, settings, 0.0033, 0.0034, CancellationToken.None);
            CollectionAssert.Contains(zoom.Trigger.Values, 3.4);
            Assert.AreEqual(1, zoom.Ranges.Count); Assert.AreEqual(333, zoom.Ranges[0].StartIndex);
        }

        [TestMethod]
        public void SweepPeriodOriginAndBlankSettingsRoundTrip()
        {
            var data = new TekScopeCsvReader().Read(Csv(new double[4000]), CancellationToken.None);
            var settings = new ModeHopSettings { SweepDurationSeconds = 1.2, SweepOriginSeconds = 0.1 };
            var analysis = engine.Detect(data, settings, CancellationToken.None);
            var view = engine.Preview(data, analysis, settings, 0, 4, CancellationToken.None);
            Assert.AreEqual(4, view.SweepBoundaries.Count); Assert.AreEqual(1.3, view.SweepBoundaries[1].Seconds, 1e-12);
            settings.SweepDurationSeconds = null;
            var path = Path.Combine(folder, "settings.json"); JsonContractSerializer.Write(path, settings);
            var restored = JsonContractSerializer.Read<ModeHopSettings>(path); restored.Validate();
            Assert.IsNull(restored.SweepDurationSeconds);
            Assert.AreEqual(0, engine.Preview(data, analysis, restored, 0, 4, CancellationToken.None).SweepBoundaries.Count);
        }

        [TestMethod]
        public void DenseSweepMarkersAreBoundedAndReported()
        {
            var data = new TekScopeCsvReader().Read(Csv(new double[10000], 1e-5), CancellationToken.None);
            var settings = new ModeHopSettings { SweepDurationSeconds = 1e-5 };
            var view = engine.Preview(data, engine.Detect(data, settings, CancellationToken.None), settings, 0, data.DurationSeconds, CancellationToken.None);
            Assert.IsTrue(view.SweepMarkersDecimated); Assert.IsLessThanOrEqualTo(500, view.SweepBoundaries.Count);
        }

        [TestMethod]
        public void ReaderRejectsMissingChannelsCountMismatchAndInvalidNumbers()
        {
            foreach (var pair in new[] { Tuple.Create("TIME,CH2,CH3", "TIME,CH1,CH3"), Tuple.Create("Record Length,2.000000E+000", "Record Length,3"), Tuple.Create("0.20000000000000001,0", "NaN,0") })
            {
                var path = Csv(new[] { 0.2, 3.4 });
                var original = File.ReadAllText(path); var modified = original.Replace(pair.Item1, pair.Item2);
                Assert.AreNotEqual(original, modified); File.WriteAllText(path, modified);
                Assert.ThrowsExactly<InvalidDataException>(() => new TekScopeCsvReader().Read(path, CancellationToken.None));
            }
        }

        [TestMethod]
        public void ReaderRejectsTooManyRowsAndTimeReversal()
        {
            var path = Csv(new[] { 0.2, 3.4 }); File.AppendAllText(path, "-1.9,0.2,0\n");
            Assert.ThrowsExactly<InvalidDataException>(() => new TekScopeCsvReader().Read(path, CancellationToken.None));
            path = Csv(new[] { 0.2, 3.4 }); File.WriteAllText(path, File.ReadAllText(path).Replace("-1.999000E+000", "-2.001000E+000"));
            Assert.ThrowsExactly<InvalidDataException>(() => new TekScopeCsvReader().Read(path, CancellationToken.None));
        }

        [TestMethod]
        public void ReaderDetectsSourceChangedDuringRead()
        {
            var path = Csv(new double[131072]);
            var progress = new InlineProgress(value => { if (value.Percent < 100) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1)); });
            Assert.ThrowsExactly<IOException>(() => new TekScopeCsvReader().Read(path, CancellationToken.None, progress));
        }

        [TestMethod]
        public void CancellationStopsReadingDetectionAndPreview()
        {
            var path = Csv(new double[131072]); var reader = new TekScopeCsvReader();
            using (var cancel = new CancellationTokenSource())
            {
                var progress = new InlineProgress(value => cancel.Cancel());
                Assert.ThrowsExactly<OperationCanceledException>(() => reader.Read(path, cancel.Token, progress));
                var data = reader.Read(path, CancellationToken.None);
                Assert.ThrowsExactly<OperationCanceledException>(() => engine.Detect(data, new ModeHopSettings(), cancel.Token));
                var analysis = engine.Detect(data, new ModeHopSettings(), CancellationToken.None);
                Assert.ThrowsExactly<OperationCanceledException>(() => engine.Preview(data, analysis, new ModeHopSettings(), 0, data.DurationSeconds, cancel.Token));
            }
        }

        [TestMethod]
        public void InvalidSettingsAreRejected()
        {
            foreach (var settings in new[] { new ModeHopSettings { ThresholdVoltage = double.NaN }, new ModeHopSettings { ExclusionTailSeconds = -1 }, new ModeHopSettings { ExclusionTailSeconds = double.NaN }, new ModeHopSettings { ExclusionTailSeconds = double.PositiveInfinity }, new ModeHopSettings { SweepDurationSeconds = 0 }, new ModeHopSettings { SweepOriginSeconds = -1 }, new ModeHopSettings { MaximumPreviewPoints = 10001 } })
                Assert.ThrowsExactly<ArgumentException>(() => settings.Validate());
        }

        private sealed class InlineProgress : IProgress<OperationProgress>
        {
            private readonly Action<OperationProgress> action;
            public InlineProgress(Action<OperationProgress> action) { this.action = action; }
            public void Report(OperationProgress value) { action(value); }
        }
    }
}
