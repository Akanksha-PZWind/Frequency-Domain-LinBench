using LinBench.Core.Models;
using LinBench.Core.Services;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;

namespace LinBench.Core.ViewModels
{
    public enum ResultsViewType
    {
        OperatingPoints,
        Campbell,
        Modes,
        TransferFunction,
        TimeResponse,
        Structure,
        StabilitySummary,
        LoopShape,
        Nyquist,
        Poles,
        GainStudy
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
        public ModesPanelViewModel ModesPanel { get; }
        public TransferFunctionPanelViewModel TransferFunctionPanel { get; }
        public TimeResponsePanelViewModel TimeResponsePanel { get; }
        public StructurePanelViewModel StructurePanel { get; }
        public StabilitySummaryPanelViewModel StabilitySummaryPanel { get; }
        public ClosedLoopDetailPanelViewModel ClosedLoopDetailPanel { get; }
        public GainStudyPanelViewModel GainStudyPanel { get; }

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
        private bool _hasClosedLoop;
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
        /// <summary>Closed-loop views are enabled only with a controller and closed-loop results.</summary>
        public bool HasClosedLoop { get => _hasClosedLoop; private set => SetProperty(ref _hasClosedLoop, value); }

        private object? _viewSettingsContent;
        private object? _resultsExtraContent;
        /// <summary>Right sidebar ▾ VIEW SETTINGS: controls of the current page (null = section hidden).</summary>
        public object? ViewSettingsContent { get => _viewSettingsContent; set => SetProperty(ref _viewSettingsContent, value); }
        /// <summary>Extra content under ▾ RESULTS (e.g. the Campbell symmetry legend).</summary>
        public object? ResultsExtraContent { get => _resultsExtraContent; set => SetProperty(ref _resultsExtraContent, value); }
        private object? _insightsContent;
        /// <summary>NEW: right sidebar ▾ INSIGHTS of the current page (null = section hidden).</summary>
        public object? InsightsContent { get => _insightsContent; set => SetProperty(ref _insightsContent, value); }

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
            ModesPanel = new ModesPanelViewModel(_api);
            TransferFunctionPanel = new TransferFunctionPanelViewModel(_api);
            TimeResponsePanel = new TimeResponsePanelViewModel(_api);
            StructurePanel = new StructurePanelViewModel(_api);
            StabilitySummaryPanel = new StabilitySummaryPanelViewModel();
            ClosedLoopDetailPanel = new ClosedLoopDetailPanelViewModel(_api);
            GainStudyPanel = new GainStudyPanelViewModel(_api);

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

            ModesPanel.Invalidate();
            OperatingPointsPanel.LoadData(summary);
            CampbellPanel.LoadData(summary.Campbell);
            StabilitySummaryPanel.LoadData(summary);

            TransferFunctionPanel.SetupChannels(summary.Channels, summary.StdInputs, summary.StdOutputs);
            TimeResponsePanel.SetupChannels(summary.Channels, summary.StdInputs, summary.StdOutputs);
            StructurePanel.InputIndex = ChannelDefaults.Pick(summary.StdInputs, "pitch", "torque", "wind");
            StructurePanel.OutputIndex = ChannelDefaults.Pick(summary.StdOutputs, "genspeed", "rotspeed");

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

            UpdateActivePanel();
        }

        private void OnViewChanged()
        {
            RefreshSidebar();
            UpdateActivePanel();
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
            HasClosedLoop = summary.HasController && summary.Closed != null && summary.Closed.Count > 0;
            StatusSummary = $"{winds.Count} operating points · {(HasClosedLoop ? "open + closed loop" : "open loop")}";
            OnPropertyChanged(nameof(Summary));

            DataItems.Clear();
            DataItems.Add(new SidebarMetric("Case", summary.InfoString("title") ?? "–"));
            DataItems.Add(new SidebarMetric("Linearisation folder", summary.InfoString("lin_dir") ?? "–"));
            DataItems.Add(new SidebarMetric("Files / operating points", $"{Sig(InfoNum("n_files"), 6)} / {summary.OperatingPoints.Count}"));
            if (winds.Count > 0)
                DataItems.Add(new SidebarMetric("Wind range", $"{Sig(winds.Min(), 3)}–{Sig(winds.Max(), 3)} m/s"));
            DataItems.Add(new SidebarMetric("Model size",
                $"{Sig(InfoNum("n_states"), 6)} states · {Sig(InfoNum("n_inputs"), 6)} inputs · {Sig(InfoNum("n_outputs"), 6)} outputs"));
            var discon = summary.InfoString("discon");
            DataItems.Add(new SidebarMetric("Controller (DISCON)",
                string.IsNullOrEmpty(discon) ? "none – open loop only" : System.IO.Path.GetFileName(discon), discon ?? ""));
            DataItems.Add(new SidebarMetric("Analysis", summary.Mode));
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
            [ResultsViewType.Campbell] = new[] { "Campbell diagram", "Damping of the tracked modes", "Tracked modes" },
            [ResultsViewType.Modes] = new[] { "Eigenvalues", "Mode list" },
            [ResultsViewType.TransferFunction] = new[] { "Bode diagram", "Pole-zero map" },
            [ResultsViewType.TimeResponse] = new[] { "Step / impulse response" },
            [ResultsViewType.Structure] = new[] { "Controllability and observability", "Hankel singular values" },
            [ResultsViewType.StabilitySummary] = new[] { "Closed-loop stability and margins per wind speed" },
            [ResultsViewType.LoopShape] = new[] { "Loop gain L, sensitivity S, complementary sensitivity T" },
            [ResultsViewType.Nyquist] = new[] { "Nyquist plot of L(jω)" },
            [ResultsViewType.Poles] = new[] { "Open- and closed-loop poles" },
            [ResultsViewType.GainStudy] = new[] { "Damping map over PI gain factors" },
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

        /// <summary>Views without their own sidebar values: the selected operating point (and its closed loop).</summary>
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

            var row = _summary?.Closed?.FirstOrDefault(c => c.OpIndex == op.Index);
            if (row != null)
            {
                yield return new SidebarSection
                {
                    Title = $"Closed loop at {Sig(op.WindSpeed, 3)} m/s",
                    Items = new List<SidebarMetric>
                    {
                        new("Closed loop", row.Stable ? "stable" : "UNSTABLE", row.RegionName, row.Stable ? "good" : "bad"),
                        new("Gain margin", row.GainMarginDb == null ? "∞" : Sig(row.GainMarginDb, 3) + " dB", "", row.GainMarginDb < 6 ? "bad" : ""),
                        new("Phase margin", Sig(row.PhaseMarginDeg, 3) + "°", "", row.PhaseMarginDeg < 30 ? "bad" : ""),
                        new("Delay margin", row.DelayMarginS == null ? "–" : Sig(row.DelayMarginS, 3) + " s"),
                        new("Lowest damping", row.MinZeta == null ? "–" : Sig(row.MinZeta * 100, 3) + " %", row.MinZetaLabel ?? ""),
                    }
                };
            }
        }

        private void UpdateActivePanel()
        {
            int op = SelectedOpIndex >= 0 ? SelectedOpIndex : 0;

            switch (CurrentView)
            {
                case ResultsViewType.Modes:
                    _ = ModesPanel.LoadForOpAsync(op);
                    break;
                case ResultsViewType.TransferFunction:
                    TransferFunctionPanel.SetOp(op);
                    break;
                case ResultsViewType.TimeResponse:
                    TimeResponsePanel.SetOp(op);
                    break;
                case ResultsViewType.Structure:
                    _ = StructurePanel.LoadForOpAsync(op);
                    break;
                case ResultsViewType.LoopShape:
                case ResultsViewType.Nyquist:
                case ResultsViewType.Poles:
                    _ = ClosedLoopDetailPanel.LoadForOpAsync(op);
                    break;
                case ResultsViewType.GainStudy:
                    _ = GainStudyPanel.LoadForOpAsync(op);
                    break;
            }
        }
    }
}
