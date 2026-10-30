using System.Text.Json;
using System.Text.Json.Serialization;

// =====================================================================================================
//  LinBench JSON models  –  REWRITTEN to match the REAL Python backend (Backend/linbench/server.py,
//  analysis.py, discover.py, closedloop.py, gainstudy.py).
//
//  The previous models were written for an API that does not exist (e.g. scan.groups, ops[].az_sens as a
//  number, channels.x/u/y, closed[].pm_deg ...). System.Text.Json therefore either returned EMPTY lists
//  (scan "groups" missing -> no operating points -> "Run analysis" never enabled) or THREW (summary ->
//  results never loaded).
//
//  Rules used below:
//   * [JsonPropertyName] = the exact key the backend sends.
//   * Every number that the backend can send as null (NaN/inf are cleaned to null) is "double?".
//   * The C# property names the XAML / panels already use are kept; where the backend shape differs,
//     a [JsonIgnore] computed property provides the old name (e.g. ClosedLoopDetail.LoopShape).
// =====================================================================================================

namespace LinBench.Core.Models
{
    // ------------------------------------------------------------------ /api/ping, /api/version
    public class PingResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("app")] public string App { get; set; } = string.Empty;
        [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    }

    public class AppVersionInfo
    {
        [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
        [JsonPropertyName("frozen")] public bool Frozen { get; set; }
        [JsonPropertyName("offline")] public bool Offline { get; set; }
        [JsonPropertyName("blocked")] public int Blocked { get; set; }
    }

    // ------------------------------------------------------------------ /api/inspect
    public class InspectRequest
    {
        [JsonPropertyName("lin_dir")] public string LinDir { get; set; } = string.Empty;
        [JsonPropertyName("recursive")] public bool Recursive { get; set; }
        [JsonPropertyName("discon")] public string Discon { get; set; } = string.Empty;
        [JsonPropertyName("yaml")] public string Yaml { get; set; } = string.Empty;
        [JsonPropertyName("cp")] public string Cp { get; set; } = string.Empty;
        [JsonPropertyName("fst")] public string Fst { get; set; } = string.Empty;
    }

    public class InspectResult
    {
        [JsonPropertyName("lin_dir")] public string? LinDir { get; set; }
        [JsonPropertyName("discon")] public string? Discon { get; set; }
        [JsonPropertyName("yaml")] public string? Yaml { get; set; }
        [JsonPropertyName("cp")] public string? Cp { get; set; }
        [JsonPropertyName("fst")] public string? Fst { get; set; }
        [JsonPropertyName("found")] public Dictionary<string, string> Found { get; set; } = new();
        [JsonPropertyName("messages")] public List<string> Messages { get; set; } = new();
        [JsonPropertyName("scan")] public FolderScanResult? Scan { get; set; }

    }

    public class FolderScanResult
    {
        [JsonPropertyName("folder")] public string Folder { get; set; } = string.Empty;
        [JsonPropertyName("n_files")] public int FileCount { get; set; }

        // CHANGED: backend key is "ops" (was "groups")
        [JsonPropertyName("ops")] public List<OperatingGroup> Ops { get; set; } = new();

        [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();

        // list of [file name, reason]
        [JsonPropertyName("skipped")] public List<List<string>> Skipped { get; set; } = new();

        [JsonPropertyName("name_order_differs")] public bool NameOrderDiffers { get; set; }

        // ---- compatibility / convenience (not sent by the backend)
        [JsonIgnore] public List<OperatingGroup> Groups => Ops;
        [JsonIgnore] public int OperatingPointCount => Ops.Count;
        [JsonIgnore]
        public List<double>? WindRange => Ops.Count == 0
            ? null
            : new List<double> { Ops.Min(o => o.WindSpeed), Ops.Max(o => o.WindSpeed) };
    }

    public class OperatingGroup
    {
        [JsonPropertyName("wind_speed")] public double WindSpeed { get; set; }
        [JsonPropertyName("rotor_speed")] public double? RotorSpeedRadS { get; set; }
        [JsonPropertyName("rotor_rpm")] public double? RotorRpm { get; set; }
        [JsonPropertyName("n_files")] public int FileCount { get; set; }
        [JsonPropertyName("files")] public List<string> Files { get; set; } = new();
        [JsonPropertyName("azimuth_deg")] public List<double?> Azimuths { get; set; } = new();
        [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
        [JsonPropertyName("omega_check")] public OmegaCheckInfo? OmegaCheck { get; set; }

        [JsonIgnore] public string WarningText => Warnings.Count == 0 ? "OK" : string.Join(" · ", Warnings);
    }

    public class OmegaCheckInfo
    {
        /// <summary>Relative spread of rotor speed across the azimuth files (fraction, 0.01 = 1 %).</summary>
        [JsonPropertyName("spread")] public double? Spread { get; set; }
        [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;      // none, drift, ...
        [JsonPropertyName("level")] public string Level { get; set; } = string.Empty;    // ok, notice, warning
        [JsonPropertyName("slope_rpm_s")] public double? SlopeRpmPerS { get; set; }
        [JsonPropertyName("r2_time")] public double? R2Time { get; set; }
        [JsonPropertyName("r2_azimuth")] public double? R2Azimuth { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;

        [JsonIgnore] public double? SpreadPct => Spread * 100.0;
        [JsonIgnore] public string Status => string.IsNullOrEmpty(Message) ? Level : $"{Level}: {Message}";
    }

    // ------------------------------------------------------------------ /api/load, /api/progress, /api/recent
    public class LoadRequest
    {
        [JsonPropertyName("lin_dir")] public string LinDir { get; set; } = string.Empty;
        [JsonPropertyName("recursive")] public bool Recursive { get; set; }
        [JsonPropertyName("discon")] public string Discon { get; set; } = string.Empty;
        [JsonPropertyName("yaml")] public string Yaml { get; set; } = string.Empty;
        [JsonPropertyName("cp")] public string Cp { get; set; } = string.Empty;
        [JsonPropertyName("fst")] public string Fst { get; set; } = string.Empty;
        [JsonPropertyName("mode")] public string Mode { get; set; } = "both"; // open, closed, both
        [JsonPropertyName("remove_azimuth")] public bool RemoveAzimuth { get; set; } = true;
        [JsonPropertyName("cache")] public bool Cache { get; set; } = true;
    }

    public class ProgressUpdate
    {
        [JsonPropertyName("stage")] public string Stage { get; set; } = string.Empty;
        // CHANGED: double (Python may send a float)
        [JsonPropertyName("pct")] public double Percent { get; set; }
        [JsonPropertyName("msg")] public string Message { get; set; } = string.Empty;
        [JsonPropertyName("elapsed")] public double Elapsed { get; set; }
        [JsonPropertyName("open_done")] public bool OpenDone { get; set; }
        [JsonPropertyName("done")] public bool Done { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("log")] public List<LogEntry> Log { get; set; } = new();
        [JsonPropertyName("log_n")] public int LogCount { get; set; }
    }

    public class LogEntry
    {
        [JsonPropertyName("t")] public double Time { get; set; }
        [JsonPropertyName("stage")] public string Stage { get; set; } = string.Empty;
        [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
        [JsonPropertyName("data")] public Dictionary<string, JsonElement>? Data { get; set; }
    }

    public class RecentFolder
    {
        [JsonPropertyName("lin_dir")] public string LinDir { get; set; } = string.Empty;
        [JsonPropertyName("discon")] public string? Discon { get; set; }
        [JsonPropertyName("yaml")] public string? Yaml { get; set; }
        [JsonPropertyName("cp")] public string? Cp { get; set; }
        [JsonPropertyName("fst")] public string? Fst { get; set; }
    }

    // ------------------------------------------------------------------ /api/summary
    public class SummaryResult
    {
        [JsonPropertyName("info")] public Dictionary<string, JsonElement>? Info { get; set; }
        [JsonPropertyName("ops")] public List<OpSummary> OperatingPoints { get; set; } = new();
        [JsonPropertyName("campbell")] public CampbellData? Campbell { get; set; }
        [JsonPropertyName("closed")] public List<ClosedLoopRow>? Closed { get; set; }
        [JsonPropertyName("closed_pending")] public bool ClosedPending { get; set; }
        [JsonPropertyName("design")] public List<Dictionary<string, JsonElement>>? Design { get; set; }
        [JsonPropertyName("controller")] public Dictionary<string, JsonElement>? Controller { get; set; }
        [JsonPropertyName("channels")] public ChannelCollection? Channels { get; set; }

        // CHANGED: int -> int?  (e.g. "wave": null, "gentq": null)
        [JsonPropertyName("std_inputs")] public Dictionary<string, int?>? StdInputs { get; set; }
        [JsonPropertyName("std_outputs")] public Dictionary<string, int?>? StdOutputs { get; set; }

        [JsonPropertyName("defaults")] public Dictionary<string, int?>? Defaults { get; set; }
        [JsonPropertyName("has_controller")] public bool HasController { get; set; }
        [JsonPropertyName("mode")] public string Mode { get; set; } = "both";
        [JsonPropertyName("load_time_s")] public double? LoadTimeS { get; set; }

        /// <summary>Reads a string from "info" (e.g. "title", "name").</summary>
        public string? InfoString(string key) =>
            Info != null && Info.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    public class OpSummary
    {
        // CHANGED: real backend keys
        [JsonPropertyName("i")] public int Index { get; set; }
        [JsonPropertyName("wind")] public double WindSpeed { get; set; }
        [JsonPropertyName("rotor_rpm")] public double? Omega { get; set; }            // rpm
        [JsonPropertyName("rotor_rad_s")] public double? OmegaRadS { get; set; }
        [JsonPropertyName("pitch_deg")] public double? Pitch { get; set; }            // deg
        [JsonPropertyName("gen_rpm")] public double? GenRpm { get; set; }
        [JsonPropertyName("gen_torque")] public double? GenTorqueNm { get; set; }     // N·m
        [JsonPropertyName("power_kw")] public double? PowerKw { get; set; }
        [JsonPropertyName("thrust_kn")] public double? Thrust { get; set; }           // kN
        [JsonPropertyName("tsr")] public double? Tsr { get; set; }
        [JsonPropertyName("cp")] public double? Cp { get; set; }
        [JsonPropertyName("ct")] public double? Ct { get; set; }
        [JsonPropertyName("ptfm_pitch_deg")] public double? PlatformPitchDeg { get; set; }
        [JsonPropertyName("n_az")] public int AzimuthCount { get; set; }
        [JsonPropertyName("periodicity_raw")] public double? PeriodicityRaw { get; set; }
        [JsonPropertyName("periodicity_mbc")] public double? PeriodicityMbc { get; set; }
        [JsonPropertyName("az_sens")] public AzimuthSensitivityInfo? AzSens { get; set; }   // CHANGED: object, not a number
        [JsonPropertyName("omega_check")] public OmegaCheckInfo? OmegaCheck { get; set; }
        [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
        [JsonPropertyName("files")] public List<string> Files { get; set; } = new();

        // NEW: one entry per .lin file of this operating point (azimuth, time, rotor speed) – used by the
        // "Rotor speed across the azimuth files" chart on the Operating points panel
        [JsonPropertyName("file_diag")] public List<FileDiagInfo> FileDiag { get; set; } = new();

        // ---- values in the units the existing XAML columns show
        [JsonIgnore] public double? Torque => GenTorqueNm / 1000.0;   // kNm
        [JsonIgnore] public double? Power => PowerKw / 1000.0;        // MW
        [JsonIgnore] public double? AzimuthSensitivity => AzSens?.MaxRelFreqChange;
    }

    /// <summary>NEW: summary.ops[].file_diag[] – trim values of each azimuth file.</summary>
    public class FileDiagInfo
    {
        [JsonPropertyName("file")] public string? File { get; set; }
        [JsonPropertyName("azimuth_deg")] public double? AzimuthDeg { get; set; }
        [JsonPropertyName("time")] public double? Time { get; set; }
        [JsonPropertyName("rotor_rpm")] public double? RotorRpm { get; set; }
        [JsonPropertyName("wind")] public double? Wind { get; set; }
        [JsonPropertyName("pitch_deg")] public double? PitchDeg { get; set; }
        [JsonPropertyName("gen_torque")] public double? GenTorque { get; set; }
        [JsonPropertyName("gen_rpm")] public double? GenRpm { get; set; }
        [JsonPropertyName("max_df")] public double? MaxDf { get; set; }
        [JsonPropertyName("max_dz")] public double? MaxDz { get; set; }
    }

    public class AzimuthSensitivityInfo
    {
        [JsonPropertyName("df")] public double? MaxRelFreqChange { get; set; }
        [JsonPropertyName("df_fn")] public double? DfFn { get; set; }
        [JsonPropertyName("df_mode")] public string? DfMode { get; set; }
        [JsonPropertyName("dz")] public double? MaxDampingChange { get; set; }
        [JsonPropertyName("dz_mode")] public string? DzMode { get; set; }
        [JsonPropertyName("dz_zeta")] public double? DzZeta { get; set; }

        // NEW: per mode, the change of frequency / damping in every single-azimuth model vs the averaged model
        // (one value per azimuth file, in file_diag order) – "Mode frequency in each single-azimuth model" chart
        [JsonPropertyName("modes")] public List<AzimuthModeInfo> Modes { get; set; } = new();
    }

    public class AzimuthModeInfo
    {
        [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
        [JsonPropertyName("fn")] public double? Fn { get; set; }          // Hz
        [JsonPropertyName("zeta")] public double? Zeta { get; set; }
        [JsonPropertyName("df")] public List<double?> Df { get; set; } = new();   // relative (0.01 = 1 %)
        [JsonPropertyName("dz")] public List<double?> Dz { get; set; } = new();   // absolute damping ratio change
    }

    // CHANGED: backend sends {"inputs":[...], "outputs":[...]} (not x/u/y)
    public class ChannelCollection
    {
        [JsonPropertyName("inputs")] public List<ChannelInfo> Inputs { get; set; } = new();
        [JsonPropertyName("outputs")] public List<ChannelInfo> Outputs { get; set; } = new();
    }

    public class ChannelInfo
    {
        [JsonPropertyName("i")] public int Index { get; set; }
        [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
        [JsonPropertyName("unit")] public string Unit { get; set; } = string.Empty;
        [JsonPropertyName("disp_unit")] public string DisplayUnit { get; set; } = string.Empty;
        [JsonPropertyName("factor")] public double? Factor { get; set; }
        [JsonPropertyName("module")] public string Module { get; set; } = string.Empty;
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
        [JsonPropertyName("rotating_only")] public bool RotatingOnly { get; set; }

        /// <summary>Used by the ComboBoxes (DisplayMemberPath="Name").</summary>
        [JsonIgnore]
        public string Name => string.IsNullOrEmpty(DisplayUnit) ? Label : $"{Label} [{DisplayUnit}]";

        public override string ToString() => Name;
    }

    // ------------------------------------------------------------------ Campbell (summary.campbell, /api/campbell)
    public class CampbellData
    {
        [JsonPropertyName("tracks")] public List<CampbellTrack> Tracks { get; set; } = new();
        [JsonPropertyName("rotor")] public List<RotorHarmonic> Rotor { get; set; } = new();

        [JsonIgnore]
        public List<double> Winds => Tracks.SelectMany(t => t.Points).Select(p => p.Wind)
                                           .Concat(Rotor.Select(r => r.Wind))
                                           .Distinct().OrderBy(w => w).ToList();
    }

    public class CampbellTrack
    {
        [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
        [JsonPropertyName("family")] public string? Family { get; set; }
        [JsonPropertyName("sym")] public string? Type { get; set; }          // '', 'BW', 'FW', 'coll'
        [JsonPropertyName("mean_f")] public double? MeanFrequencyHz { get; set; }
        [JsonPropertyName("points")] public List<CampbellPoint> Points { get; set; } = new();
    }

    public class CampbellPoint
    {
        [JsonPropertyName("op")] public int Op { get; set; }
        [JsonPropertyName("wind")] public double Wind { get; set; }
        [JsonPropertyName("fn")] public double? FnHz { get; set; }          // natural frequency [Hz]
        [JsonPropertyName("fd")] public double? FdHz { get; set; }          // damped frequency [Hz]
        [JsonPropertyName("zeta")] public double? Zeta { get; set; }        // damping ratio [-]
        [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
        [JsonPropertyName("whirl")] public double? Whirl { get; set; }
    }

    public class RotorHarmonic
    {
        [JsonPropertyName("wind")] public double Wind { get; set; }
        [JsonPropertyName("f1p")] public double? F1p { get; set; }          // 1P [Hz]
    }

    // ------------------------------------------------------------------ /api/modes  ->  {op, wind, modes:[...]}
    public class ModesResponse
    {
        [JsonPropertyName("op")] public int Op { get; set; }
        [JsonPropertyName("wind")] public double Wind { get; set; }
        [JsonPropertyName("modes")] public List<ModeItem> Modes { get; set; } = new();
    }

    public class ModeItem
    {
        [JsonPropertyName("k")] public int Mode { get; set; }
        [JsonPropertyName("re")] public double? Re { get; set; }
        [JsonPropertyName("im")] public double? Im { get; set; }
        [JsonPropertyName("fn")] public double? FreqHz { get; set; }       // undamped natural frequency [Hz]
        [JsonPropertyName("fd")] public double? DampedFreqHz { get; set; }
        [JsonPropertyName("wn")] public double? FreqRad { get; set; }      // [rad/s]
        [JsonPropertyName("zeta")] public double? DampingRatio { get; set; }
        [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
        [JsonPropertyName("dof")] public string Dof { get; set; } = string.Empty;
        [JsonPropertyName("family")] public string Family { get; set; } = string.Empty;
        [JsonPropertyName("share")] public double? Share { get; set; }
        [JsonPropertyName("sym")] public string Sym { get; set; } = string.Empty;
        [JsonPropertyName("whirl")] public double? WhirlValue { get; set; }
        [JsonPropertyName("rotor_share")] public double? RotorShare { get; set; }
        [JsonPropertyName("oscillatory")] public bool Oscillatory { get; set; }

        /// <summary>[[label, share], ...] – the dominant degrees of freedom of this mode.</summary>
        [JsonPropertyName("top")] public List<List<JsonElement>> Top { get; set; } = new();
        [JsonPropertyName("families")] public Dictionary<string, double?> Families { get; set; } = new();

        [JsonIgnore] public string Whirl => !string.IsNullOrEmpty(Sym) ? Sym : (WhirlValue is double w && Math.Abs(w) > 0.5 ? (w > 0 ? "FW" : "BW") : "");

        [JsonIgnore]
        public List<StateParticipation> States => Top
            .Where(t => t.Count >= 2)
            .Select(t => new StateParticipation
            {
                Name = t[0].ValueKind == JsonValueKind.String ? t[0].GetString() ?? "" : t[0].ToString(),
                Factor = t[1].ValueKind == JsonValueKind.Number ? t[1].GetDouble() : 0.0
            }).ToList();
    }

    public class StateParticipation
    {
        public string Name { get; set; } = string.Empty;
        public double Factor { get; set; }
    }

    // ------------------------------------------------------------------ summary.closed[] rows
    public class ClosedLoopRow
    {
        [JsonPropertyName("op")] public int OpIndex { get; set; }
        [JsonPropertyName("wind")] public double WindSpeed { get; set; }
        [JsonPropertyName("region")] public string Region { get; set; } = string.Empty;
        [JsonPropertyName("region_name")] public string RegionName { get; set; } = string.Empty;
        [JsonPropertyName("gm_db")] public double? GainMarginDb { get; set; }
        [JsonPropertyName("w_gm")] public double? GainCrossoverW { get; set; }
        [JsonPropertyName("pm")] public double? PhaseMarginDeg { get; set; }            // CHANGED: key "pm"
        [JsonPropertyName("w_pm")] public double? PhaseCrossoverW { get; set; }
        [JsonPropertyName("dm")] public double? DelayMarginS { get; set; }              // seconds
        [JsonPropertyName("s_m")] public double? StabilityMargin { get; set; }          // min |1+L|
        [JsonPropertyName("disk_gm_db")] public double? DiskGainMarginDb { get; set; }
        [JsonPropertyName("disk_pm")] public double? DiskPhaseMarginDeg { get; set; }
        [JsonPropertyName("Smax_db")] public double? SmaxDb { get; set; }
        [JsonPropertyName("Tmax_db")] public double? TmaxDb { get; set; }
        [JsonPropertyName("bw_T")] public double? BandwidthT { get; set; }
        [JsonPropertyName("stable")] public bool Stable { get; set; }
        [JsonPropertyName("nyquist_ok")] public bool NyquistOk { get; set; }
        [JsonPropertyName("min_zeta")] public double? MinZeta { get; set; }
        [JsonPropertyName("min_zeta_f")] public double? MinZetaFreq { get; set; }
        [JsonPropertyName("min_zeta_label")] public string? MinZetaLabel { get; set; }
        [JsonPropertyName("Kp")] public double? Kp { get; set; }
        [JsonPropertyName("Ki")] public double? Ki { get; set; }
        [JsonPropertyName("VS_Kp")] public double? VsKp { get; set; }
        [JsonPropertyName("VS_Ki")] public double? VsKi { get; set; }
        [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new();

        // ---- compatibility with the existing Stability summary XAML
        [JsonIgnore] public double? DelayMarginMs => DelayMarginS * 1000.0;
        [JsonIgnore] public double? DiskMargin => DiskGainMarginDb;
        [JsonIgnore] public string StatusText => Stable ? "Stable" : "UNSTABLE";
    }

    // ------------------------------------------------------------------ /api/closed?op=  (flat object)
    public class ClosedLoopDetail
    {
        [JsonPropertyName("op")] public int OpIndex { get; set; }
        [JsonPropertyName("wind")] public double WindSpeed { get; set; }
        [JsonPropertyName("region")] public string Region { get; set; } = string.Empty;
        [JsonPropertyName("region_name")] public string RegionName { get; set; } = string.Empty;
        [JsonPropertyName("act")] public string Actuator { get; set; } = string.Empty;
        [JsonPropertyName("act_unit")] public string ActuatorUnit { get; set; } = string.Empty;

        [JsonPropertyName("w")] public List<double?> W { get; set; } = new();
        [JsonPropertyName("L_mag")] public List<double?> LMag { get; set; } = new();
        [JsonPropertyName("L_phase")] public List<double?> LPhase { get; set; } = new();
        [JsonPropertyName("S_mag")] public List<double?> SMag { get; set; } = new();
        [JsonPropertyName("T_mag")] public List<double?> TMag { get; set; } = new();
        [JsonPropertyName("nyq_w")] public List<double?> NyqW { get; set; } = new();
        [JsonPropertyName("nyq_re")] public List<double?> NyqRe { get; set; } = new();
        [JsonPropertyName("nyq_im")] public List<double?> NyqIm { get; set; } = new();

        [JsonPropertyName("gm_min")] public MarginPoint? GmMin { get; set; }
        [JsonPropertyName("pm_min")] public MarginPoint? PmMin { get; set; }
        [JsonPropertyName("dm")] public double? DelayMarginS { get; set; }
        [JsonPropertyName("s_m")] public double? StabilityMargin { get; set; }
        [JsonPropertyName("Smax")] public double? Smax { get; set; }
        [JsonPropertyName("Tmax")] public double? Tmax { get; set; }
        [JsonPropertyName("bw_T")] public double? BandwidthT { get; set; }
        [JsonPropertyName("P")] public int P { get; set; }
        [JsonPropertyName("Z")] public int Z { get; set; }
        [JsonPropertyName("N")] public int N { get; set; }
        [JsonPropertyName("nyquist_ok")] public bool NyquistOk { get; set; }
        [JsonPropertyName("stable")] public bool Stable { get; set; }
        [JsonPropertyName("disk_gm_db")] public double? DiskGainMarginDb { get; set; }
        [JsonPropertyName("disk_pm")] public double? DiskPhaseMarginDeg { get; set; }
        [JsonPropertyName("ol_poles")] public List<PoleInfo> OlPoles { get; set; } = new();
        [JsonPropertyName("cl_poles")] public List<PoleInfo> ClPoles { get; set; } = new();

        // ---- the shapes the existing LoopShape / Nyquist / Poles panels use
        // Backend magnitudes are LINEAR; the panel axis is dB -> convert here
        [JsonIgnore]
        public LoopShapeData LoopShape => new()
        {
            Freq = Clean(W, LMag).x,
            LMag = Db(Clean(W, LMag).y),
            LPhase = Clean(W, LPhase).y,
            SMag = Db(Clean(W, SMag).y),
            TMag = Db(Clean(W, TMag).y),
            Ms = Smax,
            Mt = Tmax
        };

        private static List<double> Db(List<double> lin) => lin.Select(v => v > 0 ? 20.0 * Math.Log10(v) : double.NaN).ToList();

        [JsonIgnore]
        public NyquistData Nyquist
        {
            get
            {
                var (re, im) = Clean(NyqRe, NyqIm);
                return new NyquistData { Real = re, Imag = im, WindingNumber = N };
            }
        }

        [JsonIgnore]
        public PolesData Poles => new()
        {
            OpenLoopPoles = OlPoles.Where(p => p.Re.HasValue && p.Im.HasValue).Select(p => new List<double> { p.Re!.Value, p.Im!.Value }).ToList(),
            ClosedLoopPoles = ClPoles.Where(p => p.Re.HasValue && p.Im.HasValue).Select(p => new List<double> { p.Re!.Value, p.Im!.Value }).ToList(),
            Labels = ClPoles.Select(p => p.Label).ToList()
        };

        [JsonIgnore]
        public MarginDetail Margins => new()
        {
            GainMarginDb = GmMin?.GainMarginDb,
            PhaseMarginDeg = PmMin?.PhaseMargin,
            DelayMarginMs = DelayMarginS * 1000.0,
            DiskMargin = DiskGainMarginDb,
            Stable = Stable
        };

        /// <summary>Drops pairs where either value is null (NaN/inf cleaned by the backend).</summary>
        private static (List<double> x, List<double> y) Clean(List<double?> xs, List<double?> ys)
        {
            var x = new List<double>(); var y = new List<double>();
            int n = Math.Min(xs.Count, ys.Count);
            for (int i = 0; i < n; i++)
            {
                if (xs[i].HasValue && ys[i].HasValue) { x.Add(xs[i]!.Value); y.Add(ys[i]!.Value); }
            }
            return (x, y);
        }
    }

    public class MarginPoint
    {
        [JsonPropertyName("w")] public double? W { get; set; }
        [JsonPropertyName("pm")] public double? PhaseMargin { get; set; }
        [JsonPropertyName("gm_db")] public double? GainMarginDb { get; set; }
        [JsonPropertyName("gm")] public double? GainMargin { get; set; }
    }

    public class PoleInfo
    {
        [JsonPropertyName("re")] public double? Re { get; set; }
        [JsonPropertyName("im")] public double? Im { get; set; }
        [JsonPropertyName("fn")] public double? Fn { get; set; }
        [JsonPropertyName("wn")] public double? Wn { get; set; }
        [JsonPropertyName("zeta")] public double? Zeta { get; set; }
        [JsonPropertyName("rel")] public double? Rel { get; set; }
        [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
        [JsonPropertyName("origin")] public string? Origin { get; set; }
    }

    // View-side shapes (built from ClosedLoopDetail, not deserialized)
    public class LoopShapeData
    {
        public List<double> Freq { get; set; } = new();
        public List<double> LMag { get; set; } = new();
        public List<double> LPhase { get; set; } = new();
        public List<double> SMag { get; set; } = new();
        public List<double> TMag { get; set; } = new();
        public double? Ms { get; set; }
        public double? Mt { get; set; }
    }

    public class NyquistData
    {
        public List<double> Real { get; set; } = new();
        public List<double> Imag { get; set; } = new();
        public int? WindingNumber { get; set; }
    }

    public class PolesData
    {
        public List<List<double>> OpenLoopPoles { get; set; } = new();   // [re, im]
        public List<List<double>> ClosedLoopPoles { get; set; } = new();
        public List<string>? Labels { get; set; }
    }

    public class MarginDetail
    {
        public double? GainMarginDb { get; set; }
        public double? PhaseMarginDeg { get; set; }
        public double? DelayMarginMs { get; set; }
        public double? DiskMargin { get; set; }
        public bool Stable { get; set; }
    }

    // ------------------------------------------------------------------ /api/tf, /api/time
    public class TfRequest
    {
        [JsonPropertyName("op")] public int OpIndex { get; set; }
        [JsonPropertyName("input")] public int InputIndex { get; set; }
        [JsonPropertyName("output")] public int OutputIndex { get; set; }
    }

    public class TfResult
    {
        [JsonPropertyName("w")] public List<double?> W { get; set; } = new();
        [JsonPropertyName("mag")] public List<double?> MagRaw { get; set; } = new();
        [JsonPropertyName("phase")] public List<double?> PhaseRaw { get; set; } = new();
        [JsonPropertyName("poles")] public List<PoleInfo> Poles { get; set; } = new();
        [JsonPropertyName("zeros")] public List<PoleInfo> Zeros { get; set; } = new();
        [JsonPropertyName("metrics")] public TfMetrics? Metrics { get; set; }
        // NEW: used by the Transfer function page (Bode subtitle, G(s) box, pole-zero map)
        [JsonPropertyName("poles_cancelled")] public List<PoleInfo> PolesCancelled { get; set; } = new();
        [JsonPropertyName("labels")] public TfLabels? Labels { get; set; }
        [JsonPropertyName("units")] public TfUnits? Units { get; set; }
        [JsonPropertyName("wind")] public double? Wind { get; set; }
        [JsonPropertyName("op")] public int? Op { get; set; }

        [JsonIgnore] public List<double> Freq => Pairs(MagRaw).x;
        /// <summary>Magnitude in dB (backend sends linear |G|).</summary>
        [JsonIgnore] public List<double> Mag => Pairs(MagRaw).y.Select(v => v > 0 ? 20.0 * Math.Log10(v) : double.NaN).ToList();
        [JsonIgnore] public List<double> Phase => Pairs(PhaseRaw).y;
        [JsonIgnore] public List<double> PhaseFreq => Pairs(PhaseRaw).x;
        [JsonIgnore] public double? DcGain => Metrics?.DcGain;
        [JsonIgnore] public double? Bandwidth => Metrics?.BandwidthW;

        private (List<double> x, List<double> y) Pairs(List<double?> ys)
        {
            var x = new List<double>(); var y = new List<double>();
            int n = Math.Min(W.Count, ys.Count);
            for (int i = 0; i < n; i++)
                if (W[i].HasValue && ys[i].HasValue) { x.Add(W[i]!.Value); y.Add(ys[i]!.Value); }
            return (x, y);
        }
    }

    public class TfMetrics
    {
        [JsonPropertyName("dc_gain")] public double? DcGain { get; set; }
        [JsonPropertyName("integrator")] public bool Integrator { get; set; }
        [JsonPropertyName("peak_gain")] public double? PeakGain { get; set; }
        [JsonPropertyName("peak_w")] public double? PeakW { get; set; }
        [JsonPropertyName("bandwidth_w")] public double? BandwidthW { get; set; }
        [JsonPropertyName("n_poles")] public int NPoles { get; set; }
        [JsonPropertyName("n_zeros")] public int NZeros { get; set; }
        [JsonPropertyName("n_rhp_zeros")] public int NRhpZeros { get; set; }
        [JsonPropertyName("n_rhp_poles")] public int NRhpPoles { get; set; }
        // NEW
        [JsonPropertyName("n_cancelled")] public int NCancelled { get; set; }
        [JsonPropertyName("rhp_zeros")] public List<PoleInfo> RhpZeros { get; set; } = new();
        [JsonPropertyName("dominant")] public PoleInfo? Dominant { get; set; }
        [JsonPropertyName("rel_degree")] public int? RelDegree { get; set; }
        [JsonPropertyName("full_order")] public int FullOrder { get; set; }
        [JsonPropertyName("full_zeros")] public int FullZeros { get; set; }
        [JsonPropertyName("cancelled_pairs")] public int CancelledPairs { get; set; }
    }

    public class TfLabels
    {
        [JsonPropertyName("input")] public string Input { get; set; } = string.Empty;
        [JsonPropertyName("output")] public string Output { get; set; } = string.Empty;
    }

    public class TfUnits
    {
        [JsonPropertyName("input")] public string Input { get; set; } = string.Empty;
        [JsonPropertyName("output")] public string Output { get; set; } = string.Empty;
        [JsonPropertyName("gain")] public string Gain { get; set; } = string.Empty;
    }

    public class TimeRequest
    {
        [JsonPropertyName("op")] public int OpIndex { get; set; }
        [JsonPropertyName("input")] public int InputIndex { get; set; }
        [JsonPropertyName("output")] public int OutputIndex { get; set; }
        [JsonPropertyName("kind")] public string Kind { get; set; } = "step"; // step or impulse
        [JsonPropertyName("amp")] public double Amplitude { get; set; } = 1.0;
        [JsonPropertyName("T")] public double? Duration { get; set; }
    }

    public class TimeResult
    {
        [JsonPropertyName("t")] public List<double?> TRaw { get; set; } = new();
        [JsonPropertyName("y")] public List<double?> YRaw { get; set; } = new();
        [JsonPropertyName("T")] public double? Duration { get; set; }
        [JsonPropertyName("metrics")] public Dictionary<string, JsonElement>? Metrics { get; set; }
        [JsonPropertyName("units")] public Dictionary<string, string>? Units { get; set; }
        [JsonPropertyName("labels")] public Dictionary<string, string>? Labels { get; set; }

        [JsonIgnore] public List<double> Time => Pairs().x;
        [JsonIgnore] public List<double> Output => Pairs().y;

        private (List<double> x, List<double> y) Pairs()
        {
            var x = new List<double>(); var y = new List<double>();
            int n = Math.Min(TRaw.Count, YRaw.Count);
            for (int i = 0; i < n; i++)
                if (TRaw[i].HasValue && YRaw[i].HasValue) { x.Add(TRaw[i]!.Value); y.Add(YRaw[i]!.Value); }
            return (x, y);
        }
    }

    // ------------------------------------------------------------------ /api/gain_map
    public class GainMapRequest
    {
        [JsonPropertyName("op")] public int OpIndex { get; set; }
        [JsonPropertyName("pair")] public string Pair { get; set; } = "pi";
        [JsonPropertyName("lo")] public double Lo { get; set; } = 0.25;
        [JsonPropertyName("hi")] public double Hi { get; set; } = 4.0;
        [JsonPropertyName("n")] public int N { get; set; } = 25;
    }

    public class GainMapResult
    {
        [JsonPropertyName("op")] public int OpIndex { get; set; }
        [JsonPropertyName("wind")] public double? Wind { get; set; }
        [JsonPropertyName("pair")] public string Pair { get; set; } = "pi";
        [JsonPropertyName("region")] public string Region { get; set; } = string.Empty;
        [JsonPropertyName("region_name")] public string RegionName { get; set; } = string.Empty;
        [JsonPropertyName("fx")] public List<double> Fx { get; set; } = new();
        [JsonPropertyName("fy")] public List<double> Fy { get; set; } = new();
        [JsonPropertyName("zeta")] public List<List<double?>> Zeta { get; set; } = new();
        [JsonPropertyName("fn")] public List<List<double?>> Fn { get; set; } = new();
        [JsonPropertyName("max_re")] public List<List<double?>> MaxRe { get; set; } = new();
        [JsonPropertyName("base")] public Dictionary<string, JsonElement>? Base { get; set; }
        [JsonPropertyName("names")] public Dictionary<string, JsonElement>? Names { get; set; }

        // The map is in gain FACTORS; the baseline controller is at factor 1.0 / 1.0
        [JsonIgnore] public double BaseFx => 1.0;
        [JsonIgnore] public double BaseFy => 1.0;
        [JsonIgnore]
        public List<List<bool>> Stable => MaxRe.Select(r => r.Select(v => v.HasValue && v.Value < 1e-7).ToList()).ToList();
    }

    // ------------------------------------------------------------------ /api/structure  (needs inputs + outputs)
    public class StructureResult
    {
        [JsonPropertyName("op")] public int OpIndex { get; set; }
        [JsonPropertyName("n")] public int TotalStates { get; set; }
        [JsonPropertyName("rank_c")] public int ControllableRank { get; set; }
        [JsonPropertyName("rank_o")] public int ObservableRank { get; set; }
        [JsonPropertyName("n_min")] public int MinimalOrder { get; set; }
        [JsonPropertyName("stabilizable")] public bool Stabilizable { get; set; }
        [JsonPropertyName("detectable")] public bool Detectable { get; set; }
        [JsonPropertyName("uncontrollable")] public List<PoleInfo> Uncontrollable { get; set; } = new();
        [JsonPropertyName("unobservable")] public List<PoleInfo> Unobservable { get; set; } = new();
        [JsonPropertyName("hsv")] public List<double?> HsvRaw { get; set; } = new();
        [JsonPropertyName("order_1pct")] public int? Order1Pct { get; set; }

        [JsonIgnore] public List<string> UncontrollableModes => Uncontrollable.Select(Describe).ToList();
        [JsonIgnore] public List<string> UnobservableModes => Unobservable.Select(Describe).ToList();
        [JsonIgnore] public List<double> HankelSingularValues => HsvRaw.Where(v => v.HasValue).Select(v => v!.Value).ToList();

        private static string Describe(PoleInfo p) =>
            $"{p.Label}  ({(p.Fn.HasValue ? $"{p.Fn.Value:0.###} Hz" : "-")}, ζ {(p.Zeta.HasValue ? $"{p.Zeta.Value:P1}" : "-")})";
    }
}
