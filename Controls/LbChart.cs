using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LinBench.Wpf.Controls
{
    public enum LineDash { Solid, Dash, Dot, DashDot, Long }
    public enum MarkerShape { Circle, Square, Diamond, TriUp, TriDown, Cross, Ring }   // Cross = ×, Ring = hollow ○

    /// <summary>One data series of an <see cref="LbChart"/>.</summary>
    public sealed class ChartSeries
    {
        public string Name { get; set; } = string.Empty;
        public IReadOnlyList<double> X { get; set; } = Array.Empty<double>();
        public IReadOnlyList<double> Y { get; set; } = Array.Empty<double>();   // NaN = gap
        public Color Color { get; set; } = LbChart.Palette[0];
        public double LineWidth { get; set; } = 1.6;     // 0 = markers only
        public double MarkerSize { get; set; } = 3.2;    // radius in px, 0 = no markers
        public MarkerShape Marker { get; set; } = MarkerShape.Circle;
        /// <summary>Optional marker shape per point (e.g. symmetry of each mode in the s-plane).</summary>
        public IReadOnlyList<MarkerShape>? Markers { get; set; }
        /// <summary>Not used for the automatic axis range (e.g. damping-ratio guide rays).</summary>
        public bool NoExtent { get; set; }
        public LineDash Dash { get; set; } = LineDash.Solid;
        public double Opacity { get; set; } = 1.0;
        public bool InLegend { get; set; } = true;
        public bool NoHover { get; set; }
        public string Unit { get; set; } = string.Empty;
        public IReadOnlyList<string>? Tips { get; set; } // hover text per point (lines separated by \n)
    }

    public enum AnnotationKind { VLine, HLine, Band, PointLabel }

    /// <summary>Reference lines, bands and text labels drawn on top of / under the data.</summary>
    public sealed class ChartAnnotation
    {
        public AnnotationKind Kind { get; set; }
        public double X { get; set; }
        public double X1 { get; set; }
        public double Y { get; set; }
        public string? Label { get; set; }
        public Color Color { get; set; } = Color.FromRgb(100, 116, 139);
        public bool Dashed { get; set; }
        public double Width { get; set; } = 1.0;
        public double Opacity { get; set; } = 0.08;      // Band only
        public bool AnchorEnd { get; set; }              // PointLabel: text ends at the point (right-aligned)
    }

    public enum ChartHover { Nearest, AllAtX }

    /// <summary>
    /// View settings shared by the whole app (Settings window + sidebar ▸ PLOTS), like the original LinBench.
    /// Saved on this PC in %USERPROFILE%\.linbench\wpf_view_settings.json as soon as something changes.
    /// </summary>
    public sealed class ChartSettings : INotifyPropertyChanged
    {
        public static ChartSettings Instance { get; } = Load();

        // plots
        private string _legend = "auto", _foldAbove = "6", _plotHeight = "normal", _lines = "normal", _grid = "on", _cursor = "full";
        // appearance / accessibility / units
        private string _colours = "matlab", _contrast = "normal", _scale = "100", _viewList = "names", _freqUnit = "rad/s";

        /// <summary>auto · inside · beside · above · off</summary>
        public string Legend { get => _legend; set => Set(ref _legend, value); }
        /// <summary>4 · 6 · 10 · never – curves before a legend is folded to "n/m curves" (Auto)</summary>
        public string FoldAbove { get => _foldAbove; set => Set(ref _foldAbove, value); }
        /// <summary>compact · normal · tall</summary>
        public string PlotHeight { get => _plotHeight; set => Set(ref _plotHeight, value); }
        /// <summary>thin · normal · bold</summary>
        public string Lines { get => _lines; set => Set(ref _lines, value); }
        /// <summary>off · on · fine</summary>
        public string Grid { get => _grid; set => Set(ref _grid, value); }
        /// <summary>full · vertical · off ("line" is read as vertical)</summary>
        public string Cursor { get => _cursor; set => Set(ref _cursor, value == "line" ? "vertical" : value); }
        /// <summary>matlab · cb (colour-blind safe, Okabe–Ito) · vivid</summary>
        public string Colours { get => _colours; set => Set(ref _colours, value); }
        /// <summary>normal · high</summary>
        public string Contrast { get => _contrast; set => Set(ref _contrast, value); }
        /// <summary>90 · 100 · 110 · 125 (% of the workspace, plots included)</summary>
        public string Scale { get => _scale; set => Set(ref _scale, value); }
        /// <summary>names · icons</summary>
        public string ViewList { get => _viewList; set => Set(ref _viewList, value); }
        /// <summary>rad/s · hz (frequency axis of control plots; Campbell is always Hz)</summary>
        public string FreqUnit { get => _freqUnit; set => Set(ref _freqUnit, value); }

        public double LineFactor => _lines == "thin" ? 0.65 : _lines == "bold" ? 1.6 : 1.0;
        public double HeightFactor => _plotHeight == "compact" ? 0.78 : _plotHeight == "tall" ? 1.3 : 1.0;
        public int FoldLimit => _foldAbove == "never" ? int.MaxValue : int.TryParse(_foldAbove, out var n) ? n : 6;
        public double ScaleFactor => double.TryParse(_scale, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v / 100.0, 0.5, 2) : 1.0;

        public void RestoreDefaults()
        {
            var d = new ChartSettings();
            Legend = d.Legend; FoldAbove = d.FoldAbove; PlotHeight = d.PlotHeight; Lines = d.Lines; Grid = d.Grid; Cursor = d.Cursor;
            Colours = d.Colours; Contrast = d.Contrast; Scale = d.Scale; ViewList = d.ViewList; FreqUnit = d.FreqUnit;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private bool _loading;

        private void Set(ref string field, string value, [CallerMemberName] string? name = null)
        {
            value = (value ?? string.Empty).ToLowerInvariant();
            if (field == value || value.Length == 0) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            if (!_loading) Save();
        }

        private static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".linbench", "wpf_view_settings.json");

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var dict = new Dictionary<string, string>
                {
                    ["legend"] = _legend, ["foldAbove"] = _foldAbove, ["plotHeight"] = _plotHeight, ["lines"] = _lines, ["grid"] = _grid,
                    ["cursor"] = _cursor, ["colours"] = _colours, ["contrast"] = _contrast, ["scale"] = _scale, ["viewList"] = _viewList,
                    ["freqUnit"] = _freqUnit
                };
                File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(dict));
            }
            catch { /* settings are a convenience – never crash */ }
        }

        private static ChartSettings Load()
        {
            var cs = new ChartSettings();
            try
            {
                if (!File.Exists(FilePath)) return cs;
                var d = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath));
                if (d == null) return cs;
                cs._loading = true;
                string G(string k, string def) => d.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v) ? v : def;
                cs.Legend = G("legend", cs._legend); cs.FoldAbove = G("foldAbove", cs._foldAbove); cs.PlotHeight = G("plotHeight", cs._plotHeight);
                cs.Lines = G("lines", cs._lines); cs.Grid = G("grid", cs._grid); cs.Cursor = G("cursor", cs._cursor);
                cs.Colours = G("colours", cs._colours); cs.Contrast = G("contrast", cs._contrast); cs.Scale = G("scale", cs._scale);
                cs.ViewList = G("viewList", cs._viewList); cs.FreqUnit = G("freqUnit", cs._freqUnit);
            }
            catch { }
            finally { cs._loading = false; }
            return cs;
        }
    }

    /// <summary>Number formatting like the original LinBench web UI (significant digits, no trailing zeros).</summary>
    public static class ChartFormat
    {
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        public static string Sig(double? v, int sig = 4)
        {
            if (v == null || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return "–";
            double x = v.Value;
            if (x == 0) return "0";
            double ax = Math.Abs(x);
            if (ax >= 1e6 || ax < 1e-4) return x.ToString("0.##e0", CI);
            int dec = Math.Clamp(sig - 1 - (int)Math.Floor(Math.Log10(ax)), 0, 10);
            var s = Math.Round(x, dec).ToString("F" + dec, CI);
            if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
            return s == "-0" ? "0" : s;
        }

        public static double NaN(double? v) => v ?? double.NaN;
    }

    /// <summary>
    /// Chart in the style of the original LinBench charts (chart.js): framed plot, ticks outside, axis titles,
    /// line dash + marker shape per series, reference lines with labels, point labels, legend (collapsible
    /// "n/m curves" box when there are many series; click an entry to hide / show it), hover read-out,
    /// Ctrl+wheel zoom / drag pan / double-click reset, right-click: enlarge, PNG, CSV.
    /// </summary>
    public class LbChart : FrameworkElement
    {
        public static readonly Color[] Palette =
        {
            Color.FromRgb(0, 114, 189), Color.FromRgb(217, 83, 25), Color.FromRgb(237, 177, 32), Color.FromRgb(126, 47, 142),
            Color.FromRgb(119, 172, 48), Color.FromRgb(77, 190, 238), Color.FromRgb(162, 20, 47), Color.FromRgb(77, 77, 77)
        };
        public static readonly Color Accent = Color.FromRgb(31, 111, 209);
        public static readonly Color MutedColor = Color.FromRgb(120, 132, 150);
        public static readonly Color BaselineColor = Color.FromRgb(190, 196, 204);
        public static readonly Color GoodColor = Color.FromRgb(22, 128, 61);
        public static readonly Color WarnColor = Color.FromRgb(217, 119, 6);
        public static readonly Color CritColor = Color.FromRgb(200, 30, 30);

        private static readonly Typeface Face = new("Segoe UI");
        private static readonly Brush TextNormal = Frozen(Color.FromRgb(30, 41, 59));
        private static readonly Brush TextHigh = Frozen(Color.FromRgb(5, 10, 20));
        private static readonly Brush TickNormal = Frozen(Color.FromRgb(51, 65, 85));
        private static Brush TextBrush => ChartSettings.Instance.Contrast == "high" ? TextHigh : TextNormal;
        private static Brush TickBrush => ChartSettings.Instance.Contrast == "high" ? TextHigh : TickNormal;
        private static readonly Brush MutedBrush = Frozen(MutedColor);
        private static readonly Pen GridNormal = FrozenPen(Color.FromRgb(228, 232, 238), 1);
        private static readonly Pen GridHigh = FrozenPen(Color.FromRgb(190, 198, 210), 1);
        private static Pen GridPen => ChartSettings.Instance.Contrast == "high" ? GridHigh : GridNormal;
        private static readonly Pen FineGridPen = FrozenPen(Color.FromRgb(242, 244, 247), 1);
        private static readonly Pen FrameNormal = FrozenPen(Color.FromRgb(30, 41, 59), 1);
        private static readonly Pen FrameHigh = FrozenPen(Color.FromRgb(0, 0, 0), 1.6);
        private static Pen FramePen => ChartSettings.Instance.Contrast == "high" ? FrameHigh : FrameNormal;

        // curve colour sets: the code uses the MATLAB palette; Settings ▸ Curve colours swaps them at drawing time
        private static readonly Color[] OkabeIto =
        {
            Color.FromRgb(0, 114, 178), Color.FromRgb(230, 159, 0), Color.FromRgb(240, 228, 66), Color.FromRgb(204, 121, 167),
            Color.FromRgb(0, 158, 115), Color.FromRgb(86, 180, 233), Color.FromRgb(213, 94, 0), Color.FromRgb(0, 0, 0)
        };
        private static readonly Color[] Vivid =
        {
            Color.FromRgb(31, 119, 180), Color.FromRgb(255, 127, 14), Color.FromRgb(188, 189, 34), Color.FromRgb(148, 103, 189),
            Color.FromRgb(44, 160, 44), Color.FromRgb(23, 190, 207), Color.FromRgb(214, 39, 40), Color.FromRgb(127, 127, 127)
        };
        public static Color Map(Color c)
        {
            var set = ChartSettings.Instance.Colours;
            if (set == "matlab") return c;
            int i = Array.IndexOf(Palette, c);
            if (i < 0) return c;
            return set == "cb" ? OkabeIto[i] : Vivid[i];
        }
        private static readonly Pen TickPen = FrozenPen(Color.FromRgb(30, 41, 59), 1);

        private List<ChartSeries> _series = new();
        private List<ChartAnnotation> _ann = new();
        private readonly HashSet<string> _hidden = new();
        private Rect? _view;                       // zoomed / panned range (null = auto)
        private Point? _mouse;
        private Point _dragStart;
        private Rect _dragView;
        private bool _dragging;
        private Rect _plotRect;
        private Rect _lastRange;
        private bool _legendOpen;
        private Rect _legendToggle = Rect.Empty;
        private readonly List<(Rect r, string name)> _legendHits = new();

        public string XLabel { get; set; } = string.Empty;
        public string YLabel { get; set; } = string.Empty;
        public string XName { get; set; } = "x";
        public string XUnit { get; set; } = string.Empty;
        public string ChartTitle { get; set; } = string.Empty;   // used for Enlarge / file names
        public bool ShowLegend { get; set; } = true;             // drawn when ≥ 2 legend series
        public ChartHover Hover { get; set; } = ChartHover.Nearest;
        public double XPad { get; set; } = 0.03;
        public double YPad { get; set; } = 0.07;
        public double[]? YInclude { get; set; }
        public double[]? XInclude { get; set; }
        /// <summary>Fixed y range [min, max] (e.g. Campbell 0 … fmax); null = automatic.</summary>
        public double[]? YDomain { get; set; }
        /// <summary>Above this many legend entries the legend is the collapsible "▸ n/m curves" box
        /// (null = Settings ▸ Fold legends above).</summary>
        public int? LegendCollapseAbove { get; set; }
        /// <summary>NEW: logarithmic x axis (Bode diagrams). Points with x ≤ 0 are skipped; ticks at decades.</summary>
        public bool IsXLog { get; set; }

        /// <summary>x in axis space (log10 for a log axis) and back.</summary>
        private double TX(double x) => IsXLog ? (x > 0 ? Math.Log10(x) : double.NaN) : x;
        private double IX(double t) => IsXLog ? Math.Pow(10, t) : t;

        private double? _baseHeight;

        /// <summary>Raised when the user hides / shows a series in the legend (names of the hidden series).</summary>
        public event Action<IReadOnlyCollection<string>>? HiddenChanged;

        /// <summary>NEW: a data point was clicked (series, index of the point in X/Y). Used by the Campbell
        /// diagram to animate the clicked mode. Only series with markers and without NoHover can be clicked.</summary>
        public event Action<ChartSeries, int>? PointClicked;
        private Rect? _viewBeforeDrag;

        public LbChart()
        {
            ClipToBounds = true;
            SnapsToDevicePixels = true;
            Cursor = Cursors.Cross;
            ContextMenu = BuildMenu();   // Ctrl+wheel: zoom · drag: pan · double-click: reset · right-click: enlarge, PNG, CSV
            Loaded += (s, e) =>
            {
                ChartSettings.Instance.PropertyChanged += OnSettingsChanged;
                if (_baseHeight == null && !double.IsNaN(Height)) _baseHeight = Height;
                ApplyHeight();
            };
            Unloaded += (s, e) => ChartSettings.Instance.PropertyChanged -= OnSettingsChanged;
        }

        private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChartSettings.PlotHeight)) ApplyHeight();
            InvalidateVisual();
        }

        private void ApplyHeight()
        {
            if (_baseHeight is double h) Height = Math.Round(h * ChartSettings.Instance.HeightFactor);
        }

        public void SetData(IEnumerable<ChartSeries> series, IEnumerable<ChartAnnotation>? annotations = null)
        {
            _series = series.ToList();
            _ann = annotations?.ToList() ?? new List<ChartAnnotation>();
            _hidden.RemoveWhere(n => !_series.Any(s => s.Name == n));
            _view = null;
            InvalidateVisual();
        }

        public void Clear() => SetData(Array.Empty<ChartSeries>());

        /// <summary>Hide exactly these series (used to keep two linked charts in step).</summary>
        public void SetHidden(IEnumerable<string> names)
        {
            _hidden.Clear();
            foreach (var n in names) _hidden.Add(n);
            InvalidateVisual();
        }

        private bool Visible(ChartSeries s) => !(s.InLegend && _hidden.Contains(s.Name));

        protected override Size MeasureOverride(Size available) =>
            new(double.IsInfinity(available.Width) ? 400 : available.Width,
                double.IsInfinity(available.Height) ? 280 : available.Height);

        // ------------------------------------------------------------------ helpers

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        private static Pen FrozenPen(Color c, double w, bool dashed = false)
        {
            var p = new Pen(Frozen(c), w);
            if (dashed) p.DashStyle = new DashStyle(new double[] { 4, 3 }, 0);
            p.Freeze();
            return p;
        }

        private static Pen SeriesPen(Color c, double w, LineDash dash)
        {
            var p = new Pen(Frozen(c), w) { LineJoin = PenLineJoin.Round };
            // DashStyle lengths are in multiples of the line width
            p.DashStyle = dash switch
            {
                LineDash.Dash => new DashStyle(new double[] { 3.5, 2.2 }, 0),
                LineDash.Dot => new DashStyle(new double[] { 0.9, 1.9 }, 0),
                LineDash.DashDot => new DashStyle(new double[] { 4, 2, 1, 2 }, 0),
                LineDash.Long => new DashStyle(new double[] { 6, 4 }, 0),
                _ => DashStyles.Solid
            };
            p.Freeze();
            return p;
        }

        private static Color WithOpacity(Color c, double o) { c.A = (byte)(255 * Math.Clamp(o, 0, 1)); return c; }

        public static void DrawMarker(DrawingContext dc, MarkerShape shape, Point p, double r, Brush fill, Pen? outline)
        {
            switch (shape)
            {
                case MarkerShape.Circle:
                    dc.DrawEllipse(fill, outline, p, r, r);
                    return;
                case MarkerShape.Square:
                    dc.DrawRectangle(fill, outline, new Rect(p.X - r * 0.9, p.Y - r * 0.9, r * 1.8, r * 1.8));
                    return;
                case MarkerShape.Cross:
                {
                    var pen = new Pen(fill, Math.Max(1.5, r * 0.42)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    pen.Freeze();
                    double d = r * 0.95;
                    dc.DrawLine(pen, new Point(p.X - d, p.Y - d), new Point(p.X + d, p.Y + d));
                    dc.DrawLine(pen, new Point(p.X - d, p.Y + d), new Point(p.X + d, p.Y - d));
                    return;
                }
                case MarkerShape.Ring:
                {
                    var pen = new Pen(fill, Math.Max(1.4, r * 0.38));
                    pen.Freeze();
                    dc.DrawEllipse(Brushes.White, pen, p, r, r);
                    return;
                }
            }
            Point[] pts = shape switch
            {
                MarkerShape.Diamond => new[] { new Point(p.X, p.Y - r * 1.25), new Point(p.X + r * 1.1, p.Y), new Point(p.X, p.Y + r * 1.25), new Point(p.X - r * 1.1, p.Y) },
                MarkerShape.TriUp => new[] { new Point(p.X, p.Y - r * 1.3), new Point(p.X + r * 1.2, p.Y + r * 0.85), new Point(p.X - r * 1.2, p.Y + r * 0.85) },
                _ => new[] { new Point(p.X, p.Y + r * 1.3), new Point(p.X + r * 1.2, p.Y - r * 0.85), new Point(p.X - r * 1.2, p.Y - r * 0.85) },
            };
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(pts[0], true, true);
                ctx.PolyLineTo(pts.Skip(1).ToList(), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(fill, outline, g);
        }

        private FormattedText Text(string s, double size, Brush brush, bool bold = false) =>
            new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                bold ? new Typeface(Face.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal) : Face,
                size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        private Rect AutoRange()
        {
            double x0 = double.PositiveInfinity, x1 = double.NegativeInfinity, y0 = double.PositiveInfinity, y1 = double.NegativeInfinity;
            void IncX(double v) { if (Finite(v)) { x0 = Math.Min(x0, v); x1 = Math.Max(x1, v); } }
            void IncY(double v) { if (Finite(v)) { y0 = Math.Min(y0, v); y1 = Math.Max(y1, v); } }

            foreach (var s in _series.Where(q => !q.NoExtent))
                for (int i = 0; i < Math.Min(s.X.Count, s.Y.Count); i++)
                    if (Finite(TX(s.X[i])) && Finite(s.Y[i])) { IncX(TX(s.X[i])); IncY(s.Y[i]); }
            foreach (var a in _ann)
            {
                if (a.Kind == AnnotationKind.HLine) IncY(a.Y);
                if (a.Kind == AnnotationKind.VLine) IncX(TX(a.X));
            }
            if (XInclude != null) foreach (var v in XInclude) IncX(TX(v));
            if (YInclude != null) foreach (var v in YInclude) IncY(v);

            bool fixedY = YDomain is { Length: 2 } && Finite(YDomain[0]) && Finite(YDomain[1]) && YDomain[1] > YDomain[0];
            if (fixedY) { y0 = YDomain![0]; y1 = YDomain[1]; }

            bool noY = y0 > y1;
            if (x0 > x1) { x0 = 0; x1 = 1; }
            if (noY) { y0 = 0; y1 = 1; }
            if (x1 - x0 < 1e-12) { double d = x0 == 0 ? 1 : Math.Abs(x0) * 0.05; x0 -= d; x1 += d; }
            if (y1 - y0 < 1e-12) { double d = y0 == 0 ? 1 : Math.Abs(y0) * 0.05; y0 -= d; y1 += d; }
            double dx = (x1 - x0) * XPad, dy = noY || fixedY ? 0 : (y1 - y0) * YPad;
            return new Rect(x0 - dx, y0 - dy, (x1 - x0) + 2 * dx, (y1 - y0) + 2 * dy);
        }

        private static double NiceStep(double span, int target)
        {
            double raw = span / Math.Max(1, target);
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double n = raw / mag;
            double s = n < 1.5 ? 1 : n < 3 ? 2 : n < 7 ? 5 : 10;
            return s * mag;
        }

        private static List<double> Ticks(double min, double max, double step)
        {
            var list = new List<double>();
            double start = Math.Ceiling(min / step - 1e-9) * step;
            for (double v = start; v <= max + step * 1e-9 && list.Count < 200; v += step)
                list.Add(Math.Abs(v) < step * 1e-9 ? 0 : v);
            return list;
        }

        private static string TickText(double v, double step)
        {
            int dec = Math.Clamp(-(int)Math.Floor(Math.Log10(step) + 1e-9), 0, 8);
            if (Math.Abs(v) >= 1e5 || (Math.Abs(v) > 0 && Math.Abs(v) < 1e-4))
                return v.ToString("0.##e0", CultureInfo.InvariantCulture);
            return v.ToString("F" + dec, CultureInfo.InvariantCulture);
        }

        private List<ChartSeries> LegendItems() =>
            _series.Where(s => s.InLegend && !string.IsNullOrEmpty(s.Name)).GroupBy(s => s.Name).Select(g => g.First()).ToList();

        // ------------------------------------------------------------------ rendering

        protected override void OnRender(DrawingContext dc)
        {
            var set = ChartSettings.Instance;
            double lf = set.LineFactor;
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H));
            if (W < 60 || H < 60) return;

            var r = _view ?? AutoRange();
            _lastRange = r;
            double xmin = r.Left, xmax = r.Right, ymin = r.Top, ymax = r.Bottom;

            var legendItems = LegendItems();
            bool legendAllowed = ShowLegend && set.Legend != "off" && legendItems.Count >= 2;
            bool beside = legendAllowed && set.Legend == "beside";
            bool above = legendAllowed && set.Legend == "above";
            List<FormattedText>? aboveTexts = null;
            int aboveRows = 0;
            if (above)
            {
                aboveTexts = legendItems.Select(s => Text(s.Name, 11.5, TextBrush)).ToList();
                double x = 0, avail = Math.Max(100, W - 80);
                aboveRows = 1;
                foreach (var t in aboveTexts) { double iw = t.Width + 44; if (x + iw > avail && x > 0) { aboveRows++; x = 0; } x += iw; }
            }
            double besideW = 0;
            List<FormattedText>? besideTexts = null;
            if (beside)
            {
                besideTexts = legendItems.Select(s => Text(s.Name, 11.5, TextBrush)).ToList();
                besideW = Math.Min(W * 0.35, besideTexts.Max(t => t.Width) + 46);
            }

            // y ticks first: their width decides the left margin
            double yStep = NiceStep(ymax - ymin, Math.Max(3, (int)(H / 70)));
            var yt = Ticks(ymin, ymax, yStep);
            var yTexts = yt.Select(v => Text(TickText(v, yStep), 11.5, TickBrush)).ToList();
            double yTickW = yTexts.Count > 0 ? yTexts.Max(t => t.Width) : 20;

            double L = 24 + yTickW + 8, R = 10 + besideW, T = 10 + aboveRows * 18, B = 46;
            var pr = new Rect(L, T, Math.Max(10, W - L - R), Math.Max(10, H - T - B));
            _plotRect = pr;
            double PXT(double t) => pr.Left + (t - xmin) / (xmax - xmin) * pr.Width;   // axis space
            double PX(double x) => PXT(TX(x));                                           // data space
            double PY(double y) => pr.Bottom - (y - ymin) / (ymax - ymin) * pr.Height;

            double xStep = NiceStep(xmax - xmin, Math.Max(3, (int)(pr.Width / 90)));
            // x ticks in axis space with their labels; a log axis gets decades (+ 2 and 5 when the range is short)
            var xt = new List<(double t, string label)>();
            var xMinor = new List<double>();
            if (IsXLog)
            {
                int k0 = (int)Math.Floor(xmin), k1 = (int)Math.Ceiling(xmax);
                bool few = xmax - xmin < 2.2;
                for (int k = k0; k <= k1 && k - k0 < 40; k++)
                    for (int m = 1; m <= 9; m++)
                    {
                        double t = k + Math.Log10(m);
                        if (t < xmin - 1e-9 || t > xmax + 1e-9) continue;
                        if (m == 1 || (few && (m == 2 || m == 5))) xt.Add((t, ChartFormat.Sig(m * Math.Pow(10, k), 3)));
                        else xMinor.Add(t);
                    }
                xStep = 1;
            }
            else
            {
                foreach (var v in Ticks(xmin, xmax, xStep)) xt.Add((v, TickText(v, xStep)));
            }

            // grid (on / fine / off) + tick labels
            if (set.Grid == "fine")
            {
                foreach (var v in Ticks(ymin, ymax, yStep / 5)) dc.DrawLine(FineGridPen, new Point(pr.Left, PY(v)), new Point(pr.Right, PY(v)));
                if (!IsXLog)
                    foreach (var v in Ticks(xmin, xmax, xStep / 5)) dc.DrawLine(FineGridPen, new Point(PXT(v), pr.Top), new Point(PXT(v), pr.Bottom));
            }
            if (set.Grid != "off")
                foreach (var v in xMinor) dc.DrawLine(FineGridPen, new Point(PXT(v), pr.Top), new Point(PXT(v), pr.Bottom));
            for (int i = 0; i < yt.Count; i++)
            {
                double y = PY(yt[i]);
                if (set.Grid != "off") dc.DrawLine(GridPen, new Point(pr.Left, y), new Point(pr.Right, y));
                dc.DrawLine(TickPen, new Point(pr.Left, y), new Point(pr.Left + 4, y));
                dc.DrawLine(TickPen, new Point(pr.Right - 4, y), new Point(pr.Right, y));
                dc.DrawText(yTexts[i], new Point(pr.Left - 6 - yTexts[i].Width, y - yTexts[i].Height / 2));
            }
            foreach (var (v, label) in xt)
            {
                double x = PXT(v);
                if (set.Grid != "off") dc.DrawLine(GridPen, new Point(x, pr.Top), new Point(x, pr.Bottom));
                dc.DrawLine(TickPen, new Point(x, pr.Bottom - 4), new Point(x, pr.Bottom));
                dc.DrawLine(TickPen, new Point(x, pr.Top), new Point(x, pr.Top + 4));
                var t = Text(label, 11.5, TickBrush);
                dc.DrawText(t, new Point(x - t.Width / 2, pr.Bottom + 5));
            }

            // axis titles
            if (!string.IsNullOrEmpty(XLabel))
            {
                var t = Text(XLabel, 13, TextBrush);
                dc.DrawText(t, new Point(pr.Left + pr.Width / 2 - t.Width / 2, H - t.Height - 4));
            }
            if (!string.IsNullOrEmpty(YLabel))
            {
                var t = Text(YLabel, 13, TextBrush);
                double cx = 4 + t.Height / 2, cy = pr.Top + pr.Height / 2;
                dc.PushTransform(new RotateTransform(-90, cx, cy));
                dc.DrawText(t, new Point(cx - t.Width / 2, cy - t.Height / 2));
                dc.Pop();
            }

            dc.PushClip(new RectangleGeometry(pr));

            // bands, reference lines
            foreach (var a in _ann.Where(q => q.Kind == AnnotationKind.Band))
            {
                double x0 = PX(Math.Min(a.X, a.X1)), x1 = PX(Math.Max(a.X, a.X1));
                dc.DrawRectangle(Frozen(WithOpacity(a.Color, a.Opacity)), null, new Rect(x0, pr.Top, Math.Max(1, x1 - x0), pr.Height));
            }
            foreach (var a in _ann.Where(q => q.Kind == AnnotationKind.HLine))
            {
                double y = PY(a.Y);
                dc.DrawLine(FrozenPen(a.Color, a.Width, a.Dashed), new Point(pr.Left, y), new Point(pr.Right, y));
                if (!string.IsNullOrEmpty(a.Label))
                {
                    var t = Text(a.Label!, 11, MutedBrush);
                    dc.DrawText(t, new Point(pr.Right - t.Width - 4, y - t.Height - 1));
                }
            }
            foreach (var a in _ann.Where(q => q.Kind == AnnotationKind.VLine))
            {
                double x = PX(a.X);
                dc.DrawLine(FrozenPen(a.Color, a.Width, a.Dashed), new Point(x, pr.Top), new Point(x, pr.Bottom));
            }

            // series: lines, then markers
            foreach (var s in _series.Where(Visible))
            {
                int n = Math.Min(s.X.Count, s.Y.Count);
                if (s.LineWidth <= 0 || n < 2) continue;
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    bool open = false;
                    for (int i = 0; i < n; i++)
                    {
                        if (!Finite(TX(s.X[i])) || !Finite(s.Y[i])) { open = false; continue; }
                        var p = new Point(PX(s.X[i]), PY(s.Y[i]));
                        if (!open) { ctx.BeginFigure(p, false, false); open = true; }
                        else ctx.LineTo(p, true, true);
                    }
                }
                geo.Freeze();
                dc.DrawGeometry(null, SeriesPen(WithOpacity(Map(s.Color), s.Opacity), s.LineWidth * lf, s.Dash), geo);
            }
            var white = FrozenPen(Colors.White, 0.8);
            foreach (var s in _series.Where(q => q.MarkerSize > 0 && Visible(q)))
            {
                var fill = Frozen(WithOpacity(Map(s.Color), s.Opacity));
                double mr = s.MarkerSize * Math.Sqrt(lf);
                for (int i = 0; i < Math.Min(s.X.Count, s.Y.Count); i++)
                    if (Finite(TX(s.X[i])) && Finite(s.Y[i]))
                        DrawMarker(dc, MarkerAt(s, i), new Point(PX(s.X[i]), PY(s.Y[i])), mr, fill, white);
            }

            // point labels (e.g. azimuth at each point, "3P" at the right edge)
            foreach (var a in _ann.Where(q => q.Kind == AnnotationKind.PointLabel && !string.IsNullOrEmpty(q.Label)))
            {
                var t = Text(a.Label!, 11, Frozen(a.Color));
                double x = a.AnchorEnd ? PX(a.X) - t.Width - 4 : PX(a.X) - t.Width / 2;
                dc.DrawText(t, new Point(x, PY(a.Y) - t.Height - 3));
            }

            dc.Pop();
            dc.DrawRectangle(null, FramePen, pr);

            _legendHits.Clear();
            _legendToggle = Rect.Empty;
            if (legendAllowed)
            {
                if (beside) DrawLegendBeside(dc, pr, legendItems, besideTexts!, besideW);
                else if (above) DrawLegendAbove(dc, pr, legendItems, aboveTexts!);
                else DrawLegend(dc, pr, legendItems, set.Legend == "inside");
            }
            if (set.Cursor != "off") DrawHover(dc, pr, PX, PY, set.Cursor == "full", xmin, xmax, ymin, ymax, xStep, yStep);
        }

        private static MarkerShape MarkerAt(ChartSeries s, int i) =>
            s.Markers != null && i < s.Markers.Count ? s.Markers[i] : s.Marker;

        private void DrawLegendRow(DrawingContext dc, ChartSeries s, FormattedText text, double left, double yMid, double rowW)
        {
            bool hidden = !Visible(s);
            var c = hidden ? Color.FromRgb(200, 205, 212) : Map(s.Color);
            if (s.LineWidth > 0)
                dc.DrawLine(SeriesPen(c, 2, s.Dash), new Point(left, yMid), new Point(left + 22, yMid));
            if (s.MarkerSize > 0) DrawMarker(dc, s.Marker, new Point(left + 11, yMid), 3.6, Frozen(c), null);
            dc.PushOpacity(hidden ? 0.45 : 1);
            dc.DrawText(text, new Point(left + 28, yMid - text.Height / 2));
            dc.Pop();
            _legendHits.Add((new Rect(left - 4, yMid - 9, rowW, 18), s.Name));
        }

        private void DrawLegendAbove(DrawingContext dc, Rect pr, List<ChartSeries> items, List<FormattedText> texts)
        {
            double x = pr.Left, y = 4 + 9, rowH = 18;
            for (int i = 0; i < items.Count; i++)
            {
                double iw = texts[i].Width + 44;
                if (x + iw > pr.Right && x > pr.Left) { x = pr.Left; y += rowH; }
                DrawLegendRow(dc, items[i], texts[i], x, y, iw - 8);
                x += iw;
            }
        }

        private void DrawLegend(DrawingContext dc, Rect pr, List<ChartSeries> items, bool neverFold = false)
        {
            var boxFill = Frozen(Color.FromArgb(240, 255, 255, 255));
            var boxPen = FrozenPen(Color.FromRgb(30, 41, 59), 1);
            double rowH = 18;

            if (!neverFold && items.Count > (LegendCollapseAbove ?? ChartSettings.Instance.FoldLimit))
            {
                // collapsible box at the top left: "▸ 14/14 curves"
                int shown = items.Count(s => Visible(s));
                var head = Text($"{(_legendOpen ? "▾" : "▸")} {shown}/{items.Count} curves", 12, TextBrush, true);
                double w = head.Width + 22, h = rowH + 8;
                var texts = _legendOpen ? items.Select(s => Text(s.Name, 11.5, TextBrush)).ToList() : new List<FormattedText>();
                if (_legendOpen && texts.Count > 0)
                {
                    w = Math.Max(w, texts.Max(t => t.Width) + 46);
                    h += rowH * texts.Count + 4;
                }
                h = Math.Min(h, pr.Height - 16);
                var box = new Rect(pr.Left + 8, pr.Top + 8, w, h);
                dc.DrawRectangle(boxFill, boxPen, box);
                dc.DrawText(head, new Point(box.Left + 10, box.Top + 4 + (rowH - head.Height) / 2));
                _legendToggle = new Rect(box.Left, box.Top, w, rowH + 8);
                if (_legendOpen)
                {
                    dc.PushClip(new RectangleGeometry(box));
                    for (int i = 0; i < items.Count; i++)
                        DrawLegendRow(dc, items[i], texts[i], box.Left + 10, box.Top + rowH + 10 + rowH * i + rowH / 2, w - 12);
                    dc.Pop();
                }
                return;
            }

            var tx = items.Select(s => Text(s.Name, 11.5, TextBrush)).ToList();
            double bw = tx.Max(t => t.Width) + 46, bh = rowH * items.Count + 8;
            var b = new Rect(pr.Right - bw - 8, pr.Top + 8, bw, bh);
            dc.DrawRectangle(boxFill, FrozenPen(Color.FromRgb(148, 163, 184), 1), b);
            for (int i = 0; i < items.Count; i++)
                DrawLegendRow(dc, items[i], tx[i], b.Left + 8, b.Top + 4 + rowH * i + rowH / 2, bw - 8);
        }

        private void DrawLegendBeside(DrawingContext dc, Rect pr, List<ChartSeries> items, List<FormattedText> texts, double w)
        {
            double rowH = 18, left = pr.Right + 14;
            for (int i = 0; i < items.Count; i++)
            {
                double y = pr.Top + 4 + rowH * i + rowH / 2;
                if (y > pr.Bottom) break;
                DrawLegendRow(dc, items[i], texts[i], left, y, w - 14);
            }
        }

        private void DrawBadge(DrawingContext dc, string text, Point at, bool alignRight, bool centerX)
        {
            var t = Text(text, 11.5, Brushes.White, true);
            double w = t.Width + 10, h = t.Height + 4;
            double x = centerX ? at.X - w / 2 : alignRight ? at.X - w : at.X;
            var r = new Rect(x, at.Y - h / 2, w, h);
            dc.DrawRoundedRectangle(Frozen(Color.FromRgb(30, 34, 40)), null, r, 3, 3);
            dc.DrawText(t, new Point(r.Left + 5, r.Top + 2));
        }

        private void DrawHover(DrawingContext dc, Rect pr, Func<double, double> PX, Func<double, double> PY, bool full,
                               double xmin, double xmax, double ymin, double ymax, double xStep, double yStep)
        {
            if (_mouse == null || _dragging || !pr.Contains(_mouse.Value)) return;
            if (_legendHits.Any(h => h.r.Contains(_mouse.Value)) || _legendToggle.Contains(_mouse.Value)) return;
            var m = _mouse.Value;

            // crosshair: dotted line(s) through the cursor with the values on the axes
            var dotted = new Pen(Frozen(Color.FromRgb(90, 98, 110)), 1) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) };
            dotted.Freeze();
            double vx = IX(xmin + (m.X - pr.Left) / pr.Width * (xmax - xmin));
            double vy = ymin + (pr.Bottom - m.Y) / pr.Height * (ymax - ymin);
            dc.DrawLine(dotted, new Point(m.X, pr.Top), new Point(m.X, pr.Bottom));
            DrawBadge(dc, IsXLog ? ChartFormat.Sig(vx, 3) : TickText(vx, xStep / 10), new Point(m.X, pr.Bottom + 12), false, true);
            if (full)
            {
                dc.DrawLine(dotted, new Point(pr.Left, m.Y), new Point(pr.Right, m.Y));
                DrawBadge(dc, TickText(vy, yStep / 10), new Point(pr.Left - 2, m.Y), true, false);
            }
            var lines = new List<(string text, Color? swatch, bool bold)>();
            var hoverable = _series.Where(s => !s.NoHover && Visible(s)).ToList();

            if (Hover == ChartHover.AllAtX)
            {
                double best = double.MaxValue, bx = double.NaN;
                foreach (var s in hoverable)
                    for (int i = 0; i < Math.Min(s.X.Count, s.Y.Count); i++)
                        if (Finite(TX(s.X[i])) && Finite(s.Y[i]) && Math.Abs(PX(s.X[i]) - m.X) < best) { best = Math.Abs(PX(s.X[i]) - m.X); bx = s.X[i]; }
                if (double.IsNaN(bx) || best > 40) return;

                double gx = PX(bx);
                lines.Add(($"{XName} {ChartFormat.Sig(bx, 4)}{(XUnit.Length > 0 ? " " + XUnit : "")}", null, true));
                foreach (var s in hoverable)
                {
                    int k = -1; double d = double.MaxValue;
                    for (int i = 0; i < Math.Min(s.X.Count, s.Y.Count); i++)
                        if (Finite(s.Y[i]) && Math.Abs(TX(s.X[i]) - TX(bx)) < d) { d = Math.Abs(TX(s.X[i]) - TX(bx)); k = i; }
                    if (k < 0 || Math.Abs(PX(s.X[k]) - gx) > 3) continue;
                    DrawMarker(dc, MarkerAt(s, k), new Point(gx, PY(s.Y[k])), 4.5, Frozen(Map(s.Color)), FrozenPen(Colors.White, 1.5));
                    lines.Add(($"{s.Name}   {ChartFormat.Sig(s.Y[k], 4)}{(s.Unit.Length > 0 ? " " + s.Unit : "")}", Map(s.Color), false));
                }
            }
            else
            {
                ChartSeries? bs = null; int bi = -1; double best = 30 * 30;
                foreach (var s in hoverable)
                    for (int i = 0; i < Math.Min(s.X.Count, s.Y.Count); i++)
                    {
                        if (!Finite(TX(s.X[i])) || !Finite(s.Y[i])) continue;
                        double dx = PX(s.X[i]) - m.X, dy = PY(s.Y[i]) - m.Y, d2 = dx * dx + dy * dy;
                        if (d2 < best) { best = d2; bs = s; bi = i; }
                    }
                if (bs == null) return;
                var p = new Point(PX(bs.X[bi]), PY(bs.Y[bi]));
                DrawMarker(dc, MarkerAt(bs, bi), p, 5, Frozen(Map(bs.Color)), FrozenPen(Colors.White, 1.5));
                if (bs.Tips != null && bi < bs.Tips.Count && !string.IsNullOrEmpty(bs.Tips[bi]))
                {
                    bool first = true;
                    foreach (var line in bs.Tips[bi].Split('\n'))
                    {
                        lines.Add((line, first ? Map(bs.Color) : (Color?)null, first));
                        first = false;
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(bs.Name)) lines.Add((bs.Name, Map(bs.Color), true));
                    lines.Add(($"{XName} {ChartFormat.Sig(bs.X[bi], 4)}{(XUnit.Length > 0 ? " " + XUnit : "")}", null, false));
                    lines.Add(($"{ChartFormat.Sig(bs.Y[bi], 5)}{(bs.Unit.Length > 0 ? " " + bs.Unit : "")}", null, false));
                }
            }
            if (lines.Count == 0) return;

            var texts = lines.Select(l => (t: Text(l.text, 12, TextBrush, l.bold), l.swatch)).ToList();
            double rowH = 18, w = texts.Max(x => x.t.Width) + 30, h = rowH * texts.Count + 10;
            double bx0 = m.X + 16, by0 = m.Y + 14;
            if (bx0 + w > ActualWidth - 4) bx0 = m.X - w - 16;
            if (by0 + h > ActualHeight - 4) by0 = Math.Max(4, ActualHeight - h - 4);
            var box = new Rect(Math.Max(4, bx0), by0, w, h);
            dc.DrawRoundedRectangle(Frozen(Color.FromArgb(245, 255, 255, 255)), FrozenPen(Color.FromRgb(203, 213, 225), 1), box, 6, 6);
            for (int i = 0; i < texts.Count; i++)
            {
                double y = box.Top + 5 + rowH * i;
                if (texts[i].swatch is Color c)
                    dc.DrawRectangle(Frozen(c), null, new Rect(box.Left + 8, y + rowH / 2 - 5, 10, 10));
                dc.DrawText(texts[i].t, new Point(box.Left + 24, y + (rowH - texts[i].t.Height) / 2));
            }
        }

        // ------------------------------------------------------------------ interaction

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = e.GetPosition(this);
            if (_dragging && _plotRect.Width > 0)
            {
                double dx = (p.X - _dragStart.X) / _plotRect.Width * _dragView.Width;
                double dy = (p.Y - _dragStart.Y) / _plotRect.Height * _dragView.Height;
                _view = new Rect(_dragView.X - dx, _dragView.Y + dy, _dragView.Width, _dragView.Height);
            }
            Cursor = _legendToggle.Contains(p) || _legendHits.Any(h => h.r.Contains(p)) ||
                     (!_dragging && PointClicked != null && NearestPoint(p, 14) != null) ? Cursors.Hand : Cursors.Cross;
            _mouse = p;
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            _mouse = null;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            var p = e.GetPosition(this);

            // legend: open / close the "n/m curves" box, hide / show a series
            if (_legendToggle.Contains(p))
            {
                _legendOpen = !_legendOpen;
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            foreach (var (r, name) in _legendHits)
            {
                if (!r.Contains(p)) continue;
                if (!_hidden.Remove(name)) _hidden.Add(name);
                InvalidateVisual();
                HiddenChanged?.Invoke(_hidden.ToList());
                e.Handled = true;
                return;
            }

            if (e.ClickCount == 2) { ResetView(); return; }
            _dragging = true;
            _dragStart = p;
            _viewBeforeDrag = _view;
            _dragView = _view ?? _lastRange;
            CaptureMouse();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            bool wasDragging = _dragging;
            _dragging = false;
            ReleaseMouseCapture();

            // NEW: a click (no real drag) on a data point
            var p = e.GetPosition(this);
            if (wasDragging && PointClicked != null && (p - _dragStart).Length < 4)
            {
                _view = _viewBeforeDrag;                // undo the tiny pan of the click
                var hit = NearestPoint(p, 14);
                if (hit != null) PointClicked(hit.Value.series, hit.Value.index);
                InvalidateVisual();
            }
        }

        /// <summary>Nearest clickable point within maxPx pixels (same mapping as the last drawing).</summary>
        private (ChartSeries series, int index)? NearestPoint(Point m, double maxPx)
        {
            var pr = _plotRect;
            var r = _lastRange;
            if (pr.Width <= 0 || pr.Height <= 0 || r.Width <= 0 || r.Height <= 0 || !pr.Contains(m)) return null;
            double PX(double x) => pr.Left + (TX(x) - r.Left) / r.Width * pr.Width;
            double PY(double y) => pr.Bottom - (y - r.Top) / r.Height * pr.Height;
            ChartSeries? bs = null; int bi = -1; double best = maxPx * maxPx;
            foreach (var s in _series.Where(q => !q.NoHover && q.MarkerSize > 0 && Visible(q)))
                for (int i = 0; i < Math.Min(s.X.Count, s.Y.Count); i++)
                {
                    if (!Finite(TX(s.X[i])) || !Finite(s.Y[i])) continue;
                    double dx = PX(s.X[i]) - m.X, dy = PY(s.Y[i]) - m.Y, d2 = dx * dx + dy * dy;
                    if (d2 < best) { best = d2; bs = s; bi = i; }
                }
            return bs == null ? null : (bs, bi);
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            // plain wheel scrolls the page; Ctrl + wheel zooms (like the + / − buttons of the original charts)
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || !_plotRect.Contains(e.GetPosition(this))) return;
            Zoom(e.Delta > 0 ? 0.8 : 1.25, e.GetPosition(this));
            e.Handled = true;
        }

        private void Zoom(double factor, Point? at = null)
        {
            var r = _view ?? _lastRange;
            var p = at ?? new Point(_plotRect.Left + _plotRect.Width / 2, _plotRect.Top + _plotRect.Height / 2);
            double fx = (p.X - _plotRect.Left) / _plotRect.Width, fy = (_plotRect.Bottom - p.Y) / _plotRect.Height;
            double cx = r.Left + fx * r.Width, cy = r.Top + fy * r.Height;
            double w = r.Width * factor, h = r.Height * factor;
            _view = new Rect(cx - fx * w, cy - fy * h, w, h);
            InvalidateVisual();
        }

        public void ResetView()
        {
            _view = null;
            InvalidateVisual();
        }

        private ContextMenu BuildMenu()
        {
            var menu = new ContextMenu();
            void Add(string header, Action a) { var mi = new MenuItem { Header = header }; mi.Click += (s, e) => a(); menu.Items.Add(mi); }
            Add("Enlarge", Enlarge);
            Add("Zoom in  (+)", () => Zoom(0.8));
            Add("Zoom out  (−)", () => Zoom(1.25));
            Add("Reset view", ResetView);
            Add("Show all curves", () => { _hidden.Clear(); InvalidateVisual(); HiddenChanged?.Invoke(_hidden.ToList()); });
            menu.Items.Add(new Separator());
            Add("Save PNG…", SavePng);
            Add("Copy image", CopyImage);
            Add("Save CSV…", SaveCsv);
            return menu;
        }

        private void Enlarge()
        {
            var big = new LbChart
            {
                XLabel = XLabel, YLabel = YLabel, XName = XName, XUnit = XUnit, ChartTitle = ChartTitle,
                ShowLegend = ShowLegend, Hover = Hover, XPad = XPad, YPad = YPad, YInclude = YInclude, XInclude = XInclude,
                YDomain = YDomain, LegendCollapseAbove = LegendCollapseAbove, IsXLog = IsXLog
            };
            big.SetData(_series, _ann);
            big.SetHidden(_hidden);
            var win = new Window
            {
                Title = string.IsNullOrEmpty(ChartTitle) ? YLabel : ChartTitle,
                Width = 1200, Height = 760, Background = Brushes.White,
                Content = new Border { Padding = new Thickness(16), Child = big },
                Owner = Window.GetWindow(this),
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            win.Show();
        }

        private BitmapSource Snapshot()
        {
            int w = Math.Max(1, (int)ActualWidth), h = Math.Max(1, (int)ActualHeight);
            var keep = _mouse; _mouse = null;                 // no hover box in the image
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                OnRender(dc);
            }
            _mouse = keep;
            var bmp = new RenderTargetBitmap(w * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }

        private string SafeName() =>
            string.Concat((string.IsNullOrEmpty(ChartTitle) ? "linbench_chart" : ChartTitle).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        private void SavePng()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "PNG image|*.png", FileName = SafeName() + ".png" };
            if (dlg.ShowDialog() != true) return;
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(Snapshot()));
            using var fs = File.Create(dlg.FileName);
            enc.Save(fs);
        }

        private void CopyImage()
        {
            try { Clipboard.SetImage(Snapshot()); } catch { }
        }

        /// <summary>All data of this chart as CSV rows: chart, series, x, y (used by "All plot data of this view").</summary>
        public void WriteCsv(StringBuilder sb, bool header = true)
        {
            var ci = CultureInfo.InvariantCulture;
            string chart = (string.IsNullOrEmpty(ChartTitle) ? YLabel : ChartTitle).Replace('"', '\'');
            if (header) sb.AppendLine($"chart,series,\"{XLabel}\",y");
            foreach (var s in _series)
            {
                string name = s.Name.Replace('"', '\'');
                for (int i = 0; i < Math.Min(s.X.Count, s.Y.Count); i++)
                    if (Finite(TX(s.X[i])) && Finite(s.Y[i]))
                        sb.Append('"').Append(chart).Append("\",\"").Append(name).Append("\",")
                          .Append(s.X[i].ToString("R", ci)).Append(',')
                          .AppendLine(s.Y[i].ToString("R", ci));
            }
        }

        private void SaveCsv()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "CSV file|*.csv", FileName = SafeName() + ".csv" };
            if (dlg.ShowDialog() != true) return;
            var sb = new StringBuilder();
            WriteCsv(sb);
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        }
    }
}
