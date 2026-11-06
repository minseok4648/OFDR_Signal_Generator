using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace OFDR_Signal_Core.Models
{
    [DataContract]
    public sealed class StrainComparisonSnapshot
    {
        [DataMember] public DateTime RecordedAtUtc { get; private set; }
        [DataMember] public string ReferenceId { get; private set; }
        [DataMember] public string SignalId { get; private set; }
        [DataMember] public string AuxiliaryId { get; private set; }
        [DataMember] public string SignalAuxiliaryId { get; private set; }
        [DataMember] public bool IsDiagnosticAnalysis { get; private set; }
        [DataMember] public bool InputPhaseQcPassed { get; private set; }
        [DataMember] public string PhaseQcSummary { get; private set; }
        [DataMember] public int SourceSegmentCount { get; private set; }
        [DataMember] public double DistanceMetersPerFftBin { get; private set; }
        [DataMember] public double StrainMicrostrainPerLag { get; private set; }
        [DataMember] public double[] DistancesMeters { get; private set; }
        [DataMember] public double[] StrainMicrostrain { get; private set; }

        public static bool CanCapture(IndependentOfdrProcessingResult result) => result?.PhysicalAxis?.Enabled == true
            && result.Config != null && result.SegmentCenterDistancesMeters != null && result.SegmentPeakLags != null
            && result.SegmentCenterDistancesMeters.Length > 0 && result.SegmentCenterDistancesMeters.Length == result.SegmentPeakLags.Length;

        public static StrainComparisonSnapshot Capture(IndependentOfdrProcessingResult result, DateTime recordedAtUtc, int maximumPoints = 10000)
        {
            if (!CanCapture(result)) throw new InvalidOperationException("거리축과 strain 환산이 적용된 완료 결과가 필요합니다.");
            if (maximumPoints < 2 || maximumPoints > 10000) throw new ArgumentOutOfRangeException(nameof(maximumPoints));
            var distances = result.SegmentCenterDistancesMeters; var lags = result.SegmentPeakLags;
            var factor = result.PhysicalAxis.StrainMicrostrainPerLag; var offset = result.Config.StrainOffsetMicrostrain;
            for (var i = 0; i < distances.Length; i++)
                if (!ModeHopSettings.IsFinite(distances[i]) || !ModeHopSettings.IsFinite(offset + lags[i] * factor)
                    || (i > 0 && distances[i] < distances[i - 1])) throw new InvalidOperationException("거리별 strain 결과 좌표 또는 값이 올바르지 않습니다.");
            var indices = new List<int>(Math.Min(maximumPoints, distances.Length));
            if (distances.Length <= maximumPoints) for (var i = 0; i < distances.Length; i++) indices.Add(i);
            else
            {
                var buckets = maximumPoints / 2;
                for (var b = 0; b < buckets; b++)
                {
                    var first = (int)((long)b * distances.Length / buckets); var end = (int)((long)(b + 1) * distances.Length / buckets);
                    var min = first; var max = first;
                    for (var i = first + 1; i < end; i++)
                    {
                        if (offset + lags[i] * factor < offset + lags[min] * factor) min = i;
                        if (offset + lags[i] * factor > offset + lags[max] * factor) max = i;
                    }
                    indices.Add(Math.Min(min, max)); if (min != max) indices.Add(Math.Max(min, max));
                }
            }
            var snapshot = new StrainComparisonSnapshot
            {
                RecordedAtUtc = recordedAtUtc.ToUniversalTime(), ReferenceId = result.ReferenceId, SignalId = result.SignalId,
                AuxiliaryId = result.AuxiliaryId, SignalAuxiliaryId = result.SignalAuxiliaryId,
                IsDiagnosticAnalysis = result.IsDiagnosticAnalysis, InputPhaseQcPassed = result.InputPhaseQcPassed, PhaseQcSummary = result.PhaseQcSummary,
                SourceSegmentCount = distances.Length, DistanceMetersPerFftBin = result.PhysicalAxis.DistanceMetersPerFftBin,
                StrainMicrostrainPerLag = factor, DistancesMeters = new double[indices.Count], StrainMicrostrain = new double[indices.Count]
            };
            for (var i = 0; i < indices.Count; i++) { snapshot.DistancesMeters[i] = distances[indices[i]]; snapshot.StrainMicrostrain[i] = offset + lags[indices[i]] * factor; }
            return snapshot;
        }
    }
}
