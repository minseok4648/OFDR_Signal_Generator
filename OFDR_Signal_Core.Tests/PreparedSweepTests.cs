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
    public sealed class PreparedSweepTests
    {
        private string folder;
        private readonly CancellationToken token = CancellationToken.None;
        [TestInitialize] public void Init() { folder = Path.Combine(Path.GetTempPath(), "ofdr_sweep_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); }
        [TestCleanup] public void Cleanup() { if (folder != null && Path.GetFileName(folder).StartsWith("ofdr_sweep_")) Directory.Delete(folder, true); }
        private string Csv(int period = 4096, bool mask = false, bool startHigh = false, bool recordStart = false, bool firstEdgeOnly = false)
        {
            var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".csv");
            var count = firstEdgeOnly ? period + 5 : recordStart ? period + 12 : 2 * period + 12;
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("Model,DPO4104B\nSample Interval,1e-6\nRecord Length," + count + "\nHorizontal Units,s\nVertical Units,V,V,V,V\nTIME,CH1,CH2,CH3,CH4");
                for (var i = 0; i < count; i++)
                {
                    var j = ((i - (recordStart ? 0 : 5)) % period + period) % period;
                    var high = firstEdgeOnly ? i == 5 || (startHigh && i < 2) : recordStart ? i < 2 || i == period : i == 5 || i == 5 + period || i == 5 + 2 * period || (startHigh && i < 2);
                    var mh = mask && (j == 0 || j == 100 || j == 101 || j == period - 1);
                    writer.WriteLine(string.Join(",", (-2 + i * 1e-6).ToString("F3", CultureInfo.InvariantCulture), high ? "3.4" : "1.8", mh ? "3.4" : "1.8", Math.Cos(2 * Math.PI * 32 * j / period).ToString("G17", CultureInfo.InvariantCulture), (i * 0.001 + Math.Sin(2 * Math.PI * 64 * j / period)).ToString("G17", CultureInfo.InvariantCulture)));
                }
            }
            return path;
        }
        private PreparedSweepData Prepare(string path, string role, bool useRecordingStart = false, double tailSeconds = 0, SweepSelectionMode? mode = null, double leadSeconds = 0)
        {
            var source = new TekScopeCsvReader().Read(path, token); var engine = new SweepPreparationEngine(); var sweep = engine.DetectSweeps(source, token, mode ?? (useRecordingStart ? SweepSelectionMode.RecordingStartToFirstEdge : SweepSelectionMode.CompleteSweeps))[0];
            var part = engine.Slice(source, sweep, token); var settings = new AuxiliaryInspectionSettings { MaximumPreviewPoints = 100 };
            var detector = new ModeHopPreviewEngine();
            var excluded = detector.ClipToSamples(detector.Detect(source, new ModeHopSettings { ExclusionLeadSeconds = leadSeconds, ExclusionTailSeconds = tailSeconds }, token), sweep.StartIndex, sweep.EndIndexExclusive, source.SampleIntervalSeconds, token);
            var quality = new AuxiliaryQualityEngine(); var stitched = quality.Stitch(part, excluded, token); var diagnosis = quality.Analyze(stitched, settings, token);
            return engine.Prepare(source, sweep, role, stitched, diagnosis.Metrics, settings, 1.8, token);
        }
        [TestMethod] public void FourChannelsDuplicateTimeAndCompleteEdgesArePreserved()
        {
            var data = new TekScopeCsvReader().Read(Csv(startHigh: true), token);
            var sweeps = new SweepPreparationEngine().DetectSweeps(data, token);
            Assert.IsTrue(data.HasFourChannels); Assert.AreEqual(2, sweeps.Count); Assert.AreEqual(5, sweeps[0].StartIndex); Assert.AreEqual(4101, sweeps[0].EndIndexExclusive);
            Assert.AreEqual(1e-6, data.SampleIntervalSeconds); Assert.AreEqual(-2.0, data.SourceStartSeconds); Assert.AreEqual(64, data.Sha256.Length);
            var view = new ModeHopPreviewEngine().Preview(data, new ModeHopPreviewEngine().Detect(data, new ModeHopSettings(), token), new ModeHopSettings(), 0, data.DurationSeconds, token);
            Assert.AreEqual(3, view.SweepBoundaries.Count); Assert.IsNotNull(view.Measurement); Assert.IsNotNull(view.SweepTrigger);
            Assert.IsTrue(view.Measurement.Values.Length <= 10000);
        }
        [TestMethod] public void ManualRecordingStartUsesNextEdgeAndPreservesAutomaticCache()
        {
            var source = new TekScopeCsvReader().Read(Csv(recordStart: true), token); var engine = new SweepPreparationEngine();
            Assert.AreEqual(0, engine.DetectSweeps(source, token).Count);
            var sweep = engine.DetectSweeps(source, token, true).Single();
            Assert.AreEqual(0, sweep.StartIndex); Assert.AreEqual(4096, sweep.EndIndexExclusive); Assert.AreEqual(1, sweep.Number);
            Assert.IsTrue(sweep.StartSpecifiedAtRecordStart); Assert.IsTrue(sweep.ToString().Contains("기록 시작 수동 지정"));
            Assert.AreEqual(0, sweep.StartSeconds); Assert.AreEqual(0.004096, sweep.EndSeconds, 1e-12);
            Assert.AreEqual(0, engine.DetectSweeps(source, token).Count);
        }
        [TestMethod] public void ManualRecordingStartAddsPrefixWithoutChangingLaterSweeps()
        {
            var source = new TekScopeCsvReader().Read(Csv(), token); var engine = new SweepPreparationEngine();
            var automatic = engine.DetectSweeps(source, token); var manual = engine.DetectSweeps(source, token, true);
            Assert.AreEqual(3, manual.Count); Assert.AreEqual(0, manual[0].StartIndex); Assert.AreEqual(5, manual[0].EndIndexExclusive);
            for (var i = 0; i < automatic.Count; i++)
            {
                Assert.AreEqual(i + 1, automatic[i].Number); Assert.AreEqual(i + 2, manual[i + 1].Number);
                Assert.AreEqual(automatic[i].StartIndex, manual[i + 1].StartIndex); Assert.AreEqual(automatic[i].EndIndexExclusive, manual[i + 1].EndIndexExclusive);
                Assert.IsFalse(automatic[i].StartSpecifiedAtRecordStart); Assert.IsFalse(manual[i + 1].StartSpecifiedAtRecordStart);
            }
            Assert.AreSame(automatic, engine.DetectSweeps(source, token));
        }
        [TestMethod] public void ManualRecordingStartNeedsAnObservedEndAndHonorsCancellation()
        {
            var path = Csv(recordStart: true); File.WriteAllText(path, File.ReadAllText(path).Replace(",3.4,", ",1.8,"));
            var source = new TekScopeCsvReader().Read(path, token); var engine = new SweepPreparationEngine();
            Assert.AreEqual(0, engine.DetectSweeps(source, token, true).Count);
            Assert.Throws<InvalidDataException>(() => new TekScopeCsvReader().ReadUntilFirstSweepEnd(path, token, true));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); Assert.Throws<OperationCanceledException>(() => engine.DetectSweeps(source, cancelled.Token, true));
                Assert.Throws<OperationCanceledException>(() => new TekScopeCsvReader().ReadUntilFirstSweepEnd(path, cancelled.Token, true));
            }
        }
        [TestMethod] public void FirstEdgeToFileEndAcceptsOneObservedEdgeAndIncludesFinalSample()
        {
            var path = Csv(firstEdgeOnly: true, startHigh: true); var source = new TekScopeCsvReader().Read(path, token); var engine = new SweepPreparationEngine();
            Assert.AreEqual(0, engine.DetectSweeps(source, token).Count);
            var sweep = engine.DetectSweeps(source, token, SweepSelectionMode.FirstEdgeToRecordingEnd).Single();
            Assert.AreEqual(5, sweep.StartIndex); Assert.AreEqual(4101, sweep.EndIndexExclusive); Assert.AreEqual(4096, sweep.SampleCount);
            Assert.AreEqual(5e-6, sweep.StartSeconds, 1e-12); Assert.AreEqual(4101e-6, sweep.EndSeconds, 1e-12);
            Assert.IsTrue(sweep.EndSpecifiedAtRecordEnd); Assert.IsFalse(sweep.StartSpecifiedAtRecordStart); Assert.IsTrue(sweep.ToString().Contains("파일 끝"));
            var slice = engine.Slice(source, sweep, token); Assert.AreEqual(source.Auxiliary.Last(), slice.Auxiliary.Last()); Assert.AreEqual(5, slice.FirstSampleIndex);
            Assert.AreEqual(0, engine.DetectSweeps(source, token).Count);
        }
        [TestMethod] public void FirstEdgeToFileEndDoesNotSplitAtLaterEdgesOrMutateAutomaticCache()
        {
            var source = new TekScopeCsvReader().Read(Csv(), token); var engine = new SweepPreparationEngine();
            var automatic = engine.DetectSweeps(source, token); var range = engine.DetectSweeps(source, token, SweepSelectionMode.FirstEdgeToRecordingEnd).Single();
            Assert.AreEqual(2, automatic.Count); Assert.AreEqual(5, range.StartIndex); Assert.AreEqual(8204, range.EndIndexExclusive);
            Assert.AreSame(automatic, engine.DetectSweeps(source, token)); Assert.IsTrue(automatic.All(s => !s.EndSpecifiedAtRecordEnd));
        }
        [TestMethod] public void FileEndSelectionRejectsPartialInputAndNeedsAnActualRisingEdge()
        {
            var engine = new SweepPreparationEngine(); var path = Csv(firstEdgeOnly: true);
            File.WriteAllText(path, File.ReadAllText(path).Replace(",3.4,", ",1.8,"));
            var source = new TekScopeCsvReader().Read(path, token);
            Assert.AreEqual(0, engine.DetectSweeps(source, token, SweepSelectionMode.FirstEdgeToRecordingEnd).Count);
            var partial = new TekScopeCsvReader().ReadUntilSecondSweepStart(Csv(), token);
            Assert.Throws<ArgumentException>(() => engine.DetectSweeps(partial, token, SweepSelectionMode.FirstEdgeToRecordingEnd));
            Assert.Throws<OperationCanceledException>(() => engine.DetectSweeps(source, new CancellationToken(true), SweepSelectionMode.FirstEdgeToRecordingEnd));
            File.WriteAllText(path, File.ReadAllText(path).Replace(",1.8,1.8,", ",3.4,1.8,"));
            source = new TekScopeCsvReader().Read(path, token);
            Assert.AreEqual(0, engine.DetectSweeps(source, token, SweepSelectionMode.FirstEdgeToRecordingEnd).Count);
        }
        [TestMethod] public void WholeRecordingIncludesBothEndpointsWithoutEdgesAndPreservesAutomaticRanges()
        {
            var engine = new SweepPreparationEngine(); var path = Csv();
            foreach (var noEdges in new[] { false, true })
            {
                if (noEdges) File.WriteAllText(path, File.ReadAllText(path).Replace(",3.4,", ",1.8,"));
                var source = new TekScopeCsvReader().Read(path, token);
                var range = engine.DetectSweeps(source, token, SweepSelectionMode.RecordingStartToRecordingEnd).Single();
                Assert.AreEqual(0, range.StartIndex); Assert.AreEqual(source.SampleCount, range.EndIndexExclusive);
                Assert.AreEqual(0, range.StartSeconds); Assert.AreEqual(source.DurationSeconds, range.EndSeconds);
                Assert.IsTrue(range.StartSpecifiedAtRecordStart && range.EndSpecifiedAtRecordEnd);
                Assert.IsTrue(range.SelectionDescription.Contains("기록 시작 → 기록 끝"));
                var slice = engine.Slice(source, range, token);
                CollectionAssert.AreEqual(source.Auxiliary, slice.Auxiliary);
                var automatic = engine.DetectSweeps(source, token);
                Assert.AreEqual(noEdges ? 0 : 2, automatic.Count);
                engine.DetectSweeps(source, token, SweepSelectionMode.RecordingStartToRecordingEnd);
                Assert.AreSame(automatic, engine.DetectSweeps(source, token));
            }
        }
        [TestMethod] public void WholeRecordingRejectsPartialInputAndHonorsCancellation()
        {
            var engine = new SweepPreparationEngine(); var path = Csv();
            var partial = new TekScopeCsvReader().ReadUntilSecondSweepStart(path, token);
            Assert.Throws<ArgumentException>(() => engine.DetectSweeps(partial, token, SweepSelectionMode.RecordingStartToRecordingEnd));
            var source = new TekScopeCsvReader().Read(path, token);
            Assert.Throws<OperationCanceledException>(() => engine.DetectSweeps(source, new CancellationToken(true), SweepSelectionMode.RecordingStartToRecordingEnd));
        }
        [TestMethod] public void WholeRecordingSavesBothBoundariesAndPairedSourceMapping()
        {
            var path = Csv(mask: true); var source = new TekScopeCsvReader().Read(path, token);
            var prepared = Prepare(path, "Ref", mode: SweepSelectionMode.RecordingStartToRecordingEnd);
            Assert.AreEqual(0, prepared.OriginalIndices[0]); Assert.AreEqual(source.SampleCount - 1, prepared.OriginalIndices.Last());
            Assert.IsTrue(prepared.SampleCount < source.SampleCount);
            for (var i = 0; i < prepared.SampleCount; i++)
            {
                Assert.AreEqual(source.Auxiliary[prepared.OriginalIndices[i]], prepared.Auxiliary[i]);
                Assert.AreEqual(source.Measurement[prepared.OriginalIndices[i]], prepared.Measurement[i]);
            }
            var csv = Path.Combine(folder, "whole_recording.csv"); var io = new PreparedSweepCsv();
            io.Write(prepared, csv, false, token); var copy = io.Read(csv, "Ref", token);
            Assert.IsTrue(copy.Metadata.Sweep.StartSpecifiedAtRecordStart && copy.Metadata.Sweep.EndSpecifiedAtRecordEnd);
            Assert.AreEqual(source.SampleCount, copy.Metadata.Sweep.EndIndexExclusive);
            CollectionAssert.AreEqual(prepared.OriginalIndices, copy.OriginalIndices);
            CollectionAssert.AreEqual(prepared.Auxiliary, copy.Auxiliary); CollectionAssert.AreEqual(prepared.Measurement, copy.Measurement);
        }
        [TestMethod] public void FileEndSelectionSavesBoundaryMetadataAndPairedSourceMapping()
        {
            var path = Csv(firstEdgeOnly: true); var prepared = Prepare(path, "Ref", mode: SweepSelectionMode.FirstEdgeToRecordingEnd);
            Assert.AreEqual(5, prepared.OriginalIndices[0]); Assert.AreEqual(4100, prepared.OriginalIndices.Last());
            var maskedPath = Csv(mask: true, firstEdgeOnly: true);
            var masked = Prepare(maskedPath, "Signal", mode: SweepSelectionMode.FirstEdgeToRecordingEnd);
            var source = new TekScopeCsvReader().Read(maskedPath, token);
            Assert.AreEqual(4092, masked.SampleCount); Assert.AreEqual(6, masked.OriginalIndices[0]); Assert.AreEqual(4099, masked.OriginalIndices.Last());
            for (var i = 0; i < masked.SampleCount; i++) { Assert.AreEqual(source.Auxiliary[masked.OriginalIndices[i]], masked.Auxiliary[i]); Assert.AreEqual(source.Measurement[masked.OriginalIndices[i]], masked.Measurement[i]); }
            var csv = Path.Combine(folder, "file_end.csv"); var io = new PreparedSweepCsv(); io.Write(masked, csv, false, token); var copy = io.Read(csv, "Signal", token);
            Assert.IsTrue(copy.Metadata.Sweep.EndSpecifiedAtRecordEnd); Assert.IsFalse(copy.Metadata.Sweep.StartSpecifiedAtRecordStart);
            Assert.AreEqual(4101, copy.Metadata.Sweep.EndIndexExclusive); CollectionAssert.AreEqual(masked.OriginalIndices, copy.OriginalIndices);
            CollectionAssert.AreEqual(masked.Auxiliary, copy.Auxiliary); CollectionAssert.AreEqual(masked.Measurement, copy.Measurement);
            var oldCsv = Path.Combine(folder, "automatic.csv"); io.Write(Prepare(Csv(), "Ref"), oldCsv, false, token);
            Assert.IsFalse(File.ReadAllText(Path.ChangeExtension(oldCsv, ".metadata.json")).Contains("EndSpecifiedAtRecordEnd"));
            Assert.IsFalse(io.Read(oldCsv, "Ref", token).Metadata.Sweep.EndSpecifiedAtRecordEnd);
        }
        [TestMethod] public void ManualRecordingStartBoundedReadStopsAtItsFirstObservedEnd()
        {
            var source = new TekScopeCsvReader().ReadUntilFirstSweepEnd(Csv(recordStart: true), token, true);
            Assert.IsTrue(source.IsPartialRead); Assert.AreEqual(4097, source.SampleCount);
            Assert.AreEqual(4096, new SweepPreparationEngine().DetectSweeps(source, token, true).Single().EndIndexExclusive);
        }
        [TestMethod] public void ManualRecordingStartSaveReloadPreservesMappingAndProvenance()
        {
            var path = Csv(mask: true, recordStart: true); var prepared = Prepare(path, "Ref", true);
            var source = new TekScopeCsvReader().Read(path, token);
            Assert.AreEqual(4092, prepared.SampleCount); Assert.AreEqual(1, prepared.OriginalIndices[0]); Assert.AreEqual(4094, prepared.OriginalIndices.Last());
            for (var i = 0; i < prepared.SampleCount; i++)
            {
                Assert.AreEqual(source.Auxiliary[prepared.OriginalIndices[i]], prepared.Auxiliary[i]);
                Assert.AreEqual(source.Measurement[prepared.OriginalIndices[i]], prepared.Measurement[i]);
            }
            var csv = Path.Combine(folder, "manual.csv"); var io = new PreparedSweepCsv(); io.Write(prepared, csv, false, token);
            var copy = io.Read(csv, "Ref", token);
            Assert.IsTrue(copy.Metadata.Sweep.StartSpecifiedAtRecordStart); Assert.AreEqual(0, copy.Metadata.Sweep.StartIndex);
            Assert.AreEqual(4096, copy.Metadata.Sweep.EndIndexExclusive); CollectionAssert.AreEqual(prepared.OriginalIndices, copy.OriginalIndices);
            CollectionAssert.AreEqual(prepared.Auxiliary, copy.Auxiliary); CollectionAssert.AreEqual(prepared.Measurement, copy.Measurement);
        }
        [TestMethod] public void PulseTailPreparedChannelsAndSavedMetadataUseExactlyTheSameMask()
        {
            var path = Csv(mask: true); var prepared = Prepare(path, "Ref", tailSeconds: 0.0001);
            var source = new TekScopeCsvReader().Read(path, token);
            var expected = new ModeHopPreviewEngine().Detect(source, new ModeHopSettings { ExclusionTailSeconds = 0.0001 }, token);
            Assert.AreEqual(0.0001, prepared.Metadata.ModeHopExclusionTailSeconds);
            foreach (var index in prepared.OriginalIndices) Assert.IsFalse(expected.Ranges.Any(r => index >= r.StartIndex && index < r.EndIndexExclusive));
            for (var i = 0; i < prepared.SampleCount; i++)
            {
                Assert.AreEqual(source.Auxiliary[prepared.OriginalIndices[i]], prepared.Auxiliary[i]);
                Assert.AreEqual(source.Measurement[prepared.OriginalIndices[i]], prepared.Measurement[i]);
            }
            var csv = Path.Combine(folder, "tail_prepared.csv"); var io = new PreparedSweepCsv(); io.Write(prepared, csv, false, token);
            var copy = io.Read(csv, "Ref", token); Assert.AreEqual(0.0001, copy.Metadata.ModeHopExclusionTailSeconds);
            CollectionAssert.AreEqual(prepared.OriginalIndices, copy.OriginalIndices);
        }
        [TestMethod] public void PulseLeadAndTailPreparedCsvPreservesPairedSourceSamplesAndMetadata()
        {
            var path = Csv(mask: true); var prepared = Prepare(path, "Ref", tailSeconds: 0.0001, leadSeconds: 0.00005);
            var source = new TekScopeCsvReader().Read(path, token);
            var excluded = new ModeHopPreviewEngine().Detect(source, new ModeHopSettings { ExclusionLeadSeconds = 0.00005, ExclusionTailSeconds = 0.0001 }, token);
            var expected = Enumerable.Range(prepared.Metadata.Sweep.StartIndex, prepared.Metadata.Sweep.SampleCount)
                .Where(i => !excluded.Ranges.Any(r => i >= r.StartIndex && i < r.EndIndexExclusive)).ToArray();
            CollectionAssert.AreEqual(expected, prepared.OriginalIndices);
            for (var i = 0; i < prepared.SampleCount; i++)
            {
                Assert.AreEqual(source.Auxiliary[expected[i]], prepared.Auxiliary[i]);
                Assert.AreEqual(source.Measurement[expected[i]], prepared.Measurement[i]);
            }
            var csv = Path.Combine(folder, "lead_tail_prepared.csv"); var io = new PreparedSweepCsv(); io.Write(prepared, csv, false, token);
            var copy = io.Read(csv, "Ref", token);
            Assert.AreEqual(0.00005, copy.Metadata.ModeHopExclusionLeadSeconds); Assert.AreEqual(0.0001, copy.Metadata.ModeHopExclusionTailSeconds);
            CollectionAssert.AreEqual(expected, copy.OriginalIndices);
            copy.Metadata.ModeHopExclusionLeadSeconds = -1; Assert.Throws<ArgumentException>(() => copy.Validate());
        }
        [TestMethod] public void ModeHopRemovalKeepsBothChannelsAndAbsoluteOriginalIndices()
        {
            var path = Csv(mask: true); var data = Prepare(path, "Ref"); var source = new TekScopeCsvReader().Read(path, token);
            Assert.AreEqual(4092, data.SampleCount); Assert.AreEqual(6, data.OriginalIndices[0]); Assert.AreEqual(4099, data.OriginalIndices.Last());
            for (var i = 0; i < data.SampleCount; i++) { Assert.AreEqual(source.Auxiliary[data.OriginalIndices[i]], data.Auxiliary[i]); Assert.AreEqual(source.Measurement[data.OriginalIndices[i]], data.Measurement[i]); }
            Assert.AreEqual(3, data.Metadata.ExcludedRanges.Count); Assert.AreEqual(5, data.Metadata.ExcludedRanges[0].StartIndex);
        }
        [TestMethod] public void CsvRoundtripPreservesSynchronizationCoordinatesAndRejectsWrongRole()
        {
            var data = Prepare(Csv(mask: true), "Signal"); var path = Path.Combine(folder, "prepared.csv"); var io = new PreparedSweepCsv();
            io.Write(data, path, false, token); var copy = io.Read(path, "Signal", token);
            CollectionAssert.AreEqual(data.Auxiliary, copy.Auxiliary); CollectionAssert.AreEqual(data.Measurement, copy.Measurement); CollectionAssert.AreEqual(data.OriginalIndices, copy.OriginalIndices);
            Assert.AreEqual("0,0,", File.ReadLines(path).Skip(1).First().Substring(0, 4));
            Assert.Throws<InvalidDataException>(() => io.Read(path, "Ref", token));
            File.AppendAllText(path, "bad\n"); Assert.Throws<InvalidDataException>(() => io.Read(path, "Signal", token));
        }
        [TestMethod] public void UnequalInputsUseIndependentPhaseAndCommonGridThroughEntireAnalysis()
        {
            var reference = Prepare(Csv(4096), "Ref"); var signal = Prepare(Csv(3072), "Signal"); var input = PreparedSweepData.ToInput(reference, signal);
            var linear = new AuxKLinearizer(new FftEngine()).LinearizeSweepsDetailed(reference.Auxiliary, reference.Measurement, signal.Auxiliary, signal.Measurement, 1e-5, 100, token);
            Assert.AreEqual(3072, linear.Reference.Length); Assert.AreEqual(3072, linear.Signal.Length);
            Assert.AreEqual(Math.Min(linear.ReferenceMetrics.PhaseSpanRadians, linear.SignalMetrics.PhaseSpanRadians), linear.CommonPhaseSpanRadians);
            CollectionAssert.AreEqual(linear.ReferenceDiagnostics.UniformPhaseGrid.Values, linear.SignalDiagnostics.UniformPhaseGrid.Values);
            using (var result = new IndependentOfdrProcessingEngine().Run(input, new IndependentOfdrProcessingConfig { SegmentSize = 128, SegmentHop = 128, SegmentIfftZeroPadFactor = 4, UsePhysicalAxes = false, RestrictToMeasurementRange = false, MaxPreviewPoints = 100 }, token))
            {
                Assert.AreEqual(3072, result.SampleCount); Assert.IsNull(result.PhysicalAxis.ExpectedAppliedStrainMicrostrain); Assert.IsTrue(result.Polarization.Contains("CH4")); Assert.IsTrue(result.SegmentStarts.Length > 0);
                Assert.IsFalse(result.PerformanceMetrics.Any(m => m.Name == "expected_applied_strain_microstrain"));
            }
        }
        [TestMethod] public void MissingChannelNonfiniteAndWrongRecordLengthAreErrors()
        {
            var path = Csv(); var original = File.ReadAllText(path);
            File.WriteAllText(path, original.Replace("TIME,CH1,CH2,CH3,CH4", "TIME,CH1,CH2,CH3,OTHER")); Assert.Throws<InvalidDataException>(() => new TekScopeCsvReader().Read(path, token));
            File.WriteAllText(path, original.Replace(",3.4,", ",NaN,")); Assert.Throws<InvalidDataException>(() => new TekScopeCsvReader().Read(path, token));
            File.WriteAllText(path, original.Replace("Record Length,8204", "Record Length,8205")); Assert.Throws<InvalidDataException>(() => new TekScopeCsvReader().Read(path, token));
        }
        [TestMethod] public void BoundedReadStopsAtSecondRisingAndMarksPartial()
        {
            var path = Csv(); var data = new TekScopeCsvReader().ReadUntilSecondSweepStart(path, token);
            Assert.IsTrue(data.IsPartialRead); Assert.AreEqual(4102, data.SampleCount); Assert.AreEqual(1, new SweepPreparationEngine().DetectSweeps(data, token).Count);
            var cancelled = new CancellationTokenSource(); cancelled.Cancel(); Assert.Throws<OperationCanceledException>(() => new TekScopeCsvReader().Read(path, cancelled.Token));
        }
        [TestMethod] public void CancelledOverwritePreservesBothExistingFilesAndLeavesNoPartials()
        {
            var data = Prepare(Csv(), "Ref"); var path = Path.Combine(folder, "out.csv"); var io = new PreparedSweepCsv(); io.Write(data, path, false, token);
            var csv = File.ReadAllBytes(path); var meta = File.ReadAllBytes(Path.ChangeExtension(path, ".metadata.json")); var cancel = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() => io.Write(data, path, true, cancel.Token, new CancelProgress(cancel)));
            CollectionAssert.AreEqual(csv, File.ReadAllBytes(path)); CollectionAssert.AreEqual(meta, File.ReadAllBytes(Path.ChangeExtension(path, ".metadata.json"))); Assert.AreEqual(0, Directory.GetFiles(folder, "*.partial").Length);
        }
        [TestMethod] public void MetadataIndexTamperingAndMismatchedAcquisitionsAreRejected()
        {
            var reference = Prepare(Csv(), "Ref"); var signal = Prepare(Csv(), "Signal"); reference.OriginalIndices[10]++;
            Assert.Throws<ArgumentException>(() => reference.Validate());
            reference = Prepare(Csv(), "Ref"); var input = PreparedSweepData.ToInput(reference, signal); input.Auxiliary.Preparation = signal.Metadata;
            Assert.Throws<InvalidOperationException>(() => IndependentOfdrProcessingEngine.ValidateInput(input));
        }
        [TestMethod] public void QcPreflightReportsBothRolesAndRejectsBeforeFft()
        {
            var reference = Prepare(Csv(), "Ref"); var signal = Prepare(Csv(), "Signal");
            reference.Metadata.Diagnosis.Phase.NonmonotonicRatio = .14; reference.Metadata.Diagnosis.Decision = "REJECT_PHASE_QC";
            signal.Metadata.Diagnosis.Phase.NonmonotonicRatio = .13; signal.Metadata.Diagnosis.Decision = "REJECT_PHASE_QC";
            var reason = PreparedSweepQcPolicy.PairBlockReason(reference.Metadata, signal.Metadata, 1e-5);
            Assert.IsTrue(reason.Contains("Ref") && reason.Contains("Signal") && reason.Contains("CH3") && reason.Contains("14%") && reason.Contains("13%") && reason.Contains("0.001%") && reason.Contains("ms"));
            var progress = new CountProgress();
            var error = Assert.Throws<InvalidOperationException>(() => new IndependentOfdrProcessingEngine().Run(PreparedSweepData.ToInput(reference, signal), new IndependentOfdrProcessingConfig(), token, progress));
            Assert.IsTrue(error.Message.Contains("분석 전 위상 QC 차단")); Assert.AreEqual(0, progress.Count);
        }
        [TestMethod] public void QcPreflightUsesBothSavedAndCurrentLimits()
        {
            var data = Prepare(Csv(), "Ref"); data.Metadata.Diagnosis.Phase.NonmonotonicRatio = 1e-4; data.Metadata.DiagnosisSettings.MaximumNonmonotonicRatio = 1e-3;
            Assert.IsNotNull(PreparedSweepQcPolicy.BlockReason(data.Metadata, "Ref", 1e-5));
            Assert.IsNull(PreparedSweepQcPolicy.BlockReason(data.Metadata, "Ref", 1e-3));
            data.Metadata.Diagnosis.Decision = "REJECT_PHASE_QC";
            Assert.IsNotNull(PreparedSweepQcPolicy.BlockReason(data.Metadata, "Ref", 1e-3));
            data.Metadata.Diagnosis.Decision = "PASS_PHASE_QC"; data.Metadata.DiagnosisSettings.MaximumNonmonotonicRatio = 1e-5;
            Assert.IsNotNull(PreparedSweepQcPolicy.BlockReason(data.Metadata, "Ref", 1e-3));
        }
        [TestMethod] public void QcPreflightRequiresMatchingCompleteFiniteDiagnosis()
        {
            var data = Prepare(Csv(), "Ref"); var diagnosis = data.Metadata.Diagnosis;
            Assert.IsNull(PreparedSweepQcPolicy.BlockReason(data.Metadata, "Ref", 1e-5));
            data.Metadata.Diagnosis = null; Assert.IsNotNull(PreparedSweepQcPolicy.BlockReason(data.Metadata, "Ref", 1e-5)); data.Metadata.Diagnosis = diagnosis;
            diagnosis.SelectedStartIndex++; Assert.IsNotNull(PreparedSweepQcPolicy.BlockReason(data.Metadata, "Ref", 1e-5)); diagnosis.SelectedStartIndex--;
            var span = diagnosis.Phase.PhaseSpanRadians; diagnosis.Phase.PhaseSpanRadians = 1; Assert.IsNotNull(PreparedSweepQcPolicy.BlockReason(data.Metadata, "Ref", 1e-5)); diagnosis.Phase.PhaseSpanRadians = span;
            diagnosis.Phase.NonmonotonicRatio = double.NaN; Assert.IsNotNull(PreparedSweepQcPolicy.BlockReason(data.Metadata, "Ref", 1e-5)); diagnosis.Phase.NonmonotonicRatio = 0;
            Assert.IsNotNull(PreparedSweepQcPolicy.BlockReason(data.Metadata, "Ref", double.NaN)); Assert.IsNotNull(PreparedSweepQcPolicy.BlockReason(data.Metadata, "Signal", 1e-5));
        }
        [TestMethod] public void QcRejectedCsvRemainsExportableButCannotAnalyzeAfterReload()
        {
            var data = Prepare(Csv(), "Ref"); data.Metadata.Diagnosis.Phase.NonmonotonicRatio = .14; data.Metadata.Diagnosis.Decision = "REJECT_PHASE_QC";
            var path = Path.Combine(folder, "rejected.csv"); var csv = new PreparedSweepCsv(); csv.Write(data, path, false, token);
            var reloaded = csv.Read(path, "Ref", token);
            Assert.AreEqual(.14, reloaded.Metadata.Diagnosis.Phase.NonmonotonicRatio); Assert.IsNotNull(PreparedSweepQcPolicy.BlockReason(reloaded.Metadata, "Ref", 1e-5));
            CollectionAssert.AreEqual(data.Auxiliary, reloaded.Auxiliary); CollectionAssert.AreEqual(data.Measurement, reloaded.Measurement);
        }
        private sealed class CountProgress : IProgress<OperationProgress>
        {
            public int Count; public void Report(OperationProgress value) { Count++; }
        }
        private sealed class CancelProgress : IProgress<OperationProgress>
        {
            private readonly CancellationTokenSource cancellation; public CancelProgress(CancellationTokenSource value) { cancellation = value; }
            public void Report(OperationProgress value) { cancellation.Cancel(); }
        }
    }
}
