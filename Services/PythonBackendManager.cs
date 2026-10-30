using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LinBench.Core.Models;

namespace LinBench.Core.Services
{
    public interface IPythonBackendManager : IDisposable
    {
        bool IsRunning { get; }
        int Port { get; }
        string BaseUrl { get; }
        event EventHandler<string>? OutputReceived;
        event EventHandler<string>? ErrorReceived;

        Task<bool> EnsureRunningAsync(int port = 8765, string? customPython = null, CancellationToken ct = default);

        /// <summary>Re-attach after the connection was lost. Starts a backend only if the port is free
        /// (never a second one next to a busy backend).</summary>
        Task<bool> ReconnectAsync(int port, string? customPython = null, CancellationToken ct = default);

        /// <summary>Every line python.exe prints – for the analysis log.</summary>
        event EventHandler<string>? BackendLine;

        void Stop();
    }

    /// <summary>
    /// Starts (or attaches to) the LinBench Python backend exactly like start_backend.bat does:
    ///     python -m linbench --port 8765 --no-browser      (working dir = folder that CONTAINS "linbench")
    ///
    /// WHY THIS WAS REWRITTEN (works from start_backend.bat, fails when started from Visual Studio / VS Code):
    ///  1. Python was picked from a hard-coded list (Python314, Python313 … in AppData / Program Files) BEFORE the
    ///     "python" on PATH that the .bat uses, so the app could start a different Python that has no numpy/scipy.
    ///  2. Visual Studio / VS Code pass their OWN environment to the app. If Python (or pip packages, or a PATH change)
    ///     was installed after the IDE was opened, the IDE's PATH is stale. A double-clicked .bat always gets the
    ///     current PATH. -> We now rebuild PATH from the registry (User + Machine) exactly like a new cmd window.
    ///  3. Nothing checked that numpy/scipy import. The .bat checks and runs "pip install" – now we do too.
    ///  4. "Stop debugging" kills LinBench.Wpf.exe without running Closing, so python.exe was left running on
    ///     port 8765. Windows lets a second python bind the SAME port (SO_REUSEADDR), so requests were split between
    ///     two backends (load on one, progress on the other). -> The child is now in a kill-on-close Job Object,
    ///     a busy/foreign port is detected, and the real port is read from the backend's own "running at" line.
    ///  5. Anaconda/Miniconda Pythons need their Library\bin folders on PATH to load numpy's DLLs (an "Anaconda
    ///     Prompt" sets these; the IDE does not). -> Added automatically.
    /// Everything the backend prints is written to %USERPROFILE%\.linbench\wpf_backend.log.
    /// The backend (Backend\linbench) serves only what the Operating points and Campbell pages need.
    /// </summary>
    public class PythonBackendManager : IPythonBackendManager
    {
        private Process? _process;
        private readonly ILinBenchApiClient _api;
        private readonly SemaphoreSlim _startLock = new(1, 1);
        private bool _disposed;
        private static PythonInfo? _resolvedPython;              // cached for the lifetime of the app
        private static readonly object _logLock = new();

        public bool IsRunning
        {
            get
            {
                try { return _process != null && !_process.HasExited; }
                catch { return false; }
            }
        }

        public int Port { get; private set; } = 8765;
        public string BaseUrl => $"http://127.0.0.1:{Port}";

        /// <summary>First start: numpy/scipy import can be slow (antivirus scans every .pyd). Keep waiting while the process lives.</summary>
        public int StartupTimeoutSeconds { get; set; } = 180;

        public static string LogFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".linbench", "wpf_backend.log");

        public event EventHandler<string>? OutputReceived;
        public event EventHandler<string>? ErrorReceived;

        /// <summary>Every line python.exe prints (stdout and stderr) – for the analysis log only, not the status bar.</summary>
        public event EventHandler<string>? BackendLine;

        private readonly Queue<string> _lastErrLines = new();
        private bool _stopping;

        public PythonBackendManager(ILinBenchApiClient api)
        {
            _api = api;
        }

        // ================================================================== start / attach

        public async Task<bool> EnsureRunningAsync(int port = 8765, string? customPython = null, CancellationToken ct = default)
        {
            await _startLock.WaitAsync(ct);
            try
            {
                // Our own backend is alive -> just (re)connect to it
                if (IsRunning)
                {
                    _api.BaseUrl = BaseUrl;
                    return await WaitForPingAsync(ct);
                }

                Port = port;
                _api.BaseUrl = BaseUrl;

                // 1) A LinBench backend already answers – on the configured port OR anywhere in 8765..8784
                //    (start_backend.bat always uses 8765, and the backend moves up when a port is taken).
                var found = await FindRunningBackendAsync(port, ct);
                if (found != null)
                {
                    Port = found.Value.port;
                    _api.BaseUrl = BaseUrl;
                    Info($"Connected to the running LinBench backend at {BaseUrl} (v{found.Value.version})");
                    return true;
                }

                // 2) Port taken but not answering: usually a LinBench that is busy computing or still importing
                //    numpy/scipy (start_backend.bat just started). Wait for it – starting a SECOND backend here
                //    splits the session (load on one, progress/summary on the other).
                int usePort = port;
                if (!IsPortFree(port))
                {
                    Info($"Port {port} is in use – waiting up to 45 s for it to answer as LinBench…");
                    var sw = Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < 45)
                    {
                        ct.ThrowIfCancellationRequested();
                        var v = await PingPortAsync(port, TimeSpan.FromSeconds(5), ct);
                        if (v != null)
                        {
                            Info($"Connected to the running LinBench backend at {BaseUrl} (v{v})");
                            return true;
                        }
                        if (IsPortFree(port)) break;      // it went away – start our own on it
                        await Task.Delay(1000, ct);
                    }
                    if (!IsPortFree(port))
                    {
                        usePort = FindFreePort(port + 20);  // outside the range the backend itself scans
                        Info($"Port {port} is used by a program that is not LinBench – using port {usePort} instead.");
                    }
                }
                Port = usePort;
                _api.BaseUrl = BaseUrl;

                // 3) What to launch: pre-built LinBench.exe (PyInstaller) or python -m linbench
                var psi = new ProcessStartInfo
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,     // the backend never reads stdin; avoids inheriting the IDE's handle
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                var freshPath = BuildFreshPath();
                psi.Environment["PATH"] = freshPath;
                psi.Environment["PYTHONUNBUFFERED"] = "1";
                psi.Environment["PYTHONIOENCODING"] = "utf-8";

                var prebuiltExe = FindPrebuiltBackendExe();
                if (!string.IsNullOrEmpty(prebuiltExe))
                {
                    psi.FileName = prebuiltExe;
                    psi.WorkingDirectory = Path.GetDirectoryName(prebuiltExe) ?? AppContext.BaseDirectory;
                    psi.ArgumentList.Add("--port");
                    psi.ArgumentList.Add(usePort.ToString());
                    psi.ArgumentList.Add("--no-browser");
                    Info($"Using pre-built backend executable: {prebuiltExe}");
                }
                else
                {
                    var backendDir = FindLinBenchSourceDirectory();
                    if (backendDir == null)
                    {
                        Error("Backend folder not found: no 'linbench' package next to LinBench.Wpf.exe or in the project. " +
                              "Rebuild the solution (the .csproj copies Backend\\ to the output folder) or set the " +
                              "environment variable LINBENCH_SOURCE_DIR to the folder that contains 'linbench'.");
                        return false;
                    }
                    Info($"Backend source folder: {backendDir}");

                    var py = await ResolvePythonAsync(customPython, backendDir, freshPath, ct);
                    if (py == null) return false;

                    psi.FileName = py.Exe;
                    psi.WorkingDirectory = backendDir;
                    psi.ArgumentList.Add("-u");
                    psi.ArgumentList.Add("-m");
                    psi.ArgumentList.Add("linbench");
                    psi.ArgumentList.Add("--port");
                    psi.ArgumentList.Add(usePort.ToString());
                    psi.ArgumentList.Add("--no-browser");

                    psi.Environment["PATH"] = PathForPython(py.Exe, freshPath);
                    var existing = Environment.GetEnvironmentVariable("PYTHONPATH");
                    psi.Environment["PYTHONPATH"] = string.IsNullOrEmpty(existing)
                        ? backendDir
                        : backendDir + Path.PathSeparator + existing;
                    // a PYTHONHOME inherited from another tool breaks a normal python.exe
                    psi.Environment.Remove("PYTHONHOME");
                }

                Info($"Starting backend: \"{psi.FileName}\" {string.Join(" ", psi.ArgumentList)}   (in {psi.WorkingDirectory})");

                _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _process.OutputDataReceived += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    DetectPort(e.Data);
                    WriteLog("py  ", e.Data);
                    BackendLine?.Invoke(this, e.Data);
                };
                // FIX: Python's stderr (warnings, tracebacks of single requests) was shown in the status bar as
                // "Backend: ..." and looked like a crash. It now goes to the log; a real crash is reported by Exited.
                _process.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    WriteLog("py! ", e.Data);
                    lock (_lastErrLines)
                    {
                        _lastErrLines.Enqueue(e.Data);
                        while (_lastErrLines.Count > 6) _lastErrLines.Dequeue();
                    }
                    BackendLine?.Invoke(this, e.Data);
                };
                _process.Exited += (s, e) =>
                {
                    if (_stopping) return;
                    int code = -1;
                    try { code = ((Process)s!).ExitCode; } catch { }
                    string tail;
                    lock (_lastErrLines) tail = string.Join(" | ", _lastErrLines);
                    Error($"The Python backend stopped (exit code {code}). {tail}  – details: {LogFilePath}");
                };
                _stopping = false;

                try
                {
                    _process.Start();
                    try { _process.StandardInput.Close(); } catch { }
                    KillOnCloseJob.Add(_process);
                }
                catch (Exception ex)
                {
                    Error($"Could not start '{psi.FileName}': {ex.Message}. Install Python 3.10-3.12 (tick 'Add python.exe to PATH'), " +
                          "run 'python -m pip install numpy scipy', or set the Python path in Settings.");
                    _process.Dispose();
                    _process = null;
                    return false;
                }

                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                return await WaitForPingAsync(ct);
            }
            finally
            {
                _startLock.Release();
            }
        }

        public async Task<bool> ReconnectAsync(int port, string? customPython = null, CancellationToken ct = default)
        {
            if (IsRunning) return await EnsureRunningAsync(port, customPython, ct);   // just waits for our own

            var found = await FindRunningBackendAsync(Port, ct);
            if (found == null && Port != port) found = await FindRunningBackendAsync(port, ct);
            if (found != null)
            {
                Port = found.Value.port;
                _api.BaseUrl = BaseUrl;
                Info($"Reconnected to the LinBench backend at {BaseUrl}");
                return true;
            }
            if (!IsPortFree(port))
            {
                Info($"The backend on port {port} is busy – waiting for it…");
                return false;
            }
            return await EnsureRunningAsync(port, customPython, ct);
        }

        // ---- direct port probes (independent of _api.BaseUrl)
        private static readonly HttpClient _probeHttp = new(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromMinutes(2)
        };

        /// <summary>LinBench version if http://127.0.0.1:port/api/ping answers as LinBench within the timeout, else null.</summary>
        private static async Task<string?> PingPortAsync(int port, TimeSpan timeout, CancellationToken ct)
        {
            // FIX: do not abort the request when we stop waiting – an aborted request makes Python print a
            // ConnectionAbortedError traceback (looks like a crash). Wait up to 'timeout', then just ignore it.
            async Task<string?> Core()
            {
                try
                {
                    var json = await _probeHttp.GetStringAsync($"http://127.0.0.1:{port}/api/ping").ConfigureAwait(false);
                    var ping = System.Text.Json.JsonSerializer.Deserialize<PingResponse>(json);
                    return IsLinBench(ping) ? (ping!.Version ?? "") : null;
                }
                catch
                {
                    return null;
                }
            }

            try
            {
                var work = Core();
                var first = await Task.WhenAny(work, Task.Delay(timeout, ct));
                return first == work ? await work : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Checks the configured port and 8765..8784 in parallel; returns the first LinBench found.</summary>
        private static async Task<(int port, string version)?> FindRunningBackendAsync(int preferred, CancellationToken ct)
        {
            var ports = new List<int> { preferred };
            for (int p = 8765; p < 8785; p++) if (p != preferred) ports.Add(p);

            var listening = await Task.WhenAll(ports.Select(p => IsListeningAsync(p, 300)));
            var tasks = ports.Select((p, i) => listening[i]
                ? PingPortAsync(p, TimeSpan.FromSeconds(p == preferred ? 5 : 3), ct)
                : Task.FromResult<string?>(null)).ToArray();
            var results = await Task.WhenAll(tasks);
            for (int i = 0; i < ports.Count; i++)     // preferred port first, then the lowest
                if (results[i] != null) return (ports[i], results[i]!);
            return null;
        }

        /// <summary>FAST START: true when something accepts connections on the port (answers in &lt; 1 ms locally).
        /// Pinging a port where nothing listens costs about 2 s on Windows (the connect is retried), so check first.</summary>
        private static async Task<bool> IsListeningAsync(int port, int timeoutMs)
        {
            var client = new TcpClient();
            try
            {
                var connect = client.ConnectAsync(IPAddress.Loopback, port);
                var first = await Task.WhenAny(connect, Task.Delay(timeoutMs));
                if (first != connect)
                {
                    _ = connect.ContinueWith(t => { _ = t.Exception; client.Dispose(); }, TaskScheduler.Default);
                    return false;
                }
                await connect;
                bool ok = client.Connected;
                client.Dispose();
                return ok;
            }
            catch
            {
                client.Dispose();
                return false;
            }
        }

        private static bool IsLinBench(PingResponse? ping) =>
            ping != null && ping.Ok &&
            (string.IsNullOrEmpty(ping.App) || ping.App.Equals("LinBench", StringComparison.OrdinalIgnoreCase));

        private static readonly Regex RunningAt = new(@"running at https?://[^:/\s]+:(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The backend tries port..port+19 and prints the one it really got – follow it.</summary>
        private void DetectPort(string line)
        {
            var m = RunningAt.Match(line);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var p) && p != Port)
            {
                Port = p;
                _api.BaseUrl = BaseUrl;
                WriteLog("wpf ", $"Backend reports port {p} – following it.");
            }
        }

        private async Task<bool> WaitForPingAsync(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var nextNote = 10;
            while (sw.Elapsed.TotalSeconds < StartupTimeoutSeconds && !ct.IsCancellationRequested)
            {
                if (_process != null && _process.HasExited)
                {
                    int code = -1;
                    try { code = _process.ExitCode; } catch { }
                    Error($"Backend process exited (code {code}). See {LogFilePath} for the Python error. " +
                          "Most common cause: numpy/scipy not installed for this Python → run:  python -m pip install numpy scipy");
                    return false;
                }

                await Task.Delay(150, ct);
                _api.BaseUrl = BaseUrl;   // may have changed through DetectPort
                // not bound yet: do not ping (a refused connect costs about 2 s on Windows)
                if (await IsListeningAsync(Port, 300))
                {
                    var ping = await _api.PingAsync(ct);
                    if (IsLinBench(ping))
                    {
                        Info($"LinBench backend is ready at {BaseUrl} (v{ping!.Version}) after {sw.Elapsed.TotalSeconds:0.0} s");
                        return true;
                    }
                }

                if (sw.Elapsed.TotalSeconds >= nextNote)
                {
                    Info($"Still waiting for the backend to start ({(int)sw.Elapsed.TotalSeconds}s – the first start can be slow while numpy/scipy load)…");
                    nextNote += 10;
                }
            }

            Error($"Timed out after {StartupTimeoutSeconds}s waiting for {BaseUrl}/api/ping. See {LogFilePath}.");
            return false;
        }

        public void Stop()
        {
            try
            {
                _stopping = true;
                if (_process != null && !_process.HasExited)
                {
                    Info("Stopping Python backend process...");
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(2000);
                }
            }
            catch { }
            finally
            {
                _process?.Dispose();
                _process = null;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();
                _startLock.Dispose();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        // ================================================================== messages / log

        private void Info(string msg)
        {
            WriteLog("wpf ", msg);
            OutputReceived?.Invoke(this, msg);
        }

        private void Error(string msg)
        {
            WriteLog("ERR ", msg);
            ErrorReceived?.Invoke(this, msg);
        }

        private static void WriteLog(string tag, string text)
        {
            try
            {
                lock (_logLock)
                {
                    var path = LogFilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 2_000_000) fi.Delete();     // keep it small
                    File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {tag}{text}{Environment.NewLine}");
                }
            }
            catch { }
        }

        // ================================================================== ports

        private static bool IsPortFree(int port)
        {
            // Something accepts connections there?
            try
            {
                using var client = new TcpClient();
                var connect = client.ConnectAsync(IPAddress.Loopback, port);
                if (connect.Wait(400) && client.Connected) return false;
            }
            catch { /* refused = nobody listening */ }

            // Can we bind it exclusively?
            try
            {
                var l = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
                l.Start();
                l.Stop();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int FindFreePort(int start)
        {
            for (int p = start; p < start + 200 && p < 65535; p++)
                if (IsPortFree(p)) return p;
            // let Windows choose one
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        // ================================================================== backend discovery

        private static string? FindPrebuiltBackendExe()
        {
            if (!OperatingSystem.IsWindows()) return null;

            var baseDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "Backend", "LinBench.exe"),
                Path.Combine(baseDir, "Backend", "dist", "LinBench", "LinBench.exe"),
                Path.Combine(baseDir, "LinBench", "LinBench.exe"),
                Path.Combine(baseDir, "..", "Backend", "LinBench.exe"),
                Path.Combine(baseDir, "..", "LinBench", "LinBench.exe"),
            };

            foreach (var cand in candidates)
            {
                try { if (File.Exists(cand)) return Path.GetFullPath(cand); } catch { }
            }
            return null;
        }

        /// <summary>Folder that CONTAINS the "linbench" package, or null.</summary>
        private static string? FindLinBenchSourceDirectory()
        {
            var baseDir = AppContext.BaseDirectory;
            string?[] candidates =
            {
                Environment.GetEnvironmentVariable("LINBENCH_SOURCE_DIR"),
                Path.Combine(baseDir, "Backend"),                                   // copied next to the exe by the .csproj
                Path.Combine(baseDir, "..", "..", "..", "Backend"),                 // project folder (bin\Debug\net8.0-windows)
                Path.Combine(baseDir, "..", "..", "..", "..", "Backend"),           // solution folder
                Path.Combine(baseDir, "..", "..", "..", "..", "..", "Backend"),     // bin\Debug\net8.0-windows\win-x64
                baseDir
            };

            foreach (var cand in candidates)
            {
                if (string.IsNullOrWhiteSpace(cand)) continue;
                string full;
                try { full = Path.GetFullPath(cand); } catch { continue; }
                var pkg = Path.Combine(full, "linbench");
                if (File.Exists(Path.Combine(pkg, "__main__.py")) && File.Exists(Path.Combine(pkg, "server.py")))
                    return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            return null;
        }

        // ================================================================== python discovery (same rules as start_backend.bat)

        private sealed class PythonInfo
        {
            public string Exe = "";
            public string Version = "";
            public string Missing = "";          // "" = numpy and scipy both import
            public bool Ok => Missing.Length == 0;
        }

        private const string ProbeScript =
@"import sys, importlib.util
missing = []
for name in ('numpy', 'scipy'):
    try:
        if importlib.util.find_spec(name) is None:
            missing.append(name + ' (not installed)')
    except Exception as exc:
        missing.append(name + ' (' + str(exc).strip()[:150] + ')')
print('LBPROBE|' + sys.executable + '|' + sys.version.split()[0] + '|' + '; '.join(missing))
";

        private async Task<PythonInfo?> ResolvePythonAsync(string? customPython, string backendDir, string freshPath, CancellationToken ct)
        {
            if (_resolvedPython != null && File.Exists(_resolvedPython.Exe) &&
                (string.IsNullOrWhiteSpace(customPython) || SamePath(customPython, _resolvedPython.Exe)))
                return _resolvedPython;

            var probeFile = Path.Combine(Path.GetTempPath(), "linbench_probe.py");
            try { File.WriteAllText(probeFile, ProbeScript, new UTF8Encoding(false)); }
            catch (Exception ex) { Error($"Cannot write {probeFile}: {ex.Message}"); return null; }

            var candidates = PythonCandidates(customPython, backendDir, freshPath);
            Info("Looking for a Python with numpy and scipy (same as start_backend.bat)…");

            var runnable = new List<PythonInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (exe, prefix) in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var info = await ProbeAsync(exe, prefix, probeFile, freshPath, ct);
                if (info == null) continue;
                if (!seen.Add(info.Exe)) continue;

                if (!IsSupportedVersion(info.Version))
                {
                    WriteLog("wpf ", $"  skip {info.Exe} (Python {info.Version} – LinBench needs 3.9 or newer)");
                    continue;
                }
                if (info.Ok)
                {
                    Info($"Using Python {info.Version}: {info.Exe}");
                    _resolvedPython = info;
                    RememberPython(info.Exe);
                    return info;
                }
                WriteLog("wpf ", $"  {info.Exe} (Python {info.Version}) is missing: {info.Missing}");
                runnable.Add(info);
            }

            if (runnable.Count == 0)
            {
                Error("Python was not found. Install Python 3.10-3.12 from https://www.python.org/downloads/ and tick " +
                      "'Add python.exe to PATH', or enter the full path of python.exe in Settings. " +
                      "(Restart Visual Studio / VS Code after installing Python so it sees the new PATH.)");
                return null;
            }

            // Like start_backend.bat: install the runtime dependencies into the first Python found on PATH
            var target = runnable[0];
            Info($"Python {target.Version} ({target.Exe}) is missing {target.Missing}. Installing numpy scipy with pip (one time)…");
            var pipOk = await RunPipInstallAsync(target.Exe, freshPath, ct);
            var after = await ProbeAsync(target.Exe, Array.Empty<string>(), probeFile, freshPath, ct);
            if (pipOk && after != null && after.Ok)
            {
                Info($"Dependencies installed. Using Python {after.Version}: {after.Exe}");
                _resolvedPython = after;
                RememberPython(after.Exe);
                return after;
            }

            Error($"Could not prepare Python ({target.Exe}): {(after?.Missing ?? target.Missing)}. " +
                  $"Open a command prompt and run:  \"{target.Exe}\" -m pip install numpy scipy   " +
                  $"(details in {LogFilePath})");
            return null;
        }

        private static bool IsSupportedVersion(string version)
        {
            var parts = version.Split('.');
            if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
                return true; // unknown – let it try
            return major > 3 || (major == 3 && minor >= 9);
        }

        // FAST START: the Python that worked is stored in %USERPROFILE%\.linbench\wpf_python.txt and tried first next time
        private static string RememberedPythonFile =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".linbench", "wpf_python.txt");

        private static string? ReadRememberedPython()
        {
            try { return File.Exists(RememberedPythonFile) ? File.ReadAllText(RememberedPythonFile).Trim() : null; }
            catch { return null; }
        }

        private static void RememberPython(string exe)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RememberedPythonFile)!);
                File.WriteAllText(RememberedPythonFile, exe);
            }
            catch { }
        }

        private static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>Candidate interpreters, in the order start_backend.bat would find them (Settings and venvs first).</summary>
        private static List<(string exe, string[] prefix)> PythonCandidates(string? customPath, string backendDir, string freshPath)
        {
            var list = new List<(string, string[])>();
            var none = Array.Empty<string>();
            void Add(string? p)
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                p = p.Trim().Trim('"');
                try { if (File.Exists(p)) list.Add((Path.GetFullPath(p), none)); } catch { }
            }

            bool win = OperatingSystem.IsWindows();
            string exeName = win ? "python.exe" : "python3";
            string Venv(string root) => win ? Path.Combine(root, "Scripts", "python.exe") : Path.Combine(root, "bin", "python");

            // 1) explicit choices, then the Python that worked last time (fast start)
            Add(customPath);
            Add(Environment.GetEnvironmentVariable("LINBENCH_PYTHON"));
            Add(ReadRememberedPython());

            // 2) a virtual environment next to the backend / project / solution
            var baseDir = AppContext.BaseDirectory;
            foreach (var root in new[]
                     {
                         backendDir,
                         Path.Combine(baseDir, "..", "..", "..", "Backend"),
                         Path.Combine(baseDir, "..", "..", "..", "..", "Backend"),
                         Path.Combine(baseDir, "..", "..", ".."),
                         Path.Combine(baseDir, "..", "..", "..", ".."),
                         baseDir
                     })
            {
                foreach (var v in new[] { ".venv", "venv", "env", ".venv-build" })
                    Add(Venv(Path.Combine(root, v)));
            }

            // 3) "python" on the (fresh) PATH – exactly what start_backend.bat runs
            foreach (var dir in freshPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string d = dir.Trim().Trim('"');
                if (d.Length == 0) continue;
                Add(Path.Combine(d, exeName));
                if (!win) Add(Path.Combine(d, "python"));
            }

            // 4) the "py" launcher (start_backend.bat falls back to it)
            if (win)
            {
                var pyLaunchers = freshPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Select(d => Path.Combine(d.Trim().Trim('"'), "py.exe"))
                    .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "py.exe"))
                    .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Launcher", "py.exe"));
                foreach (var py in pyLaunchers)
                {
                    try { if (File.Exists(py)) { list.Add((Path.GetFullPath(py), new[] { "-3" })); break; } } catch { }
                }
            }

            // 5) usual install folders (newest first)
            if (win)
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

                foreach (var parent in new[]
                         {
                             Path.Combine(local, "Programs", "Python"),     // python.org installer (per user)
                             pf,                                              // python.org installer (all users)
                             Path.Combine(local, "Python"),                  // new "Python install manager" (pythoncore-3.x-64)
                         })
                {
                    try
                    {
                        if (!Directory.Exists(parent)) continue;
                        foreach (var d in Directory.GetDirectories(parent, "Python3*").Concat(Directory.GetDirectories(parent, "pythoncore-3*"))
                                                   .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
                            Add(Path.Combine(d, "python.exe"));
                    }
                    catch { }
                }

                foreach (var conda in new[] { "anaconda3", "miniconda3", "miniforge3", "mambaforge", "Anaconda3", "Miniconda3" })
                {
                    Add(Path.Combine(user, conda, "python.exe"));
                    Add(Path.Combine(pd, conda, "python.exe"));
                    Add(Path.Combine(local, conda, "python.exe"));
                }
            }
            else
            {
                Add("/usr/bin/python3");
                Add("/usr/local/bin/python3");
                Add("/opt/homebrew/bin/python3");
            }
            return list;
        }

        private async Task<PythonInfo?> ProbeAsync(string exe, string[] prefix, string probeFile, string freshPath, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in prefix) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(probeFile);
            psi.Environment["PATH"] = PathForPython(exe, freshPath);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment.Remove("PYTHONHOME");

            try
            {
                using var p = new Process { StartInfo = psi };
                p.Start();
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(30));   // the probe only looks the packages up (no import)
                try { await p.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    try { p.Kill(true); } catch { }
                    if (ct.IsCancellationRequested) throw;
                    WriteLog("wpf ", $"  probe timed out: {exe}");
                    return null;
                }

                var stdout = await outTask;
                var line = stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("LBPROBE|"));
                if (line == null)
                {
                    var err = (await errTask).Trim();
                    WriteLog("wpf ", $"  not usable: {exe} {string.Join(" ", prefix)} (exit {p.ExitCode}) {Short(err)}");
                    return null;
                }
                var parts = line.Split('|', 4);
                if (parts.Length < 4) return null;
                // For the py launcher (and Store/WindowsApps aliases) run the real interpreter afterwards
                var real = parts[1].Trim();
                return new PythonInfo
                {
                    Exe = File.Exists(real) ? real : exe,
                    Version = parts[2].Trim(),
                    Missing = parts[3].Trim()
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                WriteLog("wpf ", $"  cannot run {exe}: {ex.Message}");
                return null;
            }
        }

        private static string Short(string s) => s.Length > 300 ? s[..300] + "…" : s;

        private async Task<bool> RunPipInstallAsync(string pythonExe, string freshPath, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // requirements.txt also lists PyInstaller + PySide6 (build only, ~300 MB) – the backend needs only these:
            foreach (var a in new[] { "-m", "pip", "install", "--disable-pip-version-check", "numpy", "scipy" })
                psi.ArgumentList.Add(a);
            psi.Environment["PATH"] = PathForPython(pythonExe, freshPath);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";

            try
            {
                using var p = new Process { StartInfo = psi };
                p.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Info("pip: " + e.Data); };
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) WriteLog("pip!", e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMinutes(15));
                await p.WaitForExitAsync(cts.Token);
                return p.ExitCode == 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Error($"pip install failed: {ex.Message}");
                return false;
            }
        }

        // ================================================================== environment

        /// <summary>
        /// PATH as a NEW command prompt would see it (Machine + User from the registry) plus this process' PATH.
        /// Visual Studio / VS Code keep the PATH they were started with; a .bat started from Explorer gets the current one.
        /// </summary>
        private static string BuildFreshPath()
        {
            var parts = new List<string>();
            void AddAll(string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                foreach (var raw in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = Environment.ExpandEnvironmentVariables(raw.Trim());
                    if (p.Length > 0 && !parts.Contains(p, StringComparer.OrdinalIgnoreCase)) parts.Add(p);
                }
            }

            if (OperatingSystem.IsWindows())
            {
                // Same order as Windows builds it for a new process: Machine first, then User
                AddAll(ReadRegistryPath(machine: true));
                AddAll(ReadRegistryPath(machine: false));
            }
            AddAll(Environment.GetEnvironmentVariable("PATH"));
            return string.Join(Path.PathSeparator, parts);
        }

        private static string? ReadRegistryPath(bool machine)
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                using var key = machine
                    ? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment")
                    : Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment");
                return key?.GetValue("Path", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            }
            catch { return null; }
        }

        /// <summary>Conda Pythons need their DLL folders on PATH (an "Anaconda Prompt" adds them; the IDE does not).</summary>
        private static string PathForPython(string pythonExe, string basePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(pythonExe));
                if (string.IsNullOrEmpty(dir)) return basePath;
                var extra = new List<string> { dir };
                if (Directory.Exists(Path.Combine(dir, "conda-meta")))
                {
                    extra.Add(Path.Combine(dir, "Library", "mingw-w64", "bin"));
                    extra.Add(Path.Combine(dir, "Library", "usr", "bin"));
                    extra.Add(Path.Combine(dir, "Library", "bin"));
                    extra.Add(Path.Combine(dir, "Scripts"));
                }
                else if (Directory.Exists(Path.Combine(dir, "Scripts")))
                {
                    extra.Add(Path.Combine(dir, "Scripts"));
                }
                return string.Join(Path.PathSeparator, extra) + Path.PathSeparator + basePath;
            }
            catch { return basePath; }
        }

        // ================================================================== kill python when the app dies (also on "Stop debugging")

        private static class KillOnCloseJob
        {
            private static IntPtr _job = IntPtr.Zero;
            private static readonly object _lock = new();

            public static void Add(Process process)
            {
                if (!OperatingSystem.IsWindows()) return;
                try
                {
                    lock (_lock)
                    {
                        if (_job == IntPtr.Zero)
                        {
                            var job = CreateJobObject(IntPtr.Zero, null);
                            if (job == IntPtr.Zero) return;

                            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                            int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                            IntPtr ptr = Marshal.AllocHGlobal(length);
                            try
                            {
                                Marshal.StructureToPtr(info, ptr, false);
                                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)length))
                                {
                                    CloseHandle(job);
                                    return;
                                }
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(ptr);
                            }
                            _job = job;   // intentionally never closed: Windows closes it when LinBench.Wpf.exe ends
                        }
                        AssignProcessToJobObject(_job, process.Handle);
                    }
                }
                catch
                {
                    // not critical – Stop() still kills the backend on a normal close
                }
            }

            private const int JobObjectExtendedLimitInformation = 9;
            private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

            [StructLayout(LayoutKind.Sequential)]
            private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                public long PerProcessUserTimeLimit;
                public long PerJobUserTimeLimit;
                public uint LimitFlags;
                public UIntPtr MinimumWorkingSetSize;
                public UIntPtr MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint PriorityClass;
                public uint SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct IO_COUNTERS
            {
                public ulong ReadOperationCount;
                public ulong WriteOperationCount;
                public ulong OtherOperationCount;
                public ulong ReadTransferCount;
                public ulong WriteTransferCount;
                public ulong OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
                public IO_COUNTERS IoInfo;
                public UIntPtr ProcessMemoryLimit;
                public UIntPtr JobMemoryLimit;
                public UIntPtr PeakProcessMemoryUsed;
                public UIntPtr PeakJobMemoryUsed;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool CloseHandle(IntPtr handle);
        }
    }
}
