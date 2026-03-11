using System.Collections.Generic;
using System.Numerics;
using OFDR_Signal_Generator.Models;

namespace OFDR_Signal_Generator.Processing
{
    public sealed class PipelineRunResult
    {
        public IList<StageResult> Stages { get; set; }

        public double[] RawSignalMagnitudes { get; set; }

        public double[] ReferenceSignalMagnitudes { get; set; }

        public double[] RawSpectrumMagnitudes { get; set; }

        public double[] ReferenceSpectrumMagnitudes { get; set; }

        public IList<Complex[]> RawSegments { get; set; }

        public IList<Complex[]> ReferenceSegments { get; set; }

        public IList<Complex[]> ConjugateMultiplySegments { get; set; }

        public IList<Complex[]> IfftSegments { get; set; }

        public IList<int> SegmentStartIndices { get; set; }

        public IList<int> SegmentCenterIndices { get; set; }

        public IList<double> SegmentRepresentativeValues { get; set; }

        public IList<int> IfftPeakIndices { get; set; }

        public double[] FinalProfile { get; set; }

        public IList<DetectedEvent> DetectedEvents { get; set; }

        public ComparisonSummary ComparisonSummary { get; set; }

        public IList<EventMatchResult> MatchResults { get; set; }
    }
}
