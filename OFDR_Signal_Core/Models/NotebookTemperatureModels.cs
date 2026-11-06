using System;
using System.Runtime.Serialization;

namespace OFDR_Signal_Core.Models
{
    // Historical notebook reproduction only. These axes are not calibrated distance/strain.
    [DataContract]
    public sealed class NotebookTemperatureConfig
    {
        [DataMember] public string ReferenceFile { get; set; } = "tek0002ALL.csv";
        [DataMember] public string[] MeasurementFiles { get; set; } = new[] { "tek0003ALL.csv", "tek0004ALL.csv", "tek0005ALL.csv" };
        [DataMember] public string[] Labels { get; set; } = new[] { "60C", "80C", "100C" };
        [DataMember] public double TriggerVoltage { get; set; } = 3;
        [DataMember] public int StartOffsetSamples { get; set; } = 15000;
        [DataMember] public int InputSamples { get; set; } = 17000000;
        [DataMember] public int AuxiliarySliceStart { get; set; } = 40;
        [DataMember] public int MeasurementSliceStart { get; set; } = 1;
        [DataMember] public int ResampledSamples { get; set; } = 524288;
        [DataMember] public int SegmentSize { get; set; } = 4096;
        [DataMember] public int SegmentStep { get; set; } = 50;
        [DataMember] public int ZeroPadFactor { get; set; } = 8;
        [DataMember] public int FirstBin { get; set; }
        [DataMember] public int StopBin { get; set; } = 262144;

        public void Validate()
        {
            if (!ModeHopSettings.IsFinite(TriggerVoltage) || StartOffsetSamples < 0 || InputSamples < 4
                || AuxiliarySliceStart < 0 || MeasurementSliceStart < 0
                || (long)AuxiliarySliceStart + MeasurementSliceStart > InputSamples - 4
                || ResampledSamples < 4 || SegmentSize < 4 || SegmentSize > ResampledSamples / 2
                || SegmentStep < 1 || ZeroPadFactor < 1 || (long)SegmentSize * ZeroPadFactor > int.MaxValue
                || FirstBin < 0 || StopBin <= FirstBin || StopBin > ResampledSamples / 2)
                throw new ArgumentException("Invalid notebook reproduction dimensions/trigger.");
            if (string.IsNullOrWhiteSpace(ReferenceFile) || MeasurementFiles == null || Labels == null
                || MeasurementFiles.Length == 0 || Labels.Length != MeasurementFiles.Length)
                throw new ArgumentException("Reference, measurement files and matching labels are required.");
            for (var i = 0; i < Labels.Length; i++)
                if (string.IsNullOrWhiteSpace(Labels[i]) || string.IsNullOrWhiteSpace(MeasurementFiles[i]))
                    throw new ArgumentException("Empty file/label in notebook reproduction config.");
        }
    }

    [DataContract]
    public sealed class NotebookSweepMetrics
    {
        [DataMember] public int SourceSamples { get; set; }
        [DataMember] public double SampleIntervalSeconds { get; set; }
        [DataMember] public int TriggerIndex { get; set; }
        [DataMember] public int CropStartIndex { get; set; }
        [DataMember] public int UsedSamples { get; set; }
        [DataMember] public double FirstPhaseRadians { get; set; }
        [DataMember] public double LastPhaseRadians { get; set; }
        [DataMember] public double NonmonotonicRatio { get; set; }
    }

    public sealed class NotebookTemperatureSweep
    {
        public double[] Values { get; set; }
        public System.Numerics.Complex[] Spectrum { get; set; }
        public NotebookSweepMetrics Metrics { get; set; }
    }

    public sealed class NotebookTemperatureProfile
    {
        public int[] Positions { get; set; }
        public double[] Shifts { get; set; }
    }
}
