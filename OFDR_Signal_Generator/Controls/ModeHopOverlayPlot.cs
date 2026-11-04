using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OFDR_Signal_Core.Models;

namespace OFDR_Signal_Generator.Controls
{
    public sealed class TimeRangeRequestedEventArgs : EventArgs
    {
        public double StartSeconds { get; set; }
        public double EndSeconds { get; set; }
    }

    // Dedicated plot: explicit x coordinates, independent voltage axes, exact interval shading.
    public sealed class ModeHopOverlayPlot : FrameworkElement
    {
        public static readonly DependencyProperty ViewportProperty = DependencyProperty.Register(nameof(Viewport), typeof(ModeHopViewport), typeof(ModeHopOverlayPlot), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnViewportChanged));
        public ModeHopViewport Viewport { get => (ModeHopViewport)GetValue(ViewportProperty); set => SetValue(ViewportProperty, value); }
        public event EventHandler<TimeRangeRequestedEventArgs> RangeRequested;
        private Point? dragStart;
        private Point mouse;
        private Rect area;
        private readonly PlotZoomWindow zoom = new PlotZoomWindow();
        private bool hasFullExtent, navigationRequested;
        private double fullStart, fullEnd, requestedStart, requestedEnd;
        private double auxiliaryMin, auxiliaryMax, triggerMin, triggerMax;
        private static readonly Brush AuxiliaryBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));
        private static readonly Brush TriggerBrush = new SolidColorBrush(Color.FromRgb(251, 146, 60));
        private static readonly Brush ExclusionBrush = new SolidColorBrush(Color.FromArgb(65, 248, 75, 96));

        public ModeHopOverlayPlot() { MinHeight = 240; Cursor = Cursors.Cross; ClipToBounds = true; ToolTip = "휠: 확대/축소 · 드래그: 영역 확대 · 우클릭: 이전 · 더블클릭: 전체"; }
        internal Rect InteractionArea => area;
        internal Rect ZoomBounds => zoom.Bounds;
        private double ViewStart => fullStart + (fullEnd - fullStart) * zoom.Bounds.Left;
        private double ViewEnd => fullStart + (fullEnd - fullStart) * zoom.Bounds.Right;
        private static void OnViewportChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
        {
            var plot = (ModeHopOverlayPlot)target;
            var data = (ModeHopViewport)e.NewValue;
            if (data == null) { plot.hasFullExtent = false; plot.navigationRequested = false; plot.zoom.Reset(); return; }
            if (!plot.hasFullExtent)
            {
                plot.fullStart = data.StartSeconds; plot.fullEnd = data.EndSeconds; plot.hasFullExtent = true;
            }
            if (!plot.navigationRequested)
            {
                plot.zoom.Reset();
                plot.zoom.SetX((data.StartSeconds - plot.fullStart) / (plot.fullEnd - plot.fullStart), (data.EndSeconds - plot.fullStart) / (plot.fullEnd - plot.fullStart), true);
            }
            else if (Math.Abs(data.StartSeconds - plot.requestedStart) < 1e-10 && Math.Abs(data.EndSeconds - plot.requestedEnd) < 1e-10) plot.navigationRequested = false;
            if (data.StartSeconds == plot.fullStart && data.EndSeconds == plot.fullEnd)
            {
                plot.auxiliaryMin = data.Auxiliary.Values.Min(); plot.auxiliaryMax = data.Auxiliary.Values.Max();
                plot.triggerMin = Math.Min(data.Trigger.Values.Min(), data.ThresholdVoltage); plot.triggerMax = Math.Max(data.Trigger.Values.Max(), data.ThresholdVoltage);
                if (data.SweepTrigger != null) { plot.triggerMin = Math.Min(plot.triggerMin, data.SweepTrigger.Values.Min()); plot.triggerMax = Math.Max(plot.triggerMax, data.SweepTrigger.Values.Max()); }
                Pad(ref plot.auxiliaryMin, ref plot.auxiliaryMax); Pad(ref plot.triggerMin, ref plot.triggerMax);
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(15, 23, 42)), null, new Rect(RenderSize), 8, 8);
            area = new Rect(78, 42, Math.Max(1, ActualWidth - 158), Math.Max(1, ActualHeight - 95));
            var v = Viewport;
            if (v == null) { Text(dc, "CSV를 읽으면 전체 신호가 표시됩니다.", 40, 65, Brushes.LightGray); return; }
            var auxMin = auxiliaryMin + (auxiliaryMax - auxiliaryMin) * zoom.Bounds.Top;
            var auxMax = auxiliaryMin + (auxiliaryMax - auxiliaryMin) * zoom.Bounds.Bottom;
            var trMin = triggerMin + (triggerMax - triggerMin) * zoom.Bounds.Top;
            var trMax = triggerMin + (triggerMax - triggerMin) * zoom.Bounds.Bottom;
            Text(dc, "CH3 보조간섭계 (V)", area.Left, 10, AuxiliaryBrush);
            Text(dc, "CH2 트리거 (V) · 붉은 음영: 제외 예정", Math.Max(area.Left + 230, area.Right - 300), 10, TriggerBrush);
            dc.PushClip(new RectangleGeometry(area));
            foreach (var range in v.Ranges)
            {
                if (range.EndSeconds <= ViewStart || range.StartSeconds >= ViewEnd) continue;
                var left = X(Math.Max(ViewStart, range.StartSeconds));
                var right = X(Math.Min(ViewEnd, range.EndSeconds));
                dc.DrawRectangle(ExclusionBrush, null, new Rect(left, area.Top, Math.Max(1, right - left), area.Height));
            }
            for (var tick = 0; tick <= 5; tick++)
            {
                var y = area.Top + area.Height * tick / 5;
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(49, 62, 83)), 0.6), new Point(area.Left, y), new Point(area.Right, y));
            }
            var thresholdY = Y(v.ThresholdVoltage, trMin, trMax);
            dc.DrawLine(new Pen(TriggerBrush, 0.8) { DashStyle = DashStyles.Dash }, new Point(area.Left, thresholdY), new Point(area.Right, thresholdY));
            var lastLabel = double.NegativeInfinity;
            foreach (var boundary in v.SweepBoundaries)
            {
                var x = X(boundary.Seconds);
                dc.DrawLine(new Pen(Brushes.Khaki, 0.7) { DashStyle = DashStyles.Dot }, new Point(x, area.Top), new Point(x, area.Bottom));
                if (x - lastLabel > 75) { Text(dc, "S" + boundary.Number, x + 4, area.Top + 4, Brushes.Khaki); lastLabel = x; }
            }
            Trace(dc, v.Auxiliary, auxMin, auxMax, AuxiliaryBrush);
            Trace(dc, v.Trigger, trMin, trMax, TriggerBrush);
            if (v.SweepTrigger != null) { Trace(dc, v.SweepTrigger, trMin, trMax, Brushes.LightGreen); Text(dc, "CH1 시작 트리거 (초록)", area.Left + 5, area.Top + 23, Brushes.LightGreen); }
            if (dragStart.HasValue)
            {
                var left = Math.Max(area.Left, Math.Min(dragStart.Value.X, mouse.X));
                var right = Math.Min(area.Right, Math.Max(dragStart.Value.X, mouse.X));
                var top = Math.Abs(dragStart.Value.Y - mouse.Y) < 6 ? area.Top : Math.Min(dragStart.Value.Y, mouse.Y);
                var height = Math.Abs(dragStart.Value.Y - mouse.Y) < 6 ? area.Height : Math.Abs(dragStart.Value.Y - mouse.Y);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(65, 125, 211, 252)), new Pen(Brushes.LightSkyBlue, 1), new Rect(left, top, Math.Max(0, right - left), height));
            }
            dc.Pop();
            for (var tick = 0; tick <= 5; tick++)
            {
                var ratio = tick / 5.0;
                var y = area.Bottom - area.Height * ratio;
                Text(dc, (auxMin + (auxMax - auxMin) * ratio).ToString("G4", CultureInfo.InvariantCulture), 5, y - 8, AuxiliaryBrush);
                Text(dc, (trMin + (trMax - trMin) * ratio).ToString("G4", CultureInfo.InvariantCulture), area.Right + 8, y - 8, TriggerBrush);
                var t = ViewStart + (ViewEnd - ViewStart) * ratio;
                Text(dc, t.ToString("G7", CultureInfo.InvariantCulture), area.Left + area.Width * ratio - 13, area.Bottom + 9, Brushes.LightGray);
            }
            Text(dc, "시간 (s) · 휠: 확대/축소 · 드래그: 영역 확대 · 우클릭: 이전 · 더블클릭: 전체", area.Left, area.Bottom + 32, Brushes.LightGray);
            if (area.Contains(mouse)) Text(dc, Time(mouse.X).ToString("F7", CultureInfo.InvariantCulture) + " s", area.Right - 135, area.Bottom + 32, Brushes.White);
        }

        private void Trace(DrawingContext dc, TimedPreview data, double min, double max, Brush brush)
        {
            if (data.Values.Length == 0) return;
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(X(data.Seconds[0]), Y(data.Values[0], min, max)), false, false);
                for (var i = 1; i < data.Values.Length; i++) context.LineTo(new Point(X(data.Seconds[i]), Y(data.Values[i], min, max)), true, false);
            }
            geometry.Freeze(); dc.DrawGeometry(null, new Pen(brush, 1), geometry);
        }
        private static void Pad(ref double min, ref double max)
        {
            var padding = Math.Max((max - min) * 0.06, 1e-6); min -= padding; max += padding;
        }
        private double X(double seconds) => area.Left + area.Width * (seconds - ViewStart) / (ViewEnd - ViewStart);
        private double Y(double value, double min, double max) => area.Bottom - area.Height * (value - min) / (max - min);
        private double Time(double x) => ViewStart + (Math.Max(area.Left, Math.Min(area.Right, x)) - area.Left) / area.Width * (ViewEnd - ViewStart);
        private void Text(DrawingContext dc, string text, double x, double y, Brush brush)
        {
            dc.DrawText(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (Viewport == null || !area.Contains(e.GetPosition(this))) return;
            if (e.ClickCount == 2) { ResetZoom(); e.Handled = true; return; }
            dragStart = mouse = e.GetPosition(this); CaptureMouse(); InvalidateVisual();
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); mouse = e.GetPosition(this); InvalidateVisual(); }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!dragStart.HasValue) return;
            var start = dragStart.Value; var end = e.GetPosition(this);
            dragStart = null; ReleaseMouseCapture(); InvalidateVisual();
            ZoomSelection(start, end); e.Handled = true;
        }
        internal bool ZoomAt(Point point, int delta)
        {
            if (Viewport == null || dragStart.HasValue || !zoom.Wheel(point, area, delta)) return false;
            Navigate(); return true;
        }
        internal bool ZoomSelection(Point first, Point last)
        {
            if (Viewport == null || !zoom.Select(first, last, area)) return false;
            Navigate(); return true;
        }
        internal void ResetZoom() { zoom.Reset(); Navigate(); }
        internal void PreviousZoom() { if (zoom.Undo()) Navigate(); }
        private void Navigate()
        {
            InvalidateVisual();
            if (Viewport == null || RangeRequested == null || (Math.Abs(ViewStart - Viewport.StartSeconds) < 1e-10 && Math.Abs(ViewEnd - Viewport.EndSeconds) < 1e-10)) return;
            requestedStart = ViewStart; requestedEnd = ViewEnd; navigationRequested = true;
            RangeRequested.Invoke(this, new TimeRangeRequestedEventArgs { StartSeconds = requestedStart, EndSeconds = requestedEnd });
        }
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e); if (Viewport == null || !area.Contains(e.GetPosition(this))) return;
            ZoomAt(e.GetPosition(this), e.Delta); e.Handled = true;
        }
        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonDown(e); if (Viewport == null || !area.Contains(e.GetPosition(this))) return;
            PreviousZoom(); e.Handled = true;
        }
        protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); dragStart = null; InvalidateVisual(); }
    }
}
