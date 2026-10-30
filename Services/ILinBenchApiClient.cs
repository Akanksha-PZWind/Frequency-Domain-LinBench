using LinBench.Core.Models;
using System.Text.Json;

namespace LinBench.Core.Services
{
    public interface ILinBenchApiClient
    {
        string BaseUrl { get; set; }

        /// <summary>NEW: last error text from the backend (or connection problem). Null after a successful call.</summary>
        string? LastError { get; }

        Task<PingResponse?> PingAsync(CancellationToken ct = default);
        Task<AppVersionInfo?> GetVersionAsync(CancellationToken ct = default);
        Task<InspectResult?> InspectAsync(InspectRequest request, CancellationToken ct = default);
        Task<bool> StartLoadAsync(LoadRequest request, CancellationToken ct = default);
        Task<ProgressUpdate?> GetProgressAsync(int since = 0, CancellationToken ct = default);
        Task<SummaryResult?> GetSummaryAsync(CancellationToken ct = default);
        Task<List<RecentFolder>> GetRecentAsync(CancellationToken ct = default);
        Task<List<ModeItem>> GetModesAsync(int opIndex, CancellationToken ct = default);
        Task<CampbellData?> GetCampbellAsync(bool all = false, bool closed = false, CancellationToken ct = default);
        Task<ClosedLoopDetail?> GetClosedAsync(int opIndex, CancellationToken ct = default);
        Task<Dictionary<string, JsonElement>?> GetInteractionAsync(CancellationToken ct = default);
        Task<Dictionary<string, JsonElement>?> GetDelayMarginsAsync(double tau, CancellationToken ct = default);
        Task<TfResult?> CalculateTfAsync(TfRequest request, CancellationToken ct = default);
        Task<TimeResult?> CalculateTimeAsync(TimeRequest request, CancellationToken ct = default);
        Task<TfResult?> CalculateClosedTfAsync(TfRequest request, CancellationToken ct = default);
        Task<TimeResult?> CalculateClosedTimeAsync(TimeRequest request, CancellationToken ct = default);
        Task<GainMapResult?> CalculateGainMapAsync(GainMapRequest request, CancellationToken ct = default);
        Task<Dictionary<string, JsonElement>?> CalculateGainPointAsync(int op, double fx, double fy, string pair = "pi", CancellationToken ct = default);
        Task<Dictionary<string, JsonElement>?> CalculateGainScalingAsync(CancellationToken ct = default);
        Task<StructureResult?> CalculateStructureAsync(int op, List<int>? inputs = null, List<int>? outputs = null, CancellationToken ct = default);
        Task<ClosedLoopRow?> SetRegionAsync(int op, string region, CancellationToken ct = default);
        Task<bool> OpenFolderInExplorerAsync(string path, CancellationToken ct = default);
    }
}
