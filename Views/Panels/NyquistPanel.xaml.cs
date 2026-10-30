using LinBench.Core.ViewModels;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media;

namespace LinBench.Wpf.Views.Panels
{
    public partial class NyquistPanel : UserControl
    {
        public NyquistPanel()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is ClosedLoopDetailPanelViewModel oldVm)
                oldVm.PropertyChanged -= OnVmPropertyChanged;

            if (e.NewValue is ClosedLoopDetailPanelViewModel newVm)
            {
                newVm.PropertyChanged += OnVmPropertyChanged;
                UpdatePlot(newVm);
            }
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ClosedLoopDetailPanelViewModel.Detail) && DataContext is ClosedLoopDetailPanelViewModel vm)
            {
                UpdatePlot(vm);
            }
        }

        private void UpdatePlot(ClosedLoopDetailPanelViewModel vm)
        {
            PlotNyquist.Clear();

            var nyq = vm.Detail?.Nyquist;
            if (nyq == null || nyq.Real.Count == 0) return;

            var primaryBrush = (Brush)FindResource("BrushPrimary");
            var critBrush = (Brush)FindResource("BrushCrit");

            // Add Nyquist curve
            PlotNyquist.AddSeries("L(jω)", nyq.Real, nyq.Imag, primaryBrush, 1.8);

            // Add critical point (-1, 0)
            PlotNyquist.AddSeries("Critical Point (-1,0)", new[] { -1.0 }, new[] { 0.0 }, critBrush, 3.0, isScatter: true);
        }
    }
}
