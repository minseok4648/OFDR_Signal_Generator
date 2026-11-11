using System;
using System.IO;
using System.Linq;
using System.Threading;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;

namespace OFDR_Signal_Core.Services
{
    // UI-independent preparation of the same complete-record mask used by reflectogram/ZNCC.
    // Every capture retains its own simultaneous CH3 phase and CH4 measurement.
    public static class FourChannelCsvPreparation
    {
        public static PreparedSweepData Read(string path, string role, double phaseLimit, CancellationToken token, IProgress<OperationProgress> progress = null)
        {
            if (role != "Ref" && role != "Signal") throw new ArgumentException("Ref 또는 Signal 역할이 필요합니다.");
            var raw = new TekScopeCsvReader().Read(path, token, progress);
            if (!raw.HasFourChannels || raw.IsPartialRead) throw new ArgumentException("전체 CH1~CH4 CSV가 필요합니다.");
            var mask = new ModeHopPreviewEngine().Detect(raw, new ModeHopSettings { ThresholdVoltage = 1.8, ExclusionTailSeconds = .0001 }, token);
            var quality = new AuxiliaryQualityEngine();
            var stitched = quality.Stitch(raw, mask, token);
            var settings = new AuxiliaryInspectionSettings { MaximumNonmonotonicRatio = phaseLimit, MaximumPreviewPoints = 10000 };
            progress?.Report(new OperationProgress { Stage = role + " · 전체 기록 CH3 위상 QC", Percent = 95 });
            var diagnosis = quality.Analyze(stitched, settings, token);
            var preparation = new SweepPreparationEngine();
            var sweep = preparation.DetectSweeps(raw, token, SweepSelectionMode.RecordingStartToRecordingEnd).Single();
            return preparation.Prepare(raw, sweep, role, stitched, diagnosis.Metrics, settings, 1.8, token);
        }
        public static void ValidateDistinct(PreparedSweepData reference, PreparedSweepData signal)
        {
            if (reference == null || signal == null) throw new ArgumentException("Ref와 Signal이 필요합니다.");
            if (string.Equals(Path.GetFullPath(reference.Metadata.SourcePath), Path.GetFullPath(signal.Metadata.SourcePath), StringComparison.OrdinalIgnoreCase)
                || string.Equals(reference.Metadata.SourceSha256, signal.Metadata.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Ref와 Signal에는 서로 다른 실제 취득 파일을 선택하세요. 자기상관을 측정 비교로 표시하지 않습니다.");
        }
    }
}
