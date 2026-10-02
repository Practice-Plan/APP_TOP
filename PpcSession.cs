using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WindowTopTool;

/// <summary>
/// Owns a long-lived, self-healing connection to the PPC server.
///
/// Responsibilities:
/// <list type="bullet">
/// <item>Launch the local PPC launcher (<c>l.exe</c> / <c>l.ps1</c>) once at startup.</item>
/// <item>Connect with a bounded retry window (5s interval, up to 2 minutes), then
/// REGISTER_APP / UPDATE_APP + AUTH and verify the server version is supported.</item>
/// <item>Keep the link warm with a background watchdog that pings periodically and
/// transparently reconnects (re-handshaking) if the link drops.</item>
/// <item>Forward application errors (<see cref="SendError"/>) and info
/// (<see cref="SendInfo"/>) to PPC as WINDOW ERROR / WINDOW INFO popups.</item>
/// </list>
///
/// All <see cref="PpcConnector"/> access is serialized under <see cref="_gate"/> so
/// the watchdog thread and UI-thread callers (error forwarding) never race.
/// </summary>
public sealed class PpcSession : IDisposable
{
    private const int ConnectRetryIntervalMs = 5000;      // wait between connect attempts
    private const int StartupConnectWindowMs = 120_000;   // 2 minutes max for a connect+handshake cycle
    private const int KeepAliveIntervalMs = 30_000;       // ping cadence once connected

    private readonly AppConfig _cfg;
    private readonly object _gate = new object();

    private PpcConnector? _connector;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private volatile bool _stopRequested;   // e.g. unsupported PPC version → stop retrying
    private volatile bool _linkDropped;      // set after a successful connect was later lost
    private bool _disposed;

    public PpcSession(AppConfig cfg)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
    }

    /// <summary>True when a live, authenticated connection is currently held.</summary>
    public bool IsConnected
    {
        get { lock (_gate) return _connector != null; }
    }

    /// <summary>Start the connect + watchdog loop on a background thread.</summary>
    public void Start()
    {
        if (!_cfg.PpcEnabled)
        {
            AppLogger.Info("PPC disabled in config; PpcSession not started.");
            return;
        }
        if (_worker != null)
            return;

        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        TryLaunchLauncher();

        bool up = await ConnectAndHandshakeAsync(ct);
        if (!up && !_stopRequested)
        {
            AppLogger.Error(
                $"PPC connection failed (connection timeout after {StartupConnectWindowMs / 1000}s).",
                PpcErrorCodes.ErrorTimeout);
        }

        // Watchdog: keep the link warm and reconnect if it drops.
        while (!ct.IsCancellationRequested && !_stopRequested)
        {
            try { await Task.Delay(KeepAliveIntervalMs, ct); }
            catch (TaskCanceledException) { break; }

            if (IsConnected)
            {
                try
                {
                    Ping();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"PPC keep-alive failed, dropping link: {ex.Message}",
                        PpcErrorCodes.WarningPpcReconnect);
                    CloseConnector();
                    _linkDropped = true;
                }
            }

            if (!IsConnected && !_stopRequested)
            {
                AppLogger.Info("PPC link down; attempting to reconnect...");
                if (await ConnectAndHandshakeAsync(ct) && _linkDropped)
                {
                    _linkDropped = false;
                    SendInfo("PPC connection restored.");
                }
            }
        }
    }

    /// <summary>
    /// Connect (bounded retry) then register/update + AUTH + version check.
    /// Returns true when a live, authenticated, version-compatible connection is
    /// established. A <see cref="PpcErrorCodes.ErrorPpcVersionUnsupported"/> result
    /// stops all further retries (pointless against an incompatible server).
    /// </summary>
    private async Task<bool> ConnectAndHandshakeAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(StartupConnectWindowMs);
        while (!ct.IsCancellationRequested && !_stopRequested)
        {
            // Wait before each attempt — also gives the launcher time to boot PPC.
            try { await Task.Delay(ConnectRetryIntervalMs, ct); }
            catch (TaskCanceledException) { return false; }

            var candidate = new PpcConnector(_cfg.PpcHost, _cfg.PpcPort);
            bool connected = false;
            try
            {
                candidate.Connect();
                connected = true;
            }
            catch (Exception ex)
            {
                candidate.Dispose();
                AppLogger.Debug($"PPC connect attempt failed: {ex.Message}");
            }

            if (connected)
            {
                lock (_gate)
                {
                    _connector?.Dispose();
                    _connector = candidate;
                }

                try
                {
                    Handshake();
                    return true;
                }
                catch (PpcException ex) when (ex.ErrorCode == PpcErrorCodes.ErrorPpcVersionUnsupported)
                {
                    AppLogger.Error($"PPC version incompatible: {ex.Message}",
                        PpcErrorCodes.ErrorPpcVersionUnsupported);
                    CloseConnector();
                    _stopRequested = true;
                    return false;
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"PPC handshake failed: {ex.Message}",
                        PpcErrorCodes.ErrorPpcNotRunning);
                    CloseConnector();
                    // fall through to retry within the window
                }
            }

            if (DateTime.UtcNow >= deadline)
                return false;
        }
        return false;
    }

    /// <summary>
    /// Register/UPDATE, persist the returned hash, authenticate, and verify the
    /// server version. Throws on any protocol failure (caller handles).
    /// </summary>
    private void Handshake()
    {
        PpcConnector connector;
        lock (_gate)
            connector = _connector ?? throw new InvalidOperationException("PPC connector is not connected.");

        var exePath = Environment.ProcessPath
            ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
            ?? string.Empty;
        var appId = string.IsNullOrWhiteSpace(_cfg.PpcAppId) ? AppConfig.ApplicationName : _cfg.PpcAppId;
        var version = _cfg.PpcAppVersion;

        string hash;
        if (string.IsNullOrEmpty(_cfg.PpcAppHash))
        {
            hash = connector.RegisterApp(appId, version, exePath);
            AppLogger.Info($"PPC REGISTER_APP ok: {appId} v{version}");
        }
        else
        {
            try
            {
                hash = connector.UpdateApp(appId, version, exePath);
                AppLogger.Info($"PPC UPDATE_APP ok: {appId} v{version}");
            }
            catch (PpcException ex) when (ex.ErrorCode == PpcErrorCodes.ErrorAppNotRegistered)
            {
                hash = connector.RegisterApp(appId, version, exePath);
                AppLogger.Info($"PPC re-REGISTER_APP ok (was not registered): {appId} v{version}");
            }
        }

        _cfg.PpcAppHash = hash;
        _cfg.Save();

        connector.Authenticate(hash);
        AppLogger.Info($"PPC AUTH ok: {appId}");

        var check = connector.CheckVersionCompatibility();
        AppLogger.Info($"PPC version check: {check}");
    }

    /// <summary>Forward an error to PPC as a WINDOW ERROR popup (no-op when offline).</summary>
    public void SendError(string message) => SendWindow(error: true, message);

    /// <summary>Forward an informational message to PPC as a WINDOW INFO popup (no-op when offline).</summary>
    public void SendInfo(string message) => SendWindow(error: false, message);

    private void SendWindow(bool error, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        PpcConnector? connector;
        lock (_gate) connector = _connector;
        if (connector == null)
            return; // offline: skip silently (forwarding must never spawn logging recursion)

        try
        {
            if (error) connector.ShowErrorWindow(message);
            else connector.ShowInfoWindow(message);
        }
        catch (Exception ex)
        {
            // The link is likely dead; drop it so the watchdog reconnects.
            AppLogger.Debug($"PPC window send failed: {ex.Message}");
            CloseConnector();
            _linkDropped = true;
        }
    }

    private void Ping()
    {
        PpcConnector? connector;
        lock (_gate) connector = _connector;
        connector?.Ping(); // throws on failure → handled by the watchdog
    }

    private void CloseConnector()
    {
        lock (_gate)
        {
            _connector?.Dispose();
            _connector = null;
        }
    }

    /// <summary>
    /// Launch the PPC launcher that sits next to the executable. Prefers
    /// <c>l.exe</c>, falling back to <c>l.ps1</c> (usually only one is present).
    /// Non-blocking. If none is found the connect-retry loop still runs in case
    /// PPC is already up.
    /// </summary>
    private static void TryLaunchLauncher()
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            var exe = Path.Combine(dir, "l.exe");
            var ps1 = Path.Combine(dir, "l.ps1");

            if (File.Exists(exe))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = dir,
                    UseShellExecute = true,
                });
                AppLogger.Info($"PPC launcher started: {exe}");
            }
            else if (File.Exists(ps1))
            {
                var ps = SystemPowerShellPath();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ps,
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1}\"",
                    WorkingDirectory = dir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                AppLogger.Info($"PPC launcher started via system PowerShell '{ps}': {ps1}");
            }
            else
            {
                AppLogger.Debug("No PPC launcher (l.exe / l.ps1) found next to the app; trying direct connect.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to launch PPC launcher: {ex.Message}");
        }
    }

    /// <summary>
    /// Absolute path to the OS-installed Windows PowerShell (System32), so the
    /// launcher always runs in the system PowerShell environment rather than
    /// relying on the app's PATH. Falls back to the bare "powershell.exe".
    /// </summary>
    private static string SystemPowerShellPath()
    {
        try
        {
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!string.IsNullOrEmpty(system32))
            {
                var candidate = Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch { /* fall back to PATH lookup */ }
        return "powershell.exe";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts?.Cancel(); } catch { /* ignore */ }
        CloseConnector();
        try { _worker?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _cts?.Dispose();
    }
}
