using OFDR_Signal_Generator.Models;

namespace OFDR_Signal_Generator.Processing
{
    public sealed class StageContext
    {
        public double[] Samples { get; set; }

        public SyntheticSignalResult SourceSignal { get; set; }

        public double SampleSpacing { get; set; }
    }
}
