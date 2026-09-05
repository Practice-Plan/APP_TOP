using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowTopTool
{
    /// <summary>
    /// Manages window pinning/unpinning functionality
    /// </summary>
    public class WindowPinManager : IDisposable
    {
        private readonly HashSet<IntPtr> _pinnedWindows = new HashSet<IntPtr>();
        private readonly Dictionary<IntPtr, WindowInfo> _windowInfoDict = new Dictionary<IntPtr, WindowInfo>();
        private readonly StateManager _stateManager = new StateManager();
        private bool _disposed = false;

        public event EventHandler<WindowPinEventArgs>? WindowPinned;
        public event EventHandler<WindowPinEventArgs>? WindowUnpinned;
        public event EventHandler? WindowsChanged;

        public IReadOnlyCollection<WindowInfo> PinnedWindows => _windowInfoDict.Values;

        public int PinnedCount => _pinnedWindows.Count;

        public WindowPinManager()
        {
            // WindowStateProtector is created and managed by MainForm to avoid duplicate hooks
        }

        /// <summary>
        /// Pin a window to top
        /// </summary>
        public bool PinWindow(IntPtr hWnd)
        {
            // Validate window
            if (!WindowValidator.IsValidWindow(hWnd))
            {
                Debug.WriteLine($"Invalid window for pinning: {hWnd}");
                AppLogger.Warn(
                    $"Cannot pin window {hWnd}: invalid or already closed",
                    PpcErrorCodes.ErrorWindowNotFound);
                return false;
            }

            // Check application filter
            var config = AppConfig.Instance;
            if (config.AllowedApplications.Count > 0)
            {
                var processName = GetProcessNameFromHandle(hWnd);
                if (string.IsNullOrEmpty(processName) || !config.AllowedApplications.Contains(processName))
                {
                    Debug.WriteLine($"Application not in allowed list: {processName}");
                    return false;
                }
            }

            if (_pinnedWindows.Contains(hWnd))
                return false;

            try
            {
                // Handle fullscreen/minimized windows
                if (WindowValidator.IsWindowMinimized(hWnd) || WindowValidator.IsWindowFullscreen(hWnd))
                {
                    WindowValidator.RestoreWindow(hWnd);
                    System.Threading.Thread.Sleep(100); // Wait for restore
                }

                // Set window to topmost
                var result = NativeMethods.SetWindowPos(
                    hWnd,
                    HWND.TOPMOST,
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | 
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW
                );

                if (!result)
                {
                    var error = Marshal.GetLastWin32Error();
                    Debug.WriteLine($"SetWindowPos failed for handle {hWnd}. Error code: {error}");

                    // Provide specific guidance based on error code and record
                    // the corresponding PPC error code for diagnostics.
                    if (error == 5) // ERROR_ACCESS_DENIED
                    {
                        Debug.WriteLine("Access denied: the target window may belong to a higher-privilege process. Run this tool as administrator.");
                        AppLogger.Error(
                            $"SetWindowPos denied for handle {hWnd} (Win32 error 5: access denied)",
                            PpcErrorCodes.ErrorWindowPinFailed);
                    }
                    else if (error == 1400) // ERROR_INVALID_WINDOW_HANDLE
                    {
                        Debug.WriteLine("Invalid window handle: the target window may have been closed.");
                        AppLogger.Warn(
                            $"SetWindowPos failed for handle {hWnd} (Win32 error 1400: invalid handle)",
                            PpcErrorCodes.ErrorWindowNotFound);
                    }
                    else
                    {
                        AppLogger.Error(
                            $"SetWindowPos failed for handle {hWnd} (Win32 error {error})",
                            PpcErrorCodes.ErrorWindowPinFailed);
                    }

                    return false;
                }

                // Get window information
                var windowInfo = CreateWindowInfo(hWnd);
                _pinnedWindows.Add(hWnd);
                _windowInfoDict[hWnd] = windowInfo;

                // Set default opacity
                SetWindowOpacity(hWnd, AppConfig.Instance.DefaultOpacity);

                // Raise events (only once)
                WindowPinned?.Invoke(this, new WindowPinEventArgs(windowInfo));
                WindowsChanged?.Invoke(this, EventArgs.Empty);

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to pin window: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Toggle pin state for a window
        /// </summary>
        public void TogglePin(IntPtr hWnd)
        {
            if (IsPinned(hWnd))
            {
                UnpinWindow(hWnd);
            }
            else
            {
                PinWindow(hWnd);
            }
        }

        /// <summary>
        /// Toggle only the topmost flag without full pin management (no opacity/click-through tracking)
        /// </summary>
        public void ToggleTopmostOnly(IntPtr hWnd)
        {
            if (!WindowValidator.IsValidWindow(hWnd))
                return;

            try
            {
                var exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
                bool isTopmost = (exStyle & NativeMethods.WS_EX_TOPMOST) != 0;

                var result = NativeMethods.SetWindowPos(
                    hWnd,
                    isTopmost ? HWND.NOTOPMOST : HWND.TOPMOST,
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE |
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW
                );

                if (!result)
                {
                    var error = Marshal.GetLastWin32Error();
                    Debug.WriteLine($"ToggleTopmostOnly failed for handle {hWnd}. Error code: {error}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to toggle topmost: {ex.Message}");
            }
        }

        private string GetProcessNameFromHandle(IntPtr hWnd)
        {
            try
            {
                NativeMethods.GetWindowThreadProcessId(hWnd, out var processId);
                var process = Process.GetProcessById((int)processId);
                return process.ProcessName;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Unpin a window from top
        /// </summary>
        public bool UnpinWindow(IntPtr hWnd)
        {
            if (!_pinnedWindows.Contains(hWnd))
                return false;

            try
            {
                // Remove topmost style
                var result = NativeMethods.SetWindowPos(
                    hWnd,
                    HWND.NOTOPMOST,
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | 
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW
                );

                if (!result)
                {
                    var error = Marshal.GetLastWin32Error();
                    Debug.WriteLine($"SetWindowPos (unpin) failed for handle {hWnd}. Error code: {error}");
                }

                // Restore opacity to 100%
                SetWindowOpacity(hWnd, 255, true);

                // Remove transparent style
                RemoveClickThrough(hWnd);

                var windowInfo = _windowInfoDict[hWnd];
                _pinnedWindows.Remove(hWnd);
                _windowInfoDict.Remove(hWnd);

                WindowUnpinned?.Invoke(this, new WindowPinEventArgs(windowInfo));
                WindowsChanged?.Invoke(this, EventArgs.Empty);

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to unpin window: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Unpin all windows
        /// </summary>
        public void UnpinAll()
        {
            var handles = new List<IntPtr>(_pinnedWindows);
            foreach (var hWnd in handles)
            {
                UnpinWindow(hWnd);
            }
        }

        /// <summary>
        /// Check if window is pinned
        /// </summary>
        public bool IsPinned(IntPtr hWnd)
        {
            return _pinnedWindows.Contains(hWnd);
        }

        /// <summary>
        /// Set window opacity (0-255)
        /// </summary>
        public void SetWindowOpacity(IntPtr hWnd, byte opacity, bool forceRemove = false)
        {
            if (!_pinnedWindows.Contains(hWnd) && !forceRemove)
                return;

            try
            {
                // Add layered style
                var exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
                exStyle |= NativeMethods.WS_EX_LAYERED;
                NativeMethods.SetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE, exStyle);

                // Set opacity
                NativeMethods.SetLayeredWindowAttributes(hWnd, 0, opacity, 2); // LWA_ALPHA = 2

                if (_windowInfoDict.ContainsKey(hWnd))
                {
                    _windowInfoDict[hWnd].Opacity = opacity;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set opacity: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply the current global window settings to every pinned window.
        /// This keeps existing windows in sync when settings are saved.
        /// </summary>
        public void ApplyGlobalSettings()
        {
            var opacity = AppConfig.Instance.DefaultOpacity;
            foreach (var hWnd in new List<IntPtr>(_pinnedWindows))
            {
                if (NativeMethods.IsWindow(hWnd))
                    SetWindowOpacity(hWnd, opacity);
            }
        }

        /// <summary>
        /// Enable/disable click-through mode
        /// </summary>
        public void SetClickThrough(IntPtr hWnd, bool enable)
        {
            if (!_pinnedWindows.Contains(hWnd))
                return;

            try
            {
                var exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);

                if (enable)
                {
                    exStyle |= NativeMethods.WS_EX_TRANSPARENT;
                }
                else
                {
                    exStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
                }

                NativeMethods.SetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE, exStyle);

                if (_windowInfoDict.ContainsKey(hWnd))
                {
                    _windowInfoDict[hWnd].IsClickThrough = enable;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set click-through: {ex.Message}");
            }
        }

        /// <summary>
        /// Remove click-through style
        /// </summary>
        private void RemoveClickThrough(IntPtr hWnd)
        {
            try
            {
                var exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
                exStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
                NativeMethods.SetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE, exStyle);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to remove click-through: {ex.Message}");
            }
        }

        /// <summary>
        /// Toggle click-through mode
        /// </summary>
        public void ToggleClickThrough(IntPtr hWnd)
        {
            if (_windowInfoDict.TryGetValue(hWnd, out var info))
            {
                SetClickThrough(hWnd, !info.IsClickThrough);
            }
        }

        /// <summary>
        /// Clean up invalid window handles
        /// </summary>
        public void CleanupInvalidWindows()
        {
            var invalidHandles = new List<IntPtr>();

            foreach (var hWnd in _pinnedWindows)
            {
                if (!NativeMethods.IsWindow(hWnd))
                {
                    invalidHandles.Add(hWnd);
                }
            }

            foreach (var hWnd in invalidHandles)
            {
                _pinnedWindows.Remove(hWnd);
                _windowInfoDict.Remove(hWnd);
            }

            if (invalidHandles.Count > 0)
            {
                WindowsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Save current state to file
        /// </summary>
        public void SaveState()
        {
            _stateManager.SaveState(_windowInfoDict.Values);
        }

        /// <summary>
        /// Restore state from file
        /// </summary>
        public void RestoreState()
        {
            var savedWindows = _stateManager.LoadState();

            foreach (var savedWindow in savedWindows)
            {
                // Try to find window by title
                var hWnd = NativeMethods.FindWindow(null, savedWindow.Title);
                if (hWnd != IntPtr.Zero && NativeMethods.IsWindow(hWnd))
                {
                    PinWindow(hWnd);

                    // Restore opacity
                    SetWindowOpacity(hWnd, savedWindow.Opacity);

                    // Restore click-through
                    if (savedWindow.IsClickThrough)
                    {
                        SetClickThrough(hWnd, true);
                    }
                }
            }
        }

        /// <summary>
        /// Create WindowInfo from window handle
        /// </summary>
        private WindowInfo CreateWindowInfo(IntPtr hWnd)
        {
            var title = GetWindowTitle(hWnd);
            var processName = GetProcessName(hWnd);

            // Get original position
            NativeMethods.GetWindowRect(hWnd, out var rect);

            return new WindowInfo(hWnd, title, processName)
            {
                Opacity = AppConfig.Instance.DefaultOpacity,
                OriginalLeft = rect.Left,
                OriginalTop = rect.Top,
                OriginalWidth = rect.Right - rect.Left,
                OriginalHeight = rect.Bottom - rect.Top
            };
        }

        /// <summary>
        /// Get window title
        /// </summary>
        private string GetWindowTitle(IntPtr hWnd)
        {
            var length = NativeMethods.GetWindowTextLength(hWnd);
            if (length == 0)
                return string.Empty;

            var builder = new StringBuilder(length + 1);
            NativeMethods.GetWindowText(hWnd, builder, builder.Capacity);
            return builder.ToString();
        }

        /// <summary>
        /// Get process name from window handle
        /// </summary>
        private string GetProcessName(IntPtr hWnd)
        {
            try
            {
                NativeMethods.GetWindowThreadProcessId(hWnd, out var processId);
                var process = Process.GetProcessById((int)processId);
                return process.ProcessName;
            }
            catch
            {
                return "Unknown";
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                SaveState();
                _disposed = true;
            }
        }
    }

    public class WindowPinEventArgs : EventArgs
    {
        public WindowInfo Window { get; }

        public WindowPinEventArgs(WindowInfo window)
        {
            Window = window;
        }
    }
}