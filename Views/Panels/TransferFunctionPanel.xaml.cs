using LinBench.Core.Models;
using LinBench.Core.ViewModels;
using LinBench.Wpf.Controls;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using static LinBench.Wpf.Controls.ChartFormat;

namespace LinBench.Wpf.Views.Panels
{
    /// <summary>
    /// Transfer function at the selected wind speed, like the original LinBench (app.js 'ol:tf' + tfInsights):
    /// Bode diagram (log frequency axis) and Pole-zero map in the page;
    /// sidebar ▾ VIEW SETTINGS (Input, Output, Quick pick), ▾ INSIGHTS (G(s) in factored form, verdict, tiles,
    /// poles / zeros tables) and ▾ RESULTS (Bode diagram / Pole-zero map numbers).
    /// </summary>
    public partial class TransferFunctionPanel : UserControl, IViewSidebar
    {
        private const double TwoPi = 2 * Math.PI;

        private TransferFunctionPanelViewModel? _vm;
        private MainViewModel? _main;
        private bool _redrawQueued;
        private SummaryResult? _quickFor;

        private readonly StackPanel _settings;
        private readonly StackPanel _insights;
        private ComboBox _cbIn = null!, _cbOut = null!;
        private WrapPanel _quick = null!;
        private StackPanel _quickBox = null!;

        public FrameworkElement? ViewSettings => _settings;
        public FrameworkElement? ResultsExtra => null;
        public FrameworkElement? Insights => _insights;

        private static readonly Color Good = Color.FromRgb(22, 128, 61);
        private static readonly Color Warn = Color.FromRgb(180, 83, 9);
        private static readonly Color Bad = Color.FromRgb(200, 30, 30);
        private static readonly Brush TextBrush = Frozen(Color.FromRgb(31, 41, 55));
        private static readonly Brush MutedBrush = Frozen(Color.FromRgb(100, 112, 128));
        private static readonly Brush LineBrush = Frozen(Color.FromRgb(226, 230, 235));

        public TransferFunctionPanel()
        {
            InitializeComponent();
            _settings = BuildViewSettings();
            _insights = new StackPanel();
            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
            Unloaded += (s, e) => ChartSettings.Instance.PropertyChanged -= OnChartSettingsChanged;
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        // ------------------------------------------------------------------ wiring

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ChartSettings.Instance.PropertyChanged -= OnChartSettingsChanged;
            ChartSettings.Instance.PropertyChanged += OnChartSettingsChanged;
            if (_main == null && Window.GetWindow(this)?.DataContext is MainViewModel main)
            {
                _main = main;
                _main.Results.PropertyChanged += (s, a) =>
                {
                    if (a.PropertyName == nameof(ResultsViewModel.Summary)) QueueRedraw();
                };
            }
            QueueRedraw();
        }

        private void OnChartSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChartSettings.FreqUnit)) QueueRedraw();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = e.NewValue as TransferFunctionPanelViewModel;
            if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;

            // the sidebar controls are not in this page's visual tree: give them the view model directly
            _settings.DataContext = _vm;
            if (_vm != null)
            {
                _cbIn.ItemsSource = Grouped(_vm.Inputs);
                _cbOut.ItemsSource = Grouped(_vm.Outputs);
            }
            QueueRedraw();
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(TransferFunctionPanelViewModel.Result):
                case nameof(TransferFunctionPanelViewModel.Error):
                    QueueRedraw();
                    break;
                case nameof(TransferFunctionPanelViewModel.IsLoading):
                    TxtBusy.Visibility = _vm?.IsLoading == true ? Visibility.Visible : Visibility.Collapsed;
                    break;
            }
        }

        private void OnCancelledChanged(object sender, RoutedEventArgs e) => QueueRedraw();

        private void QueueRedraw()
        {
            if (_redrawQueued) return;
            _redrawQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _redrawQueued = false;
                try { Redraw(); }
                catch (Exception ex)
                {
                    TxtError.Text = "Could not draw the transfer function: " + ex.Message;
                    TxtError.Visibility = Visibility.Visible;
                }
            }));
        }

        // ------------------------------------------------------------------ frequency unit (Settings ▸ Frequency)

        private static bool Hz => ChartSettings.Instance.FreqUnit == "hz";
        private static double WConv(double w) => Hz ? w / TwoPi : w;
        private static string WUnit => Hz ? "Hz" : "rad/s";
        private static string Fw(double? w) => w == null ? "–" : Sig(WConv(w.Value), 3) + " " + WUnit;

        // ------------------------------------------------------------------ sidebar ▾ VIEW SETTINGS

        private StackPanel BuildViewSettings()
        {
            var sp = new StackPanel();

            sp.Children.Add(SettingLabel("Input"));
            _cbIn = ChannelCombo(nameof(TransferFunctionPanelViewModel.SelectedInput));
            sp.Children.Add(_cbIn);

            sp.Children.Add(SettingLabel("Output"));
            _cbOut = ChannelCombo(nameof(TransferFunctionPanelViewModel.SelectedOutput));
            sp.Children.Add(_cbOut);

            _quickBox = new StackPanel();
            _quickBox.Children.Add(SettingLabel("Quick pick"));
            _quick = new WrapPanel();
            _quickBox.Children.Add(_quick);
            sp.Children.Add(_quickBox);
            return sp;
        }

        private static TextBlock SettingLabel(string text) =>
            new() { Text = text, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = TextBrush, Margin = new Thickness(0, 0, 0, 6) };

        /// <summary>Channel list grouped by module (ED, SrvD, IfW …) like the original's &lt;optgroup&gt;.</summary>
        private static ComboBox ChannelCombo(string path)
        {
            var cb = new ComboBox
            {
                DisplayMemberPath = nameof(ChannelInfo.Name),
                Margin = new Thickness(0, 0, 0, 12),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MaxDropDownHeight = 420
            };
            cb.SetBinding(Selector.SelectedItemProperty, new Binding(path) { Mode = BindingMode.TwoWay });
            ScrollViewer.SetCanContentScroll(cb, false);   // grouped items: smooth scrolling

            var header = new FrameworkElementFactory(typeof(TextBlock));
            header.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            header.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            header.SetValue(TextBlock.FontSizeProperty, 11.5);
            header.SetValue(TextBlock.ForegroundProperty, MutedBrush);
            header.SetValue(TextBlock.MarginProperty, new Thickness(6, 6, 0, 2));
            cb.GroupStyle.Add(new GroupStyle { HeaderTemplate = new DataTemplate { VisualTree = header } });
            return cb;
        }

        private static ICollectionView Grouped(object source)
        {
            var view = new CollectionViewSource { Source = source }.View;
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ChannelInfo.Module)));
            return view;
        }

        /// <summary>Quick pick buttons; a pair is offered only when the dataset has both channels.</summary>
        private void BuildQuickPicks()
        {
            var s = _main?.Results.Summary;
            if (ReferenceEquals(s, _quickFor) && _quick.Children.Count > 0) return;
            _quickFor = s;
            _quick.Children.Clear();
            var si = s?.StdInputs;
            var so = s?.StdOutputs;
            int? Get(Dictionary<string, int?>? d, string k) => d != null && d.TryGetValue(k, out var v) ? v : null;
            void Add(string label, string ik, string ok)
            {
                int? i = Get(si, ik), o = Get(so, ok);
                if (i == null || o == null || _vm == null) return;
                if (!_vm.Inputs.Any(c => c.Index == i) || !_vm.Outputs.Any(c => c.Index == o)) return;
                var b = new Button { Content = label, Margin = new Thickness(0, 0, 6, 6) };
                b.SetResourceReference(StyleProperty, "ShellSmallButton");
                b.Click += (_, _) => _vm?.SetPair(i.Value, o.Value);
                _quick.Children.Add(b);
            }
            Add("Pitch → Gen speed", "pitch", "genspeed");
            Add("Torque → Gen speed", "torque", "genspeed");
            Add("Wind → Gen speed", "wind", "genspeed");
            Add("Pitch → Platform pitch", "pitch", "ptfmpitch");
            Add("Wind → Platform pitch", "wind", "ptfmpitch");
            Add("Pitch → Tower-top accel", "pitch", "nacacc_fa");
            _quickBox.Visibility = _quick.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ------------------------------------------------------------------ drawing

        private static readonly Regex ModulePrefix = new(@"^(ED|SrvD|AD|IfW|HD|BD_\d|ExtPtfm|MAP|MD)\s+", RegexOptions.Compiled);
        private static string ShortLab(string? l) => ModulePrefix.Replace(l ?? "", "").Replace("controller command: ", "");

        private void Redraw()
        {
            BuildQuickPicks();
            TxtBusy.Visibility = _vm?.IsLoading == true ? Visibility.Visible : Visibility.Collapsed;
            string err = _vm?.Error ?? "";
            TxtError.Text = err.Length > 0 ? "Transfer function: " + err : "";
            TxtError.Visibility = err.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            var r = _vm?.Result;
            if (r == null || r.Metrics == null)
            {
                ChartMag.Clear(); ChartPhase.Clear(); ChartPz.Clear();
                _insights.Children.Clear();
                _main?.Results.SetPanelSidebar(ResultsViewType.TransferFunction, new List<SidebarSection>());
                return;
            }
            var m = r.Metrics;
            string gain = r.Units?.Gain ?? "";
            string labIn = r.Labels?.Input ?? _vm?.SelectedInput?.Label ?? "input";
            string labOut = r.Labels?.Output ?? _vm?.SelectedOutput?.Label ?? "output";
            TxtBodeSub.Text = $"{labIn} → {labOut} · {Sig(r.Wind, 3)} m/s · open loop";

            DrawBode(r, gain);
            DrawPoleZero(r);
            BuildInsights(r, gain);
            PublishResults(m, gain);
        }

        private void DrawBode(TfResult r, string gain)
        {
            int n = Math.Min(r.W.Count, Math.Min(r.MagRaw.Count, r.PhaseRaw.Count));
            var x = new double[n];
            var mag = new double[n];
            var ph = new double[n];
            for (int i = 0; i < n; i++)
            {
                double w = NaN(r.W[i]);
                x[i] = double.IsNaN(w) ? double.NaN : WConv(w);
                double g = NaN(r.MagRaw[i]);
                mag[i] = g > 0 ? 20 * Math.Log10(g) : double.NaN;
                ph[i] = NaN(r.PhaseRaw[i]);
            }
            var col = LbChart.Palette[0];

            ChartMag.IsXLog = true;
            ChartMag.XPad = 0;
            ChartMag.XLabel = "";
            ChartMag.YLabel = $"Magnitude [dB {gain}]";
            ChartMag.XName = "ω";
            ChartMag.XUnit = WUnit;
            ChartMag.Hover = ChartHover.AllAtX;
            ChartMag.SetData(new[] { new ChartSeries { Name = "Magnitude", X = x, Y = mag, Color = col, MarkerSize = 0, LineWidth = 1.8, Unit = "dB" } });

            ChartPhase.IsXLog = true;
            ChartPhase.XPad = 0;
            ChartPhase.XLabel = $"Frequency [{WUnit}]";
            ChartPhase.YLabel = "Phase [deg]";
            ChartPhase.XName = "ω";
            ChartPhase.XUnit = WUnit;
            ChartPhase.Hover = ChartHover.AllAtX;
            ChartPhase.SetData(new[] { new ChartSeries { Name = "Phase", X = x, Y = ph, Color = col, MarkerSize = 0, LineWidth = 1.8, Unit = "deg" } });
        }

        private static string Cplx(double? re, double? im) =>
            $"{Sig(re, 4)} {((im ?? 0) >= 0 ? "+" : "−")} {Sig(Math.Abs(im ?? 0), 4)}j";

        private void DrawPoleZero(TfResult r)
        {
            var series = new List<ChartSeries>();
            if (CbCancelled.IsChecked == true && r.PolesCancelled.Count > 0)
            {
                series.Add(new ChartSeries
                {
                    Name = "Cancelled poles",
                    X = r.PolesCancelled.Select(p => NaN(p.Re)).ToArray(),
                    Y = r.PolesCancelled.Select(p => NaN(p.Im)).ToArray(),
                    Color = LbChart.MutedColor,
                    Opacity = 0.5,
                    LineWidth = 0,
                    MarkerSize = 3.5,
                    Marker = MarkerShape.Cross,
                    Tips = r.PolesCancelled.Select(p => $"Cancelled pole · {p.Label}\n{Cplx(p.Re, p.Im)}").ToArray()
                });
            }
            series.Add(new ChartSeries
            {
                Name = "Poles",
                X = r.Poles.Select(p => NaN(p.Re)).ToArray(),
                Y = r.Poles.Select(p => NaN(p.Im)).ToArray(),
                Color = LbChart.Palette[1],
                LineWidth = 0,
                MarkerSize = 4.5,
                Marker = MarkerShape.Cross,
                Tips = r.Poles.Select(p =>
                    $"Pole · {p.Label}\n{Cplx(p.Re, p.Im)}\nf_n {Sig(p.Fn, 4)} Hz · ζ {(p.Zeta == null ? "–" : Sig(p.Zeta * 100, 3) + "%")}\nweight {Sig(p.Rel, 3)}").ToArray()
            });
            series.Add(new ChartSeries
            {
                Name = "Zeros",
                X = r.Zeros.Select(z => NaN(z.Re)).ToArray(),
                Y = r.Zeros.Select(z => NaN(z.Im)).ToArray(),
                Color = LbChart.Palette[0],
                LineWidth = 0,
                MarkerSize = 4.5,
                Marker = MarkerShape.Ring,
                Tips = r.Zeros.Select(z => $"Zero\n{Cplx(z.Re, z.Im)}\n|z| {Sig(Math.Sqrt(Sq(z.Re) + Sq(z.Im)) / TwoPi, 4)} Hz").ToArray()
            });

            // y range like the original: ± min(1.1 · largest |Im| below 30 rad/s, 6), at least ± 1.2
            double lim = 1.2;
            foreach (var p in r.Poles)
            {
                double a = Math.Abs(p.Im ?? 0);
                if (a < 30) lim = Math.Max(lim, a);
            }
            double y = Math.Min(lim * 1.1, 6);
            ChartPz.XLabel = "Real part [1/s]";
            ChartPz.YLabel = "Imaginary part [rad/s]";
            ChartPz.XName = "Re";
            ChartPz.XUnit = "1/s";
            ChartPz.Hover = ChartHover.Nearest;
            ChartPz.YDomain = new[] { -y, y };
            ChartPz.XInclude = new[] { 0.0 };
            ChartPz.SetData(series, new[]
            {
                new ChartAnnotation { Kind = AnnotationKind.VLine, X = 0, Color = Color.FromRgb(150, 158, 170), Width = 1 },
                new ChartAnnotation { Kind = AnnotationKind.HLine, Y = 0, Color = Color.FromRgb(205, 210, 218), Width = 1 }
            });
        }

        private static double Sq(double? v) => (v ?? 0) * (v ?? 0);

        // ------------------------------------------------------------------ sidebar ▾ RESULTS

        private void PublishResults(TfMetrics m, string gain)
        {
            var bode = new List<SidebarMetric>
            {
                new("DC gain", m.Integrator ? "∞ (integrator)" : Sig(m.DcGain, 4), m.Integrator ? "" : gain),
                new("Peak gain", Sig(m.PeakGain, 4), $"{gain} @ {Fw(m.PeakW)}"),
                new("Bandwidth (−3 dB)", m.BandwidthW is double bw && bw > 0 ? Fw(bw) : "–"),
                new("RHP zeros", m.NRhpZeros.ToString(CultureInfo.InvariantCulture), m.NRhpZeros > 0 ? "non-minimum phase" : "", m.NRhpZeros > 0 ? "bad" : "good"),
                new("RHP poles", m.NRhpPoles.ToString(CultureInfo.InvariantCulture), "", m.NRhpPoles > 0 ? "bad" : "good"),
                new("Effective order", $"{m.NPoles} poles · {m.NZeros} zeros"),
                new("Cancelled pole-zero pairs", m.NCancelled.ToString(CultureInfo.InvariantCulture), $"of {m.FullOrder} states")
            };
            if (m.Dominant != null)
                bode.Add(new SidebarMetric("Dominant mode", m.Dominant.Label, $"{Sig(m.Dominant.Fn, 3)} Hz · ζ {Sig(m.Dominant.Zeta * 100, 3)}%"));

            var pz = new List<SidebarMetric>
            {
                new("Poles", m.NPoles.ToString(CultureInfo.InvariantCulture)),
                new("Zeros", m.NZeros.ToString(CultureInfo.InvariantCulture))
            };
            if (m.RhpZeros.Count > 0)
                pz.Add(new SidebarMetric("Lowest RHP zero", $"{Sig(m.RhpZeros[0].Re, 3)} ± {Sig(Math.Abs(m.RhpZeros[0].Im ?? 0), 3)}j", "1/s", "bad"));
            pz.Add(new SidebarMetric("Relative degree", m.RelDegree?.ToString(CultureInfo.InvariantCulture) ?? "–"));

            _main?.Results.SetPanelSidebar(ResultsViewType.TransferFunction, new List<SidebarSection>
            {
                new() { Title = "Bode diagram", Items = bode },
                new() { Title = "Pole-zero map", Items = pz }
            });
        }

        // ------------------------------------------------------------------ sidebar ▾ INSIGHTS

        private sealed record Factor(string Txt, double At0, double Wn);

        /// <summary>One factor per real root or conjugate pair: (s + a), (s² + 2ζωₙ s + ωₙ²), s at the origin.</summary>
        private static Factor ToFactor(PoleInfo q)
        {
            double re = q.Re ?? 0, im = q.Im ?? 0, wn = Math.Sqrt(re * re + im * im);
            if (wn < 1e-9) return new Factor("s", 0, wn);
            if (Math.Abs(im) < 1e-9) return new Factor($"(s {(re <= 0 ? "+" : "−")} {Sig(Math.Abs(re), 3)})", -re, wn);
            double b = -2 * re;
            return new Factor($"(s² {(b >= 0 ? "+" : "−")} {Sig(Math.Abs(b), 3)}s + {Sig(wn * wn, 3)})", wn * wn, wn);
        }

        private static string Show(List<Factor> f) =>
            f.Count == 0 ? "1" : string.Concat(f.Take(4).Select(q => q.Txt)) + (f.Count > 4 ? $" · … ({f.Count - 4} more)" : "");

        private void BuildInsights(TfResult r, string gain)
        {
            var m = r.Metrics!;
            _insights.Children.Clear();
            static List<PoleInfo> Half(IEnumerable<PoleInfo> list) => list.Where(pz => (pz.Im ?? 0) >= -1e-9).ToList();

            // ---- G(s) in factored form
            var P = Half(r.Poles).Select(ToFactor).OrderBy(f => f.Wn).ToList();
            var Z = Half(r.Zeros).Select(ToFactor).OrderBy(f => f.Wn).ToList();
            double prodP = P.Aggregate(1.0, (a, f) => a * f.At0), prodZ = Z.Aggregate(1.0, (a, f) => a * f.At0);
            double? K = !m.Integrator && m.DcGain != null && prodZ != 0 ? m.DcGain * prodP / prodZ : null;

            var mono = new FontFamily("Cascadia Mono, Consolas, Courier New");
            var box = new Border
            {
                Background = Frozen(Color.FromRgb(247, 249, 252)),
                BorderBrush = LineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var bx = new StackPanel();
            bx.Children.Add(new TextBlock
            {
                Text = $"G(s) = {ShortLab(r.Labels?.Output)} / {ShortLab(r.Labels?.Input)}  [{gain}]",
                FontSize = 11.5,
                Foreground = MutedBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });
            var frac = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            frac.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            frac.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var kText = new TextBlock
            {
                Text = K == null ? "K ·" : Sig(K, 3) + " ·",
                FontFamily = mono,
                FontSize = 12.5,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            frac.Children.Add(kText);
            var quot = new StackPanel();
            quot.Children.Add(new TextBlock { Text = Show(Z), FontFamily = mono, FontSize = 12.5, Foreground = TextBrush, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
            quot.Children.Add(new Border { Height = 1, Background = TextBrush, Margin = new Thickness(0, 3, 0, 3) });
            quot.Children.Add(new TextBlock { Text = Show(P), FontFamily = mono, FontSize = 12.5, Foreground = TextBrush, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
            Grid.SetColumn(quot, 1);
            frac.Children.Add(quot);
            bx.Children.Add(frac);
            bx.Children.Add(new TextBlock
            {
                Text = $"{m.NZeros} zeros / {m.NPoles} poles · s in rad/s · factors in order of frequency",
                FontSize = 11.5,
                Foreground = MutedBrush,
                TextWrapping = TextWrapping.Wrap
            });
            box.Child = bx;
            _insights.Children.Add(box);

            // ---- verdict
            var light = Half(r.Poles).Where(p => p.Zeta is double z && z >= 0 && z < 0.05 && Math.Abs(p.Im ?? 0) > 1e-9)
                                     .OrderByDescending(p => p.Rel ?? 0).Take(3).ToList();
            double? zmin = m.RhpZeros.Count > 0 ? m.RhpZeros.Select(z => Math.Sqrt(Sq(z.Re) + Sq(z.Im))).Min() : null;
            if (m.NRhpPoles > 0)
                _insights.Children.Add(Verdict("bad", "Unstable open loop", $"{m.NRhpPoles} RHP pole(s): feedback must stabilise this channel"));
            else if (zmin != null)
                _insights.Children.Add(Verdict("warn", "Non-minimum phase", $"RHP zero limits the feedback bandwidth to ≈ {Fw(zmin / 2)}"));
            else
                _insights.Children.Add(Verdict("good", "Stable, minimum phase", "no RHP poles or zeros"));

            // ---- tiles
            var tiles = new List<Tile>();
            if (m.Integrator) tiles.Add(new Tile("Steady state", "integrating", "", "a constant input ramps the output"));
            else tiles.Add(new Tile("Steady-state gain", Sig(m.DcGain, 3), gain, (m.DcGain ?? 0) < 0 ? "opposite sign" : "same sign"));
            tiles.Add(new Tile("−3 dB bandwidth", m.BandwidthW is double bw && bw > 0 ? Sig(WConv(bw), 3) : "–", m.BandwidthW is double b2 && b2 > 0 ? WUnit : ""));
            if (m.Dominant != null)
                tiles.Add(new Tile("Dominant mode", Sig(m.Dominant.Fn, 3), "Hz", $"{m.Dominant.Label} · ζ {Sig(m.Dominant.Zeta * 100, 3)} %", "", true));
            tiles.Add(new Tile("Lightly damped", light.Count.ToString(CultureInfo.InvariantCulture), "ζ < 5 %",
                string.Join(" · ", light.Select(p => $"{p.Label} {Sig(p.Fn, 3)} Hz")), light.Count > 0 ? "warn" : "good", light.Count > 0));
            if (zmin != null)
                tiles.Add(new Tile("Bandwidth limit", Sig(WConv(zmin.Value / 2), 3), WUnit, $"½ × lowest RHP zero {Fw(zmin)}", "warn"));
            tiles.Add(new Tile("Modes not in this pair", $"{m.NCancelled}/{m.FullOrder}", "", "not excited or not seen"));
            _insights.Children.Add(TileGrid(tiles));

            // ---- poles and zeros, lowest frequency first
            var poles = Half(r.Poles).OrderBy(p => p.Fn ?? 0).Select(p => new[]
            {
                Sig(p.Fn, 3), p.Zeta == null ? "–" : Sig(p.Zeta * 100, 3), p.Label
            }).ToList();
            _insights.Children.Add(Table($"Poles ({poles.Count})", new[] { "f [Hz]", "ζ [%]", "Mode" }, poles, null));

            var zs = Half(r.Zeros).Select(z =>
            {
                double w = Math.Sqrt(Sq(z.Re) + Sq(z.Im));
                return (z, f: w / TwoPi, zeta: w > 1e-12 ? -(z.Re ?? 0) / w : (double?)null, rhp: (z.Re ?? 0) > 1e-9);
            }).OrderBy(t => t.f).ToList();
            _insights.Children.Add(Table($"Zeros ({zs.Count})", new[] { "f [Hz]", "ζ [%]", "" },
                zs.Select(t => new[] { Sig(t.f, 3), t.zeta == null ? "–" : Sig(t.zeta * 100, 3), t.rhp ? "RHP" : "" }).ToList(),
                zs.Select(t => t.rhp).ToList()));
        }

        private static Color ToneColor(string tone) => tone switch { "good" => Good, "warn" => Warn, "bad" => Bad, _ => Color.FromRgb(31, 41, 55) };

        /// <summary>Coloured one-line verdict with a short explanation (good / warn / bad).</summary>
        private static Border Verdict(string tone, string title, string sub)
        {
            var c = ToneColor(tone);
            var bg = tone switch
            {
                "good" => Color.FromRgb(236, 253, 243),
                "warn" => Color.FromRgb(255, 247, 230),
                _ => Color.FromRgb(254, 236, 236)
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Frozen(c), TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(sub))
                sp.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            return new Border
            {
                Background = Frozen(bg),
                BorderBrush = Frozen(c),
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 10),
                Child = sp
            };
        }

        private sealed record Tile(string Key, string Value, string Unit = "", string Sub = "", string Level = "", bool Wide = false);

        /// <summary>Two tiles per row; a wide tile takes the whole row.</summary>
        private static Grid TileGrid(List<Tile> tiles)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int row = -1, col = 2;
            foreach (var t in tiles)
            {
                if (t.Wide || col >= 2) { g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); row++; col = 0; }
                var el = TileElement(t);
                Grid.SetRow(el, row);
                Grid.SetColumn(el, col);
                if (t.Wide) el.Margin = new Thickness(0, 0, 0, 6);
                else el.Margin = col == 0 ? new Thickness(0, 0, 4, 6) : new Thickness(4, 0, 0, 6);
                if (t.Wide) { Grid.SetColumnSpan(el, 2); col = 2; }
                else col++;
                g.Children.Add(el);
            }
            return g;
        }

        private static Border TileElement(Tile t)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = t.Key.ToUpperInvariant(),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = MutedBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 3)
            });
            var v = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Frozen(ToneColor(t.Level)) };
            v.Inlines.Add(new System.Windows.Documents.Run(t.Value) { FontSize = 17, FontWeight = FontWeights.SemiBold });
            if (!string.IsNullOrEmpty(t.Unit))
                v.Inlines.Add(new System.Windows.Documents.Run(" " + t.Unit) { FontSize = 11.5, Foreground = MutedBrush });
            sp.Children.Add(v);
            if (!string.IsNullOrEmpty(t.Sub))
                sp.Children.Add(new TextBlock { Text = t.Sub, FontSize = 11.5, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            return new Border
            {
                Background = Brushes.White,
                BorderBrush = LineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 7, 9, 7),
                Child = sp
            };
        }

        /// <summary>"▸ Poles (3)" with a small table (numbers right-aligned, text left; red rows = RHP).</summary>
        private readonly Dictionary<string, bool> _open = new();

        private Expander Table(string title, string[] cols, List<string[]> rows, List<bool>? bad)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            void Cell(string text, int r, int c, bool head, bool isBad)
            {
                var tb = new TextBlock
                {
                    Text = text,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = head ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = isBad ? Frozen(Bad) : head ? MutedBrush : TextBrush,
                    TextAlignment = c < 2 ? TextAlignment.Right : TextAlignment.Left,
                    Margin = new Thickness(c < 2 ? 0 : 10, 0, c < 2 ? 6 : 0, 0)
                };
                var cell = new Border
                {
                    BorderBrush = LineBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(0, 4, 0, 4),
                    Child = tb
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                g.Children.Add(cell);
            }
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < cols.Length; c++) Cell(cols[c], 0, c, true, false);
            for (int i = 0; i < rows.Count; i++)
            {
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                bool b = bad != null && i < bad.Count && bad[i];
                for (int c = 0; c < cols.Length && c < rows[i].Length; c++) Cell(rows[i][c], i + 1, c, false, b);
            }
            var ex = new Expander
            {
                Header = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = TextBrush },
                Content = g,
                Margin = new Thickness(0, 4, 0, 6)
            };
            string key = title.Split(' ')[0];   // "Poles" / "Zeros": stays open / closed when the data changes
            ex.IsExpanded = _open.TryGetValue(key, out var o) && o;
            ex.Expanded += (_, _) => _open[key] = true;
            ex.Collapsed += (_, _) => _open[key] = false;
            ex.SetResourceReference(StyleProperty, "ShellExpander");
            return ex;
        }
    }
}
