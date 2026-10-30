using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using LinBench.Core.Models;
using LinBench.Core.Services;

namespace LinBench.Core.ViewModels
{
    /// <summary>Operating points page: the trim values and azimuth-file checks of every wind speed (from /api/summary).</summary>
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

    /// <summary>Campbell diagram page (open loop): tracked modes across wind speeds, and the
    /// Campbell visualization (mode-shape animation of the point clicked in the diagram).</summary>
    public class CampbellPanelViewModel : ViewModelBase
    {
        private readonly ILinBenchApiClient _api;
        private bool _includeAllTracks;
        private CampbellData? _data;
        private CampbellData? _structural;

        /// <summary>true = also the hydrodynamic / aerodynamic state modes (/api/campbell?all=1).</summary>
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
            ClearModeShape();               // a new analysis: nothing selected
            _structural = data;
            _data = data;
            if (_includeAllTracks)
            {
                _ = RefreshAsync();          // keep the aero/hydro choice for a new analysis
                return;
            }
            ApplyData();
        }

        /// <summary>The data currently shown (with or without aero/hydro states).</summary>
        public CampbellData? Data => _data;

        private bool _isRefreshing;
        /// <summary>true while /api/campbell is being asked again (state set changed).</summary>
        public bool IsRefreshing
        {
            get => _isRefreshing;
            private set => SetProperty(ref _isRefreshing, value);
        }

        /// <summary>1P rotor frequency line (Hz) per wind speed, for the Campbell plot.</summary>
        public List<RotorHarmonic> Rotor => _data?.Rotor ?? new List<RotorHarmonic>();

        public async Task RefreshAsync()
        {
            if (!_includeAllTracks && _structural != null)
            {
                // the structural modes come with /api/summary: no request needed
                _data = _structural;
                ApplyData();
                return;
            }
            IsRefreshing = true;
            try
            {
                var res = await _api.GetCampbellAsync(IncludeAllTracks);
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

        // ------------------------------------------------------------------ NEW: Campbell visualization

        private CampbellTrack? _selectedTrack;
        private CampbellPoint? _selectedPoint;
        private ModeShapeResult? _modeShape;
        private bool _isLoadingShape;
        private string _shapeError = string.Empty;
        private double _shapeScale = 0.15;
        private int _shapeRequest;

        /// <summary>Tracked mode of the clicked point (null = nothing selected yet).</summary>
        public CampbellTrack? SelectedTrack { get => _selectedTrack; private set => SetProperty(ref _selectedTrack, value); }
        /// <summary>The clicked point: operating point (Op) and eigenvalue index (K).</summary>
        public CampbellPoint? SelectedPoint { get => _selectedPoint; private set => SetProperty(ref _selectedPoint, value); }
        /// <summary>Animation frames from /api/mode_shape.</summary>
        public ModeShapeResult? ModeShape { get => _modeShape; private set => SetProperty(ref _modeShape, value); }
        public bool IsLoadingShape { get => _isLoadingShape; private set => SetProperty(ref _isLoadingShape, value); }
        public string ShapeError { get => _shapeError; private set => SetProperty(ref _shapeError, value); }

        /// <summary>Largest deflection as a fraction of the tower height / blade length (rad for rotations).</summary>
        public double ShapeScale { get => _shapeScale; set => SetProperty(ref _shapeScale, value); }
        /// <summary>Turbine size entered by the user (null = ElastoDyn file next to the .lin files, else NREL 5 MW).</summary>
        public double? TowerLen { get; set; }
        public double? BladeLen { get; set; }
        public double? HubRadius { get; set; }

        /// <summary>Rank of the clicked point among all modes shown at its wind speed, lowest frequency = 0.</summary>
        private int? ModeIndexAt(CampbellPoint p)
        {
            var atOp = (_data?.Tracks ?? new List<CampbellTrack>())
                .SelectMany(t => t.Points)
                .Where(q => q.Op == p.Op && q.FnHz.HasValue)
                .OrderBy(q => q.FnHz!.Value)
                .ToList();
            int i = atOp.FindIndex(q => ReferenceEquals(q, p));
            if (i < 0 && p.FnHz.HasValue)       // same point from another copy of the data: match by frequency
                i = atOp.FindIndex(q => Math.Abs(q.FnHz!.Value - p.FnHz.Value) < 1e-9);
            return i >= 0 ? i : null;
        }

        public void ClearModeShape()
        {
            _shapeRequest++;
            SelectedTrack = null;
            SelectedPoint = null;
            ModeShape = null;
            ShapeError = string.Empty;
            IsLoadingShape = false;
        }

        /// <summary>Campbell diagram clicked: animate this mode at this wind speed.</summary>
        public Task SelectModeAsync(CampbellTrack track, CampbellPoint point)
        {
            SelectedTrack = track;
            SelectedPoint = point;
            return LoadModeShapeAsync();
        }

        /// <summary>Asks the backend for the frames of the selected point (again after a scale / size change).</summary>
        public async Task LoadModeShapeAsync()
        {
            var p = SelectedPoint;
            if (p == null) return;
            int id = ++_shapeRequest;
            IsLoadingShape = true;
            ShapeError = string.Empty;
            try
            {
                // CHANGED: the mode is given by its rank at the clicked wind speed (0 = lowest frequency,
                // 1 = second lowest, …) plus the clicked frequency and wind speed. The eigenvalue index K is
                // sent too when the Campbell data has it, but it is no longer required.
                var r = await _api.GetModeShapeAsync(new ModeShapeRequest
                {
                    Op = p.Op, K = p.K, Wind = p.Wind, ModeIndex = ModeIndexAt(p), Freq = p.FnHz,
                    IncludeAll = IncludeAllTracks,
                    Scale = ShapeScale, Frames = 48,
                    TowerLen = TowerLen, BladeLen = BladeLen, HubRadius = HubRadius
                });
                if (id != _shapeRequest) return;            // a newer click won
                ShapeError = r == null ? (_api.LastError ?? "No answer from the backend.") : string.Empty;
                if (r != null) ModeShape = r;
            }
            finally
            {
                if (id == _shapeRequest) IsLoadingShape = false;
            }
        }

        private void ApplyData()
        {
            if (_data == null) return;
            WindSpeeds.Clear();
            foreach (var w in _data.Winds) WindSpeeds.Add(w);

            Tracks.Clear();
            foreach (var t in _data.Tracks)
            {
                Tracks.Add(t);
            }
        }
    }
}
