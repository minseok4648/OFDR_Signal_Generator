using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Generator.Controls
{
    public sealed class AuxiliaryDiagnosticPlot : FrameworkElement
    {
        public static readonly DependencyProperty PlotProperty = DependencyProperty.Register(nameof(Plot), typeof(DiagnosticPlotData), typeof(AuxiliaryDiagnosticPlot), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPlotChanged));
        public DiagnosticPlotData Plot { get => (DiagnosticPlotData)GetValue(PlotProperty); set => SetValue(PlotProperty, value); }
        public event EventHandler<TimeRangeRequestedEventArgs> RangeRequested;
        private Rect area;
        private Point? dragStart;
        private Point mouse;
        private readonly PlotZoomWindow zoom = new PlotZoomWindow();
        public AuxiliaryDiagnosticPlot() { MinHeight = 240; ClipToBounds = true; Cursor = Cursors.Cross; ToolTip = "휠: 확대/축소 · 드래그: 영역 확대 · 우클릭: 이전 · 더블클릭: 전체"; }
        private static void OnPlotChanged(DependencyObject target, DependencyPropertyChangedEventArgs e) { var plot = (AuxiliaryDiagnosticPlot)target; plot.zoom.Reset(); plot.dragStart = null; }
        internal Rect InteractionArea => area;
        internal Rect ZoomBounds => zoom.Bounds;
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(15, 23, 42)), null, new Rect(RenderSize), 6, 6);
            var p = Plot;
            if (p?.Primary?.Values == null || p.Primary.Values.Length == 0) { Label(dc, "데이터가 없습니다.", 20, 40, Brushes.LightGray); return; }
            area = new Rect(78, 50, Math.Max(1, ActualWidth - 100), Math.Max(1, ActualHeight - 110));
            Label(dc, p.Title, 12, 8, Brushes.White);
            if (p.Secondary != null) Label(dc, p.SecondaryLabel ?? "주황: 생성 Grid", 12, 27, Brushes.Orange);
            var values = p.Primary.Values.Concat(p.Secondary?.Values ?? new double[0]);
            var min = values.Min(); var max = values.Max(); var padding = Math.Max((max - min) * 0.06, 1e-8); min -= padding; max += padding;
            var fullMin = min; var fullRange = max - min;
            min = fullMin + fullRange * zoom.Bounds.Top; max = fullMin + fullRange * zoom.Bounds.Bottom;
            dc.PushClip(new RectangleGeometry(area));
            for (var i = 0; i <= 4; i++)
            {
                var y = area.Top + area.Height * i / 4;
                dc.DrawLine(new Pen(Brushes.SlateGray, 0.3), new Point(area.Left, y), new Point(area.Right, y));
            }
            foreach (var marker in p.Markers ?? new double[0])
            {
                var x = X(marker); dc.DrawLine(new Pen(Brushes.Goldenrod, 0.6) { DashStyle = DashStyles.Dot }, new Point(x, area.Top), new Point(x, area.Bottom));
            }
            DrawTrace(dc, p.Primary, min, max, Brushes.DeepSkyBlue);
            if (p.Secondary != null) DrawTrace(dc, p.Secondary, min, max, Brushes.Orange);
            if (dragStart.HasValue)
            {
                var left = Math.Min(mouse.X, dragStart.Value.X); var width = Math.Abs(mouse.X - dragStart.Value.X);
                var top = Math.Abs(mouse.Y - dragStart.Value.Y) < 6 ? area.Top : Math.Min(mouse.Y, dragStart.Value.Y);
                var height = Math.Abs(mouse.Y - dragStart.Value.Y) < 6 ? area.Height : Math.Abs(mouse.Y - dragStart.Value.Y);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 125, 211, 252)), new Pen(Brushes.LightBlue, 1), new Rect(left, top, width, height));
            }
            dc.Pop();
            for (var i = 0; i <= 4; i++)
            {
                var ratio = i / 4.0;
                Label(dc, (min + (max - min) * ratio).ToString("G5", CultureInfo.InvariantCulture), 3, area.Bottom - area.Height * ratio - 6, Brushes.LightGray);
                Label(dc, Time(area.Left + area.Width * ratio).ToString("G9", CultureInfo.InvariantCulture), Math.Min(area.Right - 65, area.Left + area.Width * ratio - 12), area.Bottom + 8, Brushes.LightGray);
            }
            Label(dc, p.XAxis + " · " + p.YAxis, area.Left, area.Bottom + 29, Brushes.LightGray);
        }
        private void DrawTrace(DrawingContext dc, TimedPreview series, double min, double max, Brush brush)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                for (var i = 0; i < series.Values.Length; i++)
                {
                    var point = new Point(X(series.Seconds[i]), area.Bottom - area.Height * (series.Values[i] - min) / (max - min));
                    if (i == 0) context.BeginFigure(point, false, false); else context.LineTo(point, true, false);
                }
            }
            geometry.Freeze(); dc.DrawGeometry(null, new Pen(brush, 1), geometry);
        }
        private double X(double value) => area.Left + area.Width * ((value - Plot.XStart) / Math.Max(1e-20, Plot.XEnd - Plot.XStart) - zoom.Bounds.Left) / zoom.Bounds.Width;
        private double Time(double x) => Plot.XStart + (zoom.Bounds.Left + (Math.Max(area.Left, Math.Min(area.Right, x)) - area.Left) / area.Width * zoom.Bounds.Width) * (Plot.XEnd - Plot.XStart);
        private void Label(DrawingContext dc, string text, double x, double y, Brush brush) => dc.DrawText(new FormattedText(text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e); if (Plot == null || !area.Contains(e.GetPosition(this))) return;
            if (e.ClickCount == 2) { ResetZoom(); e.Handled = true; return; }
            dragStart = mouse = e.GetPosition(this); CaptureMouse(); InvalidateVisual();
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); mouse = e.GetPosition(this); if (dragStart.HasValue) InvalidateVisual(); }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e); if (!dragStart.HasValue) return;
            var first = dragStart.Value; var end = e.GetPosition(this); dragStart = null; ReleaseMouseCapture();
            ZoomSelection(first, end); e.Handled = true;
        }
        internal bool ZoomAt(Point point, int delta) { if (Plot == null || dragStart.HasValue || !zoom.Wheel(point, area, delta)) return false; InvalidateVisual(); return true; }
        internal bool ZoomSelection(Point first, Point last)
        {
            if (Plot == null) return false;
            var from = Time(Math.Min(first.X, last.X)); var to = Time(Math.Max(first.X, last.X));
            if (!zoom.Select(first, last, area)) return false;
            InvalidateVisual(); RangeRequested?.Invoke(this, new TimeRangeRequestedEventArgs { StartSeconds = from, EndSeconds = to }); return true;
        }
        internal void ResetZoom() { zoom.Reset(); InvalidateVisual(); }
        internal void PreviousZoom() { if (zoom.Undo()) InvalidateVisual(); }
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e); if (Plot == null || !area.Contains(e.GetPosition(this))) return;
            ZoomAt(e.GetPosition(this), e.Delta); e.Handled = true;
        }
        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonDown(e); if (Plot == null || !area.Contains(e.GetPosition(this))) return;
            PreviousZoom(); e.Handled = true;
        }
        protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); dragStart = null; InvalidateVisual(); }
    }
}
