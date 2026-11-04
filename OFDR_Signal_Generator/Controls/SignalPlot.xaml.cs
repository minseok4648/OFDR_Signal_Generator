using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OFDR_Signal_Generator.Controls
{
    public partial class SignalPlot : UserControl
    {
        public static readonly DependencyProperty ValuesProperty =
            DependencyProperty.Register(nameof(Values), typeof(IEnumerable<double>), typeof(SignalPlot), new PropertyMetadata(null, OnPlotPropertyChanged));

        public static readonly DependencyProperty SecondaryValuesProperty =
            DependencyProperty.Register(nameof(SecondaryValues), typeof(IEnumerable<double>), typeof(SignalPlot), new PropertyMetadata(null, OnPlotPropertyChanged));

        public static readonly DependencyProperty MarkerIndicesProperty =
            DependencyProperty.Register(nameof(MarkerIndices), typeof(IEnumerable<int>), typeof(SignalPlot), new PropertyMetadata(null, OnPlotPropertyChanged));

        public static readonly DependencyProperty LineBrushProperty =
            DependencyProperty.Register(nameof(LineBrush), typeof(Brush), typeof(SignalPlot), new PropertyMetadata(new SolidColorBrush(Color.FromRgb(56, 189, 248)), OnPlotPropertyChanged));

        public static readonly DependencyProperty SecondaryLineBrushProperty =
            DependencyProperty.Register(nameof(SecondaryLineBrush), typeof(Brush), typeof(SignalPlot), new PropertyMetadata(new SolidColorBrush(Color.FromRgb(251, 146, 60)), OnPlotPropertyChanged));

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

        private readonly Stack<ZoomState> zoomHistory;
        private double[] plottedSamples;
        private double[] secondaryPlottedSamples;
        private double plotLeft;
        private double plotTop;
        private double plotWidth;
        private double plotHeight;
        private double plotMin;
        private double plotMax;
        private double plotRange;
        private int viewStartIndex;
        private int viewEndIndex;
        private bool hasManualYRange;
        private double viewMinY;
        private double viewMaxY;
        private bool isDraggingZoom;
        private Point zoomStartPoint;
        private Point zoomCurrentPoint;

        public SignalPlot()
        {
            zoomHistory = new Stack<ZoomState>();
            InitializeComponent();
            Loaded += delegate { RenderPlot(); };
            SizeChanged += delegate { RenderPlot(); };
        }

        public IEnumerable<double> Values
        {
            get { return (IEnumerable<double>)GetValue(ValuesProperty); }
            set { SetValue(ValuesProperty, value); }
        }

        public IEnumerable<double> SecondaryValues
        {
            get { return (IEnumerable<double>)GetValue(SecondaryValuesProperty); }
            set { SetValue(SecondaryValuesProperty, value); }
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

        public Brush SecondaryLineBrush
        {
            get { return (Brush)GetValue(SecondaryLineBrushProperty); }
            set { SetValue(SecondaryLineBrushProperty, value); }
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
            var plot = (SignalPlot)dependencyObject;
            if (e.Property == ValuesProperty || e.Property == SecondaryValuesProperty)
            {
                plot.ResetZoomState();
            }

            plot.RenderPlot();
        }

        private void RenderPlot()
        {
            if (PlotCanvas == null)
            {
                return;
            }

            PlotCanvas.Children.Clear();
            plottedSamples = (Values ?? Enumerable.Empty<double>()).ToArray();
            secondaryPlottedSamples = (SecondaryValues ?? Enumerable.Empty<double>()).ToArray();
            var markers = (MarkerIndices ?? Enumerable.Empty<int>()).ToArray();
            var sampleCount = GetSampleCount();
            EmptyStateText.Visibility = sampleCount > 1 ? Visibility.Collapsed : Visibility.Visible;
            HoverPopup.Visibility = Visibility.Collapsed;
            SelectionRectangle.Visibility = Visibility.Collapsed;
            InteractionHintText.Visibility = sampleCount > 1 && ActualWidth >= 600 ? Visibility.Visible : Visibility.Collapsed;

            var width = Math.Max(0.0, ActualWidth - 10.0);
            var height = Math.Max(0.0, ActualHeight - 10.0);

            if (sampleCount <= 1 || width <= 80.0 || height <= 80.0)
            {
                return;
            }

            EnsureZoomBounds(sampleCount);

            plotLeft = 64.0;
            plotTop = 18.0;
            var plotRight = 18.0;
            var plotBottom = 34.0;
            plotWidth = Math.Max(24.0, width - plotLeft - plotRight);
            plotHeight = Math.Max(24.0, height - plotTop - plotBottom);

            var visiblePrimarySamples = GetVisibleSamples(plottedSamples);
            var visibleSecondarySamples = GetVisibleSamples(secondaryPlottedSamples);
            var visibleAllSamples = visiblePrimarySamples.Concat(visibleSecondarySamples).ToArray();
            if (visibleAllSamples.Length <= 1)
            {
                return;
            }

            var automaticMin = visibleAllSamples.Min();
            var automaticMax = visibleAllSamples.Max();
            plotMin = hasManualYRange ? viewMinY : automaticMin;
            plotMax = hasManualYRange ? viewMaxY : automaticMax;
            plotRange = plotMax - plotMin;
            if (double.IsNaN(plotRange) || double.IsInfinity(plotRange) || plotRange < 1e-9)
            {
                hasManualYRange = false;
                plotMin = automaticMin;
                plotMax = automaticMax;
                plotRange = 1.0;
            }

            AddGridLines();
            AddAxes();
            AddAxisLabels();
            if (visibleSecondarySamples.Length > 1)
            {
                AddSignalPolyline(visibleSecondarySamples, SecondaryLineBrush, 1.3, new DoubleCollection { 6.0, 3.0 });
            }

            if (visiblePrimarySamples.Length > 1)
            {
                AddSignalPolyline(visiblePrimarySamples, LineBrush, 1.6, null);
            }

            AddMarkers(markers);
        }

        private void ResetZoomState()
        {
            zoomHistory.Clear();
            viewStartIndex = 0;
            viewEndIndex = 0;
            hasManualYRange = false;
            viewMinY = 0.0;
            viewMaxY = 0.0;
            isDraggingZoom = false;
        }

        private int GetSampleCount()
        {
            return Math.Max(plottedSamples == null ? 0 : plottedSamples.Length, secondaryPlottedSamples == null ? 0 : secondaryPlottedSamples.Length);
        }

        private void EnsureZoomBounds(int sampleCount)
        {
            if (sampleCount <= 0)
            {
                viewStartIndex = 0;
                viewEndIndex = 0;
                return;
            }

            if (viewEndIndex <= viewStartIndex || viewEndIndex >= sampleCount)
            {
                viewStartIndex = 0;
                viewEndIndex = sampleCount - 1;
                zoomHistory.Clear();
                return;
            }

            viewStartIndex = Math.Max(0, Math.Min(sampleCount - 1, viewStartIndex));
            viewEndIndex = Math.Max(viewStartIndex + 1, Math.Min(sampleCount - 1, viewEndIndex));
        }

        private double[] GetVisibleSamples(double[] samples)
        {
            if (samples == null || samples.Length == 0)
            {
                return new double[0];
            }

            var start = Math.Max(0, Math.Min(samples.Length - 1, viewStartIndex));
            var end = Math.Max(start, Math.Min(samples.Length - 1, viewEndIndex));
            var length = Math.Max(1, end - start + 1);
            return samples.Skip(start).Take(length).ToArray();
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

            AddLabel(plotLeft - 8.0, plotTop + plotHeight + 4.0, FormatAxisValue(GetXAxisValueForGlobalIndex(viewStartIndex)), HorizontalAlignment.Left);
            AddLabel(plotLeft + (plotWidth / 2.0) - 18.0, plotTop + plotHeight + 4.0, FormatAxisValue(GetXAxisValueForGlobalIndex((viewStartIndex + viewEndIndex) / 2)), HorizontalAlignment.Left);
            AddLabel(plotLeft + plotWidth - 28.0, plotTop + plotHeight + 4.0, FormatAxisValue(GetXAxisValueForGlobalIndex(viewEndIndex)), HorizontalAlignment.Left);

            AddLabel(plotLeft - 58.0, 0.0, string.IsNullOrWhiteSpace(YAxisTitle) ? "Y" : YAxisTitle, HorizontalAlignment.Left, Brushes.WhiteSmoke, 12, FontWeights.SemiBold);
            AddLabel(plotLeft + plotWidth + 8.0, plotTop + plotHeight + 4.0, string.IsNullOrWhiteSpace(XAxisTitle) ? "X" : XAxisTitle, HorizontalAlignment.Left, Brushes.WhiteSmoke, 12, FontWeights.SemiBold);
        }

        private void AddSignalPolyline(IReadOnlyList<double> visibleSamples, Brush stroke, double thickness, DoubleCollection dashArray)
        {
            var polyline = new Polyline
            {
                Clip = new RectangleGeometry(new Rect(plotLeft, plotTop, plotWidth, plotHeight)),
                Stroke = stroke,
                StrokeThickness = thickness,
                SnapsToDevicePixels = true,
                StrokeDashArray = dashArray
            };

            for (var visibleIndex = 0; visibleIndex < visibleSamples.Count; visibleIndex++)
            {
                polyline.Points.Add(new Point(GetX(visibleIndex, visibleSamples.Count), GetY(visibleSamples[visibleIndex])));
            }

            PlotCanvas.Children.Add(polyline);
        }

        private void AddMarkers(IEnumerable<int> markers)
        {
            foreach (var markerIndex in markers)
            {
                if (markerIndex < viewStartIndex || markerIndex > viewEndIndex)
                {
                    continue;
                }

                var visibleIndex = markerIndex - viewStartIndex;
                var visibleSamples = GetVisibleSamples(plottedSamples);
                if (visibleIndex < 0 || visibleIndex >= visibleSamples.Length)
                {
                    continue;
                }

                var x = GetX(visibleIndex, visibleSamples.Length);
                var y = GetY(visibleSamples[visibleIndex]);

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

                if (y < plotTop || y > plotTop + plotHeight) continue;

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

        private void PlotCanvas_OnMouseMove(object sender, MouseEventArgs e)
        {
            if (GetSampleCount() <= 1)
            {
                HoverPopup.Visibility = Visibility.Collapsed;
                return;
            }

            var point = e.GetPosition(PlotCanvas);
            if (isDraggingZoom)
            {
                zoomCurrentPoint = point;
                UpdateSelectionRectangle();
            }

            if (point.X < plotLeft || point.X > plotLeft + plotWidth || point.Y < plotTop || point.Y > plotTop + plotHeight)
            {
                HoverPopup.Visibility = Visibility.Collapsed;
                return;
            }

            var visibleCount = Math.Max(1, viewEndIndex - viewStartIndex + 1);
            var ratio = (point.X - plotLeft) / Math.Max(1.0, plotWidth);
            var visibleIndex = (int)Math.Round(ratio * Math.Max(0, visibleCount - 1));
            visibleIndex = Math.Max(0, Math.Min(visibleCount - 1, visibleIndex));
            var globalIndex = viewStartIndex + visibleIndex;
            var primaryValue = TryGetSampleValue(plottedSamples, globalIndex);
            var secondaryValue = TryGetSampleValue(secondaryPlottedSamples, globalIndex);
            var xValue = GetXAxisValueForGlobalIndex(globalIndex);

            HoverText.Text = secondaryValue.HasValue
                ? string.Format(CultureInfo.InvariantCulture, "{0}: {1}\n{2}: {3}\nOverlay: {4}",
                    string.IsNullOrWhiteSpace(XAxisTitle) ? "X" : XAxisTitle,
                    FormatAxisValue(xValue),
                    string.IsNullOrWhiteSpace(YAxisTitle) ? "Y" : YAxisTitle,
                    FormatAxisValue(primaryValue ?? 0.0),
                    FormatAxisValue(secondaryValue.Value))
                : string.Format(CultureInfo.InvariantCulture, "{0}: {1}\n{2}: {3}",
                    string.IsNullOrWhiteSpace(XAxisTitle) ? "X" : XAxisTitle,
                    FormatAxisValue(xValue),
                    string.IsNullOrWhiteSpace(YAxisTitle) ? "Y" : YAxisTitle,
                    FormatAxisValue(primaryValue ?? 0.0));
            HoverPopup.Visibility = Visibility.Visible;

            var popupX = Math.Min(ActualWidth - 140.0, Math.Max(8.0, point.X + 12.0));
            var popupY = Math.Min(ActualHeight - 64.0, Math.Max(8.0, point.Y + 12.0));
            HoverPopup.Margin = new Thickness(popupX, popupY, 0.0, 0.0);
        }

        private void PlotCanvas_OnMouseLeave(object sender, MouseEventArgs e)
        {
            if (!isDraggingZoom)
            {
                HoverPopup.Visibility = Visibility.Collapsed;
            }
        }

        private void PlotCanvas_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (GetSampleCount() <= 1)
            {
                return;
            }

            var point = e.GetPosition(PlotCanvas);
            if (!IsInPlotArea(point))
            {
                return;
            }

            if (e.ClickCount == 2) { ResetZoom(); e.Handled = true; return; }

            isDraggingZoom = true;
            zoomStartPoint = point;
            zoomCurrentPoint = point;
            HoverPopup.Visibility = Visibility.Collapsed;
            PlotCanvas.CaptureMouse();
            UpdateSelectionRectangle();
        }

        private void PlotCanvas_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!isDraggingZoom)
            {
                return;
            }

            isDraggingZoom = false;
            PlotCanvas.ReleaseMouseCapture();
            SelectionRectangle.Visibility = Visibility.Collapsed;

            var point = e.GetPosition(PlotCanvas);
            ZoomSelection(zoomStartPoint, point);
            e.Handled = true;
        }

        internal Rect InteractionArea => new Rect(plotLeft, plotTop, plotWidth, plotHeight);
        internal Rect ZoomBounds => new Rect(viewStartIndex, plotMin, Math.Max(0, viewEndIndex - viewStartIndex), Math.Max(0, plotRange));
        internal bool ZoomSelection(Point first, Point point)
        {
            if (GetSampleCount() < 2 || Math.Abs(point.X - first.X) < 8.0)
            {
                return false;
            }

            var startIndex = GetGlobalIndexFromPoint(Math.Min(first.X, point.X));
            var endIndex = GetGlobalIndexFromPoint(Math.Max(first.X, point.X));
            if (endIndex - startIndex < 2)
            {
                return false;
            }

            var selectedTop = Math.Abs(point.Y - first.Y) < 8 ? plotTop : Math.Max(plotTop, Math.Min(plotTop + plotHeight, Math.Min(first.Y, point.Y)));
            var selectedBottom = Math.Abs(point.Y - first.Y) < 8 ? plotTop + plotHeight : Math.Max(plotTop, Math.Min(plotTop + plotHeight, Math.Max(first.Y, point.Y)));
            var selectedMaxY = GetYAxisValueFromPoint(selectedTop);
            var selectedMinY = GetYAxisValueFromPoint(selectedBottom);
            if (selectedMaxY - selectedMinY < 1e-12)
            {
                return false;
            }

            zoomHistory.Push(new ZoomState
            {
                StartIndex = viewStartIndex,
                EndIndex = viewEndIndex,
                HasManualYRange = hasManualYRange,
                MinY = viewMinY,
                MaxY = viewMaxY
            });
            viewStartIndex = startIndex;
            viewEndIndex = endIndex;
            hasManualYRange = true;
            viewMinY = selectedMinY;
            viewMaxY = selectedMaxY;
            RenderPlot();
            return true;
        }

        internal bool ZoomAt(Point point, int delta)
        {
            var count = GetSampleCount();
            if (count < 2 || isDraggingZoom || !InteractionArea.Contains(point) || delta == 0) return false;
            double left, right, minY, maxY;
            var factor = PlotZoomWindow.WheelFactor(delta);
            PlotZoomWindow.ScaleRange(viewStartIndex, viewEndIndex, 0, count - 1, (point.X - plotLeft) / plotWidth, factor, out left, out right);
            var all = plottedSamples.Concat(secondaryPlottedSamples).ToArray();
            var fullMin = all.Min(); var fullMax = all.Max();
            if (fullMax - fullMin < 1e-9) fullMax = fullMin + 1;
            PlotZoomWindow.ScaleRange(plotMin, plotMin + plotRange, fullMin, fullMax, (plotTop + plotHeight - point.Y) / plotHeight, factor, out minY, out maxY);
            zoomHistory.Push(new ZoomState { StartIndex = viewStartIndex, EndIndex = viewEndIndex, HasManualYRange = hasManualYRange, MinY = viewMinY, MaxY = viewMaxY });
            var minimum = Math.Min(2, count - 1);
            viewStartIndex = Math.Max(0, Math.Min(count - 1 - minimum, (int)Math.Round(left)));
            viewEndIndex = Math.Max(viewStartIndex + minimum, Math.Min(count - 1, (int)Math.Round(right)));
            hasManualYRange = true; viewMinY = minY; viewMaxY = maxY;
            RenderPlot(); return true;
        }

        internal void ResetZoom() { ResetZoomState(); RenderPlot(); }
        private void PlotCanvas_OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (GetSampleCount() < 2 || !IsInPlotArea(e.GetPosition(PlotCanvas))) return;
            ZoomAt(e.GetPosition(PlotCanvas), e.Delta); e.Handled = true;
        }
        private void PlotCanvas_OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (GetSampleCount() < 2 || !IsInPlotArea(e.GetPosition(PlotCanvas))) return;
            ZoomOutButton_OnClick(sender, e); e.Handled = true;
        }
        private void PlotCanvas_OnLostMouseCapture(object sender, MouseEventArgs e) { isDraggingZoom = false; SelectionRectangle.Visibility = Visibility.Collapsed; }

        private void ZoomOutButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (zoomHistory.Count > 0)
            {
                var previousRange = zoomHistory.Pop();
                viewStartIndex = previousRange.StartIndex;
                viewEndIndex = previousRange.EndIndex;
                hasManualYRange = previousRange.HasManualYRange;
                viewMinY = previousRange.MinY;
                viewMaxY = previousRange.MaxY;
            }
            else
            {
                ResetZoomState();
            }

            RenderPlot();
        }

        private void ResetZoomButton_OnClick(object sender, RoutedEventArgs e)
        {
            ResetZoomState();
            RenderPlot();
        }

        private void UpdateSelectionRectangle()
        {
            if (!isDraggingZoom)
            {
                SelectionRectangle.Visibility = Visibility.Collapsed;
                return;
            }

            var left = Math.Max(plotLeft, Math.Min(zoomStartPoint.X, zoomCurrentPoint.X));
            var right = Math.Min(plotLeft + plotWidth, Math.Max(zoomStartPoint.X, zoomCurrentPoint.X));
            var top = Math.Max(plotTop, Math.Min(zoomStartPoint.Y, zoomCurrentPoint.Y));
            var bottom = Math.Min(plotTop + plotHeight, Math.Max(zoomStartPoint.Y, zoomCurrentPoint.Y));
            var height = Math.Max(0.0, bottom - top);
            var width = Math.Max(0.0, right - left);
            if (width < 1.0 || height < 1.0)
            {
                SelectionRectangle.Visibility = Visibility.Collapsed;
                return;
            }

            SelectionRectangle.Visibility = Visibility.Visible;
            SelectionRectangle.Margin = new Thickness(left, top, 0.0, 0.0);
            SelectionRectangle.Width = width;
            SelectionRectangle.Height = height;
        }

        private bool IsInPlotArea(Point point)
        {
            return point.X >= plotLeft && point.X <= plotLeft + plotWidth && point.Y >= plotTop && point.Y <= plotTop + plotHeight;
        }

        private int GetGlobalIndexFromPoint(double x)
        {
            var clampedX = Math.Max(plotLeft, Math.Min(plotLeft + plotWidth, x));
            var ratio = (clampedX - plotLeft) / Math.Max(1.0, plotWidth);
            var visibleCount = Math.Max(1, viewEndIndex - viewStartIndex + 1);
            var visibleIndex = (int)Math.Round(ratio * Math.Max(0, visibleCount - 1));
            visibleIndex = Math.Max(0, Math.Min(visibleCount - 1, visibleIndex));
            return viewStartIndex + visibleIndex;
        }

        private double GetYAxisValueFromPoint(double y)
        {
            var clampedY = Math.Max(plotTop, Math.Min(plotTop + plotHeight, y));
            var ratio = (clampedY - plotTop) / Math.Max(1.0, plotHeight);
            return plotMax - (ratio * plotRange);
        }

        private double GetXAxisValueForGlobalIndex(int globalIndex)
        {
            var sampleCount = GetSampleCount();
            if (sampleCount <= 1)
            {
                return XStartValue;
            }

            globalIndex = Math.Max(0, Math.Min(sampleCount - 1, globalIndex));
            return XStartValue + ((XEndValue - XStartValue) * (globalIndex / (double)Math.Max(1, sampleCount - 1)));
        }

        private static double? TryGetSampleValue(double[] samples, int index)
        {
            if (samples == null || index < 0 || index >= samples.Length)
            {
                return null;
            }

            return samples[index];
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

        private double GetX(int visibleIndex, int visibleCount)
        {
            return plotLeft + (visibleIndex / (double)Math.Max(1, visibleCount - 1)) * plotWidth;
        }

        private double GetY(double value)
        {
            var normalized = (value - plotMin) / plotRange;
            return plotTop + (1.0 - normalized) * plotHeight;
        }

        private sealed class ZoomState
        {
            public int StartIndex { get; set; }
            public int EndIndex { get; set; }
            public bool HasManualYRange { get; set; }
            public double MinY { get; set; }
            public double MaxY { get; set; }
        }
    }
}










