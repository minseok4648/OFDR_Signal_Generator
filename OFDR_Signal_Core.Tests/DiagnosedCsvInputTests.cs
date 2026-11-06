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
    public sealed class DiagnosedCsvInputTests
    {
        private string folder;
        private readonly CancellationToken token = CancellationToken.None;
        [TestInitialize] public void Init() { folder = Path.Combine(Path.GetTempPath(), "ofdr_csv_inputs_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); }
        [TestCleanup] public void Cleanup() { if (folder != null && Path.GetFileName(folder).StartsWith("ofdr_csv_inputs_")) Directory.Delete(folder, true); }
        private Tuple<string, string> Fixture(string name, int n = 4096, bool mask = false, bool noisy = false, bool flat = false, bool crop = false)
        {
            var raw = Path.Combine(folder, name + " raw.csv");
            using (var writer = new StreamWriter(raw))
            {
                writer.WriteLine("Model,DPO4104B\nSample Interval,1e-6\nRecord Length," + (n + 12) + "\nVertical Units,V,V,V,V\nHorizontal Units,s\nTIME,CH1,CH2,CH3,CH4");
                for (var i = 0; i < n + 12; i++)
                {
                    var j = ((i - 5) % n + n) % n; var phi = 2 * Math.PI * 32 * j / n;
                    var aux = flat ? .002 : Math.Cos(phi) + (noisy ? .6 * Math.Cos(3 * phi) : 0);
                    writer.WriteLine((-2 + i * 1e-6).ToString("F3", CultureInfo.InvariantCulture) + "," + (i == 5 || i == n + 5 ? "3.4" : "0") + "," + (mask && j >= 100 && j < 110 ? "3.4" : "0") + "," + aux.ToString("G17", CultureInfo.InvariantCulture) + "," + (i * .001 + Math.Sin(2 * phi)).ToString("G17", CultureInfo.InvariantCulture));
                }
            }
            var source = new TekScopeCsvReader().Read(raw, token); var sweepEngine = new SweepPreparationEngine(); var sweep = sweepEngine.DetectSweeps(source, token).Single();
            var part = sweepEngine.Slice(source, sweep, token); var exclusions = new ModeHopPreviewEngine().Detect(part, new ModeHopSettings(), token);
            var quality = new AuxiliaryQualityEngine(); var stitched = quality.Stitch(part, exclusions, token);
            var settings = new AuxiliaryInspectionSettings { MaximumPreviewPoints = 100, StartSeconds = crop ? 67e-6 : 0, EndSeconds = crop ? (stitched.Values.Length - 50) * 1e-6 : stitched.DurationSeconds };
            var diagnosis = quality.Analyze(stitched, settings, token); var csv = Path.Combine(folder, name + " diagnosed.csv");
            new AuxiliaryCsvExporter().Export(part, stitched, csv, false, 1.8, diagnosis.Metrics, settings, false, token);
            return Tuple.Create(csv, raw);
        }
        [TestMethod] public void FourFilesUseSavedIndicesAndReadCh4WithoutFilenameConvention()
        {
            var r = Fixture("reference", mask: true, crop: true); var s = Fixture("signal", 3072, mask: true, crop: true);
            var input = new DiagnosedCsvInputReader().Read(r.Item1, s.Item1, r.Item2, s.Item2, token); IndependentOfdrProcessingEngine.ValidateInput(input);
            Assert.AreEqual(3, input.Auxiliary.ChannelNumber); Assert.AreEqual(4, input.Reference.ChannelNumber);
            Assert.AreEqual(67, input.Reference.Preparation.FileInputs.OriginalExport.StartIndex); Assert.AreEqual(0, input.Reference.Preparation.SelectedStartIndex);
            Assert.AreEqual("SavedDiagnosticCsv", input.Reference.SourceType); Assert.AreEqual(input.Reference.Values.Length, input.Reference.Preparation.Diagnosis.Phase.SampleCount);
            var read = new DiagnosedCsvInputReader().ReadPair(r.Item1, r.Item2, "Ref", token); var raw = new TekScopeCsvReader().Read(r.Item2, token);
            for (var i = 0; i < read.SampleCount; i++) { Assert.AreEqual(raw.Auxiliary[read.OriginalIndices[i]], read.Auxiliary[i]); Assert.AreEqual(raw.Measurement[read.OriginalIndices[i]], read.Measurement[i]); }
            Assert.AreEqual(r.Item1, input.Auxiliary.SourcePath); Assert.AreEqual(r.Item2, input.Reference.SourcePath);
        }
        [TestMethod] public void MismatchedMeasurementAndAuxiliaryOnlyMeasurementAreRejected()
        {
            var r = Fixture("reference"); var s = Fixture("signal", 3072); var reader = new DiagnosedCsvInputReader();
            Assert.Throws<InvalidDataException>(() => reader.ReadPair(r.Item1, s.Item2, "Ref", token));
            var error = Assert.Throws<InvalidDataException>(() => reader.ReadPair(r.Item1, r.Item1, "Ref", token)); Assert.IsTrue(error.Message.Contains("CH3"));
        }
        [TestMethod] public void PrefixValidationIsMarkedAndDoesNotReuseWholeRangeDiagnosis()
        {
            var r = Fixture("reference"); var data = new DiagnosedCsvInputReader().ReadPair(r.Item1, r.Item2, "Ref", token, maximumSamplesForValidation: 128);
            Assert.AreEqual(128, data.SampleCount); Assert.IsTrue(data.Metadata.FileInputs.PartialValidationInput); Assert.AreEqual(4096, data.Metadata.FileInputs.OriginalExport.RowCount);
            Assert.IsNull(data.Metadata.Diagnosis); Assert.AreEqual(4096, data.Metadata.FileInputs.OriginalExport.LastDiagnosis.Phase.SampleCount);
            var cancelled = new CancellationTokenSource(); cancelled.Cancel(); Assert.Throws<OperationCanceledException>(() => new DiagnosedCsvInputReader().ReadPair(r.Item1, r.Item2, "Ref", cancelled.Token));
        }
        [TestMethod] public void DiagnosticExecutionRetainsFailedQcAndStrictDefaultStillRejects()
        {
            var r = Fixture("reference", noisy: true); var s = Fixture("signal", 3072, noisy: true);
            var input = new DiagnosedCsvInputReader().Read(r.Item1, s.Item1, r.Item2, s.Item2, token);
            Assert.AreEqual("REJECT_PHASE_QC", input.Auxiliary.Preparation.Diagnosis.Decision);
            var engine = new IndependentOfdrProcessingEngine(); var config = new IndependentOfdrProcessingConfig { SegmentSize = 128, SegmentHop = 128, SegmentIfftZeroPadFactor = 4, UsePhysicalAxes = false, RestrictToMeasurementRange = false, MaxPreviewPoints = 100 };
            Assert.IsFalse(config.AllowFailedPhaseQcForDiagnostics); Assert.Throws<InvalidOperationException>(() => engine.Run(input, config, token));
            config.AllowFailedPhaseQcForDiagnostics = true;
            using (var result = engine.Run(input, config, token))
            {
                Assert.IsTrue(result.IsDiagnosticAnalysis); Assert.IsFalse(result.InputPhaseQcPassed); Assert.AreEqual(false, result.KLinearization.PhaseQcPassed); Assert.AreEqual(false, result.SignalKLinearization.PhaseQcPassed);
                Assert.IsTrue(result.KLinearization.DiagnosticOnly); Assert.IsTrue(result.KLinearization.NonmonotonicRatio > 1e-5); Assert.IsTrue(result.SegmentStarts.Length > 0); Assert.AreEqual(3072, result.SampleCount);
                var path = Path.Combine(folder, "result.json"); JsonContractSerializer.Write(path, result); var copy = JsonContractSerializer.Read<IndependentOfdrProcessingResult>(path); Assert.IsTrue(copy.IsDiagnosticAnalysis); Assert.IsFalse(copy.InputPhaseQcPassed);
            }
        }
        [TestMethod] public void DiagnosticExecutionStillRejectsFlatPhase()
        {
            var r = Fixture("reference", flat: true); var s = Fixture("signal"); var input = new DiagnosedCsvInputReader().Read(r.Item1, s.Item1, r.Item2, s.Item2, token);
            Assert.Throws<InvalidOperationException>(() => new IndependentOfdrProcessingEngine().Run(input, new IndependentOfdrProcessingConfig { AllowFailedPhaseQcForDiagnostics = true }, token));
        }
        [TestMethod] public void CsvHashIsCheckedEvenForPrefixValidation()
        {
            var r = Fixture("reference"); File.AppendAllText(r.Item1, "corrupt");
            Assert.Throws<InvalidDataException>(() => new DiagnosedCsvInputReader().ReadPair(r.Item1, r.Item2, "Ref", token, maximumSamplesForValidation: 128));
        }
    }
}
