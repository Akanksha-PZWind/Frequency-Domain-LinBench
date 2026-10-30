using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using LinBench.Core.Models;
using LinBench.Core.Services;

namespace LinBench.Core.ViewModels
{
    public class SetupViewModel : ViewModelBase
    {
        private readonly ILinBenchApiClient _api;

        // CHANGED: was Action<LoadRequest>. Now async so the button stays disabled while the
        // backend is being started / the /api/load request is in flight.
        private readonly Func<LoadRequest, Task> _onRun;
        private readonly AsyncRelayCommand _runCommand;

        private string _linDir = string.Empty;
        private bool _recursive;
        private bool _removeAzimuth = true;
        private bool _isInspecting;
        private string _statusMessage = "Choose a linearisation folder.";
        private string _scanMeta = string.Empty;
        private bool _suppressInspect;      // NEW: avoid re-inspect loops while auto-filling paths
        private CancellationTokenSource? _inspectCts;

        public string LinearisationDir
        {
            get => _linDir;
            set
            {
                if (SetProperty(ref _linDir, value?.Trim().Trim('"') ?? string.Empty))
                    OnInputsChanged();
            }
        }

        public bool Recursive
        {
            get => _recursive;
            set
            {
                if (SetProperty(ref _recursive, value))
                    OnInputsChanged();
            }
        }

        public bool RemoveAzimuth
        {
            get => _removeAzimuth;
            set => SetProperty(ref _removeAzimuth, value);
        }

        public bool IsInspecting
        {
            get => _isInspecting;
            set
            {
                if (SetProperty(ref _isInspecting, value))
                    _runCommand?.RaiseCanExecuteChanged();
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public string ScanMeta
        {
            get => _scanMeta;
            set => SetProperty(ref _scanMeta, value);
        }

        public ObservableCollection<OperatingGroup> OperatingGroups { get; } = new();
        public ObservableCollection<RecentFolder> RecentFolders { get; } = new();
        public ObservableCollection<string> DetectedMessages { get; } = new();

        public ICommand RunAnalysisCommand => _runCommand;
        public ICommand SelectRecentCommand { get; }
        public ICommand RescanCommand { get; }   // NEW: optional "Rescan" button

        public SetupViewModel(ILinBenchApiClient api, Func<LoadRequest, Task> onRun)
        {
            _api = api;
            _onRun = onRun;

            _runCommand = new AsyncRelayCommand(ExecuteRunAsync, CanExecuteRun);
            SelectRecentCommand = new RelayCommand(ExecuteSelectRecent);
            RescanCommand = new RelayCommand(OnInputsChanged);
            // NOTE: recent folders are loaded by MainViewModel.InitializeAsync once the backend is up.
        }

        public async Task LoadRecentAsync()
        {
            var recent = await _api.GetRecentAsync();
            RecentFolders.Clear();
            foreach (var r in recent)
                RecentFolders.Add(r);
        }

        /// <summary>NEW: called by MainViewModel after the backend connects, so a folder typed earlier is scanned.</summary>
        public void RefreshInspect() => OnInputsChanged();

        private void ExecuteSelectRecent(object? param)
        {
            if (param is not RecentFolder r) return;

            _suppressInspect = true;
            try
            {
                LinearisationDir = r.LinDir;
            }
            finally
            {
                _suppressInspect = false;
            }
            OnInputsChanged();
        }

        // CHANGED: no more Task.Run. The whole inspect runs on the UI thread (HttpClient is async, so the UI
        // does not freeze). Before, OperatingGroups was modified from a worker thread -> WPF threw
        // NotSupportedException -> the list stayed empty -> CanExecuteRun() was false -> button never enabled.
        private async void OnInputsChanged()
        {
            if (_suppressInspect) return;

            _runCommand.RaiseCanExecuteChanged();

            if (string.IsNullOrWhiteSpace(LinearisationDir) || !Directory.Exists(LinearisationDir))
            {
                _inspectCts?.Cancel();
                OperatingGroups.Clear();
                DetectedMessages.Clear();
                ScanMeta = string.Empty;
                StatusMessage = string.IsNullOrWhiteSpace(LinearisationDir)
                    ? "Choose a linearisation folder."
                    : "Folder does not exist.";
                return;
            }

            _inspectCts?.Cancel();
            var cts = new CancellationTokenSource();
            _inspectCts = cts;
            var token = cts.Token;

            try
            {
                await Task.Delay(300, token); // debounce typing
                IsInspecting = true;
                StatusMessage = "Inspecting folder...";

                var req = new InspectRequest
                {
                    LinDir = LinearisationDir,
                    Recursive = Recursive
                };

                var res = await _api.InspectAsync(req, token);
                if (token.IsCancellationRequested) return;

                if (res == null)
                {
                    OperatingGroups.Clear();
                    ScanMeta = string.Empty;
                    StatusMessage = $"Scan failed – {_api.LastError ?? "no response from the Python backend"}";
                    return;
                }

                DetectedMessages.Clear();
                foreach (var m in res.Messages ?? new System.Collections.Generic.List<string>())
                    DetectedMessages.Add(m);

                OperatingGroups.Clear();
                if (res.Scan?.Groups != null && res.Scan.Groups.Count > 0)
                {
                    foreach (var g in res.Scan.Groups)
                        OperatingGroups.Add(g);

                    var nFiles = res.Scan.FileCount;
                    var nOps = res.Scan.OperatingPointCount > 0 ? res.Scan.OperatingPointCount : OperatingGroups.Count;
                    var wr = res.Scan.WindRange;
                    var rangeStr = wr != null && wr.Count >= 2 ? $" ({wr[0]:0.#} - {wr[1]:0.#} m/s)" : "";
                    ScanMeta = $"{nFiles} .lin files · {nOps} operating points{rangeStr}";
                    StatusMessage = $"Ready to run analysis across {nOps} operating points.";
                    if (res.Scan.Skipped.Count > 0)
                        StatusMessage += $"  ({res.Scan.Skipped.Count} file(s) skipped: {string.Join(", ", res.Scan.Skipped.Take(3).Select(x => x.FirstOrDefault()))})";
                    foreach (var w in res.Scan.Warnings)
                        DetectedMessages.Add("⚠ " + w);
                }
                else if (res.Messages != null && res.Messages.Count > 0)
                {
                    // e.g. "Folder scan failed: ..." from the backend
                    ScanMeta = "Scan failed";
                    StatusMessage = string.Join("  ", res.Messages);
                }
                else
                {
                    ScanMeta = "No .lin files found";
                    StatusMessage = Recursive
                        ? "No OpenFAST .lin files found in the selected folder or its sub-folders."
                        : "No OpenFAST .lin files found. Tick 'Include sub-folders' if they are nested.";
                }
            }
            catch (OperationCanceledException) { /* superseded by a newer inspect */ }
            catch (Exception ex)
            {
                StatusMessage = $"Scan error: {ex.Message}";
            }
            finally
            {
                if (ReferenceEquals(_inspectCts, cts))
                    IsInspecting = false;
                _runCommand.RaiseCanExecuteChanged();
            }
        }

        private bool CanExecuteRun()
        {
            return !IsInspecting &&
                   !string.IsNullOrWhiteSpace(LinearisationDir) &&
                   Directory.Exists(LinearisationDir) &&
                   OperatingGroups.Count > 0;
        }

        private async Task ExecuteRunAsync()
        {
            var req = new LoadRequest
            {
                LinDir = LinearisationDir,
                Recursive = Recursive,
                RemoveAzimuth = RemoveAzimuth,
                Cache = true
            };

            StatusMessage = "Sending analysis request to the Python backend...";
            await _onRun(req);
        }
    }
}
