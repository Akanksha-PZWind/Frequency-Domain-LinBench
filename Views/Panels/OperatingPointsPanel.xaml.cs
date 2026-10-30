using LinBench.Core.Models;
using LinBench.Core.ViewModels;
using LinBench.Wpf.Controls;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using static LinBench.Wpf.Controls.ChartFormat;

namespace LinBench.Wpf.Views.Panels
{
    /// <summary>Row of the "Azimuth files at X m/s" table.</summary>
    public sealed class AzimuthFileRow
    {
        public string File { get; set; } = "";
        public string Azimuth { get; set; } = "";
        public string Time { get; set; } = "";
        public string Omega { get; set; } = "";
        public string DOmega { get; set; } = "";
        public string Pitch { get; set; } = "";
        public string GenTorque { get; set; } = "";
        public string GenSpeed { get; set; } = "";
        public string MaxDf { get; set; } = "";
        public string MaxDz { get; set; } = "";
    }

    /// <summary>Row of the "Operating-point table".</summary>
    public sealed class OperatingPointRow
    {
        public OpSummary Op { get; set; } = null!;
        public string Wind { get; set; } = "";
        public string Rotor { get; set; } = "";
        public string Spread { get; set; } = "";
        public string Pitch { get; set; } = "";
        public string Torque { get; set; } = "";
        public string Power { get; set; } = "";
        public string Tsr { get; set; } = "";
        public string Cp { get; set; } = "";
        public string Ct { get; set; } = "";
        public string PlatformPitch { get; set; } = "";
        public string Azimuths { get; set; } = "";
        public string Periodicity { get; set; } = "";
        public string AzModels { get; set; } = "";
        public string Notes { get; set; } = "";
    }

    /// <summary>
    /// Operating points page – same charts, metrics and tables as the original LinBench web UI
    /// (app.js PANELS['ol:schedule'] + renderAzimuthDebug). All values come from /api/summary.
    /// </summary>
    public partial class OperatingPointsPanel : UserControl
    {
        private static readonly Color S1 = LbChart.Palette[0];
        private static readonly Color S2 = LbChart.Palette[1];

        private OperatingPointsPanelViewModel? _vm;
        private bool _redrawQueued;
        private readonly List<SidebarSection> _side = new();   // values shown in the right "RESULTS" sidebar
        private bool _updatingGrid;

        public OperatingPointsPanel()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Loaded += (s, e) => QueueRedraw();
        }

        // ------------------------------------------------------------------ wiring

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_vm != null)
            {
                _vm.OperatingPoints.CollectionChanged -= OnPointsChanged;
                _vm.PropertyChanged -= OnVmPropertyChanged;
            }
            _vm = e.NewValue as OperatingPointsPanelViewModel;
            if (_vm != null)
            {
                _vm.OperatingPoints.CollectionChanged += OnPointsChanged;
                _vm.PropertyChanged += OnVmPropertyChanged;
            }
            QueueRedraw();
        }

        private void OnPointsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRedraw();

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(OperatingPointsPanelViewModel.SelectedPoint)) QueueRedraw();
        }

        /// <summary>LoadData adds the points one by one – draw once, after the last one.</summary>
        private void QueueRedraw()
        {
            if (_redrawQueued) return;
            _redrawQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _redrawQueued = false;
                try { Redraw(); }
                catch (Exception ex) { TxtOrderNote.Text = "Could not draw the operating-point charts: " + ex.Message; }
            }));
        }

        private void OnOpGridSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingGrid || _vm == null) return;
            if (OpGrid.SelectedItem is OperatingPointRow row && !ReferenceEquals(_vm.SelectedPoint, row.Op))
                _vm.SelectedPoint = row.Op;        // ResultsViewModel follows -> wind-speed chips + all panels
        }

        /// <summary>The tables sit inside the page's ScrollViewer: pass the wheel on when they cannot scroll.</summary>
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

        // ------------------------------------------------------------------ drawing

        private void Redraw()
        {
            var ops = _vm?.OperatingPoints.ToList() ?? new List<OpSummary>();
            if (ops.Count == 0)
            {
                foreach (var c in new[] { ChartRotor, ChartPitch, ChartPower, ChartTorque, ChartTsr, ChartCpCt, ChartAzSpread, ChartOrder, ChartAzModes })
                    c.Clear();
                PublishSidebar(new List<SidebarSection>());
                FileGrid.ItemsSource = null; OpGrid.ItemsSource = null;
                return;
            }

            _side.Clear();
            int sel = _vm?.SelectedPoint != null ? ops.IndexOf(_vm.SelectedPoint) : 0;
            if (sel < 0) sel = 0;
            var W = ops.Select(o => o.WindSpeed).ToArray();

            // ---- rows 1-2: trim values vs wind speed (selected wind speed = vertical line)
            TrimChart(ChartRotor, "Rotor speed", "rpm", W, sel, ("Rotor speed", ops.Select(o => o.Omega), S1));
            TrimChart(ChartPitch, "Blade pitch", "deg", W, sel, ("Pitch", ops.Select(o => o.Pitch), S1));
            TrimChart(ChartPower, "Generator power", "MW", W, sel, ("Power", ops.Select(o => o.PowerKw / 1000.0), S1));
            TrimChart(ChartTorque, "Generator torque", "MN·m", W, sel, ("Torque", ops.Select(o => o.GenTorqueNm / 1e6), S1));
            TrimChart(ChartTsr, "Tip-speed ratio", "", W, sel, ("TSR", ops.Select(o => o.Tsr), S1));
            TrimChart(ChartCpCt, "Power and thrust coefficients", "", W, sel,
                      ("Cp", ops.Select(o => o.Cp), S1), ("Ct", ops.Select(o => o.Ct), S2));

            // ---- row 3
            AzimuthSpreadChart(ops, sel, W);
            LinearisationOrderChart(ops[sel]);

            // ---- azimuth files at the selected wind speed
            AzimuthFiles(ops[sel]);

            // ---- operating-point table
            OperatingPointTable(ops, sel);

            PublishSidebar(_side);
        }

        /// <summary>The numbers go to the right sidebar (▾ RESULTS), as in the original LinBench.</summary>
        private void PublishSidebar(List<SidebarSection> sections)
        {
            if (Window.GetWindow(this)?.DataContext is MainViewModel main)
                main.Results.SetPanelSidebar(ResultsViewType.OperatingPoints, sections.ToList());
        }

        private static void TrimChart(LbChart chart, string title, string unit, double[] W, int sel,
                                      params (string Name, IEnumerable<double?> Y, Color Color)[] series)
        {
            chart.XLabel = "Wind speed [m/s]";
            chart.YLabel = unit.Length > 0 ? $"{title} [{unit}]" : title;
            chart.XName = "Wind";
            chart.XUnit = "m/s";
            chart.XInclude = new[] { W.Min(), W.Max() };
            chart.Hover = ChartHover.Nearest;

            var list = series.Select(s => new ChartSeries
            {
                Name = s.Name,
                X = W,
                Y = s.Y.Select(NaN).ToArray(),
                Color = s.Color,
                LineWidth = 1.6,
                MarkerSize = 3.2,
                Unit = unit
            }).ToList();

            chart.SetData(list, new[]
            {
                new ChartAnnotation { Kind = AnnotationKind.VLine, X = W[sel], Color = LbChart.Accent, Width = 1 }
            });
        }

        /// <summary>
        /// Did every azimuth file of a wind speed see the same rotor speed? The files of one wind speed are
        /// spread sideways in azimuth order (0°…360° → left…right) and joined: drift = slope, 1P/3P ripple =
        /// wave, a clean set = flat line at 0.
        /// </summary>
        private void AzimuthSpreadChart(List<OpSummary> ops, int sel, double[] W)
        {
            var sorted = W.OrderBy(v => v).ToList();
            var gaps = sorted.Zip(sorted.Skip(1), (a, b) => b - a).Where(g => g > 0).ToList();
            double half = 0.3 * (gaps.Count > 0 ? gaps.Min() : 1.0);

            var series = new List<ChartSeries>();
            foreach (var o in ops)
            {
                var fd = o.FileDiag.Where(f => f.RotorRpm.HasValue).OrderBy(f => f.AzimuthDeg ?? 0.0).ToList();
                if (fd.Count == 0) continue;
                double mean = fd.Average(f => f.RotorRpm!.Value);
                if (Math.Abs(mean) < 1e-12) continue;
                var oc = o.OmegaCheck;
                var color = oc?.Level == "warning" ? LbChart.CritColor : oc?.Level == "notice" ? LbChart.WarnColor : S1;
                series.Add(new ChartSeries
                {
                    Name = $"{Sig(o.WindSpeed, 3)} m/s",
                    X = fd.Select(f => o.WindSpeed + half * (2.0 * (f.AzimuthDeg ?? 180.0) / 360.0 - 1.0)).ToArray(),
                    Y = fd.Select(f => (f.RotorRpm!.Value / mean - 1.0) * 100.0).ToArray(),
                    Color = color,
                    LineWidth = 1.4,
                    MarkerSize = 3.2,
                    InLegend = false,
                    Unit = "%",
                    Tips = fd.Select(f =>
                        $"{Sig(o.WindSpeed, 3)} m/s · {f.File}\n" +
                        $"azimuth {Sig(f.AzimuthDeg, 4)}° · t {Sig(f.Time, 5)} s\n" +
                        $"Ω {Sig(f.RotorRpm, 6)} rpm · {(f.RotorRpm >= mean ? "+" : "")}{Sig((f.RotorRpm!.Value / mean - 1) * 100, 3)} %" +
                        (oc != null && !string.IsNullOrEmpty(oc.Kind) && oc.Kind != "none" ? $"\nspread {Sig(oc.Spread * 100, 3)} % · {oc.Kind}" : "")).ToArray()
                });
            }

            ChartAzSpread.XLabel = "Wind speed [m/s] (files spread by azimuth)";
            ChartAzSpread.YLabel = "ΔΩ from mean [%]";
            ChartAzSpread.XName = "Wind";
            ChartAzSpread.XUnit = "m/s";
            ChartAzSpread.XPad = 0.04;
            ChartAzSpread.YInclude = new[] { -0.35, 0.35 };
            ChartAzSpread.Hover = ChartHover.Nearest;
            ChartAzSpread.SetData(series, new[]
            {
                new ChartAnnotation { Kind = AnnotationKind.Band, X = W[sel] - half * 1.15, X1 = W[sel] + half * 1.15, Color = LbChart.Accent, Opacity = 0.08 },
                new ChartAnnotation { Kind = AnnotationKind.HLine, Y = 0.25, Dashed = true, Color = LbChart.MutedColor, Label = "±0.25 % (warning: 0.5 % peak to peak)" },
                new ChartAnnotation { Kind = AnnotationKind.HLine, Y = -0.25, Dashed = true, Color = LbChart.MutedColor }
            });

            double largest = ops.Max(o => o.OmegaCheck?.Spread ?? 0.0) * 100.0;
            var bad = ops.Where(o => o.OmegaCheck?.Level == "warning").ToList();
            _side.Add(new SidebarSection
            {
                Title = "Rotor speed across the azimuth files",
                Items = new List<SidebarMetric>
                {
                    new("Largest spread", Sig(largest, 3) + " %", "max − min"),
                    new("Points with a warning",
                        bad.Count > 0 ? string.Join(", ", bad.Select(o => Sig(o.WindSpeed, 3))) + " m/s" : "none", "",
                        bad.Count > 0 ? "bad" : "good")
                }
            });
        }

        /// <summary>Rotor speed of each file of the selected wind speed vs simulation time; labels = azimuth.</summary>
        private void LinearisationOrderChart(OpSummary o)
        {
            var oc = o.OmegaCheck;
            var fd = o.FileDiag.Where(f => f.RotorRpm.HasValue).ToList();
            var byT = fd.OrderBy(f => f.Time ?? 0.0).ToList();
            bool hasT = byT.Count > 0 && byT.All(f => f.Time.HasValue);
            double mean = fd.Count > 0 ? fd.Average(f => f.RotorRpm!.Value) : double.NaN;

            TxtOrderTitle.Text = $"Rotor speed at {Sig(o.WindSpeed, 3)} m/s, in the order it was linearised";
            var xs = byT.Select((f, k) => hasT ? f.Time!.Value : k).ToArray();
            var ys = byT.Select(f => f.RotorRpm!.Value).ToArray();

            ChartOrder.XLabel = hasT ? "Simulation time [s]" : "File order";
            ChartOrder.YLabel = "Rotor speed [rpm]";
            ChartOrder.XName = "t";
            ChartOrder.XUnit = hasT ? "s" : "";
            ChartOrder.Hover = ChartHover.Nearest;
            ChartOrder.YInclude = null;

            var ann = new List<ChartAnnotation>();
            for (int k = 0; k < byT.Count; k++)
                ann.Add(new ChartAnnotation { Kind = AnnotationKind.PointLabel, X = xs[k], Y = ys[k], Label = Sig(byT[k].AzimuthDeg, 3) + "°", Color = LbChart.MutedColor });
            if (!double.IsNaN(mean))
                ann.Add(new ChartAnnotation { Kind = AnnotationKind.HLine, Y = mean, Dashed = true, Color = LbChart.MutedColor, Label = $"mean {Sig(mean, 5)} rpm" });

            ChartOrder.SetData(new[]
            {
                new ChartSeries
                {
                    Name = "Ω", X = xs, Y = ys, Unit = "rpm",
                    Color = oc?.Level == "warning" ? LbChart.CritColor : S1, LineWidth = 1.6, MarkerSize = 3.6, InLegend = false,
                    Tips = byT.Select(f =>
                        $"{f.File}\nazimuth {Sig(f.AzimuthDeg, 4)}° · t {Sig(f.Time, 5)} s\n" +
                        $"Ω {Sig(f.RotorRpm, 6)} rpm ({(f.RotorRpm >= mean ? "+" : "")}{Sig((f.RotorRpm!.Value / mean - 1) * 100, 3)} %)").ToArray()
                }
            }, ann);

            var kindTxt = (oc?.Kind ?? "none") switch
            {
                "drift" => "drift in time",
                "periodic" => "periodic in azimuth",
                "scatter" => "no pattern",
                _ => "constant"
            };
            var items = new List<SidebarMetric>
            {
                new("Spread", Sig((oc?.Spread ?? 0) * 100, 3) + " %", kindTxt,
                    oc?.Level == "warning" ? "bad" : oc?.Level == "ok" ? "good" : "")
            };
            if (oc?.SlopeRpmPerS != null)
                items.Add(new SidebarMetric("Trend dΩ/dt", Sig(oc.SlopeRpmPerS, 3), "rpm/s"));
            if (oc?.R2Time != null)
                items.Add(new SidebarMetric("Fit: time / azimuth",
                    $"{Sig(oc.R2Time, 2)} / {(oc.R2Azimuth == null ? "–" : Sig(oc.R2Azimuth, 2))}", "adjusted R²"));
            _side.Add(new SidebarSection
            {
                Title = $"Rotor speed at {Sig(o.WindSpeed, 3)} m/s, in the order it was linearised",
                Items = items,
                Note = oc?.Message ?? string.Empty
            });
            TxtOrderNote.Text = string.Empty;
        }

        /// <summary>"Azimuth files at X m/s": metrics, mode frequency in each single-azimuth model, file table.</summary>
        private void AzimuthFiles(OpSummary o)
        {
            TxtAzFilesTitle.Text = $"Azimuth files at {Sig(o.WindSpeed, 3)} m/s";
            var az = o.AzSens;
            var fd = o.FileDiag;

            var azItems = new List<SidebarMetric>();
            if (az != null)
            {
                azItems.Add(new SidebarMetric("Largest frequency change", Sig(az.MaxRelFreqChange * 100, 3) + " %",
                    $"{az.DfMode} ({Sig(az.DfFn, 3)} Hz)", az.MaxRelFreqChange > 0.01 ? "bad" : ""));
                azItems.Add(new SidebarMetric("Largest damping change", Sig(az.MaxDampingChange * 100, 3) + " points",
                    $"{az.DzMode} (ζ {Sig(az.DzZeta * 100, 3)} %)", az.MaxDampingChange > 0.005 ? "bad" : ""));
            }
            _side.Add(new SidebarSection { Title = $"Azimuth files at {Sig(o.WindSpeed, 3)} m/s", Items = azItems });

            // mode frequency in each single-azimuth model (x = azimuth of each file, in file_diag order)
            var modes = az?.Modes?.Take(8).ToList() ?? new List<AzimuthModeInfo>();
            if (modes.Count > 0 && fd.Count > 0)
            {
                ModeCard.Visibility = Visibility.Visible;
                var series = new List<ChartSeries>();
                for (int k = 0; k < modes.Count; k++)
                {
                    var m = modes[k];
                    var pts = new List<(double x, double y)>();
                    for (int i = 0; i < Math.Min(fd.Count, m.Df.Count); i++)
                        if (fd[i].AzimuthDeg.HasValue && m.Df[i].HasValue)
                            pts.Add((fd[i].AzimuthDeg!.Value, m.Df[i]!.Value * 100.0));
                    pts.Sort((a, b) => a.x.CompareTo(b.x));
                    series.Add(new ChartSeries
                    {
                        Name = $"{m.Label} ({Sig(m.Fn, 3)} Hz)",
                        X = pts.Select(p => p.x).ToArray(),
                        Y = pts.Select(p => p.y).ToArray(),
                        Color = LbChart.Palette[k % LbChart.Palette.Length],
                        LineWidth = 1.6,
                        MarkerSize = 3,
                        Unit = "%"
                    });
                }
                ChartAzModes.XLabel = "Azimuth [deg]";
                ChartAzModes.YLabel = "Δf vs averaged [%]";
                ChartAzModes.XName = "ψ";
                ChartAzModes.XUnit = "°";
                ChartAzModes.Hover = ChartHover.AllAtX;
                ChartAzModes.ShowLegend = true;
                ChartAzModes.SetData(series, new[]
                {
                    new ChartAnnotation { Kind = AnnotationKind.HLine, Y = 0, Color = LbChart.MutedColor, Width = 1 }
                });
            }
            else
            {
                ModeCard.Visibility = Visibility.Collapsed;
                ChartAzModes.Clear();
            }

            // per-file table
            var withRpm = fd.Where(f => f.RotorRpm.HasValue).ToList();
            double mean = withRpm.Count > 0 ? withRpm.Average(f => f.RotorRpm!.Value) : double.NaN;
            FileGrid.ItemsSource = fd.Select(f => new AzimuthFileRow
            {
                File = f.File ?? "",
                Azimuth = Sig(f.AzimuthDeg, 4),
                Time = Sig(f.Time, 5),
                Omega = Sig(f.RotorRpm, 6),
                DOmega = f.RotorRpm.HasValue && !double.IsNaN(mean) ? Sig((f.RotorRpm.Value / mean - 1) * 100, 3) : "–",
                Pitch = Sig(f.PitchDeg, 4),
                GenTorque = f.GenTorque == null ? "–" : Sig(f.GenTorque / 1000.0, 4),
                GenSpeed = Sig(f.GenRpm, 5),
                MaxDf = f.MaxDf == null ? "–" : Sig(f.MaxDf * 100, 3),
                MaxDz = f.MaxDz == null ? "–" : Sig(f.MaxDz * 100, 3)
            }).ToList();
        }

        private void OperatingPointTable(List<OpSummary> ops, int sel)
        {
            var rows = ops.Select(o =>
            {
                var oc = o.OmegaCheck;
                var a = o.AzSens;
                return new OperatingPointRow
                {
                    Op = o,
                    Wind = Sig(o.WindSpeed, 4),
                    Rotor = Sig(o.Omega, 4),
                    Spread = oc?.Spread > 1e-5 ? $"{Sig(oc!.Spread * 100, 2)} % {oc.Kind}" : "0",
                    Pitch = Sig(o.Pitch, 4),
                    Torque = Sig(o.GenTorqueNm / 1e6, 4),
                    Power = Sig(o.PowerKw / 1000.0, 4),
                    Tsr = Sig(o.Tsr, 4),
                    Cp = Sig(o.Cp, 4),
                    Ct = Sig(o.Ct, 4),
                    PlatformPitch = Sig(o.PlatformPitchDeg, 4),
                    Azimuths = o.AzimuthCount.ToString(),
                    Periodicity = $"{Sig(o.PeriodicityRaw * 100, 2)}% / {Sig(o.PeriodicityMbc * 100, 2)}%",
                    AzModels = a != null ? $"{Sig(a.MaxRelFreqChange * 100, 2)} % / {Sig(a.MaxDampingChange * 100, 2)} pt" : "–",
                    Notes = string.Join("; ", o.Warnings)
                };
            }).ToList();

            _updatingGrid = true;
            try
            {
                OpGrid.ItemsSource = rows;
                OpGrid.SelectedIndex = sel;
            }
            finally
            {
                _updatingGrid = false;
            }
        }
    }
}
