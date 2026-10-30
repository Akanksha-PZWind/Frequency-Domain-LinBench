using LinBench.Core.ViewModels;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media;

namespace LinBench.Wpf.Views.Panels
{
    public partial class PolesPanel : UserControl
    {
        public PolesPanel()
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
            PlotPoles.Clear();

            var poles = vm.Detail?.Poles;
            if (poles == null) return;

            var olBrush = (Brush)FindResource("BrushTextMuted");
            var clBrush = (Brush)FindResource("BrushPrimary");

            if (poles.OpenLoopPoles != null && poles.OpenLoopPoles.Count > 0)
            {
                var olX = poles.OpenLoopPoles.Select(p => p[0]);
                var olY = poles.OpenLoopPoles.Select(p => p[1]);
                PlotPoles.AddSeries("Open-Loop Poles", olX, olY, olBrush, 3.0, isScatter: true);
            }

            if (poles.ClosedLoopPoles != null && poles.ClosedLoopPoles.Count > 0)
            {
                var clX = poles.ClosedLoopPoles.Select(p => p[0]);
                var clY = poles.ClosedLoopPoles.Select(p => p[1]);
                PlotPoles.AddSeries("Closed-Loop Poles", clX, clY, clBrush, 3.5, isScatter: true);
            }
        }
    }
}
