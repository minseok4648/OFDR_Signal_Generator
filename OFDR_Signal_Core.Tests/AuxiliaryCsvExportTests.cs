using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;

namespace OFDR_Signal_Core.Tests
{
    [TestClass]
    public sealed class AuxiliaryCsvExportTests
    {
        private string folder;
        private ModeHopRecording source;
        private StitchedAuxiliarySignal signal;
        [TestInitialize]
        public void Initialize()
        {
            folder = Path.Combine(Path.GetTempPath(), "OFDR_AuxExport_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "source.csv");
            File.WriteAllText(path, "Sample Interval,0.001\nRecord Length,9\nTIME,CH2,CH3\n-2,3.4,0\n-1.999,0,0.1\n-1.998,0,0.2\n-1.997,3.4,0.3\n-1.996,0,0.4\n-1.995,0,0.5\n-1.994,3.4,0.6\n-1.993,0,0.7\n-1.992,0,0.8\n", new UTF8Encoding(false));
            source = new TekScopeCsvReader().Read(path, CancellationToken.None);
            signal = new AuxiliaryQualityEngine().Stitch(source, new ModeHopPreviewEngine().Detect(source, new ModeHopSettings(), CancellationToken.None), CancellationToken.None);
        }
        [TestCleanup] public void Cleanup() { Directory.Delete(folder, true); }
        private AuxiliaryQualityMetrics Diagnosis() => new AuxiliaryQualityMetrics { SelectedStartIndex = 2, SelectedEndIndexExclusive = 5, Decision = "REJECT_PHASE_QC" };
        private AuxiliaryCsvExportResult Export(string path, bool entire = false, bool overwrite = false, CancellationToken token = default(CancellationToken), IProgress<OperationProgress> progress = null)
            => new AuxiliaryCsvExporter().Export(source, signal, path, entire, 1.8, Diagnosis(), new AuxiliaryInspectionSettings { StartSeconds = 0.002, EndSeconds = 0.005 }, overwrite, token, progress);

        [TestMethod]
        public void DiagnosedRangeExportsUnchangedSamplesMappingTimeAndGlobalJoinAtFirstRow()
        {
            var path = Path.Combine(folder, "diagnosed.csv");
            var result = Export(path); var lines = File.ReadAllLines(path);
            Assert.AreEqual(AuxiliaryCsvExporter.Header, lines[0]); Assert.AreEqual(4, lines.Length);
            for (var row = 0; row < 3; row++)
            {
                var cells = lines[row + 1].Split(','); var index = row + 2;
                Assert.AreEqual(index, int.Parse(cells[0], CultureInfo.InvariantCulture));
                Assert.AreEqual(index * 0.001, double.Parse(cells[1], CultureInfo.InvariantCulture));
                Assert.AreEqual(signal.Values[index], double.Parse(cells[2], CultureInfo.InvariantCulture));
                Assert.AreEqual(signal.OriginalIndices[index], int.Parse(cells[3], CultureInfo.InvariantCulture));
                Assert.AreEqual(-2 + signal.OriginalIndices[index] * 0.001, double.Parse(cells[4], CultureInfo.InvariantCulture));
            }
            Assert.AreEqual("1", lines[1].Split(',')[5]); Assert.AreEqual("1", lines[3].Split(',')[5]);
            var metadata = JsonContractSerializer.Read<AuxiliaryCsvExportMetadata>(result.MetadataPath);
            Assert.AreEqual(3, metadata.RowCount); Assert.AreEqual(2, metadata.JoinRows);
            Assert.AreEqual(2, metadata.StartIndex); Assert.AreEqual(5, metadata.EndIndexExclusive);
            Assert.AreEqual(source.Sha256, metadata.SourceSha256); Assert.AreEqual("REJECT_PHASE_QC", metadata.LastDiagnosis.Decision);
            using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) Assert.AreEqual(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(), metadata.CsvSha256);
        }
        [TestMethod]
        public void WholeExportDoesNotRequireDiagnosisOrSourceFileAndUsesInvariantNumbers()
        {
            File.Delete(source.SourcePath);
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                var result = new AuxiliaryCsvExporter().Export(source, signal, Path.Combine(folder, "entire.csv"), true, 1.8, null, null, false, CancellationToken.None);
                Assert.AreEqual(signal.Values.Length, result.Metadata.RowCount);
                Assert.IsTrue(result.Metadata.EntireStitchedSignal); Assert.IsNull(result.Metadata.LastDiagnosis);
                Assert.AreEqual(6, File.ReadAllLines(result.CsvPath)[1].Split(',').Length);
                StringAssert.Contains(File.ReadAllText(result.CsvPath), "0.1");
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }
        [TestMethod]
        public void OverwriteRequiresOptInAndSourceCannotBeOverwritten()
        {
            var path = Path.Combine(folder, "output.csv"); File.WriteAllText(path, "old content");
            Assert.ThrowsExactly<IOException>(() => Export(path)); Assert.AreEqual("old content", File.ReadAllText(path));
            Export(path, overwrite: true); Assert.AreEqual(AuxiliaryCsvExporter.Header, File.ReadLines(path).First());
            var original = File.ReadAllText(source.SourcePath);
            Assert.ThrowsExactly<IOException>(() => Export(source.SourcePath, overwrite: true));
            Assert.AreEqual(original, File.ReadAllText(source.SourcePath));
        }
        [TestMethod]
        public void CancellationLeavesExistingCsvAndMetadataUnchangedAndNoPartials()
        {
            var path = Path.Combine(folder, "cancel.csv"); var metadata = Path.ChangeExtension(path, ".metadata.json");
            File.WriteAllText(path, "old csv"); File.WriteAllText(metadata, "old metadata");
            using (var cancel = new CancellationTokenSource())
            {
                var progress = new InlineProgress(() => cancel.Cancel());
                Assert.ThrowsExactly<OperationCanceledException>(() => Export(path, overwrite: true, token: cancel.Token, progress: progress));
            }
            Assert.AreEqual("old csv", File.ReadAllText(path)); Assert.AreEqual("old metadata", File.ReadAllText(metadata));
            Assert.AreEqual(0, Directory.GetFiles(folder, "*.partial").Length); Assert.AreEqual(0, Directory.GetFiles(folder, "*.backup").Length);
        }
        [TestMethod]
        public void FailedMetadataPublicationRestoresPreviousCsv()
        {
            var path = Path.Combine(folder, "rollback.csv"); File.WriteAllText(path, "old csv");
            Directory.CreateDirectory(Path.ChangeExtension(path, ".metadata.json"));
            Assert.Throws<IOException>(() => Export(path, overwrite: true));
            Assert.AreEqual("old csv", File.ReadAllText(path)); Assert.AreEqual(0, Directory.GetFiles(folder, "*.partial").Length); Assert.AreEqual(0, Directory.GetFiles(folder, "*.backup").Length);
        }
        [TestMethod]
        public void MissingDiagnosisAndInvalidRangeAreRejected()
        {
            var exporter = new AuxiliaryCsvExporter(); var path = Path.Combine(folder, "bad.csv");
            Assert.ThrowsExactly<InvalidOperationException>(() => exporter.Export(source, signal, path, false, 1.8, null, null, false, CancellationToken.None));
            var diagnosis = Diagnosis(); diagnosis.SelectedEndIndexExclusive = 100;
            Assert.ThrowsExactly<ArgumentException>(() => exporter.Export(source, signal, path, false, 1.8, diagnosis, null, false, CancellationToken.None));
            Assert.IsFalse(File.Exists(path));
        }
        private sealed class InlineProgress : IProgress<OperationProgress>
        {
            private readonly Action action; public InlineProgress(Action action) { this.action = action; }
            public void Report(OperationProgress value) { action(); }
        }
    }
}
