using LinBench.Core.ViewModels;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media;

namespace LinBench.Wpf.Views.Panels
{
    public partial class TimeResponsePanel : UserControl
    {
        public TimeResponsePanel()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is TimeResponsePanelViewModel oldVm)
                oldVm.PropertyChanged -= OnVmPropertyChanged;

            if (e.NewValue is TimeResponsePanelViewModel newVm)
            {
                newVm.PropertyChanged += OnVmPropertyChanged;
                UpdatePlots(newVm);
            }
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TimeResponsePanelViewModel.Result) && DataContext is TimeResponsePanelViewModel vm)
            {
                UpdatePlots(vm);
            }
        }

        private void UpdatePlots(TimeResponsePanelViewModel vm)
        {
            PlotTime.Clear();

            var res = vm.Result;
            if (res == null || res.Time.Count == 0) return;

            var primaryBrush = (Brush)FindResource("BrushPrimary");
            PlotTime.AddSeries("Response", res.Time, res.Output, primaryBrush, 2.0);
        }
    }
}
