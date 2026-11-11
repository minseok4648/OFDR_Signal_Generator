using System;
using System.Collections.Generic;
using System.Linq;

namespace OFDR_Signal_Core.Processing
{
    public interface IPlotViewport
    {
        int Count { get; }
        double XStart { get; }
        double XEnd { get; }
        string XAxisTitle { get; }
        double DbFloor { get; }
        double DbCeiling { get; }
        string Description { get; }
        string PrimaryLabel { get; }
        string SecondaryLabel { get; }
        double XAt(int index);
        double ValueAt(int index, bool reference = false);
        FftViewportSamples Read(int start, int end, int maximumPoints = 10000);
    }

    public sealed class PowerDensityViewport : IPlotViewport
    {
        private readonly ReflectogramRecord record;
        public PowerDensityViewport(ReflectogramRecord record)
        {
            this.record = record ?? throw new ArgumentNullException(nameof(record));
            if (record.CorrectedPower == null || record.NominalPower == null || record.CorrectedPower.Length != record.NominalPower.Length || record.CorrectedPower.Length < 2)
                throw new ArgumentException("B/C 전력 배열이 필요합니다.");
            DbFloor = ReflectogramEngine.Db(Math.Min(record.CorrectedPower.Min(), record.NominalPower.Min()));
            DbCeiling = ReflectogramEngine.Db(Math.Max(record.CorrectedPower.Max(), record.NominalPower.Max()));
        }
        public int Count => record.CorrectedPower.Length;
        public double XStart => 0;
        public double XEnd => XAt(Count - 1);
        public string XAxisTitle => "m";
        public double DbFloor { get; }
        public double DbCeiling { get; }
        public string Description => "거리 grid · C 보정 후 실선 / B 보정 전 점선 · dB re 1 V²/m";
        public string PrimaryLabel => "C";
        public string SecondaryLabel => "B";
        public double XAt(int index) => index * record.GridMeters;
        public double ValueAt(int index, bool reference = false) => index < 0 || index >= Count ? double.NaN : ReflectogramEngine.Db((reference ? record.NominalPower : record.CorrectedPower)[index]);
        public FftViewportSamples Read(int start, int end, int maximumPoints = 10000)
        {
            start = Math.Max(0, Math.Min(Count - 1, start)); end = Math.Max(start, Math.Min(Count - 1, end));
            var length = end - start + 1; var limit = Math.Max(16, Math.Min(10000, maximumPoints));
            int[] bins;
            if (length <= limit) bins = Enumerable.Range(start, length).ToArray();
            else {
                var selected = new SortedSet<int> { start, end }; var buckets = (limit - 2) / 4;
                for (int b = 0; b < buckets; b++) {
                    int left = start + (int)((long)b * length / buckets), right = start + (int)((long)(b + 1) * length / buckets);
                    int cmin = left, cmax = left, bmin = left, bmax = left;
                    for (int i = left + 1; i < right; i++) {
                        if (record.CorrectedPower[i] < record.CorrectedPower[cmin]) cmin = i;
                        if (record.CorrectedPower[i] > record.CorrectedPower[cmax]) cmax = i;
                        if (record.NominalPower[i] < record.NominalPower[bmin]) bmin = i;
                        if (record.NominalPower[i] > record.NominalPower[bmax]) bmax = i;
                    }
                    selected.Add(cmin); selected.Add(cmax); selected.Add(bmin); selected.Add(bmax);
                }
                bins = selected.ToArray();
            }
            return new FftViewportSamples { Bins = bins, Signal = bins.Select(i => ValueAt(i)).ToArray(), Reference = bins.Select(i => ValueAt(i, true)).ToArray(), OriginalBinCount = length };
        }
    }
}
