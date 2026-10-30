using LinBench.Core.Models;
using LinBench.Core.ViewModels;
using LinBench.Wpf.Controls;
using System.Collections.Specialized;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using static LinBench.Wpf.Controls.ChartFormat;

namespace LinBench.Wpf.Views.Panels
{
    /// <summary>Row of the Mode list.</summary>
    public sealed class ModeRow
    {
        public ModeItem Mode { get; set; } = null!;
        public string Fn { get; set; } = "";
        public string Fd { get; set; } = "";
        public string Zeta { get; set; } = "";
        public bool Unstable { get; set; }
        public string Label { get; set; } = "";
        public string Symmetry { get; set; } = "";
        public string Share { get; set; } = "";
    }

    /// <summary>One participation bar of the selected mode.</summary>
    public sealed class ParticipationBar
    {
        public string Name { get; set; } = "";
        public double BarWidth { get; set; }
        public string Percent { get; set; } = "";
    }

    /// <summary>
    /// Modes at the selected wind speed, like the original LinBench (app.js 'ol:modes'):
    /// s-plane (colour = component, marker = symmetry, dashed rays ζ = 0.05 / 0.2 / 0.5),
    /// Mode list with the participation of the selected mode. Sidebar: VIEW SETTINGS (aero/hydro states,
    /// max frequency) and RESULTS (modes shown, least damped, unstable).
    /// </summary>
    public partial class ModesPanel : UserControl, IViewSidebar
    {
        private ModesPanelViewModel? _vm;
        private MainViewModel? _main;
        private bool _redrawQueued;

        private bool _showHydro;
        private double _fmax = 2.0;
        private readonly StackPanel _settings;
        private TextBox _tbFmax = null!;
        public FrameworkElement? ViewSettings => _settings;
        public FrameworkElement? ResultsExtra => null;

        private static readonly Dictionary<string, MarkerShape> SymMark = new()
        {
            ["collective"] = MarkerShape.Circle,
            ["BW"] = MarkerShape.TriDown,
            ["FW"] = MarkerShape.TriUp,
            ["cyclic"] = MarkerShape.Diamond,
            [""] = MarkerShape.Circle
        };
        private static readonly Dictionary<string, string> SymName = new()
        {
            ["collective"] = "collective (symmetric)",
            ["BW"] = "backward whirl",
            ["FW"] = "forward whirl",
            ["cyclic"] = "cyclic (asymmetric)",
            [""] = ""
        };

        private static Color FamColor(string? family) => (family ?? "other") switch
        {
            "platform" => LbChart.Palette[0],
            "tower" => LbChart.Palette[1],
            "drivetrain" => LbChart.Palette[2],
            "blade flap" => LbChart.Palette[6],
            "blade edge" => LbChart.Palette[4],
            "blade torsion" => LbChart.Palette[3],
            "nacelle" => LbChart.Palette[5],
            "controller" => LbChart.Palette[3],
            "hydro/aero states" => LbChart.MutedColor,
            _ => LbChart.Palette[7]
        };

        public ModesPanel()
        {
            InitializeComponent();
            _settings = BuildViewSettings();
            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
        }

        // ------------------------------------------------------------------ wiring

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_main == null && Window.GetWindow(this)?.DataContext is MainViewModel main)
            {
                _main = main;
                _main.Results.PropertyChanged += (s, a) =>
                {
                    if (a.PropertyName == nameof(ResultsViewModel.CurrentOp)) QueueRedraw();
                };
            }
            QueueRedraw();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_vm != null) _vm.Modes.CollectionChanged -= OnModesChanged;
            _vm = e.NewValue as ModesPanelViewModel;
            if (_vm != null) _vm.Modes.CollectionChanged += OnModesChanged;
            QueueRedraw();
        }

        private void OnModesChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRedraw();

        private void QueueRedraw()
        {
            if (_redrawQueued) return;
            _redrawQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _redrawQueued = false;
                try { Redraw(); }
                catch (Exception ex) { TxtEigSub.Text = "Could not draw the modes: " + ex.Message; }
            }));
        }

        private void OnGridPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var inner = FindChild<ScrollViewer>((DependencyObject)sender);
            bool canScroll = inner != null &&
                             (e.Delta < 0 ? inner.VerticalOffset < inner.ScrollableHeight : inner.VerticalOffset > 0);
            if (canScroll) return;
            e.Handled = true;
            var outer = FindParent<ScrollViewer>((DependencyObject)sender);
            outer?.ScrollToVerticalOffset(outer.VerticalOffset - e.Delta);
        }

        private static T? FindChild<T>(DependencyObject d) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is T t) return t;
                var r = FindChild<T>(c);
                if (r != null) return r;
            }
            return null;
        }

        private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            var p = VisualTreeHelper.GetParent(d);
            while (p != null && p is not T) p = VisualTreeHelper.GetParent(p);
            return p as T;
        }

        // ------------------------------------------------------------------ sidebar ▾ VIEW SETTINGS

        private StackPanel BuildViewSettings()
        {
            var sp = new StackPanel();
            var cb = new CheckBox
            {
                Content = new TextBlock { Text = "show hydrodynamic / aerodynamic state modes", TextWrapping = TextWrapping.Wrap, FontSize = 13 },
                Margin = new Thickness(2, 0, 0, 12)
            };
            cb.SetResourceReference(StyleProperty, "ShellCheckBox");
            cb.Checked += (s, e) => { _showHydro = true; QueueRedraw(); };
            cb.Unchecked += (s, e) => { _showHydro = false; QueueRedraw(); };
            sp.Children.Add(cb);

            sp.Children.Add(new TextBlock { Text = "Max frequency [Hz]", FontSize = 13, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 0, 6) });
            _tbFmax = new TextBox { Text = "2", Tag = "2", FontFamily = new FontFamily("Segoe UI"), FontSize = 13 };
            _tbFmax.SetResourceReference(StyleProperty, "ShellInput");
            _tbFmax.KeyDown += (s, e) => { if (e.Key == Key.Enter) ApplyFmax(); };
            _tbFmax.LostFocus += (s, e) => ApplyFmax();
            sp.Children.Add(_tbFmax);
            return sp;
        }

        private void ApplyFmax()
        {
            var t = _tbFmax.Text.Trim().Replace(',', '.');
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
            {
                if (Math.Abs(v - _fmax) < 1e-12) return;
                _fmax = v;
                QueueRedraw();
            }
            else
            {
                _tbFmax.Text = _fmax.ToString(CultureInfo.InvariantCulture);
            }
        }

        // ------------------------------------------------------------------ drawing

        private void Redraw()
        {
            var all = _vm?.Modes.ToList() ?? new List<ModeItem>();
            var op = _main?.Results.CurrentOp;
            TxtEigSub.Text = op == null ? "s-plane · upper half-plane" : $"s-plane at {Sig(op.WindSpeed, 3)} m/s · upper half-plane";
            TxtBusy.Visibility = all.Count == 0 && op != null ? Visibility.Visible : Visibility.Collapsed;

            var modes = all.Where(m => (_showHydro || m.Family != "hydro/aero states") && (m.FreqHz ?? double.MaxValue) <= _fmax)
                           .OrderBy(m => m.FreqHz ?? 0).ToList();

            // ---- s-plane: one scatter series per component, marker = symmetry
            var series = new List<ChartSeries>();
            double maxIm = Math.Max(0.1, modes.Select(m => m.Im ?? 0).DefaultIfEmpty(0).Max());
            foreach (var z in new[] { 0.05, 0.2, 0.5 })   // constant-damping rays
            {
                series.Add(new ChartSeries
                {
                    Name = $"ζ {z}",
                    X = new[] { 0.0, -z * maxIm / Math.Sqrt(1 - z * z) },
                    Y = new[] { 0.0, maxIm },
                    Color = Color.FromRgb(205, 210, 218),
                    LineWidth = 1,
                    Dash = LineDash.Dash,
                    MarkerSize = 0,
                    InLegend = false,
                    NoHover = true,
                    NoExtent = true
                });
            }
            foreach (var fam in modes.Select(m => m.Family).Distinct())
            {
                var ms = modes.Where(m => m.Family == fam).ToList();
                series.Add(new ChartSeries
                {
                    Name = string.IsNullOrEmpty(fam) ? "other" : fam,
                    X = ms.Select(m => NaN(m.Re)).ToArray(),
                    Y = ms.Select(m => NaN(m.Im)).ToArray(),
                    Color = FamColor(fam),
                    LineWidth = 0,
                    MarkerSize = 4.5,
                    Markers = ms.Select(m => SymMark.TryGetValue(m.Sym ?? "", out var mk) ? mk : MarkerShape.Circle).ToArray(),
                    Tips = ms.Select(m =>
                        $"{m.Label}\nf_n {Sig(m.FreqHz, 4)} Hz · ζ {Sig(m.DampingRatio * 100, 3)}%\n" +
                        $"λ = {Sig(m.Re, 4)} {((m.Im ?? 0) >= 0 ? "+" : "−")} {Sig(Math.Abs(m.Im ?? 0), 4)}j").ToArray()
                });
            }
            ChartEig.XLabel = "Real part [1/s]";
            ChartEig.YLabel = "Imaginary part [rad/s]";
            ChartEig.XName = "Re";
            ChartEig.XUnit = "1/s";
            ChartEig.Hover = ChartHover.Nearest;
            ChartEig.XInclude = new[] { 0.0 };
            ChartEig.YInclude = new[] { 0.0 };
            ChartEig.SetData(series, new[]
            {
                new ChartAnnotation { Kind = AnnotationKind.VLine, X = 0, Color = Color.FromRgb(150, 158, 170), Width = 1 }
            });

            // ---- mode list
            var rows = modes.Select(m => new ModeRow
            {
                Mode = m,
                Fn = Sig(m.FreqHz, 4),
                Fd = Sig(m.DampedFreqHz, 4),
                Zeta = Sig(m.DampingRatio * 100, 3),
                Unstable = m.DampingRatio < 0,
                Label = m.Label,
                Symmetry = SymName.TryGetValue(m.Sym ?? "", out var nm) && nm.Length > 0 ? nm : "–",
                Share = m.Share == null ? "–" : Sig(m.Share * 100, 3) + "%"
            }).ToList();
            ModeGrid.ItemsSource = rows;
            TxtNoModes.Visibility = rows.Count == 0 && all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (rows.Count > 0) ModeGrid.SelectedIndex = 0;
            else ShowParticipation(null);

            // ---- sidebar ▾ RESULTS
            var items = new List<SidebarMetric> { new("Modes shown", modes.Count.ToString(CultureInfo.InvariantCulture)) };
            var osc = modes.Where(m => m.Oscillatory && m.DampingRatio.HasValue).ToList();
            if (osc.Count > 0)
            {
                var lz = osc.OrderBy(m => m.DampingRatio!.Value).First();
                items.Add(new SidebarMetric("Least damped", Sig(lz.DampingRatio * 100, 3) + "%", lz.Label, lz.DampingRatio < 0 ? "bad" : ""));
            }
            int unstable = all.Count(m => (m.Re ?? 0) > 1e-9);
            items.Add(new SidebarMetric("Unstable", unstable.ToString(CultureInfo.InvariantCulture), "", unstable > 0 ? "bad" : "good"));
            _main?.Results.SetPanelSidebar(ResultsViewType.Modes, new List<SidebarSection> { new() { Title = "Eigenvalues", Items = items } });
        }

        private void OnModeSelected(object sender, SelectionChangedEventArgs e) =>
            ShowParticipation((ModeGrid.SelectedItem as ModeRow)?.Mode);

        /// <summary>Bars of the dominant degrees of freedom (top[] = [label, share]).</summary>
        private void ShowParticipation(ModeItem? m)
        {
            if (m == null)
            {
                TxtPartTitle.Text = string.Empty;
                PartList.ItemsSource = null;
                return;
            }
            TxtPartTitle.Text = m.Label;
            var bars = new List<ParticipationBar>();
            foreach (var pair in m.Top)
            {
                if (pair.Count < 2) continue;
                string name = pair[0].ValueKind == JsonValueKind.String ? pair[0].GetString() ?? "" : pair[0].ToString();
                double v = pair[1].ValueKind == JsonValueKind.Number ? pair[1].GetDouble() : 0;
                bars.Add(new ParticipationBar { Name = name, BarWidth = Math.Max(1, v * 260), Percent = Sig(v * 100, 3) + "%" });
            }
            PartList.ItemsSource = bars;
        }
    }
}
