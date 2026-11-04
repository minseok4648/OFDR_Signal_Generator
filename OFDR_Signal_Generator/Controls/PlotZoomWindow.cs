using System;
using System.Collections.Generic;
using System.Windows;

namespace OFDR_Signal_Generator.Controls
{
    // Display-only viewport. Y is measured bottom-up, independently of voltage units.
    internal sealed class PlotZoomWindow
    {
        private readonly Stack<Rect> history = new Stack<Rect>();
        public Rect Bounds { get; private set; } = new Rect(0, 0, 1, 1);
        public void Reset() { Bounds = new Rect(0, 0, 1, 1); history.Clear(); }
        public void SetX(double first, double end, bool resetY)
        {
            Bounds = new Rect(Math.Max(0, first), resetY ? 0 : Bounds.Y, Math.Max(1e-10, Math.Min(1, end) - Math.Max(0, first)), resetY ? 1 : Bounds.Height);
        }
        public bool Undo()
        {
            if (history.Count == 0) return false;
            Bounds = history.Pop(); return true;
        }
        public bool Wheel(Point point, Rect area, int delta)
        {
            if (delta == 0 || !area.Contains(point) || area.Width <= 0 || area.Height <= 0) return false;
            double left, right, bottom, top;
            var factor = WheelFactor(delta);
            ScaleRange(Bounds.Left, Bounds.Right, 0, 1, (point.X - area.Left) / area.Width, factor, out left, out right);
            ScaleRange(Bounds.Top, Bounds.Bottom, 0, 1, (area.Bottom - point.Y) / area.Height, factor, out bottom, out top);
            return Change(new Rect(left, bottom, right - left, top - bottom));
        }
        public bool Select(Point first, Point last, Rect area)
        {
            if (area.Width <= 0 || area.Height <= 0 || Math.Abs(first.X - last.X) < 6) return false;
            var left = Unit((Math.Min(first.X, last.X) - area.Left) / area.Width);
            var right = Unit((Math.Max(first.X, last.X) - area.Left) / area.Width);
            var bottom = Math.Abs(first.Y - last.Y) < 6 ? 0 : Unit((area.Bottom - Math.Max(first.Y, last.Y)) / area.Height);
            var top = Math.Abs(first.Y - last.Y) < 6 ? 1 : Unit((area.Bottom - Math.Min(first.Y, last.Y)) / area.Height);
            if (right <= left) return false;
            if (top <= bottom) { bottom = 0; top = 1; }
            var width = Math.Max(1e-8, Bounds.Width * (right - left));
            var height = Math.Max(1e-8, Bounds.Height * (top - bottom));
            return Change(new Rect(Math.Min(1 - width, Bounds.X + Bounds.Width * left), Math.Min(1 - height, Bounds.Y + Bounds.Height * bottom), width, height));
        }
        private bool Change(Rect next)
        {
            if (next == Bounds) return false;
            history.Push(Bounds); Bounds = next; return true;
        }
        public static double WheelFactor(int delta) => Math.Pow(1.25, -Math.Max(-8, Math.Min(8, delta / 120.0)));
        public static void ScaleRange(double first, double end, double fullFirst, double fullEnd, double anchor, double factor, out double left, out double right)
        {
            var span = Math.Max(fullEnd - fullFirst, 1e-20);
            var width = Math.Min(span, Math.Max(span * 1e-8, (end - first) * factor));
            var fixedPoint = first + (end - first) * Unit(anchor);
            left = Math.Max(fullFirst, Math.Min(fullEnd - width, fixedPoint - width * Unit(anchor)));
            right = left + width;
        }
        private static double Unit(double value) => Math.Max(0, Math.Min(1, value));
    }
}
