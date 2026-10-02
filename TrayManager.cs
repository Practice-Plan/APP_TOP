using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace WindowTopTool
{
    /// <summary>
    /// Manages the system-tray icon and context menu using the native
    /// <c>Shell_NotifyIconW</c> API (no WinForms dependency). A hidden
    /// top-level Win32 window receives the tray callback messages and
    /// forwards them onto the WinUI3 dispatcher.
    /// </summary>
    public sealed class TrayManager : IDisposable
    {
        private const int TrayIconId = 1;
        private const int BaseMenuItemId = 1000;

        // Menu item IDs — declared once so handlers can switch on them.
        private const int MenuPinActive = BaseMenuItemId + 1;
        private const int MenuWindowList = BaseMenuItemId + 2;
        private const int MenuSettings = BaseMenuItemId + 3;
        private const int MenuUnpinAll = BaseMenuItemId + 4;
        private const int MenuExit = BaseMenuItemId + 5;

        private readonly WindowPinManager _pinManager;
        private readonly DispatcherQueue _dispatcher;
        private IntPtr _trayWindow;
        private IntPtr _hIcon;
        private bool _iconAdded;
        private bool _disposed;

        public event EventHandler? SettingsRequested;
        public event EventHandler? ExitRequested;

        public TrayManager(WindowPinManager pinManager)
        {
            _pinManager = pinManager ?? throw new ArgumentNullException(nameof(pinManager));
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            _pinManager.WindowsChanged += OnWindowsChanged;
            LocalizationManager.LanguageChanged += OnLanguageChanged;

            CreateTrayWindow();
            LoadIcon();
            RegisterTrayIcon();
        }

        // ──────────────────────────────────────────────────────
        // Hidden tray message window
        // ──────────────────────────────────────────────────────

        private static IntPtr _windowClassAtom;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private static readonly WndProcDelegate _trayWindowProcDelegate = TrayWindowProc;

        private static IntPtr RegisterWindowClass()
        {
            if (_windowClassAtom != IntPtr.Zero) return _windowClassAtom;

            var hInstance = NativeMethods.GetModuleHandle(null);
            var wc = new NativeMethods.WNDCLASSEXW
            {
                cbSize = Marshal.SizeOf(typeof(NativeMethods.WNDCLASSEXW)),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_trayWindowProcDelegate),
                hInstance = hInstance,
                lpszClassName = "WindowTopTool.TrayMessageWindow"
            };
            var registered = NativeMethods.RegisterClassExW(ref wc);
            if (!registered)
                AppLogger.Warn($"RegisterClassExW failed: {Marshal.GetLastWin32Error()}");
            _windowClassAtom = registered ? new IntPtr(1) : IntPtr.Zero;
            return _windowClassAtom;
        }

        private void CreateTrayWindow()
        {
            RegisterWindowClass();
            var hInstance = NativeMethods.GetModuleHandle(null);
            _trayWindow = NativeMethods.CreateWindowExW(
                0, "WindowTopTool.TrayMessageWindow",
                "WindowTopTool Tray", 0,
                0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (_trayWindow == IntPtr.Zero)
            {
                AppLogger.Error("Failed to create tray message window");
            }
        }

        private static IntPtr TrayWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == NativeMethods.WM_TRAYICON)
            {
                var lParamValue = lParam.ToInt64();
                var mouseMsg = (int)(lParamValue & 0xFFFF);
                switch (mouseMsg)
                {
                    case 0x0200: // WM_MOUSEMOVE
                    case 0x0204: // WM_RBUTTONDOWN
                    case 0x0205: // WM_RBUTTONUP
                    case 0x0206: // WM_RBUTTONDBLCLK
                        break;
                    case 0x0201: // WM_LBUTTONDOWN
                    case 0x0202: // WM_LBUTTONUP
                        break;
                    case 0x0203: // WM_LBUTTONDBLCLK
                        TrayManagerRegistry.Get(hWnd)?.HandleTrayLeftDoubleClick();
                        return IntPtr.Zero;
                    case 0x0208: // WM_MBUTTONUP — ignore
                        break;
                    default:
                        break;
                }

                if (mouseMsg == 0x0205) // WM_RBUTTONUP — show context menu on right-click release
                {
                    TrayManagerRegistry.Get(hWnd)?.ShowContextMenu();
                }
                return IntPtr.Zero;
            }

            if (msg == NativeMethods.WM_COMMAND)
            {
                var commandId = wParam.ToInt32() & 0xFFFF;
                TrayManagerRegistry.Get(hWnd)?.HandleMenuCommand(commandId);
                return IntPtr.Zero;
            }

            if (msg == NativeMethods.WM_DESTROY)
            {
                TrayManagerRegistry.Remove(hWnd);
                return IntPtr.Zero;
            }

            return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        // ──────────────────────────────────────────────────────
        // Icon loading & registration
        // ──────────────────────────────────────────────────────

        private void LoadIcon()
        {
            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
                if (File.Exists(iconPath))
                {
                    using var fileStream = new FileStream(iconPath, FileMode.Open, FileAccess.Read);
                    var icon = new Icon(fileStream);
                    _hIcon = icon.Handle;
                    // Icon becomes owned by the tray — do not dispose the managed wrapper.
                    GC.SuppressFinalize(icon);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Could not load tray icon from file: {ex.Message}");
            }

            if (_hIcon == IntPtr.Zero)
            {
                _hIcon = CreateFallbackIcon();
            }
        }

        private static IntPtr CreateFallbackIcon()
        {
            using var bmp = new Bitmap(32, 32);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(120, 120, 130), 2.5f);
            g.DrawEllipse(pen, 7, 5, 18, 18);
            g.DrawLine(pen, 16, 23, 16, 30);
            using var highlightPen = new Pen(Color.FromArgb(160, 160, 170), 1f);
            g.DrawEllipse(highlightPen, 10, 8, 12, 12);
            var hIcon = bmp.GetHicon();
            return hIcon;
        }

        private void RegisterTrayIcon()
        {
            if (_trayWindow == IntPtr.Zero) return;

            TrayManagerRegistry.Register(_trayWindow, this);

            var data = new NativeMethods.NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf(typeof(NativeMethods.NOTIFYICONDATAW)),
                hWnd = _trayWindow,
                uID = TrayIconId,
                uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP,
                uCallbackMessage = NativeMethods.WM_TRAYICON,
                hIcon = _hIcon,
                szTip = AppConfig.AppDisplayName,
                uVersion = NativeMethods.NOTIFYICON_VERSION_4
            };

            if (NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref data))
            {
                _iconAdded = true;
                // Update to v4 so we get richer mouse messages.
                NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_SETVERSION, ref data);
            }
            else
            {
                AppLogger.Error($"Shell_NotifyIconW(NIM_ADD) failed: {Marshal.GetLastWin32Error()}");
            }
        }

        private void UpdateTrayIcon()
        {
            if (!_iconAdded) return;

            var count = _pinManager.PinnedCount;
            var tip = count > 0
                ? $"{AppConfig.AppDisplayName} ({count} pinned)"
                : AppConfig.AppDisplayName;

            var data = new NativeMethods.NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf(typeof(NativeMethods.NOTIFYICONDATAW)),
                hWnd = _trayWindow,
                uID = TrayIconId,
                uFlags = NativeMethods.NIF_TIP,
                szTip = tip
            };
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data);
        }

        // ──────────────────────────────────────────────────────
        // Context menu (dynamically rebuilt each time so labels
        // always reflect the current locale and pinned-window list)
        // ──────────────────────────────────────────────────────

        private void ShowContextMenu()
        {
            if (_trayWindow == IntPtr.Zero) return;
            var hMenu = NativeMethods.CreatePopupMenu();
            if (hMenu == IntPtr.Zero) return;

            try
            {
                Append(hMenu, MenuPinActive, LocalizationManager.GetString("Tray_PinActiveWindow"));
                NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, null);
                Append(hMenu, MenuWindowList, LocalizationManager.GetString("Tray_WindowPinOptions"));
                NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, null);
                Append(hMenu, MenuSettings, LocalizationManager.GetString("Tray_Settings"));
                Append(hMenu, MenuUnpinAll, LocalizationManager.GetString("Tray_UnpinAll"));
                NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, null);
                Append(hMenu, MenuExit, LocalizationManager.GetString("Tray_Exit"));

                NativeMethods.GetCursorPos(out var pt);
                NativeMethods.SetForegroundWindow(_trayWindow);
                NativeMethods.TrackPopupMenu(
                    hMenu,
                    NativeMethods.TPM_LEFTALIGN | NativeMethods.TPM_BOTTOMALIGN,
                    pt.X, pt.Y, 0, _trayWindow, IntPtr.Zero);
                NativeMethods.PostMessageW(_trayWindow, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                NativeMethods.DestroyMenu(hMenu);
            }
        }

        private static void Append(IntPtr hMenu, int id, string text)
        {
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, new IntPtr(id), text);
        }

        private void HandleTrayLeftDoubleClick()
        {
            RunOnUiThread(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        }

        private void HandleMenuCommand(int commandId)
        {
            switch (commandId)
            {
                case MenuPinActive:
                    RunOnUiThread(() => PinCurrentActiveWindow());
                    break;
                case MenuWindowList:
                    RunOnUiThread(() => ShowWindowPinPicker());
                    break;
                case MenuSettings:
                    RunOnUiThread(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
                    break;
                case MenuUnpinAll:
                    RunOnUiThread(() =>
                    {
                        _pinManager.UnpinAll();
                        ShowNotification(
                            LocalizationManager.GetString("Main_UnpinAllTitle"),
                            LocalizationManager.GetString("Main_UnpinAllBody"));
                    });
                    break;
                case MenuExit:
                    RunOnUiThread(() => ExitRequested?.Invoke(this, EventArgs.Empty));
                    break;
            }
        }

        private void RunOnUiThread(Action action)
        {
            if (_dispatcher.HasThreadAccess)
            {
                try { action(); }
                catch (Exception ex) { AppLogger.Error("Tray command failed", ex); }
            }
            else
            {
                _dispatcher.TryEnqueue(() =>
                {
                    try { action(); }
                    catch (Exception ex) { AppLogger.Error("Tray command failed", ex); }
                });
            }
        }

        // ──────────────────────────────────────────────────────
        // Pin-current-window helper
        // ──────────────────────────────────────────────────────

        private void PinCurrentActiveWindow()
        {
            try
            {
                var hWnd = NativeMethods.GetForegroundWindow();
                if (hWnd == IntPtr.Zero)
                {
                    ShowNotification(LocalizationManager.GetString("Tray_OperationFailed"),
                        LocalizationManager.GetString("Tray_NoActiveWindow"), NIIF_WARNING);
                    return;
                }

                if (_pinManager.IsPinned(hWnd))
                {
                    var title = GetWindowTitle(hWnd);
                    _pinManager.UnpinWindow(hWnd);
                    ShowNotification(LocalizationManager.GetString("Tray_Unpinned"),
                        string.IsNullOrEmpty(title) ? LocalizationManager.GetString("Tray_UnnamedWindow") : title);
                }
                else
                {
                    var ok = _pinManager.PinWindow(hWnd);
                    var title = GetWindowTitle(hWnd);
                    if (ok)
                        ShowNotification(LocalizationManager.GetString("Tray_Pinned"),
                            string.IsNullOrEmpty(title) ? LocalizationManager.GetString("Tray_UnnamedWindow") : title);
                    else
                        ShowNotification(LocalizationManager.GetString("Tray_CannotPin"),
                            LocalizationManager.GetString("Tray_CannotPinBody"), NIIF_WARNING);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to pin active window from tray", ex);
                ShowNotification(LocalizationManager.GetString("Tray_OperationError"),
                    LocalizationManager.GetString("Tray_OperationErrorBody"), NIIF_ERROR);
            }
        }

        private void ShowWindowPinPicker()
        {
            // This previously spawned a dynamic WinForms ContextMenuStrip.
            // In the pure WinUI3 world we hand off to the main settings
            // window — it exposes the same window list via SettingsView.
            SettingsRequested?.Invoke(this, EventArgs.Empty);
        }

        private static string GetWindowTitle(IntPtr hWnd)
        {
            var len = NativeMethods.GetWindowTextLength(hWnd);
            if (len <= 0) return string.Empty;
            var sb = new System.Text.StringBuilder(len + 1);
            NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        // ──────────────────────────────────────────────────────
        // Bubble notification (Shell_NotifyIcon with NIF_INFO)
        // ──────────────────────────────────────────────────────

        public const int NIIF_NONE = 0x00000000;
        public const int NIIF_WARNING = 0x00000002;
        public const int NIIF_ERROR = 0x00000003;
        public const int NIIF_INFO = 0x00000001;

        public void ShowNotification(string title, string message, int icon = NIIF_INFO)
        {
            var config = AppConfig.Instance;

            // Respect user preferences at the tray-manager level — callers
            // (e.g. WindowPinManager) no longer need to re-check.
            if (!config.ShowNotifications) return;
            if (icon == NIIF_INFO && !config.ShowPinNotifications) return;
            if ((icon == NIIF_ERROR || icon == NIIF_WARNING) && !config.ShowErrorNotifications) return;

            if (!_iconAdded || _trayWindow == IntPtr.Zero) return;

            // Clamp lengths — Shell_NotifyIcon rejects strings that exceed
            // the struct sizes (szInfo 256, szInfoTitle 64).
            if (message.Length > 255) message = message[..255];
            if (title.Length > 63) title = title[..63];

            var data = new NativeMethods.NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf(typeof(NativeMethods.NOTIFYICONDATAW)),
                hWnd = _trayWindow,
                uID = TrayIconId,
                uFlags = NativeMethods.NIF_INFO,
                szInfo = message,
                szInfoTitle = title,
                dwInfoFlags = icon,
                uVersion = NativeMethods.NOTIFYICON_VERSION_4
            };

            if (!NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data))
            {
                AppLogger.Warn($"Tray bubble failed: {Marshal.GetLastWin32Error()}");
            }
        }

        // ──────────────────────────────────────────────────────
        // Event plumbing
        // ──────────────────────────────────────────────────────

        private void OnWindowsChanged(object? sender, EventArgs e) => UpdateTrayIcon();

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            // Tip text depends on localized product name and pinned count.
            UpdateTrayIcon();
        }

        // ──────────────────────────────────────────────────────
        // Dispose
        // ──────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _pinManager.WindowsChanged -= OnWindowsChanged;
            LocalizationManager.LanguageChanged -= OnLanguageChanged;

            try
            {
                if (_iconAdded)
                {
                    var data = new NativeMethods.NOTIFYICONDATAW
                    {
                        cbSize = Marshal.SizeOf(typeof(NativeMethods.NOTIFYICONDATAW)),
                        hWnd = _trayWindow,
                        uID = TrayIconId
                    };
                    NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
                    _iconAdded = false;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Tray icon unregistration failed", ex);
            }

            if (_trayWindow != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_trayWindow);
                _trayWindow = IntPtr.Zero;
            }

            if (_hIcon != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(_hIcon);
                _hIcon = IntPtr.Zero;
            }
        }
    }

    // ─── WNDCLASSEX alias for backward compatibility (NativeMethods.WNDCLASSEXW is canonical) ─

    /// <summary>
    /// Weak map between the tray window handle and the owning TrayManager
    /// instance. The static callback needs to find its way back to the
    /// managed object that owns that window — this is the safest way to do
    /// it without leaking GCHandles.
    /// </summary>
    internal static class TrayManagerRegistry
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, TrayManager> _map = new();

        public static void Register(IntPtr hWnd, TrayManager manager) => _map[hWnd] = manager;
        public static TrayManager? Get(IntPtr hWnd) => _map.TryGetValue(hWnd, out var m) ? m : null;
        public static void Remove(IntPtr hWnd) => _map.TryRemove(hWnd, out _);
    }
}
