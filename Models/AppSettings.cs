using System.IO;
using System.Text.Json;

namespace LinBench.Core.Models
{
    public class AppSettings
    {
        public string Theme { get; set; } = "Light"; // Light, Dark
        public string FrequencyUnit { get; set; } = "rad/s"; // rad/s, Hz
        public double LineScale { get; set; } = 1.0;
        public string GridMode { get; set; } = "Normal"; // Fine, Normal, Off
        public string LegendPosition { get; set; } = "Auto"; // Auto, Inside, Side, Top, Off
        public bool AutoStartPythonBackend { get; set; } = true;
        public int PythonBackendPort { get; set; } = 8765;
        public string PythonExecutablePath { get; set; } = string.Empty;
        public bool UseWebView2Mode { get; set; } = false; // Optional toggle between Pure WPF and WebView2
        public string LastLinearisationDir { get; set; } = string.Empty;
        public string LastDisconPath { get; set; } = string.Empty;

        private static string SettingsFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".linbench", "wpf_settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch
            {
                // Fall back to defaults
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch
            {
                // Ignore save errors
            }
        }
    }
}
