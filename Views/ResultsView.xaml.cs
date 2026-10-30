using System.ComponentModel;
using System.Windows.Controls;
using LinBench.Core.ViewModels;
using LinBench.Wpf.Views.Panels;

namespace LinBench.Wpf.Views
{
    /// <summary>Results screen: wind-speed strip + the current page (Operating points or Campbell diagram).</summary>
    public partial class ResultsView : UserControl
    {
        private OperatingPointsPanel? _opPanel;
        private CampbellPanel? _campbellPanel;

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
                ResultsViewType.Campbell => _campbellPanel ??= new CampbellPanel { DataContext = vm.CampbellPanel },
                _ => _opPanel ??= new OperatingPointsPanel { DataContext = vm.OperatingPointsPanel }
            };

            PanelHost.Content = panel;

            // the page's own sidebar controls (▾ VIEW SETTINGS, extra content under ▾ RESULTS)
            var side = panel as IViewSidebar;
            vm.ViewSettingsContent = side?.ViewSettings;
            vm.ResultsExtraContent = side?.ResultsExtra;
        }
    }
}
