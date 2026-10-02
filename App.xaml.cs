using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace WindowTopTool;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    public DispatcherQueue Dispatcher { get; } = null!;
    public WindowPinManager PinManager { get; } = null!;
    public TrayManager? TrayManager { get; private set; }
    public EdgeAutoHideManager? AutoHideManager { get; private set; }
    public MiniWindowManager MiniWindowManager { get; } = null!;
    public WindowHookManager? HookManager { get; private set; }
    public MainWindow? MainWindow { get; private set; }

    private PpcSession? _ppc;

    private volatile bool _shuttingDown;

    // Single-instance guard: one copy per user session. The mutex is held for
    // the whole process lifetime; a second launch detects it is already taken,
    // brings the first instance's window to the foreground, and exits.
    private const string SingleInstanceMutexName = "wang.station.WindowTopTool.SingleInstance";
    private static Mutex? _singleInstanceMutex;
    private bool _isFirstInstance = true;

    public App()
    {
        try
        {
            // Single-instance check first: bail out of initialization if another
            // copy is already running (OnLaunched handles activation + exit).
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
            _isFirstInstance = createdNew;
            if (!_isFirstInstance)
                return;

            InitializeComponent();

            AppConfig.TryDetectPpcConfigPath();
            AppLogger.Configure(AppConfig.Instance.LocalLogEnabled, AppConfig.Instance.PpcLogLevel);
            LocalizationManager.Initialize();

            Dispatcher = DispatcherQueue.GetForCurrentThread();

            PinManager = new WindowPinManager();
            PinManager.RestoreState();
            MiniWindowManager = new MiniWindowManager(PinManager);
        }
        catch (Exception ex)
        {
            try
            {
                var dir = System.IO.Path.Combine(Path.GetTempPath(), "WindowTopTool");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, "startup_error.log"),
                    $"[{DateTime.Now:HH:mm:ss}] CTOR FAIL: {ex}\n\n");
            }
            catch { /* ignore */ }
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // A second launch: surface the existing window and quit immediately.
        if (!_isFirstInstance)
        {
            ActivateExistingInstance();
            try { Exit(); } catch { /* nothing more we can do */ }
            return;
        }

        // If the constructor failed to spin up core services, exit now rather
        // than risking NullReferenceExceptions deep inside OnLaunched.
        if (PinManager is null || Dispatcher is null)
        {
            try { Exit(); } catch { /* nothing more we can do */ }
            return;
        }

        try
        {
            base.OnLaunched(args);

            try { TrayManager = new TrayManager(PinManager); }
            catch (Exception ex) { AppLogger.Error("TrayManager init failed", ex); }

            if (TrayManager is not null)
            {
                TrayManager.SettingsRequested += (_, _) => OpenMainWindow();
                TrayManager.ExitRequested += (_, _) => ExitApp();
            }

            try { AutoHideManager = new EdgeAutoHideManager(PinManager); AutoHideManager.Start(); }
            catch (Exception ex) { AppLogger.Error("AutoHideManager init failed", ex); }

            try { HookManager = new WindowHookManager(PinManager); }
            catch (Exception ex) { AppLogger.Error("HookManager init failed", ex); }

            PinManager.WindowsChanged += OnConfigurationChanged;
            AppConfig.Instance.ConfigurationChanged += OnConfigurationChanged;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                var ex = e.ExceptionObject as Exception ?? new Exception(Convert.ToString(e.ExceptionObject));
                AppLogger.Error("Unhandled exception", ex);
            };

            OpenMainWindow();

            // Long-lived, self-healing PPC connection (launch + connect + register
            // + keep-alive/reconnect + error forwarding) runs off the UI thread.
            _ppc = new PpcSession(AppConfig.Instance);
            AppLogger.ErrorSink = msg => _ppc?.SendError(msg);
            _ppc.Start();
        }
        catch (Exception ex)
        {
            try
            {
                var dir = System.IO.Path.Combine(Path.GetTempPath(), "WindowTopTool");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, "startup_error.log"),
                    $"[{DateTime.Now:HH:mm:ss}] OnLaunched FAIL: {ex}\n\n");
            }
            catch { /* ignore */ }
        }
    }

    public void OpenMainWindow()
    {
        if (!Dispatcher.HasThreadAccess)
        {
            AppLogger.Info("OpenMainWindow: marshaling to UI thread");
            Dispatcher.TryEnqueue(OpenMainWindow);
            return;
        }

        AppLogger.Info($"OpenMainWindow: MainWindow null={MainWindow is null}");
        MainWindow ??= new MainWindow(PinManager);

        // Ensure the Win32 host window is visible. WinUI3's AppWindow.Show()
        // can be a no-op after the window has been hidden once.
        MainWindow.AppWindow.Show();
        MainWindow.Activate();
        AppLogger.Info("OpenMainWindow: Show+Activate done");
    }

    // PPC connection logic (launch, connect+retry, register/update/auth,
    // keep-alive/reconnect, version check, error/info forwarding) now lives in
    // PpcSession.cs and is driven by the _ppc field created in OnLaunched.

    /// <summary>
    /// Bring the already-running instance's main window to the foreground.
    /// Called when a duplicate launch is detected. Best-effort — Windows may
    /// block cross-process foreground changes, in which case we silently give up.
    /// </summary>
    private static void ActivateExistingInstance()
    {
        const int SW_RESTORE = 9;
        try
        {
            var hWnd = NativeMethods.FindWindow(null, "Window Top Tool");
            if (hWnd != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(hWnd, SW_RESTORE);
                NativeMethods.SetForegroundWindow(hWnd);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"Could not activate existing instance: {ex.Message}");
        }
    }

    /// <summary>
    /// Cleanly exit the application: save state, dispose services, then call
    /// <see cref="Application.Exit"/> so the process actually terminates
    /// (closing the last window alone is not enough when a tray icon lives on).
    /// </summary>
    public void ExitApp()
    {
        if (!Dispatcher.HasThreadAccess)
        {
            Dispatcher.TryEnqueue(ExitApp);
            return;
        }

        try
        {
            MainWindow?.ForceClose();
            MainWindow = null;
        }
        catch (Exception ex) { AppLogger.Warn($"Closing main window failed: {ex.Message}"); }

        Shutdown();

        try { Exit(); }
        catch (Exception ex) { AppLogger.Warn($"Application.Exit threw: {ex.Message}"); }
    }

    private void OnConfigurationChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.HasThreadAccess)
        {
            Dispatcher.TryEnqueue(() => OnConfigurationChanged(sender, e));
            return;
        }

        try
        {
            PinManager.ApplyGlobalSettings();
            AutoHideManager?.UpdatePiPSize();
            HookManager?.RefreshClickThroughState();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Config change failed: {ex.Message}");
        }
    }

    private void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        try
        {
            AutoHideManager?.Stop();
            PinManager.SaveState();
            TrayManager?.Dispose();
            AutoHideManager?.Dispose();
            HookManager?.Dispose();

            // Stop forwarding errors to PPC, then shut the session down.
            AppLogger.ErrorSink = null;
            var ppc = _ppc;
            _ppc = null;
            ppc?.Dispose();

            try
            {
                if (_isFirstInstance)
                    _singleInstanceMutex?.ReleaseMutex();
            }
            catch (Exception ex) { AppLogger.Warn($"Releasing single-instance mutex failed: {ex.Message}"); }
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Shutdown cleanup failed", ex);
        }
    }
}
