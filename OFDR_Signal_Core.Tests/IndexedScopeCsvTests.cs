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
    public sealed class IndexedScopeCsvTests
    {
        private string directory;
        [TestInitialize] public void Setup() { directory = Path.Combine(Path.GetTempPath(), "ofdr_indexed_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); }
        [TestCleanup] public void Cleanup() { Directory.Delete(directory, true); }
        private string Write(string text)
        {
            // The filename deliberately disagrees with the content: data is the source of truth.
            var path = Path.Combine(directory, "20Mpts_100MSps.CSV"); File.WriteAllText(path, text, new UTF8Encoding(true)); return path;
        }
        private const string Header = "Index,Time_s,CH1_Ref_V,CH2_Xpol_V,CH3_Ypol_V,CH4_V\n";
        [TestMethod] public void IndexedExportUsesActualTimeAndRowsAndPreservesPhysicalChannelRoles()
        {
            var text = new StringBuilder(Header);
            for (var i = 0; i < 4096; i++) text.AppendLine(string.Join(",", new[] { i.ToString(), (-.0498198 + i * 1e-7).ToString("R", CultureInfo.InvariantCulture), "1", "2", "3", "4" }));
            var recording = new TekScopeCsvReader().Read(Write(text.ToString()), CancellationToken.None);
            Assert.AreEqual(4096, recording.SampleCount); Assert.AreEqual(1e-7, recording.SampleIntervalSeconds, 1e-18);
            Assert.AreEqual(-.0498198, recording.SourceStartSeconds, 1e-15);
            Assert.AreEqual(1, recording.SweepTrigger[5]); Assert.AreEqual(2, recording.Trigger[5]); Assert.AreEqual(3, recording.Auxiliary[5]); Assert.AreEqual(4, recording.Measurement[5]);
            Assert.AreEqual("4096", recording.Header["Record Length"]); Assert.AreEqual(64, recording.Sha256.Length);
            Assert.IsTrue(recording.Header["Sample Interval Origin"].Contains("last Time_s"));
        }
        [TestMethod] public void ReorderedCanonicalAliasesRetainTimeColumnAndSourceIndex()
        {
            var path = Write("CH4_V,Index,CH2_V,Time_s,CH1_V,CH3_V\n4,100,2,-1,1,3\n5,101,2,-0.9,1,3\n6,102,2,-0.8,1,3\n");
            var r = new TekScopeCsvReader().Read(path, CancellationToken.None);
            Assert.AreEqual(.1, r.SampleIntervalSeconds, 1e-15); Assert.AreEqual(6, r.Measurement[2]); Assert.AreEqual("100", r.Header["First Source Index"]);
        }
        [TestMethod] public void IndexedInfinityStillParticipatesInUnionMask()
        {
            var r = new TekScopeCsvReader().Read(Write(Header + "0,0,0,0,0,1\n1,0.1,0,0,inf,1\n2,0.2,0,0,0,-inf\n3,0.3,0,0,0,1\n"), CancellationToken.None);
            CollectionAssert.AreEqual(new[] { 1, 2 }, r.InvalidSampleIndices); CollectionAssert.AreEqual(new[] { 2 }, r.InvalidMeasurementIndices);
        }
        [TestMethod] public void DuplicateMissingOrNonIntegerIndexIsRejected()
        {
            foreach (var index in new[] { "0", "2", "0.5" })
                Assert.ThrowsExactly<InvalidDataException>(() => new TekScopeCsvReader().Read(Write(Header + "0,0,1,2,3,4\n" + index + ",0.1,1,2,3,4\n2,0.2,1,2,3,4\n"), CancellationToken.None));
        }
        [TestMethod] public void IrregularOrRepeatedTimeAndMalformedRowsAreRejected()
        {
            foreach (var row in new[] { "1,0.11,1,2,3,4", "1,0,1,2,3,4", "1,0.1,1,2,3" })
                Assert.ThrowsExactly<InvalidDataException>(() => new TekScopeCsvReader().Read(Write(Header + "0,0,1,2,3,4\n" + row + "\n2,0.2,1,2,3,4\n"), CancellationToken.None));
        }
        [TestMethod] public void AmbiguousChannelAliasesAndMissingFourthChannelAreRejected()
        {
            foreach (var header in new[] { "Index,Time_s,CH1,CH2,CH3,CH3_Ypol_V,CH4", "Index,Time_s,CH1,CH2,CH3" })
                Assert.ThrowsExactly<InvalidDataException>(() => new TekScopeCsvReader().Read(Write(header + "\n"), CancellationToken.None));
        }
        [TestMethod] public void AutoBandpassChangesOnlyAnOtherwiseInvalidHighCutoff()
        {
            var settings = new DocumentStageSettings { BandpassEnabled = true };
            Assert.AreEqual(4.5, settings.ResolveHighMHz(1e-7), 1e-12);
            Assert.AreEqual(12, settings.ResolveHighMHz(4e-8), 1e-12);
            Assert.AreEqual(12, settings.ResolveHighMHz(1e-8), 1e-12);
            settings.AutoLimitBandpass = false; Assert.AreEqual(12, settings.ResolveHighMHz(1e-7), 1e-12);
            settings.AutoLimitBandpass = true; settings.LowMHz = 4.6;
            Assert.ThrowsExactly<ArgumentException>(() => settings.ResolveHighMHz(1e-7));
        }
        [TestMethod] public void TenMSpsPreprocessorRecordsAppliedBandAndCanKeepStrictMode()
        {
            const int n = 4096; const double dt = 1e-7;
            var a = new OfdrChannelData { SampleIntervalSeconds = dt, Values = Enumerable.Range(0, n).Select(i => Math.Sin(2 * Math.PI * 2e6 * dt * i)).ToArray() };
            var settings = new DocumentStageSettings { Enabled = true, BandpassEnabled = true };
            var pair = DocumentStagePreprocessor.Process(a, a, settings, true, 16, CancellationToken.None);
            Assert.IsTrue(pair.Description.Contains("1–4.5MHz")); Assert.IsTrue(pair.Auxiliary.All(ModeHopSettings.IsFinite));
            settings.AutoLimitBandpass = false;
            Assert.ThrowsExactly<ArgumentException>(() => DocumentStagePreprocessor.Process(a, a, settings, true, 16, CancellationToken.None));
        }
        [TestMethod] public void UnifiedPreparationRetainsWholeSourceAndCommonMaskProvenance()
        {
            var text = new StringBuilder(Header);
            for (var i = 0; i < 4096; i++) text.AppendLine(string.Join(",", new[] { i.ToString(), (i * 1e-7).ToString("R", CultureInfo.InvariantCulture), "0", i >= 100 && i < 110 ? "3" : "0",
                Math.Sin(2 * Math.PI * .2 * i).ToString("R", CultureInfo.InvariantCulture), Math.Cos(.1 * i).ToString("R", CultureInfo.InvariantCulture) }));
            var path=Write(text.ToString());var prepared=FourChannelCsvPreparation.Read(path,"Ref",1e-5,CancellationToken.None);
            prepared.Validate();
            Assert.AreEqual(4096,prepared.Metadata.Sweep.EndIndexExclusive);Assert.AreEqual(3086,prepared.SampleCount);
            Assert.AreEqual(99,prepared.OriginalIndices[99]);Assert.AreEqual(1110,prepared.OriginalIndices[100]);
            Assert.AreEqual(prepared.SampleCount,prepared.Metadata.Diagnosis.Phase.SampleCount);
            Assert.AreEqual(3,prepared.Metadata.AuxiliaryChannel);Assert.AreEqual(4,prepared.Metadata.MeasurementChannel);
            var copy=Path.Combine(directory,"copied.csv");File.Copy(path,copy);
            var signal=FourChannelCsvPreparation.Read(copy,"Signal",1e-5,CancellationToken.None);
            Assert.ThrowsExactly<ArgumentException>(()=>FourChannelCsvPreparation.ValidateDistinct(prepared,signal));
        }
    }
}
