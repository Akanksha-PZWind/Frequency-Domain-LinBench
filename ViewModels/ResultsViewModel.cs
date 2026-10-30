using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using LinBench.Core.Models;
using LinBench.Core.Services;

namespace LinBench.Core.ViewModels
{
    /// <summary>The two open-loop pages of the app.</summary>
    public enum ResultsViewType
    {
        OperatingPoints,
        Campbell
    }

    /// <summary>NEW: one line of the right "RESULTS" sidebar (label · value · small note). Tone: "", "good", "bad".</summary>
    public class SidebarMetric
    {
        public string Label { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string Sub { get; set; } = string.Empty;
        public string Tone { get; set; } = string.Empty;

        public SidebarMetric() { }
        public SidebarMetric(string label, string value, string sub = "", string tone = "")
        {
            Label = label; Value = value; Sub = sub; Tone = tone;
        }
    }

    /// <summary>NEW: a titled block of the sidebar, e.g. "Rotor speed across the azimuth files".</summary>
    public class SidebarSection
    {
        public string Title { get; set; } = string.Empty;
        public List<SidebarMetric> Items { get; set; } = new();
        public string Note { get; set; } = string.Empty;
    }

    public class ResultsViewModel : ViewModelBase
    {
        private readonly ILinBenchApiClient _api;
        private SummaryResult? _summary;
        private int _selectedOpIndex;
        private ResultsViewType _currentView = ResultsViewType.OperatingPoints;

        public ObservableCollection<OpSummary> OperatingPoints { get; } = new();

        public int SelectedOpIndex
        {
            get => _selectedOpIndex;
            set
            {
                if (value >= 0 && value < OperatingPoints.Count && SetProperty(ref _selectedOpIndex, value))
                {
                    OnOpChanged();
                }
            }
        }

        public OpSummary? CurrentOp =>
            _selectedOpIndex >= 0 && _selectedOpIndex < OperatingPoints.Count ? OperatingPoints[_selectedOpIndex] : null;

        public ResultsViewType CurrentView
        {
            get => _currentView;
            set
            {
                if (SetProperty(ref _currentView, value))
                {
                    OnViewChanged();
                }
            }
        }

        public OperatingPointsPanelViewModel OperatingPointsPanel { get; }
        public CampbellPanelViewModel CampbellPanel { get; }

        public ICommand PreviousOpCommand { get; }
        public ICommand NextOpCommand { get; }
        public ICommand SelectOpCommand { get; }
        public ICommand SetViewCommand { get; }

        // ------------------------------------------------------------------ NEW: shell (top bar, strip, sidebar)

        private string _datasetName = string.Empty;
        private string _datasetMeta = string.Empty;
        private string _ctxRotor = "–";
        private string _ctxPitch = "–";
        private string _ctxTorque = "–";
        private readonly Dictionary<ResultsViewType, List<SidebarSection>> _panelSidebars = new();

        /// <summary>The loaded /api/summary (dataset details window).</summary>
        public SummaryResult? Summary => _summary;

        private string _statusSummary = string.Empty;
        /// <summary>Status bar, right: "17 operating points · open loop".</summary>
        public string StatusSummary { get => _statusSummary; private set => SetProperty(ref _statusSummary, value); }

        /// <summary>Dataset pill in the top bar: turbine / case title.</summary>
        public string DatasetName { get => _datasetName; private set => SetProperty(ref _datasetName, value); }
        /// <summary>Dataset pill: "17 points · 4–20 m/s".</summary>
        public string DatasetMeta { get => _datasetMeta; private set => SetProperty(ref _datasetMeta, value); }
        /// <summary>Strip under the wind-speed chips: ROTOR / PITCH / GEN TORQUE of the selected wind speed.</summary>
        public string CtxRotor { get => _ctxRotor; private set => SetProperty(ref _ctxRotor, value); }
        public string CtxPitch { get => _ctxPitch; private set => SetProperty(ref _ctxPitch, value); }
        public string CtxTorque { get => _ctxTorque; private set => SetProperty(ref _ctxTorque, value); }

        private object? _viewSettingsContent;
        private object? _resultsExtraContent;
        /// <summary>Right sidebar ▾ VIEW SETTINGS: controls of the current page (null = section hidden).</summary>
        public object? ViewSettingsContent { get => _viewSettingsContent; set => SetProperty(ref _viewSettingsContent, value); }
        /// <summary>Extra content under ▾ RESULTS (e.g. the Campbell symmetry legend).</summary>
        public object? ResultsExtraContent { get => _resultsExtraContent; set => SetProperty(ref _resultsExtraContent, value); }

        /// <summary>Right sidebar ▾ RESULTS: blocks for the current view and wind speed.</summary>
        public ObservableCollection<SidebarSection> SidebarSections { get; } = new();
        /// <summary>Right sidebar ▸ IN THIS VIEW: what the current page shows.</summary>
        public ObservableCollection<string> InThisView { get; } = new();
        /// <summary>Right sidebar ▸ DATA: the analysed dataset.</summary>
        public ObservableCollection<SidebarMetric> DataItems { get; } = new();

        /// <summary>A panel (e.g. Operating points) publishes its own result values for the sidebar.</summary>
        public void SetPanelSidebar(ResultsViewType view, IEnumerable<SidebarSection> sections)
        {
            _panelSidebars[view] = sections.ToList();
            if (view == CurrentView) RefreshSidebar();
        }

        public ResultsViewModel(ILinBenchApiClient api)
        {
            _api = api;

            OperatingPointsPanel = new OperatingPointsPanelViewModel();
            CampbellPanel = new CampbellPanelViewModel(_api);

            // FIX: a row clicked in the operating-point table selects that wind speed everywhere
            OperatingPointsPanel.PropertyChanged += (s, e) =>
            {
                if (_loading || e.PropertyName != nameof(OperatingPointsPanelViewModel.SelectedPoint)) return;
                if (OperatingPointsPanel.SelectedPoint is { } p)
                {
                    var i = OperatingPoints.IndexOf(p);
                    if (i >= 0 && i != SelectedOpIndex) SelectedOpIndex = i;
                }
            };

            PreviousOpCommand = new RelayCommand(() => SelectedOpIndex--, () => SelectedOpIndex > 0);
            NextOpCommand = new RelayCommand(() => SelectedOpIndex++, () => SelectedOpIndex < OperatingPoints.Count - 1);
            SelectOpCommand = new RelayCommand(p =>
            {
                if (p is int idx) SelectedOpIndex = idx;
                else if (p is OpSummary op) SelectedOpIndex = op.Index;
            });
            SetViewCommand = new RelayCommand(p =>
            {
                if (p is ResultsViewType vt) CurrentView = vt;
                else if (p is string s && Enum.TryParse<ResultsViewType>(s, true, out var parsed)) CurrentView = parsed;
            });
        }

        private bool _loading;

        public void LoadSummary(SummaryResult summary)
        {
            _loading = true;
            try { LoadSummaryCore(summary); }
            finally { _loading = false; }
            SyncOperatingPointsSelection();
        }

        private void LoadSummaryCore(SummaryResult summary)
        {
            _summary = summary;
            _panelSidebars.Clear();
            UpdateShellInfo(summary);
            OperatingPoints.Clear();
            foreach (var op in summary.OperatingPoints)
            {
                OperatingPoints.Add(op);
            }

            OperatingPointsPanel.LoadData(summary);
            CampbellPanel.LoadData(summary.Campbell);

            if (_selectedOpIndex >= OperatingPoints.Count) _selectedOpIndex = 0;
            OnPropertyChanged(nameof(SelectedOpIndex));
            OnOpChanged();
        }

        // FIX: the wind-speed chips / arrows mark the same point in the operating-point charts and table
        private void SyncOperatingPointsSelection()
        {
            if (_loading) return;
            var op = CurrentOp;
            if (op != null && !ReferenceEquals(OperatingPointsPanel.SelectedPoint, op))
                OperatingPointsPanel.SelectedPoint = op;
        }

        private void OnOpChanged()
        {
            OnPropertyChanged(nameof(CurrentOp));
            UpdateContextStrip();
            RefreshSidebar();
            SyncOperatingPointsSelection();
            ((RelayCommand)PreviousOpCommand).RaiseCanExecuteChanged();
            ((RelayCommand)NextOpCommand).RaiseCanExecuteChanged();
        }

        private void OnViewChanged()
        {
            RefreshSidebar();
        }

        // ------------------------------------------------------------------ NEW: shell helpers

        private static string Sig(double? v, int sig = 4) => LinBench.Wpf.Controls.ChartFormat.Sig(v, sig);

        private double? InfoNum(string key)
        {
            if (_summary?.Info != null && _summary.Info.TryGetValue(key, out var e) && e.ValueKind == JsonValueKind.Number)
                return e.GetDouble();
            return null;
        }

        private void UpdateShellInfo(SummaryResult summary)
        {
            DatasetName = summary.InfoString("title") ?? summary.InfoString("name") ?? "Analysis";
            var winds = summary.OperatingPoints.Select(o => o.WindSpeed).ToList();
            DatasetMeta = winds.Count == 0 ? "no points"
                : $"{winds.Count} points · {Sig(winds.Min(), 3)}–{Sig(winds.Max(), 3)} m/s";
            StatusSummary = $"{winds.Count} operating points · open loop";
            OnPropertyChanged(nameof(Summary));

            DataItems.Clear();
            DataItems.Add(new SidebarMetric("Case", summary.InfoString("title") ?? "–"));
            DataItems.Add(new SidebarMetric("Linearisation folder", summary.InfoString("lin_dir") ?? "–"));
            DataItems.Add(new SidebarMetric("Files / operating points", $"{Sig(InfoNum("n_files"), 6)} / {summary.OperatingPoints.Count}"));
            if (winds.Count > 0)
                DataItems.Add(new SidebarMetric("Wind range", $"{Sig(winds.Min(), 3)}–{Sig(winds.Max(), 3)} m/s"));
            DataItems.Add(new SidebarMetric("Model size",
                $"{Sig(InfoNum("n_states"), 6)} states · {Sig(InfoNum("n_inputs"), 6)} inputs · {Sig(InfoNum("n_outputs"), 6)} outputs"));
            DataItems.Add(new SidebarMetric("Analysis", "open loop"));
            if (summary.LoadTimeS.HasValue)
                DataItems.Add(new SidebarMetric("Load time", Sig(summary.LoadTimeS, 3) + " s"));
        }

        private void UpdateContextStrip()
        {
            var op = CurrentOp;
            CtxRotor = op?.Omega == null ? "–" : Sig(op.Omega, 3);
            CtxPitch = op?.Pitch == null ? "–" : Sig(op.Pitch, 3);
            CtxTorque = op?.GenTorqueNm == null ? "–" : Sig(op.GenTorqueNm / 1e6, 3);
        }

        private static readonly Dictionary<ResultsViewType, string[]> ViewContents = new()
        {
            [ResultsViewType.OperatingPoints] = new[] { "Rotor speed", "Blade pitch", "Generator power", "Generator torque", "Tip-speed ratio",
                "Power and thrust coefficients", "Rotor speed across the azimuth files", "Rotor speed in the order it was linearised",
                "Azimuth files at the selected wind speed", "Operating-point table" },
            [ResultsViewType.Campbell] = new[] { "Campbell diagram", "Campbell visualization diagram (mode-shape animation)", "Damping of the tracked modes", "Tracked modes" },
        };

        private void RefreshSidebar()
        {
            SidebarSections.Clear();
            if (_panelSidebars.TryGetValue(CurrentView, out var own) && own.Count > 0)
            {
                foreach (var sec in own) SidebarSections.Add(sec);
            }
            else
            {
                foreach (var sec in DefaultSidebar()) SidebarSections.Add(sec);
            }

            InThisView.Clear();
            if (ViewContents.TryGetValue(CurrentView, out var items))
                foreach (var it in items) InThisView.Add(it);
        }

        /// <summary>Views without their own sidebar values: the selected operating point.</summary>
        private IEnumerable<SidebarSection> DefaultSidebar()
        {
            var op = CurrentOp;
            if (op == null) yield break;

            yield return new SidebarSection
            {
                Title = $"Operating point at {Sig(op.WindSpeed, 3)} m/s",
                Items = new List<SidebarMetric>
                {
                    new("Rotor speed", Sig(op.Omega, 4), "rpm"),
                    new("Blade pitch", Sig(op.Pitch, 4), "deg"),
                    new("Generator torque", Sig(op.GenTorqueNm / 1e6, 4), "MN·m"),
                    new("Generator power", Sig(op.PowerKw / 1000.0, 4), "MW"),
                }
            };
        }
    }
}

