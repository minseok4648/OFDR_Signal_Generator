using System;
using System.Collections.Generic;
using System.Globalization;
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

        public static readonly DependencyProperty XAxisTitleProperty =
            DependencyProperty.Register(nameof(XAxisTitle), typeof(string), typeof(SignalPlot), new PropertyMetadata("X", OnPlotPropertyChanged));

        public static readonly DependencyProperty YAxisTitleProperty =
            DependencyProperty.Register(nameof(YAxisTitle), typeof(string), typeof(SignalPlot), new PropertyMetadata("Y", OnPlotPropertyChanged));

        public static readonly DependencyProperty XStartValueProperty =
            DependencyProperty.Register(nameof(XStartValue), typeof(double), typeof(SignalPlot), new PropertyMetadata(0.0, OnPlotPropertyChanged));

        public static readonly DependencyProperty XEndValueProperty =
            DependencyProperty.Register(nameof(XEndValue), typeof(double), typeof(SignalPlot), new PropertyMetadata(1.0, OnPlotPropertyChanged));

        private double[] plottedSamples;
        private double plotLeft;
        private double plotTop;
        private double plotWidth;
        private double plotHeight;
        private double plotMin;
        private double plotMax;
        private double plotRange;

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

        public string XAxisTitle
        {
            get { return (string)GetValue(XAxisTitleProperty); }
            set { SetValue(XAxisTitleProperty, value); }
        }

        public string YAxisTitle
        {
            get { return (string)GetValue(YAxisTitleProperty); }
            set { SetValue(YAxisTitleProperty, value); }
        }

        public double XStartValue
        {
            get { return (double)GetValue(XStartValueProperty); }
            set { SetValue(XStartValueProperty, value); }
        }

        public double XEndValue
        {
            get { return (double)GetValue(XEndValueProperty); }
            set { SetValue(XEndValueProperty, value); }
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
            plottedSamples = (Values ?? Enumerable.Empty<double>()).ToArray();
            var markers = (MarkerIndices ?? Enumerable.Empty<int>()).ToArray();
            EmptyStateText.Visibility = plottedSamples.Length > 1 ? Visibility.Collapsed : Visibility.Visible;
            HoverPopup.Visibility = Visibility.Collapsed;

            var width = Math.Max(0.0, ActualWidth - 10.0);
            var height = Math.Max(0.0, ActualHeight - 10.0);

            if (plottedSamples.Length <= 1 || width <= 80.0 || height <= 80.0)
            {
                return;
            }

            plotLeft = 64.0;
            plotTop = 18.0;
            var plotRight = 18.0;
            var plotBottom = 34.0;
            plotWidth = Math.Max(24.0, width - plotLeft - plotRight);
            plotHeight = Math.Max(24.0, height - plotTop - plotBottom);
            plotMin = plottedSamples.Min();
            plotMax = plottedSamples.Max();
            plotRange = plotMax - plotMin;
            if (plotRange < 1e-9)
            {
                plotRange = 1.0;
            }

            AddGridLines();
            AddAxes();
            AddAxisLabels();
            AddSignalPolyline();
            AddMarkers(markers);
        }

        private void AddGridLines()
        {
            for (var index = 0; index < 5; index++)
            {
                var y = plotTop + (plotHeight / 4.0) * index;
                PlotCanvas.Children.Add(new Line
                {
                    X1 = plotLeft,
                    X2 = plotLeft + plotWidth,
                    Y1 = y,
                    Y2 = y,
                    Stroke = new SolidColorBrush(Color.FromArgb(70, 148, 163, 184)),
                    StrokeThickness = 0.8
                });
            }

            for (var index = 0; index < 5; index++)
            {
                var x = plotLeft + (plotWidth / 4.0) * index;
                PlotCanvas.Children.Add(new Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = plotTop,
                    Y2 = plotTop + plotHeight,
                    Stroke = new SolidColorBrush(Color.FromArgb(45, 148, 163, 184)),
                    StrokeThickness = 0.8,
                    StrokeDashArray = new DoubleCollection { 2.0, 4.0 }
                });
            }
        }

        private void AddAxes()
        {
            PlotCanvas.Children.Add(new Line
            {
                X1 = plotLeft,
                X2 = plotLeft,
                Y1 = plotTop,
                Y2 = plotTop + plotHeight,
                Stroke = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                StrokeThickness = 1.0
            });

            PlotCanvas.Children.Add(new Line
            {
                X1 = plotLeft,
                X2 = plotLeft + plotWidth,
                Y1 = plotTop + plotHeight,
                Y2 = plotTop + plotHeight,
                Stroke = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                StrokeThickness = 1.0
            });
        }

        private void AddAxisLabels()
        {
            AddLabel(plotLeft - 10.0, plotTop - 8.0, FormatAxisValue(plotMax), HorizontalAlignment.Right);
            AddLabel(plotLeft - 10.0, plotTop + (plotHeight / 2.0) - 8.0, FormatAxisValue((plotMax + plotMin) / 2.0), HorizontalAlignment.Right);
            AddLabel(plotLeft - 10.0, plotTop + plotHeight - 8.0, FormatAxisValue(plotMin), HorizontalAlignment.Right);

            AddLabel(plotLeft - 8.0, plotTop + plotHeight + 4.0, FormatAxisValue(XStartValue), HorizontalAlignment.Left);
            AddLabel(plotLeft + (plotWidth / 2.0) - 18.0, plotTop + plotHeight + 4.0, FormatAxisValue((XStartValue + XEndValue) / 2.0), HorizontalAlignment.Left);
            AddLabel(plotLeft + plotWidth - 28.0, plotTop + plotHeight + 4.0, FormatAxisValue(XEndValue), HorizontalAlignment.Left);

            AddLabel(plotLeft - 58.0, 0.0, string.IsNullOrWhiteSpace(YAxisTitle) ? "Y" : YAxisTitle, HorizontalAlignment.Left, Brushes.WhiteSmoke, 12, FontWeights.SemiBold);
            AddLabel(plotLeft + plotWidth + 8.0, plotTop + plotHeight + 4.0, string.IsNullOrWhiteSpace(XAxisTitle) ? "X" : XAxisTitle, HorizontalAlignment.Left, Brushes.WhiteSmoke, 12, FontWeights.SemiBold);
        }

        private void AddSignalPolyline()
        {
            var polyline = new Polyline
            {
                Stroke = LineBrush,
                StrokeThickness = 1.6,
                SnapsToDevicePixels = true
            };

            for (var index = 0; index < plottedSamples.Length; index++)
            {
                polyline.Points.Add(new Point(GetX(index), GetY(plottedSamples[index])));
            }

            PlotCanvas.Children.Add(polyline);
        }

        private void AddMarkers(IEnumerable<int> markers)
        {
            foreach (var markerIndex in markers)
            {
                if (markerIndex < 0 || markerIndex >= plottedSamples.Length)
                {
                    continue;
                }

                var x = GetX(markerIndex);
                var y = GetY(plottedSamples[markerIndex]);

                PlotCanvas.Children.Add(new Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = plotTop,
                    Y2 = plotTop + plotHeight,
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

        private void PlotCanvas_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (plottedSamples == null || plottedSamples.Length <= 1)
            {
                HoverPopup.Visibility = Visibility.Collapsed;
                return;
            }

            var point = e.GetPosition(PlotCanvas);
            if (point.X < plotLeft || point.X > plotLeft + plotWidth || point.Y < plotTop || point.Y > plotTop + plotHeight)
            {
                HoverPopup.Visibility = Visibility.Collapsed;
                return;
            }

            var ratio = (point.X - plotLeft) / Math.Max(1.0, plotWidth);
            var index = (int)Math.Round(ratio * (plottedSamples.Length - 1));
            index = Math.Max(0, Math.Min(plottedSamples.Length - 1, index));
            var value = plottedSamples[index];
            var xValue = XStartValue + ((XEndValue - XStartValue) * (index / (double)Math.Max(1, plottedSamples.Length - 1)));

            HoverText.Text = string.Format(CultureInfo.InvariantCulture, "{0}: {1}\n{2}: {3}",
                string.IsNullOrWhiteSpace(XAxisTitle) ? "X" : XAxisTitle,
                FormatAxisValue(xValue),
                string.IsNullOrWhiteSpace(YAxisTitle) ? "Y" : YAxisTitle,
                FormatAxisValue(value));
            HoverPopup.Visibility = Visibility.Visible;

            var popupX = Math.Min(ActualWidth - 140.0, Math.Max(8.0, point.X + 12.0));
            var popupY = Math.Min(ActualHeight - 64.0, Math.Max(8.0, point.Y + 12.0));
            HoverPopup.Margin = new Thickness(popupX, popupY, 0.0, 0.0);
        }

        private void PlotCanvas_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            HoverPopup.Visibility = Visibility.Collapsed;
        }

        private void AddLabel(double x, double y, string text, HorizontalAlignment alignment, Brush foreground = null, double fontSize = 11.0, FontWeight? fontWeight = null)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = foreground ?? new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                FontSize = fontSize,
                FontWeight = fontWeight ?? FontWeights.Normal
            };

            PlotCanvas.Children.Add(label);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var desiredSize = label.DesiredSize;
            var left = alignment == HorizontalAlignment.Right ? x - desiredSize.Width : x;
            Canvas.SetLeft(label, left);
            Canvas.SetTop(label, y);
        }

        private static string FormatAxisValue(double value)
        {
            return Math.Abs(value - Math.Round(value)) < 1e-6
                ? Math.Round(value).ToString(CultureInfo.InvariantCulture)
                : value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private double GetX(int index)
        {
            return plotLeft + (index / (double)Math.Max(1, plottedSamples.Length - 1)) * plotWidth;
        }

        private double GetY(double value)
        {
            var normalized = (value - plotMin) / plotRange;
            return plotTop + (1.0 - normalized) * plotHeight;
        }
    }
}
