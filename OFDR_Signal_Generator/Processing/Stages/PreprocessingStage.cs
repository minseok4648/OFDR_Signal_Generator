using System.Linq;
using OFDR_Signal_Generator.Models;

namespace OFDR_Signal_Generator.Processing.Stages
{
    public sealed class PreprocessingStage : IProcessingStage
    {
        public string Name
        {
            get { return "Preprocessed"; }
        }

        public StageResult Execute(StageContext context)
        {
            var source = context.Samples ?? new double[0];
            if (source.Length == 0)
            {
                return new StageResult
                {
                    Name = Name,
                    Description = "Subtracts DC offset and applies a short moving-average smoother.",
                    Samples = source,
                    MarkerIndices = new int[0],
                    Metrics = StageMath.BuildMetrics(source, 0),
                    DetectedEvents = new DetectedEvent[0]
                };
            }

            var mean = source.Average();
            var centered = source.Select(value => value - mean).ToArray();
            var smoothed = new double[centered.Length];

            for (var index = 0; index < centered.Length; index++)
            {
                var start = index - 2;
                if (start < 0)
                {
                    start = 0;
                }

                var end = index + 2;
                if (end >= centered.Length)
                {
                    end = centered.Length - 1;
                }

                double sum = 0.0;
                var count = 0;
                for (var cursor = start; cursor <= end; cursor++)
                {
                    sum += centered[cursor];
                    count++;
                }

                smoothed[index] = count == 0 ? 0.0 : sum / count;
            }

            return new StageResult
            {
                Name = Name,
                Description = "Subtracts DC offset and applies a short moving-average smoother.",
                Samples = smoothed,
                MarkerIndices = new int[0],
                Metrics = StageMath.BuildMetrics(smoothed, 0),
                DetectedEvents = new DetectedEvent[0]
            };
        }
    }
}
