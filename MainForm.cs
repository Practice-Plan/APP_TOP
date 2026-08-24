using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WindowTopTool
{
    /// <summary>
    /// Main form (hidden window for message handling)
    /// </summary>
    public class MainForm : Form
    {
        private readonly WindowPinManager _pinManager;
        private readonly TrayManager _trayManager;
        private readonly EdgeAutoHideManager _autoHideManager;
        private readonly MiniWindowManager _miniWindowManager;
        private readonly WindowHookManager? _hookManager;
        private readonly System.Windows.Forms.Timer _cleanupTimer;
        private readonly System.Windows.Forms.Timer _autoSaveTimer;
        private SettingsForm? _settingsForm;
        private PpcConnector? _ppcConnector;

        public MainForm()
        {
            // Initialize components
            _pinManager = new WindowPinManager();
            _trayManager = new TrayManager(_pinManager);
            _autoHideManager = new EdgeAutoHideManager(_pinManager);
            _autoHideManager.PiPSize = new Size(_config.PiPWidth, _config.PiPHeight);
            _miniWindowManager = new MiniWindowManager(_pinManager);

            // Initialize hook manager for click-through
            try
            {
                _hookManager = new WindowHookManager(_pinManager);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize hook manager: {ex.Message}");
            }

            // Configure form
            ConfigureForm();

            // Setup event handlers
            SetupEventHandlers();

            // Setup cleanup timer
            _cleanupTimer = new System.Windows.Forms.Timer
            {
                Interval = 5000 // Check every 5 seconds
            };
            _cleanupTimer.Tick += (s, e) => _pinManager.CleanupInvalidWindows();
            _cleanupTimer.Start();

            // Setup auto-save timer
            _autoSaveTimer = new System.Windows.Forms.Timer
            {
                Interval = AppConfig.Instance.AutoSaveInterval * 1000
            };
            _autoSaveTimer.Tick += (s, e) => _pinManager.SaveState();
            _autoSaveTimer.Start();

            // Start edge auto-hide manager
            _autoHideManager.Start();

            // Restore state
            _pinManager.RestoreState();

            // Initialize PPC connection (runs on a background thread so the
            // UI thread is never blocked by a slow or unreachable server).
            InitializePpc();
        }

        /// <summary>
        /// Connect to the PPC server when <see cref="AppConfig.PpcEnabled"/> is
        /// set. Runs on a thread-pool thread because Connect/Register are
        /// synchronous TCP calls. The flow is:
        /// <list type="number">
        /// <item>Connect to PPC. If the server is not running, attempt to
        ///   start it automatically in a visible terminal
        ///   (PATH → Program Files → Program Files (x86)).</item>
        /// <item>If the connection still cannot be established after the
        ///   terminal auto-start, show a localized warning window so users in
        ///   any language environment understand the failure.</item>
        /// <item>If a stored hash exists, try AUTH; on failure re-REGISTER.</item>
        /// <item>Otherwise REGISTER to obtain a fresh hash, then AUTH.</item>
        /// <item>Check PPC version compatibility.</item>
        /// <item>Query PPCPATH and migrate the config to
        ///   <c>&lt;ppc_path&gt;/app/config/</c>.</item>
        /// </list>
        /// All failures are logged but never crash the app — PPC is an optional
        /// telemetry/management channel, not a hard dependency.
        /// </summary>
        private void InitializePpc()
        {
            var config = AppConfig.Instance;
            // Portable mode never contacts PPC — hard guarantee that holds
            // even if a portable config file (e.g. copied from an install)
            // happens to carry PpcEnabled=true.
            if (AppConfig.IsPortable || !config.PpcEnabled)
                return;

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                // True only when both the direct connection AND the terminal
                // auto-start failed — the case that warrants a warning window.
                bool connectionAndStartupFailed = false;
                try
                {
                    AppLogger.Info($"Connecting to PPC at {config.PpcHost}:{config.PpcPort}");
                    _ppcConnector = new PpcConnector(config.PpcHost, config.PpcPort);

                    try
                    {
                        _ppcConnector.Connect();
                    }
                    catch (PpcException) when (!AppConfig.IsPpcConfigDirectorySet)
                    {
                        // PPC server is not running — try to start it in a
                        // visible terminal so the user can see its output.
                        AppLogger.Warn(
                            "PPC server not reachable; attempting auto-start in terminal",
                            PpcErrorCodes.WarningPpcReconnect);
                        if (PpcConnector.TryStartPpcServer())
                        {
                            _ppcConnector.Connect();
                        }
                        else
                        {
                            // Both the direct connection and the terminal
                            // auto-start failed — flag this so the outer
                            // handler shows a localized warning window.
                            connectionAndStartupFailed = true;
                            throw;
                        }
                    }
                    AppLogger.Info("Connected to PPC server");

                    var exePath = Application.ExecutablePath;

                    // Try the stored hash first; re-register if it is rejected.
                    if (!string.IsNullOrEmpty(config.PpcAppHash))
                    {
                        try
                        {
                            _ppcConnector.Authenticate(config.PpcAppHash);
                            AppLogger.Info($"Authenticated with PPC as '{config.PpcAppId}' (stored hash)");
                        }
                        catch (PpcException)
                        {
                            AppLogger.Warn(
                                "Stored PPC hash rejected; re-registering",
                                PpcErrorCodes.WarningPpcReconnect);
                            var hash = _ppcConnector.RegisterApp(
                                config.PpcAppId, config.PpcAppVersion, exePath);
                            config.PpcAppHash = hash;
                            config.Save();
                            _ppcConnector.AuthenticateWithStoredHash();
                            AppLogger.Info("Re-registered and authenticated with PPC");
                        }
                    }
                    else
                    {
                        var hash = _ppcConnector.RegisterApp(
                            config.PpcAppId, config.PpcAppVersion, exePath);
                        config.PpcAppHash = hash;
                        config.Save();
                        _ppcConnector.AuthenticateWithStoredHash();
                        AppLogger.Info("Registered and authenticated with PPC; hash stored");
                    }

                    // Verify the PPC server version is within the supported range.
                    var versionCheck = _ppcConnector.CheckVersionCompatibility();
                    if (versionCheck.IsSupported)
                        AppLogger.Info($"PPC version check passed: {versionCheck.Reason}");
                    else
                        AppLogger.Warn(
                            $"PPC version check failed: {versionCheck.Reason}",
                            PpcErrorCodes.WarningPpcReconnect);

                    // Migrate config to <ppc_path>/app/config/ if not already there.
                    MigrateConfigToPpcDirectory();
                }
                catch (PpcException ex)
                {
                    AppLogger.Error(
                        "PPC connection failed",
                        ex,
                        ex.ErrorCode ?? PpcErrorCodes.ErrorPpcNotRunning);
                    ForwardErrorToPpc($"PPC connection failed [{ex.ErrorCode ?? PpcErrorCodes.ErrorPpcNotRunning}]: {ex.Message}");
                    if (connectionAndStartupFailed)
                    {
                        ShowPpcConnectionFailedWarning(config.PpcHost, config.PpcPort);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error(
                        "PPC initialization error",
                        ex,
                        PpcErrorCodes.ErrorPpcNotRunning);
                    ForwardErrorToPpc($"PPC init error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Shows a localized warning window explaining that the PPC connection
        /// and the automatic terminal start both failed. The text is resolved
        /// via <see cref="LocalizationManager"/> so it follows the active
        /// language (English/Chinese/French/Russian/Arabic) and, for Arabic,
        /// the message box is shown right-to-left. Marshalled to the UI thread
        /// because <see cref="InitializePpc"/> runs on a thread-pool thread.
        /// </summary>
        private void ShowPpcConnectionFailedWarning(string host, int port)
        {
            var title = LocalizationManager.GetString("Ppc_ConnectionFailedTitle");
            var body = LocalizationManager.GetString("Ppc_ConnectionFailedBody", host, port);

            // RTL message boxes require an owner window, so set the options
            // based on the active language and always pass `this` as owner.
            var options = LocalizationManager.IsRightToLeft
                ? MessageBoxOptions.RtlReading
                : (MessageBoxOptions)0;

            // Marshal onto the UI thread — MessageBox must be shown there and
            // IsHandleCreated may be false during very early startup.
            void ShowBox()
            {
                MessageBox.Show(this, body, title,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button1, options);
            }

            try
            {
                if (IsHandleCreated && InvokeRequired)
                    BeginInvoke((Action)ShowBox);
                else
                    ShowBox();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to show PPC connection warning window", ex);
            }
        }

        /// <summary>
        /// Query the PPC installation path and migrate the application config
        /// to <c>&lt;ppc_path&gt;/app/config/</c>. Called once after a
        /// successful PPC connection + authentication. Silently skipped when
        /// the config is already stored under a PPC directory.
        /// </summary>
        private void MigrateConfigToPpcDirectory()
        {
            if (AppConfig.IsPpcConfigDirectorySet)
                return; // Already migrated.

            if (_ppcConnector == null || !_ppcConnector.IsAuthenticated)
                return;

            try
            {
                var ppcPath = _ppcConnector.GetPpcPath()?.Trim();
                if (string.IsNullOrEmpty(ppcPath))
                {
                    AppLogger.Warn("PPCPATH returned an empty path; skipping config migration");
                    return;
                }
                AppConfig.MigrateToPpcDirectory(ppcPath);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to migrate config to PPC directory", ex);
                ForwardErrorToPpc($"Config migration failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Forward an error message to PPC's WINDOW ERROR popup. Silently
        /// skips if PPC is not connected. This implements v0.0.5's
        /// "all error status codes shown via PPC WINDOW" requirement.
        /// </summary>
        private void ForwardErrorToPpc(string message)
        {
            try
            {
                _ppcConnector?.ShowErrorWindow($"[WindowTopTool] {message}");
            }
            catch
            {
                // Silent fail — don't let error notification cause more errors.
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Show settings form on startup
            ShowSettingsForm();
        }

        private void ConfigureForm()
        {
            // Make form invisible
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            Opacity = 0;
            WindowState = FormWindowState.Minimized;
            Load += (s, e) => Hide();
        }

        private void SetupEventHandlers()
        {
            // Pin manager events
            _pinManager.WindowPinned += OnWindowPinned;
            _pinManager.WindowUnpinned += OnWindowUnpinned;

            // Tray events
            _trayManager.PinWindowRequested += OnPinWindowRequested;
            _trayManager.UnpinAllRequested += OnUnpinAllRequested;
            _trayManager.SettingsRequested += OnSettingsRequested;
            _trayManager.ExitRequested += OnExitRequested;
        }

        private void ShowSettingsForm()
        {
            if (_settingsForm == null || _settingsForm.IsDisposed)
            {
                _settingsForm = new SettingsForm(_pinManager);
                _settingsForm.FormClosed += (s, e) => _settingsForm = null;
                _settingsForm.Show();
            }
            else
            {
                _settingsForm.Activate();
            }
        }

        private void OnSettingsRequested(object? sender, EventArgs e)
        {
            ShowSettingsForm();
        }

        private void OnPinWindowRequested(object? sender, IntPtr hWnd)
        {
            if (hWnd != IntPtr.Zero)
            {
                _pinManager.TogglePin(hWnd);
            }
        }

        private void OnWindowPinned(object? sender, WindowPinEventArgs e)
        {
            _trayManager.ShowNotification(
                LocalizationManager.GetString("Main_WindowPinned"),
                e.Window.Title
            );
        }

        private void OnWindowUnpinned(object? sender, WindowPinEventArgs e)
        {
            _trayManager.ShowNotification(
                LocalizationManager.GetString("Main_WindowUnpinned"),
                e.Window.Title
            );
        }

        private void OnUnpinAllRequested(object? sender, EventArgs e)
        {
            _pinManager.UnpinAll();
            _trayManager.ShowNotification(
                LocalizationManager.GetString("Main_UnpinAllTitle"),
                LocalizationManager.GetString("Main_UnpinAllBody"));
        }

        private void OnExitRequested(object? sender, EventArgs e)
        {
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cleanupTimer?.Stop();
                _cleanupTimer?.Dispose();

                _autoSaveTimer?.Stop();
                _autoSaveTimer?.Dispose();

                _autoHideManager?.Stop();
                _autoHideManager?.Dispose();

                _hookManager?.Dispose();

                _pinManager?.SaveState();
                _trayManager?.Dispose();

                _settingsForm?.Close();
                _settingsForm?.Dispose();

                _ppcConnector?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
