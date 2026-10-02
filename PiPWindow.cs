using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace WindowTopTool
{
    /// <summary>
    /// Picture-in-Picture window for hidden pinned windows.
    ///
    /// v0.0.5+: Captures the target window's content via PrintWindow and
    /// displays it as a live preview, rather than just showing an icon.
    /// The PiP window size is configurable via AppConfig.PiPWidth/PiPHeight.
    ///
    /// v0.0.7+: Rewritten as a pure Win32 window class (no Form dependency).
    /// Uses CreateWindowExW + WndProc, Win32 SetTimer for live capture,
    /// and manual WndProc message handling for mouse interaction.
    /// </summary>
    public class PiPWindow : IDisposable
    {
        // ─── Constants ────────────────────────────────────────────────────

        private const string WindowClassName = "WindowTopTool.PipWindow";
        private const int TimerIdCapture = 1;
        private const int DRAG_THRESHOLD = 5;

        // ─── Instance state ────────────────────────────────────────────────

        private IntPtr _hWnd;
        private readonly IntPtr _targetWindow;
        private readonly string _windowTitle;
        private readonly Icon? _windowIcon;
        private readonly Bitmap? _windowIconBitmap;
        private readonly Size _originalSize;
        private readonly Point _originalLocation;

        private int _width;
        private int _height;
        private int _x;
        private int _y;

        // Live content capture
        private Bitmap? _captureBitmap;
        private bool _timerStarted;

        // Interaction state
        private bool _isHovering = false;
        private bool _isDragging = false;
        private Point _dragStartPos;
        private Point _formStartPos;
        private bool _wasDragged = false;
        private bool _mouseTrackingActive = false;

        // Modern color palette
        private readonly Color _bgColor = Color.FromArgb(44, 62, 80);
        private readonly Color _bgColorHover = Color.FromArgb(52, 73, 94);
        private readonly Color _accentColor = Color.FromArgb(52, 152, 219);
        private readonly Color _borderColor = Color.FromArgb(41, 128, 185);

        // Dispose state
        private bool _disposed;

        // ─── Static WndProc dispatch ───────────────────────────────────────

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private static readonly WndProcDelegate _pipWndProcDelegate = PipWindowProc;

        // Maps HWND → PiPWindow instance for WndProc dispatch.
        private static readonly Dictionary<IntPtr, PiPWindow> _instances = new();
        private static readonly object _instancesLock = new();

        // ─── Public API ────────────────────────────────────────────────────

        public event EventHandler<WindowRestoredEventArgs>? WindowRestored;

        public IntPtr TargetWindow => _targetWindow;
        public string WindowTitle => _windowTitle;

        /// <summary>
        /// Create a PiP window showing live content of the target window.
        /// </summary>
        public PiPWindow(IntPtr targetWindow, string windowTitle, Icon? windowIcon,
            Size originalSize, Point originalLocation, Size pipSize)
        {
            _targetWindow = targetWindow;
            _windowTitle = windowTitle;
            _windowIcon = windowIcon;
            _originalSize = originalSize;
            _originalLocation = originalLocation;

            // Create fallback icon bitmap
            if (windowIcon != null)
            {
                try
                {
                    var bitmap = windowIcon.ToBitmap();
                    if (bitmap != null)
                        _windowIconBitmap = new Bitmap(bitmap, new Size(48, 48));
                }
                catch { /* ignore */ }
            }

            // Clamp size to reasonable bounds
            _width = Math.Max(120, Math.Min(800, pipSize.Width));
            _height = Math.Max(80, Math.Min(600, pipSize.Height));

            // Register window class if not already registered
            RegisterWindowClass();

            // Create the window
            CreatePipWindow();

            // Start capture timer — update live content every second
            NativeMethods.SetTimer(_hWnd, new IntPtr(TimerIdCapture), 1000, IntPtr.Zero);
            _timerStarted = true;

            // First capture immediately
            CaptureTargetWindow();
        }

        public void Show()
        {
            if (_hWnd != IntPtr.Zero)
                NativeMethods.ShowWindow(_hWnd, NativeMethods.SW_SHOW);
        }

        public void Close()
        {
            try
            {
                // Stop timer first
                if (_timerStarted && _hWnd != IntPtr.Zero)
                {
                    NativeMethods.KillTimer(_hWnd, new IntPtr(TimerIdCapture));
                    _timerStarted = false;
                }

                // Destroy the window
                if (_hWnd != IntPtr.Zero)
                {
                    NativeMethods.DestroyWindow(_hWnd);
                    _hWnd = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PiPWindow.Close error: {ex.Message}");
            }
        }

        /// <summary>Public method to restore the window from outside (e.g. hotkey toggle).</summary>
        public void RestoreWindowFromOutside()
        {
            RestoreWindow();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~PiPWindow()
        {
            Dispose(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            _disposed = true;

            if (disposing)
            {
                Close();

                // Unregister from instance map
                lock (_instancesLock)
                {
                    // We can't use _hWnd here because Close() zeroes it out,
                    // so we rely on the WM_DESTROY cleanup path. But be safe:
                    // try to remove anything that might still reference us.
                }

                // Clean up GDI+ resources
                _captureBitmap?.Dispose();
                _windowIconBitmap?.Dispose();
                // Note: _windowIcon is owned by the caller, do not dispose
                // Note: _windowIconBitmap and _windowIcon are readonly fields —
                // they are disposed here but not set to null (standard pattern for readonly disposables)
            }
        }

        // ─── Window Class Registration ─────────────────────────────────────

        private static bool _classRegistered;
        private static readonly object _classLock = new();

        private static void RegisterWindowClass()
        {
            lock (_classLock)
            {
                if (_classRegistered)
                    return;

                var wc = new NativeMethods.WNDCLASSEXW
                {
                    cbSize = Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                    style = 0,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_pipWndProcDelegate),
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = NativeMethods.GetModuleHandle(null),
                    hIcon = IntPtr.Zero,
                    hCursor = IntPtr.Zero, // We'll set a cursor on WM_SETCURSOR if needed
                    hbrBackground = IntPtr.Zero,
                    lpszMenuName = null,
                    lpszClassName = WindowClassName,
                    hIconSm = IntPtr.Zero
                };

                if (NativeMethods.RegisterClassExW(ref wc))
                {
                    _classRegistered = true;
                }
                else
                {
                    var err = Marshal.GetLastWin32Error();
                    Debug.WriteLine($"RegisterClassExW failed: {err}");
                }
            }
        }

        private void CreatePipWindow()
        {
            // Position: top-right of primary monitor's working area
            var workingArea = ScreenHelper.GetWorkingArea(IntPtr.Zero);
            int screenRight = workingArea.Right > 0 ? workingArea.Right : 1920;
            int screenTop = workingArea.Top;
            _x = screenRight - _width - 20;
            _y = screenTop + 20;

            // Styles: WS_POPUP | WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TOOLWINDOW
            uint style = NativeMethods.WS_POPUP;
            uint exStyle = NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;

            _hWnd = NativeMethods.CreateWindowExW(
                exStyle,
                WindowClassName,
                null,
                style,
                _x, _y,
                _width, _height,
                IntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.GetModuleHandle(null),
                IntPtr.Zero);

            if (_hWnd == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                Debug.WriteLine($"CreateWindowExW failed: {err}");
                throw new InvalidOperationException($"Failed to create PiP window (Win32 error {err})");
            }

            // Add to instance map for WndProc dispatch
            lock (_instancesLock)
            {
                _instances[_hWnd] = this;
            }

            // Ensure layered opacity (95%)
            NativeMethods.SetLayeredWindowAttributes(_hWnd, 0, 242, 2); // LWA_ALPHA=2, 242 ≈ 95%

            // Bring to topmost
            NativeMethods.SetWindowPos(_hWnd, HWND.TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
        }

        // ─── Static WndProc ───────────────────────────────────────────────

        [DllImport("user32.dll")]
        private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

        [StructLayout(LayoutKind.Sequential)]
        private struct TRACKMOUSEEVENT
        {
            public uint cbSize;
            public uint dwFlags;
            public IntPtr hwndTrack;
            public uint dwHoverTime;
        }

        private const uint TME_HOVER = 0x00000001;
        private const uint TME_LEAVE = 0x00000002;
        private const uint WM_MOUSEHOVER = 0x02A1;
        private const uint WM_MOUSELEAVE = 0x02A3;

        private static IntPtr PipWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            PiPWindow? instance = null;
            lock (_instancesLock)
            {
                _instances.TryGetValue(hWnd, out instance);
            }

            switch (msg)
            {
                case NativeMethods.WM_PAINT:
                    instance?.OnWndPaint(hWnd);
                    return IntPtr.Zero;

                case NativeMethods.WM_ERASEBKGND:
                    // We paint everything in WM_PAINT
                    return new IntPtr(1); // Non-zero = handled

                case NativeMethods.WM_TIMER:
                    if (wParam.ToInt32() == TimerIdCapture)
                        instance?.OnWndTimer();
                    return IntPtr.Zero;

                case NativeMethods.WM_MOUSEMOVE:
                    instance?.OnWndMouseMove(hWnd, lParam);
                    return IntPtr.Zero;

                case NativeMethods.WM_LBUTTONDOWN:
                    instance?.OnWndLButtonDown();
                    return IntPtr.Zero;

                case NativeMethods.WM_LBUTTONUP:
                    instance?.OnWndLButtonUp();
                    return IntPtr.Zero;

                case NativeMethods.WM_LBUTTONDBLCLK:
                    instance?.OnWndLButtonDblClick();
                    return IntPtr.Zero;

                case NativeMethods.WM_RBUTTONDOWN:
                    instance?.OnWndRButtonDown();
                    return IntPtr.Zero;

                case NativeMethods.WM_RBUTTONUP:
                    instance?.OnWndRButtonUp();
                    return IntPtr.Zero;

                case WM_MOUSEHOVER:
                    instance?.OnWndMouseHover();
                    return IntPtr.Zero;

                case WM_MOUSELEAVE:
                    instance?.OnWndMouseLeave();
                    return IntPtr.Zero;

                case NativeMethods.WM_DESTROY:
                    // Clean up instance map
                    lock (_instancesLock)
                    {
                        _instances.Remove(hWnd);
                    }
                    return IntPtr.Zero;
            }

            return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        // ─── WndProc Message Handlers ────────────────────────────────────

        private void OnWndPaint(IntPtr hWnd)
        {
            try
            {
                using var g = Graphics.FromHwnd(hWnd);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                var rect = new Rectangle(0, 0, _width - 1, _height - 1);

                // Draw captured content or fallback background
                if (_captureBitmap != null)
                {
                    g.DrawImage(_captureBitmap, rect,
                        0, 0, _captureBitmap.Width, _captureBitmap.Height,
                        GraphicsUnit.Pixel);
                }
                else
                {
                    // Fallback: gradient background + icon
                    var bgGradient = _isHovering ? _bgColorHover : _bgColor;
                    using var brush = new LinearGradientBrush(
                        rect, bgGradient,
                        Color.FromArgb(
                            Math.Max(0, bgGradient.R - 10),
                            Math.Max(0, bgGradient.G - 10),
                            Math.Max(0, bgGradient.B - 10)),
                        LinearGradientMode.Vertical);
                    g.FillRectangle(brush, rect);

                    // Draw icon fallback
                    var iconSize = Math.Min(48, Math.Min(_width, _height) / 2);
                    if (_windowIconBitmap != null)
                    {
                        var iconX = (_width - iconSize) / 2;
                        var iconY = (_height - iconSize) / 2 - 5;
                        g.DrawImage(_windowIconBitmap, iconX, iconY, iconSize, iconSize);
                    }
                    else
                    {
                        using var defaultPen = new Pen(Color.White, 2.5f);
                        var centerX = _width / 2;
                        var centerY = _height / 2 - 5;
                        var radius = Math.Min(iconSize / 2, 20);
                        g.DrawEllipse(defaultPen, centerX - radius, centerY - radius, radius * 2, radius * 2);
                        g.DrawLine(defaultPen, centerX, centerY + (int)radius, centerX, centerY + (int)radius + 12);
                    }
                }

                // Draw accent border (thicker on hover)
                var borderWidth = _isHovering ? 3 : 2;
                using var pen = new Pen(_isHovering ? _accentColor : _borderColor, borderWidth);
                g.DrawRectangle(pen, rect);

                // Draw top accent bar
                using var accentBrush = new SolidBrush(_accentColor);
                g.FillRectangle(accentBrush, 0, 0, _width, 4);

                // Draw title overlay at bottom (semi-transparent background)
                using var font = new Font("Microsoft YaHei", 8F, FontStyle.Regular);
                var titleText = _windowTitle.Length > 18 ? _windowTitle.Substring(0, 15) + "..." : _windowTitle;
                var textSize = g.MeasureString(titleText, font);
                var titleY = _height - (int)textSize.Height - 6;
                using var titleBgBrush = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
                g.FillRectangle(titleBgBrush, 0, titleY - 2, _width, textSize.Height + 4);
                using var titleBrush = new SolidBrush(Color.FromArgb(240, 240, 240));
                g.DrawString(titleText, font, titleBrush, (_width - textSize.Width) / 2, titleY);

                // Draw hint text on hover
                if (_isHovering)
                {
                    using var hintFont = new Font("Microsoft YaHei", 7F);
                    using var hintBrush = new SolidBrush(Color.FromArgb(200, 200, 200));
                    var hintText = LocalizationManager.GetString("PiP_ClickToRestore");
                    var hintSize = g.MeasureString(hintText, hintFont);
                    using var hintBgBrush = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
                    g.FillRectangle(hintBgBrush, 0, titleY - 18, _width, 14);
                    g.DrawString(hintText, hintFont, hintBrush,
                        (_width - hintSize.Width) / 2, titleY - 16);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PiPWindow WM_PAINT error: {ex.Message}");
            }
        }

        private void OnWndTimer()
        {
            CaptureTargetWindow();
        }

        private void OnWndMouseMove(IntPtr hWnd, IntPtr lParam)
        {
            // Extract client coords from lParam
            int x = (short)(lParam.ToInt32() & 0xFFFF);
            int y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

            // Request track mouse for hover/leave events (once per mouse entry)
            if (!_mouseTrackingActive)
            {
                var tme = new TRACKMOUSEEVENT
                {
                    cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                    dwFlags = TME_HOVER | TME_LEAVE,
                    hwndTrack = hWnd,
                    dwHoverTime = 0
                };
                if (TrackMouseEvent(ref tme))
                    _mouseTrackingActive = true;
            }

            if (_isDragging)
            {
                GetCursorPos(out var cursorPos);
                var deltaX = cursorPos.X - _dragStartPos.X;
                var deltaY = cursorPos.Y - _dragStartPos.Y;

                if (Math.Abs(deltaX) > DRAG_THRESHOLD || Math.Abs(deltaY) > DRAG_THRESHOLD)
                    _wasDragged = true;

                if (_wasDragged)
                {
                    _x = _formStartPos.X + deltaX;
                    _y = _formStartPos.Y + deltaY;
                    NativeMethods.MoveWindow(_hWnd, _x, _y, _width, _height, true);
                }
            }
        }

        private void OnWndMouseHover()
        {
            _isHovering = true;
            RequestRepaint();
        }

        private void OnWndMouseLeave()
        {
            _isHovering = false;
            _mouseTrackingActive = false;
            RequestRepaint();
        }

        private void OnWndLButtonDown()
        {
            _isDragging = true;
            _wasDragged = false;
            GetCursorPos(out var pt);
            _dragStartPos = new Point(pt.X, pt.Y);
            _formStartPos = new Point(_x, _y);

            // Capture mouse to keep getting move events during drag
            NativeMethods.SetCapture(_hWnd);
        }

        private void OnWndLButtonUp()
        {
            // Release mouse capture
            NativeMethods.ReleaseCapture();

            if (!_wasDragged && !_isDragging)
            {
                // Click with no drag → restore
                RestoreWindow();
            }
            _isDragging = false;
        }

        private void OnWndLButtonDblClick()
        {
            // Double click also restores
            RestoreWindow();
        }

        private void OnWndRButtonDown()
        {
            // Right-click restore
            RestoreWindow();
        }

        private void OnWndRButtonUp()
        {
            // Handled by WM_RBUTTONDOWN — nothing to do here
        }

        private static void GetCursorPos(out Point pt)
        {
            NativeMethods.GetCursorPos(out var nativePt);
            pt = new Point(nativePt.X, nativePt.Y);
        }

        // ─── Live content capture ────────────────────────────────────────

        /// <summary>
        /// Capture the target window's content using PrintWindow.
        /// Works even when the target window is hidden.
        /// </summary>
        private void CaptureTargetWindow()
        {
            try
            {
                // Get target window size
                NativeMethods.GetWindowRect(_targetWindow, out var rect);
                var srcW = rect.Right - rect.Left;
                var srcH = rect.Bottom - rect.Top;
                if (srcW <= 0 || srcH <= 0)
                    return;

                // Cap capture size to prevent excessive memory use
                const int maxCapture = 1280;
                if (srcW > maxCapture) srcW = maxCapture;
                if (srcH > maxCapture) srcH = maxCapture;

                // Create or reuse bitmap
                if (_captureBitmap == null ||
                    _captureBitmap.Width != srcW ||
                    _captureBitmap.Height != srcH)
                {
                    _captureBitmap?.Dispose();
                    _captureBitmap = new Bitmap(srcW, srcH);
                }

                // Capture using PrintWindow with PW_RENDERFULLCONTENT
                using var g = Graphics.FromImage(_captureBitmap);
                var hdc = g.GetHdc();
                try
                {
                    NativeMethods.PrintWindow(
                        _targetWindow,
                        hdc,
                        NativeMethods.PW_RENDERFULLCONTENT);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }

                RequestRepaint();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PiP capture failed: {ex.Message}");
            }
        }

        private void RequestRepaint()
        {
            if (_hWnd != IntPtr.Zero)
            {
                NativeMethods.InvalidateRect(_hWnd, IntPtr.Zero, true);
                NativeMethods.UpdateWindow(_hWnd);
            }
        }

        // ─── Restore logic ───────────────────────────────────────────────

        private void RestoreWindow()
        {
            try
            {
                NativeMethods.MoveWindow(_targetWindow,
                    _originalLocation.X,
                    _originalLocation.Y,
                    _originalSize.Width,
                    _originalSize.Height,
                    true);

                NativeMethods.ShowWindow(_targetWindow, 9); // SW_RESTORE
                NativeMethods.SetForegroundWindow(_targetWindow);

                WindowRestored?.Invoke(this, new WindowRestoredEventArgs(_targetWindow));

                // Close the PiP window (this will also set _hWnd = IntPtr.Zero)
                Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to restore window: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Event args raised when the PiP window restores the hidden target window.
    /// Contains the handle of the window that was restored.
    /// </summary>
    public class WindowRestoredEventArgs : EventArgs
    {
        public IntPtr WindowHandle { get; }

        public WindowRestoredEventArgs(IntPtr windowHandle)
        {
            WindowHandle = windowHandle;
        }
    }
}
