using OFDR_Signal_Generator.Models;

namespace OFDR_Signal_Generator.Processing.Stages
{
    public sealed class TransformStage : IProcessingStage
    {
        public string Name
        {
            get { return "Transform Profile"; }
        }

        public StageResult Execute(StageContext context)
        {
            var source = context.Samples ?? new double[0];
            var transformed = new double[source.Length];

            for (var index = 0; index < source.Length; index++)
            {
                var previous = index == 0 ? source[index] : source[index - 1];
                var next = index == source.Length - 1 ? source[index] : source[index + 1];
                var slope = next - previous;
                transformed[index] = System.Math.Abs(source[index]) + (System.Math.Abs(slope) * 0.5);
            }

            return new StageResult
            {
                Name = Name,
                Description = "Builds a same-domain energy profile by emphasizing slope changes around reflection events.",
                Samples = transformed,
                MarkerIndices = new int[0],
                Metrics = StageMath.BuildMetrics(transformed, 0),
                DetectedEvents = new DetectedEvent[0]
            };
        }
    }
}
