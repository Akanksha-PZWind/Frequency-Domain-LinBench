using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LinBench.Core.Models;

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
        Task<CampbellData?> GetCampbellAsync(bool all = false, CancellationToken ct = default);
        /// <summary>NEW: animation frames of one mode at one operating point (Campbell visualization).</summary>
        Task<ModeShapeResult?> GetModeShapeAsync(ModeShapeRequest request, CancellationToken ct = default);
        Task<bool> OpenFolderInExplorerAsync(string path, CancellationToken ct = default);
    }
}
