using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OFDR_Signal_Generator.ViewModels;

namespace OFDR_Signal_Generator.Controls
{
    public sealed class StrainComparisonPlot : FrameworkElement
    {
        public static readonly DependencyProperty EntriesProperty = DependencyProperty.Register(nameof(Entries), typeof(IEnumerable<StrainComparisonEntry>), typeof(StrainComparisonPlot),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, EntriesChanged));
        public IEnumerable<StrainComparisonEntry> Entries { get => (IEnumerable<StrainComparisonEntry>)GetValue(EntriesProperty); set => SetValue(EntriesProperty, value); }
        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(StrainComparisonPlot), new FrameworkPropertyMetadata("거리별 strain 비교 (με)", FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty XAxisTitleProperty = DependencyProperty.Register(nameof(XAxisTitle), typeof(string), typeof(StrainComparisonPlot), new FrameworkPropertyMetadata("거리 (m)", FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty YAxisTitleProperty = DependencyProperty.Register(nameof(YAxisTitle), typeof(string), typeof(StrainComparisonPlot), new FrameworkPropertyMetadata("strain (με)", FrameworkPropertyMetadataOptions.AffectsRender));
        public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
        public string XAxisTitle { get => (string)GetValue(XAxisTitleProperty); set => SetValue(XAxisTitleProperty, value); }
        public string YAxisTitle { get => (string)GetValue(YAxisTitleProperty); set => SetValue(YAxisTitleProperty, value); }
        private readonly PlotZoomWindow zoom = new PlotZoomWindow();
        private readonly HashSet<StrainComparisonEntry> observedEntries = new HashSet<StrainComparisonEntry>();
        private Rect area;
        private Point? dragStart;
        private Point mouse;
        private double fullLeft, fullRight, fullBottom, fullTop;
        internal Rect InteractionArea => area;
        internal Rect ZoomBounds => zoom.Bounds;
        internal int RenderedCurveCount { get; private set; }
        public StrainComparisonPlot()
        {
            MinHeight = 260; ClipToBounds = true; Cursor = Cursors.Cross;
            ToolTip = "휠: 확대/축소 · 드래그: 영역 확대 · 우클릭: 이전 범위 · 더블클릭: 전체";
        }
        private static void EntriesChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
        {
            var plot = (StrainComparisonPlot)target;
            if (e.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= plot.CollectionChanged;
            if (e.NewValue is INotifyCollectionChanged current) current.CollectionChanged += plot.CollectionChanged;
            plot.ObserveEntries();
        }
        private void CollectionChanged(object sender, NotifyCollectionChangedEventArgs e) { ObserveEntries(); }
        private void ObserveEntries()
        {
            foreach (var entry in observedEntries)
                PropertyChangedEventManager.RemoveHandler(entry, EntryPropertyChanged, nameof(StrainComparisonEntry.DisplayEnabled));
            observedEntries.Clear();
            foreach (var entry in Entries ?? Enumerable.Empty<StrainComparisonEntry>())
                if (observedEntries.Add(entry)) PropertyChangedEventManager.AddHandler(entry, EntryPropertyChanged, nameof(StrainComparisonEntry.DisplayEnabled));
            ResetZoom();
        }
        private void EntryPropertyChanged(object sender, PropertyChangedEventArgs e) { ResetZoom(); }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(15, 23, 42)), null, new Rect(0, 0, ActualWidth, ActualHeight), 6, 6);
            var curves = Entries?.Where(e => e.DisplayEnabled).ToArray() ?? new StrainComparisonEntry[0]; RenderedCurveCount = curves.Length;
            area = new Rect(78, 48, Math.Max(1, ActualWidth - 105), Math.Max(1, ActualHeight - 108));
            Label(dc, Title, 12, 7, Brushes.White);
            Label(dc, YAxisTitle, 12, 27, Brushes.LightGray);
            if (curves.Length == 0)
            {
                Label(dc, Entries?.Any() == true ? "표시할 기록의 Display Enable을 체크하세요." : "'현재 값 기록'을 누르면 완료 결과가 여기에 누적됩니다.", 40, 65, Brushes.LightGray);
                return;
            }
            fullLeft = curves.Min(c => c.XValues[0]); fullRight = curves.Max(c => c.XValues.Last());
            if (fullRight <= fullLeft) { fullLeft -= 0.5; fullRight += 0.5; }
            fullBottom = Math.Min(0, curves.Min(c => c.YValues.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).DefaultIfEmpty(0).Min()));
            fullTop = Math.Max(0, curves.Max(c => c.YValues.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).DefaultIfEmpty(0).Max()));
            var padding = Math.Max(1e-9, (fullTop - fullBottom) * 0.06);
            if (fullBottom == fullTop) padding = 1;
            fullBottom -= padding; fullTop += padding;
            var minY = fullBottom + zoom.Bounds.Top * (fullTop - fullBottom);
            var maxY = fullBottom + zoom.Bounds.Bottom * (fullTop - fullBottom);
            for (var i = 0; i <= 4; i++)
            {
                var f = i / 4.0; var x = area.Left + f * area.Width; var y = area.Bottom - f * area.Height;
                dc.DrawLine(new Pen(Brushes.SlateGray, 0.3), new Point(x, area.Top), new Point(x, area.Bottom));
                dc.DrawLine(new Pen(Brushes.SlateGray, 0.3), new Point(area.Left, y), new Point(area.Right, y));
                Label(dc, (minY + f * (maxY - minY)).ToString("G6", CultureInfo.InvariantCulture), 3, y - 7, Brushes.LightGray);
                Label(dc, Distance(x).ToString("G7", CultureInfo.InvariantCulture), Math.Min(area.Right - 55, x - 12), area.Bottom + 9, Brushes.LightGray);
            }
            Label(dc, XAxisTitle, area.Left, area.Bottom + 32, Brushes.LightGray);
            for (var i = 0; i < curves.Length; i++)
            {
                var x = Math.Max(area.Left, area.Right - curves.Length * 90) + i * 90;
                dc.DrawLine(new Pen(curves[i].LineBrush, 2), new Point(x, 32), new Point(x + 15, 32));
                Label(dc, curves[i].Name, x + 20, 24, Brushes.LightGray);
            }
            dc.PushClip(new RectangleGeometry(area));
            if (minY <= 0 && maxY >= 0) dc.DrawLine(new Pen(Brushes.LightSlateGray, 1), new Point(area.Left, Y(0)), new Point(area.Right, Y(0)));
            foreach (var curve in curves)
            {
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    var connected = false;
                    for (var i = 0; i < curve.XValues.Length; i++)
                    {
                        if (double.IsNaN(curve.YValues[i])) { connected = false; continue; }
                        var p = new Point(X(curve.XValues[i]), Y(curve.YValues[i]));
                        if (!connected) { context.BeginFigure(p, false, false); connected = true; } else context.LineTo(p, true, false);
                    }
                }
                var pen = new Pen(curve.LineBrush, curve.IsReference ? 1.5 : 1.3);
                if (curve.IsReference) pen.DashStyle = DashStyles.Dash;
                geometry.Freeze(); dc.DrawGeometry(null, pen, geometry);
                if (curve.XValues.Length == 1) dc.DrawEllipse(curve.LineBrush, null, new Point(X(curve.XValues[0]), Y(curve.YValues[0])), 3, 3);
            }
            if (dragStart.HasValue)
            {
                var left = Math.Min(mouse.X, dragStart.Value.X); var top = Math.Min(mouse.Y, dragStart.Value.Y);
                var height = Math.Abs(mouse.Y - dragStart.Value.Y);
                if (height < 6) { top = area.Top; height = area.Height; }
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(65, 125, 211, 252)), new Pen(Brushes.LightBlue, 1), new Rect(left, top, Math.Abs(mouse.X - dragStart.Value.X), height));
            }
            dc.Pop();
        }
        private double X(double value) => area.Left + area.Width * ((value - fullLeft) / (fullRight - fullLeft) - zoom.Bounds.Left) / zoom.Bounds.Width;
        private double Y(double value) => area.Bottom - area.Height * ((value - fullBottom) / (fullTop - fullBottom) - zoom.Bounds.Top) / zoom.Bounds.Height;
        private double Distance(double x) => fullLeft + (zoom.Bounds.Left + (x - area.Left) / area.Width * zoom.Bounds.Width) * (fullRight - fullLeft);
        private void Label(DrawingContext dc, string text, double x, double y, Brush brush) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
        internal void ResetZoom() { zoom.Reset(); dragStart = null; InvalidateVisual(); }
        internal bool ZoomAt(Point point, int delta) { if (RenderedCurveCount == 0 || dragStart.HasValue || !zoom.Wheel(point, area, delta)) return false; InvalidateVisual(); return true; }
        internal bool ZoomSelection(Point first, Point last) { if (RenderedCurveCount == 0 || !zoom.Select(first, last, area)) return false; InvalidateVisual(); return true; }
        protected override void OnMouseWheel(MouseWheelEventArgs e) { base.OnMouseWheel(e); if (ZoomAt(e.GetPosition(this), e.Delta)) e.Handled = true; }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e); if (RenderedCurveCount == 0 || !area.Contains(e.GetPosition(this))) return;
            if (e.ClickCount == 2) { ResetZoom(); e.Handled = true; return; }
            dragStart = mouse = e.GetPosition(this); CaptureMouse(); InvalidateVisual(); e.Handled = true;
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); mouse = e.GetPosition(this); if (dragStart.HasValue) InvalidateVisual(); }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e); if (!dragStart.HasValue) return;
            var first = dragStart.Value; var last = e.GetPosition(this); dragStart = null; ReleaseMouseCapture(); ZoomSelection(first, last); e.Handled = true;
        }
        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) { base.OnMouseRightButtonDown(e); if (zoom.Undo()) { InvalidateVisual(); e.Handled = true; } }
        protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); dragStart = null; InvalidateVisual(); }
    }
}
