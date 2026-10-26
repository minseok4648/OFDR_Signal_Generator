using System.Linq;
using OFDR_Signal_Generator.Models;

namespace OFDR_Signal_Generator.Processing.Stages
{
    public sealed class CalibrationStage : IProcessingStage
    {
        public string Name
        {
            get { return "Calibration"; }
        }

        public StageResult Execute(StageContext context)
        {
            var source = context.Samples ?? new double[0];
            var calibrated = new double[source.Length];

            if (source.Length > 0)
            {
                for (var index = 0; index < source.Length; index++)
                {
                    var distanceWeight = 1.0 + (0.75 * index / System.Math.Max(1, source.Length - 1));
                    calibrated[index] = source[index] * distanceWeight;
                }

                var max = calibrated.Max();
                if (max > 0.0)
                {
                    for (var index = 0; index < calibrated.Length; index++)
                    {
                        calibrated[index] = StageMath.Clamp(calibrated[index] / max, 0.0, 1.0);
                    }
                }
            }

            return new StageResult
            {
                Name = Name,
                Description = "거리 기반 gain compensation을 적용하고 profile을 0..1 범위로 normalize합니다.",
                Samples = calibrated,
                MarkerIndices = new int[0],
                Metrics = StageMath.BuildMetrics(calibrated, 0),
                DetectedEvents = new DetectedEvent[0]
            };
        }
    }
}
