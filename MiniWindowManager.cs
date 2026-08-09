using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace WindowTopTool
{
    /// <summary>
    /// Manages mini window (picture-in-picture) functionality
    /// </summary>
    public class MiniWindowManager
    {
        private readonly WindowPinManager _pinManager;
        private readonly Dictionary<IntPtr, WindowInfo> _miniWindows = new Dictionary<IntPtr, WindowInfo>();

        public MiniWindowManager(WindowPinManager pinManager)
        {
            _pinManager = pinManager;
        }

        /// <summary>
        /// Toggle mini mode for the current foreground window
        /// </summary>
        public void ToggleMiniWindow()
        {
            var hWnd = NativeMethods.GetForegroundWindow();
            if (hWnd != IntPtr.Zero && NativeMethods.IsWindow(hWnd))
            {
                ToggleMiniMode(hWnd);
            }
        }

        /// <summary>
        /// Toggle mini mode for a window
        /// </summary>
        public void ToggleMiniMode(IntPtr hWnd)
        {
            if (!_pinManager.IsPinned(hWnd))
                return;

            if (_miniWindows.ContainsKey(hWnd))
            {
                RestoreFromMiniMode(hWnd);
            }
            else
            {
                SwitchToMiniMode(hWnd);
            }
        }

        /// <summary>
        /// Switch window to mini mode
        /// </summary>
        private void SwitchToMiniMode(IntPtr hWnd)
        {
            if (!NativeMethods.IsWindow(hWnd))
                return;

            try
            {
                // Get current position
                NativeMethods.GetWindowRect(hWnd, out var rect);

                // Get window info
                var windowInfo = GetWindowInfo(hWnd);
                if (windowInfo == null)
                    return;

                // Save original position
                windowInfo.OriginalLeft = rect.Left;
                windowInfo.OriginalTop = rect.Top;
                windowInfo.OriginalWidth = rect.Right - rect.Left;
                windowInfo.OriginalHeight = rect.Bottom - rect.Top;
                windowInfo.IsMiniMode = true;

                // Calculate mini position
                var config = AppConfig.Instance;
                var miniX = config.MiniWindowX;
                var miniY = config.MiniWindowY;
                var miniWidth = config.MiniWindowWidth;
                var miniHeight = config.MiniWindowHeight;

                // Move and resize window
                NativeMethods.MoveWindow(hWnd, miniX, miniY, miniWidth, miniHeight, true);

                _miniWindows[hWnd] = windowInfo;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to switch to mini mode: {ex.Message}");
            }
        }

        /// <summary>
        /// Restore window from mini mode
        /// </summary>
        private void RestoreFromMiniMode(IntPtr hWnd)
        {
            if (!_miniWindows.TryGetValue(hWnd, out var windowInfo))
                return;

            try
            {
                // Restore original position
                NativeMethods.MoveWindow(
                    hWnd,
                    windowInfo.OriginalLeft,
                    windowInfo.OriginalTop,
                    windowInfo.OriginalWidth,
                    windowInfo.OriginalHeight,
                    true
                );

                windowInfo.IsMiniMode = false;
                _miniWindows.Remove(hWnd);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to restore from mini mode: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if window is in mini mode
        /// </summary>
        public bool IsInMiniMode(IntPtr hWnd)
        {
            return _miniWindows.ContainsKey(hWnd);
        }

        /// <summary>
        /// Set mini window position for all windows
        /// </summary>
        public void SetMiniWindowPosition(int x, int y)
        {
            AppConfig.Instance.MiniWindowX = x;
            AppConfig.Instance.MiniWindowY = y;
            AppConfig.Instance.Save();
        }

        /// <summary>
        /// Set mini window size for all windows
        /// </summary>
        public void SetMiniWindowSize(int width, int height)
        {
            AppConfig.Instance.MiniWindowWidth = width;
            AppConfig.Instance.MiniWindowHeight = height;
            AppConfig.Instance.Save();
        }

        /// <summary>
        /// Get window info from pin manager
        /// </summary>
        private WindowInfo? GetWindowInfo(IntPtr hWnd)
        {
            foreach (var info in _pinManager.PinnedWindows)
            {
                if (info.Handle == hWnd)
                    return info;
            }
            return null;
        }
    }
}