using LinBench.Core.ViewModels;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media;

namespace LinBench.Wpf.Views.Panels
{
    public partial class LoopShapePanel : UserControl
    {
        public LoopShapePanel()
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
            PlotLoop.Clear();

            var ls = vm.Detail?.LoopShape;
            if (ls == null || ls.Freq.Count == 0) return;

            var b1 = (Brush)FindResource("BrushSeries1");
            var b2 = (Brush)FindResource("BrushSeries2");
            var b3 = (Brush)FindResource("BrushSeries5");

            PlotLoop.AddSeries("L(s) Loop", ls.Freq, ls.LMag, b1, 2.0);
            PlotLoop.AddSeries("S(s) Sensitivity", ls.Freq, ls.SMag, b2, 1.8);
            PlotLoop.AddSeries("T(s) Complementary", ls.Freq, ls.TMag, b3, 1.8);
        }
    }
}
