using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OFDR_Signal_Generator.Controls
{
    public partial class SignalPlot : UserControl
    {
        public static readonly DependencyProperty ValuesProperty =
            DependencyProperty.Register(nameof(Values), typeof(IEnumerable<double>), typeof(SignalPlot), new PropertyMetadata(null, OnPlotPropertyChanged));

        public static readonly DependencyProperty MarkerIndicesProperty =
            DependencyProperty.Register(nameof(MarkerIndices), typeof(IEnumerable<int>), typeof(SignalPlot), new PropertyMetadata(null, OnPlotPropertyChanged));

        public static readonly DependencyProperty LineBrushProperty =
            DependencyProperty.Register(nameof(LineBrush), typeof(Brush), typeof(SignalPlot), new PropertyMetadata(new SolidColorBrush(Color.FromRgb(56, 189, 248)), OnPlotPropertyChanged));

        public static readonly DependencyProperty MarkerBrushProperty =
            DependencyProperty.Register(nameof(MarkerBrush), typeof(Brush), typeof(SignalPlot), new PropertyMetadata(new SolidColorBrush(Color.FromRgb(251, 191, 36)), OnPlotPropertyChanged));

        public SignalPlot()
        {
            InitializeComponent();
            Loaded += delegate { RenderPlot(); };
            SizeChanged += delegate { RenderPlot(); };
        }

        public IEnumerable<double> Values
        {
            get { return (IEnumerable<double>)GetValue(ValuesProperty); }
            set { SetValue(ValuesProperty, value); }
        }

        public IEnumerable<int> MarkerIndices
        {
            get { return (IEnumerable<int>)GetValue(MarkerIndicesProperty); }
            set { SetValue(MarkerIndicesProperty, value); }
        }

        public Brush LineBrush
        {
            get { return (Brush)GetValue(LineBrushProperty); }
            set { SetValue(LineBrushProperty, value); }
        }

        public Brush MarkerBrush
        {
            get { return (Brush)GetValue(MarkerBrushProperty); }
            set { SetValue(MarkerBrushProperty, value); }
        }

        private static void OnPlotPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
        {
            ((SignalPlot)dependencyObject).RenderPlot();
        }

        private void RenderPlot()
        {
            if (PlotCanvas == null)
            {
                return;
            }

            PlotCanvas.Children.Clear();

            var samples = (Values ?? Enumerable.Empty<double>()).ToArray();
            var markers = (MarkerIndices ?? Enumerable.Empty<int>()).ToArray();
            EmptyStateText.Visibility = samples.Length > 1 ? Visibility.Collapsed : Visibility.Visible;

            var width = ActualWidth - 18.0;
            var height = ActualHeight - 18.0;

            if (samples.Length <= 1 || width <= 24.0 || height <= 24.0)
            {
                return;
            }

            var left = 12.0;
            var top = 10.0;
            var plotWidth = width - left;
            var plotHeight = height - top - 8.0;
            var min = samples.Min();
            var max = samples.Max();
            var range = max - min;
            if (range < 1e-9)
            {
                range = 1.0;
            }

            AddGridLines(top, plotWidth + left, plotHeight, left);

            var polyline = new Polyline
            {
                Stroke = LineBrush,
                StrokeThickness = 1.6,
                SnapsToDevicePixels = true
            };

            for (var index = 0; index < samples.Length; index++)
            {
                var x = left + (index / (double)(samples.Length - 1)) * plotWidth;
                var normalized = (samples[index] - min) / range;
                var y = top + (1.0 - normalized) * plotHeight;
                polyline.Points.Add(new Point(x, y));
            }

            PlotCanvas.Children.Add(polyline);

            foreach (var markerIndex in markers)
            {
                if (markerIndex < 0 || markerIndex >= samples.Length)
                {
                    continue;
                }

                var x = left + (markerIndex / (double)(samples.Length - 1)) * plotWidth;
                var normalized = (samples[markerIndex] - min) / range;
                var y = top + (1.0 - normalized) * plotHeight;

                PlotCanvas.Children.Add(new Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = top,
                    Y2 = top + plotHeight,
                    Stroke = new SolidColorBrush(Color.FromArgb(140, 251, 191, 36)),
                    StrokeThickness = 1.0,
                    StrokeDashArray = new DoubleCollection { 3.0, 3.0 }
                });

                var marker = new Ellipse
                {
                    Width = 8.0,
                    Height = 8.0,
                    Fill = MarkerBrush,
                    Stroke = Brushes.White,
                    StrokeThickness = 1.0
                };

                Canvas.SetLeft(marker, x - 4.0);
                Canvas.SetTop(marker, y - 4.0);
                PlotCanvas.Children.Add(marker);
            }
        }

        private void AddGridLines(double top, double width, double plotHeight, double left)
        {
            for (var index = 0; index < 4; index++)
            {
                var y = top + (plotHeight / 3.0) * index;
                PlotCanvas.Children.Add(new Line
                {
                    X1 = left,
                    X2 = width,
                    Y1 = y,
                    Y2 = y,
                    Stroke = new SolidColorBrush(Color.FromArgb(70, 148, 163, 184)),
                    StrokeThickness = 0.8
                });
            }
        }
    }
}
