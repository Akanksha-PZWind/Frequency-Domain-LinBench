using LinBench.Core.Models;
using LinBench.Core.ViewModels;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using static LinBench.Wpf.Controls.ChartFormat;

namespace LinBench.Wpf.Views.Dialogs
{
    /// <summary>
    /// Dataset details window (click the case pill in the top bar), like the original LinBench.
    /// Everything comes from /api/summary → info{} and ops[].
    /// </summary>
    public partial class TurbineSpecsDialog : UserControl
    {
        private static readonly Brush Muted = Frozen(Color.FromRgb(107, 112, 121));
        private static readonly Brush Ink = Frozen(Color.FromRgb(31, 35, 41));
        private static readonly Brush Blue = Frozen(Color.FromRgb(42, 120, 214));
        private static readonly Brush Line = Frozen(Color.FromRgb(236, 236, 232));
        private static readonly Brush CardBorder = Frozen(Color.FromRgb(228, 228, 223));

        public TurbineSpecsDialog()
        {
            InitializeComponent();
            IsVisibleChanged += (s, e) => { if (IsVisible) Build(); };
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        // ------------------------------------------------------------------ info{} helpers

        private Dictionary<string, JsonElement> _info = new();

        private JsonElement? Get(string key) => _info.TryGetValue(key, out var e) && e.ValueKind != JsonValueKind.Null ? e : null;
        private string? Str(string key) => Get(key) is JsonElement e ? (e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString()) : null;
        private double? Num(string key) => Get(key) is JsonElement e && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;
        private bool? Bool(string key) => Get(key) is JsonElement e && (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False) ? e.GetBoolean() : null;
        private List<string> StrList(string key)
        {
            if (Get(key) is not JsonElement e) return new List<string>();
            if (e.ValueKind == JsonValueKind.Array) return e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).ToList();
            if (e.ValueKind == JsonValueKind.String) return (e.GetString() ?? "").Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            return new List<string>();
        }

        // ------------------------------------------------------------------ build

        private void Build()
        {
            Body.Children.Clear();
            var main = DataContext as MainViewModel;
            var sum = main?.Results.Summary;
            if (sum == null)
            {
                TxtTitle.Text = "No analysis loaded";
                Body.Children.Add(new TextBlock { Text = "Run an analysis first.", Foreground = Muted });
                return;
            }
            _info = sum.Info ?? new Dictionary<string, JsonElement>();
            var ops = sum.OperatingPoints;
            TxtTitle.Text = Str("title") ?? Str("name") ?? "Dataset";

            int nOps = ops.Count;
            double wMin = ops.Count > 0 ? ops.Min(o => o.WindSpeed) : double.NaN, wMax = ops.Count > 0 ? ops.Max(o => o.WindSpeed) : double.NaN;
            var discon = Str("discon");
            bool hasCtrl = !string.IsNullOrEmpty(discon);

            // ---- three tiles
            var tiles = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 14) };
            tiles.Children.Add(Tile("OPERATING POINTS", nOps.ToString(CultureInfo.InvariantCulture), "", $"{Sig(wMin, 3)}–{Sig(wMax, 3)} m/s"));
            tiles.Children.Add(Tile("LINEAR MODEL", Sig(Num("n_states"), 6), "states", Bool("has_blade_dofs") == true ? "with blade DOFs" : "no blade DOFs"));
            tiles.Children.Add(Tile("CONTROLLER", hasCtrl ? System.IO.Path.GetFileName(discon!) : "none", "",
                hasCtrl ? (main!.Results.HasClosedLoop ? "closed loop available" : "closed loop not computed") : "open loop only", last: true));
            Body.Children.Add(tiles);

            // ---- notes
            if (Bool("name_order_differs") == true)
                Body.Children.Add(Banner("File names are not in wind-speed / azimuth order; LinBench orders operating points by the values written inside each file."));
            foreach (var w in StrList("scan_warnings")) Body.Children.Add(Banner(w));

            // ---- two columns of cards
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Place(grid, TurbineCard(), 0, 0);
            Place(grid, RangeCard(ops), 0, 2);
            Place(grid, ModelCard(), 2, 0);
            Place(grid, InputsCard(), 2, 2);
            Body.Children.Add(grid);
        }

        private static void Place(Grid g, UIElement e, int row, int col)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            g.Children.Add(e);
        }

        private UIElement TurbineCard()
        {
            var (card, sp) = Card("TURBINE");
            var tp = Get("turbine_params");
            var src = Get("turbine_sources");
            string? Source(string k) => src is JsonElement s && s.ValueKind == JsonValueKind.Object && s.TryGetProperty(k, out var v) ? v.ToString() : null;
            double? P(string k) => tp is JsonElement t && t.ValueKind == JsonValueKind.Object && t.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

            var rows = new (string key, string label, Func<double, string> fmt)[]
            {
                ("R", "Rotor radius", v => Sig(v, 4) + " m"),
                ("J", "Drivetrain inertia", v => Sig(v, 4) + " kg·m²"),
                ("Ng", "Gearbox ratio", v => Sig(v, 4)),
                ("GenEff", "Generator efficiency", v => Sig(v, 4) + " %"),
                ("GBoxEff", "Gearbox efficiency", v => Sig(v, 4) + " %"),
                ("rho", "Air density", v => Sig(v, 4) + " kg/m³"),
                ("rated_rotor_speed", "Rated rotor speed", v => Sig(v * 60 / (2 * Math.PI), 4) + " rpm"),
                ("rated_power", "Rated power", v => Sig(v / 1e6, 4) + " MW"),
                ("v_min", "Cut-in wind speed", v => Sig(v, 3) + " m/s"),
                ("v_rated", "Rated wind speed", v => Sig(v, 3) + " m/s"),
                ("v_max", "Cut-out wind speed", v => Sig(v, 3) + " m/s"),
                ("TSR_operational", "Operational TSR", v => Sig(v, 3)),
            };
            int n = 0;
            foreach (var (key, label, fmt) in rows)
            {
                if (P(key) is not double v) continue;
                var r = Row(label, fmt(v));
                if (Source(key) is string s) r.ToolTip = "from " + s;
                sp.Children.Add(r);
                n++;
            }
            if (n == 0) sp.Children.Add(new TextBlock { Text = "No turbine data – give a DISCON.IN, tuning YAML or .fst.", Foreground = Muted, FontSize = 12.5 });
            return card;
        }

        private UIElement RangeCard(List<OpSummary> ops)
        {
            var (card, sp) = Card("OPERATING RANGE (FROM THE LINEARISATIONS)");
            sp.Children.Add(new TextBlock { Text = "Wind speeds", FontSize = 13, Margin = new Thickness(0, 2, 0, 4) });
            var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            foreach (var o in ops.OrderBy(q => q.WindSpeed)) chips.Children.Add(Chip(Sig(o.WindSpeed, 3)));
            sp.Children.Add(chips);

            var rpm = ops.Where(o => o.Omega.HasValue).Select(o => o.Omega!.Value).ToList();
            var pitch = ops.Where(o => o.Pitch.HasValue).Select(o => o.Pitch!.Value).ToList();
            if (rpm.Count > 0) sp.Children.Add(Row("Rotor speed", $"{Sig(rpm.Min(), 4)} – {Sig(rpm.Max(), 4)} rpm"));
            if (pitch.Count > 0) sp.Children.Add(Row("Blade pitch", $"{Sig(pitch.Min(), 3)} – {Sig(pitch.Max(), 3)} °"));
            var nFiles = Num("n_files");
            if (nFiles.HasValue && ops.Count > 0)
                sp.Children.Add(Row("Files", $"{Sig(nFiles, 6)} ({Sig(nFiles / ops.Count, 3)} azimuths per wind speed)", last: true));
            return card;
        }

        private UIElement ModelCard()
        {
            var (card, sp) = Card("LINEAR MODEL");
            sp.Children.Add(Stacked("Title", Str("title") ?? "–"));
            sp.Children.Add(Row("States / inputs / outputs", $"{Sig(Num("n_states"), 6)} / {Sig(Num("n_inputs"), 6)} / {Sig(Num("n_outputs"), 6)}"));
            sp.Children.Add(Row("Rotor triplets (MBC)", Sig(Num("rotor_triplets"), 4)));
            sp.Children.Add(Row("Blade DOFs", Bool("has_blade_dofs") == true ? "yes" : "no"));
            var removed = StrList("removed_states");
            sp.Children.Add(Row("Removed states", removed.Count == 0 ? "none" : removed.Count.ToString(CultureInfo.InvariantCulture)));
            if (removed.Count > 0) sp.Children.Add(new TextBlock { Text = string.Join("\n", removed), FontSize = 11.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });

            var dofs = StrList("dofs");
            if (dofs.Count > 0)
            {
                sp.Children.Add(new TextBlock { Text = "Structural DOFs", FontSize = 13, Margin = new Thickness(0, 6, 0, 4) });
                var w = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
                foreach (var d in dofs) w.Children.Add(Chip(d));
                sp.Children.Add(w);
            }
            var mods = StrList("modules");
            if (mods.Count > 0)
            {
                sp.Children.Add(new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 4, 0, 6) });
                sp.Children.Add(new TextBlock { Text = "Modules", FontSize = 13, Margin = new Thickness(0, 0, 0, 4) });
                var w = new WrapPanel();
                foreach (var m in mods) w.Children.Add(Chip(m));
                sp.Children.Add(w);
            }
            return card;
        }

        private UIElement InputsCard()
        {
            var (card, sp) = Card("INPUT FILES");
            sp.Children.Add(Stacked("Linearisation folder", Str("lin_dir") ?? "–", mono: true));
            foreach (var (k, label) in new[] { ("discon", "DISCON"), ("yaml", "Tuning YAML"), ("cp", "Cp-Ct-Cq table"), ("fst", "OpenFAST model") })
            {
                var v = Str(k);
                var r = Row(label, string.IsNullOrEmpty(v) ? "not given" : System.IO.Path.GetFileName(v), last: k == "fst");
                if (!string.IsNullOrEmpty(v)) r.ToolTip = v;
                sp.Children.Add(r);
            }
            return card;
        }

        // ------------------------------------------------------------------ building blocks

        private static (Border card, StackPanel body) Card(string title)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Blue, Margin = new Thickness(0, 0, 0, 10) });
            var card = new Border
            {
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
                Child = sp,
                VerticalAlignment = VerticalAlignment.Top
            };
            return (card, sp);
        }

        private static Border Tile(string label, string value, string unit, string sub, bool last = false)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = label, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Muted });
            var vrow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            vrow.Children.Add(new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Ink });
            if (unit.Length > 0) vrow.Children.Add(new TextBlock { Text = " " + unit, FontSize = 13, Foreground = Muted, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 0, 4) });
            sp.Children.Add(vrow);
            sp.Children.Add(new TextBlock { Text = sub, FontSize = 12.5, Foreground = Muted });
            return new Border
            {
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, last ? 0 : 10, 0),
                Child = sp
            };
        }

        private static Border Banner(string text) => new()
        {
            Background = Frozen(Color.FromRgb(232, 241, 252)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new TextBlock { Text = text, FontSize = 13, Foreground = Frozen(Color.FromRgb(40, 55, 75)), TextWrapping = TextWrapping.Wrap }
        };

        private static Border Row(string label, string value, bool last = false)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center });
            var v = new TextBlock { Text = value, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = Ink, TextAlignment = TextAlignment.Right };
            Grid.SetColumn(v, 1);
            g.Children.Add(v);
            return new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1), Padding = new Thickness(0, 6, 0, 6), Child = g };
        }

        private static Border Stacked(string label, string value, bool mono = false)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Ink });
            var v = new TextBlock { Text = value, FontSize = mono ? 12.5 : 13, Foreground = Ink, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            if (mono) v.FontFamily = new FontFamily("Consolas");
            sp.Children.Add(v);
            return new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 4, 0, 6), Child = sp };
        }

        private static Border Chip(string text) => new()
        {
            BorderBrush = Frozen(Color.FromRgb(205, 207, 211)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(0, 0, 4, 4),
            Child = new TextBlock { Text = text, FontSize = 12.5, Foreground = Ink }
        };
    }
}
