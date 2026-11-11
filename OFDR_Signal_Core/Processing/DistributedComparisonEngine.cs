using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Services;

namespace OFDR_Signal_Core.Processing
{
    [DataContract]
    public sealed class DistributedCurve
    {
        [DataMember] public string Pair { get; set; }
        [DataMember] public string Condition { get; set; }
        [DataMember] public string ReferencePath { get; set; }
        [DataMember] public string SignalPath { get; set; }
        [DataMember] public string ReferenceRange { get; set; }
        [DataMember] public string SignalRange { get; set; }
        [DataMember] public string Summary { get; set; }
        [DataMember] public List<StageQualityPoint> Points { get; set; }
        public double MedianZncc => Median(Points.Select(p => p.PeakZncc));
        public int Accepted => Points.Count(p => p.Valid);
        private static double Median(IEnumerable<double> values)
        {
            var a = values.Where(ModeHopSettings.IsFinite).OrderBy(x => x).ToArray();
            return a.Length == 0 ? double.NaN : (a[(a.Length - 1) / 2] + a[a.Length / 2]) / 2;
        }
    }

    [DataContract]
    public sealed class DistributedComparisonResult
    {
        [DataMember] public string Status { get; set; }
        [DataMember] public string Method { get; set; }
        [DataMember] public IndependentOfdrProcessingConfig Config { get; set; }
        [DataMember] public string Provider { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool PairOnly { get; set; }
        [DataMember] public List<DistributedCurve> Curves { get; set; } = new List<DistributedCurve>();
        [DataMember] public List<PreparedSweepMetadata> Sources { get; set; } = new List<PreparedSweepMetadata>();
    }

    // Orchestration only: all numerical processing uses the existing document stage engine.
    // No PDF consensus is inferred from peak-only profiles or from unregistered hop segments.
    public sealed class DistributedComparisonEngine
    {
        public const string Method = "각 비교 1쌍, 취득별 최장 정상 구간, 공통 상대 span. B/C 동일 mask. 단일쌍 품질 판정이며 PDF의 전체 구간/3조합 상관곡선 합의가 아닙니다. 거리 원점·홉별 절대 파장 미등록.";
        public const string PairMethod = "Ref·Signal 실제 두 취득의 B/C 비교. 취득별 최장 정상 구간, 공통 상대 span, 동일 mask. 단일쌍 ZNCC이며 반복 합의·절대 strain 교정이 아닙니다. 거리 원점·홉별 절대 파장 미등록.";

        public static IndependentOfdrProcessingConfig DefaultConfig() => new IndependentOfdrProcessingConfig {
            StageComparisonEnabled = true, AllowFailedPhaseQcForDiagnostics = true, UsePhysicalAxes = false,
            SegmentSize = 128, SegmentHop = 32, SegmentIfftZeroPadFactor = 8, MaxPreviewPoints = 2048,
            StageComparison = new StageComparisonSettings { OutputSamples = 65536,
                Document = new DocumentStageSettings { Enabled = true, PhysicalWindow = true, BandpassEnabled = true } }
        };

        public DistributedComparisonResult Run(string reference, string repeat, string heated,
            IndependentOfdrProcessingConfig config, string directory, CancellationToken token, IProgress<string> progress)
            => RunInternal(reference, repeat, heated, config, directory, token, progress, false);

        public DistributedComparisonResult RunReferenceSignal(string reference, string signal,
            IndependentOfdrProcessingConfig config, string directory, CancellationToken token, IProgress<string> progress)
            => RunInternal(reference, null, signal, config, directory, token, progress, true);

        private DistributedComparisonResult RunInternal(string reference, string repeat, string heated,
            IndependentOfdrProcessingConfig config, string directory, CancellationToken token, IProgress<string> progress, bool pairOnly)
        {
            Directory.CreateDirectory(directory);
            JsonContractSerializer.Write(Path.Combine(directory, "config.json"), config);
            JsonContractSerializer.Write(Path.Combine(directory, "inputs.json"), new Dictionary<string,string> {
                ["reference"] = reference, ["room_repeat"] = repeat, ["heated"] = heated, ["method"] = pairOnly ? PairMethod : Method });
            var result = new DistributedComparisonResult { Status = "RUNNING", Config = JsonContractSerializer.Clone(config), Method = pairOnly ? PairMethod : Method, PairOnly = pairOnly };
            var timer = System.Diagnostics.Stopwatch.StartNew(); string error = null;
            try {
                config.StageComparison.Validate(config);
                if ((pairOnly ? new[] { reference, heated } : new[] { reference, repeat, heated }).Any(p => !File.Exists(p))) throw new FileNotFoundException(pairOnly ? "Ref·Signal 원본 두 파일을 선택하세요." : "기준·상온 반복·가열 원본 세 파일을 선택하세요.");
                if (pairOnly && string.Equals(Path.GetFullPath(reference), Path.GetFullPath(heated), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Ref와 Signal에는 서로 다른 실제 취득 파일이 필요합니다.");
                if (!pairOnly && string.Equals(Path.GetFullPath(reference), Path.GetFullPath(repeat), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("상온 반복에는 기준과 다른 실제 취득 파일이 필요합니다. 자기상관을 반복성으로 표시하지 않습니다.");
                var fft = new FftEngine(); result.Provider = fft.ProviderDescription;
                if (!Environment.Is64BitProcess || !fft.NativeProviderAvailable) throw new InvalidOperationException("실제 전체 기록 처리는 x64 native MKL이 필요합니다.");
                if (pairOnly) RunPair(reference, heated, "Ref / Signal", config, directory, result, token, progress);
                else {
                    RunPair(reference, repeat, "상온 반복", config, directory, result, token, progress);
                    GC.Collect();
                    RunPair(reference, heated, "가열 비교", config, directory, result, token, progress);
                }
                result.Status = "COMPLETED";
                Validate(result);
                JsonContractSerializer.Write(Path.Combine(directory, "comparison.json"), result);
            }
            catch (OperationCanceledException ex) { result.Status = "CANCELED"; error = ex.Message; throw; }
            catch (Exception ex) { result.Status = "FAILED"; error = ex.ToString(); throw; }
            finally {
                JsonContractSerializer.Write(Path.Combine(directory, "dataset_manifest.json"), result.Sources);
                JsonContractSerializer.Write(Path.Combine(directory, "metrics.json"), new Dictionary<string,string> {
                    ["Status"] = result.Status, ["Error"] = error, ["Provider"] = result.Provider,
                    ["ElapsedSeconds"] = timer.Elapsed.TotalSeconds.ToString("R", CultureInfo.InvariantCulture),
                    ["CompletedCurves"] = result.Curves.Count.ToString(CultureInfo.InvariantCulture) });
                File.WriteAllText(Path.Combine(directory, "summary.md"), "# Distributed wavelength comparison\n\nStatus: " + result.Status + "\n\n" + result.Method + "\n\n" + error);
            }
            return result;
        }

        private static void RunPair(string refPath, string sigPath, string pair, IndependentOfdrProcessingConfig config,
            string directory, DistributedComparisonResult result, CancellationToken token, IProgress<string> progress)
        {
            var r = Read(refPath, pair + " Ref", result.Sources, token, progress);
            var s = Read(sigPath, pair + " Signal", result.Sources, token, progress);
            if (result.PairOnly && string.Equals(r.Item1.Preparation.SourceSha256, s.Item1.Preparation.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Ref와 Signal의 내용이 동일합니다. 서로 다른 실제 취득 파일을 선택하세요.");
            var input = new IndependentOfdrInput { Auxiliary = r.Item1, Reference = r.Item2, SignalAuxiliary = s.Item1, Signal = s.Item2 };
            foreach (var nominal in new[] { true, false }) {
                token.ThrowIfCancellationRequested();
                var c = JsonContractSerializer.Clone(config); var d = c.StageComparison.Document;
                d.Enabled = true; d.PhysicalWindow = true; d.BandpassEnabled = true; d.UseNominalGrid = nominal;
                d.SelectContinuousRange = true; d.DelayMode = StageDelayMode.None;
                progress?.Report(pair + " · " + (nominal ? "B 명목 시간축" : "C AUX 위상축") + " 처리");
                using (var processed = new StageComparisonEngine().Run(input, c, token)) {
                    var curve = new DistributedCurve { Pair = pair, Condition = nominal ? "B" : "C", ReferencePath = refPath, SignalPath = sigPath,
                        ReferenceRange = processed.DocumentStages.ReferenceRange, SignalRange = processed.DocumentStages.SignalRange,
                        Summary = processed.DocumentStages.Summary, Points = processed.DocumentStages.Points };
                    var folder = Path.Combine(directory, (result.PairOnly ? "pair_" : pair == "상온 반복" ? "room_" : "heated_") + curve.Condition); Directory.CreateDirectory(folder);
                    JsonContractSerializer.Write(Path.Combine(folder, "effective_config.json"), processed.Config);
                    JsonContractSerializer.Write(Path.Combine(folder, "curve.json"), curve);
                    using (var w = new StreamWriter(Path.Combine(folder, "profile.csv"))) {
                        w.WriteLine("distance_m,candidate_shift_pm,peak_zncc,separation,overlap,status");
                        foreach (var p in curve.Points) w.WriteLine(string.Join(",", new[] { p.DistanceMeters, p.CandidateShiftPm, p.PeakZncc, p.Separation, p.Overlap }.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "," + p.Status);
                    }
                    result.Curves.Add(curve);
                }
            }
        }

        private static Tuple<OfdrChannelData,OfdrChannelData> Read(string path, string role, List<PreparedSweepMetadata> sources,
            CancellationToken token, IProgress<string> progress)
        {
            progress?.Report(role + " · 전체 CSV 읽기: " + Path.GetFileName(path));
            var raw = new TekScopeCsvReader().Read(path, token);
            if (!raw.HasFourChannels || raw.IsPartialRead) throw new ArgumentException("CH1–CH4 전체 원본 Tek CSV가 필요합니다.");
            var mask = new ModeHopPreviewEngine().Detect(raw, new ModeHopSettings { ThresholdVoltage = 1.8, ExclusionTailSeconds = .0001 }, token);
            var stitched = new AuxiliaryQualityEngine().Stitch(raw, mask, token); var values = new double[stitched.Values.Length];
            for (int i = 0; i < values.Length; i++) { if ((i & 65535) == 0) token.ThrowIfCancellationRequested(); values[i] = raw.Measurement[stitched.OriginalIndices[i]]; }
            var metadata = new PreparedSweepMetadata { Role = role, SourcePath = Path.GetFullPath(path), SourceSha256 = raw.Sha256, SourceHeader = raw.Header,
                ModeHopThresholdVoltage = 1.8, ModeHopExclusionTailSeconds = .0001, SourceStartSeconds = raw.SourceStartSeconds,
                SampleIntervalSeconds = raw.SampleIntervalSeconds, RowCount = values.Length, SelectedStartIndex = 0, SelectedEndIndexExclusive = values.Length,
                Sweep = new SweepRange { StartIndex = 0, EndIndexExclusive = raw.SampleCount }, ExcludedRanges = mask.Ranges };
            sources.Add(metadata);
            Func<double[],int,OfdrChannelData> channel = (v,n) => new OfdrChannelData { Values = v, Preparation = metadata, SourcePath = path,
                ChannelNumber = n, MeasurementId = role, SampleIntervalSeconds = raw.SampleIntervalSeconds };
            return Tuple.Create(channel(stitched.Values,3), channel(values,4));
        }

        public static DistributedComparisonResult Load(string path)
        {
            var file = Directory.Exists(path) ? Path.Combine(path,"comparison.json") : path;
            if (new FileInfo(file).Length > 64 * 1024 * 1024) throw new ArgumentException("결과 파일이 너무 큽니다. 원시 파일 대신 comparison.json을 선택하세요.");
            var result = JsonContractSerializer.Read<DistributedComparisonResult>(file); Validate(result); return result;
        }
        public static void Validate(DistributedComparisonResult r)
        {
            if (r?.Status != "COMPLETED" || r.Config?.StageComparison?.Document == null || !r.Config.StageComparison.Document.Enabled || r.Curves == null || r.Curves.Count != (r.PairOnly ? 2 : 4))
                throw new ArgumentException("완료된 Ref·Signal B/C 두 곡선 또는 상온/가열 B/C 네 곡선의 comparison.json이 필요합니다.");
            r.Config.StageComparison.Document.Validate();
            foreach (var pair in r.PairOnly ? new[] { "Ref / Signal" } : new[] { "상온 반복", "가열 비교" }) foreach (var condition in new[] { "B", "C" }) {
                var matches = r.Curves.Where(c => c != null && c.Pair == pair && c.Condition == condition).ToArray();
                if (matches.Length != 1 || matches[0].Points == null || matches[0].Points.Count == 0 || matches[0].Points.Count > 10000)
                    throw new ArgumentException("중복·누락 곡선 또는 10,000점을 초과한 결과입니다.");
                double previous = double.NegativeInfinity;
                foreach (var p in matches[0].Points) {
                    if (p == null || !ModeHopSettings.IsFinite(p.DistanceMeters) || p.DistanceMeters <= previous ||
                        (ModeHopSettings.IsFinite(p.PeakZncc) && (p.PeakZncc < -1.000001 || p.PeakZncc > 1.000001)))
                        throw new ArgumentException("거리 순서 또는 ZNCC 범위가 잘못된 결과입니다.");
                    previous = p.DistanceMeters;
                }
            }
        }
    }
}
