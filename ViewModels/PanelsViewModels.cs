using LinBench.Core.Models;
using LinBench.Core.Services;
using System.Collections.ObjectModel;

namespace LinBench.Core.ViewModels
{
    internal static class ChannelDefaults
    {
        /// <summary>First non-null index among the given std_inputs/std_outputs keys, else 0.</summary>
        public static int Pick(Dictionary<string, int?>? map, params string[] keys)
        {
            if (map != null)
                foreach (var k in keys)
                    if (map.TryGetValue(k, out var v) && v.HasValue) return v.Value;
            return 0;
        }
    }

    public class OperatingPointsPanelViewModel : ViewModelBase
    {
        public ObservableCollection<OpSummary> OperatingPoints { get; } = new();

        private OpSummary? _selectedPoint;
        public OpSummary? SelectedPoint
        {
            get => _selectedPoint;
            set => SetProperty(ref _selectedPoint, value);
        }

        public void LoadData(SummaryResult summary)
        {
            OperatingPoints.Clear();
            foreach (var op in summary.OperatingPoints)
            {
                OperatingPoints.Add(op);
            }
            SelectedPoint = OperatingPoints.FirstOrDefault();
        }
    }

    public class CampbellPanelViewModel : ViewModelBase
    {
        private readonly ILinBenchApiClient _api;
        private bool _isClosedLoop;
        private bool _includeAllTracks;
        private CampbellData? _data;

        public bool IsClosedLoop
        {
            get => _isClosedLoop;
            set
            {
                if (SetProperty(ref _isClosedLoop, value))
                {
                    _ = RefreshAsync();
                }
            }
        }

        public bool IncludeAllTracks
        {
            get => _includeAllTracks;
            set
            {
                if (SetProperty(ref _includeAllTracks, value))
                {
                    _ = RefreshAsync();
                }
            }
        }

        public ObservableCollection<CampbellTrack> Tracks { get; } = new();
        public ObservableCollection<double> WindSpeeds { get; } = new();

        public CampbellPanelViewModel(ILinBenchApiClient api)
        {
            _api = api;
        }

        public void LoadData(CampbellData? data)
        {
            _data = data;
            OpenLoop = data;            // NEW: kept for the grey open-loop lines in the closed-loop Campbell diagram
            _isClosedLoop = false;
            OnPropertyChanged(nameof(IsClosedLoop));
            ApplyData();
        }

        /// <summary>NEW: the open-loop Campbell data of the current analysis (from /api/summary).</summary>
        public CampbellData? OpenLoop { get; private set; }

        /// <summary>NEW: the data currently shown (open or closed loop, with or without aero/hydro states).</summary>
        public CampbellData? Data => _data;

        private bool _isRefreshing;
        /// <summary>NEW: true while /api/campbell is being asked again (loop or state set changed).</summary>
        public bool IsRefreshing
        {
            get => _isRefreshing;
            private set => SetProperty(ref _isRefreshing, value);
        }

        /// <summary>1P rotor frequency line (Hz) per wind speed, for the Campbell plot.</summary>
        public List<RotorHarmonic> Rotor => _data?.Rotor ?? new List<RotorHarmonic>();

        public async Task RefreshAsync()
        {
            IsRefreshing = true;
            try
            {
                var res = await _api.GetCampbellAsync(IncludeAllTracks, IsClosedLoop);
                if (res != null)
                {
                    _data = res;
                    ApplyData();
                }
            }
            finally
            {
                IsRefreshing = false;
            }
        }

        private void ApplyData()
        {
            if (_data == null) return;
            WindSpeeds.Clear();
            foreach (var w in _data.Winds) WindSpeeds.Add(w);

            // CHANGED: backend sends tracks[].points[] (wind, fn, zeta); open/closed come from separate requests
            Tracks.Clear();
            foreach (var t in _data.Tracks)
            {
                Tracks.Add(t);
            }
        }
    }

    public class ModesPanelViewModel : ViewModelBase
    {
        private readonly ILinBenchApiClient _api;
        private int _currentOp = -1;

        public ObservableCollection<ModeItem> Modes { get; } = new();

        private ModeItem? _selectedMode;
        public ModeItem? SelectedMode
        {
            get => _selectedMode;
            set => SetProperty(ref _selectedMode, value);
        }

        public ModesPanelViewModel(ILinBenchApiClient api)
        {
            _api = api;
        }

        /// <summary>NEW: forget the cached operating point (call after a new analysis).</summary>
        public void Invalidate() => _currentOp = -1;

        public async Task LoadForOpAsync(int opIndex)
        {
            if (_currentOp == opIndex && Modes.Count > 0) return;
            _currentOp = opIndex;

            var modes = await _api.GetModesAsync(opIndex);
            Modes.Clear();
            foreach (var m in modes)
            {
                Modes.Add(m);
            }
            SelectedMode = Modes.FirstOrDefault();
        }
    }

    public class TransferFunctionPanelViewModel : ViewModelBase
    {
        private readonly ILinBenchApiClient _api;
        private int _currentOp;
        private ChannelInfo? _selectedInput;
        private ChannelInfo? _selectedOutput;
        private TfResult? _tfResult;
        private bool _isLoading;
        private string _error = string.Empty;
        private int _request;          // CHANGED: only the newest reply is shown (fast input / output changes)
        private bool _batch;           // CHANGED: SetPair sets both channels with one request

        public ObservableCollection<ChannelInfo> Inputs { get; } = new();
        public ObservableCollection<ChannelInfo> Outputs { get; } = new();

        public ChannelInfo? SelectedInput
        {
            get => _selectedInput;
            set
            {
                if (SetProperty(ref _selectedInput, value) && !_batch) _ = CalculateAsync();
            }
        }

        public ChannelInfo? SelectedOutput
        {
            get => _selectedOutput;
            set
            {
                if (SetProperty(ref _selectedOutput, value) && !_batch) _ = CalculateAsync();
            }
        }

        public TfResult? Result
        {
            get => _tfResult;
            set => SetProperty(ref _tfResult, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        /// <summary>Last backend error ('' = none).</summary>
        public string Error
        {
            get => _error;
            set => SetProperty(ref _error, value);
        }

        public TransferFunctionPanelViewModel(ILinBenchApiClient api)
        {
            _api = api;
        }

        // CHANGED: like the original, inputs without the MBC cos / sin parts, outputs without rotating-frame channels
        public void SetupChannels(ChannelCollection? channels, Dictionary<string, int?>? stdIn, Dictionary<string, int?>? stdOut)
        {
            _batch = true;
            try
            {
                Inputs.Clear();
                Outputs.Clear();
                if (channels != null)
                {
                    foreach (var u in channels.Inputs.Where(c => c.Role != "cos" && c.Role != "sin")) Inputs.Add(u);
                    foreach (var y in channels.Outputs.Where(c => c.Role != "rot")) Outputs.Add(y);
                }

                int inIdx = ChannelDefaults.Pick(stdIn, "pitch", "torque", "wind");
                int outIdx = ChannelDefaults.Pick(stdOut, "genspeed", "rotspeed");

                SelectedInput = Inputs.FirstOrDefault(c => c.Index == inIdx) ?? Inputs.FirstOrDefault();
                SelectedOutput = Outputs.FirstOrDefault(c => c.Index == outIdx) ?? Outputs.FirstOrDefault();
            }
            finally { _batch = false; }
            _ = CalculateAsync();
        }

        /// <summary>NEW: Quick pick (e.g. Pitch → Gen speed): both channels, one request.</summary>
        public void SetPair(int inputIndex, int outputIndex)
        {
            var i = Inputs.FirstOrDefault(c => c.Index == inputIndex);
            var o = Outputs.FirstOrDefault(c => c.Index == outputIndex);
            if (i == null || o == null) return;
            _batch = true;
            try { SelectedInput = i; SelectedOutput = o; }
            finally { _batch = false; }
            _ = CalculateAsync();
        }

        public void SetOp(int opIndex)
        {
            _currentOp = opIndex;
            _ = CalculateAsync();
        }

        public async Task CalculateAsync()
        {
            if (SelectedInput == null || SelectedOutput == null) return;
            // CHANGED: send the backend's own channel index ("i")
            int inIdx = SelectedInput.Index;
            int outIdx = SelectedOutput.Index;
            int id = ++_request;

            IsLoading = true;
            try
            {
                var req = new TfRequest { OpIndex = _currentOp, InputIndex = inIdx, OutputIndex = outIdx };
                var r = await _api.CalculateTfAsync(req);
                if (id != _request) return;
                Error = string.Empty;
                Result = r;
            }
            catch (Exception ex)
            {
                if (id == _request) Error = ex.Message;
            }
            finally
            {
                if (id == _request) IsLoading = false;
            }
        }
    }

    public class TimeResponsePanelViewModel : ViewModelBase
    {
        private readonly ILinBenchApiClient _api;
        private int _currentOp;
        private ChannelInfo? _selectedInput;
        private ChannelInfo? _selectedOutput;
        private string _kind = "step"; // step, impulse
        private double _amplitude = 1.0;
        private double _duration = 20.0;
        private TimeResult? _timeResult;
        private bool _isLoading;

        public ObservableCollection<ChannelInfo> Inputs { get; } = new();
        public ObservableCollection<ChannelInfo> Outputs { get; } = new();

        public ChannelInfo? SelectedInput
        {
            get => _selectedInput;
            set
            {
                if (SetProperty(ref _selectedInput, value)) _ = CalculateAsync();
            }
        }

        public ChannelInfo? SelectedOutput
        {
            get => _selectedOutput;
            set
            {
                if (SetProperty(ref _selectedOutput, value)) _ = CalculateAsync();
            }
        }

        public string Kind
        {
            get => _kind;
            set
            {
                if (SetProperty(ref _kind, value)) _ = CalculateAsync();
            }
        }

        public double Amplitude
        {
            get => _amplitude;
            set
            {
                if (SetProperty(ref _amplitude, value)) _ = CalculateAsync();
            }
        }

        public double Duration
        {
            get => _duration;
            set
            {
                if (SetProperty(ref _duration, value)) _ = CalculateAsync();
            }
        }

        public TimeResult? Result
        {
            get => _timeResult;
            set => SetProperty(ref _timeResult, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public TimeResponsePanelViewModel(ILinBenchApiClient api)
        {
            _api = api;
        }

        // CHANGED: std_inputs/std_outputs values can be null; output key is "genspeed" (not "omega")
        public void SetupChannels(ChannelCollection? channels, Dictionary<string, int?>? stdIn, Dictionary<string, int?>? stdOut)
        {
            Inputs.Clear();
            Outputs.Clear();
            if (channels != null)
            {
                foreach (var u in channels.Inputs) Inputs.Add(u);
                foreach (var y in channels.Outputs) Outputs.Add(y);
            }

            int inIdx = ChannelDefaults.Pick(stdIn, "pitch", "torque", "wind");
            int outIdx = ChannelDefaults.Pick(stdOut, "genspeed", "rotspeed");

            SelectedInput = Inputs.FirstOrDefault(c => c.Index == inIdx) ?? Inputs.FirstOrDefault();
            SelectedOutput = Outputs.FirstOrDefault(c => c.Index == outIdx) ?? Outputs.FirstOrDefault();
        }

        public void SetOp(int opIndex)
        {
            _currentOp = opIndex;
            _ = CalculateAsync();
        }

        public async Task CalculateAsync()
        {
            if (SelectedInput == null || SelectedOutput == null) return;
            // CHANGED: send the backend's own channel index ("i")
            int inIdx = SelectedInput.Index;
            int outIdx = SelectedOutput.Index;

            IsLoading = true;
            try
            {
                var req = new TimeRequest
                {
                    OpIndex = _currentOp,
                    InputIndex = inIdx,
                    OutputIndex = outIdx,
                    Kind = Kind,
                    Amplitude = Amplitude,
                    Duration = Duration
                };
                Result = await _api.CalculateTimeAsync(req);
            }
            finally
            {
                IsLoading = false;
            }
        }
    }

    public class StructurePanelViewModel : ViewModelBase
    {
        private readonly ILinBenchApiClient _api;
        private int _currentOp;
        private StructureResult? _result;
        private bool _isLoading;

        public StructureResult? Result
        {
            get => _result;
            set => SetProperty(ref _result, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public StructurePanelViewModel(ILinBenchApiClient api)
        {
            _api = api;
        }

        // NEW: the backend needs at least one input and one output (defaults: pitch -> generator speed)
        public int InputIndex { get; set; }
        public int OutputIndex { get; set; }

        public async Task LoadForOpAsync(int opIndex)
        {
            _currentOp = opIndex;
            IsLoading = true;
            try
            {
                Result = await _api.CalculateStructureAsync(opIndex,
                    new List<int> { InputIndex }, new List<int> { OutputIndex });
            }
            finally
            {
                IsLoading = false;
            }
        }
    }

    public class StabilitySummaryPanelViewModel : ViewModelBase
    {
        public ObservableCollection<ClosedLoopRow> ClosedRows { get; } = new();

        public void LoadData(SummaryResult summary)
        {
            ClosedRows.Clear();
            if (summary.Closed != null)
            {
                foreach (var r in summary.Closed)
                {
                    ClosedRows.Add(r);
                }
            }
        }
    }

    public class ClosedLoopDetailPanelViewModel : ViewModelBase
    {
        private readonly ILinBenchApiClient _api;
        private int _currentOp;
        private ClosedLoopDetail? _detail;
        private bool _isLoading;

        public ClosedLoopDetail? Detail
        {
            get => _detail;
            set => SetProperty(ref _detail, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public ClosedLoopDetailPanelViewModel(ILinBenchApiClient api)
        {
            _api = api;
        }

        public async Task LoadForOpAsync(int opIndex)
        {
            _currentOp = opIndex;
            IsLoading = true;
            try
            {
                Detail = await _api.GetClosedAsync(opIndex);
            }
            finally
            {
                IsLoading = false;
            }
        }
    }

    public class GainStudyPanelViewModel : ViewModelBase
    {
        private readonly ILinBenchApiClient _api;
        private int _currentOp;
        private GainMapResult? _mapResult;
        private bool _isLoading;

        public GainMapResult? MapResult
        {
            get => _mapResult;
            set => SetProperty(ref _mapResult, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public GainStudyPanelViewModel(ILinBenchApiClient api)
        {
            _api = api;
        }

        public async Task LoadForOpAsync(int opIndex)
        {
            _currentOp = opIndex;
            IsLoading = true;
            try
            {
                var req = new GainMapRequest { OpIndex = opIndex, Pair = "pi", Lo = 0.25, Hi = 4.0, N = 25 };
                MapResult = await _api.CalculateGainMapAsync(req);
            }
            finally
            {
                IsLoading = false;
            }
        }
    }
}
