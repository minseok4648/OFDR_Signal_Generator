using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization;

namespace OFDR_Signal_Core.Models
{
    public enum StageDelayMode { None, Microseconds, ReflectionDistance }
    public enum StageCorrelationMode { Legacy, CircularZncc, OverlapZncc }

    [DataContract]
    public sealed class DocumentStageSettings
    {
        [DataMember] public bool Enabled { get; set; }
        [DataMember] public bool SelectContinuousRange { get; set; } = true;
        [DataMember] public int ReferenceRangeIndex { get; set; } = -1;
        [DataMember] public int SignalRangeIndex { get; set; } = -1;
        [DataMember] public int MinimumRangeSamples { get; set; } = 1024;
        [DataMember] public bool BandpassEnabled { get; set; }
        [DataMember] public double LowMHz { get; set; } = 1;
        [DataMember] public double HighMHz { get; set; } = 12;
        [DataMember] public bool AutoLimitBandpass { get; set; } = true;
        [DataMember] public double EdgeGuardMicroseconds { get; set; } = 2;
        [DataMember] public StageDelayMode DelayMode { get; set; }
        [DataMember] public double DelayMicroseconds { get; set; } = .401;
        [DataMember] public double ReflectionDistanceMeters { get; set; } = 18.14;
        [DataMember] public double AuxiliaryDelayMicroseconds { get; set; } = .979;
        [DataMember] public double GroupIndex { get; set; } = 1.4682;
        [DataMember] public double WavelengthNm { get; set; } = 1550;
        [DataMember] public bool IncreasingWavelength { get; set; } = true;
        [DataMember] public bool UseNominalGrid { get; set; }
        [DataMember] public double NominalSweepNmPerSecond { get; set; } = 50;
        [DataMember] public bool PhysicalWindow { get; set; }
        [DataMember] public double GaugeMeters { get; set; } = .75;
        [DataMember] public double HopMeters { get; set; } = .10;
        [DataMember] public double StartMeters { get; set; } = .5;
        [DataMember] public double EndMeters { get; set; } = 24;
        [DataMember] public double MaximumShiftPm { get; set; } = 40;
        [DataMember] public StageCorrelationMode Correlation { get; set; } = StageCorrelationMode.OverlapZncc;
        [DataMember] public double MinimumOverlap { get; set; } = .5;
        [DataMember] public double MinimumZncc { get; set; } = .35;
        [DataMember] public double MinimumSeparation { get; set; } = .08;
        [DataMember] public double PeakExclusionPm { get; set; } = 8;

        public double AppliedDelaySeconds => DelayMode == StageDelayMode.None ? 0 : DelayMode == StageDelayMode.Microseconds
            ? DelayMicroseconds * 1e-6 : (AuxiliaryDelayMicroseconds * 1e-6 - 2 * GroupIndex * ReflectionDistanceMeters / 299792458.0) / 2;
        public string PhaseKey => string.Join("|", new[] { SelectContinuousRange.ToString(), ReferenceRangeIndex.ToString(CultureInfo.InvariantCulture), SignalRangeIndex.ToString(CultureInfo.InvariantCulture),
            MinimumRangeSamples.ToString(CultureInfo.InvariantCulture), BandpassEnabled.ToString(), LowMHz.ToString("R", CultureInfo.InvariantCulture), HighMHz.ToString("R", CultureInfo.InvariantCulture), AutoLimitBandpass.ToString(),
            EdgeGuardMicroseconds.ToString("R", CultureInfo.InvariantCulture), DelayMode.ToString(), AppliedDelaySeconds.ToString("R", CultureInfo.InvariantCulture),
            UseNominalGrid.ToString(), NominalSweepNmPerSecond.ToString("R", CultureInfo.InvariantCulture), WavelengthNm.ToString("R", CultureInfo.InvariantCulture), AuxiliaryDelayMicroseconds.ToString("R", CultureInfo.InvariantCulture) });
        public void Validate()
        {
            if (!Enabled) return;
            foreach (var v in new[] { LowMHz, HighMHz, EdgeGuardMicroseconds, DelayMicroseconds, ReflectionDistanceMeters,
                AuxiliaryDelayMicroseconds, GroupIndex, WavelengthNm, NominalSweepNmPerSecond, GaugeMeters, HopMeters,
                StartMeters, EndMeters, MaximumShiftPm, MinimumOverlap, MinimumZncc, MinimumSeparation, PeakExclusionPm })
                if (!ModeHopSettings.IsFinite(v)) throw new ArgumentException("문서 단계 설정에 비유한 값이 있습니다.");
            if (!Enum.IsDefined(typeof(StageDelayMode), DelayMode) || !Enum.IsDefined(typeof(StageCorrelationMode), Correlation)
                || ReferenceRangeIndex < -1 || SignalRangeIndex < -1 || MinimumRangeSamples < 16
                || LowMHz <= 0 || HighMHz <= LowMHz || EdgeGuardMicroseconds < 0 || ReflectionDistanceMeters < 0
                || AuxiliaryDelayMicroseconds <= 0 || GroupIndex <= 0 || WavelengthNm <= 0 || NominalSweepNmPerSecond <= 0
                || GaugeMeters <= 0 || HopMeters <= 0 || StartMeters < 0 || EndMeters <= StartMeters || MaximumShiftPm <= 0
                || MinimumOverlap <= 0 || MinimumOverlap > 1 || MinimumZncc < -1 || MinimumZncc > 1
                || MinimumSeparation < 0 || MinimumSeparation > 2 || PeakExclusionPm < 0)
                throw new ArgumentException("문서 단계의 범위/필터/지연/품질 설정을 확인하세요.");
        }
        public double ResolveHighMHz(double sampleIntervalSeconds)
        {
            if (!ModeHopSettings.IsFinite(sampleIntervalSeconds) || sampleIntervalSeconds <= 0) throw new ArgumentException("유효한 sample interval이 필요합니다.");
            var nyquistMHz = .5e-6 / sampleIntervalSeconds;
            if (!AutoLimitBandpass || HighMHz < nyquistMHz) return HighMHz;
            var upper = .9 * nyquistMHz;
            if (LowMHz >= upper) throw new ArgumentException("현재 sample rate에서 AUX 필터 하한이 너무 높습니다. 하한을 " + upper.ToString("G6", CultureInfo.InvariantCulture) + " MHz 미만으로 낮추세요.");
            return upper;
        }
        public string Description => string.Format(CultureInfo.InvariantCulture,
            "구간 {0} Ref/Sig={1}/{2} · BPF {3} {4:G4}–{5:G4}MHz · guard {6:G4}us · delay {7} {8:G6}us · grid {9} · {10} · overlap≥{11:G3} · ZNCC≥{12:G3}/분리≥{13:G3}",
            SelectContinuousRange ? "독립" : "전체", ReferenceRangeIndex, SignalRangeIndex, BandpassEnabled ? "ON" : "OFF", LowMHz, HighMHz,
            EdgeGuardMicroseconds, DelayMode, AppliedDelaySeconds * 1e6, UseNominalGrid ? "명목" : "AUX", Correlation, MinimumOverlap, MinimumZncc, MinimumSeparation);
    }

    [DataContract]
    public sealed class StageQualityPoint
    {
        [DataMember] public int ProfileIndex { get; set; }
        [DataMember] public double DistanceMeters { get; set; }
        [DataMember] public double WindowStartMeters { get; set; }
        [DataMember] public double WindowEndMeters { get; set; }
        [DataMember] public double? CandidateStrainMicrostrain { get; set; }
        public double? AcceptedStrainMicrostrain => Valid ? CandidateStrainMicrostrain : null;
        [DataMember] public double CandidateLag { get; set; }
        [DataMember] public double CandidateShiftPm { get; set; }
        [DataMember] public double PeakZncc { get; set; }
        [DataMember] public double SecondPeak { get; set; }
        [DataMember] public double Separation { get; set; }
        [DataMember] public double Overlap { get; set; }
        [DataMember] public string Status { get; set; }
        public bool Valid => Status == "VALID_RELATIVE";
    }

    [DataContract]
    public sealed class DocumentStageResult
    {
        [DataMember] public string Summary { get; set; }
        [DataMember] public string ReferenceRange { get; set; }
        [DataMember] public string SignalRange { get; set; }
        [DataMember] public double DistancePerBin { get; set; }
        [DataMember] public double DistanceOffsetMeters { get; set; }
        [DataMember] public bool StrainEnabled { get; set; }
        [DataMember] public string CalibrationNote { get; set; }
        [DataMember] public PreviewSeries CandidateStrain { get; set; }
        [DataMember] public double FrequencyGHzPerLag { get; set; }
        [DataMember] public double WavelengthPmPerLag { get; set; }
        [DataMember] public double CommonBandwidthGHz { get; set; }
        [DataMember] public List<StageQualityPoint> Points { get; set; } = new List<StageQualityPoint>();
        [DataMember] public PreviewSeries AcceptedShift { get; set; }
        [DataMember] public PreviewSeries PeakZncc { get; set; }
        [DataMember] public PreviewSeries PeakSeparation { get; set; }
    }
}
