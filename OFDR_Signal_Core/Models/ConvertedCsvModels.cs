using System;

namespace OFDR_Signal_Core.Models
{
    public sealed class ConvertedOfdrCsvData
    {
        public string SetId { get; set; }
        public string CsvPath { get; set; }
        public int RowCount { get; set; }
        public double[] TimeSeconds { get; set; }
        public double[] Reference { get; set; }
        public double[] XPolarization { get; set; }
        public double[] YPolarization { get; set; }

        public double SampleIntervalSeconds
        {
            get
            {
                if (TimeSeconds == null || TimeSeconds.Length < 2) return 0.0;
                return (TimeSeconds[TimeSeconds.Length - 1] - TimeSeconds[0]) / Math.Max(1, TimeSeconds.Length - 1);
            }
        }
    }
}
