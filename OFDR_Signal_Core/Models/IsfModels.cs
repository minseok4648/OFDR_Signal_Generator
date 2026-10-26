using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

namespace OFDR_Signal_Core.Models
{
    [DataContract]
    public sealed class IsfWaveformHeader
    {
        [DataMember(Order = 1)] public int PointCount { get; set; }
        [DataMember(Order = 2)] public int BytesPerSample { get; set; }
        [DataMember(Order = 3)] public int BitCount { get; set; }
        [DataMember(Order = 4)] public string Encoding { get; set; }
        [DataMember(Order = 5)] public string BinaryFormat { get; set; }
        [DataMember(Order = 6)] public string ByteOrder { get; set; }
        [DataMember(Order = 7)] public string WaveformId { get; set; }
        [DataMember(Order = 8)] public string XUnit { get; set; }
        [DataMember(Order = 9)] public double XIncrement { get; set; }
        [DataMember(Order = 10)] public double XZero { get; set; }
        [DataMember(Order = 11)] public double PointOffset { get; set; }
        [DataMember(Order = 12)] public string YUnit { get; set; }
        [DataMember(Order = 13)] public double YMultiplier { get; set; }
        [DataMember(Order = 14)] public double YOffset { get; set; }
        [DataMember(Order = 15)] public double YZero { get; set; }
        [DataMember(Order = 16)] public long PayloadOffset { get; set; }
        [DataMember(Order = 17)] public long PayloadLength { get; set; }
        [DataMember(Order = 18)] public string RawPreamble { get; set; }

        public double GetTimeSeconds(int index)
        {
            return XZero + ((index - PointOffset) * XIncrement);
        }

        public double GetVoltage(short rawCode)
        {
            return ((rawCode - YOffset) * YMultiplier) + YZero;
        }
    }

    [DataContract]
    public sealed class OfdrChannelSet : INotifyPropertyChanged
    {
        private string csvStatus;
        private string qcStatus;

        [DataMember(Order = 1)] public string SetId { get; set; }
        [DataMember(Order = 2)] public double DisplacementMm { get; set; }
        [DataMember(Order = 3)] public string Repetition { get; set; }
        [DataMember(Order = 4)] public string Channel1Path { get; set; }
        [DataMember(Order = 5)] public string Channel2Path { get; set; }
        [DataMember(Order = 6)] public string Channel3Path { get; set; }
        [DataMember(Order = 7)] public int PointCount { get; set; }
        [DataMember(Order = 8)] public double SampleSpacingSeconds { get; set; }
        [DataMember(Order = 9)] public bool IsValid { get; set; }
        [DataMember(Order = 10)] public string ValidationMessage { get; set; }

        public bool HasChannel1 { get { return !string.IsNullOrWhiteSpace(Channel1Path); } }
        public bool HasChannel2 { get { return !string.IsNullOrWhiteSpace(Channel2Path); } }
        public bool HasChannel3 { get { return !string.IsNullOrWhiteSpace(Channel3Path); } }

        public string CsvStatus
        {
            get { return csvStatus ?? "Not converted"; }
            set { if (csvStatus != value) { csvStatus = value; OnPropertyChanged(); } }
        }

        public string QcStatus
        {
            get { return qcStatus ?? "Not processed"; }
            set { if (qcStatus != value) { qcStatus = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }

    [DataContract]
    public sealed class OfdrDatasetManifest
    {
        [DataMember(Order = 1)] public int SchemaVersion { get; set; } = 1;
        [DataMember(Order = 2)] public string InputRoot { get; set; }
        [DataMember(Order = 3)] public DateTime ScannedUtc { get; set; }
        [DataMember(Order = 4)] public List<OfdrChannelSet> Sets { get; set; } = new List<OfdrChannelSet>();
    }

    [DataContract]
    public sealed class IsfSourceMetadata
    {
        [DataMember(Order = 1)] public int Channel { get; set; }
        [DataMember(Order = 2)] public string Path { get; set; }
        [DataMember(Order = 3)] public long FileSize { get; set; }
        [DataMember(Order = 4)] public string Sha256 { get; set; }
        [DataMember(Order = 5)] public IsfWaveformHeader Header { get; set; }
    }

    [DataContract]
    public sealed class CsvSetMetadata
    {
        [DataMember(Order = 1)] public int SchemaVersion { get; set; } = 1;
        [DataMember(Order = 2)] public string SetId { get; set; }
        [DataMember(Order = 3)] public double DisplacementMm { get; set; }
        [DataMember(Order = 4)] public string Repetition { get; set; }
        [DataMember(Order = 5)] public long RowCount { get; set; }
        [DataMember(Order = 6)] public string CsvFileName { get; set; }
        [DataMember(Order = 7)] public string CsvSha256 { get; set; }
        [DataMember(Order = 8)] public DateTime ConvertedUtc { get; set; }
        [DataMember(Order = 9)] public string ConverterVersion { get; set; }
        [DataMember(Order = 10)] public List<IsfSourceMetadata> Sources { get; set; } = new List<IsfSourceMetadata>();
    }

    public sealed class ConversionResult
    {
        public string SetId { get; set; }
        public string CsvPath { get; set; }
        public string MetadataPath { get; set; }
        public long RowCount { get; set; }
        public long OutputBytes { get; set; }
        public TimeSpan Elapsed { get; set; }
        public bool Skipped { get; set; }
    }

    public sealed class OperationProgress
    {
        public string Stage { get; set; }
        public string Detail { get; set; }
        public double Percent { get; set; }
        public long Completed { get; set; }
        public long Total { get; set; }
    }
}
