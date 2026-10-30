using LinBench.Core.Models;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace LinBench.Core.Services
{
    /// <summary>
    /// HTTP client for the LinBench Python backend (Backend/linbench/server.py).
    ///
    /// KEY FIX: the Python server (http.server.BaseHTTPRequestHandler) reads the request body ONLY via the
    /// Content-Length header. HttpClient.PostAsJsonAsync() streams JSON with "Transfer-Encoding: chunked" and
    /// NO Content-Length, so the backend received an EMPTY body for every POST:
    ///   /api/inspect -> {"lin_dir": ""}  (no scan -> no operating points -> Run analysis disabled)
    ///   /api/load    -> 400 "Linearisation folder not found."
    /// All requests go through SendAsync(), which sends a StringContent (Content-Length is set) and never
    /// aborts a request once it has been sent.
    /// </summary>
    public class LinBenchApiClient : ILinBenchApiClient
    {
        private readonly HttpClient _http;
        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                             | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
        };

        private string _baseUrl = "http://127.0.0.1:8765";

        public string BaseUrl
        {
            get => _baseUrl;
            set => _baseUrl = (value ?? string.Empty).TrimEnd('/');
        }

        /// <summary>Last error (HTTP status + backend "error" text, or exception message). Null after a successful call.</summary>
        public string? LastError { get; private set; }

        public LinBenchApiClient(string baseUrl = "http://127.0.0.1:8765", HttpClient? httpClient = null)
        {
            BaseUrl = baseUrl;
            // Local backend: never go through a system/corporate proxy
            _http = httpClient ?? new HttpClient(new HttpClientHandler { UseProxy = false });
            // Scanning / loading a large folder can take minutes
            _http.Timeout = TimeSpan.FromMinutes(10);
        }

        private string Url(string path) => $"{BaseUrl}{path}";

        // ------------------------------------------------------------------ helpers
        //
        // FIX "WPF crashes the backend": a request that was SENT is never aborted any more.
        // Before, the app cancelled requests (folder-scan debounce, ping timeout, health check, leaving the
        // progress screen, closing the app). Python then could not write its answer and printed
        //     ConnectionAbortedError: [WinError 10053] An established connection was aborted ...
        // with a long traceback for every such request - in the start_backend.bat window and in the app's
        // status bar. It looked like the backend crashed. Now cancelling only means "stop waiting":
        // the request runs to the end in the background and the answer is dropped.
        //
        // Heavy calculations are also queued (max 2 at a time). Python's server accepts only 5 waiting
        // connections; more than that and Windows refuses the connection.

        private static readonly SemaphoreSlim _computeGate = new(2, 2);

        private sealed record Reply(int Status, string? Reason, string Body, Exception? Error)
        {
            public bool Ok => Error == null && Status >= 200 && Status < 300;
        }

        private static bool IsLight(string path) =>
            path.StartsWith("/api/ping") || path.StartsWith("/api/progress") || path.StartsWith("/api/version") ||
            path.StartsWith("/api/recent") || path.StartsWith("/api/inspect") || path.StartsWith("/api/load") ||
            path.StartsWith("/api/open_folder");

        /// <summary>Sends one request and always lets it finish. Returns null when the caller gave up waiting
        /// (giveUpAfter elapsed); throws OperationCanceledException when ct was cancelled.</summary>
        private async Task<Reply?> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct,
                                             TimeSpan? giveUpAfter = null)
        {
            bool gated = !IsLight(path);
            if (gated) await _computeGate.WaitAsync(ct);    // still queued = nothing sent yet, safe to cancel
            var url = Url(path);
            // POST bodies need Content-Length (the Python server ignores chunked bodies) -> StringContent
            string? json = body == null ? null : JsonSerializer.Serialize(body, _jsonOpts);

            async Task<Reply> Core()
            {
                try
                {
                    using var req = new HttpRequestMessage(method, url);
                    if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, CancellationToken.None)
                                                .ConfigureAwait(false);
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return new Reply((int)resp.StatusCode, resp.ReasonPhrase, text, null);
                }
                catch (Exception ex)
                {
                    return new Reply(0, null, string.Empty, ex);
                }
                finally
                {
                    if (gated) _computeGate.Release();
                }
            }

            var work = Core();
            var waitLimit = Task.Delay(giveUpAfter ?? System.Threading.Timeout.InfiniteTimeSpan, ct);
            var first = await Task.WhenAny(work, waitLimit);
            if (first != work)
            {
                ct.ThrowIfCancellationRequested();
                return null;                                 // gave up waiting; the request still completes
            }
            return await work;
        }

        private T? Parse<T>(Reply? r, string path) where T : class
        {
            if (r == null) { LastError = $"Timed out waiting for {path}"; return null; }
            if (r.Error != null) { LastError = Describe(r.Error, path); return null; }
            if (!r.Ok) { LastError = ErrorText(r); return null; }
            try
            {
                return JsonSerializer.Deserialize<T>(r.Body, _jsonOpts);
            }
            catch (Exception ex)
            {
                LastError = Describe(ex, path);
                return null;
            }
        }

        private async Task<T?> PostAsync<T>(string path, object body, CancellationToken ct) where T : class
        {
            LastError = null;
            var r = await SendAsync(HttpMethod.Post, path, body, ct);
            return Parse<T>(r, path);
        }

        private async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
        {
            LastError = null;
            var r = await SendAsync(HttpMethod.Get, path, null, ct);
            return Parse<T>(r, path);
        }

        private string Describe(Exception ex, string path) => ex switch
        {
            HttpRequestException h => $"Cannot reach the LinBench backend at {BaseUrl} ({h.Message})",
            TaskCanceledException => $"Timed out waiting for {path}",
            JsonException j => $"Unexpected response from {path}: {j.Message}",
            _ => $"{path}: {ex.Message}"
        };

        /// <summary>The backend answers errors as {"error": "...", "trace": "..."}.</summary>
        private static string ErrorText(Reply r)
        {
            var body = r.Body ?? string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("error", out var e))
                    return e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString();
            }
            catch { /* not JSON */ }
            if (body.Length > 300) body = body[..300] + "...";
            return $"HTTP {r.Status} {r.Reason} {body}".Trim();
        }

        // ------------------------------------------------------------------ API

        public async Task<PingResponse?> PingAsync(CancellationToken ct = default)
        {
            try
            {
                // wait at most 5 s for the answer, but never abort the request (see the note above)
                var r = await SendAsync(HttpMethod.Get, "/api/ping", null, ct, TimeSpan.FromSeconds(5));
                if (r == null || !r.Ok) return null;
                return JsonSerializer.Deserialize<PingResponse>(r.Body, _jsonOpts);
            }
            catch
            {
                return null;
            }
        }

        public Task<AppVersionInfo?> GetVersionAsync(CancellationToken ct = default)
            => GetAsync<AppVersionInfo>("/api/version", ct);

        public Task<InspectResult?> InspectAsync(InspectRequest request, CancellationToken ct = default)
            => PostAsync<InspectResult>("/api/inspect", request, ct);

        public async Task<bool> StartLoadAsync(LoadRequest request, CancellationToken ct = default)
        {
            LastError = null;
            var r = await SendAsync(HttpMethod.Post, "/api/load", request, ct);
            if (r != null && r.Ok) return true;
            LastError = r == null ? "Timed out waiting for /api/load"
                      : r.Error != null ? Describe(r.Error, "/api/load") : ErrorText(r);
            return false;
        }

        public Task<ProgressUpdate?> GetProgressAsync(int since = 0, CancellationToken ct = default)
            => GetAsync<ProgressUpdate>($"/api/progress?since={since}", ct);

        public Task<SummaryResult?> GetSummaryAsync(CancellationToken ct = default)
            => GetAsync<SummaryResult>("/api/summary", ct);

        public async Task<List<RecentFolder>> GetRecentAsync(CancellationToken ct = default)
            => await GetAsync<List<RecentFolder>>("/api/recent", ct) ?? new List<RecentFolder>();

        // CHANGED: the backend returns {op, wind, modes:[...]} – not a bare list
        public async Task<List<ModeItem>> GetModesAsync(int opIndex, CancellationToken ct = default)
            => (await GetAsync<ModesResponse>($"/api/modes?op={opIndex}", ct))?.Modes ?? new List<ModeItem>();

        public Task<CampbellData?> GetCampbellAsync(bool all = false, bool closed = false, CancellationToken ct = default)
        {
            var query = new List<string>();
            if (all) query.Add("all=1");
            if (closed) query.Add("closed=1");
            var q = query.Count > 0 ? "?" + string.Join("&", query) : "";
            return GetAsync<CampbellData>($"/api/campbell{q}", ct);
        }

        public Task<ClosedLoopDetail?> GetClosedAsync(int opIndex, CancellationToken ct = default)
            => GetAsync<ClosedLoopDetail>($"/api/closed?op={opIndex}", ct);

        public Task<Dictionary<string, JsonElement>?> GetInteractionAsync(CancellationToken ct = default)
            => GetAsync<Dictionary<string, JsonElement>>("/api/interaction", ct);

        public Task<Dictionary<string, JsonElement>?> GetDelayMarginsAsync(double tau, CancellationToken ct = default)
            => GetAsync<Dictionary<string, JsonElement>>(
                $"/api/delay?tau={tau.ToString(System.Globalization.CultureInfo.InvariantCulture)}", ct);

        public Task<TfResult?> CalculateTfAsync(TfRequest request, CancellationToken ct = default)
            => PostAsync<TfResult>("/api/tf", request, ct);

        public Task<TimeResult?> CalculateTimeAsync(TimeRequest request, CancellationToken ct = default)
            => PostAsync<TimeResult>("/api/time", request, ct);

        public Task<TfResult?> CalculateClosedTfAsync(TfRequest request, CancellationToken ct = default)
            => PostAsync<TfResult>("/api/closed_tf", request, ct);

        public Task<TimeResult?> CalculateClosedTimeAsync(TimeRequest request, CancellationToken ct = default)
            => PostAsync<TimeResult>("/api/closed_time", request, ct);

        public Task<GainMapResult?> CalculateGainMapAsync(GainMapRequest request, CancellationToken ct = default)
            => PostAsync<GainMapResult>("/api/gain_map", request, ct);

        public Task<Dictionary<string, JsonElement>?> CalculateGainPointAsync(int op, double fx, double fy, string pair = "pi", CancellationToken ct = default)
            => PostAsync<Dictionary<string, JsonElement>>("/api/gain_point", new { op, fx, fy, pair }, ct);

        public Task<Dictionary<string, JsonElement>?> CalculateGainScalingAsync(CancellationToken ct = default)
            => PostAsync<Dictionary<string, JsonElement>>("/api/gain_scaling", new { }, ct);

        // NOTE: the backend REQUIRES at least one input and one output index
        public Task<StructureResult?> CalculateStructureAsync(int op, List<int>? inputs = null, List<int>? outputs = null, CancellationToken ct = default)
            => PostAsync<StructureResult>("/api/structure",
                new { op, inputs = inputs ?? new List<int>(), outputs = outputs ?? new List<int>() }, ct);

        public Task<ClosedLoopRow?> SetRegionAsync(int op, string region, CancellationToken ct = default)
            => PostAsync<ClosedLoopRow>("/api/region", new { op, region }, ct);

        public async Task<bool> OpenFolderInExplorerAsync(string path, CancellationToken ct = default)
        {
            try
            {
                var r = await SendAsync(HttpMethod.Post, "/api/open_folder", new { path }, ct);
                return r != null && r.Ok;
            }
            catch
            {
                return false;
            }
        }
    }
}
