using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using LinBench.Core.Models;
using LinBench.Core.Services;

namespace LinBench.Core.ViewModels
{
    public enum ActiveScreen
    {
        Setup,
        Progress,
        Results
    }

    public class MainViewModel : ViewModelBase, IDisposable
    {
        private readonly ILinBenchApiClient _api;
        private readonly IPythonBackendManager _backend;
        private AppSettings _settings;

        private ActiveScreen _currentScreen = ActiveScreen.Setup;
        private bool _isNavOpen = true;
        private string _datasetTitle = string.Empty;
        private bool _hasDataset;
        private string _statusText = "Ready";
        private bool _isBackendConnected;
        private string _currentFolderPath = string.Empty;
        private string _verdict = string.Empty;
        private string _appVersion = "1.1.0";
        private bool _isSettingsOpen;
        private bool _isLogOpen;
        private bool _isTurbineSpecsOpen;
        private bool _isCommandPaletteOpen;

        public ActiveScreen CurrentScreen
        {
            get => _currentScreen;
            set => SetProperty(ref _currentScreen, value);
        }

        public bool IsNavOpen
        {
            get => _isNavOpen;
            set => SetProperty(ref _isNavOpen, value);
        }

        public string DatasetTitle
        {
            get => _datasetTitle;
            set => SetProperty(ref _datasetTitle, value);
        }

        public bool HasDataset
        {
            get => _hasDataset;
            set => SetProperty(ref _hasDataset, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public bool IsBackendConnected
        {
            get => _isBackendConnected;
            set => SetProperty(ref _isBackendConnected, value);
        }

        public string CurrentFolderPath
        {
            get => _currentFolderPath;
            set => SetProperty(ref _currentFolderPath, value);
        }

        public string Verdict
        {
            get => _verdict;
            set => SetProperty(ref _verdict, value);
        }

        public string AppVersion
        {
            get => _appVersion;
            set => SetProperty(ref _appVersion, value);
        }

        public bool IsSettingsOpen
        {
            get => _isSettingsOpen;
            set => SetProperty(ref _isSettingsOpen, value);
        }

        public bool IsLogOpen
        {
            get => _isLogOpen;
            set => SetProperty(ref _isLogOpen, value);
        }

        public bool IsTurbineSpecsOpen
        {
            get => _isTurbineSpecsOpen;
            set => SetProperty(ref _isTurbineSpecsOpen, value);
        }

        public bool IsCommandPaletteOpen
        {
            get => _isCommandPaletteOpen;
            set => SetProperty(ref _isCommandPaletteOpen, value);
        }

        public AppSettings Settings => _settings;
        public string BackendBaseUrl => _backend.BaseUrl;

        public SetupViewModel Setup { get; }
        public ProgressViewModel Progress { get; }
        public ResultsViewModel Results { get; }
        public SettingsViewModel SettingsVm { get; }

        public ICommand ToggleNavCommand { get; }
        public ICommand NewAnalysisCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand CloseSettingsCommand { get; }
        public ICommand OpenLogCommand { get; }
        public ICommand CloseLogCommand { get; }
        public ICommand OpenTurbineSpecsCommand { get; }
        public ICommand CloseTurbineSpecsCommand { get; }
        public ICommand OpenCommandPaletteCommand { get; }
        public ICommand CloseCommandPaletteCommand { get; }
        public ICommand OpenFolderInExplorerCommand { get; }
        public ICommand SwitchToResultsCommand { get; }
        public ICommand SwitchToSetupCommand { get; }

        public MainViewModel(ILinBenchApiClient? api = null, IPythonBackendManager? backend = null)
        {
            _settings = AppSettings.Load();
            _api = api ?? new LinBenchApiClient($"http://127.0.0.1:{_settings.PythonBackendPort}");
            _backend = backend ?? new PythonBackendManager(_api);

            Setup = new SetupViewModel(_api, OnRunRequestedAsync);
            Progress = new ProgressViewModel(_api, OnOpenResults, OnBackToSetup);
            // NEW: event-driven completion instead of the old MonitorLoadingCompletionAsync() loop
            Progress.OpenLoopReady += OnOpenLoopReady;
            Progress.Finished += OnAnalysisFinished;
            Results = new ResultsViewModel(_api);
            SettingsVm = new SettingsViewModel(_settings, OnSettingsSaved);

            ToggleNavCommand = new RelayCommand(() => IsNavOpen = !IsNavOpen);
            NewAnalysisCommand = new RelayCommand(OnBackToSetup);
            OpenSettingsCommand = new RelayCommand(() => IsSettingsOpen = true);
            CloseSettingsCommand = new RelayCommand(() => IsSettingsOpen = false);
            OpenLogCommand = new RelayCommand(() => IsLogOpen = true);
            CloseLogCommand = new RelayCommand(() => IsLogOpen = false);
            OpenTurbineSpecsCommand = new RelayCommand(() => IsTurbineSpecsOpen = true);
            CloseTurbineSpecsCommand = new RelayCommand(() => IsTurbineSpecsOpen = false);
            OpenCommandPaletteCommand = new RelayCommand(() => IsCommandPaletteOpen = true);
            CloseCommandPaletteCommand = new RelayCommand(() => IsCommandPaletteOpen = false);
            OpenFolderInExplorerCommand = new AsyncRelayCommand(async () =>
            {
                if (!string.IsNullOrEmpty(CurrentFolderPath))
                    await _api.OpenFolderInExplorerAsync(CurrentFolderPath);
            });
            SwitchToResultsCommand = new RelayCommand(OnOpenResults);
            SwitchToSetupCommand = new RelayCommand(OnBackToSetup);

            // CHANGED: backend output arrives on a worker thread -> marshal to UI, and also show it in the
            // analysis log so Python tracebacks are visible while a run is in progress.
            _backend.OutputReceived += (s, msg) => Ui.Post(() =>
            {
                StatusText = msg;
                if (Progress.IsPolling) Progress.AddLocalLog("backend", msg);
            });
            _backend.ErrorReceived += (s, err) => Ui.Post(() =>
            {
                StatusText = $"Backend: {err}";
                if (Progress.IsPolling) Progress.AddLocalLog("backend", err);
            });
            // FIX: raw python output (warnings, per-request tracebacks) goes to the analysis log only –
            // it no longer replaces the status bar text, where it looked like the backend had crashed.
            _backend.BackendLine += (s, line) =>
            {
                if (Progress.IsPolling) Progress.AddLocalLog("python", line);
            };
        }

        /// <summary>NEW: start (or reconnect to) the Python backend using the current settings.</summary>
        private async Task<bool> ConnectBackendAsync(CancellationToken ct = default)
        {
            bool ok;
            if (_settings.AutoStartPythonBackend)
            {
                ok = await _backend.EnsureRunningAsync(
                    _settings.PythonBackendPort,
                    _settings.PythonExecutablePath,
                    ct);
            }
            else
            {
                _api.BaseUrl = $"http://127.0.0.1:{_settings.PythonBackendPort}";
                var ping = await _api.PingAsync(ct);
                ok = ping != null && ping.Ok;
                if (!ok)
                {
                    // FIX: a saved wpf_settings.json with auto-start OFF meant the app only worked after
                    // start_backend.bat was run by hand. Nothing is answering, so start it anyway.
                    ok = await _backend.EnsureRunningAsync(
                        _settings.PythonBackendPort,
                        _settings.PythonExecutablePath,
                        ct);
                }
            }
            IsBackendConnected = ok;
            return ok;
        }

        public async Task InitializeAsync(CancellationToken ct = default)
        {
            StatusText = "Connecting to LinBench Python backend...";
            await ConnectBackendAsync(ct);

            if (IsBackendConnected)
            {
                StatusText = "Ready";
                var ver = await _api.GetVersionAsync(ct);
                if (ver != null && !string.IsNullOrEmpty(ver.Version)) AppVersion = ver.Version;
                await Setup.LoadRecentAsync();
                Setup.RefreshInspect(); // NEW: scan a folder the user may have typed before the backend was ready
            }
            else
            {
                StatusText = "Python backend not connected – see %USERPROFILE%\\.linbench\\wpf_backend.log, or set the Python path in Settings.";
            }

            StartHealthCheck();
        }

        // ------------------------------------------------------------------ FIX: keep checking the connection
        // Before, the connection was checked ONCE at start-up. If the backend was not answering at that moment
        // (start_backend.bat started after the app, numpy/scipy still importing, first start slower than the
        // timeout) the status stayed "not connected" for ever although the backend was running fine.
        private System.Windows.Threading.DispatcherTimer? _healthTimer;
        private bool _healthBusy;
        private int _healthFailures;
        private DateTime _lastAutoStart = DateTime.MinValue;

        private void StartHealthCheck()
        {
            if (_healthTimer != null) return;
            _healthTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _healthTimer.Tick += async (s, e) => await HealthTickAsync();
            _healthTimer.Start();
        }

        private async Task HealthTickAsync()
        {
            if (_healthBusy) return;
            _healthBusy = true;
            try
            {
                var ping = await _api.PingAsync();
                if (ping != null && ping.Ok)
                {
                    _healthFailures = 0;
                    if (!IsBackendConnected)
                    {
                        IsBackendConnected = true;
                        StatusText = $"Connected to LinBench backend at {_api.BaseUrl}";
                        var ver = await _api.GetVersionAsync();
                        if (ver != null && !string.IsNullOrEmpty(ver.Version)) AppVersion = ver.Version;
                        await Setup.LoadRecentAsync();
                        if (Setup.OperatingGroups.Count == 0) Setup.RefreshInspect();
                    }
                    return;
                }

                // A busy backend (analysis running) can miss a ping – only react after 3 misses in a row
                if (++_healthFailures < 4 || Progress.IsPolling) return;

                if (IsBackendConnected)
                {
                    IsBackendConnected = false;
                    StatusText = "Python backend not answering – reconnecting…";
                }

                // Find it again (it may be on another port) or start it; at most every 20 s
                if ((DateTime.UtcNow - _lastAutoStart).TotalSeconds >= 20)
                {
                    _lastAutoStart = DateTime.UtcNow;
                    // FIX: never start a second backend next to one that is only busy
                    var ok = await _backend.ReconnectAsync(_settings.PythonBackendPort, _settings.PythonExecutablePath);
                    IsBackendConnected = ok;
                    if (ok)
                    {
                        _healthFailures = 0;
                        StatusText = $"Connected to LinBench backend at {_api.BaseUrl}";
                        await Setup.LoadRecentAsync();
                        if (Setup.OperatingGroups.Count == 0) Setup.RefreshInspect();
                    }
                }
            }
            catch
            {
                // never let the timer crash the app
            }
            finally
            {
                _healthBusy = false;
            }
        }

        // CHANGED: this is what the "Run analysis" button finally calls (through SetupViewModel.RunAnalysisCommand).
        // Flow: make sure backend is up -> POST /api/load -> switch to Progress -> poll /api/progress
        //       -> on open_done/done: GET /api/summary -> fill Results -> switch to Results.
        private async Task OnRunRequestedAsync(LoadRequest req)
        {
            try
            {
                // 1) Backend must be reachable (it may have crashed or never started)
                var ping = await _api.PingAsync();
                if (ping == null || !ping.Ok)
                {
                    StatusText = "Starting LinBench Python backend...";
                    if (!await ConnectBackendAsync())
                    {
                        StatusText = "Python backend not connected. Check Settings (Python path / port).";
                        Setup.StatusMessage = "Cannot run: the Python backend is not running. " +
                                              "Check the Python path in Settings and run: python -m pip install numpy scipy";
                        return;
                    }
                }
                IsBackendConnected = true;

                // 2) Reset UI state from any previous run
                CurrentFolderPath = req.LinDir;
                HasDataset = false;
                Verdict = string.Empty;
                Progress.Reset();

                // 3) Start the analysis on the Python side
                StatusText = "Starting analysis...";
                var started = await _api.StartLoadAsync(req);
                if (!started)
                {
                    var err = _api.LastError ?? "unknown error";
                    StatusText = "Failed to start analysis on Python backend.";
                    Setup.StatusMessage = $"Backend refused the analysis request – {err}";
                    return;
                }

                // 4) Show progress and poll
                Setup.StatusMessage = "Analysis running...";
                CurrentScreen = ActiveScreen.Progress;
                StatusText = "Analysis running...";
                Progress.StartPolling();
            }
            catch (Exception ex)
            {
                StatusText = $"Run failed: {ex.Message}";
                Setup.StatusMessage = $"Run failed: {ex.Message}";
            }
        }

        // the results are published as soon as the analysis is done -> load them so "Open results" works at once
        private async void OnOpenLoopReady()
        {
            try
            {
                if (!HasDataset)
                    await LoadAndApplySummaryAsync();
            }
            catch (Exception ex)
            {
                Progress.AddLocalLog("wpf", $"Could not load the summary: {ex.Message}");
            }
        }

        // NEW: run finished (success or error)
        private async void OnAnalysisFinished(bool success)
        {
            try
            {
                if (!success)
                {
                    StatusText = "Analysis failed – see the error on the progress screen.";
                    Setup.StatusMessage = Progress.ErrorMessage ?? "Analysis failed.";
                    return;
                }

                // Always reload the final summary
                var ok = await LoadAndApplySummaryAsync();
                if (ok)
                {
                    StatusText = "Analysis complete";
                    Setup.StatusMessage = "Analysis complete.";
                    if (CurrentScreen == ActiveScreen.Progress)
                        OnOpenResults(); // auto-navigate to the Results screen
                    _ = Setup.LoadRecentAsync();
                }
                else
                {
                    Progress.ErrorMessage = $"Analysis finished but results could not be loaded. {_api.LastError}";
                    StatusText = "Could not load results from backend.";
                }
            }
            catch (Exception ex)
            {
                Progress.ErrorMessage = $"Failed to load results: {ex.Message}";
            }
        }

        // CHANGED: returns true/false so callers know whether results were loaded
        private async Task<bool> LoadAndApplySummaryAsync()
        {
            var summary = await _api.GetSummaryAsync();
            if (summary == null)
            {
                Progress.AddLocalLog("wpf", _api.LastError ?? "GET /api/summary returned nothing.");
                return false;
            }

            Results.LoadSummary(summary);
            HasDataset = true;
            Progress.ResultsReady = true;

            // backend "info" has "title" (from the .lin header) and "name"
            var turbine = summary.InfoString("name") ?? summary.InfoString("title") ?? "Model";
            var nOps = summary.OperatingPoints.Count;
            DatasetTitle = $"{turbine} ({nOps} operating points)";

            Verdict = "Open loop analysis complete";
            return true;
        }

        private void OnOpenResults()
        {
            if (!HasDataset) return; // nothing to show yet
            CurrentScreen = ActiveScreen.Results;
            StatusText = "Viewing results";
        }

        private void OnBackToSetup()
        {
            Progress.StopPolling();
            CurrentScreen = ActiveScreen.Setup;
            StatusText = "Ready";
        }

        private void OnSettingsSaved(AppSettings newSettings)
        {
            _settings = newSettings;
            // NEW: point the client at the (possibly changed) port; the next Run reconnects/starts the backend if needed
            _api.BaseUrl = $"http://127.0.0.1:{_settings.PythonBackendPort}";
            IsSettingsOpen = false;
        }

        public void Dispose()
        {
            _healthTimer?.Stop();
            Progress.StopPolling();
            _backend.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
