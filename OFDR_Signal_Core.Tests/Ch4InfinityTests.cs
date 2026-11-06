using System;
using System.Collections.Generic;
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
    public sealed class Ch4InfinityTests
    {
        private string folder;
        private readonly CancellationToken token = CancellationToken.None;
        [TestInitialize] public void Init() { folder = Path.Combine(Path.GetTempPath(), "ofdr_inf_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); }
        [TestCleanup] public void Cleanup() { Directory.Delete(folder, true); }
        private string Csv(int corruptColumn = -1, string corruptValue = null, bool allInfinity = false)
        {
            var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".csv");
            var invalid = new Dictionary<int, string> { [7] = "inf", [50] = "-INF", [51] = "+Infinity", [300] = "-Infinity" };
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("Sample Interval,1e-6\nRecord Length,524\nVertical Units,V,V,V,V\nHorizontal Units,s\nTIME,CH1,CH2,CH3,CH4");
                for (var i = 0; i < 524; i++)
                {
                    var phase = 2 * Math.PI * 8 * (i - 5) / 256;
                    var cells = new[] { (-2 + i * 1e-6).ToString("F6", CultureInfo.InvariantCulture), i == 5 || i == 261 || i == 517 ? "3.4" : "0.1",
                        (i >= 7 && i < 9) || (i >= 263 && i < 265) ? "3.4" : "0.1", Math.Cos(phase).ToString("G17", CultureInfo.InvariantCulture),
                        allInfinity ? "inf" : invalid.ContainsKey(i) ? invalid[i] : Math.Sin(phase * 2).ToString("G17", CultureInfo.InvariantCulture) };
                    if (i == 10 && corruptColumn >= 0) cells[corruptColumn] = corruptValue;
                    writer.WriteLine(string.Join(",", cells));
                }
            }
            return path;
        }
        [TestMethod] public void ReaderPreservesAllRowsAndMarksOnlyCh4Infinity()
        {
            var source = new TekScopeCsvReader().Read(Csv(), token);
            Assert.AreEqual(524, source.SampleCount); Assert.AreEqual(4, source.InvalidMeasurementCount);
            CollectionAssert.AreEqual(new[] { 7, 50, 51, 300 }, source.InvalidMeasurementIndices);
            Assert.IsTrue(double.IsPositiveInfinity(source.Measurement[7])); Assert.IsTrue(double.IsNegativeInfinity(source.Measurement[50]));
            Assert.AreEqual(-2, source.SourceStartSeconds); Assert.AreEqual(64, source.Sha256.Length);
            var partial = new TekScopeCsvReader().ReadUntilSecondSweepStart(source.SourcePath, token);
            Assert.IsTrue(partial.IsPartialRead); Assert.AreEqual(262, partial.SampleCount); Assert.AreEqual(3, partial.InvalidMeasurementCount);
        }
        [TestMethod] public void InfinityUnionExcludesBothChannelsWithoutDoubleCountingOrTailExpansion()
        {
            var source = new TekScopeCsvReader().Read(Csv(), token); var engine = new ModeHopPreviewEngine();
            var analysis = engine.Detect(source, new ModeHopSettings(), token);
            Assert.AreEqual(7L, analysis.ExcludedSamples); Assert.AreEqual(4, analysis.IgnoredMeasurementSamples);
            Assert.AreEqual(3, analysis.IgnoredMeasurementRanges.Count);
            var onlyInf = engine.Detect(source, new ModeHopSettings { ThresholdVoltage = 4, ExclusionTailSeconds = 0.1 }, token);
            Assert.AreEqual(4L, onlyInf.ExcludedSamples);
            var clipped = engine.ClipToSamples(analysis, 5, 261, 1e-6, token);
            Assert.AreEqual(4L, clipped.ExcludedSamples); Assert.AreEqual(3, clipped.IgnoredMeasurementSamples);
            var preview = engine.Preview(source, analysis, new ModeHopSettings(), 0, source.DurationSeconds, token);
            Assert.IsTrue(preview.Measurement.Values.All(ModeHopSettings.IsFinite)); Assert.AreEqual(520, preview.Measurement.Values.Length);
            Assert.IsFalse(preview.Measurement.Seconds.Contains(7e-6)); Assert.IsTrue(preview.Measurement.Seconds.Contains(8e-6));
        }
        [TestMethod] public void InfinityExclusionAndSourceIndicesSurviveBothCsvFormatsAndFourFileReading()
        {
            var source = new TekScopeCsvReader().Read(Csv(), token); var sweeps = new SweepPreparationEngine(); var sweep = sweeps.DetectSweeps(source, token)[0];
            var detector = new ModeHopPreviewEngine(); var mask = detector.ClipToSamples(detector.Detect(source, new ModeHopSettings(), token), sweep.StartIndex, sweep.EndIndexExclusive, 1e-6, token);
            var part = sweeps.Slice(source, sweep, token); var quality = new AuxiliaryQualityEngine(); var stitched = quality.Stitch(part, mask, token);
            var settings = new AuxiliaryInspectionSettings(); var diagnosis = quality.Analyze(stitched, settings, token);
            var prepared = sweeps.Prepare(source, sweep, "Ref", stitched, diagnosis.Metrics, settings, 1.8, token);
            Assert.AreEqual(252, prepared.SampleCount); Assert.AreEqual(3, part.InvalidMeasurementCount);
            Assert.IsTrue(prepared.Measurement.All(ModeHopSettings.IsFinite));
            foreach (var i in prepared.OriginalIndices) Assert.IsFalse(new[] { 7, 8, 50, 51 }.Contains(i));
            for (var i = 0; i < prepared.SampleCount; i++) { Assert.AreEqual(source.Measurement[prepared.OriginalIndices[i]], prepared.Measurement[i]); Assert.AreEqual(source.Auxiliary[prepared.OriginalIndices[i]], prepared.Auxiliary[i]); }
            var paired = Path.Combine(folder, "paired.csv"); new PreparedSweepCsv().Write(prepared, paired, false, token);
            var read = new PreparedSweepCsv().Read(paired, "Ref", token); Assert.AreEqual(3, read.Metadata.IgnoredMeasurementRanges.Sum(r => r.SampleCount));
            var aux = Path.Combine(folder, "auxiliary_export.csv"); var export = new AuxiliaryCsvExporter().Export(part, stitched, aux, true, 1.8, diagnosis.Metrics, settings, false, token);
            Assert.AreEqual(3, export.Metadata.IgnoredMeasurementRanges.Sum(r => r.SampleCount)); Assert.AreEqual(7, export.Metadata.IgnoredMeasurementRanges[0].StartIndex);
            var fourFile = new DiagnosedCsvInputReader().ReadPair(aux, source.SourcePath, "Ref", token);
            CollectionAssert.AreEqual(prepared.Measurement, fourFile.Measurement); CollectionAssert.AreEqual(prepared.OriginalIndices, fourFile.OriginalIndices);
            Assert.AreEqual(3, fourFile.Metadata.IgnoredMeasurementRanges.Sum(r => r.SampleCount));
        }
        [TestMethod] public void AllInfinityHasEmptyFinitePreviewAndCannotProduceAnInput()
        {
            var source = new TekScopeCsvReader().Read(Csv(allInfinity: true), token); var detector = new ModeHopPreviewEngine(); var mask = detector.Detect(source, new ModeHopSettings(), token);
            Assert.AreEqual(524L, mask.ExcludedSamples);
            Assert.AreEqual(0, detector.Preview(source, mask, new ModeHopSettings(), 0, source.DurationSeconds, token).Measurement.Values.Length);
            Assert.Throws<InvalidOperationException>(() => new AuxiliaryQualityEngine().Stitch(source, mask, token));
            using (var cts = new CancellationTokenSource()) { cts.Cancel(); Assert.Throws<OperationCanceledException>(() => detector.Detect(source, new ModeHopSettings(), cts.Token)); }
        }
        [TestMethod] public void TimeInfinityAndAnyChannelNaNMalformedValuesAndOverflowRemainErrors()
        {
            Assert.Throws<InvalidDataException>(() => new TekScopeCsvReader().Read(Csv(0, "inf"), token));
            foreach (var channel in new[] { 1, 2, 3, 4 })
                foreach (var value in new[] { "NaN", "garbage", "1e9999" }) Assert.Throws<InvalidDataException>(() => new TekScopeCsvReader().Read(Csv(channel, value), token));
        }

        private static void SetCell(string path, int sample, int column, string value)
        {
            var lines = File.ReadAllLines(path); var header = Array.FindIndex(lines, line => line.StartsWith("TIME,"));
            var cells = lines[header + 1 + sample].Split(','); cells[column] = value;
            lines[header + 1 + sample] = string.Join(",", cells); File.WriteAllLines(path, lines);
        }

        [TestMethod] public void EveryChannelInfinityIsExcludedWithoutFalseTriggersOrTail()
        {
            foreach (var channel in new[] { 1, 2, 3, 4 }) foreach (var value in new[] { "inf", "-INF", "+Infinity", " -infinity " })
            {
                var source = new TekScopeCsvReader().Read(Csv(channel, value), token);
                CollectionAssert.AreEqual(new[] { 7, 10, 50, 51, 300 }, source.InvalidSampleIndices);
                Assert.AreEqual(524, source.SampleCount); Assert.AreEqual(channel == 4 ? 5 : 4, source.InvalidMeasurementCount);
                var settings = new ModeHopSettings { ThresholdVoltage = 4, ExclusionTailSeconds = .1 };
                var detector = new ModeHopPreviewEngine(); var mask = detector.Detect(source, settings, token);
                Assert.AreEqual(5L, mask.ExcludedSamples); Assert.AreEqual(5, mask.IgnoredSamples);
                var preview = detector.Preview(source, mask, settings, 0, source.DurationSeconds, token);
                foreach (var series in new[] { preview.Auxiliary, preview.Trigger, preview.SweepTrigger, preview.Measurement })
                    Assert.IsTrue(series.Values.All(ModeHopSettings.IsFinite));
                var sweeps = new SweepPreparationEngine().DetectSweeps(source, token);
                Assert.AreEqual(2, sweeps.Count); Assert.AreEqual(5, sweeps[0].StartIndex);
                var partial = new TekScopeCsvReader().ReadUntilSecondSweepStart(source.SourcePath, token);
                Assert.AreEqual(262, partial.SampleCount); Assert.AreEqual(4, partial.InvalidSampleCount);
            }
        }

        [TestMethod] public void InfinityUnionDeduplicatesChannelsAndPreservesFirstLastSourceTimes()
        {
            var path = Csv();
            SetCell(path, 0, 1, "inf"); SetCell(path, 10, 1, "inf"); SetCell(path, 10, 2, "-inf"); SetCell(path, 10, 3, "inf");
            SetCell(path, 11, 3, "-inf"); SetCell(path, 523, 2, "inf");
            var source = new TekScopeCsvReader().Read(path, token); var detector = new ModeHopPreviewEngine();
            CollectionAssert.AreEqual(new[] { 0, 7, 10, 11, 50, 51, 300, 523 }, source.InvalidSampleIndices);
            var mask = detector.Detect(source, new ModeHopSettings { ThresholdVoltage = 4 }, token);
            Assert.AreEqual(8L, mask.ExcludedSamples); Assert.AreEqual(8, mask.IgnoredSamples); Assert.AreEqual(4, mask.IgnoredMeasurementSamples);
            var stitched = new AuxiliaryQualityEngine().Stitch(source, mask, token);
            Assert.AreEqual(1, stitched.OriginalIndices.First()); Assert.AreEqual(522, stitched.OriginalIndices.Last());
            Assert.AreEqual(516, stitched.Values.Length); Assert.AreEqual(-2, source.SourceStartSeconds);
        }

        [TestMethod] public void NonfiniteSweepTriggerNeverInventsAnEdgeAcrossUnknownSamples()
        {
            var path = Csv(); SetCell(path, 1, 1, "+inf"); SetCell(path, 2, 1, "-inf"); SetCell(path, 3, 1, "3.4");
            var source = new TekScopeCsvReader().Read(path, token);
            var sweeps = new SweepPreparationEngine().DetectSweeps(source, token);
            Assert.AreEqual(2, sweeps.Count); Assert.AreEqual(5, sweeps[0].StartIndex);
            Assert.AreEqual(262, new TekScopeCsvReader().ReadUntilSecondSweepStart(path, token).SampleCount);
            // The actual transition was lost: do not substitute a guessed edge after -Inf.
            SetCell(path, 4, 1, "-inf"); source = new TekScopeCsvReader().Read(path, token);
            sweeps = new SweepPreparationEngine().DetectSweeps(source, token);
            Assert.AreEqual(1, sweeps.Count); Assert.AreEqual(261, sweeps[0].StartIndex);
            Assert.AreEqual(518, new TekScopeCsvReader().ReadUntilSecondSweepStart(path, token).SampleCount);
        }

        [TestMethod] public void EveryChannelMaskSurvivesPreparationExportAndBothImportPaths()
        {
            foreach (var channel in new[] { 1, 2, 3, 4 })
            {
                var source = new TekScopeCsvReader().Read(Csv(channel, "inf"), token); var sweeps = new SweepPreparationEngine();
                var sweep = sweeps.DetectSweeps(source, token)[0]; var detector = new ModeHopPreviewEngine();
                var mask = detector.ClipToSamples(detector.Detect(source, new ModeHopSettings(), token), sweep.StartIndex, sweep.EndIndexExclusive, 1e-6, token);
                var part = sweeps.Slice(source, sweep, token); var quality = new AuxiliaryQualityEngine(); var stitched = quality.Stitch(part, mask, token);
                var settings = new AuxiliaryInspectionSettings(); var diagnosis = quality.Analyze(stitched, settings, token);
                var prepared = sweeps.Prepare(source, sweep, "Ref", stitched, diagnosis.Metrics, settings, 1.8, token);
                Assert.AreEqual(251, prepared.SampleCount); Assert.AreEqual(4, part.InvalidSampleCount);
                Assert.IsTrue(prepared.Auxiliary.All(ModeHopSettings.IsFinite) && prepared.Measurement.All(ModeHopSettings.IsFinite));
                Assert.IsFalse(prepared.OriginalIndices.Any(i => new[] { 7, 8, 10, 50, 51 }.Contains(i)));
                for (var i = 0; i < prepared.SampleCount; i++)
                {
                    Assert.AreEqual(source.Measurement[prepared.OriginalIndices[i]], prepared.Measurement[i]);
                    Assert.AreEqual(source.Auxiliary[prepared.OriginalIndices[i]], prepared.Auxiliary[i]);
                }
                var paired = Path.Combine(folder, "paired_" + channel + ".csv"); new PreparedSweepCsv().Write(prepared, paired, false, token);
                var read = new PreparedSweepCsv().Read(paired, "Ref", token);
                CollectionAssert.AreEqual(prepared.OriginalIndices, read.OriginalIndices); Assert.AreEqual(4, read.Metadata.IgnoredSampleRanges.Sum(r => r.SampleCount));
                var aux = Path.Combine(folder, "aux_" + channel + ".csv");
                var export = new AuxiliaryCsvExporter().Export(part, stitched, aux, true, 1.8, diagnosis.Metrics, settings, false, token);
                Assert.AreEqual(4, export.Metadata.IgnoredSampleRanges.Sum(r => r.SampleCount));
                var fourFile = new DiagnosedCsvInputReader().ReadPair(aux, source.SourcePath, "Ref", token);
                CollectionAssert.AreEqual(prepared.Measurement, fourFile.Measurement); CollectionAssert.AreEqual(prepared.OriginalIndices, fourFile.OriginalIndices);
                Assert.AreEqual(4, fourFile.Metadata.IgnoredSampleRanges.Sum(r => r.SampleCount));
            }
        }

        [TestMethod] public void TwoChannelInputSupportsInfinityAndLegacyMetadataStillLoads()
        {
            var path = Path.Combine(folder, "two.csv");
            File.WriteAllText(path, "Sample Interval,1e-6\nRecord Length,6\nTIME,CH2,CH3\n0,0,inf\n0,+inf,1\n0,0,2\n0,0,3\n0,0,4\n0,0,5\n");
            var source = new TekScopeCsvReader().Read(path, token); Assert.IsFalse(source.HasFourChannels);
            Assert.AreEqual(6, source.SampleCount); CollectionAssert.AreEqual(new[] { 0, 1 }, source.InvalidSampleIndices);
            var mask = new ModeHopPreviewEngine().Detect(source, new ModeHopSettings { ExclusionTailSeconds = .1 }, token);
            Assert.AreEqual(2L, mask.ExcludedSamples);
            CollectionAssert.AreEqual(new[] { 2.0, 3.0, 4.0, 5.0 }, new AuxiliaryQualityEngine().Stitch(source, mask, token).Values);
            var legacy = Path.Combine(folder, "old.metadata.json"); File.WriteAllText(legacy, "{\"IgnoredMeasurementRanges\":[{\"StartIndex\":4,\"EndIndexExclusive\":6}]}");
            Assert.AreEqual(2, JsonContractSerializer.Read<PreparedSweepMetadata>(legacy).IgnoredSampleRanges.Single().SampleCount);
            Assert.AreEqual(2, JsonContractSerializer.Read<AuxiliaryCsvExportMetadata>(legacy).IgnoredSampleRanges.Single().SampleCount);
        }
        [TestMethod] public void FiniteMinMaxPreviewRetainsExtremaAtOriginalTimes()
        {
            var values = new double[20000]; values[0] = double.PositiveInfinity; values[123] = 9; values[456] = -7; values[19999] = double.NegativeInfinity;
            var preview = ModeHopPreviewEngine.Reduce(values, 0, values.Length, 1e-6, 100, token, true);
            Assert.IsTrue(preview.Values.All(ModeHopSettings.IsFinite)); Assert.IsTrue(preview.Values.Length <= 100);
            Assert.AreEqual(123e-6, preview.Seconds[Array.IndexOf(preview.Values, 9)], 1e-15);
            Assert.AreEqual(456e-6, preview.Seconds[Array.IndexOf(preview.Values, -7)], 1e-15);
        }
        [TestMethod] public void FormattedWholeSignalEndAllowsRoundoffButNotExtraSamples()
        {
            const int n = 4089; var path = Path.Combine(folder, "roundoff_fixture.csv");
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("Sample Interval,1e-6\nRecord Length,4089\nTIME,CH2,CH3");
                for (var i = 0; i < n; i++) writer.WriteLine((i * 1e-6).ToString("G17", CultureInfo.InvariantCulture) + ",0," + Math.Cos(2 * Math.PI * 32 * i / n).ToString("G17", CultureInfo.InvariantCulture));
            }
            var source = new TekScopeCsvReader().Read(path, token);
            var signal = new AuxiliaryQualityEngine().Stitch(source, new ModeHopPreviewEngine().Detect(source, new ModeHopSettings(), token), token);
            var displayedMilliseconds = (signal.DurationSeconds * 1000).ToString("G12", CultureInfo.InvariantCulture);
            var end = double.Parse(displayedMilliseconds, CultureInfo.InvariantCulture) / 1000;
            var quality = new AuxiliaryQualityEngine();
            Assert.AreEqual(n, quality.Analyze(signal, new AuxiliaryInspectionSettings { EndSeconds = end }, token).Metrics.Phase.SampleCount);
            Assert.Throws<ArgumentException>(() => quality.Analyze(signal, new AuxiliaryInspectionSettings { EndSeconds = signal.DurationSeconds + 0.5e-6 }, token));
        }
    }
}
