using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace LinBench.Wpf
{
    public partial class App : Application
    {
        // NEW: never crash silently – log unexpected UI errors to %USERPROFILE%\.linbench\wpf_error.log
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += (s, args) =>
            {
                Log(args.Exception);
                MessageBox.Show(args.Exception.Message, "LinBench – unexpected error",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                args.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                Log(args.Exception);
                args.SetObserved();
            };
        }

        private static void Log(Exception ex)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".linbench");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "wpf_error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
            }
            catch { }
        }
    }
}
