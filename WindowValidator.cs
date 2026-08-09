using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowTopTool
{
    /// <summary>
    /// Validates windows for pinning functionality
    /// Excludes input method windows, tray windows, and other non-normal windows
    /// </summary>
    public static class WindowValidator
    {
        private const int GWL_EXSTYLE = -20;
        private const int GWL_STYLE = -16;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WS_EX_NOACTIVATE = 0x08000000;
        private const uint WS_EX_APPWINDOW = 0x00040000;
        private const uint WS_EX_CONTROLPARENT = 0x00010000;
        private const uint WS_EX_TOPMOST = 0x00000008;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_CHILD = 0x40000000;
        private const uint WS_POPUP = 0x80000000;

        // Window classes to exclude
        private static readonly string[] ExcludedClasses = new[]
        {
            "IME",
            "MSIMEUI",
            "InputMethod",
            "tooltips_class32",
            "Shell_TrayWnd",
            "NotifyIconOverflowWindow",
            "ApplicationFrameWindow",
            "Windows.UI.Core.CoreWindow",
            "Progman",
            "ProgramManager",
            "WorkerW",
            "Shell_SecondaryTrayWnd",
            "DVControl",
            "Msg",
            "MultitaskingUIFrame",
            "TaskListOverlayWnd",
            "TaskListOverlayWnd_1.0",
            "EdgeUiInputTopWnd",
            "EdgeUiInputBottomWnd",
            "SearchApp",
            "SystemSettings",
            "Windows.Shell.Splash",
            "WindowTopTool",
            // Additional system windows
            "Button",
            "Static",
            "Edit",
            "ListBox",
            "ComboBox",
            "ScrollBar",
            "Dialog",
            "MSCTFIME UI",
            "MSIME Window",
            "SysTabControl32",
            "SysListView32",
            "SysTreeView32",
            "SysHeader32",
            "ToolbarWindow32",
            "ReBarWindow32",
            "ComboBoxEx32",
            "msctls_statusbar32",
            "msctls_progress32",
            "NetUIHWND",
            "NetUICheckForUpdateWorker",
            "Shell_Flyout",
            "Windows.UI.Xaml.Input.InputPaneWindow"
        };

        // Process names to exclude (except special windows like explorer)
        private static readonly string[] ExcludedProcesses = new[]
        {
            "ApplicationFrameHost",
            "SearchApp",
            "ShellExperienceHost",
            "Microsoft.NotesNinja",
            "WindowTopTool" // Self
        };

        /// <summary>
        /// Validate if a window is suitable for pinning (standard validation)
        /// </summary>
        public static bool IsValidWindow(IntPtr hWnd)
        {
            return IsValidWindow(hWnd, requireVisible: true);
        }

        /// <summary>
        /// Validate if a window is suitable for pinning with optional visibility check
        /// </summary>
        public static bool IsValidWindow(IntPtr hWnd, bool requireVisible)
        {
            if (hWnd == IntPtr.Zero)
                return false;

            try
            {
                // Check if window is valid
                if (!NativeMethods.IsWindow(hWnd))
                    return false;

                // Check if window is visible (skip when requireVisible is false)
                if (requireVisible && !NativeMethods.IsWindowVisible(hWnd))
                    return false;

                // Check window style
                var style = NativeMethods.GetWindowLong(hWnd, GWL_STYLE);
                var exStyle = NativeMethods.GetWindowLong(hWnd, GWL_EXSTYLE);

                // Must have visible style (skip when requireVisible is false)
                if (requireVisible && (style & WS_VISIBLE) == 0)
                    return false;

                // Exclude child windows
                if ((style & WS_CHILD) != 0)
                    return false;

                // Exclude tool windows (like tooltips, tray icons)
                if ((exStyle & WS_EX_TOOLWINDOW) != 0)
                    return false;

                // Exclude no-activate windows
                if ((exStyle & WS_EX_NOACTIVATE) != 0)
                    return false;

                // Exclude popup windows without title bar
                if ((style & WS_POPUP) != 0 && (style & 0x00C00000) == 0) // WS_CAPTION
                    return false;

                // Check window class
                var className = GetWindowClass(hWnd);
                if (IsExcludedClass(className))
                    return false;

                // Check process name
                var processName = GetProcessName(hWnd);
                if (IsExcludedProcess(processName))
                    return false;

                // Check window title
                var title = GetWindowTitle(hWnd);
                if (string.IsNullOrWhiteSpace(title))
                    return false;

                // Check window size (must be larger than minimum)
                if (!NativeMethods.GetWindowRect(hWnd, out var rect))
                    return false;

                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;

                if (width < 100 || height < 100)
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Check if window is a special window that should bypass normal validation.
        /// Returns false — all windows must pass standard IsValidWindow validation.
        /// </summary>
        public static bool IsSpecialWindow(IntPtr hWnd)
        {
            return false;
        }

        /// <summary>
        /// Get window class name
        /// </summary>
        private static string GetWindowClass(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>
        /// Get window title
        /// </summary>
        private static string GetWindowTitle(IntPtr hWnd)
        {
            var length = NativeMethods.GetWindowTextLength(hWnd);
            if (length == 0)
                return string.Empty;

            var sb = new StringBuilder(length + 1);
            NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>
        /// Get process name from window handle
        /// </summary>
        private static string GetProcessName(IntPtr hWnd)
        {
            try
            {
                NativeMethods.GetWindowThreadProcessId(hWnd, out var processId);
                var process = Process.GetProcessById((int)processId);
                return process.ProcessName.ToLowerInvariant();
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Check if window class is in exclusion list
        /// </summary>
        private static bool IsExcludedClass(string className)
        {
            if (string.IsNullOrEmpty(className))
                return false;

            foreach (var excluded in ExcludedClasses)
            {
                if (className.StartsWith(excluded, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Check if process name is in exclusion list
        /// </summary>
        private static bool IsExcludedProcess(string processName)
        {
            if (string.IsNullOrEmpty(processName))
                return false;

            foreach (var excluded in ExcludedProcesses)
            {
                if (processName.Equals(excluded, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Check if window is fullscreen
        /// </summary>
        public static bool IsWindowFullscreen(IntPtr hWnd)
        {
            try
            {
                if (!NativeMethods.GetWindowRect(hWnd, out var rect))
                    return false;

                var screen = System.Windows.Forms.Screen.FromHandle(hWnd);
                if (screen == null)
                    return false;

                // Check if window covers entire screen
                return rect.Left <= 0 &&
                       rect.Top <= 0 &&
                       rect.Right >= screen.Bounds.Right &&
                       rect.Bottom >= screen.Bounds.Bottom;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Check if window is minimized
        /// </summary>
        public static bool IsWindowMinimized(IntPtr hWnd)
        {
            var placement = new NativeMethods.WINDOWPLACEMENT();
            placement.length = System.Runtime.InteropServices.Marshal.SizeOf(placement);
            NativeMethods.GetWindowPlacement(hWnd, ref placement);

            return placement.showCmd == 2; // SW_SHOWMINIMIZED
        }

        /// <summary>
        /// Restore window from minimized/fullscreen state
        /// </summary>
        public static void RestoreWindow(IntPtr hWnd)
        {
            if (IsWindowMinimized(hWnd))
            {
                // Restore from minimized state
                NativeMethods.ShowWindow(hWnd, 9); // SW_RESTORE
            }

            if (IsWindowFullscreen(hWnd))
            {
                // Exit fullscreen by showing window normally
                NativeMethods.ShowWindow(hWnd, 1); // SW_NORMAL
            }
        }
    }
}