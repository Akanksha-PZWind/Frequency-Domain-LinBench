using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LinBench.Wpf.Controls
{
    public class PlotSeries
    {
        public string Title { get; set; } = string.Empty;
        public Brush Stroke { get; set; } = Brushes.SteelBlue;
        public double Thickness { get; set; } = 1.8;
        public bool IsScatter { get; set; }
        public bool IsVisible { get; set; } = true;
        public List<Point> Points { get; set; } = new();
    }

    public partial class WpfPlotView : UserControl
    {
        private readonly List<PlotSeries> _series = new();
        private double _minX, _maxX, _minY, _maxY;
        private double _viewMinX, _viewMaxX, _viewMinY, _viewMaxY;
        private bool _isPanning;
        private Point _panStart;
        private Point _panViewStart;

        public string Title
        {
            get => TxtTitle.Text;
            set => TxtTitle.Text = value;
        }

        public string XAxisLabel { get; set; } = "x";
        public string YAxisLabel { get; set; } = "y";
        public bool IsXLog { get; set; }
        public bool IsYLog { get; set; }

        public WpfPlotView()
        {
            InitializeComponent();
        }

        public void Clear()
        {
            _series.Clear();
            LegendPanel.Children.Clear();
            Redraw();
        }

        public void AddSeries(string title, IEnumerable<double> xs, IEnumerable<double> ys, Brush stroke, double thickness = 1.8, bool isScatter = false)
        {
            var pts = new List<Point>();
            using var ex = xs.GetEnumerator();
            using var ey = ys.GetEnumerator();
            while (ex.MoveNext() && ey.MoveNext())
            {
                var x = ex.Current;
                var y = ey.Current;
                if (!double.IsNaN(x) && !double.IsInfinity(x) && !double.IsNaN(y) && !double.IsInfinity(y))
                {
                    if (IsXLog && x <= 0) continue;
                    if (IsYLog && y <= 0) continue;
                    pts.Add(new Point(x, y));
                }
            }

            var s = new PlotSeries
            {
                Title = title,
                Stroke = stroke,
                Thickness = thickness,
                IsScatter = isScatter,
                Points = pts
            };
            _series.Add(s);

            UpdateLegend();
            ResetZoom();
        }

        public void ResetZoom()
        {
            if (_series.Count == 0 || _series.All(s => s.Points.Count == 0))
            {
                _minX = 0; _maxX = 1; _minY = 0; _maxY = 1;
            }
            else
            {
                var allPts = _series.Where(s => s.IsVisible).SelectMany(s => s.Points).ToList();
                if (allPts.Count == 0) allPts = _series.SelectMany(s => s.Points).ToList();

                _minX = allPts.Min(p => p.X);
                _maxX = allPts.Max(p => p.X);
                _minY = allPts.Min(p => p.Y);
                _maxY = allPts.Max(p => p.Y);

                if (Math.Abs(_maxX - _minX) < 1e-9) { _minX -= 1; _maxX += 1; }
                if (Math.Abs(_maxY - _minY) < 1e-9) { _minY -= 1; _maxY += 1; }

                // Add 5% padding
                var dx = (_maxX - _minX) * 0.05;
                var dy = (_maxY - _minY) * 0.05;
                _minX -= dx; _maxX += dx;
                _minY -= dy; _maxY += dy;
            }

            _viewMinX = _minX;
            _viewMaxX = _maxX;
            _viewMinY = _minY;
            _viewMaxY = _maxY;

            Redraw();
        }

        private void UpdateLegend()
        {
            LegendPanel.Children.Clear();
            foreach (var s in _series)
            {
                var btn = new Button
                {
                    Content = s.Title,
                    Foreground = s.Stroke,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(2, 0, 2, 0),
                    Cursor = Cursors.Hand,
                    FontSize = 11,
                    FontWeight = FontWeights.Medium
                };
                btn.Click += (o, e) =>
                {
                    s.IsVisible = !s.IsVisible;
                    btn.Opacity = s.IsVisible ? 1.0 : 0.4;
                    Redraw();
                };
                LegendPanel.Children.Add(btn);
            }
        }

        private void OnPlotSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

        private void Redraw()
        {
            PlotCanvas.Children.Clear();
            double w = PlotCanvas.ActualWidth;
            double h = PlotCanvas.ActualHeight;
            if (w <= 10 || h <= 10) return;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                // Background & Grid
                var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(226, 232, 240)), 0.8);
                gridPen.Freeze();

                var axisPen = new Pen(new SolidColorBrush(Color.FromRgb(30, 41, 59)), 1.2);
                axisPen.Freeze();

                // Draw 5 grid lines across X and Y
                var textTypeface = new Typeface("Segoe UI, sans-serif");
                var textBrush = new SolidColorBrush(Color.FromRgb(100, 116, 139));
                textBrush.Freeze();

                for (int i = 0; i <= 5; i++)
                {
                    double frac = i / 5.0;
                    double px = frac * w;
                    double py = frac * h;

                    // Vertical grid line
                    dc.DrawLine(gridPen, new Point(px, 0), new Point(px, h));

                    // Horizontal grid line
                    dc.DrawLine(gridPen, new Point(0, py), new Point(w, py));

                    // X coordinate label
                    double vx = _viewMinX + frac * (_viewMaxX - _viewMinX);
                    var textX = new FormattedText(FormatVal(vx), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, textTypeface, 10, textBrush, 1.25);
                    dc.DrawText(textX, new Point(Math.Max(2, px - textX.Width / 2), h - 14));

                    // Y coordinate label
                    double vy = _viewMaxY - frac * (_viewMaxY - _viewMinY);
                    var textY = new FormattedText(FormatVal(vy), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, textTypeface, 10, textBrush, 1.25);
                    dc.DrawText(textY, new Point(4, Math.Max(2, py - 6)));
                }

                // Plot curves
                foreach (var s in _series.Where(s => s.IsVisible))
                {
                    if (s.Points.Count == 0) continue;

                    var pen = new Pen(s.Stroke, s.Thickness);
                    pen.Freeze();

                    if (s.IsScatter)
                    {
                        foreach (var pt in s.Points)
                        {
                            var canvasPt = ToCanvas(pt, w, h);
                            if (canvasPt.X >= 0 && canvasPt.X <= w && canvasPt.Y >= 0 && canvasPt.Y <= h)
                            {
                                dc.DrawEllipse(s.Stroke, pen, canvasPt, 3.5, 3.5);
                            }
                        }
                    }
                    else
                    {
                        var geo = new StreamGeometry();
                        using (var ctx = geo.Open())
                        {
                            bool first = true;
                            foreach (var pt in s.Points)
                            {
                                var cp = ToCanvas(pt, w, h);
                                if (first)
                                {
                                    ctx.BeginFigure(cp, false, false);
                                    first = false;
                                }
                                else
                                {
                                    ctx.LineTo(cp, true, false);
                                }
                            }
                        }
                        geo.Freeze();
                        dc.DrawGeometry(null, pen, geo);
                    }
                }
            }

            var drawingImage = new DrawingImage(visual.Drawing);
            var image = new Image { Source = drawingImage, Stretch = Stretch.None };
            PlotCanvas.Children.Add(image);

            TxtAxisLabel.Text = $"{XAxisLabel} vs {YAxisLabel}";
        }

        private Point ToCanvas(Point dataPt, double w, double h)
        {
            double xFrac = (_viewMaxX != _viewMinX) ? (dataPt.X - _viewMinX) / (_viewMaxX - _viewMinX) : 0.5;
            double yFrac = (_viewMaxY != _viewMinY) ? (_viewMaxY - dataPt.Y) / (_viewMaxY - _viewMinY) : 0.5;
            return new Point(xFrac * w, yFrac * h);
        }

        private Point ToData(Point canvasPt, double w, double h)
        {
            double x = _viewMinX + (canvasPt.X / w) * (_viewMaxX - _viewMinX);
            double y = _viewMaxY - (canvasPt.Y / h) * (_viewMaxY - _viewMinY);
            return new Point(x, y);
        }

        private static string FormatVal(double v)
        {
            if (Math.Abs(v) < 1e-4 && Math.Abs(v) > 0) return v.ToString("0.0e0", CultureInfo.InvariantCulture);
            if (Math.Abs(v) >= 1e4) return v.ToString("0.0e0", CultureInfo.InvariantCulture);
            return Math.Abs(v) >= 10 ? v.ToString("0.#", CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        #region Interaction: Pan, Zoom, Crosshair
        private void OnPlotMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ResetZoom();
                return;
            }

            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _isPanning = true;
                _panStart = e.GetPosition(PlotCanvas);
                _panViewStart = new Point(_viewMinX, _viewMinY);
                PlotCanvas.CaptureMouse();
            }
        }

        private void OnPlotMouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(PlotCanvas);
            double w = PlotCanvas.ActualWidth;
            double h = PlotCanvas.ActualHeight;

            if (_isPanning && w > 0 && h > 0)
            {
                var dxPixels = pos.X - _panStart.X;
                var dyPixels = pos.Y - _panStart.Y;

                var dxData = dxPixels / w * (_viewMaxX - _viewMinX);
                var dyData = dyPixels / h * (_viewMaxY - _viewMinY);

                _viewMinX = _panViewStart.X - dxData;
                _viewMaxX = _viewMinX + (_maxX - _minX);
                _viewMinY = _panViewStart.Y + dyData;
                _viewMaxY = _viewMinY + (_maxY - _minY);

                Redraw();
            }
            else if (w > 0 && h > 0)
            {
                // Crosshair readout
                var dataPt = ToData(pos, w, h);
                TxtCrosshair.Text = $"x: {FormatVal(dataPt.X)}   y: {FormatVal(dataPt.Y)}";
                CrosshairBadge.Visibility = Visibility.Visible;
            }
        }

        private void OnPlotMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                PlotCanvas.ReleaseMouseCapture();
            }
        }

        private void OnPlotMouseWheel(object sender, MouseWheelEventArgs e)
        {
            double factor = e.Delta > 0 ? 0.85 : 1.15;
            var pos = e.GetPosition(PlotCanvas);
            double w = PlotCanvas.ActualWidth;
            double h = PlotCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            var center = ToData(pos, w, h);

            double newSpanX = (_viewMaxX - _viewMinX) * factor;
            double newSpanY = (_viewMaxY - _viewMinY) * factor;

            double fracX = pos.X / w;
            double fracY = (h - pos.Y) / h;

            _viewMinX = center.X - fracX * newSpanX;
            _viewMaxX = _viewMinX + newSpanX;
            _viewMinY = center.Y - fracY * newSpanY;
            _viewMaxY = _viewMinY + newSpanY;

            Redraw();
        }

        private void OnResetZoomClick(object sender, RoutedEventArgs e) => ResetZoom();

        private void OnExportPngClick(object sender, RoutedEventArgs e)
        {
            try
            {
                double w = ActualWidth > 0 ? ActualWidth : 800;
                double h = ActualHeight > 0 ? ActualHeight : 500;
                var rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(this);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));

                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), $"{Title.Replace(" ", "_")}.png");
                using var fs = File.OpenWrite(path);
                encoder.Save(fs);
                MessageBox.Show($"Plot exported to:\n{path}", "Export PNG", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnExportCsvClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var s in _series)
                {
                    sb.AppendLine($"# Series: {s.Title}");
                    sb.AppendLine($"{XAxisLabel},{YAxisLabel}");
                    foreach (var p in s.Points)
                    {
                        sb.AppendLine($"{p.X},{p.Y}");
                    }
                    sb.AppendLine();
                }

                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"{Title.Replace(" ", "_")}.csv");
                File.WriteAllText(path, sb.ToString());
                MessageBox.Show($"Data exported to:\n{path}", "Export CSV", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        #endregion
    }
}
