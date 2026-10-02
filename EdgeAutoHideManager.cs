using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;

namespace WindowTopTool
{
    /// <summary>
    /// Enhanced edge auto-hide manager with drag detection and PiP mode
    /// Trigger conditions: dragging right + 2/3 window at edge
    /// </summary>
    public class EdgeAutoHideManager : IDisposable
    {
        private readonly WindowPinManager _pinManager;
        private readonly Timer _checkTimer;
        private readonly Dictionary<IntPtr, PiPWindow> _pipWindows = new();
        private readonly Dictionary<IntPtr, HiddenWindowState> _hiddenWindows = new();
        private readonly Dictionary<IntPtr, Point> _lastPositions = new();
        private readonly Dictionary<IntPtr, DateTime> _lastPositionTime = new();
        private readonly object _lock = new();
        private bool _disposed = false;
        private Icon? _defaultIcon;

        /// <summary>Size for new PiP windows (set from AppConfig).</summary>
        public Size PiPSize { get; set; } = new Size(240, 180);

        /// <summary>
        /// Refresh PiPSize from AppConfig. Called when the user saves settings.
        /// Existing PiP windows keep their current size — this only affects
        /// newly created ones.
        /// </summary>
        public void UpdatePiPSize()
        {
            var config = AppConfig.Instance;
            PiPSize = new Size(config.PiPWidth, config.PiPHeight);
        }

        public EdgeAutoHideManager(WindowPinManager pinManager)
        {
            _pinManager = pinManager;
            // 使用 System.Threading.Timer（后台线程池定时器），间隔 50ms 检测
            _checkTimer = new Timer(OnCheckTimerCallback, null, Timeout.Infinite, 50);

            _pinManager.WindowPinned += OnWindowPinned;
            _pinManager.WindowUnpinned += OnWindowUnpinned;

            LoadDefaultIcon();
        }

        private void LoadDefaultIcon()
        {
            try
            {
                var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    _defaultIcon = new Icon(iconPath);
                }
            }
            catch
            {
                // Ignore icon loading errors
            }
        }

        public void Start()
        {
            _checkTimer.Change(0, 50);
        }

        public void Stop()
        {
            _checkTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Toggle PiP mode for the current foreground window
        /// </summary>
        public void TogglePiPForActiveWindow()
        {
            var hWnd = NativeMethods.GetForegroundWindow();
            if (hWnd == IntPtr.Zero || !NativeMethods.IsWindow(hWnd))
                return;

            // Check if window is already in PiP mode
            if (_pipWindows.TryGetValue(hWnd, out var existingPip))
            {
                // Restore from PiP
                existingPip.RestoreWindowFromOutside();
                OnWindowRestored(this, new WindowRestoredEventArgs(hWnd));
            }
            else if (_pinManager.IsPinned(hWnd))
            {
                // Only allow PiP for pinned windows
                if (_hiddenWindows.TryGetValue(hWnd, out var state) && !state.IsHidden)
                {
                    HideWindowToPiP(hWnd);
                }
                else
                {
                    // Initialize tracking then hide
                    if (!_hiddenWindows.ContainsKey(hWnd))
                    {
                        NativeMethods.GetWindowRect(hWnd, out var rect);
                        _hiddenWindows[hWnd] = new HiddenWindowState
                        {
                            IsHidden = false,
                            OriginalPosition = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
                        };
                    }
                    HideWindowToPiP(hWnd);
                }
            }
        }

        private void OnWindowPinned(object? sender, WindowPinEventArgs e)
        {
            // Initialize tracking for pinned window
            if (!_hiddenWindows.ContainsKey(e.Window.Handle))
            {
                _hiddenWindows[e.Window.Handle] = new HiddenWindowState
                {
                    IsHidden = false,
                    OriginalPosition = new Rectangle(
                        e.Window.OriginalLeft,
                        e.Window.OriginalTop,
                        e.Window.OriginalWidth,
                        e.Window.OriginalHeight
                    )
                };
            }
        }

        private void OnWindowUnpinned(object? sender, WindowPinEventArgs e)
        {
            // Remove PiP window if exists
            if (_pipWindows.TryGetValue(e.Window.Handle, out var pipWindow))
            {
                pipWindow.Close();
                pipWindow.Dispose();
                _pipWindows.Remove(e.Window.Handle);
            }

            // Remove state
            _hiddenWindows.Remove(e.Window.Handle);
            _lastPositions.Remove(e.Window.Handle);
            _lastPositionTime.Remove(e.Window.Handle);
        }

        /// <summary>
        /// System.Threading.Timer 回调（在 ThreadPool 线程执行）。
        /// 使用锁保护共享集合的访问。
        /// </summary>
        private void OnCheckTimerCallback(object? state)
        {
            if (_disposed) return;

            try
            {
                lock (_lock)
                {
                    OnCheckTimerCore();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"EdgeAutoHideManager timer error: {ex.Message}");
            }
        }

        private void OnCheckTimerCore()
        {
            // Clean up invalid windows
            var invalidHandles = _hiddenWindows.Keys
                .Where(hWnd => !NativeMethods.IsWindow(hWnd))
                .ToList();

            foreach (var handle in invalidHandles)
            {
                _hiddenWindows.Remove(handle);
                _lastPositions.Remove(handle);
                _lastPositionTime.Remove(handle);

                if (_pipWindows.TryGetValue(handle, out var pipWindow))
                {
                    pipWindow.Close();
                    pipWindow.Dispose();
                    _pipWindows.Remove(handle);
                }
            }

            // Check each pinned window for drag and edge detection
            foreach (var window in _pinManager.PinnedWindows)
            {
                if (!_hiddenWindows.ContainsKey(window.Handle))
                    continue;

                CheckWindowDrag(window.Handle);
            }
        }

        private void CheckWindowDrag(IntPtr hWnd)
        {
            if (!NativeMethods.GetWindowRect(hWnd, out var rect))
                return;

            var currentPos = new Point(rect.Left, rect.Top);
            var currentTime = DateTime.Now;

            // Update position tracking
            if (_lastPositions.TryGetValue(hWnd, out var lastPos))
            {
                var deltaTime = (currentTime - _lastPositionTime[hWnd]).TotalMilliseconds;
                var deltaX = currentPos.X - lastPos.X;
                var deltaY = currentPos.Y - lastPos.Y;

                // Check if dragging right
                bool isDraggingRight = deltaX > 5 && deltaTime > 0 && deltaTime < 200;

                if (isDraggingRight)
                {
                    // Check if 2/3 of window is at right edge (use the screen the window is on)
                    // 使用 ScreenHelper（MonitorFromWindow + GetMonitorInfoW）替代 System.Windows.Forms.Screen.FromHandle
                    var workingArea = ScreenHelper.GetWorkingArea(hWnd);
                    if (workingArea == (0, 0, 0, 0))
                        return;

                    var windowWidth = rect.Right - rect.Left;

                    // Calculate how much of the window is past the right edge
                    var rightEdgePosition = workingArea.Right;
                    var windowRightEdge = rect.Right;

                    // Window must be moving right and 2/3 at edge
                    var offScreenPixels = windowRightEdge - rightEdgePosition;
                    var percentageAtEdge = (double)offScreenPixels / windowWidth;

                    if (percentageAtEdge >= 0.67 && !_hiddenWindows[hWnd].IsHidden)
                    {
                        HideWindowToPiP(hWnd);
                    }
                }
            }

            // Update last position
            _lastPositions[hWnd] = currentPos;
            _lastPositionTime[hWnd] = currentTime;
        }

        private void HideWindowToPiP(IntPtr hWnd)
        {
            if (!_hiddenWindows.TryGetValue(hWnd, out var state))
                return;

            try
            {
                // Get window info
                NativeMethods.GetWindowRect(hWnd, out var rect);
                var originalSize = new Size(rect.Right - rect.Left, rect.Bottom - rect.Top);
                var originalLocation = new Point(rect.Left, rect.Top);

                // Save original position
                state.OriginalPosition = new Rectangle(originalLocation, originalSize);

                // Get window info
                var windowInfo = GetWindowInfo(hWnd);
                if (windowInfo == null)
                    return;

                // Get window icon
                var windowIcon = GetWindowIcon(hWnd);

                // Create PiP window
                var pipWindow = new PiPWindow(hWnd, windowInfo.Title, windowIcon ?? _defaultIcon, originalSize, originalLocation, PiPSize);
                pipWindow.WindowRestored += OnWindowRestored;
                pipWindow.Show();

                // Hide the original window
                NativeMethods.ShowWindow(hWnd, 0); // SW_HIDE

                state.IsHidden = true;
                _pipWindows[hWnd] = pipWindow;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to hide window to PiP: {ex.Message}");
            }
        }

        private void OnWindowRestored(object? sender, WindowRestoredEventArgs e)
        {
            if (_hiddenWindows.TryGetValue(e.WindowHandle, out var state))
            {
                state.IsHidden = false;
            }

            // Remove PiP window
            if (_pipWindows.TryGetValue(e.WindowHandle, out var pipWindow))
            {
                _pipWindows.Remove(e.WindowHandle);
                pipWindow.Dispose();
            }
        }

        private WindowInfo? GetWindowInfo(IntPtr hWnd)
        {
            foreach (var info in _pinManager.PinnedWindows)
            {
                if (info.Handle == hWnd)
                    return info;
            }
            return null;
        }

        private Icon? GetWindowIcon(IntPtr hWnd)
        {
            try
            {
                var icon = Icon.ExtractAssociatedIcon(GetWindowProcessPath(hWnd));
                return icon;
            }
            catch
            {
                return null;
            }
        }

        private string GetWindowProcessPath(IntPtr hWnd)
        {
            try
            {
                NativeMethods.GetWindowThreadProcessId(hWnd, out var processId);
                var process = Process.GetProcessById((int)processId);
                return process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;

                _checkTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _checkTimer.Dispose();

                foreach (var pipWindow in _pipWindows.Values)
                {
                    pipWindow.Close();
                    pipWindow.Dispose();
                }
                _pipWindows.Clear();

                _defaultIcon?.Dispose();
            }
        }

        private class HiddenWindowState
        {
            public bool IsHidden { get; set; }
            public Rectangle OriginalPosition { get; set; }
        }
    }
}
