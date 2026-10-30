using System;
using System.Windows;
using System.Windows.Controls;
using LinBench.Core.ViewModels;
using Microsoft.Win32;

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

        private void DataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            e.Row.Header = (e.Row.GetIndex() + 1).ToString();
        }
    }
}
