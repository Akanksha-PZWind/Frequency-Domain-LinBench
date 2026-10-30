using LinBench.Core.Models;
using LinBench.Core.ViewModels;
using LinBench.Wpf.Controls;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using static LinBench.Wpf.Controls.ChartFormat;

namespace LinBench.Wpf.Views.Panels
{
    /// <summary>Row of the "Tracked modes" table.</summary>
    public sealed class TrackedModeRow
    {
        public string Mode { get; set; } = "";
        public string Component { get; set; } = "";
        public string Symmetry { get; set; } = "";
        public string FMin { get; set; } = "";
        public string FMax { get; set; } = "";
        public string ZetaMin { get; set; } = "";
        public bool Unstable { get; set; }
        public string At { get; set; } = "";
        public string Points { get; set; } = "";
    }

    /// <summary>
    /// Campbell page, built like the original LinBench (app.js 'ol:campbell'):
    /// • colour = one colour per mode (≤ 8 modes) or per component, line dash + marker = symmetry
    ///   (collective solid ●, backward whirl dashed ▼, forward whirl dotted ▲, cyclic dash-dot ◆)
    /// • grey 1P / 3P / 6P / 9P lines labelled at the right edge, y axis 0 … min(1.08·fmax, 4) Hz
    /// • closed loop: the open-loop modes stay as thin grey lines
    /// • sidebar: VIEW SETTINGS (Loop, Frequency, Max frequency, aero/hydro states) and RESULTS
    ///   (tracked modes, lowest mode, closest approach to 1P/3P/6P – red under 5 %, symmetry legend)
    /// </summary> 

    public partial class CampbellPanel : UserControl, IViewSidebar
    {
        private CampbellPanelViewModel? _vm;
        private MainViewModel? _main;
        private bool _redrawQueued;
        private bool _syncing;

        // view settings (as in the original: loop / natural-damped / max frequency / aero-hydro states)
        private bool _damped;
        private double? _fmax;
        private RadioButton _rbOpen = null!, _rbClosed = null!, _rbNatural = null!, _rbDamped = null!;
        private TextBox _tbFmax = null!;
        private CheckBox _cbAllStates = null!;
        private readonly StackPanel _settings;
        private readonly StackPanel _legendHost = new() { Margin = new Thickness(0, 2, 0, 4) };

        public FrameworkElement? ViewSettings => _settings;
        public FrameworkElement? ResultsExtra => _legendHost;

        private static readonly Dictionary<string, LineDash> SymDash = new()
        {
            ["collective"] = LineDash.Solid,
            ["BW"] = LineDash.Dash,
            ["FW"] = LineDash.Dot,
            ["cyclic"] = LineDash.DashDot,
            [""] = LineDash.Solid
        };
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
        private static readonly MarkerShape[] PlainMarkers =
            { MarkerShape.Circle, MarkerShape.Square, MarkerShape.Diamond, MarkerShape.TriUp, MarkerShape.TriDown };

        /// <summary>Colours per component when there are more modes than palette colours (FAMCOL in app.js).</summary>
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

        public CampbellPanel()
        {
            InitializeComponent();
            _settings = BuildViewSettings();
            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
            ChartFreq.HiddenChanged += names => ChartDamp.SetHidden(names);   // hiding a mode hides it in both charts
        }

        // ------------------------------------------------------------------ wiring

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_main == null && Window.GetWindow(this)?.DataContext is MainViewModel main)
            {
                _main = main;
                _main.Results.PropertyChanged += (s, a) =>
                {
                    if (a.PropertyName is nameof(ResultsViewModel.CurrentOp) or nameof(ResultsViewModel.HasClosedLoop))
                        QueueRedraw();
                };
            }
            QueueRedraw();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_vm != null)
            {
                _vm.Tracks.CollectionChanged -= OnTracksChanged;
                _vm.PropertyChanged -= OnVmPropertyChanged;
            }
            _vm = e.NewValue as CampbellPanelViewModel;
            if (_vm != null)
            {
                _vm.Tracks.CollectionChanged += OnTracksChanged;
                _vm.PropertyChanged += OnVmPropertyChanged;
            }
            QueueRedraw();
        }

        private void OnTracksChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRedraw();

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(CampbellPanelViewModel.IsClosedLoop) or nameof(CampbellPanelViewModel.IsRefreshing)
                or nameof(CampbellPanelViewModel.IncludeAllTracks))
                QueueRedraw();
        }

        private void QueueRedraw()
        {
            if (_redrawQueued) return;
            _redrawQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _redrawQueued = false;
                try { Redraw(); }
                catch (Exception ex) { TxtFreqSub.Text = "Could not draw the Campbell diagram: " + ex.Message; }
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

        // EDIT 1: LoadingRow Handler for the DataGrid Sr No Column
        private void ModesGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            e.Row.Header = (e.Row.GetIndex() + 1).ToString();
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

            sp.Children.Add(SettingLabel("Loop"));
            (_rbOpen, _rbClosed) = TwoSegments(sp, "open", "closed", "CampbellLoop", left =>
            {
                if (_vm != null && !_syncing) _vm.IsClosedLoop = !left;
            });
            _rbClosed.ToolTip = "Needs a DISCON.IN and a closed-loop analysis (Closed loop or Both)";
            ToolTipService.SetShowOnDisabled(_rbClosed, true);

            sp.Children.Add(SettingLabel("Frequency"));
            (_rbNatural, _rbDamped) = TwoSegments(sp, "natural", "damped", "CampbellFreq", left =>
            {
                if (_syncing) return;
                _damped = !left;
                QueueRedraw();
            });

            sp.Children.Add(SettingLabel("Max frequency [Hz]"));
            _tbFmax = new TextBox { Margin = new Thickness(0, 0, 0, 12), Tag = "auto", FontFamily = new FontFamily("Segoe UI"), FontSize = 13 };
            _tbFmax.SetResourceReference(StyleProperty, "ShellInput");
            _tbFmax.KeyDown += (s, e) => { if (e.Key == Key.Enter) ApplyFmax(); };
            _tbFmax.LostFocus += (s, e) => ApplyFmax();
            sp.Children.Add(_tbFmax);

            _cbAllStates = new CheckBox
            {
                Content = new TextBlock { Text = "include hydrodynamic / aerodynamic state modes", TextWrapping = TextWrapping.Wrap, FontSize = 13 },
                Margin = new Thickness(2, 0, 0, 4)
            };
            _cbAllStates.SetResourceReference(StyleProperty, "ShellCheckBox");
            _cbAllStates.Checked += (s, e) => { if (_vm != null && !_syncing) _vm.IncludeAllTracks = true; };
            _cbAllStates.Unchecked += (s, e) => { if (_vm != null && !_syncing) _vm.IncludeAllTracks = false; };
            sp.Children.Add(_cbAllStates);
            return sp;
        }

        private static TextBlock SettingLabel(string text) =>
            new() { Text = text, FontSize = 13, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 0, 6) };

        private static (RadioButton, RadioButton) TwoSegments(Panel host, string a, string b, string group, Action<bool> onChange)
        {
            var border = new Border { Margin = new Thickness(0, 0, 0, 12) };
            border.SetResourceReference(StyleProperty, "ShellSegmentHost");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var ra = new RadioButton { Content = a, GroupName = group, IsChecked = true };
            var rb = new RadioButton { Content = b, GroupName = group, BorderThickness = new Thickness(0) };
            ra.SetResourceReference(StyleProperty, "ShellMiniSegment");
            rb.SetResourceReference(StyleProperty, "ShellMiniSegment");
            rb.BorderThickness = new Thickness(0);
            ra.Checked += (s, e) => onChange(true);
            rb.Checked += (s, e) => onChange(false);
            row.Children.Add(ra);
            row.Children.Add(rb);
            border.Child = row;
            host.Children.Add(border);
            return (ra, rb);
        }

        private void ApplyFmax()
        {
            var t = _tbFmax.Text.Trim().Replace(',', '.');
            double? v = null;
            if (t.Length > 0 && !t.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
                v = d;
            if (v == null && t.Length > 0 && !t.Equals("auto", StringComparison.OrdinalIgnoreCase)) _tbFmax.Text = string.Empty;
            if (v == _fmax) return;
            _fmax = v;
            QueueRedraw();
        }

        private void SyncSettings()
        {
            _syncing = true;
            try
            {
                bool hasCl = _main?.Results.HasClosedLoop == true;
                _rbClosed.IsEnabled = hasCl;
                if (_vm != null)
                {
                    _rbOpen.IsChecked = !_vm.IsClosedLoop;
                    _rbClosed.IsChecked = _vm.IsClosedLoop;
                    _cbAllStates.IsChecked = _vm.IncludeAllTracks;
                    _cbAllStates.IsEnabled = !_vm.IsClosedLoop;   // as in the original: aero/hydro option only open loop
                }
                _rbNatural.IsChecked = !_damped;
                _rbDamped.IsChecked = _damped;
            }
            finally
            {
                _syncing = false;
            }
        }

        // ------------------------------------------------------------------ drawing

        private sealed class TrackSeries
        {
            public CampbellTrack Track = null!;
            public ChartSeries Freq = null!;
        }

        private void Redraw()
        {
            SyncSettings();
            TxtBusy.Visibility = _vm?.IsRefreshing == true ? Visibility.Visible : Visibility.Collapsed;

            var data = _vm?.Data;
            var tracks = data?.Tracks.Where(t => t.Points.Count > 0).ToList() ?? new List<CampbellTrack>();
            bool any = tracks.Count > 0;
            TxtEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            CardFreq.Visibility = CardDamp.Visibility = CardTable.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if (!any)
            {
                ChartFreq.Clear();
                ChartDamp.Clear();
                ModesGrid.ItemsSource = null;
                _legendHost.Children.Clear();
                Publish(new List<SidebarSection>());
                return;
            }

            bool closed = _vm!.IsClosedLoop;
            Func<CampbellPoint, double> F = _damped ? p => NaN(p.FdHz) : p => NaN(p.FnHz);
            double fmaxData = tracks.SelectMany(t => t.Points).Select(F).Where(v => !double.IsNaN(v)).DefaultIfEmpty(1).Max();
            double fmax = _fmax ?? Math.Min(fmaxData * 1.08, 4);

            TxtFreqTitle.Text = closed ? "Campbell diagram, closed loop" : "Campbell diagram";
            TxtFreqSub.Text = $"{(_damped ? "damped" : "natural")} frequency of each tracked mode · line/marker style = symmetry" +
                              (closed ? " · grey: open loop · includes controller modes" : "");

            // ---- one series per tracked mode
            bool byTrack = tracks.Count <= 8;
            var famCount = new Dictionary<string, int>();
            var list = new List<TrackSeries>();
            for (int k = 0; k < tracks.Count; k++)
            {
                var t = tracks[k];
                var sym = t.Type ?? "";
                var fam = t.Family ?? "other";
                famCount[fam + sym] = famCount.TryGetValue(fam + sym, out var c) ? c + 1 : 1;
                int n = famCount[fam + sym];
                var pts = t.Points.OrderBy(p => p.Wind).ToList();
                if (!pts.Any(p => F(p) <= fmax * 1.05)) continue;

                list.Add(new TrackSeries
                {
                    Track = t,
                    Freq = new ChartSeries
                    {
                        Name = t.Label,
                        X = pts.Select(p => p.Wind).ToArray(),
                        Y = pts.Select(F).ToArray(),
                        Color = byTrack ? LbChart.Palette[k % LbChart.Palette.Length] : FamColor(t.Family),
                        Dash = SymDash.TryGetValue(sym, out var dsh) ? dsh : LineDash.Solid,
                        Marker = sym.Length > 0 && SymMark.TryGetValue(sym, out var mk) ? mk : PlainMarkers[(n - 1) % PlainMarkers.Length],
                        MarkerSize = 3.4,
                        LineWidth = 1.6,
                        Unit = "Hz",
                        Tips = pts.Select(p =>
                            $"{t.Label}\nWind {Sig(p.Wind, 3)} m/s\n{(_damped ? "f_d" : "f_n")} {Sig(F(p), 4)} Hz  ·  ζ {Sig(p.Zeta * 100, 3)} %").ToArray()
                    }
                });
            }

            // ---- rotor harmonics 1P / 3P / 6P / 9P (grey, long dashes) with labels at the right edge
            var rotor = (data!.Rotor ?? new List<RotorHarmonic>()).Where(r => r.F1p.HasValue).OrderBy(r => r.Wind).ToList();
            var winds = rotor.Select(r => r.Wind).ToArray();
            var harm = new List<ChartSeries>();
            var ann = new List<ChartAnnotation>();
            foreach (var n in new[] { 1, 3, 6, 9 })
            {
                harm.Add(new ChartSeries
                {
                    Name = n + "P",
                    X = winds,
                    Y = rotor.Select(r => r.F1p!.Value * n).ToArray(),
                    Color = LbChart.BaselineColor,
                    LineWidth = 1.4,
                    Dash = LineDash.Long,
                    MarkerSize = 0,
                    InLegend = false,
                    NoHover = true,
                    Unit = "Hz"
                });
                if (rotor.Count > 0)
                {
                    double y = rotor[^1].F1p!.Value * n;
                    if (y < fmax)
                        ann.Add(new ChartAnnotation { Kind = AnnotationKind.PointLabel, X = winds[^1], Y = y, Label = n + "P", Color = LbChart.MutedColor, AnchorEnd = true });
                }
            }
            var selWind = _main?.Results.CurrentOp?.WindSpeed;
            if (selWind.HasValue)
                ann.Add(new ChartAnnotation { Kind = AnnotationKind.VLine, X = selWind.Value, Color = LbChart.Accent, Width = 1 });

            // ---- closed loop: open-loop modes as thin grey lines
            var ghostSrc = closed && _vm.OpenLoop != null
                ? _vm.OpenLoop.Tracks.Where(t => t.Points.Any(p => F(p) <= fmax * 1.05)).ToList()
                : new List<CampbellTrack>();
            ChartSeries Ghost(CampbellTrack t, Func<CampbellPoint, double> y)
            {
                var pts = t.Points.OrderBy(p => p.Wind).ToList();
                return new ChartSeries
                {
                    Name = "open loop",
                    X = pts.Select(p => p.Wind).ToArray(),
                    Y = pts.Select(y).ToArray(),
                    Color = LbChart.MutedColor,
                    LineWidth = 1,
                    Opacity = 0.7,
                    MarkerSize = 0,
                    InLegend = false,
                    NoHover = true
                };
            }

            // EDIT 2: Generate combined multi-line hover summary for ChartFreq (Matching reference image)
            //var multiPointTooltips = tracks.SelectMany(t => t.Points.Select(p => new { Track = t, Point = p }))
            //    .GroupBy(x => x.Point.Wind)
            //    .ToDictionary(
            //        g => g.Key,
            //        g => $"Wind {Sig(g.Key, 3)} m/s\n" + string.Join("\n", g.Select(x =>
            //            $"■ {x.Track.Label}   {Sig(F(x.Point), 4)} Hz   ζ {Sig(x.Point.Zeta * 100, 3)}%"))
            //    );


            //var multiPointTooltips = tracks.SelectMany(t => t.Points.Select(p => new { Track = t, Point = p }))
            //        .GroupBy(x => x.Point.Wind)
            //        .ToDictionary(
            //                g => g.Key,
            //                g => $"Wind {Sig(g.Key, 3)} m/s\n" + string.Join("\n", g.Select(x =>
            //                {
            //                    // Find track index to pick matching palette/family color
            //                    int k = tracks.IndexOf(x.Track);
            //                    Color c = byTrack ? LbChart.Palette[k % LbChart.Palette.Length] : FamColor(x.Track.Family);
            //                    string hexColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";

            //                    // Multi-line tooltip entry for this mode
            //                    return $"■ {x.Track.Label}\t{Sig(F(x.Point), 4)} Hz   ζ {Sig(x.Point.Zeta * 100, 3)}%";
            //                }))
            //        );


            // EDIT 2: Generate combined multi-line hover summary using native WPF Brushes
            var multiPointTooltips = tracks.SelectMany(t => t.Points.Select(p => new { Track = t, Point = p }))
                .GroupBy(x => x.Point.Wind)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var lines = new List<string>();

                        // 1. Add Rotor Harmonics (1P, 3P, 6P, 9P) at top of tooltip
                        var f1pObj = rotor.FirstOrDefault(r => Math.Abs(r.Wind - g.Key) < 1e-9);
                        if (f1pObj?.F1p is double f1p && f1p > 0)
                        {
                            foreach (var n in new[] { 1, 3, 6, 9 })
                            {
                                double hFreq = f1p * n;
                                if (hFreq <= fmax)
                                    lines.Add($"■ {n}P\t{Sig(hFreq, 4)} Hz");
                            }
                        }

                        // 2. Add Tracked Modes
                        foreach (var x in g)
                        {
                            double freqVal = F(x.Point);
                            if (!double.IsNaN(freqVal) && freqVal <= fmax * 1.05)
                            {
                                lines.Add($"■ {x.Track.Label}\t{Sig(freqVal, 4)} Hz   ζ {Sig(x.Point.Zeta * 100, 3)}%");
                            }
                        }

                        return $"Wind {Sig(g.Key, 3)} m/s\n" + string.Join("\n", lines);
                    }
                );

            foreach (var s in list)
            {
                var pts = s.Track.Points.OrderBy(p => p.Wind).ToList();
                s.Freq.Tips = pts.Select(p => multiPointTooltips.TryGetValue(p.Wind, out var summaryText)
                    ? summaryText
                    : $"{s.Track.Label}\nWind {Sig(p.Wind, 3)} m/s\n{(_damped ? "f_d" : "f_n")} {Sig(F(p), 4)} Hz  ·  ζ {Sig(p.Zeta * 100, 3)} %").ToArray();
            }

            ChartFreq.XLabel = "Wind speed [m/s]";
            ChartFreq.YLabel = "Frequency [Hz]";
            ChartFreq.XName = "Wind";
            ChartFreq.XUnit = "m/s";
            ChartFreq.YDomain = new[] { 0.0, fmax };
            ChartFreq.Hover = ChartHover.Nearest;
            ChartFreq.SetData(harm.Concat(ghostSrc.Select(t => Ghost(t, F))).Concat(list.Select(s => s.Freq)), ann);

            // ---- damping chart (same styles; hiding a mode in the Campbell legend hides it here too)
            var damp = list.Select(s =>
            {
                var pts = s.Track.Points.OrderBy(p => p.Wind).ToList();
                return new ChartSeries
                {
                    Name = s.Freq.Name,
                    X = s.Freq.X,
                    Y = pts.Select(p => NaN(p.Zeta) * 100).ToArray(),
                    Color = s.Freq.Color,
                    Dash = s.Freq.Dash,
                    Marker = s.Freq.Marker,
                    MarkerSize = 3.4,
                    LineWidth = 1.6,
                    Unit = "%",
                    Tips = pts.Select(p => $"{s.Track.Label}\nWind {Sig(p.Wind, 3)} m/s\nζ {Sig(p.Zeta * 100, 3)} %  ·  f {Sig(F(p), 4)} Hz").ToArray()
                };
            });
            ChartDamp.XLabel = "Wind speed [m/s]";
            ChartDamp.YLabel = "Damping ratio [%]";
            ChartDamp.XName = "Wind";
            ChartDamp.XUnit = "m/s";
            ChartDamp.ShowLegend = false;
            ChartDamp.Hover = ChartHover.Nearest;
            ChartDamp.SetData(ghostSrc.Select(t => Ghost(t, p => NaN(p.Zeta) * 100)).Concat(damp), new[]
            {
                new ChartAnnotation { Kind = AnnotationKind.HLine, Y = 0, Color = LbChart.CritColor, Width = 1 }
            });

            // ---- tracked modes table
            ModesGrid.ItemsSource = tracks.Select(t =>
            {
                var fs = t.Points.Select(F).Where(v => !double.IsNaN(v)).ToList();
                var withZ = t.Points.Where(p => p.Zeta.HasValue).ToList();
                var pz = withZ.Count > 0 ? withZ.OrderBy(p => p.Zeta!.Value).First() : null;
                var sym = t.Type ?? "";
                return new TrackedModeRow
                {
                    Mode = t.Label,
                    Component = t.Family ?? "",
                    Symmetry = SymName.TryGetValue(sym, out var nm) && nm.Length > 0 ? nm : "–",
                    FMin = fs.Count > 0 ? Sig(fs.Min(), 4) : "–",
                    FMax = fs.Count > 0 ? Sig(fs.Max(), 4) : "–",
                    ZetaMin = pz == null ? "–" : Sig(pz.Zeta * 100, 3),
                    Unstable = pz?.Zeta < 0,
                    At = pz == null ? "–" : Sig(pz.Wind, 3),
                    Points = t.Points.Count.ToString(CultureInfo.InvariantCulture)
                };
            }).ToList();

            // ---- sidebar ▾ RESULTS: counts and closest approach to 1P / 3P / 6P (red when < 5 %)
            var items = new List<SidebarMetric>
            {
                new("Tracked modes", tracks.Count.ToString(CultureInfo.InvariantCulture)),
                new("Lowest mode", Sig(tracks.Where(t => t.MeanFrequencyHz.HasValue).Select(t => t.MeanFrequencyHz!.Value).DefaultIfEmpty(double.NaN).Min(), 3), "Hz"),
            };
            foreach (var n in new[] { 1, 3, 6 })
            {
                double best = double.MaxValue; CampbellTrack? bt = null; CampbellPoint? bp = null;
                foreach (var t in tracks)
                    foreach (var p in t.Points)
                    {
                        var f1 = rotor.FirstOrDefault(r => Math.Abs(r.Wind - p.Wind) < 1e-9);
                        double f = F(p);
                        if (f1?.F1p is not double f1p || f1p <= 0 || double.IsNaN(f)) continue;
                        double d = Math.Abs(f - n * f1p) / (n * f1p);
                        if (d < best) { best = d; bt = t; bp = p; }
                    }
                if (bt == null || bp == null) continue;
                bool bad = best < 0.05;
                items.Add(new SidebarMetric((bad ? "● " : "") + $"Closest to {n}P", Sig(best * 100, 2) + "%",
                    $"{bt.Label} @ {Sig(bp.Wind, 3)} m/s", bad ? "bad" : ""));
            }
            Publish(new List<SidebarSection> { new() { Title = TxtFreqTitle.Text, Items = items } });

            // ---- symmetry legend under the results (only the symmetries that occur)
            _legendHost.Children.Clear();
            foreach (var sym in new[] { "collective", "BW", "FW", "cyclic" })
            {
                if (!list.Any(s => (s.Track.Type ?? "") == sym)) continue;
                _legendHost.Children.Add(new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(226, 228, 232)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(10, 3, 10, 3),
                    Margin = new Thickness(0, 0, 0, 5),
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children =
                        {
                            new SymbolSwatch { Dash = SymDash[sym], Marker = SymMark[sym], Width = 30, Height = 14, Margin = new Thickness(0, 0, 8, 0) },
                            new TextBlock { Text = SymName[sym], FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }
                        }
                    }
                });
            }
        }

        private void Publish(List<SidebarSection> sections) =>
            _main?.Results.SetPanelSidebar(ResultsViewType.Campbell, sections);

        /// <summary>Line + marker sample for the symmetry legend.</summary>
        private sealed class SymbolSwatch : FrameworkElement
        {
            public LineDash Dash { get; set; }
            public MarkerShape Marker { get; set; }

            protected override void OnRender(DrawingContext dc)
            {
                var ink = new SolidColorBrush(Color.FromRgb(55, 65, 81));
                ink.Freeze();
                var pen = new Pen(ink, 2);
                pen.DashStyle = Dash switch
                {
                    LineDash.Dash => new DashStyle(new double[] { 3.5, 2.2 }, 0),
                    LineDash.Dot => new DashStyle(new double[] { 0.9, 1.9 }, 0),
                    LineDash.DashDot => new DashStyle(new double[] { 4, 2, 1, 2 }, 0),
                    _ => DashStyles.Solid
                };
                pen.Freeze();
                double y = ActualHeight / 2;
                dc.DrawLine(pen, new Point(1, y), new Point(ActualWidth - 1, y));
                LbChart.DrawMarker(dc, Marker, new Point(ActualWidth / 2, y), 3.6, ink, null);
            }
        }
    }
}