using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace WindowTopTool
{
    /// <summary>
    /// Universal PPC connector that bridges this application to the PPC
    /// Central Processing System. Mirrors the ppc-connect Rust library (v1.0)
    /// protocol so the application and PPC share one communication contract.
    ///
    /// Architecture:  Application  →  PpcConnector  →  PPC server
    /// </summary>
    public sealed class PpcConnector : IDisposable
    {
        // ------------------------------------------------------------------
        // Constants — must match ppc-connect (Rust) and PPC server exactly.
        // ------------------------------------------------------------------

        /// <summary>Connector template version.</summary>
        public const string ConnectorVersion = "1.0";

        /// <summary>Lowest PPC version this connector can talk to.</summary>
        public const string MinPpcVersion = "0.0.6";

        /// <summary>Highest PPC version this connector can talk to.</summary>
        public const string MaxPpcVersion = "0.0.6";

        /// <summary>Default PPC listen address.</summary>
        public const string DefaultHost = "127.0.0.1";

        /// <summary>Default PPC listen port.</summary>
        public const int DefaultPort = 9527;

        /// <summary>Read timeout for server responses (milliseconds).</summary>
        private const int ReadTimeoutMs = 5000;

        /// <summary>Maximum receive buffer size per read.</summary>
        private const int BufferSize = 8192;

        // ------------------------------------------------------------------
        // State
        // ------------------------------------------------------------------

        private TcpClient? _client;
        private NetworkStream? _stream;
        private bool _authenticated;
        private string? _appId;
        private string? _appHash;

        /// <summary>The host the connector targets.</summary>
        public string Host { get; }

        /// <summary>The port the connector targets.</summary>
        public int Port { get; }

        /// <summary>Whether the connection is currently authenticated.</summary>
        public bool IsAuthenticated => _authenticated;

        /// <summary>The authenticated app id, if any.</summary>
        public string? AppId => _appId;

        // ------------------------------------------------------------------
        // Construction
        // ------------------------------------------------------------------

        /// <summary>Create a connector targeting the given host and port.</summary>
        public PpcConnector(string host, int port)
        {
            Host = host ?? throw new ArgumentNullException(nameof(host));
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port));
            Port = port;
        }

        /// <summary>Create a connector using the default PPC address.</summary>
        public static PpcConnector Localhost() => new PpcConnector(DefaultHost, DefaultPort);

        // ------------------------------------------------------------------
        // Connection lifecycle
        // ------------------------------------------------------------------

        /// <summary>Open the TCP connection to the PPC server.</summary>
        public void Connect()
        {
            ThrowIfDisposed();
            if (_client != null)
                throw new InvalidOperationException("Already connected. Disconnect first.");

            try
            {
                _client = new TcpClient();
                _client.ReceiveTimeout = ReadTimeoutMs;
                _client.SendTimeout = ReadTimeoutMs;
                _client.Connect(Host, Port);
                _stream = _client.GetStream();
                _authenticated = false;
                _appId = null;
                // _appHash is intentionally preserved: it is a credential
                // captured by RegisterApp/UpdateApp and reused across
                // reconnects via AuthenticateWithStoredHash.
            }
            catch (SocketException ex)
            {
                throw new PpcException(
                    $"Connection error: cannot reach PPC at {Host}:{Port}: {ex.Message}",
                    PpcErrorCodes.ErrorPpcNotRunning, ex);
            }
        }

        /// <summary>Asynchronously open the TCP connection.</summary>
        public async Task ConnectAsync()
        {
            ThrowIfDisposed();
            if (_client != null)
                throw new InvalidOperationException("Already connected. Disconnect first.");

            try
            {
                _client = new TcpClient();
                _client.ReceiveTimeout = ReadTimeoutMs;
                _client.SendTimeout = ReadTimeoutMs;
                await _client.ConnectAsync(Host, Port).ConfigureAwait(false);
                _stream = _client.GetStream();
                _authenticated = false;
                _appId = null;
                // _appHash intentionally preserved (see Connect remark).
            }
            catch (SocketException ex)
            {
                throw new PpcException(
                    $"Connection error: cannot reach PPC at {Host}:{Port}: {ex.Message}",
                    PpcErrorCodes.ErrorPpcNotRunning, ex);
            }
        }

        /// <summary>Close the connection if open.</summary>
        /// <remarks>
        /// The registered hash (<see cref="_appHash"/>) is intentionally kept
        /// so the caller can reconnect and re-authenticate with
        /// <see cref="AuthenticateWithStoredHash"/> without re-registering.
        /// </remarks>
        public void Disconnect()
        {
            _stream?.Dispose();
            _client?.Dispose();
            _stream = null;
            _client = null;
            _authenticated = false;
            _appId = null;
        }

        // ------------------------------------------------------------------
        // Public (no auth) commands
        // ------------------------------------------------------------------

        /// <summary>
        /// Register an application. Returns the app's SHA-256 hash, which must
        /// be used for <see cref="Authenticate"/> on later connections.
        /// </summary>
        public string RegisterApp(string appId, string version, string exePath)
        {
            var args = $"{appId}|{version}|{exePath}";
            var resp = SendCommand("REGISTER_APP", args);
            EnsureSuccess(resp, nameof(RegisterApp));
            var hash = ExtractFirstDataLine(resp);
            if (string.IsNullOrEmpty(hash))
                throw new PpcException(
                    "Registration succeeded but no hash was returned.",
                    PpcErrorCodes.ErrorPpcResponseInvalid);
            _appHash = hash;
            return hash;
        }

        /// <summary>Update an existing application registration. Returns the new hash.</summary>
        public string UpdateApp(string appId, string version, string exePath)
        {
            var args = $"{appId}|{version}|{exePath}";
            var resp = SendCommand("UPDATE_APP", args);
            EnsureSuccess(resp, nameof(UpdateApp));
            var hash = ExtractFirstDataLine(resp);
            if (string.IsNullOrEmpty(hash))
                throw new PpcException(
                    "Update succeeded but no hash was returned.",
                    PpcErrorCodes.ErrorPpcResponseInvalid);
            _appHash = hash;
            return hash;
        }

        // ------------------------------------------------------------------
        // Authentication
        // ------------------------------------------------------------------

        /// <summary>Authenticate the current connection using a registered app hash.</summary>
        public string Authenticate(string hash)
        {
            if (string.IsNullOrEmpty(hash))
                throw new ArgumentException("Hash must not be empty.", nameof(hash));

            var resp = SendCommand("AUTH", hash);
            if (!resp.IsSuccess)
            {
                _authenticated = false;
                throw new PpcException(
                    $"Authentication failed: {resp.Data.Trim()}",
                    PpcErrorCodes.ErrorPpcAuthFailed);
            }

            // Response data looks like "Authenticated as: testapp".
            _appId = null;
            foreach (var line in resp.Data.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Authenticated as:", StringComparison.Ordinal))
                {
                    _appId = trimmed.Substring("Authenticated as:".Length).Trim();
                    break;
                }
            }
            _authenticated = true;
            _appHash = hash;
            return _appId ?? string.Empty;
        }

        /// <summary>Authenticate using the hash captured during RegisterApp/UpdateApp.</summary>
        public string AuthenticateWithStoredHash()
        {
            if (string.IsNullOrEmpty(_appHash))
                throw new InvalidOperationException("No stored hash available. Register first.");
            return Authenticate(_appHash);
        }

        // ------------------------------------------------------------------
        // Version compatibility
        // ------------------------------------------------------------------

        /// <summary>
        /// Query the PPC version and verify it is supported. Throws
        /// <see cref="PpcException"/> with
        /// <see cref="PpcErrorCodes.ErrorPpcVersionUnsupported"/> when the
        /// server version is outside the connector's supported range.
        /// </summary>
        public VersionCheck CheckVersionCompatibility()
        {
            RequireAuth();
            var data = PpcVersion();
            var ppcVer = ExtractPpcVersion(data);
            var result = CheckVersion(ppcVer ?? string.Empty);
            if (!result.IsSupported)
            {
                throw new PpcException(
                    $"PPC version check failed: {result.Reason}",
                    PpcErrorCodes.ErrorPpcVersionUnsupported);
            }
            return result;
        }

        // ------------------------------------------------------------------
        // Authenticated commands — System information
        // ------------------------------------------------------------------

        /// <summary>PING — returns "PONG" on success.</summary>
        public string Ping()
        {
            RequireAuth();
            return DataOrThrow(SendCommand("PING", ""), nameof(Ping));
        }

        /// <summary>VERSION [param] — query system version information.</summary>
        public string Version(VersionParam param)
        {
            RequireAuth();
            return DataOrThrow(SendCommand("VERSION", ParamToString(param)), nameof(Version));
        }

        /// <summary>PPCVERSION — query the PPC system version string.</summary>
        public string PpcVersion()
        {
            RequireAuth();
            return DataOrThrow(SendCommand("PPCVERSION", ""), nameof(PpcVersion));
        }

        /// <summary>HELP — list all available commands.</summary>
        public string Help()
        {
            RequireAuth();
            return DataOrThrow(SendCommand("HELP", ""), nameof(Help));
        }

        /// <summary>PPCPATH — return the PPC installation directory.</summary>
        public string GetPpcPath()
        {
            RequireAuth();
            return DataOrThrow(SendCommand("PPCPATH", ""), nameof(GetPpcPath));
        }

        // ------------------------------------------------------------------
        // PPC server auto-start
        // ------------------------------------------------------------------

        /// <summary>
        /// Attempt to start the PPC server when it appears to be unreachable.
        /// Tries, in order:
        /// <list type="number">
        /// <item><c>ppc</c> / <c>ppc.exe</c> on the system PATH (command line).</item>
        /// <item><c>C:\Program Files\ppc\ppc.exe</c></item>
        /// <item><c>C:\Program Files (x86)\ppc\ppc.exe</c></item>
        /// </list>
        /// Returns <c>true</c> when a process was launched (or is already
        /// running); <c>false</c> when no executable could be found.
        /// </summary>
        public static bool TryStartPpcServer()
        {
            // Collect candidate executable paths.
            var candidates = new List<string>();

            // 1. Rely on the OS to resolve "ppc" / "ppc.exe" via PATH.
            candidates.Add("ppc");
            candidates.Add("ppc.exe");

            // 2 & 3. Search the standard Program Files directories.
            var pf64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(pf64))
                candidates.Add(Path.Combine(pf64, "ppc", "ppc.exe"));
            if (!string.IsNullOrEmpty(pf86))
                candidates.Add(Path.Combine(pf86, "ppc", "ppc.exe"));

            foreach (var candidate in candidates)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = candidate,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        CreateNoWindow = true,
                    };

                    var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        AppLogger.Info($"PPC server started from '{candidate}' (PID {proc.Id})");
                        // Give the server a moment to bind to its port.
                        System.Threading.Thread.Sleep(1500);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    // File not found / cannot start — try the next candidate.
                    AppLogger.Debug($"Could not start PPC from '{candidate}': {ex.Message}");
                }
            }

            AppLogger.Warn(
                "Could not find or start the PPC server in any searched location",
                PpcErrorCodes.WarningPpcReconnect);
            return false;
        }

        // ------------------------------------------------------------------
        // Authenticated commands — DLL information
        // ------------------------------------------------------------------

        /// <summary>DLLINFO — return all DLL information as JSON.</summary>
        public string DllInfo()
        {
            RequireAuth();
            return DataOrThrow(SendCommand("DLLINFO", ""), nameof(DllInfo));
        }

        /// <summary>DLLPATH &lt;name|ALL&gt; — return a DLL path (or all paths).</summary>
        public string DllPath(string name)
        {
            RequireAuth();
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("DLL name required.", nameof(name));
            return DataOrThrow(SendCommand("DLLPATH", name), nameof(DllPath));
        }

        /// <summary>DLLVERSION &lt;name|ALL&gt; — return a DLL version (or all versions).</summary>
        public string DllVersion(string name)
        {
            RequireAuth();
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("DLL name required.", nameof(name));
            return DataOrThrow(SendCommand("DLLVERSION", name), nameof(DllVersion));
        }

        // ------------------------------------------------------------------
        // Authenticated commands — Application logs
        // ------------------------------------------------------------------

        /// <summary>
        /// APPLOG [count] — return the calling application's own log entries.
        /// Returns at most <paramref name="count"/> lines (clamped to 100 by the
        /// server). The connector must be authenticated; an app can only read
        /// its own log, never another application's.
        /// </summary>
        /// <param name="count">Maximum number of lines to return (1–100).</param>
        /// <returns>An array of raw log lines (oldest-first within the window).</returns>
        public string[] GetAppLog(int count = 100)
        {
            RequireAuth();
            var data = DataOrThrow(
                SendCommand("APPLOG", count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                nameof(GetAppLog));
            // The server joins lines with '\n'; split and drop the trailing
            // empty entry produced by the terminal newline.
            return data.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        // ------------------------------------------------------------------
        // Low-level protocol
        // ------------------------------------------------------------------

        /// <summary>Send a raw command and return the parsed response.</summary>
        public PpcResponse SendRaw(string command)
        {
            ThrowIfDisposed();
            if (_stream == null)
                throw new InvalidOperationException("Not connected to PPC.");
            if (string.IsNullOrEmpty(command))
                throw new ArgumentException("Command must not be empty.", nameof(command));

            var line = command.TrimEnd('\n', '\r') + "\n";
            var bytes = Encoding.UTF8.GetBytes(line);

            try
            {
                _stream.Write(bytes, 0, bytes.Length);
                _stream.Flush();
            }
            catch (IOException ex)
            {
                throw new PpcException(
                    $"Failed to send command: {ex.Message}",
                    PpcErrorCodes.ErrorPpcResponseInvalid, ex);
            }

            var raw = ReadResponse();
            return ParseResponse(raw);
        }

        /// <summary>Send a command with an optional argument.</summary>
        private PpcResponse SendCommand(string command, string args)
        {
            var full = string.IsNullOrEmpty(args) ? command : $"{command} {args}";
            return SendRaw(full);
        }

        /// <summary>
        /// Read the full server response. The first read blocks (with timeout);
        /// any immediately-available follow-up data is drained so multi-line
        /// responses (e.g. DLL JSON) are captured in full.
        /// </summary>
        private string ReadResponse()
        {
            var sb = new StringBuilder();
            var buffer = new byte[BufferSize];

            try
            {
                int n = _stream!.Read(buffer, 0, buffer.Length);
                if (n == 0)
                    throw new PpcException(
                        "Connection closed by server.",
                        PpcErrorCodes.ErrorPpcResponseInvalid);
                sb.Append(Encoding.UTF8.GetString(buffer, 0, n));

                // Drain any additional data that arrived in the same window.
                while (_stream.DataAvailable)
                {
                    n = _stream.Read(buffer, 0, buffer.Length);
                    if (n == 0) break;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
                }
            }
            catch (IOException ex)
            {
                throw new PpcException(
                    $"Failed to read response: {ex.Message}",
                    PpcErrorCodes.ErrorPpcResponseInvalid, ex);
            }

            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private void RequireAuth()
        {
            if (!_authenticated)
                throw new InvalidOperationException("Not authenticated — call Authenticate() first.");
        }

        private void EnsureSuccess(PpcResponse resp, string operation)
        {
            if (!resp.IsSuccess)
                throw new PpcException(
                    $"{operation} failed [{resp.StatusCode}]: {resp.Data.Trim()}",
                    resp.ErrorCode);
        }

        private string DataOrThrow(PpcResponse resp, string operation)
        {
            EnsureSuccess(resp, operation);
            return resp.Data.Trim();
        }

        private static string ExtractFirstDataLine(PpcResponse resp)
        {
            foreach (var line in resp.Data.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                    return trimmed;
            }
            return string.Empty;
        }

        private static string ParamToString(VersionParam param) => param switch
        {
            VersionParam.Default => "",
            VersionParam.Type => "TYPE",
            VersionParam.Version => "VERSION",
            VersionParam.Kernel => "KERNEL",
            VersionParam.Hostname => "HOSTNAME",
            VersionParam.All => "ALL",
            _ => "",
        };

        private static PpcResponse ParseResponse(string raw)
        {
            var trimmed = raw.TrimEnd('\n', '\r');
            var newlineIdx = trimmed.IndexOf('\n');
            string status;
            string data;
            if (newlineIdx < 0)
            {
                status = trimmed.Trim();
                data = string.Empty;
            }
            else
            {
                status = trimmed.Substring(0, newlineIdx).Trim();
                data = trimmed.Substring(newlineIdx + 1);
            }
            return new PpcResponse(status, data);
        }

        /// <summary>Extract the version number from a PPCVERSION response.</summary>
        public static string? ExtractPpcVersion(string response)
        {
            foreach (var line in response.Split('\n'))
            {
                if (line.Contains("PPC Version"))
                {
                    var idx = line.IndexOf(':');
                    if (idx >= 0 && idx + 1 < line.Length)
                        return line.Substring(idx + 1).Trim();
                }
            }
            return null;
        }

        /// <summary>Check whether a PPC version is supported by this connector.</summary>
        public static VersionCheck CheckVersion(string ppcVersion)
        {
            if (string.IsNullOrEmpty(ppcVersion))
                return VersionCheck.Unsupported("unknown");

            if (CompareVersions(ppcVersion, MinPpcVersion) < 0)
                return VersionCheck.TooLow(ppcVersion, MinPpcVersion);
            if (CompareVersions(ppcVersion, MaxPpcVersion) > 0)
                return VersionCheck.TooHigh(ppcVersion, MaxPpcVersion);
            return VersionCheck.Supported();
        }

        private static int CompareVersions(string a, string b)
        {
            var pa = a.Split('.');
            var pb = b.Split('.');
            int len = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < len; i++)
            {
                int na = i < pa.Length && int.TryParse(pa[i], out var x) ? x : 0;
                int nb = i < pb.Length && int.TryParse(pb[i], out var y) ? y : 0;
                if (na != nb) return na - nb;
            }
            return 0;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PpcConnector));
        }

        // ------------------------------------------------------------------
        // IDisposable
        // ------------------------------------------------------------------

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            Disconnect();
            _disposed = true;
        }
    }

    /// <summary>Parameter for the VERSION command.</summary>
    public enum VersionParam
    {
        Default,
        Type,
        Version,
        Kernel,
        Hostname,
        All,
    }

    /// <summary>A parsed response from the PPC server.</summary>
    public sealed class PpcResponse
    {
        /// <summary>The hex status code on the first line (e.g. "0x00000").</summary>
        public string StatusCode { get; }

        /// <summary>Every line after the status code.</summary>
        public string Data { get; }

        public PpcResponse(string statusCode, string data)
        {
            StatusCode = statusCode ?? string.Empty;
            Data = data ?? string.Empty;
        }

        /// <summary>
        /// Alias for <see cref="StatusCode"/> — the PPC error/status code
        /// (mirrors <see cref="PpcErrorCodes"/>). Provided so callers can write
        /// <c>resp.ErrorCode</c> when treating the value as an error classifier.
        /// </summary>
        public string ErrorCode => StatusCode;

        /// <summary>Returns true when the status code indicates success (0x0xxxx).</summary>
        public bool IsSuccess => PpcErrorCodes.IsSuccess(StatusCode);

        /// <summary>Returns true when the status code is an error (0x1xxxx).</summary>
        public bool IsError => PpcErrorCodes.IsError(StatusCode);

        /// <summary>Returns true when the status code is a warning (0x3xxxx).</summary>
        public bool IsWarning => PpcErrorCodes.IsWarning(StatusCode);
    }

    /// <summary>Outcome of a version compatibility check.</summary>
    public sealed class VersionCheck
    {
        public bool IsSupported { get; }
        public string Reason { get; }

        private VersionCheck(bool supported, string reason)
        {
            IsSupported = supported;
            Reason = reason;
        }

        public static VersionCheck Supported() => new VersionCheck(true, "Supported");
        public static VersionCheck TooLow(string actual, string min) =>
            new VersionCheck(false, $"PPC version {actual} is lower than minimum {min}");
        public static VersionCheck TooHigh(string actual, string max) =>
            new VersionCheck(false, $"PPC version {actual} is higher than maximum {max}");
        public static VersionCheck Unsupported(string reason) =>
            new VersionCheck(false, reason);

        public override string ToString() => IsSupported ? "Supported" : $"Unsupported: {Reason}";
    }

    /// <summary>Errors raised by the PPC connector.</summary>
    public sealed class PpcException : Exception
    {
        /// <summary>
        /// The PPC error code (a <see cref="PpcErrorCodes"/> constant) associated
        /// with the failure, when known. May be <c>null</c> for legacy call sites.
        /// </summary>
        public string? ErrorCode { get; }

        public PpcException(string message) : base(message) { }
        public PpcException(string message, Exception innerException) : base(message, innerException) { }

        /// <summary>
        /// Create an exception tagged with a PPC error code (one of the
        /// <see cref="PpcErrorCodes"/> constants).
        /// </summary>
        public PpcException(string message, string errorCode) : base(message)
        {
            ErrorCode = errorCode;
        }

        /// <summary>
        /// Create an exception tagged with a PPC error code and an inner cause.
        /// </summary>
        public PpcException(string message, string errorCode, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }
}
