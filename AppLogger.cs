using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace WindowTopTool
{
    /// <summary>
    /// Client-side application logger for TOP_APP.
    ///
    /// Writes structured log lines to
    /// <c>%AppData%/WindowTopTool/logs/app.log</c> using the same format as the
    /// PPC server logger:
    /// <code>[YYYY-MM-DD HH:MM:SS] [LEVEL] message</code>
    ///
    /// Configuration is pushed in once via <see cref="Configure"/> from
    /// <c>Program.cs</c> after <see cref="AppConfig"/> is loaded. The write
    /// path deliberately does NOT touch <see cref="AppConfig.Instance"/> so
    /// that logging during early bootstrap (or during a failed config load)
    /// cannot recurse into the config loader. Before <see cref="Configure"/>
    /// is called, logging defaults to enabled at INFO level. Writes are
    /// serialised by a lock so concurrent UI threads do not interleave partial
    /// lines.
    /// </summary>
    public static class AppLogger
    {
        private enum Level
        {
            Debug,
            Info,
            Warn,
            Error,
        }

        // Numeric order used for level filtering (lower = more verbose).
        private const int LevelDebug = 0;
        private const int LevelInfo = 1;
        private const int LevelWarn = 2;
        private const int LevelError = 3;

        private static readonly object _gate = new object();

        // Cached configuration — set once by Configure(). Defaults keep
        // logging active before AppConfig is available.
        private static bool _enabled = true;
        private static int _minLevel = LevelInfo;
        private static string? _logFilePath;

        /// <summary>
        /// Push the logging configuration derived from <see cref="AppConfig"/>.
        /// Called once from <c>Program.cs</c> after the config file is loaded.
        /// Safe to call again to apply runtime changes.
        /// </summary>
        public static void Configure(bool enabled, string level)
        {
            _enabled = enabled;
            var l = (level ?? "INFO").Trim().ToUpperInvariant();
            _minLevel = l switch
            {
                "DEBUG" => LevelDebug,
                "INFO" => LevelInfo,
                "WARN" => LevelWarn,
                "ERROR" => LevelError,
                _ => LevelInfo,
            };
        }

        /// <summary>
        /// Resolve and cache the log file path, creating the parent directory
        /// on first access. Returns <c>null</c> if the directory cannot be
        /// created (logging is then silently disabled).
        /// </summary>
        private static string? LogFilePath
        {
            get
            {
                if (_logFilePath != null)
                    return _logFilePath;
                try
                {
                    string dir;
                    if (AppConfig.IsPortable)
                    {
                        // Portable mode: keep logs next to the executable so the
                        // distribution leaves no trace in %AppData%.
                        dir = Path.Combine(AppConfig.ExeDirectory, "logs");
                    }
                    else
                    {
                        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                        dir = Path.Combine(appData, "WindowTopTool", "logs");
                    }
                    Directory.CreateDirectory(dir);
                    _logFilePath = Path.Combine(dir, "app.log");
                }
                catch
                {
                    _logFilePath = null;
                }
                return _logFilePath;
            }
        }

        private static int LevelValue(Level level) => level switch
        {
            Level.Debug => LevelDebug,
            Level.Info => LevelInfo,
            Level.Warn => LevelWarn,
            Level.Error => LevelError,
            _ => LevelInfo,
        };

        private static string LevelTag(Level level) => level switch
        {
            Level.Debug => "DEBUG",
            Level.Info => "INFO",
            Level.Warn => "WARN",
            Level.Error => "ERROR",
            _ => "INFO",
        };

        /// <summary>
        /// Write a single log entry if the level passes the configured
        /// threshold and logging is enabled. Never throws.
        /// </summary>
        private static void Write(Level level, string message)
        {
            if (!_enabled)
                return;
            if (LevelValue(level) < _minLevel)
                return;

            var path = LogFilePath;
            if (path == null)
                return;

            var line = string.Format(
                CultureInfo.InvariantCulture,
                "[{0}] [{1}] {2}",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                LevelTag(level),
                message);

            try
            {
                lock (_gate)
                {
                    File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never disrupt the application.
            }
        }

        /// <summary>Log a DEBUG message. Suppressed unless level is DEBUG.</summary>
        public static void Debug(string message) => Write(Level.Debug, message);

        /// <summary>Log an INFO message.</summary>
        public static void Info(string message) => Write(Level.Info, message);

        /// <summary>Log a WARN message, optionally tagged with a PPC error code.</summary>
        public static void Warn(string message, string? errorCode = null)
            => Write(Level.Warn, errorCode == null ? message : $"{message} [code:{errorCode}]");

        /// <summary>Log an ERROR message, optionally tagged with a PPC error code.</summary>
        public static void Error(string message, string? errorCode = null)
            => Write(Level.Error, errorCode == null ? message : $"{message} [code:{errorCode}]");

        /// <summary>Log an ERROR message wrapping an exception, with optional code.</summary>
        public static void Error(string message, Exception ex, string? errorCode = null)
        {
            var suffix = errorCode == null ? "" : $" [code:{errorCode}]";
            Write(Level.Error, $"{message}{suffix}: {ex.GetType().Name}: {ex.Message}");
        }

        /// <summary>Return the resolved log file path, or <c>null</c> if unavailable.</summary>
        public static string? GetLogFilePath() => LogFilePath;
    }
}
