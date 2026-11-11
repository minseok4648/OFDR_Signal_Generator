using System;
using System.Globalization;
using System.Linq;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Processing
{
    // Display conversion only: neither correlation nor the quality verdict is changed.
    public static class DocumentPhysicalAxis
    {
        public static void Validate(IndependentOfdrProcessingConfig c)
        {
            foreach (var v in new[] { c.DistanceGeometryFactor, c.DistanceOffsetMeters, c.StrainCoefficient, c.StrainScaleFactor, c.StrainOffsetMicrostrain })
                if (!ModeHopSettings.IsFinite(v)) throw new ArgumentException("거리/strain 환산 계수는 유한해야 합니다.");
            if (c.DistanceGeometryFactor <= 0 || c.StrainCoefficient <= 0 || c.StrainScaleFactor == 0)
                throw new ArgumentException("거리 기하 계수와 strain 계수는 양수, strain 배율은 0이 아니어야 합니다.");
        }
        public static void Apply(IndependentOfdrProcessingResult result)
        {
            var c = result.Config; var d = result.DocumentStages;
            if (d == null || !c.UsePhysicalAxes) return;
            Validate(c);
            var settings = c.StageComparison.Document;
            var strainPerPm = 1000.0 / (settings.WavelengthNm * c.StrainCoefficient) * c.StrainScaleFactor;
            d.StrainEnabled = true;
            foreach (var p in d.Points) p.CandidateStrainMicrostrain = c.StrainOffsetMicrostrain + p.CandidateShiftPm * strainPerPm;
            d.CalibrationNote = string.Format(CultureInfo.InvariantCulture,
                "설정 기반 strain 환산(미교정): ε=Δλ/(λ·κ) · λ={0:G8} nm, κ={1:G8}, 배율={2:G8}, strain offset={3:G8} με. 거리 offset={4:G8} m, 기하 계수={5:G8}. AUX 지연={6:G8} us, ng={7:G8}. 온도 영향 미분리, 품질 통과는 정확도 보증이 아닙니다.",
                settings.WavelengthNm, c.StrainCoefficient, c.StrainScaleFactor, c.StrainOffsetMicrostrain,
                c.DistanceOffsetMeters, c.DistanceGeometryFactor, settings.AuxiliaryDelayMicroseconds, settings.GroupIndex);
            result.PhysicalAxis = new PhysicalAxisCalibrationResult { Enabled = true, DistanceMetersPerFftBin = d.DistancePerBin,
                SpectralShiftGHzPerLag = d.FrequencyGHzPerLag, StrainMicrostrainPerLag = d.WavelengthPmPerLag * strainPerPm,
                LocalSpectrumLength = c.SegmentSize * c.SegmentIfftZeroPadFactor, SweepSpanGHz = d.CommonBandwidthGHz,
                AuxiliaryGroupIndex = settings.GroupIndex, Note = d.CalibrationNote };
            result.StrainProfile = Preview(d, c.MaxPreviewPoints, p => p.Valid ? p.CandidateStrainMicrostrain.Value : double.NaN, "내부 조건 통과 strain 환산 (με; 미교정)");
            d.CandidateStrain = Preview(d, c.MaxPreviewPoints, p => p.CandidateStrainMicrostrain.Value, "전체 strain 후보 (με; 불합격 포함)");
            result.SpectralShiftProfile = Preview(d, c.MaxPreviewPoints, p => p.Valid ? p.CandidateLag * d.FrequencyGHzPerLag : double.NaN, "Spectral shift (GHz; 상대 기준)");
            foreach (var series in new[] { result.ReferenceSpectrum, result.SignalSpectrum })
            { series.XStart = d.DistanceOffsetMeters; series.XEnd = d.DistanceOffsetMeters + (result.ReferenceSpectrumValues.Length - 1) * d.DistancePerBin; series.XAxisTitle = "위치 (m; 설정 기반)"; }
            d.Summary = d.Summary.Replace("strain 미산출.", "strain은 설정 기반으로 환산.") + "\n" + d.CalibrationNote;
        }
        private static PreviewSeries Preview(DocumentStageResult d, int limit, Func<StageQualityPoint, double> value, string y)
        {
            var n = Math.Min(d.Points.Count, limit); var values = new double[n];
            for (var i = 0; i < n; i++) values[i] = value(d.Points[n == 1 ? 0 : (int)((long)i * (d.Points.Count - 1) / (n - 1))]);
            return new PreviewSeries { Values = values, XStart = d.Points.First().DistanceMeters, XEnd = d.Points.Last().DistanceMeters,
                XAxisTitle = "위치 (m; 설정 기반)", YAxisTitle = y };
        }
    }
}
