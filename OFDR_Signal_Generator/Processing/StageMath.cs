using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OFDR_Signal_Generator.Models;

namespace OFDR_Signal_Generator.Processing
{
    public static class StageMath
    {
        public static IList<StageMetric> BuildMetrics(double[] samples, int markerCount)
        {
            samples = samples ?? new double[0];
            var metrics = new List<StageMetric>();

            if (samples.Length == 0)
            {
                metrics.Add(new StageMetric { Name = "Sample 수", Value = "0" });
                return metrics;
            }

            var min = samples.Min();
            var max = samples.Max();
            var mean = samples.Average();
            var rms = Math.Sqrt(samples.Sum(value => value * value) / samples.Length);

            metrics.Add(new StageMetric { Name = "Sample 수", Value = samples.Length.ToString(CultureInfo.InvariantCulture) });
            metrics.Add(new StageMetric { Name = "Min", Value = min.ToString("0.0000", CultureInfo.InvariantCulture) });
            metrics.Add(new StageMetric { Name = "Max", Value = max.ToString("0.0000", CultureInfo.InvariantCulture) });
            metrics.Add(new StageMetric { Name = "Mean", Value = mean.ToString("0.0000", CultureInfo.InvariantCulture) });
            metrics.Add(new StageMetric { Name = "RMS", Value = rms.ToString("0.0000", CultureInfo.InvariantCulture) });
            metrics.Add(new StageMetric { Name = "Marker 수", Value = markerCount.ToString(CultureInfo.InvariantCulture) });
            return metrics;
        }

        public static double Clamp(double value, double min, double max)
        {
            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
        }
    }
}
