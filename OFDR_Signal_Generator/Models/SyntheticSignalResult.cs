using System.Collections.Generic;
using System.Numerics;

namespace OFDR_Signal_Generator.Models
{
    public sealed class SyntheticSignalResult
    {
        public double[] RawSignal { get; set; }

        public double[] ReferenceSignal { get; set; }

        public Complex[] RawComplexSignal { get; set; }

        public Complex[] ReferenceComplexSignal { get; set; }

        public IList<ReflectionEvent> GroundTruthEvents { get; set; }
    }
}
