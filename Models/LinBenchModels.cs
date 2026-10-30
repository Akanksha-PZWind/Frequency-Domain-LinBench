using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

// =====================================================================================================
//  LinBench JSON models  –  match the Python backend (Backend/linbench/server.py, analysis.py, discover.py,
//  modal.py). Only what the Operating points and Campbell diagram pages (and the start page) need.
//
//  The previous models were written for an API that does not exist (e.g. scan.groups, ops[].az_sens as a
//  number, channels.x/u/y ...). System.Text.Json therefore either returned EMPTY lists
//  (scan "groups" missing -> no operating points -> "Run analysis" never enabled) or THREW (summary ->
//  results never loaded).
//
//  Rules used below:
//   * [JsonPropertyName] = the exact key the backend sends.
//   * Every number that the backend can send as null (NaN/inf are cleaned to null) is "double?".
//   * The C# property names the XAML / panels already use are kept; where the backend shape differs,
//     a [JsonIgnore] computed property provides the old name (e.g. FolderScanResult.Groups).
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
    }

    public class InspectResult
    {
        [JsonPropertyName("lin_dir")] public string? LinDir { get; set; }
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
    }

    // ------------------------------------------------------------------ /api/summary
    public class SummaryResult
    {
        [JsonPropertyName("info")] public Dictionary<string, JsonElement>? Info { get; set; }
        [JsonPropertyName("ops")] public List<OpSummary> OperatingPoints { get; set; } = new();
        [JsonPropertyName("campbell")] public CampbellData? Campbell { get; set; }
        [JsonPropertyName("mode")] public string Mode { get; set; } = "open";
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
        /// <summary>NEW: eigenvalue index of this mode at that operating point (for the mode-shape animation).</summary>
        [JsonPropertyName("k")] public int? K { get; set; }
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

    // ------------------------------------------------------------------ NEW: /api/mode_shape (Campbell visualization)
    public class ModeShapeRequest
    {
        [JsonPropertyName("op")] public int? Op { get; set; }
        /// <summary>Eigenvalue index when the Campbell data has it (null otherwise).</summary>
        [JsonPropertyName("k")] public int? K { get; set; }
        /// <summary>Wind speed of the clicked point (used when Op is missing).</summary>
        [JsonPropertyName("wind")] public double? Wind { get; set; }
        /// <summary>0 = lowest-frequency mode of the Campbell diagram at that wind speed, 1 = next, …</summary>
        [JsonPropertyName("mode_index")] public int? ModeIndex { get; set; }
        /// <summary>Natural frequency of the clicked point [Hz] (checks / replaces ModeIndex).</summary>
        [JsonPropertyName("freq")] public double? Freq { get; set; }
        /// <summary>true when the Campbell diagram also shows the hydrodynamic / aerodynamic state modes.</summary>
        [JsonPropertyName("include_all")] public bool IncludeAll { get; set; }
        [JsonPropertyName("scale")] public double Scale { get; set; } = 0.15;
        [JsonPropertyName("n_frames")] public int Frames { get; set; } = 48;
        [JsonPropertyName("tower_len")] public double? TowerLen { get; set; }
        [JsonPropertyName("blade_len")] public double? BladeLen { get; set; }
        [JsonPropertyName("hub_radius")] public double? HubRadius { get; set; }
    }

    /// <summary>One turbine snapshot: every part is [[x...], [y...], [z...]] in metres (X downwind, Y lateral, Z up).</summary>
    public class TurbineFrame
    {
        [JsonPropertyName("tower")] public List<List<double>> Tower { get; set; } = new();
        [JsonPropertyName("nacelle")] public List<List<double>> Nacelle { get; set; } = new();
        [JsonPropertyName("blades")] public List<List<List<double>>> Blades { get; set; } = new();
    }

    public class ModeShapeResult
    {
        [JsonPropertyName("op")] public int Op { get; set; }
        [JsonPropertyName("k")] public int K { get; set; }
        [JsonPropertyName("wind")] public double Wind { get; set; }
        [JsonPropertyName("rotor_rpm")] public double? RotorRpm { get; set; }
        [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
        [JsonPropertyName("family")] public string? Family { get; set; }
        [JsonPropertyName("sym")] public string? Sym { get; set; }
        [JsonPropertyName("whirl")] public double? Whirl { get; set; }
        [JsonPropertyName("fn")] public double? FnHz { get; set; }
        [JsonPropertyName("fd")] public double? FdHz { get; set; }
        [JsonPropertyName("zeta")] public double? Zeta { get; set; }
        [JsonPropertyName("scale")] public double Scale { get; set; }
        [JsonPropertyName("n_frames")] public int FrameCount { get; set; }
        [JsonPropertyName("tower_len")] public double TowerLen { get; set; }
        [JsonPropertyName("blade_len")] public double BladeLen { get; set; }
        [JsonPropertyName("hub_radius")] public double HubRadius { get; set; }
        [JsonPropertyName("length_source")] public string LengthSource { get; set; } = string.Empty;
        /// <summary>[name, relative amplitude (largest = 1), phase in degrees] of every DOF that moves.</summary>
        [JsonPropertyName("dofs")] public List<List<JsonElement>> Dofs { get; set; } = new();
        [JsonPropertyName("ref")] public TurbineFrame? Reference { get; set; }
        [JsonPropertyName("frames")] public List<TurbineFrame> Frames { get; set; } = new();
    }
}
