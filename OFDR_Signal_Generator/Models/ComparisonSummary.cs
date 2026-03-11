namespace OFDR_Signal_Generator.Models
{
    public sealed class ComparisonSummary
    {
        public int ExpectedCount { get; set; }

        public int DetectedCount { get; set; }

        public int MatchedCount { get; set; }

        public int MissedCount { get; set; }

        public int FalsePositiveCount { get; set; }

        public double MeanAbsoluteErrorSamples { get; set; }

        public double MeanAbsoluteErrorDistance { get; set; }
    }
}
