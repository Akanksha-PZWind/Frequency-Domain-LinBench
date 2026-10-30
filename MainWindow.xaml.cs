using LinBench.Core.ViewModels;
using LinBench.Wpf.Controls;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LinBench.Wpf
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private bool _compactNav;

        public MainWindow()
        {
            InitializeComponent();
            _vm = new MainViewModel();
            DataContext = _vm;

            _vm.PropertyChanged += OnVmPropertyChanged;
            Loaded += async (s, e) =>
            {
                UpdateScreenVisibility();
                await _vm.InitializeAsync();
            };
            Closing += (s, e) => _vm.Dispose();

            PreviewKeyDown += OnMainWindowKeyDown;

            // Settings ▸ Text and plot size / View list apply at once
            ChartSettings.Instance.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName is nameof(ChartSettings.Scale) or nameof(ChartSettings.ViewList)) ApplyViewSettings();
            };
            ApplyViewSettings();
        }

        /// <summary>Text and plot size (scales nav, page and sidebar) and View list (names / icons only).</summary>
        private void ApplyViewSettings()
        {
            double f = ChartSettings.Instance.ScaleFactor;
            WorkArea.LayoutTransform = Math.Abs(f - 1) < 1e-6 ? Transform.Identity : new ScaleTransform(f, f);
            _compactNav = ChartSettings.Instance.ViewList == "icons";
            NavRoot.Tag = _compactNav ? "compact" : null;
            NavBorder.Width = _compactNav ? 64 : 230;
            TxtCompact.Text = _compactNav ? "›" : "‹   Icons only";
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.CurrentScreen):
                case nameof(MainViewModel.IsNavOpen):
                    UpdateScreenVisibility();
                    break;
                case nameof(MainViewModel.IsSettingsOpen):
                case nameof(MainViewModel.IsLogOpen):
                case nameof(MainViewModel.IsTurbineSpecsOpen):
                    UpdateModalDialogs();
                    break;
            }
        }

        /// <summary>
        /// Start page: top bar (logo, search, settings, more) + page + status bar, like the original.
        /// Results: also ☰, dataset pill, left nav, right sidebar, sidebar / report buttons.
        /// </summary>
        private void UpdateScreenVisibility()
        {
            var screen = _vm.CurrentScreen;
            bool results = screen == ActiveScreen.Results;

            SetupViewControl.Visibility = screen == ActiveScreen.Setup ? Visibility.Visible : Visibility.Collapsed;
            ProgressViewControl.Visibility = screen == ActiveScreen.Progress ? Visibility.Visible : Visibility.Collapsed;
            ResultsViewControl.Visibility = results ? Visibility.Visible : Visibility.Collapsed;
            WebViewControl.Visibility = screen == ActiveScreen.WebViewHost ? Visibility.Visible : Visibility.Collapsed;

            var onResults = results ? Visibility.Visible : Visibility.Collapsed;
            BtnNav.Visibility = onResults;
            DatasetPill.Visibility = onResults;
            BtnSidebar.Visibility = onResults;
            BtnReport.Visibility = onResults;

            NavBorder.Visibility = results && _vm.IsNavOpen ? Visibility.Visible : Visibility.Collapsed;
            SideBar.Visibility = results && BtnSidebar.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateModalDialogs()
        {
            bool anyOpen = _vm.IsSettingsOpen || _vm.IsLogOpen || _vm.IsTurbineSpecsOpen;
            ModalBackdrop.Visibility = anyOpen ? Visibility.Visible : Visibility.Collapsed;

            SettingsDlg.Visibility = _vm.IsSettingsOpen ? Visibility.Visible : Visibility.Collapsed;
            LogDlg.Visibility = _vm.IsLogOpen ? Visibility.Visible : Visibility.Collapsed;
            SpecsDlg.Visibility = _vm.IsTurbineSpecsOpen ? Visibility.Visible : Visibility.Collapsed;
        }

        // ------------------------------------------------------------------ top bar / nav

        private void OnNavClick(object sender, RoutedEventArgs e)
        {
            if (_vm.CurrentScreen != ActiveScreen.Results && _vm.HasDataset)
                _vm.CurrentScreen = ActiveScreen.Results;
        }

        private void OnSidebarToggle(object sender, RoutedEventArgs e) => UpdateScreenVisibility();

        /// <summary>"‹ Icons only" / "›" – narrow nav with icons only.</summary>
        private void OnCompactClick(object sender, RoutedEventArgs e) =>
            ChartSettings.Instance.ViewList = _compactNav ? "names" : "icons";   // saved, same as Settings ▸ View list

        private void OnMoreClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.ContextMenu != null)
            {
                b.ContextMenu.PlacementTarget = b;
                b.ContextMenu.Placement = PlacementMode.Bottom;
                b.ContextMenu.IsOpen = true;
            }
        }

        private void OnModalBackdropMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == ModalBackdrop)
            {
                _vm.IsSettingsOpen = false;
                _vm.IsLogOpen = false;
                _vm.IsTurbineSpecsOpen = false;
            }
        }

        /// <summary>Sidebar ▸ DATA: every chart of the current page in one CSV file.</summary>
        private void OnExportViewCsv(object sender, RoutedEventArgs e)
        {
            var charts = new System.Collections.Generic.List<LbChart>();
            void Collect(DependencyObject d)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                {
                    var c = VisualTreeHelper.GetChild(d, i);
                    if (c is LbChart ch) charts.Add(ch);
                    Collect(c);
                }
            }
            if (ResultsViewControl.PanelHost.Content is DependencyObject page) Collect(page);
            if (charts.Count == 0)
            {
                _vm.StatusText = "This view has no chart data to export.";
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV file|*.csv",
                FileName = $"linbench_{_vm.Results.CurrentView}.csv"
            };
            if (dlg.ShowDialog() != true) return;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < charts.Count; i++) charts[i].WriteCsv(sb, header: i == 0);
            System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));
            _vm.StatusText = $"Saved {dlg.FileName}";
        }

        // ------------------------------------------------------------------ command palette (Ctrl+K)

        public sealed class PaletteItem
        {
            public string Title { get; set; } = "";
            public string Hint { get; set; } = "";
            public Geometry? Icon { get; set; }
            public Brush IconBrush { get; set; } = Brushes.Gray;
            public bool Enabled { get; set; } = true;
            public string Keys { get; set; } = "";
            public bool IsWind { get; set; }
            public ResultsViewType? View { get; set; }
            public Action Run { get; set; } = () => { };
        }

        private System.Collections.Generic.List<PaletteItem> _paletteAll = new();
        private static readonly Brush IconInk = new SolidColorBrush(Color.FromRgb(80, 86, 96));

        private Geometry? Ico(string key) => TryFindResource(key) as Geometry;

        private System.Collections.Generic.List<PaletteItem> BuildPaletteItems()
        {
            var list = new System.Collections.Generic.List<PaletteItem>();
            bool data = _vm.HasDataset, cl = data && _vm.Results.HasClosedLoop;

            void View(string title, ResultsViewType? view, string icon, bool closed, bool available, string keys = "")
            {
                list.Add(new PaletteItem
                {
                    Title = title,
                    Hint = closed ? "Closed loop" : "Open loop",
                    Icon = Ico(icon),
                    IconBrush = IconInk,
                    Enabled = available && view != null,
                    View = view,
                    Keys = keys,
                    Run = () =>
                    {
                        if (view == null) return;
                        _vm.CurrentScreen = ActiveScreen.Results;
                        _vm.Results.CurrentView = view.Value;
                    }
                });
            }
            View("Operating points", ResultsViewType.OperatingPoints, "IcoOperatingPoints", false, data, "trim schedule");
            View("Campbell diagram", ResultsViewType.Campbell, "IcoCampbell", false, data);
            View("Modes", ResultsViewType.Modes, "IcoModes", false, data, "eigenvalues mode list");
            View("Transfer function", ResultsViewType.TransferFunction, "IcoTransfer", false, data, "bode tf");
            View("Time response", ResultsViewType.TimeResponse, "IcoTime", false, data, "step impulse");
            View("Across wind speeds", null, "IcoAcross", false, false, "sweep");
            View("Controllability & Hankel", ResultsViewType.Structure, "IcoHankel", false, data, "observability structure");
            View("Stability summary", ResultsViewType.StabilitySummary, "IcoStability", true, cl, "margins");
            View("Loop shape", ResultsViewType.LoopShape, "IcoLoop", true, cl, "sensitivity");
            View("Nyquist", ResultsViewType.Nyquist, "IcoNyquist", true, cl);
            View("Poles & root locus", ResultsViewType.Poles, "IcoPoles", true, cl);
            View("Gain study", ResultsViewType.GainStudy, "IcoGain", true, cl);
            View("Time response", null, "IcoTime", true, false);
            View("Controller & design", null, "IcoController", true, false);

            var wind = Geometry.Parse("M1,5 H10 C13,5 13,1 10,1 M1,9 H13 C16,9 16,13 13,13 M1,13 H6");
            var ops = _vm.Results.OperatingPoints;
            for (int i = 0; i < ops.Count; i++)
            {
                int idx = i;
                var o = ops[i];
                list.Add(new PaletteItem
                {
                    Title = $"{ChartFormat.Sig(o.WindSpeed, 3)} m/s",
                    Hint = $"Wind speed · {ChartFormat.Sig(o.Omega, 3)} rpm",
                    Icon = wind,
                    IconBrush = IconInk,
                    IsWind = true,
                    Keys = "wind",
                    Run = () => { _vm.CurrentScreen = ActiveScreen.Results; _vm.Results.SelectedOpIndex = idx; }
                });
            }

            void Act(string title, string hint, string icon, bool enabled, Action run, string keys = "") =>
                list.Add(new PaletteItem { Title = title, Hint = hint, Icon = Geometry.Parse(icon), IconBrush = IconInk, Enabled = enabled, Run = run, Keys = keys });
            Act("New analysis", "Action", "M8,2 V14 M2,8 H14", true, () => _vm.SwitchToSetupCommand.Execute(null), "setup inputs start");
            Act("Back to results", "Action", "M10,3 L5,8 L10,13", data, () => _vm.SwitchToResultsCommand.Execute(null));
            Act("Dataset details", "Action", "M2,2 H14 V14 H2 Z M5,6 H11 M5,9 H11 M5,12 H9", data, () => _vm.IsTurbineSpecsOpen = true, "turbine specs case");
            Act("Analysis log", "Action", "M3,1 H10 L13,4 V15 H3 Z M5.5,8 H10.5 M5.5,11 H10.5", true, () => _vm.IsLogOpen = true, "report");
            Act("Settings", "Action · Ctrl+,", "M8,5 A3,3 0 1 1 7.99,5 M8,1 V3 M8,13 V15 M1,8 H3 M13,8 H15", true, () => _vm.IsSettingsOpen = true, "preferences options");
            Act("Open linearisation folder", "Action", "M1,4 H6 L8,6 H15 V14 H1 Z", !string.IsNullOrEmpty(_vm.CurrentFolderPath),
                () => _vm.OpenFolderInExplorerCommand.Execute(null), "explorer");
            Act("All plot data of this view (CSV)", "Action", "M8,2 V11 M4,7 L8,11 L12,7 M2,14 H14", data && _vm.CurrentScreen == ActiveScreen.Results,
                () => OnExportViewCsv(this, new RoutedEventArgs()), "export download");
            Act("Show / hide results panel", "Action", "M1,2 H15 V14 H1 Z M10,2 V14", data, () => { BtnSidebar.IsChecked = BtnSidebar.IsChecked != true; UpdateScreenVisibility(); }, "sidebar");
            Act(_compactNav ? "View list: names" : "View list: icons only", "Action", "M2,4 H14 M2,8 H14 M2,12 H14", true,
                () => ChartSettings.Instance.ViewList = _compactNav ? "names" : "icons", "nav compact");
            return list;
        }

        private void OnOpenPalette(object sender, MouseButtonEventArgs e) => OpenPalette();

        private void OpenPalette()
        {
            _paletteAll = BuildPaletteItems();
            PaletteBox.Text = string.Empty;
            FilterPalette();
            PaletteOverlay.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(new Action(() => { PaletteBox.Focus(); Keyboard.Focus(PaletteBox); }),
                                   System.Windows.Threading.DispatcherPriority.Input);
        }

        private void ClosePalette()
        {
            PaletteOverlay.Visibility = Visibility.Collapsed;
            Keyboard.ClearFocus();
        }

        private void OnPaletteTextChanged(object sender, TextChangedEventArgs e) => FilterPalette();

        private void FilterPalette()
        {
            var q = PaletteBox.Text.Trim().ToLowerInvariant();
            System.Collections.Generic.IEnumerable<PaletteItem> items = _paletteAll;
            if (q.Length > 0)
            {
                var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                items = _paletteAll.Where(it =>
                {
                    var hay = (it.Title + " " + it.Hint + " " + it.Keys).ToLowerInvariant();
                    return words.All(w => hay.Contains(w));
                });
                // a number: wind speeds first
                if (char.IsDigit(q[0])) items = items.OrderByDescending(it => it.IsWind && it.Title.StartsWith(q));
            }
            var shown = items.ToList();
            PaletteList.ItemsSource = shown;

            // select the current view (empty query) or the first available entry
            int sel = -1;
            if (q.Length == 0 && _vm.CurrentScreen == ActiveScreen.Results)
                sel = shown.FindIndex(it => it.Enabled && it.View == _vm.Results.CurrentView);
            if (sel < 0) sel = shown.FindIndex(it => it.Enabled);
            PaletteList.SelectedIndex = sel;
            if (sel >= 0) PaletteList.ScrollIntoView(shown[sel]);
        }

        private void MovePalette(int dir)
        {
            if (PaletteList.ItemsSource is not System.Collections.Generic.List<PaletteItem> shown || shown.Count == 0) return;
            int i = PaletteList.SelectedIndex;
            for (int n = 0; n < shown.Count; n++)
            {
                i = (i + dir + shown.Count) % shown.Count;
                if (shown[i].Enabled) break;
            }
            PaletteList.SelectedIndex = i;
            PaletteList.ScrollIntoView(shown[i]);
        }

        private void RunSelected()
        {
            if (PaletteList.SelectedItem is not PaletteItem it || !it.Enabled) return;
            ClosePalette();
            it.Run();
        }

        private void OnPaletteKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down: MovePalette(1); e.Handled = true; break;
                case Key.Up: MovePalette(-1); e.Handled = true; break;
                case Key.Enter: RunSelected(); e.Handled = true; break;
                case Key.Escape: ClosePalette(); e.Handled = true; break;
            }
        }

        private void OnPaletteClick(object sender, MouseButtonEventArgs e) => RunSelected();

        private void OnPaletteBackdropMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == PaletteOverlay) ClosePalette();
        }

        // ------------------------------------------------------------------ keyboard

        private void OnMainWindowKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (ctrl && e.Key == Key.K)
            {
                if (PaletteOverlay.Visibility == Visibility.Visible) ClosePalette(); else OpenPalette();
                e.Handled = true;
                return;
            }
            if (ctrl && e.Key == Key.OemComma)
            {
                _vm.IsSettingsOpen = true;
                e.Handled = true;
                return;
            }
            if (ctrl && e.Key == Key.B)
            {
                _vm.IsNavOpen = !_vm.IsNavOpen;
                e.Handled = true;
                return;
            }

            // ← / → change the wind speed on the results screen (not while typing in a text box)
            if (!ctrl && _vm.CurrentScreen == ActiveScreen.Results && Keyboard.FocusedElement is not TextBox)
            {
                if (e.Key == Key.Left && _vm.Results.PreviousOpCommand.CanExecute(null))
                {
                    _vm.Results.PreviousOpCommand.Execute(null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Right && _vm.Results.NextOpCommand.CanExecute(null))
                {
                    _vm.Results.NextOpCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }
    }
}
