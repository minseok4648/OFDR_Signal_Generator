namespace OFDR_Signal_Generator.Models
{
    public sealed class EventMatchResult
    {
        public string Expected { get; set; }

        public string Detected { get; set; }

        public string ErrorSamples { get; set; }

        public string ErrorDistance { get; set; }

        public string Status { get; set; }
    }
}
