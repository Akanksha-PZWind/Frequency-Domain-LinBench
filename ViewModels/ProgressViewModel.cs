using LinBench.Core.Models;
using LinBench.Core.Services;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace LinBench.Core.ViewModels
{
    public class ProgressViewModel : ViewModelBase
    {
        private readonly ILinBenchApiClient _api;
        private readonly Action _onOpenResults;
        private readonly Action _onBack;
        private readonly RelayCommand _openResultsCommand;

        private string _stage = "starting";
        private double _pct = 0;                 // CHANGED: int -> double (matches ProgressUpdate.Percent)
        private string _msg = "Initialising analysis...";
        private double _elapsed;
        private bool _openDone;
        private bool _isDone;
        private bool _resultsReady;
        private string? _errorMessage;
        private string _mathExplanation = string.Empty;
        private CancellationTokenSource? _pollCts;

        /// <summary>NEW: raised once when the backend reports open_done (open-loop results available).</summary>
        public event Action? OpenLoopReady;

        /// <summary>NEW: raised once when the run finishes. Argument = true on success, false on error/connection loss.</summary>
        public event Action<bool>? Finished;

        public string Stage
        {
            get => _stage;
            set => SetProperty(ref _stage, value);
        }

        public double Percent
        {
            get => _pct;
            set => SetProperty(ref _pct, Math.Clamp(value, 0, 100));
        }

        public string Message
        {
            get => _msg;
            set => SetProperty(ref _msg, value);
        }

        public double Elapsed
        {
            get => _elapsed;
            set
            {
                if (SetProperty(ref _elapsed, value))
                    OnPropertyChanged(nameof(ElapsedFormatted));
            }
        }

        public string ElapsedFormatted => $"{Elapsed:0.0} s";

        public bool OpenDone
        {
            get => _openDone;
            set => SetProperty(ref _openDone, value);
        }

        public bool IsDone
        {
            get => _isDone;
            set => SetProperty(ref _isDone, value);
        }

        /// <summary>NEW: set by MainViewModel once /api/summary has been loaded into the Results screen.</summary>
        public bool ResultsReady
        {
            get => _resultsReady;
            set
            {
                if (SetProperty(ref _resultsReady, value))
                    _openResultsCommand.RaiseCanExecuteChanged();
            }
        }

        public string? ErrorMessage
        {
            get => _errorMessage;
            set
            {
                if (SetProperty(ref _errorMessage, value))
                    OnPropertyChanged(nameof(HasError));
            }
        }

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        public bool IsPolling => _pollCts != null;

        public string MathExplanation
        {
            get => _mathExplanation;
            set => SetProperty(ref _mathExplanation, value);
        }

        public ObservableCollection<LogEntry> Logs { get; } = new();

        public ICommand OpenResultsCommand => _openResultsCommand;
        public ICommand BackCommand { get; }

        public ProgressViewModel(ILinBenchApiClient api, Action onOpenResults, Action onBack)
        {
            _api = api;
            _onOpenResults = onOpenResults;
            _onBack = onBack;

            // CHANGED: enabled only when results are actually loaded (was IsEnabled="{Binding OpenDone}" in XAML)
            _openResultsCommand = new RelayCommand(() => _onOpenResults(), () => ResultsReady);
            BackCommand = new RelayCommand(() =>
            {
                StopPolling();
                _onBack();
            });
        }

        /// <summary>Resets the screen for a new run (call BEFORE StartPolling).</summary>
        public void Reset()
        {
            StopPolling();
            Logs.Clear();
            Percent = 0;
            Stage = "starting";
            Message = "Starting analysis...";
            ErrorMessage = null;
            OpenDone = false;
            IsDone = false;
            ResultsReady = false;
            Elapsed = 0;
            UpdateMathExplanation("starting");
        }

        /// <summary>NEW: add a line from the WPF side (e.g. backend stderr, connection problems).</summary>
        public void AddLocalLog(string stage, string text)
        {
            Ui.Post(() => Logs.Add(new LogEntry { Time = Elapsed, Stage = stage, Text = text }));
        }

        // CHANGED: the loop now runs on the UI thread (no Task.Run). Before, Logs.Add(...) ran on a worker
        // thread, WPF threw, the exception was swallowed by catch{} BEFORE the Done check -> the loop never
        // ended and the UI never updated.
        public async void StartPolling()
        {
            StopPolling();
            var cts = new CancellationTokenSource();
            _pollCts = cts;
            var token = cts.Token;

            int since = 0;
            int failures = 0;
            bool openRaised = false;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var p = await _api.GetProgressAsync(since, token);
                    if (token.IsCancellationRequested) break;

                    if (p == null)
                    {
                        // ~15 s without a valid answer -> give up
                        if (++failures >= 50)
                        {
                            ErrorMessage = $"Lost connection to the Python backend. {_api.LastError}";
                            Finished?.Invoke(false);
                            break;
                        }
                    }
                    else
                    {
                        failures = 0;
                        Stage = p.Stage ?? Stage;
                        Percent = p.Percent;
                        Message = p.Message ?? string.Empty;
                        Elapsed = p.Elapsed;
                        OpenDone = p.OpenDone;
                        IsDone = p.Done || string.Equals(p.Stage, "done", StringComparison.OrdinalIgnoreCase);
                        ErrorMessage = p.Error;

                        UpdateMathExplanation(Stage);

                        if (p.Log != null && p.Log.Count > 0)
                        {
                            foreach (var l in p.Log)
                                Logs.Add(l);
                        }
                        // CHANGED: advance even if the backend omits log_n
                        since = p.LogCount > 0 ? p.LogCount : since + (p.Log?.Count ?? 0);

                        if (OpenDone && !openRaised)
                        {
                            openRaised = true;
                            OpenLoopReady?.Invoke();
                        }

                        if (!string.IsNullOrEmpty(p.Error))
                        {
                            Finished?.Invoke(false);
                            break;
                        }

                        if (IsDone)
                        {
                            Percent = 100;
                            Finished?.Invoke(true);
                            break;
                        }
                    }

                    await Task.Delay(300, token);
                }
            }
            catch (OperationCanceledException) { /* stopped by user */ }
            catch (Exception ex)
            {
                ErrorMessage = $"Progress polling failed: {ex.Message}";
                Finished?.Invoke(false);
            }
            finally
            {
                if (ReferenceEquals(_pollCts, cts))
                    _pollCts = null;
            }
        }

        public void StopPolling()
        {
            _pollCts?.Cancel();
            _pollCts = null;
        }

        private void UpdateMathExplanation(string stage)
        {
            MathExplanation = stage switch
            {
                "scan" => "Scanning files: Identifying OpenFAST .lin output files, parsing header metadata, and grouping azimuth files by operating wind speed.",
                "read" => "MBC Transformation (Coleman): Transforming rotating-frame blade states (triplets) to the fixed frame using multi-blade coordinate transformation T(ψ), followed by azimuth averaging over full rotor revolutions to obtain linear time-invariant (LTI) state space matrices (A, B, C, D).",
                "modes" => "Modal Eigen-Analysis: Computing eigenvalues λ = σ ± jω of the system matrix A. Extracting natural frequencies ω_n = |λ|, damping ratios ζ = -σ/|λ|, and participation factors from right and left eigenvectors to classify modes (Tower Fore-Aft, Side-Side, Drivetrain, Whirl).",
                "campbell" => "Campbell Tracking: Tracking modal frequencies and damping across wind speeds using Modal Assurance Criterion (MAC) and the Hungarian algorithm for optimal assignment.",
                "closed" => "Closed-Loop Analysis: Coupling the OpenFAST linear model with the ROSCO controller state space model. Calculating loop transfer function L(s), sensitivity S(s), complementary sensitivity T(s), gain/phase margins, disk margins, and Nyquist winding numbers.",
                "summary" => "Publishing Results: Compiling stability metrics, mode classifications, trim operating conditions, and caching models for instantaneous access.",
                "done" => "Analysis complete. Open the results to explore Campbell diagrams, modes, transfer functions and closed-loop margins.",
                _ => "Linear state-space analysis in progress..."
            };
        }
    }
}
