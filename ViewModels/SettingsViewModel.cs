using LinBench.Core.Models;
using System.Windows.Input;

namespace LinBench.Core.ViewModels
{
    public class SettingsViewModel : ViewModelBase
    {
        private readonly AppSettings _settings;
        private readonly Action<AppSettings> _onSaved;

        private string _theme;
        private string _freqUnit;
        private double _lineScale;
        private string _gridMode;
        private string _legendPos;
        private bool _autoStart;
        private int _port;
        private string _pythonPath;
        private bool _useWebView2;

        public string Theme
        {
            get => _theme;
            set => SetProperty(ref _theme, value);
        }

        public string FrequencyUnit
        {
            get => _freqUnit;
            set => SetProperty(ref _freqUnit, value);
        }

        public double LineScale
        {
            get => _lineScale;
            set => SetProperty(ref _lineScale, value);
        }

        public string GridMode
        {
            get => _gridMode;
            set => SetProperty(ref _gridMode, value);
        }

        public string LegendPosition
        {
            get => _legendPos;
            set => SetProperty(ref _legendPos, value);
        }

        public bool AutoStartPythonBackend
        {
            get => _autoStart;
            set => SetProperty(ref _autoStart, value);
        }

        public int PythonBackendPort
        {
            get => _port;
            set => SetProperty(ref _port, value);
        }

        public string PythonExecutablePath
        {
            get => _pythonPath;
            set => SetProperty(ref _pythonPath, value);
        }

        public bool UseWebView2Mode
        {
            get => _useWebView2;
            set => SetProperty(ref _useWebView2, value);
        }

        public ICommand SaveCommand { get; }
        public ICommand ResetDefaultsCommand { get; }

        public SettingsViewModel(AppSettings settings, Action<AppSettings> onSaved)
        {
            _settings = settings;
            _onSaved = onSaved;

            _theme = settings.Theme;
            _freqUnit = settings.FrequencyUnit;
            _lineScale = settings.LineScale;
            _gridMode = settings.GridMode;
            _legendPos = settings.LegendPosition;
            _autoStart = settings.AutoStartPythonBackend;
            _port = settings.PythonBackendPort;
            _pythonPath = settings.PythonExecutablePath;
            _useWebView2 = settings.UseWebView2Mode;

            SaveCommand = new RelayCommand(Save);
            ResetDefaultsCommand = new RelayCommand(ResetDefaults);
        }

        public void Save()
        {
            _settings.Theme = Theme;
            _settings.FrequencyUnit = FrequencyUnit;
            _settings.LineScale = LineScale;
            _settings.GridMode = GridMode;
            _settings.LegendPosition = LegendPosition;
            _settings.AutoStartPythonBackend = AutoStartPythonBackend;
            _settings.PythonBackendPort = PythonBackendPort;
            _settings.PythonExecutablePath = PythonExecutablePath;
            _settings.UseWebView2Mode = UseWebView2Mode;

            _settings.Save();
            _onSaved(_settings);
        }

        public void ResetDefaults()
        {
            var def = new AppSettings();
            Theme = def.Theme;
            FrequencyUnit = def.FrequencyUnit;
            LineScale = def.LineScale;
            GridMode = def.GridMode;
            LegendPosition = def.LegendPosition;
            AutoStartPythonBackend = def.AutoStartPythonBackend;
            PythonBackendPort = def.PythonBackendPort;
            PythonExecutablePath = def.PythonExecutablePath;
            UseWebView2Mode = def.UseWebView2Mode;
        }
    }
}
