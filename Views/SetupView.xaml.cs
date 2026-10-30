using LinBench.Core.ViewModels;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

namespace LinBench.Wpf.Views
{
    public partial class SetupView : UserControl
    {
        public SetupView()
        {
            InitializeComponent();
        }

        private SetupViewModel? Vm => DataContext as SetupViewModel;

        private void OnBrowseLinDirClick(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select OpenFAST Linearisation Folder"
            };
            if (dlg.ShowDialog() == true && Vm != null)
            {
                Vm.LinearisationDir = dlg.FolderName;
            }
        }

        private void OnBrowseDisconClick(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select ROSCO DISCON.IN",
                Filter = "DISCON Input (*.IN;*.in)|*.IN;*.in|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true && Vm != null)
            {
                Vm.DisconPath = dlg.FileName;
            }
        }

        private void OnBrowseYamlClick(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select ROSCO Tuning YAML",
                Filter = "YAML Files (*.yaml;*.yml)|*.yaml;*.yml|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true && Vm != null)
            {
                Vm.YamlPath = dlg.FileName;
            }
        }

        private void OnBrowseCpClick(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select Cp-Ct-Cq Performance Table",
                Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true && Vm != null)
            {
                Vm.CpPath = dlg.FileName;
            }
        }

        private void OnBrowseFstClick(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select OpenFAST .fst Model",
                Filter = "OpenFAST Model (*.fst)|*.fst|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true && Vm != null)
            {
                Vm.FstPath = dlg.FileName;
            }
        }

        private void DataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            e.Row.Header = (e.Row.GetIndex() + 1).ToString();
        }


        // CHANGED: OnModeOpenChecked / OnModeClosedChecked / OnModeBothChecked removed –
        // the radio buttons now bind directly to SetupViewModel.Mode.
    }
}
