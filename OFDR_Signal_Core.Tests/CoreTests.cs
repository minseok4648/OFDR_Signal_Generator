using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;

namespace OFDR_Signal_Core.Tests
{
    [TestClass]
    public sealed class CoreTests
    {
        private string tempFolder;

        [TestInitialize]
        public void Initialize()
        {
            tempFolder = Path.Combine(Path.GetTempPath(), "OFDR_CoreTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempFolder);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);
        }

        [TestMethod]
        public void IsfReaderParsesHeaderAndSignedBigEndianVoltage()
        {
            var path = Path.Combine(tempFolder, "test.isf");
            WriteIsf(path, "CH1", new short[] { -1000, 0, 1000 }, 0.001, 0.0, 0.0, 4e-8);
            var reader = new IsfWaveformReader();
            var header = reader.ReadHeader(path);
            var samples = reader.ReadAllVoltages(path);
            Assert.AreEqual(3, header.PointCount);
            Assert.AreEqual(4e-8, header.XIncrement, 1e-15);
            CollectionAssert.AreEqual(new[] { -1.0, 0.0, 1.0 }, samples);
        }

        [TestMethod]
        public void IsfReaderRejectsTruncatedPayloadAndUnsupportedEncoding()
        {
            var truncated = Path.Combine(tempFolder, "truncated.isf");
            WriteIsf(truncated, "CH1", new short[] { 1, 2, 3 }, 1.0, 0.0, 0.0, 4e-8);
            using (var stream = new FileStream(truncated, FileMode.Open, FileAccess.Write)) stream.SetLength(stream.Length - 1);
            Assert.ThrowsExactly<InvalidDataException>(() => new IsfWaveformReader().ReadHeader(truncated));

            var unsupported = Path.Combine(tempFolder, "unsupported.isf");
            WriteIsf(unsupported, "CH1", new short[] { 1, 2, 3 }, 1.0, 0.0, 0.0, 4e-8);
            ReplaceAscii(unsupported, "BINARY", "ASCII ");
            Assert.ThrowsExactly<NotSupportedException>(() => new IsfWaveformReader().ReadHeader(unsupported));
        }

        [TestMethod]
        public void ScannerGroupsThreeChannelsAndConverterWritesIntegratedCsv()
        {
            var values = new short[] { 0, 1, -1, 10 };
            WriteIsf(Path.Combine(tempFolder, "0_0mm0000CH1.isf"), "CH1", values, 0.01, 0.0, 0.0, 4e-8);
            WriteIsf(Path.Combine(tempFolder, "0_0mm0000CH2.isf"), "CH2", values, 0.02, 0.0, 0.0, 4e-8);
            WriteIsf(Path.Combine(tempFolder, "0_0mm0000CH3.isf"), "CH3", values, 0.03, 0.0, 0.0, 4e-8);
            var manifest = new OfdrDatasetScanner().Scan(tempFolder);
            Assert.AreEqual(1, manifest.Sets.Count);
            Assert.IsTrue(manifest.Sets[0].IsValid, manifest.Sets[0].ValidationMessage);
            var output = Path.Combine(tempFolder, "converted_csv");
            var result = new OfdrSetCsvConverter().Convert(manifest.Sets[0], output, false, CancellationToken.None);
            var lines = File.ReadAllLines(result.CsvPath);
            Assert.AreEqual(5, lines.Length);
            Assert.AreEqual("Index,Time_s,CH1_Ref_V,CH2_Xpol_V,CH3_Ypol_V", lines[0]);
            Assert.IsTrue(File.Exists(result.MetadataPath));
            var metadata = JsonContractSerializer.Read<CsvSetMetadata>(result.MetadataPath);
            Assert.AreEqual(4L, metadata.RowCount);
            Assert.AreEqual(OfdrSetCsvConverter.ComputeSha256(result.CsvPath, CancellationToken.None), metadata.CsvSha256);
            Assert.AreEqual(3, metadata.Sources.Count);
        }

        [TestMethod]
        public void ScannerMarksMissingChannelAndTimeAxisMismatchInvalid()
        {
            var values = new short[] { 0, 1, 2, 3 };
            WriteIsf(Path.Combine(tempFolder, "0_0mm0000CH1.isf"), "CH1", values, 1.0, 0.0, 0.0, 4e-8);
            WriteIsf(Path.Combine(tempFolder, "0_0mm0000CH2.isf"), "CH2", values, 1.0, 0.0, 0.0, 4e-8);
            var missing = new OfdrDatasetScanner().Scan(tempFolder).Sets.Single();
            Assert.IsFalse(missing.IsValid);
            StringAssert.Contains(missing.ValidationMessage, "CH3 missing");

            WriteIsf(Path.Combine(tempFolder, "0_0mm0000CH3.isf"), "CH3", values, 1.0, 0.0, 0.0, 8e-8);
            var mismatched = new OfdrDatasetScanner().Scan(tempFolder).Sets.Single();
            Assert.IsFalse(mismatched.IsValid);
            StringAssert.Contains(mismatched.ValidationMessage, "time axes differ");
        }

        [TestMethod]
        public void ConverterDetectsStaleCsvAndOverwriteRepairsIt()
        {
            var set = WriteThreeChannelSet(64);
            var output = Path.Combine(tempFolder, "converted_csv");
            var converter = new OfdrSetCsvConverter();
            var first = converter.Convert(set, output, false, CancellationToken.None);
            File.AppendAllText(first.CsvPath, "corrupt");
            Assert.ThrowsExactly<IOException>(() => converter.Convert(set, output, false, CancellationToken.None));
            var repaired = converter.Convert(set, output, true, CancellationToken.None);
            var metadata = JsonContractSerializer.Read<CsvSetMetadata>(repaired.MetadataPath);
            Assert.AreEqual(metadata.CsvSha256, OfdrSetCsvConverter.ComputeSha256(repaired.CsvPath, CancellationToken.None));
        }

        [TestMethod]
        public void ConvertedCsvReaderFeedsProcessingEngine()
        {
            var set = WriteThreeChannelSet(128);
            var output = Path.Combine(tempFolder, "converted_csv");
            var converted = new OfdrSetCsvConverter().Convert(set, output, false, CancellationToken.None);
            var data = new ConvertedOfdrCsvReader().Read(converted.CsvPath, CancellationToken.None);
            Assert.AreEqual(128, data.RowCount);
            Assert.AreEqual(set.SetId, data.SetId);
            Assert.AreEqual(4e-8, data.SampleIntervalSeconds, 1e-15);

            var config = new OfdrProcessingConfig
            {
                EnableKLinearization = false,
                UseHannWindow = false,
                SegmentSize = 32,
                SegmentHop = 16,
                SegmentIfftZeroPadFactor = 2,
                NormalizeConjugateMultiply = false,
                FinalMetricMode = OfdrProcessingEngine.MetricAbsolutePeakOffset,
                FinalSmoothingWindow = 1,
                MaxPreviewPoints = 128
            };
            using (var result = new OfdrProcessingEngine().RunConvertedCsv(data, config, CancellationToken.None))
            {
                Assert.AreEqual(set.SetId, result.SetId);
                Assert.AreEqual(128, result.XPolarization.SampleCount);
                Assert.IsTrue(result.XPolarization.SegmentCount > 0);
                Assert.IsTrue(result.ReferenceSpectrum.Length == 128);
            }
        }

        [TestMethod]
        public void ChannelCsvConverterWritesAndReadsOneChannel()
        {
            var isfPath = Path.Combine(tempFolder, "0_4mm0000CH2.isf");
            var samples = new short[] { -100, 0, 100, 200 };
            WriteIsf(isfPath, "CH2", samples, 0.01, 0.0, 0.0, 4e-8);
            var output = Path.Combine(tempFolder, "converted_channels");
            var converted = new OfdrChannelCsvConverter().Convert(isfPath, output, false, CancellationToken.None);
            var lines = File.ReadAllLines(converted.CsvPath);
            Assert.AreEqual("Index,Time_s,Voltage_V", lines[0]);
            Assert.AreEqual(5, lines.Length);
            var data = new OfdrChannelFileReader().Read(converted.CsvPath, 2, CancellationToken.None);
            Assert.AreEqual("0_4mm0000CH2", data.MeasurementId);
            Assert.AreEqual(0.4, data.DisplacementMm, 1e-12);
            Assert.AreEqual(2, data.ChannelNumber);
            CollectionAssert.AreEqual(new[] { -1.0, 0.0, 1.0, 2.0 }, data.Values);
            var metadata = JsonContractSerializer.Read<ChannelCsvMetadata>(converted.MetadataPath);
            Assert.AreEqual(2, metadata.ChannelNumber);
            Assert.AreEqual(4L, metadata.RowCount);
        }

        [TestMethod]
        public void IndependentPairEngineProducesKAndCorrelationViews()
        {
            const int n = 4096;
            var auxiliary = new short[n];
            var reference = new short[n];
            var signal = new short[n];
            for (var i = 0; i < n; i++)
            {
                var phase = (0.22 * i) + (0.0000015 * i * i);
                auxiliary[i] = (short)Math.Round(12000.0 * Math.Cos(phase));
                reference[i] = (short)Math.Round(10000.0 * Math.Sin(phase * 0.08));
                signal[i] = (short)Math.Round(10000.0 * Math.Sin((phase * 0.08) + 0.35));
            }
            var auxPath = Path.Combine(tempFolder, "0_0mm0000CH1.isf");
            var sigAuxPath = Path.Combine(tempFolder, "0_4mm0000CH1.isf");
            var refPath = Path.Combine(tempFolder, "0_0mm0000CH2.isf");
            var sigPath = Path.Combine(tempFolder, "0_4mm0000CH2.isf");
            WriteIsf(auxPath, "CH1", auxiliary, 0.0001, 0.0, 0.0, 4e-8);
            WriteIsf(sigAuxPath, "CH1", auxiliary, 0.0001, 0.0, 0.0, 4e-8);
            WriteIsf(refPath, "CH2", reference, 0.0001, 0.0, 0.0, 4e-8);
            WriteIsf(sigPath, "CH2", signal, 0.0001, 0.0, 0.0, 4e-8);
            var reader = new OfdrChannelFileReader();
            var input = new IndependentOfdrInput
            {
                Auxiliary = reader.Read(auxPath, 1, CancellationToken.None),
                SignalAuxiliary = reader.Read(sigAuxPath, 1, CancellationToken.None),
                Reference = reader.Read(refPath, 2, CancellationToken.None),
                Signal = reader.Read(sigPath, 2, CancellationToken.None)
            };
            var config = new IndependentOfdrProcessingConfig
            {
                MaxAuxNonmonotonicRatio = 0.02,
                WindowFunction = "Hamming",
                SegmentSize = 64,
                SegmentHop = 32,
                SegmentIfftZeroPadFactor = 2,
                MaxPreviewPoints = 512,
                RestrictToMeasurementRange = false
            };
            var engine = new IndependentOfdrProcessingEngine();
            using (var result = engine.Run(input, config, CancellationToken.None))
            {
                Assert.AreEqual(n, result.SampleCount);
                Assert.AreEqual("Xpol (CH2)", result.Polarization);
                Assert.IsNotNull(result.KDiagnostics.WrappedPhase);
                Assert.IsNotNull(result.KDiagnostics.UnwrappedPhase);
                Assert.IsNotNull(result.PreKReferenceSpectrum);
                Assert.IsNotNull(result.PreKSignalSpectrum);
                Assert.AreEqual("FFT bin (K 보정 전)", result.PreKSignalSpectrum.XAxisTitle);
                Assert.AreEqual("거리 (m)", result.SignalSpectrum.XAxisTitle);
                Assert.IsTrue(result.SegmentStarts.Length > 1);
                Assert.IsTrue(result.SelectedSegment.Correlation.Values.Length <= 128);
                Assert.IsTrue(result.SelectedSegment.CorrelationZoom.Values.Length < result.SelectedSegment.Correlation.Values.Length);
                Assert.AreEqual(64, result.SelectedSegment.WindowedSignalSegment.Values.Length);
                Assert.AreEqual(64, result.SelectedSegment.WindowedReferenceSegment.Values.Length);
                Assert.AreEqual(128, result.SelectedSegment.SignalIfft.Values.Length);
                Assert.AreEqual(128, result.SelectedSegment.ReferenceIfft.Values.Length);
                Assert.IsNotNull(result.StrainProfile);
                Assert.IsTrue(result.PhysicalAxis.DistanceMetersPerFftBin > 0.0);
                Assert.AreNotEqual(0.0, result.PhysicalAxis.StrainMicrostrainPerLag);
                var inspected = engine.InspectSegment(result, 1);
                Assert.AreEqual(1, inspected.SegmentIndex);
                Assert.IsTrue(inspected.PeakIndex >= 0);
            }
        }

        [TestMethod]
        public void PreKAndLinearizedFftAreProducedFromDifferentSampleGrids()
        {
            var input = CreateSyntheticIndependentInput(4096);
            var config = new IndependentOfdrProcessingConfig
            {
                MaxAuxNonmonotonicRatio = 0.02,
                WindowFunction = "Hann",
                SegmentSize = 64,
                SegmentHop = 32,
                SegmentIfftZeroPadFactor = 2,
                MaxPreviewPoints = 4096,
                RestrictToMeasurementRange = false
            };
            using (var result = new IndependentOfdrProcessingEngine().Run(input, config, CancellationToken.None))
            {
                Assert.AreEqual(input.Signal.Values[0], result.SignalRaw.Values[0], 1e-12, "Raw 화면은 K 보정 전 입력이어야 합니다.");
                Assert.AreEqual(2048, result.PreKSignalSpectrum.Values.Length);
                Assert.AreEqual(2048, result.SignalSpectrum.Values.Length);
                var totalDifference = result.PreKSignalSpectrum.Values
                    .Zip(result.SignalSpectrum.Values, (before, after) => Math.Abs(before - after))
                    .Sum();
                Assert.IsTrue(totalDifference > 1e-3, "비선형 sweep에서 K 보정 전/후 FFT가 동일하면 안 됩니다.");
            }
        }

        [TestMethod]
        public void WindowIsAppliedAfterFullFftAndBeforeSegmentIfft()
        {
            var input = CreateSyntheticIndependentInput(4096);
            var rectangularConfig = new IndependentOfdrProcessingConfig
            {
                MaxAuxNonmonotonicRatio = 0.02,
                WindowFunction = "Rectangular",
                SegmentSize = 64,
                SegmentHop = 32,
                SegmentIfftZeroPadFactor = 2,
                MaxPreviewPoints = 4096,
                RestrictToMeasurementRange = false
            };
            var hannConfig = new IndependentOfdrProcessingConfig
            {
                MaxAuxNonmonotonicRatio = 0.02,
                WindowFunction = "Hann",
                SegmentSize = 64,
                SegmentHop = 32,
                SegmentIfftZeroPadFactor = 2,
                MaxPreviewPoints = 4096,
                RestrictToMeasurementRange = false
            };
            var engine = new IndependentOfdrProcessingEngine();
            using (var rectangular = engine.Run(input, rectangularConfig, CancellationToken.None))
            using (var hann = engine.Run(input, hannConfig, CancellationToken.None))
            {
                Assert.AreEqual(rectangular.ReferenceSpectrumValues.Length, hann.ReferenceSpectrumValues.Length);
                for (var i = 0; i < rectangular.ReferenceSpectrumValues.Length; i += 97)
                    Assert.AreEqual(rectangular.ReferenceSpectrumValues[i].Magnitude, hann.ReferenceSpectrumValues[i].Magnitude, 1e-8,
                        "Window 설정이 전체 FFT 결과를 바꾸면 안 됩니다.");

                var rectangularSegment = engine.InspectSegment(rectangular, 1);
                var hannSegment = engine.InspectSegment(hann, 1);
                Assert.AreEqual(rectangularSegment.ReferenceSegment.Values[0], rectangularSegment.WindowedReferenceSegment.Values[0], 1e-9);
                Assert.AreEqual(0.0, hannSegment.WindowedReferenceSegment.Values[0], 1e-9);
                Assert.IsTrue(hannSegment.ReferenceSegment.Values[0] > hannSegment.WindowedReferenceSegment.Values[0]);
                Assert.AreEqual(128, hannSegment.ReferenceIfft.Values.Length);
                Assert.AreEqual(128, hannSegment.SignalIfft.Values.Length);
                Assert.IsTrue(hannSegment.Correlation.Values.Length <= 128,
                    "상관 그래프는 설정된 최대 spectral-shift 탐색 구간만 표시해야 합니다.");
            }
        }

        [TestMethod]
        public void DualSweepKLinearizationUsesEachSweepsOwnAuxiliaryPhase()
        {
            const int n = 8192;
            var referenceAuxiliary = new double[n];
            var signalAuxiliary = new double[n];
            var reference = new double[n];
            var signal = new double[n];
            for (var i = 0; i < n; i++)
            {
                var referencePhase = (0.18 * i) + (0.0000012 * i * i);
                var signalPhase = (0.16 * i) + (0.0000021 * i * i);
                referenceAuxiliary[i] = Math.Cos(referencePhase);
                signalAuxiliary[i] = Math.Cos(signalPhase);
                reference[i] = Math.Sin(referencePhase * 0.071);
                signal[i] = Math.Sin(signalPhase * 0.071);
            }

            var fft = new FftEngine();
            var result = new AuxKLinearizer(fft).LinearizeSweepsDetailed(
                referenceAuxiliary, reference, signalAuxiliary, signal,
                0.02, 512, CancellationToken.None);

            Assert.IsNotNull(result.ReferenceDiagnostics.UniformPhaseGrid);
            Assert.IsNotNull(result.SignalDiagnostics.UniformPhaseGrid);
            Assert.AreEqual(result.ReferenceDiagnostics.UniformPhaseGrid.XEnd,
                result.SignalDiagnostics.UniformPhaseGrid.XEnd, 1e-12);
            Assert.AreEqual(result.Reference.Length, result.Signal.Length);

            var squaredError = 0.0;
            var squaredReference = 0.0;
            for (var i = 512; i < n - 512; i++)
            {
                var difference = result.Reference[i] - result.Signal[i];
                squaredError += difference * difference;
                squaredReference += result.Reference[i] * result.Reference[i];
            }
            var normalizedRmsError = Math.Sqrt(squaredError / squaredReference);
            Assert.IsTrue(normalizedRmsError < 0.08,
                "기준/측정 sweep을 각 CH1으로 독립 보정하면 동일한 normalized-k 신호가 복원되어야 합니다. 실제 오차=" + normalizedRmsError);
        }

        [TestMethod]
        public void IndependentEnginePublishesNormalizedDbFftAndSpectralShift()
        {
            var input = CreateSyntheticIndependentInput(4096);
            using (var result = new IndependentOfdrProcessingEngine().Run(input, new IndependentOfdrProcessingConfig
            {
                MaxAuxNonmonotonicRatio = 0.02,
                SegmentSize = 64,
                SegmentHop = 32,
                SegmentIfftZeroPadFactor = 2,
                MaxPreviewPoints = 4096,
                RestrictToMeasurementRange = false,
                FftDbFloor = -120.0
            }, CancellationToken.None))
            {
                Assert.AreEqual("반사 신호 (dB, 최대값 기준)", result.SignalSpectrum.YAxisTitle);
                Assert.AreEqual(0.0, result.SignalSpectrum.Values.Max(), 1e-9);
                Assert.IsTrue(result.SignalSpectrum.Values.Min() >= -120.0);
                Assert.IsNotNull(result.SpectralShiftProfile);
                Assert.AreEqual("Rayleigh spectral shift (GHz)", result.SpectralShiftProfile.YAxisTitle);
                Assert.IsTrue(result.PhysicalAxis.SpectralShiftGHzPerLag > 0.0);
            }
        }

        [TestMethod]
        public void IndependentInputRejectsSignalAuxiliaryFromDifferentSweep()
        {
            var input = CreateSyntheticIndependentInput(4096);
            input.SignalAuxiliary.DisplacementMm = 0.5;
            Assert.ThrowsExactly<InvalidOperationException>(() => IndependentOfdrProcessingEngine.ValidateInput(input));
        }

        [TestMethod]
        public void EnergyNormalizedCorrelationReturnsUnitZeroLagForIdenticalLocalSpectra()
        {
            var input = CreateSyntheticIndependentInput(4096);
            input.Signal.Values = input.Reference.Values.ToArray();
            var config = new IndependentOfdrProcessingConfig
            {
                MaxAuxNonmonotonicRatio = 0.02,
                WindowFunction = "Hann",
                SegmentSize = 64,
                SegmentHop = 32,
                SegmentIfftZeroPadFactor = 2,
                NormalizeCrossCorrelation = true,
                RestrictToMeasurementRange = false
            };
            using (var result = new IndependentOfdrProcessingEngine().Run(input, config, CancellationToken.None))
            {
                Assert.AreEqual(0, result.SelectedSegment.PeakLagBin);
                Assert.AreEqual(1.0, result.SelectedSegment.PeakMagnitude, 1e-8);
            }
        }

        [TestMethod]
        public void ConverterCancellationRemovesPartialAndFinalOutputs()
        {
            var set = WriteThreeChannelSet(300000);
            var output = Path.Combine(tempFolder, "cancelled_csv");
            var cancellation = new CancellationTokenSource();
            var progress = new CancellingProgress(cancellation);
            Assert.ThrowsExactly<OperationCanceledException>(() => new OfdrSetCsvConverter().Convert(set, output, false, cancellation.Token, progress));
            Assert.IsFalse(File.Exists(Path.Combine(output, set.SetId + ".csv")));
            Assert.IsFalse(File.Exists(Path.Combine(output, set.SetId + ".csv.partial")));
            Assert.IsFalse(File.Exists(Path.Combine(output, set.SetId + ".metadata.json")));
        }

        [TestMethod]
        public void KLinearizerRecoversUniformPhaseGrid()
        {
            const int n = 4096;
            var reference = new double[n];
            var x = new double[n];
            var y = new double[n];
            for (var i = 0; i < n; i++)
            {
                var phase = (0.25 * i) + (0.000002 * i * i);
                reference[i] = Math.Cos(phase);
                x[i] = Math.Sin(phase * 0.2);
                y[i] = Math.Cos(phase * 0.15);
            }
            var result = new AuxKLinearizer(new FftEngine()).Linearize(reference, x, y, 0.01, CancellationToken.None);
            Assert.AreEqual(n, result.Reference.Length);
            Assert.IsTrue(result.Metrics.PhaseSpanRadians > 100.0);
            Assert.IsTrue(result.Metrics.NonmonotonicRatio < 0.01);
            Assert.IsTrue(result.Metrics.AcceptedAnchorCount > n * 0.99);
            var spectrum = result.XPolarization.Select(value => new Complex(value, 0.0)).ToArray();
            new FftEngine().ForwardInPlace(spectrum);
            var restoredBin = Enumerable.Range(1, n / 2 - 1).OrderByDescending(index => spectrum[index].Magnitude).First();
            var expectedBin = result.Metrics.PhaseSpanRadians * 0.2 / (2.0 * Math.PI);
            Assert.AreEqual(expectedBin, restoredBin, 2.0, "Known phase-domain frequency was not restored after k-linearization.");
        }

        [TestMethod]
        public void MathNetFftMatchesDirectDftForSmallVector()
        {
            var input = Enumerable.Range(0, 16).Select(i => new Complex(Math.Sin(i * 0.3), Math.Cos(i * 0.2))).ToArray();
            var expected = DirectDft(input, false);
            var actual = input.ToArray();
            new FftEngine().ForwardInPlace(actual);
            for (var i = 0; i < input.Length; i++) Assert.AreEqual(expected[i].Magnitude, actual[i].Magnitude, 1e-9);
        }

        [TestMethod]
        public void StreamingSegmentMetricsMatchIndependentInMemoryCalculation()
        {
            const int n = 256;
            var reference = new short[n];
            var x = new short[n];
            var y = new short[n];
            for (var i = 0; i < n; i++)
            {
                reference[i] = (short)Math.Round(12000.0 * Math.Sin(2.0 * Math.PI * 13.0 * i / n));
                x[i] = (short)Math.Round(10000.0 * Math.Sin((2.0 * Math.PI * 13.0 * i / n) + 0.35));
                y[i] = (short)Math.Round(9000.0 * Math.Sin((2.0 * Math.PI * 13.0 * i / n) - 0.2));
            }
            WriteIsf(Path.Combine(tempFolder, "0_0mm0000CH1.isf"), "CH1", reference, 0.0001, 0.0, 0.0, 4e-8);
            WriteIsf(Path.Combine(tempFolder, "0_0mm0000CH2.isf"), "CH2", x, 0.0001, 0.0, 0.0, 4e-8);
            WriteIsf(Path.Combine(tempFolder, "0_0mm0000CH3.isf"), "CH3", y, 0.0001, 0.0, 0.0, 4e-8);
            var set = new OfdrDatasetScanner().Scan(tempFolder).Sets.Single();
            var config = new OfdrProcessingConfig
            {
                EnableKLinearization = false,
                UseHannWindow = false,
                SegmentSize = 32,
                SegmentHop = 16,
                SegmentIfftZeroPadFactor = 2,
                NormalizeConjugateMultiply = false,
                FinalMetricMode = OfdrProcessingEngine.MetricAbsolutePeakOffset,
                FinalSmoothingWindow = 1,
                MaxPreviewPoints = 128
            };
            using (var result = new OfdrProcessingEngine().RunSet(set, config, CancellationToken.None))
            {
                var fft = new FftEngine();
                for (var segment = 0; segment < result.XPolarization.SegmentCount; segment++)
                {
                    var start = result.XPolarization.SegmentStartIndices[segment];
                    var padded = new Complex[result.XPolarization.SegmentIfftLength];
                    for (var i = 0; i < config.SegmentSize; i++)
                        padded[i] = result.XPolarization.Spectrum[start + i] * Complex.Conjugate(result.ReferenceSpectrum[start + i]);
                    fft.InverseInPlace(padded);
                    var center = padded.Length / 2;
                    var shifted = Enumerable.Range(0, padded.Length).Select(i => padded[(i + center) % padded.Length]).ToArray();
                    var peak = Enumerable.Range(0, shifted.Length).OrderByDescending(i => shifted[i].Magnitude).First();
                    Assert.AreEqual(Math.Abs(peak - center), result.XPolarization.RepresentativeValues[segment], 1e-9);
                }
            }
        }

        [TestMethod]
        public void ActualDatasetContainsThirtyThreeCompleteSetsWhenAvailable()
        {
            const string path = @"C:\Project\OFDR\Datas\OFDR Raw data";
            if (!Directory.Exists(path)) Assert.Inconclusive("Actual OFDR data folder is not present.");
            var manifest = new OfdrDatasetScanner().Scan(path);
            Assert.AreEqual(33, manifest.Sets.Count);
            Assert.AreEqual(33, manifest.Sets.Count(value => value.IsValid));
            Assert.IsTrue(manifest.Sets.All(value => value.PointCount == 5000000));
        }

        private static Complex[] DirectDft(Complex[] input, bool inverse)
        {
            var output = new Complex[input.Length];
            var direction = inverse ? 1.0 : -1.0;
            for (var k = 0; k < input.Length; k++)
            {
                for (var n = 0; n < input.Length; n++)
                {
                    var angle = direction * 2.0 * Math.PI * k * n / input.Length;
                    output[k] += input[n] * new Complex(Math.Cos(angle), Math.Sin(angle));
                }
                if (inverse) output[k] /= input.Length;
            }
            return output;
        }

        private OfdrChannelSet WriteThreeChannelSet(int count)
        {
            var values = Enumerable.Range(0, count).Select(index => (short)((index % 2000) - 1000)).ToArray();
            WriteIsf(Path.Combine(tempFolder, "0_0mm0000CH1.isf"), "CH1", values, 0.001, 0.0, 0.0, 4e-8);
            WriteIsf(Path.Combine(tempFolder, "0_0mm0000CH2.isf"), "CH2", values, 0.002, 0.0, 0.0, 4e-8);
            WriteIsf(Path.Combine(tempFolder, "0_0mm0000CH3.isf"), "CH3", values, 0.003, 0.0, 0.0, 4e-8);
            return new OfdrDatasetScanner().Scan(tempFolder).Sets.Single();
        }

        private static IndependentOfdrInput CreateSyntheticIndependentInput(int count)
        {
            var auxiliary = new double[count];
            var reference = new double[count];
            var signal = new double[count];
            for (var i = 0; i < count; i++)
            {
                var phase = (0.22 * i) + (0.0000015 * i * i);
                auxiliary[i] = Math.Cos(phase);
                reference[i] = Math.Sin(phase * 0.08);
                signal[i] = Math.Sin((phase * 0.08) + 0.35);
            }
            return new IndependentOfdrInput
            {
                Auxiliary = CreateChannel("0_0mm0000CH1", 0.0, 1, auxiliary),
                SignalAuxiliary = CreateChannel("0_4mm0000CH1", 0.4, 1, auxiliary.ToArray()),
                Reference = CreateChannel("0_0mm0000CH2", 0.0, 2, reference),
                Signal = CreateChannel("0_4mm0000CH2", 0.4, 2, signal)
            };
        }

        private static OfdrChannelData CreateChannel(string id, double displacementMm, int channel, double[] values)
        {
            return new OfdrChannelData
            {
                MeasurementId = id,
                DisplacementMm = displacementMm,
                Repetition = "0000",
                ChannelNumber = channel,
                SampleIntervalSeconds = 4e-8,
                Values = values
            };
        }

        private static void ReplaceAscii(string path, string oldValue, string newValue)
        {
            Assert.AreEqual(oldValue.Length, newValue.Length);
            var bytes = File.ReadAllBytes(path);
            var text = Encoding.ASCII.GetString(bytes);
            var index = text.IndexOf(oldValue, StringComparison.Ordinal);
            Assert.IsTrue(index >= 0);
            var replacement = Encoding.ASCII.GetBytes(newValue);
            Array.Copy(replacement, 0, bytes, index, replacement.Length);
            File.WriteAllBytes(path, bytes);
        }

        private sealed class CancellingProgress : IProgress<OperationProgress>
        {
            private readonly CancellationTokenSource cancellation;

            public CancellingProgress(CancellationTokenSource cancellation)
            {
                this.cancellation = cancellation;
            }

            public void Report(OperationProgress value)
            {
                if (value != null && value.Stage == "Convert CSV" && value.Completed > 0) cancellation.Cancel();
            }
        }

        private static void WriteIsf(string path, string channel, short[] samples, double yMultiplier, double yOffset, double yZero, double xIncrement)
        {
            var payloadLength = samples.Length * 2;
            var payloadText = payloadLength.ToString(CultureInfo.InvariantCulture);
            var preamble = string.Format(CultureInfo.InvariantCulture,
                ":WFMPRE:NR_PT {0};:WFMPRE:BYT_NR 2;BIT_NR 16;ENCDG BINARY;BN_FMT RI;BYT_OR MSB;WFID Test {1};NR_PT {0};PT_FMT Y;XUNIT s;XINCR {2:G17};XZERO 0;PT_OFF 0;YUNIT V;YMULT {3:G17};YOFF {4:G17};YZERO {5:G17};:CURVE #{6}{7}",
                samples.Length, channel, xIncrement, yMultiplier, yOffset, yZero, payloadText.Length, payloadText);
            using (var stream = File.Create(path))
            {
                var header = Encoding.ASCII.GetBytes(preamble);
                stream.Write(header, 0, header.Length);
                foreach (var sample in samples)
                {
                    stream.WriteByte((byte)((sample >> 8) & 0xff));
                    stream.WriteByte((byte)(sample & 0xff));
                }
            }
        }
    }
}
