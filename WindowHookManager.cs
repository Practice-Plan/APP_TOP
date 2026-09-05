using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WindowTopTool
{
    /// <summary>
    /// Mouse click-through manager.
    ///
    /// Core mechanism:
    /// 1. WS_EX_TRANSPARENT — automatically applied to inactive pinned windows
    ///    so click/hover events are forwarded directly to the window beneath by
    ///    the OS (zero latency, zero flicker).
    /// 2. WH_MOUSE_LL low-level mouse hook — detects double-click gestures:
    ///    when the user clicks twice in a row over an inactive pinned window,
    ///    that window is activated (WS_EX_TRANSPARENT is removed and
    ///    SetForegroundWindow is called).
    /// 3. SetWinEventHook(EVENT_SYSTEM_FOREGROUND) — monitors foreground-window
    ///    changes: removes WS_EX_TRANSPARENT when a pinned window gains the
    ///    foreground, and re-adds it when the pinned window loses the foreground.
    ///
    /// Behavior rules:
    /// - Inactive pinned window: single click passes through to the window
    ///   below; double-click activates the pinned window.
    /// - Active pinned window: all mouse actions apply to the pinned window
    ///   normally.
    /// </summary>
    public class WindowHookManager : IDisposable
    {
        // ── Constants ─────────────────────────────────────────

        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        /// <summary>Maximum interval between two clicks to count as a double-click (milliseconds).</summary>
        private const int DOUBLE_CLICK_INTERVAL = 400;
        /// <summary>Maximum pixel distance between two clicks to count as a double-click.</summary>
        private const int DOUBLE_CLICK_TOLERANCE = 8;

        // ── Delegates and structures ──────────────────────────

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        private delegate IntPtr WinEventProcType(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public NativeMethods.POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // ── P/Invoke ──────────────────────────────────────────

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProcType lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetForegroundWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        private const int SW_RESTORE = 9;

        // ── Fields ────────────────────────────────────────────

        private readonly WindowPinManager _pinManager;
        private IntPtr _mouseHookID = IntPtr.Zero;
        private IntPtr _winEventHook = IntPtr.Zero;
        private LowLevelMouseProc? _mouseProc;
        private WinEventProcType? _winEventProc;
        private bool _disposed = false;

        // Double-click detection state
        private DateTime _lastClickTime = DateTime.MinValue;
        private Point _lastClickPos = Point.Empty;
        private IntPtr _lastClickTargetWindow = IntPtr.Zero;

        /// <summary>Handle of the window currently being activated, used to prevent the foreground hook from re-adding the click-through style prematurely.</summary>
        private IntPtr _activatingWindow = IntPtr.Zero;

        /// <summary>
        /// Activation retry counter. When the delayed refresh detects that the
        /// pinned window still has not gained the foreground, it retries
        /// activation up to <see cref="MAX_ACTIVATION_RETRIES"/> times.
        /// </summary>
        private int _activationRetryCount;
        private const int MAX_ACTIVATION_RETRIES = 8;

        /// <summary>Delayed refresh timer — ensures the click-through state is updated correctly after a foreground change takes effect.</summary>
        private System.Windows.Forms.Timer? _delayedRefreshTimer;

        // ── Construction and initialization ──────────────────

        public WindowHookManager(WindowPinManager pinManager)
        {
            _pinManager = pinManager;
            InstallHooks();
            _pinManager.WindowPinned += OnWindowPinned;
            _pinManager.WindowUnpinned += OnWindowUnpinned;
            _pinManager.WindowsChanged += OnWindowsChanged;
        }

        private void InstallHooks()
        {
            // Install the low-level mouse hook.
            _mouseProc = MouseHookCallback;
            using (var process = Process.GetCurrentProcess())
            using (var module = process.MainModule)
            {
                _mouseHookID = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(module?.ModuleName), 0);
            }
            if (_mouseHookID == IntPtr.Zero)
            {
                Debug.WriteLine("Failed to install low-level mouse hook");
                AppLogger.Error(
                    "Failed to install low-level mouse hook (WH_MOUSE_LL)",
                    PpcErrorCodes.ErrorHookInstallFailed);
            }

            // Install the foreground-window change event hook.
            _winEventProc = ForegroundChangedCallback;
            _winEventHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND,
                EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _winEventProc,
                0, 0,
                WINEVENT_OUTOFCONTEXT
            );
            if (_winEventHook == IntPtr.Zero)
            {
                Debug.WriteLine("Failed to install foreground-window change event hook");
                AppLogger.Error(
                    "Failed to install foreground-window event hook (EVENT_SYSTEM_FOREGROUND)",
                    PpcErrorCodes.ErrorHookInstallFailed);
            }

            // Initialize the click-through state of all currently pinned windows.
            UpdateAllPinnedWindowsClickThrough();
        }

        // ── Event callbacks ───────────────────────────────────

        /// <summary>
        /// Low-level mouse hook callback — detects double-click gestures to
        /// activate an inactive pinned window.
        /// </summary>
        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var message = wParam.ToInt32();
                var mousePos = new Point(hookStruct.pt.X, hookStruct.pt.Y);

                if (message == WM_LBUTTONDOWN)
                {
                    // When a double-click activates a pinned window, suppress
                    // this (second) click so it is NOT delivered to the window
                    // beneath. Without suppression, the underlying application
                    // receives a double-click, performs its default action, and
                    // steals the foreground — undoing our activation and
                    // re-transparenting the pinned window.
                    if (HandleLeftButtonDown(mousePos))
                    {
                        // Returning a non-zero value without calling
                        // CallNextHookEx swallows the mouse message.
                        return (IntPtr)1;
                    }
                }
            }

            // Always pass through — never block other mouse messages.
            return CallNextHookEx(_mouseHookID, nCode, wParam, lParam);
        }

        /// <summary>
        /// Foreground-window change callback — updates the WS_EX_TRANSPARENT
        /// state of all pinned windows.
        /// </summary>
        private IntPtr ForegroundChangedCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (idObject == 0 && idChild == 0)
            {
                // Foreground window changed — refresh the click-through state of all pinned windows.
                UpdateAllPinnedWindowsClickThrough();
            }
            return IntPtr.Zero;
        }

        // ── Double-click detection logic ─────────────────────

        /// <summary>
        /// Handles a left-button-down event and detects a double-click on an
        /// inactive pinned window.
        /// </summary>
        /// <returns>
        /// <c>true</c> when this click completed a double-click on a pinned
        /// window and triggered activation; the caller should swallow this
        /// mouse message to prevent it from reaching the window below.
        /// Otherwise <c>false</c>, and the message is delivered normally.
        /// </returns>
        private bool HandleLeftButtonDown(Point mousePos)
        {
            var now = DateTime.Now;

            // Find the inactive pinned window under the cursor.
            var targetWindow = FindPinnedWindowAtPoint(mousePos);
            if (targetWindow == IntPtr.Zero)
            {
                _lastClickTime = DateTime.MinValue;
                _lastClickTargetWindow = IntPtr.Zero;
                return false;
            }

            var foreground = NativeMethods.GetForegroundWindow();
            bool isTargetActive = targetWindow == foreground;

            // If the target window is already the active window, nothing to do.
            if (isTargetActive)
            {
                _lastClickTime = DateTime.MinValue;
                _lastClickTargetWindow = IntPtr.Zero;
                return false;
            }

            // Detect double-click: same window, close position, within the time interval.
            bool isDoubleClick = (now - _lastClickTime).TotalMilliseconds <= DOUBLE_CLICK_INTERVAL
                && _lastClickTargetWindow == targetWindow
                && Math.Abs(mousePos.X - _lastClickPos.X) <= DOUBLE_CLICK_TOLERANCE
                && Math.Abs(mousePos.Y - _lastClickPos.Y) <= DOUBLE_CLICK_TOLERANCE;

            if (isDoubleClick)
            {
                // Double-click — activate the pinned window.
                ActivatePinnedWindow(targetWindow);
                _lastClickTime = DateTime.MinValue;
                _lastClickTargetWindow = IntPtr.Zero;
                // Swallow this second click so the underlying application does
                // not receive a double-click and steal the foreground.
                return true;
            }
            else
            {
                // Record this click and wait for a possible second click.
                _lastClickTime = now;
                _lastClickPos = mousePos;
                _lastClickTargetWindow = targetWindow;
                return false;
            }
        }

        /// <summary>
        /// Finds the inactive pinned window under the given point (using
        /// GetWindowRect for manual hit-testing).
        /// </summary>
        private IntPtr FindPinnedWindowAtPoint(Point pt)
        {
            var foreground = NativeMethods.GetForegroundWindow();

            foreach (var window in _pinManager.PinnedWindows)
            {
                // Skip the window that is already in the foreground.
                if (window.Handle == foreground)
                    continue;

                if (!NativeMethods.IsWindow(window.Handle))
                    continue;

                if (NativeMethods.GetWindowRect(window.Handle, out var rect))
                {
                    if (pt.X >= rect.Left && pt.X <= rect.Right &&
                        pt.Y >= rect.Top && pt.Y <= rect.Bottom)
                    {
                        return window.Handle;
                    }
                }
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Activates a pinned window: removes the click-through style and sets
        /// it as the foreground window.
        ///
        /// Key fix: SetForegroundWindow is asynchronous — GetForegroundWindow
        /// may still return the old window right after the call, so
        /// UpdateAllPinnedWindowsClickThrough must NOT be invoked immediately
        /// (it would re-add the click-through style).
        /// Solution: force the foreground switch with AttachThreadInput, then
        /// refresh the click-through state via a delayed timer.
        /// </summary>
        private void ActivatePinnedWindow(IntPtr hWnd)
        {
            try
            {
                // Mark this window as being activated so the foreground hook
                // does not re-add the click-through style prematurely.
                _activatingWindow = hWnd;

                // Remove the click-through style first so the window can receive mouse events.
                SetWindowTransparent(hWnd, false);

                // Force the foreground switch using AttachThreadInput.
                // This bypasses the SetForegroundWindow restriction (background
                // processes normally cannot set the foreground window).
                ForceSetForegroundWindow(hWnd);

                Debug.WriteLine($"Activated pinned window via double-click: {hWnd}");

                // Do NOT call UpdateAllPinnedWindowsClickThrough immediately.
                // Use a delayed timer to wait for the foreground change to take
                // effect before refreshing.
                ScheduleDelayedRefresh(150);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to activate pinned window: {ex.Message}");
                _activatingWindow = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Forces a window to the foreground using AttachThreadInput.
        ///
        /// Improvements:
        /// - Call <see cref="ShowWindow"/> first to restore minimized windows;
        ///   otherwise SetForegroundWindow has no effect on them.
        /// - Attach to the input queues of BOTH the current foreground thread
        ///   and the target window's thread before calling
        ///   SetForegroundWindow, bypassing the foreground-lock restriction.
        /// - Call <see cref="BringWindowToTop"/> to ensure top Z-order.
        /// - Detach all AttachThreadInput links in finally so input queues are
        ///   not shared long-term.
        /// </summary>
        private void ForceSetForegroundWindow(IntPtr hWnd)
        {
            try
            {
                // Restore minimized/maximized windows so they can be activated.
                NativeMethods.ShowWindow(hWnd, SW_RESTORE);

                var currentThread = GetCurrentThreadId();
                var foregroundWnd = NativeMethods.GetForegroundWindow();

                uint foregroundThread = 0;
                if (foregroundWnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(foregroundWnd, out foregroundThread);
                }

                uint targetThread = 0;
                GetWindowThreadProcessId(hWnd, out targetThread);

                // Attach to both the foreground thread's and the target thread's input queues.
                bool attachedToForeground = foregroundThread != 0
                    && foregroundThread != currentThread
                    && foregroundThread != targetThread;
                bool attachedToTarget = targetThread != 0
                    && targetThread != currentThread;
                if (attachedToForeground)
                    AttachThreadInput(currentThread, foregroundThread, true);
                if (attachedToTarget)
                    AttachThreadInput(currentThread, targetThread, true);

                try
                {
                    // Explicitly grant this process permission to change the
                    // foreground and set the target as active/focused while
                    // the input queues are attached. This closes the race in
                    // which SetForegroundWindow succeeds but the target is
                    // not yet the active window when the queues detach.
                    NativeMethods.AllowSetForegroundWindow(0xFFFFFFFF);
                    BringWindowToTop(hWnd);
                    NativeMethods.SetActiveWindow(hWnd);
                    NativeMethods.SetFocus(hWnd);
                    NativeMethods.SetForegroundWindow(hWnd);
                    NativeMethods.SetForegroundWindow(hWnd);
                }
                finally
                {
                    if (attachedToForeground)
                        AttachThreadInput(currentThread, foregroundThread, false);
                    if (attachedToTarget)
                        AttachThreadInput(currentThread, targetThread, false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ForceSetForegroundWindow failed: {ex.Message}");
                // Fall back to a plain SetForegroundWindow.
                NativeMethods.SetForegroundWindow(hWnd);
            }
        }

        /// <summary>
        /// Schedules a delayed refresh of the click-through state (executed
        /// after the foreground change takes effect).
        ///
        /// Improvement: once the delay elapses, first verify the pinned window
        /// actually gained the foreground. If it did not (typical case: the
        /// second double-click was stolen by the underlying application), retry
        /// activation up to <see cref="MAX_ACTIVATION_RETRIES"/> times. Only
        /// clear <see cref="_activatingWindow"/> and refresh the click-through
        /// state once the foreground has actually switched or the retries are
        /// exhausted — otherwise WS_EX_TRANSPARENT is re-added too early and
        /// the pinned window becomes "unable to be activated".
        /// </summary>
        private void ScheduleDelayedRefresh(int delayMs)
        {
            _delayedRefreshTimer?.Stop();
            _delayedRefreshTimer?.Dispose();
            _activationRetryCount = 0;
            _delayedRefreshTimer = new System.Windows.Forms.Timer { Interval = delayMs };
            _delayedRefreshTimer.Tick += OnDelayedRefreshTick;
            _delayedRefreshTimer.Start();
        }

        private void OnDelayedRefreshTick(object? sender, EventArgs e)
        {
            _delayedRefreshTimer?.Stop();

            var foreground = NativeMethods.GetForegroundWindow();
            if (_activatingWindow != IntPtr.Zero
                && foreground != _activatingWindow
                && _activationRetryCount < MAX_ACTIVATION_RETRIES)
            {
                // Foreground switch did not take effect (possibly stolen by
                // another application) — retry the activation.
                _activationRetryCount++;
                Debug.WriteLine($"Pinned-window activation did not take effect, retrying ({_activationRetryCount}/{MAX_ACTIVATION_RETRIES})");
                ForceSetForegroundWindow(_activatingWindow);
                _delayedRefreshTimer!.Interval = 100;
                _delayedRefreshTimer.Start();
                return;
            }

            _activatingWindow = IntPtr.Zero;
            _activationRetryCount = 0;
            UpdateAllPinnedWindowsClickThrough();
        }

        // ── WS_EX_TRANSPARENT dynamic management ─────────────

        /// <summary>
        /// Updates the click-through state of all pinned windows:
        /// - Active (foreground) pinned window → remove WS_EX_TRANSPARENT (normal interaction)
        /// - Inactive pinned window → add WS_EX_TRANSPARENT (single-click pass-through)
        /// - Window currently being activated → keep click-through off (wait for the foreground change to take effect)
        /// </summary>
        private void UpdateAllPinnedWindowsClickThrough()
        {
            var foreground = NativeMethods.GetForegroundWindow();

            foreach (var window in _pinManager.PinnedWindows)
            {
                if (!NativeMethods.IsWindow(window.Handle))
                    continue;

                // A window being activated keeps the click-through style off.
                if (window.Handle == _activatingWindow)
                {
                    SetWindowTransparent(window.Handle, false);
                    continue;
                }

                bool shouldBeTransparent = window.Handle != foreground;
                SetWindowTransparent(window.Handle, shouldBeTransparent);
            }
        }

        /// <summary>
        /// Adds or removes the WS_EX_TRANSPARENT style on a window.
        /// </summary>
        private void SetWindowTransparent(IntPtr hWnd, bool transparent)
        {
            try
            {
                var exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
                var hasTransparent = (exStyle & NativeMethods.WS_EX_TRANSPARENT) != 0;

                if (transparent && !hasTransparent)
                {
                    exStyle |= NativeMethods.WS_EX_TRANSPARENT;
                    NativeMethods.SetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE, exStyle);
                }
                else if (!transparent && hasTransparent)
                {
                    exStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
                    NativeMethods.SetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE, exStyle);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set window click-through style: {ex.Message}");
            }
        }

        // ── Pinned-window event handling ─────────────────────

        private void OnWindowPinned(object? sender, WindowPinEventArgs e)
        {
            // When a new window is pinned, set its click-through state based on the current foreground.
            var foreground = NativeMethods.GetForegroundWindow();
            bool shouldBeTransparent = e.Window.Handle != foreground;
            SetWindowTransparent(e.Window.Handle, shouldBeTransparent);

            // Ensure any other pinned windows that are no longer in the foreground are also click-through.
            UpdateAllPinnedWindowsClickThrough();
        }

        private void OnWindowUnpinned(object? sender, WindowPinEventArgs e)
        {
            // When a window is unpinned, remove the click-through style to restore normal behavior.
            SetWindowTransparent(e.Window.Handle, false);
        }

        private void OnWindowsChanged(object? sender, EventArgs e)
        {
            UpdateAllPinnedWindowsClickThrough();
        }

        // ── Public methods ────────────────────────────────────

        /// <summary>
        /// Manually refreshes the click-through state of all pinned windows.
        /// </summary>
        public void RefreshClickThroughState()
        {
            UpdateAllPinnedWindowsClickThrough();
        }

        // ── Resource cleanup ──────────────────────────────────

        public void Dispose()
        {
            if (!_disposed)
            {
                // Remove the click-through style from all pinned windows.
                foreach (var window in _pinManager.PinnedWindows)
                {
                    if (NativeMethods.IsWindow(window.Handle))
                    {
                        SetWindowTransparent(window.Handle, false);
                    }
                }

                _delayedRefreshTimer?.Stop();
                _delayedRefreshTimer?.Dispose();

                if (_mouseHookID != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_mouseHookID);
                    _mouseHookID = IntPtr.Zero;
                }

                if (_winEventHook != IntPtr.Zero)
                {
                    UnhookWinEvent(_winEventHook);
                    _winEventHook = IntPtr.Zero;
                }

                _pinManager.WindowPinned -= OnWindowPinned;
                _pinManager.WindowUnpinned -= OnWindowUnpinned;
                _pinManager.WindowsChanged -= OnWindowsChanged;

                _disposed = true;
            }
        }
    }
}
