using System;
using System.Globalization;
using System.IO;
using System.Linq;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Processing
{
    // Saved QC is a preflight check; production phase extraction still runs after it passes.
    public static class PreparedSweepQcPolicy
    {
        private static string Percent(double ratio) => (ratio * 100).ToString("G6", CultureInfo.InvariantCulture) + "%";

        public static string Describe(PreparedSweepMetadata data, string role)
        {
            if (data == null) return role + " · CH3 보조간섭계: 확정한 진단 입력이 없습니다.";
            return string.Format(CultureInfo.InvariantCulture, "{0} · {1} / S{2} · CH3 보조간섭계 · 접합 [{3:G9}, {4:G9}) ms · {5:N0} samples",
                role, Path.GetFileName(data.SourcePath), data.Sweep?.Number ?? 0,
                data.SelectedStartIndex * data.SampleIntervalSeconds * 1000, data.SelectedEndIndexExclusive * data.SampleIntervalSeconds * 1000, data.RowCount);
        }

        public static string BlockReason(PreparedSweepMetadata data, string role, double processingLimit)
        {
            var context = Describe(data, role);
            if (data == null) return context;
            if (!ValidLimit(processingLimit)) return context + "\n분석 비단조 비율 한계를 0~1의 유한한 값으로 입력하세요.";
            var diagnosis = data.Diagnosis; var settings = data.DiagnosisSettings; var phase = diagnosis?.Phase;
            if (data.Role != role || data.AuxiliaryChannel != 3 || data.MeasurementChannel != 4)
                return context + "\n진단 입력의 역할 또는 채널이 일치하지 않습니다.";
            if (phase == null || settings == null || !ValidLimit(settings.MaximumNonmonotonicRatio))
                return context + "\n위상 진단 정보가 없습니다. 이 구간의 위상 분석을 다시 실행하세요.";
            if (diagnosis.SelectedStartIndex != data.SelectedStartIndex || diagnosis.SelectedEndIndexExclusive != data.SelectedEndIndexExclusive || phase.SampleCount != data.RowCount)
                return context + "\n저장된 위상 진단과 현재 선택 구간이 다릅니다. 다시 진단하세요.";
            if (!ValidLimit(phase.NonmonotonicRatio) || !ModeHopSettings.IsFinite(phase.PhaseSpanRadians))
                return context + "\n위상 진단 수치가 유효하지 않습니다. 다시 진단하세요.";
            var limits = "진단 한계 " + Percent(settings.MaximumNonmonotonicRatio) + " / 분석 한계 " + Percent(processingLimit);
            if (phase.NonmonotonicRatio > Math.Min(settings.MaximumNonmonotonicRatio, processingLimit))
                return context + "\n위상 QC 불합격: 앵커 거부 " + Percent(phase.NonmonotonicRatio) + " · " + limits;
            if (phase.AcceptedAnchorCount < 2 || phase.PhaseSpanRadians < 2 * Math.PI)
                return context + "\n위상 span 또는 단조 앵커가 부족합니다. 선택 구간을 조정한 뒤 다시 진단하세요.";
            if (diagnosis.Decision != "PASS_PHASE_QC")
                return context + "\n저장 진단이 " + (diagnosis.Decision ?? "미완료") + "입니다. 선택 구간을 다시 진단하세요. " + limits;
            return null;
        }

        public static string PairBlockReason(PreparedSweepMetadata reference, PreparedSweepMetadata signal, double processingLimit)
        {
            var failures = new[] { BlockReason(reference, "Ref", processingLimit), BlockReason(signal, "Signal", processingLimit) }.Where(s => s != null).ToArray();
            return failures.Length == 0 ? null : string.Join("\n", failures);
        }

        public static string InputBlockReason(IndependentOfdrInput input, double processingLimit)
        {
            if (input == null) return "분석 입력이 없습니다.";
            if (new[] { input.Auxiliary, input.Reference, input.SignalAuxiliary, input.Signal }.All(c => c?.Preparation == null)) return null;
            return PairBlockReason(input.Reference?.Preparation, input.Signal?.Preparation, processingLimit);
        }

        private static bool ValidLimit(double value) => ModeHopSettings.IsFinite(value) && value >= 0 && value <= 1;
    }
}
