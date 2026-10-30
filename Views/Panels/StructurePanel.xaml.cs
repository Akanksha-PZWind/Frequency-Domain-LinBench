using LinBench.Core.ViewModels;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media;

namespace LinBench.Wpf.Views.Panels
{
    public partial class StructurePanel : UserControl
    {
        public StructurePanel()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is StructurePanelViewModel oldVm)
                oldVm.PropertyChanged -= OnVmPropertyChanged;

            if (e.NewValue is StructurePanelViewModel newVm)
            {
                newVm.PropertyChanged += OnVmPropertyChanged;
                UpdatePlot(newVm);
            }
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(StructurePanelViewModel.Result) && DataContext is StructurePanelViewModel vm)
            {
                UpdatePlot(vm);
            }
        }

        private void UpdatePlot(StructurePanelViewModel vm)
        {
            PlotHsv.Clear();

            var res = vm.Result;
            if (res == null || res.HankelSingularValues.Count == 0) return;

            var indices = Enumerable.Range(1, res.HankelSingularValues.Count).Select(i => (double)i).ToList();
            var primaryBrush = (Brush)FindResource("BrushPrimary");

            PlotHsv.AddSeries("HSVs", indices, res.HankelSingularValues, primaryBrush, 2.0, isScatter: true);
        }
    }
}
