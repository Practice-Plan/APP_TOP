using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WindowTopTool
{
    /// <summary>
    /// Enhanced edge auto-hide manager with drag detection and PiP mode
    /// Trigger conditions: dragging right + 2/3 window at edge
    /// </summary>
    public class EdgeAutoHideManager : IDisposable
    {
        private readonly WindowPinManager _pinManager;
        private readonly System.Windows.Forms.Timer _checkTimer;
        private readonly Dictionary<IntPtr, PiPWindow> _pipWindows = new();
        private readonly Dictionary<IntPtr, HiddenWindowState> _hiddenWindows = new();
        private readonly Dictionary<IntPtr, Point> _lastPositions = new();
        private readonly Dictionary<IntPtr, DateTime> _lastPositionTime = new();
        private bool _disposed = false;
        private Icon? _defaultIcon;

        public EdgeAutoHideManager(WindowPinManager pinManager)
        {
            _pinManager = pinManager;
            _checkTimer = new System.Windows.Forms.Timer { Interval = 50 }; // Check every 50ms for smoother detection
            _checkTimer.Tick += OnCheckTimer;

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
            _checkTimer.Start();
        }

        public void Stop()
        {
            _checkTimer.Stop();
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

        private void OnCheckTimer(object? sender, EventArgs e)
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
                    var screen = Screen.FromHandle(hWnd);
                    if (screen == null)
                        return;

                    var workingArea = screen.WorkingArea;
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
                var pipWindow = new PiPWindow(hWnd, windowInfo.Title, windowIcon ?? _defaultIcon, originalSize, originalLocation);
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
                _checkTimer.Stop();
                _checkTimer.Dispose();

                foreach (var pipWindow in _pipWindows.Values)
                {
                    pipWindow.Close();
                    pipWindow.Dispose();
                }
                _pipWindows.Clear();

                _defaultIcon?.Dispose();

                _disposed = true;
            }
        }

        private class HiddenWindowState
        {
            public bool IsHidden { get; set; }
            public Rectangle OriginalPosition { get; set; }
        }
    }
}