using System;
using System.Collections.Generic;
using System.Numerics;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Core.Processing
{
    internal static class PreviewReducer
    {
        public static PreviewSeries FromReal(double[] samples, int maximumPoints, string xAxis, string yAxis)
        {
            return Build(samples == null ? 0 : samples.Length, maximumPoints, index => samples[index], xAxis, yAxis);
        }

        public static PreviewSeries FromMagnitude(Complex[] samples, int maximumPoints, string xAxis, string yAxis)
        {
            return Build(samples == null ? 0 : samples.Length, maximumPoints, index => samples[index].Magnitude, xAxis, yAxis);
        }

        public static PreviewSeries FromSelector(int length, int maximumPoints, Func<int, double> selector, string xAxis, string yAxis)
        {
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            return Build(length, maximumPoints, selector, xAxis, yAxis);
        }

        private static PreviewSeries Build(int length, int maximumPoints, Func<int, double> selector, string xAxis, string yAxis)
        {
            maximumPoints = Math.Max(16, maximumPoints);
            if (length <= maximumPoints)
            {
                var direct = new double[length];
                for (var i = 0; i < length; i++) direct[i] = selector(i);
                return new PreviewSeries { Values = direct, XStart = 0.0, XEnd = Math.Max(0, length - 1), XAxisTitle = xAxis, YAxisTitle = yAxis };
            }

            var pairCount = maximumPoints / 2;
            var values = new List<double>(pairCount * 2);
            for (var bucket = 0; bucket < pairCount; bucket++)
            {
                var start = (int)((long)bucket * length / pairCount);
                var end = (int)((long)(bucket + 1) * length / pairCount);
                if (end <= start) end = start + 1;
                var min = double.PositiveInfinity;
                var max = double.NegativeInfinity;
                var minIndex = start;
                var maxIndex = start;
                for (var i = start; i < end && i < length; i++)
                {
                    var value = selector(i);
                    if (value < min) { min = value; minIndex = i; }
                    if (value > max) { max = value; maxIndex = i; }
                }
                if (minIndex <= maxIndex) { values.Add(min); values.Add(max); }
                else { values.Add(max); values.Add(min); }
            }
            return new PreviewSeries { Values = values.ToArray(), XStart = 0.0, XEnd = length - 1, XAxisTitle = xAxis, YAxisTitle = yAxis };
        }
    }
}
