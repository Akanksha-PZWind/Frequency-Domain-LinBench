using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LinBench.Core.Models;

namespace LinBench.Wpf.Controls
{
    /// <summary>
    /// NEW: animated 3-D wireframe turbine for the "Campbell visualization diagram" (the WPF side of
    /// animate_mode_shape.py). The frames (tower, nacelle, 3 blades as point lists) come from the backend
    /// (/api/mode_shape); this control only projects and draws them:
    ///   • orthographic view, X downwind, Y lateral, Z up; drag = rotate, Ctrl + wheel = zoom, double-click = reset
    ///   • undeformed turbine faint grey, deformed turbine coloured (blade 1 / 2 / 3 = blue / orange / green)
    ///   • plays the cycle in a loop (Play / Pause, Speed)
    /// </summary>
    public class TurbineView : FrameworkElement
    {
        private ModeShapeResult? _shape;
        private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        private DateTime _t0 = DateTime.UtcNow;
        private double _phase;                 // frames played so far (fractional)
        private double _az = -35, _el = 18, _zoom = 1;
        private Point? _drag;
        private double _dragAz, _dragEl;

        private static readonly Color[] BladeColors =
        {
            Color.FromRgb(31, 119, 180), Color.FromRgb(255, 127, 14), Color.FromRgb(44, 160, 44)   // tab: blue, orange, green
        };

        public bool IsPlaying { get; private set; } = true;
        /// <summary>Animation speed: cycles per second = Speed × 0.6.</summary>
        public double Speed { get; set; } = 1.0;
        public bool ShowReference { get; set; } = true;
        /// <summary>Text drawn top-left when no mode is selected.</summary>
        public string EmptyText { get; set; } = "Click a point in the Campbell diagram to animate that mode.";

        public event Action? PlayingChanged;

        public TurbineView()
        {
            ClipToBounds = true;
            Focusable = true;
            Cursor = Cursors.SizeAll;
            _timer.Tick += (s, e) => InvalidateVisual();
            Loaded += (s, e) => { if (IsPlaying && _shape != null) _timer.Start(); };
            Unloaded += (s, e) => _timer.Stop();
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible && IsPlaying && _shape != null) _timer.Start(); else _timer.Stop();
            };
        }

        public void SetShape(ModeShapeResult? shape)
        {
            _shape = shape;
            if (_shape != null && IsPlaying && IsVisible) _timer.Start();
            if (_shape == null) _timer.Stop();
            InvalidateVisual();
        }

        public void SetPlaying(bool play)
        {
            if (play == IsPlaying) return;
            // keep the current frame when pausing / resuming
            _phase = CurrentFrameFloat();
            _t0 = DateTime.UtcNow;
            IsPlaying = play;
            if (play && _shape != null) _timer.Start(); else _timer.Stop();
            PlayingChanged?.Invoke();
            InvalidateVisual();
        }

        /// <summary>View presets: "3d", "front" (from upwind), "side", "top".</summary>
        public void SetView(string view)
        {
            (_az, _el) = view switch
            {
                "front" => (0.0, 0.0),
                "side" => (90.0, 0.0),
                "top" => (0.0, 89.0),
                _ => (-35.0, 18.0)
            };
            _zoom = 1;
            InvalidateVisual();
        }

        private double CurrentFrameFloat()
        {
            int n = _shape?.Frames.Count ?? 0;
            if (n == 0) return 0;
            if (!IsPlaying) return _phase;
            double cyclesPerSecond = 0.6 * Math.Max(0.05, Speed);
            return _phase + (DateTime.UtcNow - _t0).TotalSeconds * cyclesPerSecond * n;
        }

        // ------------------------------------------------------------------ projection

        private (double u, double v) Project(double x, double y, double z)
        {
            double a = _az * Math.PI / 180, e = _el * Math.PI / 180;
            // camera looks along (cos a, sin a, 0), tilted down by e; a = 0: from upwind, looking downwind
            double u = x * Math.Sin(a) - y * Math.Cos(a);           // screen right
            double w = x * Math.Cos(a) + y * Math.Sin(a);           // depth (away from the viewer)
            double v = z * Math.Cos(e) + w * Math.Sin(e);           // screen up
            return (u, v);
        }

        private static IEnumerable<(double x, double y, double z)> Points(List<List<double>>? part)
        {
            if (part == null || part.Count < 3) yield break;
            int n = Math.Min(part[0].Count, Math.Min(part[1].Count, part[2].Count));
            for (int i = 0; i < n; i++) yield return (part[0][i], part[1][i], part[2][i]);
        }

        private static IEnumerable<List<List<double>>> Parts(TurbineFrame f) =>
            new[] { f.Tower, f.Nacelle }.Concat(f.Blades);

        // ------------------------------------------------------------------ drawing

        private static Brush B(Color c, byte a = 255) { var b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); b.Freeze(); return b; }
        private static Pen P(Color c, double w, byte a = 255)
        {
            var p = new Pen(B(c, a), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            p.Freeze();
            return p;
        }

        private FormattedText T(string s, double size, Brush b, bool bold = false) =>
            new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
                size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRectangle(B(Color.FromRgb(250, 251, 252)), P(Color.FromRgb(226, 230, 235), 1), new Rect(0, 0, W, H));
            var ink = B(Color.FromRgb(31, 41, 55));
            var muted = B(Color.FromRgb(100, 112, 128));
            if (_shape == null || _shape.Frames.Count == 0 || _shape.Reference == null)
            {
                var t = T(EmptyText, 14, muted);
                t.MaxTextWidth = Math.Max(50, W - 40);
                dc.DrawText(t, new Point((W - t.Width) / 2, (H - t.Height) / 2));
                return;
            }

            // fit: bounds of every frame in this view
            double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
            foreach (var f in _shape.Frames.Append(_shape.Reference))
                foreach (var part in Parts(f))
                    foreach (var (x, y, z) in Points(part))
                    {
                        var (u, v) = Project(x, y, z);
                        u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
                    }
            double R = _shape.HubRadius + _shape.BladeLen;
            // ground square (±R around the tower base) in the fit as well
            foreach (var (gx, gy) in new[] { (-R, -R), (R, -R), (R, R), (-R, R) })
            {
                var (u, v) = Project(gx, gy, 0);
                u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
            }
            double padL = 20, padR = 20, padT = 64, padB = 34;
            double sc = Math.Min((W - padL - padR) / Math.Max(1e-6, u1 - u0), (H - padT - padB) / Math.Max(1e-6, v1 - v0)) * _zoom;
            double cu = (u0 + u1) / 2, cv = (v0 + v1) / 2;
            double cx = padL + (W - padL - padR) / 2, cy = padT + (H - padT - padB) / 2;
            Point S(double x, double y, double z)
            {
                var (u, v) = Project(x, y, z);
                return new Point(cx + (u - cu) * sc, cy - (v - cv) * sc);
            }

            StreamGeometry Line(List<List<double>>? part)
            {
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    bool first = true;
                    foreach (var (x, y, z) in Points(part))
                    {
                        var pt = S(x, y, z);
                        if (first) { ctx.BeginFigure(pt, false, false); first = false; }
                        else ctx.LineTo(pt, true, true);
                    }
                }
                g.Freeze();
                return g;
            }

            // ground: square and a cross under the tower
            var grid = P(Color.FromRgb(214, 219, 226), 1);
            var gsq = new StreamGeometry();
            using (var ctx = gsq.Open())
            {
                ctx.BeginFigure(S(-R, -R, 0), false, true);
                ctx.LineTo(S(R, -R, 0), true, true);
                ctx.LineTo(S(R, R, 0), true, true);
                ctx.LineTo(S(-R, R, 0), true, true);
            }
            gsq.Freeze();
            dc.DrawGeometry(B(Color.FromRgb(238, 241, 244)), grid, gsq);
            dc.DrawLine(grid, S(-R, 0, 0), S(R, 0, 0));
            dc.DrawLine(grid, S(0, -R, 0), S(0, R, 0));

            // reference (undeformed) turbine
            if (ShowReference)
            {
                var refPen = P(Color.FromRgb(160, 168, 180), 1.2, 150);
                foreach (var part in Parts(_shape.Reference)) dc.DrawGeometry(null, refPen, Line(part));
            }

            // current frame
            int n = _shape.Frames.Count;
            int fi = ((int)Math.Floor(CurrentFrameFloat()) % n + n) % n;
            var fr = _shape.Frames[fi];
            dc.DrawGeometry(null, P(Color.FromRgb(55, 65, 81), 5), Line(fr.Tower));
            dc.DrawGeometry(null, P(Color.FromRgb(70, 80, 96), 8), Line(fr.Nacelle));
            for (int b = 0; b < fr.Blades.Count; b++)
                dc.DrawGeometry(null, P(BladeColors[b % 3], 3.2), Line(fr.Blades[b]));
            var hub = Points(fr.Nacelle).LastOrDefault();
            dc.DrawEllipse(B(Color.FromRgb(70, 80, 96)), null, S(hub.x, hub.y, hub.z), 4.5, 4.5);

            // title block
            var head = T(_shape.Label, 15, ink, true);
            dc.DrawText(head, new Point(14, 10));
            var sub = T($"f_n {ChartFormat.Sig(_shape.FnHz, 4)} Hz  ·  ζ {ChartFormat.Sig(_shape.Zeta * 100, 3)} %  ·  " +
                        $"wind {ChartFormat.Sig(_shape.Wind, 3)} m/s  ·  rotor {ChartFormat.Sig(_shape.RotorRpm, 3)} rpm  ·  " +
                        $"deflection × {ChartFormat.Sig(_shape.Scale, 2)}", 12, muted);
            dc.DrawText(sub, new Point(14, 32));

            // axis triad (bottom left) and phase (bottom right)
            var o = new Point(40, H - 40);
            void Axis(double x, double y, double z, string name, Color c)
            {
                var (u, v) = Project(x, y, z);
                double len = 22;
                var end = new Point(o.X + u * len, o.Y - v * len);
                dc.DrawLine(P(c, 1.6), o, end);
                var t = T(name, 11, B(c), true);
                dc.DrawText(t, new Point(end.X - t.Width / 2 + u * 6, end.Y - t.Height / 2 - v * 6));
            }
            Axis(1, 0, 0, "X", Color.FromRgb(200, 30, 30));
            Axis(0, 1, 0, "Y", Color.FromRgb(22, 128, 61));
            Axis(0, 0, 1, "Z", Color.FromRgb(31, 111, 209));
            var hint = T($"phase {fi * 360 / n}°  ·  drag to rotate · Ctrl + wheel to zoom · double-click to reset", 11, muted);
            dc.DrawText(hint, new Point(W - hint.Width - 12, H - hint.Height - 8));
        }

        // ------------------------------------------------------------------ mouse

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            if (e.ClickCount == 2) { SetView("3d"); return; }
            _drag = e.GetPosition(this);
            _dragAz = _az; _dragEl = _el;
            CaptureMouse();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_drag is not Point d) return;
            var p = e.GetPosition(this);
            _az = _dragAz - (p.X - d.X) * 0.45;
            _el = Math.Clamp(_dragEl + (p.Y - d.Y) * 0.35, -10, 89);
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            _drag = null;
            ReleaseMouseCapture();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            // plain wheel scrolls the page (like the charts); Ctrl + wheel zooms
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.12 : 1 / 1.12), 0.4, 6);
            InvalidateVisual();
            e.Handled = true;
        }
    }
}
