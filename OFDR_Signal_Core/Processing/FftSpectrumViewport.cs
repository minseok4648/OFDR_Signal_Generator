using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Processing
{
    // Borrows the result's spectra; never copies or changes the full FFT arrays.
    // UI requests only a bounded viewport, with original FFT bin coordinates.
    public sealed class FftSpectrumViewport : IPlotViewport
    {
        private readonly IndependentOfdrProcessingResult result;
        private readonly int count;
        private readonly double signalMaximum, referenceMaximum, floor;

        public FftSpectrumViewport(IndependentOfdrProcessingResult result)
        {
            this.result = result ?? throw new ArgumentNullException(nameof(result));
            count = Math.Min(result.SampleCount / 2, Math.Min(result.SignalSpectrumValues?.Length ?? 0, result.ReferenceSpectrumValues?.Length ?? 0));
            if (count < 2) throw new ArgumentException("거리 FFT가 없습니다.", nameof(result));
            floor = result.Config.FftDbFloor;
            XStart = result.SignalSpectrum.XStart;
            XEnd = result.SignalSpectrum.XEnd;
            XAxisTitle = result.SignalSpectrum.XAxisTitle;
            signalMaximum = Maximum(result.SignalSpectrumValues, count);
            referenceMaximum = Maximum(result.ReferenceSpectrumValues, count);
        }

        public int Count => result.SignalSpectrumValues == null || result.ReferenceSpectrumValues == null ? 0 : count;
        public double XStart { get; }
        public double XEnd { get; }
        public string XAxisTitle { get; }
        public double DbFloor => floor;
        public double DbCeiling => 0;
        public string Description => "FFT bins · Sig 실선 / Ref 점선 · 최대값 기준 dB";
        public string PrimaryLabel => "Sig";
        public string SecondaryLabel => "Ref";
        public double XAt(int bin) => XStart + (XEnd - XStart) * bin / (count - 1.0);
        public double ValueAt(int bin, bool reference = false)
        {
            if (bin < 0 || bin >= Count) return double.NaN;
            var magnitude = (reference ? result.ReferenceSpectrumValues : result.SignalSpectrumValues)[bin].Magnitude;
            return Math.Max(floor, 20 * Math.Log10(Math.Max(1e-300, magnitude) / (reference ? referenceMaximum : signalMaximum)));
        }

        public FftViewportSamples Read(int start, int end, int maximumPoints = 10000)
        {
            if (Count == 0) return new FftViewportSamples();
            start = Math.Max(0, Math.Min(count - 1, start));
            end = Math.Max(start, Math.Min(count - 1, end));
            var limit = Math.Max(16, Math.Min(10000, maximumPoints));
            var length = end - start + 1;
            int[] bins;
            if (length <= limit) bins = Enumerable.Range(start, length).ToArray();
            else
            {
                var selected = new SortedSet<int> { start, end };
                var buckets = (limit - 2) / 4;
                for (var bucket = 0; bucket < buckets; bucket++)
                {
                    var left = start + (int)((long)bucket * length / buckets);
                    var right = start + (int)((long)(bucket + 1) * length / buckets);
                    var smin = left; var smax = left; var rmin = left; var rmax = left;
                    var slo = double.PositiveInfinity; var shi = double.NegativeInfinity;
                    var rlo = double.PositiveInfinity; var rhi = double.NegativeInfinity;
                    for (var i = left; i < right; i++)
                    {
                        var s = result.SignalSpectrumValues[i].Magnitude;
                        var r = result.ReferenceSpectrumValues[i].Magnitude;
                        if (s < slo) { slo = s; smin = i; } if (s > shi) { shi = s; smax = i; }
                        if (r < rlo) { rlo = r; rmin = i; } if (r > rhi) { rhi = r; rmax = i; }
                    }
                    selected.Add(smin); selected.Add(smax); selected.Add(rmin); selected.Add(rmax);
                }
                bins = selected.ToArray();
            }
            return new FftViewportSamples { Bins = bins, Signal = bins.Select(i => ValueAt(i)).ToArray(),
                Reference = bins.Select(i => ValueAt(i, true)).ToArray(), OriginalBinCount = length };
        }

        private static double Maximum(Complex[] values, int length)
        {
            double maximum = 1e-300;
            for (var i = 0; i < length; i++) if (values[i].Magnitude > maximum) maximum = values[i].Magnitude;
            return maximum;
        }
    }

    public sealed class FftViewportSamples
    {
        public int[] Bins { get; set; } = new int[0];
        public double[] Signal { get; set; } = new double[0];
        public double[] Reference { get; set; } = new double[0];
        public int OriginalBinCount { get; set; }
        public bool IsReduced => Bins.Length < OriginalBinCount;
    }
}
