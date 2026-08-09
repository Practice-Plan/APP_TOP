using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WindowTopTool
{
    /// <summary>
    /// Protects pinned windows from being minimized or entering fullscreen mode
    /// </summary>
    public class WindowStateProtector : IDisposable
    {
        private readonly WindowPinManager _pinManager;
        private delegate IntPtr WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
        private WinEventProc? _winEventProc;
        private IntPtr _hook = IntPtr.Zero;
        private bool _disposed = false;

        private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
        private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        private const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
        private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
        private const uint EVENT_OBJECT_STATECHANGE = 0x800A;

        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const int SW_MINIMIZE = 6;
        private const int SW_MAXIMIZE = 3;
        private const int SW_SHOWMINIMIZED = 2;
        private const int SW_SHOWMAXIMIZED = 3;

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public WindowStateProtector(WindowPinManager pinManager)
        {
            _pinManager = pinManager;
            _winEventProc = WinEventCallback;
            InstallHook();
        }

        /// <summary>
        /// Start the state protector (hook is already installed in constructor, this is for API compatibility)
        /// </summary>
        public void Start()
        {
            // Hook is already installed in constructor; this is a no-op for API compatibility
            if (_hook == IntPtr.Zero)
            {
                InstallHook();
            }
        }

        private void InstallHook()
        {
            try
            {
                _hook = SetWinEventHook(
                    EVENT_SYSTEM_MINIMIZESTART,
                    EVENT_OBJECT_STATECHANGE,
                    IntPtr.Zero,
                    _winEventProc ?? throw new InvalidOperationException("WinEventProc not initialized"),
                    0,
                    0,
                    WINEVENT_OUTOFCONTEXT
                );

                if (_hook == IntPtr.Zero)
                {
                    Debug.WriteLine("Failed to install window event hook");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to install hook: {ex.Message}");
            }
        }

        private IntPtr WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            try
            {
                // Check if this is a pinned window
                if (!_pinManager.IsPinned(hwnd))
                    return IntPtr.Zero;

                // Handle minimize/maximize attempt
                if (eventType == EVENT_SYSTEM_MINIMIZESTART || eventType == EVENT_OBJECT_STATECHANGE)
                {
                    var placement = new NativeMethods.WINDOWPLACEMENT();
                    placement.length = Marshal.SizeOf(placement);
                    NativeMethods.GetWindowPlacement(hwnd, ref placement);

                    // If window is being minimized, restore it
                    if (placement.showCmd == SW_SHOWMINIMIZED)
                    {
                        Debug.WriteLine($"Preventing minimize for pinned window: {hwnd}");
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            System.Threading.Thread.Sleep(50);
                            ShowWindow(hwnd, 9); // SW_RESTORE
                        });
                        ShowBlockNotification(hwnd, LocalizationManager.GetString("StateProtector_Minimized"));
                    }

                    // If window is being maximized (fullscreen), restore it
                    if (placement.showCmd == SW_SHOWMAXIMIZED)
                    {
                        // Check if it's truly fullscreen
                        if (NativeMethods.GetWindowRect(hwnd, out var rect))
                        {
                            var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
                            if (screen != null)
                            {
                                bool isFullscreen = rect.Left <= 0 && rect.Top <= 0 &&
                                     rect.Right >= screen.Bounds.Right &&
                                     rect.Bottom >= screen.Bounds.Bottom;

                                if (isFullscreen)
                                {
                                    Debug.WriteLine($"Preventing fullscreen for pinned window: {hwnd}");
                                    System.Threading.Tasks.Task.Run(() =>
                                    {
                                        System.Threading.Thread.Sleep(50);
                                        ShowWindow(hwnd, 1); // SW_NORMAL
                                    });
                                    ShowBlockNotification(hwnd, LocalizationManager.GetString("StateProtector_Fullscreen"));
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in event callback: {ex.Message}");
            }

            return IntPtr.Zero;
        }

        private void ShowBlockNotification(IntPtr hWnd, string operation)
        {
            try
            {
                // Get window title
                var length = NativeMethods.GetWindowTextLength(hWnd);
                if (length == 0)
                    return;

                var sb = new System.Text.StringBuilder(length + 1);
                NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
                var title = sb.ToString();

                // Show notification (this will be handled by TrayManager)
                // For now, just log it
                Debug.WriteLine($"Blocked {operation} operation for pinned window: {title}");
            }
            catch
            {
                // Ignore notification errors
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                if (_hook != IntPtr.Zero)
                {
                    UnhookWinEvent(_hook);
                }
                _disposed = true;
            }
        }
    }
}