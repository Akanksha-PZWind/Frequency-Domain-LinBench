using LinBench.Core.ViewModels;
using LinBench.Wpf.Controls;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LinBench.Wpf.Views.Dialogs
{
    /// <summary>
    /// Settings window, like the original LinBench. View settings (units, size, view list, plots, colours, contrast)
    /// live in ChartSettings.Instance – they apply at once and are saved on this PC. The Python backend section edits
    /// SettingsViewModel (port, Python path, auto start) and is applied with its own button.
    /// </summary>
    public partial class SettingsDialog : UserControl
    {
        private static readonly Brush Muted = Frozen(Color.FromRgb(107, 112, 121));
        private static readonly Brush Line = Frozen(Color.FromRgb(236, 236, 232));
        private int _group;

        public SettingsDialog()
        {
            InitializeComponent();
            IsVisibleChanged += (s, e) => { if (IsVisible) Build(); };
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this)?.DataContext is MainViewModel main) main.IsSettingsOpen = false;
        }

        private void OnRestoreDefaults(object sender, RoutedEventArgs e)
        {
            ChartSettings.Instance.RestoreDefaults();
            Build();
        }

        // ------------------------------------------------------------------ rows

        private void Build()
        {
            var cs = ChartSettings.Instance;
            Rows.Children.Clear();

            Section("UNITS");
            Choice("Frequency axis", "Control plots (Bode, loop shape, sweeps, controller); Campbell diagrams are always in Hz",
                   () => cs.FreqUnit, v => cs.FreqUnit = v, ("rad/s", "rad/s"), ("hz", "Hz"));

            Section("APPEARANCE");
            Choice("Theme", "Dark and System are not in the WPF version yet",
                   () => "light", v => { }, ("light", "Light"), ("dark", "Dark", false), ("system", "System", false));
            Choice("Density", "Spacing between and inside the cards (Compact and Roomy are not in the WPF version yet)",
                   () => "normal", v => { }, ("compact", "Compact", false), ("normal", "Normal"), ("roomy", "Roomy", false));
            Choice("Text and plot size", "Scales the workspace (plots included)",
                   () => cs.Scale, v => cs.Scale = v, ("90", "90 %"), ("100", "100 %"), ("110", "110 %"), ("125", "125 %"));
            Choice("View list", "Full names or icons only",
                   () => cs.ViewList, v => cs.ViewList = v, ("names", "Names"), ("icons", "Icons"));

            Section("PLOTS");
            Choice("Legend", "Auto: inside the axes, folded to \"N curves\" when there are more than the limit below. Beside: a column right of the plot",
                   () => cs.Legend, v => cs.Legend = v, ("auto", "Auto"), ("inside", "Inside"), ("beside", "Beside"), ("above", "Above"), ("off", "Off"));
            Choice("Fold legends above", "Number of curves before a legend is folded (Auto)",
                   () => cs.FoldAbove, v => cs.FoldAbove = v, ("4", "4"), ("6", "6"), ("10", "10"), ("never", "never"));
            Choice("Plot height", "Height of the plots in the views",
                   () => cs.PlotHeight, v => cs.PlotHeight = v, ("compact", "Compact"), ("normal", "Normal"), ("tall", "Tall"));
            Choice("Line width", "",
                   () => cs.Lines, v => cs.Lines = v, ("thin", "Thin"), ("normal", "Normal"), ("bold", "Bold"));
            Choice("Grid", "Fine adds minor lines on linear axes",
                   () => cs.Grid, v => cs.Grid = v, ("off", "Off"), ("on", "On"), ("fine", "Fine"));
            Choice("Crosshair", "Full: dotted lines in both axes with the values on the axes. Vertical: one line with the x value",
                   () => cs.Cursor, v => cs.Cursor = v, ("full", "Full"), ("vertical", "Vertical"), ("off", "Off"));

            Section("ACCESSIBILITY");
            var colours = Choice("Curve colours", "Colour-blind safe uses the Okabe–Ito set",
                   () => cs.Colours, v => { cs.Colours = v; Build(); }, ("matlab", "MATLAB"), ("cb", "Colour-blind safe"), ("vivid", "Vivid"));
            AddSwatches(colours);
            Choice("Contrast", "Darker text, borders and plot frames",
                   () => cs.Contrast, v => cs.Contrast = v, ("normal", "Normal"), ("high", "High"));

            BackendSection();
        }

        private void Section(string title) =>
            Rows.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Muted,
                Margin = new Thickness(0, 18, 0, 4)
            });

        /// <summary>One setting: title + description left, segmented buttons right. Returns the title panel.</summary>
        private StackPanel Choice(string title, string desc, Func<string> get, Action<string> set,
                                  params (string value, string label, bool enabled)[] options)
        {
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold });
            left.Children.Add(head);
            if (desc.Length > 0)
                left.Children.Add(new TextBlock { Text = desc, FontSize = 12.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

            var host = new Border { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            host.SetResourceReference(StyleProperty, "ShellSegmentHost");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            string group = "Settings" + (_group++).ToString(CultureInfo.InvariantCulture);
            string current = get();
            for (int i = 0; i < options.Length; i++)
            {
                var (value, label, enabled) = options[i];
                var rb = new RadioButton { Content = label, GroupName = group, IsEnabled = enabled, Height = 30, Padding = new Thickness(11, 0, 11, 0) };
                rb.SetResourceReference(StyleProperty, "ShellMiniSegment");
                rb.FontSize = 13;
                if (i == options.Length - 1) rb.BorderThickness = new Thickness(0);
                if (!enabled) { rb.ToolTip = "Not in the WPF version yet"; ToolTipService.SetShowOnDisabled(rb, true); }
                rb.IsChecked = string.Equals(value, current, StringComparison.OrdinalIgnoreCase);
                rb.Checked += (s, e) => set(value);
                row.Children.Add(rb);
            }
            host.Child = row;

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(left);
            Grid.SetColumn(host, 1);
            g.Children.Add(host);
            Rows.Children.Add(new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 10, 0, 10), Child = g });
            return head;
        }

        // overload so options can be written as ("value", "Label") or ("value", "Label", false)
        private StackPanel Choice(string title, string desc, Func<string> get, Action<string> set, params object[] options) =>
            Choice(title, desc, get, set, options.Select(o => o switch
            {
                ValueTuple<string, string, bool> t3 => (t3.Item1, t3.Item2, t3.Item3),
                ValueTuple<string, string> t2 => (t2.Item1, t2.Item2, true),
                _ => (o.ToString() ?? "", o.ToString() ?? "", true)
            }).ToArray());

        private static void AddSwatches(StackPanel head)
        {
            var colours = ChartSettings.Instance.Colours;
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            foreach (var c in LbChart.Palette)
                panel.Children.Add(new Border { Width = 10, Height = 10, Margin = new Thickness(0, 0, 2, 0), Background = new SolidColorBrush(LbChart.Map(c)) });
            head.Children.Add(panel);
        }

        // ------------------------------------------------------------------ python backend (SettingsViewModel)

        private void BackendSection()
        {
            if (DataContext is not SettingsViewModel vm) return;
            Section("PYTHON BACKEND");

            var auto = new CheckBox { Content = "Start the Python backend automatically", IsChecked = vm.AutoStartPythonBackend, Margin = new Thickness(0, 8, 0, 10) };
            auto.SetResourceReference(StyleProperty, "ShellCheckBox");
            auto.Checked += (s, e) => vm.AutoStartPythonBackend = true;
            auto.Unchecked += (s, e) => vm.AutoStartPythonBackend = false;
            Rows.Children.Add(auto);

            Rows.Children.Add(new TextBlock { Text = "Port", FontSize = 13, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 0, 4) });
            var port = new TextBox { Text = vm.PythonBackendPort.ToString(CultureInfo.InvariantCulture), Tag = "8765", Width = 140, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
            port.SetResourceReference(StyleProperty, "ShellInput");
            port.TextChanged += (s, e) => { if (int.TryParse(port.Text, out var p) && p > 0 && p < 65536) vm.PythonBackendPort = p; };
            Rows.Children.Add(port);

            Rows.Children.Add(new TextBlock { Text = "Python executable (optional – otherwise the one start_backend.bat would use)", FontSize = 13, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 0, 4) });
            var py = new TextBox { Text = vm.PythonExecutablePath, Tag = @"C:\…\python.exe", Margin = new Thickness(0, 0, 0, 10) };
            py.SetResourceReference(StyleProperty, "ShellInput");
            py.TextChanged += (s, e) => vm.PythonExecutablePath = py.Text.Trim();
            Rows.Children.Add(py);

            var apply = new Button { Content = "Apply backend settings", HorizontalAlignment = HorizontalAlignment.Left, Command = vm.SaveCommand };
            apply.SetResourceReference(StyleProperty, "ShellButton");
            Rows.Children.Add(apply);
        }
    }
}
