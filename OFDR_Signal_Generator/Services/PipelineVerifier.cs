using System;
using System.Collections.Generic;
using System.Globalization;
using OFDR_Signal_Generator.Models;

namespace OFDR_Signal_Generator.Services
{
    public sealed class PipelineVerifier
    {
        public ComparisonResult Compare(IList<ReflectionEvent> expectedEvents, IList<DetectedEvent> detectedEvents, double sampleSpacing)
        {
            expectedEvents = expectedEvents ?? new List<ReflectionEvent>();
            detectedEvents = detectedEvents ?? new List<DetectedEvent>();

            var rows = new List<EventMatchResult>();
            var summary = new ComparisonSummary
            {
                ExpectedCount = expectedEvents.Count,
                DetectedCount = detectedEvents.Count
            };

            var matchedDetections = new bool[detectedEvents.Count];
            var totalAbsoluteError = 0.0;

            foreach (var expected in expectedEvents)
            {
                var tolerance = Math.Max(6, (int)Math.Round(Math.Max(1.0, expected.Width) * 2.0));
                var bestIndex = -1;
                var bestError = int.MaxValue;

                for (var detectionIndex = 0; detectionIndex < detectedEvents.Count; detectionIndex++)
                {
                    if (matchedDetections[detectionIndex])
                    {
                        continue;
                    }

                    var error = Math.Abs(detectedEvents[detectionIndex].PositionIndex - expected.PositionIndex);
                    if (error < bestError)
                    {
                        bestError = error;
                        bestIndex = detectionIndex;
                    }
                }

                if (bestIndex >= 0 && bestError <= tolerance)
                {
                    matchedDetections[bestIndex] = true;
                    summary.MatchedCount++;
                    totalAbsoluteError += bestError;

                    rows.Add(new EventMatchResult
                    {
                        Expected = expected.PositionIndex.ToString(CultureInfo.InvariantCulture),
                        Detected = detectedEvents[bestIndex].PositionIndex.ToString(CultureInfo.InvariantCulture),
                        ErrorSamples = bestError.ToString(CultureInfo.InvariantCulture),
                        ErrorDistance = (bestError * sampleSpacing).ToString("0.000", CultureInfo.InvariantCulture),
                        Status = "Matched"
                    });
                }
                else
                {
                    summary.MissedCount++;
                    rows.Add(new EventMatchResult
                    {
                        Expected = expected.PositionIndex.ToString(CultureInfo.InvariantCulture),
                        Detected = "-",
                        ErrorSamples = "-",
                        ErrorDistance = "-",
                        Status = "Missed"
                    });
                }
            }

            for (var index = 0; index < matchedDetections.Length; index++)
            {
                if (matchedDetections[index])
                {
                    continue;
                }

                summary.FalsePositiveCount++;
                rows.Add(new EventMatchResult
                {
                    Expected = "-",
                    Detected = detectedEvents[index].PositionIndex.ToString(CultureInfo.InvariantCulture),
                    ErrorSamples = "-",
                    ErrorDistance = "-",
                    Status = "False Positive"
                });
            }

            if (summary.MatchedCount > 0)
            {
                summary.MeanAbsoluteErrorSamples = totalAbsoluteError / summary.MatchedCount;
                summary.MeanAbsoluteErrorDistance = summary.MeanAbsoluteErrorSamples * sampleSpacing;
            }

            return new ComparisonResult
            {
                Summary = summary,
                Rows = rows
            };
        }
    }

    public sealed class ComparisonResult
    {
        public ComparisonSummary Summary { get; set; }

        public IList<EventMatchResult> Rows { get; set; }
    }
}
