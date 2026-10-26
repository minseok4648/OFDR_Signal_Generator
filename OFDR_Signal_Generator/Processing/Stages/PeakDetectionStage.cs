using System.Collections.Generic;
using System.Linq;
using OFDR_Signal_Generator.Models;

namespace OFDR_Signal_Generator.Processing.Stages
{
    public sealed class PeakDetectionStage : IProcessingStage
    {
        public string Name
        {
            get { return "Peak Detection"; }
        }

        public StageResult Execute(StageContext context)
        {
            var source = context.Samples ?? new double[0];
            var detections = new List<DetectedEvent>();
            var markerIndices = new List<int>();

            if (source.Length >= 3)
            {
                var max = source.Max();
                var mean = source.Average();
                var threshold = mean + ((max - mean) * 0.40);
                var minimumSeparation = System.Math.Max(4, source.Length / 64);

                for (var index = 1; index < source.Length - 1; index++)
                {
                    var isPeak = source[index] >= threshold &&
                                 source[index] >= source[index - 1] &&
                                 source[index] > source[index + 1];

                    if (!isPeak)
                    {
                        continue;
                    }

                    if (markerIndices.Count > 0 && index - markerIndices.Last() < minimumSeparation)
                    {
                        if (source[index] > source[markerIndices.Last()])
                        {
                            markerIndices[markerIndices.Count - 1] = index;
                            detections[detections.Count - 1] = new DetectedEvent
                            {
                                PositionIndex = index,
                                Amplitude = source[index],
                                Distance = index * context.SampleSpacing
                            };
                        }

                        continue;
                    }

                    markerIndices.Add(index);
                    detections.Add(new DetectedEvent
                    {
                        PositionIndex = index,
                        Amplitude = source[index],
                        Distance = index * context.SampleSpacing
                    });
                }
            }

            return new StageResult
            {
                Name = Name,
                Description = "Calibration된 profile에서 local maximum을 찾아 candidate reflection event로 표시합니다.",
                Samples = source.ToArray(),
                MarkerIndices = markerIndices.ToArray(),
                Metrics = StageMath.BuildMetrics(source, markerIndices.Count),
                DetectedEvents = detections
            };
        }
    }
}
