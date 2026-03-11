using OFDR_Signal_Generator.Infrastructure;

namespace OFDR_Signal_Generator.Models
{
    public sealed class ReflectionEvent : ObservableObject
    {
        private int positionIndex;
        private double amplitude;
        private double width;

        public int PositionIndex
        {
            get { return positionIndex; }
            set { SetProperty(ref positionIndex, value); }
        }

        public double Amplitude
        {
            get { return amplitude; }
            set { SetProperty(ref amplitude, value); }
        }

        public double Width
        {
            get { return width; }
            set { SetProperty(ref width, value); }
        }

        public ReflectionEvent Clone()
        {
            return new ReflectionEvent
            {
                PositionIndex = PositionIndex,
                Amplitude = Amplitude,
                Width = Width
            };
        }
    }
}
