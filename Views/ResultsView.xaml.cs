using LinBench.Core.ViewModels;
using LinBench.Wpf.Views.Panels;
using System.ComponentModel;
using System.Windows.Controls;

namespace LinBench.Wpf.Views
{
    public partial class ResultsView : UserControl
    {
        private OperatingPointsPanel? _opPanel;
        private CampbellPanel? _campbellPanel;
        private ModesPanel? _modesPanel;
        private TransferFunctionPanel? _tfPanel;
        private TimeResponsePanel? _timePanel;
        private StructurePanel? _structurePanel;
        private StabilitySummaryPanel? _stabPanel;
        private LoopShapePanel? _loopPanel;
        private NyquistPanel? _nyquistPanel;
        private PolesPanel? _polesPanel;
        private GainStudyPanel? _gainPanel;

        public ResultsView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is ResultsViewModel oldVm)
                oldVm.PropertyChanged -= OnVmPropertyChanged;

            if (e.NewValue is ResultsViewModel newVm)
            {
                newVm.PropertyChanged += OnVmPropertyChanged;
                UpdateCurrentPanel(newVm);
            }
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ResultsViewModel.CurrentView) && DataContext is ResultsViewModel vm)
            {
                UpdateCurrentPanel(vm);
            }
        }

        private void UpdateCurrentPanel(ResultsViewModel vm)
        {
            UserControl panel = vm.CurrentView switch
            {
                ResultsViewType.OperatingPoints => _opPanel ??= new OperatingPointsPanel { DataContext = vm.OperatingPointsPanel },
                ResultsViewType.Campbell => _campbellPanel ??= new CampbellPanel { DataContext = vm.CampbellPanel },
                ResultsViewType.Modes => _modesPanel ??= new ModesPanel { DataContext = vm.ModesPanel },
                ResultsViewType.TransferFunction => _tfPanel ??= new TransferFunctionPanel { DataContext = vm.TransferFunctionPanel },
                ResultsViewType.TimeResponse => _timePanel ??= new TimeResponsePanel { DataContext = vm.TimeResponsePanel },
                ResultsViewType.Structure => _structurePanel ??= new StructurePanel { DataContext = vm.StructurePanel },
                ResultsViewType.StabilitySummary => _stabPanel ??= new StabilitySummaryPanel { DataContext = vm.StabilitySummaryPanel },
                ResultsViewType.LoopShape => _loopPanel ??= new LoopShapePanel { DataContext = vm.ClosedLoopDetailPanel },
                ResultsViewType.Nyquist => _nyquistPanel ??= new NyquistPanel { DataContext = vm.ClosedLoopDetailPanel },
                ResultsViewType.Poles => _polesPanel ??= new PolesPanel { DataContext = vm.ClosedLoopDetailPanel },
                ResultsViewType.GainStudy => _gainPanel ??= new GainStudyPanel { DataContext = vm.GainStudyPanel },
                _ => _opPanel ??= new OperatingPointsPanel { DataContext = vm.OperatingPointsPanel }
            };

            PanelHost.Content = panel;

            // NEW: the page's own sidebar controls (▾ VIEW SETTINGS, extra content under ▾ RESULTS)
            var side = panel as IViewSidebar;
            vm.ViewSettingsContent = side?.ViewSettings;
            vm.ResultsExtraContent = side?.ResultsExtra;
            vm.InsightsContent = side?.Insights;
        }
    }
}
