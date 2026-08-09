using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace WindowTopTool
{
    /// <summary>
    /// Application configuration
    /// </summary>
    public class AppConfig
    {
        // Hotkey configuration
        public int PinHotKeyId { get; set; } = 1;
        public int PinHotkeyModifier { get; set; } = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
        public int PinHotkeyKey { get; set; } = (int)System.Windows.Forms.Keys.P;

        // Transparency configuration
        public byte DefaultOpacity { get; set; } = 200; // ~78%
        public byte MinOpacity { get; set; } = 51; // ~20%
        public byte MaxOpacity { get; set; } = 255; // 100%

        // Edge auto-hide configuration (right side only)
        public int EdgeThreshold { get; set; } = 12; // pixels to trigger hide
        public int BookmarkWidth { get; set; } = 40; // bookmark width in pixels
        public int BookmarkHeight { get; set; } = 80; // bookmark height in pixels
        public int BookmarkSpacing { get; set; } = 10; // spacing between bookmarks
        public int ShowAnimationDuration { get; set; } = 200; // milliseconds

        // Mini mode configuration
        public int MiniWindowWidth { get; set; } = 200;
        public int MiniWindowHeight { get; set; } = 200;
        public int MiniWindowX { get; set; } = 50;
        public int MiniWindowY { get; set; } = 50;

        // Auto-save interval in seconds
        public int AutoSaveInterval { get; set; } = 30;

        // Tooltip display duration in milliseconds
        public int TooltipDuration { get; set; } = 2000;

        // Notification settings
        public bool ShowNotifications { get; set; } = true; // Enable notifications by default
        public bool ShowPinNotifications { get; set; } = true; // Show when window is pinned/unpinned
        public bool ShowErrorNotifications { get; set; } = true; // Show error notifications

        // Feature toggles
        public bool EnableAutoHide { get; set; } = true; // Enable edge auto-hide
        public bool EnableClickThrough { get; set; } = false; // Enable mouse click-through

        // --- New-style hotkey properties (used by MainForm.RegisterAllHotkeys) ---

        // Toggle pin: Ctrl+Alt+P
        public int TogglePinHotkeyModifiers { get; set; } = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
        public int TogglePinHotkeyKey { get; set; } = (int)System.Windows.Forms.Keys.P;

        // Toggle topmost only: Ctrl+Alt+T
        public int ToggleTopmostHotkeyModifiers { get; set; } = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
        public int ToggleTopmostHotkeyKey { get; set; } = (int)System.Windows.Forms.Keys.T;

        // Mini window: Ctrl+Alt+M
        public int MiniWindowHotkeyModifiers { get; set; } = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
        public int MiniWindowHotkeyKey { get; set; } = (int)System.Windows.Forms.Keys.M;

        // PiP: Ctrl+Alt+Shift+P
        public int PiPHotkeyModifiers { get; set; } = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT;
        public int PiPHotkeyKey { get; set; } = (int)System.Windows.Forms.Keys.P;

        // Toggle click-through: Ctrl+Alt+K
        public int ToggleClickThroughHotkeyModifiers { get; set; } = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
        public int ToggleClickThroughHotkeyKey { get; set; } = (int)System.Windows.Forms.Keys.K;

        // Unpin all: Ctrl+Alt+Shift+U
        public int UnpinAllHotkeyModifiers { get; set; } = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT;
        public int UnpinAllHotkeyKey { get; set; } = (int)System.Windows.Forms.Keys.U;

        // Allowed applications for pinning (empty = all applications)
        public List<string> AllowedApplications { get; set; } = new List<string>();

        // --- PPC connector configuration ---
        // When enabled, the application connects to the PPC Central Processing
        // System via the ppc-connect protocol (must match ppc-connect v1.0 /
        // PPC 0.0.5).
        public bool PpcEnabled { get; set; } = false;
        public string PpcHost { get; set; } = PpcConnector.DefaultHost;
        public int PpcPort { get; set; } = PpcConnector.DefaultPort;
        // App identity used when registering with PPC.
        public string PpcAppId { get; set; } = "WindowTopTool";

        /// <summary>
        /// Version reported to PPC during REGISTER_APP / UPDATE_APP.
        ///
        /// Always sourced from the running assembly version (controlled by
        /// &lt;Version&gt; in the .csproj) so it can never drift from the
        /// actual build version. A stale value previously persisted in
        /// config.json (e.g. "0.0.2" after a bump to "0.0.3") is ignored:
        /// the setter is a no-op and the getter returns the authoritative
        /// assembly version. This makes version reporting self-correcting
        /// across upgrades without any manual config edit.
        /// </summary>
        public string PpcAppVersion
        {
            get => AssemblyVersion;
            // Intentionally a no-op: the assembly version is the single
            // source of truth. Ignoring the deserialized value prevents a
            // stale persisted version from being reported to PPC.
            set { }
        }

        /// <summary>
        /// The running application's version string (e.g. "0.0.3"), derived
        /// once from the entry assembly. Matches &lt;Version&gt; in the .csproj.
        /// </summary>
        public static string AssemblyVersion { get; } = ResolveAssemblyVersion();

        /// <summary>
        /// Human-readable application name with version, e.g.
        /// "Window Top Tool v0.0.3". Single source of truth for the display
        /// name shown in the tray tooltip and used in window-title
        /// comparisons to exclude the app's own windows. Always tracks the
        /// assembly version, so it can never go stale.
        /// </summary>
        public static string AppDisplayName => $"Window Top Tool v{AssemblyVersion}";

        /// <summary>
        /// Resolve the application version from the entry assembly. Prefers
        /// <see cref="AssemblyInformationalVersionAttribute"/> (which exactly
        /// mirrors the &lt;Version&gt; property, trimming any source-revision
        /// suffix) and falls back to <see cref="AssemblyName.Version"/>.
        /// </summary>
        private static string ResolveAssemblyVersion()
        {
            try
            {
                var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info))
                {
                    var plus = info.IndexOf('+');
                    if (plus > 0) info = info.Substring(0, plus);
                    return info.Trim();
                }

                var v = asm.GetName().Version;
                return v == null ? "0.0.0" : v.ToString(3);
            }
            catch
            {
                return "0.0.0";
            }
        }
        // SHA-256 hash returned by REGISTER_APP. Persisted so the app can
        // AUTH on subsequent launches without re-registering every time.
        public string PpcAppHash { get; set; } = "";

        // --- Logging configuration ---
        // Master switch for PPC connector logging on the client side.
        public bool PpcLogEnabled { get; set; } = true;
        // Minimum level forwarded to the PPC client log: DEBUG|INFO|WARN|ERROR.
        public string PpcLogLevel { get; set; } = "INFO";
        // Master switch for the TOP_APP local log (written to
        // %AppData%/WindowTopTool/logs/app.log). When false, AppLogger calls
        // become no-ops.
        public bool LocalLogEnabled { get; set; } = true;

        // --- Localization ---
        // UI language code: "" (auto-detect system culture), or one of
        // en / zh / fr / ru / ar. Persisted across runs so a manual override
        // in SettingsForm survives restart.
        public string Language { get; set; } = "";

        // Toggle click-through hotkey
        public int ClickThroughHotKeyId { get; set; } = 2;
        public int ClickThroughHotkeyModifier { get; set; } = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
        public int ClickThroughHotkeyKey { get; set; } = (int)System.Windows.Forms.Keys.T;

        // Mini mode hotkey
        public int MiniModeHotKeyId { get; set; } = 3;
        public int MiniModeHotkeyModifier { get; set; } = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
        public int MiniModeHotkeyKey { get; set; } = (int)System.Windows.Forms.Keys.M;

        // Increase opacity hotkey
        public int IncreaseOpacityHotKeyId { get; set; } = 4;
        public int IncreaseOpacityHotkeyModifier { get; set; } = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
        public int IncreaseOpacityHotkeyKey { get; set; } = (int)System.Windows.Forms.Keys.Oemplus;

        // Decrease opacity hotkey
        public int DecreaseOpacityHotKeyId { get; set; } = 5;
        public int DecreaseOpacityHotkeyModifier { get; set; } = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
        public int DecreaseOpacityHotkeyKey { get; set; } = (int)System.Windows.Forms.Keys.OemMinus;

        private static AppConfig? _instance;

        /// <summary>
        /// When non-null, configuration is read from / written to this
        /// directory (typically <c>&lt;ppc_path&gt;/app/config/</c>).
        /// When null, the legacy <c>%AppData%/WindowTopTool/</c> location is used.
        /// </summary>
        private static string? _configDirectory;

        /// <summary>True when the config directory has been migrated to PPC.</summary>
        public static bool IsPpcConfigDirectorySet => !string.IsNullOrEmpty(_configDirectory);

        // ── Portable mode ───────────────────────────────────────
        // When a marker file (WindowTopTool.portable) is present next to the
        // executable, the app runs in portable mode: configuration and logs
        // are stored next to the exe, and PPC is never contacted. A single
        // build thus serves both installed and portable use — dropping the
        // empty marker file into the build/publish output turns it into a
        // portable distribution.

        /// <summary>
        /// Marker file name that enables portable mode. Its mere presence
        /// (contents ignored) next to the executable switches the app into
        /// self-contained mode.
        /// </summary>
        private const string PortableMarkerFileName = "WindowTopTool.portable";

        /// <summary>
        /// Directory containing the running executable, used as the storage
        /// root in portable mode (config/, logs/ live here).
        /// </summary>
        public static string ExeDirectory { get; } = ResolveExeDirectory();

        /// <summary>
        /// True when running in portable mode — the marker file exists next
        /// to the executable. In portable mode the app is fully
        /// self-contained: config and logs live next to the exe, and PPC is
        /// never invoked (no connect, no auto-start, no config migration),
        /// regardless of the persisted <see cref="PpcEnabled"/> value.
        /// </summary>
        public static bool IsPortable { get; } =
            File.Exists(Path.Combine(ExeDirectory, PortableMarkerFileName));

        /// <summary>
        /// Resolve the executable's directory. Falls back to the current
        /// directory if the base directory cannot be determined.
        /// </summary>
        private static string ResolveExeDirectory()
        {
            try
            {
                var dir = AppContext.BaseDirectory;
                return string.IsNullOrEmpty(dir) ? "." : dir;
            }
            catch
            {
                return ".";
            }
        }

        public static AppConfig Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Load();
                }
                return _instance;
            }
        }

        public event EventHandler? ConfigurationChanged;

        public void Save()
        {
            var configPath = GetConfigFilePath();
            try
            {
                var dir = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = Newtonsoft.Json.JsonConvert.SerializeObject(this, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(configPath, json);
            }
            catch (Exception ex)
            {
                // Record the failure with the PPC error code, then rethrow so
                // callers (e.g. SettingsForm.OnSave) can surface user feedback.
                AppLogger.Error("Failed to save configuration", ex, PpcErrorCodes.ErrorConfigSaveFailed);
                throw;
            }

            // Notify listeners that configuration has changed
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }

        public static AppConfig Load()
        {
            var configPath = GetConfigFilePath();
            if (File.Exists(configPath))
            {
                try
                {
                    var json = File.ReadAllText(configPath);
                    var config = Newtonsoft.Json.JsonConvert.DeserializeObject<AppConfig>(json);
                    return config ?? new AppConfig();
                }
                catch (Exception ex)
                {
                    // AppLogger is intentionally NOT used here: it would re-enter
                    // AppConfig.Instance (still null during this load) and recurse.
                    // A Debug trace plus the PPC error code is enough for diagnosis.
                    System.Diagnostics.Debug.WriteLine(
                        $"Config load failed [{PpcErrorCodes.ErrorConfigLoadFailed}]: {ex.Message}");
                    return new AppConfig();
                }
            }
            return new AppConfig();
        }

        public static void Reload()
        {
            _instance = Load();
        }

        /// <summary>
        /// Switch the config storage to <c>&lt;ppcPath&gt;/app/config/</c>.
        /// The current in-memory config is immediately saved to the new
        /// location so subsequent loads find it there. The legacy
        /// <c>%AppData%</c> copy is left in place as a bootstrap fallback.
        /// </summary>
        public static void MigrateToPpcDirectory(string ppcPath)
        {
            var newDir = Path.Combine(ppcPath, "app", "config");
            if (!Directory.Exists(newDir))
                Directory.CreateDirectory(newDir);

            _configDirectory = newDir;
            AppLogger.Info($"Configuration directory migrated to '{newDir}'");

            // Persist the current config to the new location immediately.
            try
            {
                Instance.Save();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to save config after migration", ex, PpcErrorCodes.ErrorConfigSaveFailed);
            }
        }

        /// <summary>
        /// On startup, try to discover a previously-migrated config under a
        /// detected PPC installation directory. If found, switch
        /// <see cref="_configDirectory"/> so <see cref="Load"/> reads from
        /// there instead of the legacy <c>%AppData%</c> path.
        /// </summary>
        public static void TryDetectPpcConfigPath()
        {
            // Portable mode never uses the PPC-managed config directory.
            if (IsPortable)
                return;

            // Search the standard Program Files directories for a PPC install
            // that already has a migrated config.
            var searchDirs = new List<string>();
            var pf64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(pf64))
                searchDirs.Add(Path.Combine(pf64, "ppc"));
            if (!string.IsNullOrEmpty(pf86))
                searchDirs.Add(Path.Combine(pf86, "ppc"));

            foreach (var dir in searchDirs)
            {
                var configFile = Path.Combine(dir, "app", "config", "config.json");
                if (File.Exists(configFile))
                {
                    _configDirectory = Path.Combine(dir, "app", "config");
                    System.Diagnostics.Debug.WriteLine(
                        $"Detected PPC config at '{_configDirectory}'");
                    return;
                }
            }
        }

        private static string GetConfigFilePath()
        {
            // Portable mode: store config next to the executable so the
            // distribution is fully self-contained (no %AppData% writes).
            if (IsPortable)
            {
                var dir = Path.Combine(ExeDirectory, "config");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                return Path.Combine(dir, "config.json");
            }

            // When a PPC-managed config directory has been set (either by
            // migration or by auto-detection), use it. Otherwise fall back to
            // the legacy %AppData% location.
            if (!string.IsNullOrEmpty(_configDirectory))
                return Path.Combine(_configDirectory, "config.json");

            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appFolder = Path.Combine(appDataPath, "WindowTopTool");
            if (!Directory.Exists(appFolder))
                Directory.CreateDirectory(appFolder);
            return Path.Combine(appFolder, "config.json");
        }
    }
}