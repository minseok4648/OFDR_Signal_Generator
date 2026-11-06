using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;

namespace OFDR_Signal_Core.Services
{
    // Loads C# harness profiles without rerunning the Python renderer or treating lag as strain.
    public sealed class NotebookTemperatureComparisonLoader
    {
        public NotebookTemperatureComparison Load(string directory, CancellationToken token)
        {
            directory = Path.GetFullPath(directory);
            var config = JsonContractSerializer.Read<NotebookTemperatureConfig>(Path.Combine(directory, "config.json"));
            config.Validate();
            if (JsonContractSerializer.Read<ExecutionStatus>(Path.Combine(directory, "metrics.json")).Status != "COMPLETED")
                throw new InvalidDataException("완료된 C# 재현 결과 폴더가 필요합니다.");
            var sources = JsonContractSerializer.Read<NotebookComparisonSource[]>(Path.Combine(directory, "dataset_manifest.json"));
            var reference = sources.Single(s => s.Key == "reference");
            var values = new double[config.ResampledSamples];
            using (var stream = File.OpenRead(Path.Combine(directory, "reference_klinear.bin")))
            using (var reader = new BinaryReader(stream))
            {
                if (stream.Length != (long)values.Length * sizeof(double)) throw new InvalidDataException("Ref 보간 데이터 길이가 설정과 다릅니다.");
                for (var i = 0; i < values.Length; i++)
                {
                    if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
                    values[i] = reader.ReadDouble();
                    if (!ModeHopSettings.IsFinite(values[i])) throw new InvalidDataException("Ref 보간 데이터에 유효하지 않은 값이 있습니다.");
                }
            }
            var engine = new NotebookTemperatureReproductionEngine();
            engine.RequireNativeProvider();
            var spectrum = engine.BuildSpectrum(values);
            var self = engine.Compare(spectrum, spectrum, config, token);
            var result = new NotebookTemperatureComparison { ResultDirectory = directory, Config = config };
            result.Curves.Add(Curve("Ref", reference, reference, self));
            for (var i = 0; i < config.Labels.Length; i++)
            {
                var source = sources.Single(s => s.Key == "measurement_" + i);
                var profile = ReadProfile(Path.Combine(directory, "measurement_" + i + "_profile.csv"), config, token);
                result.Curves.Add(Curve(config.Labels[i], reference, source, profile));
            }
            return result;
        }

        private static NotebookComparisonCurve Curve(string label, NotebookComparisonSource reference, NotebookComparisonSource source, NotebookTemperatureProfile profile)
        {
            if (profile.Positions.Length > 10000 || profile.Shifts.Length != profile.Positions.Length || !profile.Shifts.Any(ModeHopSettings.IsFinite))
                throw new InvalidDataException("비교 곡선은 최대 10,000점의 유효한 프로파일이어야 합니다.");
            return new NotebookComparisonCurve { Label = label, Reference = reference, Source = source, Profile = profile };
        }

        private static NotebookTemperatureProfile ReadProfile(string path, NotebookTemperatureConfig config, CancellationToken token)
        {
            var count = (config.StopBin - config.FirstBin - 1) / config.SegmentStep + 1;
            if (count > 10000) throw new InvalidDataException("UI에는 최대 10,000점의 프로파일만 전달합니다.");
            var result = new NotebookTemperatureProfile { Positions = new int[count], Shifts = new double[count] };
            using (var reader = new StreamReader(path))
            {
                if (reader.ReadLine() != "profile_index,fft_bin,shift_samples") throw new InvalidDataException("C# 프로파일 CSV 헤더가 올바르지 않습니다.");
                for (var i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var line = reader.ReadLine();
                    var cells = line?.Split(',');
                    int index, bin; double lag;
                    if (cells == null || cells.Length != 3 || !int.TryParse(cells[0], out index) || index != i
                        || !int.TryParse(cells[1], out bin) || bin != config.FirstBin + i * config.SegmentStep
                        || !double.TryParse(cells[2], NumberStyles.Float, CultureInfo.InvariantCulture, out lag) || double.IsInfinity(lag))
                        throw new InvalidDataException("C# 프로파일 좌표 또는 값 오류: " + (i + 2) + "행");
                    var complete = bin - config.SegmentSize / 2 >= 0
                        && bin - config.SegmentSize / 2 + config.SegmentSize <= config.ResampledSamples / 2;
                    if (complete == double.IsNaN(lag)) throw new InvalidDataException("프로파일 경계 NaN 위치가 설정과 다릅니다.");
                    result.Positions[i] = bin; result.Shifts[i] = lag;
                }
                if (reader.ReadLine() != null) throw new InvalidDataException("프로파일 행 수가 설정보다 많습니다.");
            }
            return result;
        }

        [DataContract]
        private sealed class ExecutionStatus { [DataMember] public string Status { get; set; } }
    }

    public sealed class NotebookTemperatureComparison
    {
        public string ResultDirectory { get; set; }
        public NotebookTemperatureConfig Config { get; set; }
        public List<NotebookComparisonCurve> Curves { get; } = new List<NotebookComparisonCurve>();
    }

    public sealed class NotebookComparisonCurve
    {
        public string Label { get; set; }
        public NotebookComparisonSource Reference { get; set; }
        public NotebookComparisonSource Source { get; set; }
        public NotebookTemperatureProfile Profile { get; set; }
    }

    [DataContract]
    public sealed class NotebookComparisonSource
    {
        [DataMember] public string Key { get; set; }
        [DataMember] public string Path { get; set; }
        [DataMember] public string Sha256 { get; set; }
        [DataMember] public NotebookSweepMetrics Processing { get; set; }
    }
}
