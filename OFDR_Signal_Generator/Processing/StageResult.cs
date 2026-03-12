using System.Collections.Generic;
using OFDR_Signal_Generator.Models;

namespace OFDR_Signal_Generator.Processing
{
    public sealed class StageResult
    {
        public string Name { get; set; }

        public string Description { get; set; }

        public double[] Samples { get; set; }

        public int[] MarkerIndices { get; set; }

        public IList<StageMetric> Metrics { get; set; }

        public IList<DetectedEvent> DetectedEvents { get; set; }

        public string XAxisTitle { get; set; }

        public string YAxisTitle { get; set; }

        public double XStartValue { get; set; }

        public double XEndValue { get; set; }
    }
}
