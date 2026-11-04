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
        private string Csv(int period = 4096, bool mask = false, bool startHigh = false)
        {
            var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".csv");
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("Model,DPO4104B\nSample Interval,1e-6\nRecord Length," + (2 * period + 12) + "\nHorizontal Units,s\nVertical Units,V,V,V,V\nTIME,CH1,CH2,CH3,CH4");
                for (var i = 0; i < 2 * period + 12; i++)
                {
                    var j = ((i - 5) % period + period) % period;
                    var high = i == 5 || i == 5 + period || i == 5 + 2 * period || (startHigh && i < 2);
                    var mh = mask && (j == 0 || j == 100 || j == 101 || j == period - 1);
                    writer.WriteLine(string.Join(",", (-2 + i * 1e-6).ToString("F3", CultureInfo.InvariantCulture), high ? "3.4" : "1.8", mh ? "3.4" : "1.8", Math.Cos(2 * Math.PI * 32 * j / period).ToString("G17", CultureInfo.InvariantCulture), (i * 0.001 + Math.Sin(2 * Math.PI * 64 * j / period)).ToString("G17", CultureInfo.InvariantCulture)));
                }
            }
            return path;
        }
        private PreparedSweepData Prepare(string path, string role)
        {
            var source = new TekScopeCsvReader().Read(path, token); var engine = new SweepPreparationEngine(); var sweep = engine.DetectSweeps(source, token)[0];
            var part = engine.Slice(source, sweep, token); var settings = new AuxiliaryInspectionSettings { MaximumPreviewPoints = 100 };
            var excluded = new ModeHopPreviewEngine().Detect(part, new ModeHopSettings(), token);
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
