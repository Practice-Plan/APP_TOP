using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace WindowTopTool
{
    /// <summary>
    /// Picture-in-Picture window for hidden pinned windows.
    ///
    /// v0.0.5+: Captures the target window's content via PrintWindow and
    /// displays it as a live preview, rather than just showing an icon.
    /// The PiP window size is configurable via AppConfig.PiPWidth/PiPHeight.
    /// </summary>
    public class PiPWindow : Form
    {
        private readonly IntPtr _targetWindow;
        private readonly string _windowTitle;
        private readonly Icon? _windowIcon;
        private readonly Bitmap? _windowIconBitmap;
        private readonly Size _originalSize;
        private readonly Point _originalLocation;

        // Live content capture
        private Bitmap? _captureBitmap;
        private readonly System.Windows.Forms.Timer _captureTimer;

        // Interaction state
        private bool _isHovering = false;
        private bool _isDragging = false;
        private Point _dragStartPos;
        private Point _formStartPos;
        private bool _wasDragged = false;
        private const int DRAG_THRESHOLD = 5;

        // Modern color palette
        private readonly Color _bgColor = Color.FromArgb(44, 62, 80);
        private readonly Color _bgColorHover = Color.FromArgb(52, 73, 94);
        private readonly Color _accentColor = Color.FromArgb(52, 152, 219);
        private readonly Color _borderColor = Color.FromArgb(41, 128, 185);

        public event EventHandler<WindowRestoredEventArgs>? WindowRestored;

        public IntPtr TargetWindow => _targetWindow;
        public string WindowTitle => _windowTitle;

        /// <summary>
        /// Create a PiP window showing live content of the target window.
        /// </summary>
        /// <param name="pipSize">Size of the PiP window (from AppConfig).</param>
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
                var bitmap = windowIcon.ToBitmap();
                if (bitmap != null)
                    _windowIconBitmap = new Bitmap(bitmap, new Size(48, 48));
            }

            // Capture timer — update live content every second
            _captureTimer = new System.Windows.Forms.Timer
            {
                Interval = 1000,
                Enabled = true
            };
            _captureTimer.Tick += OnCaptureTick;

            InitializeForm(pipSize);
            CaptureTargetWindow(); // first capture immediately
        }

        private void InitializeForm(Size pipSize)
        {
            // Clamp to reasonable bounds
            var w = Math.Max(120, Math.Min(800, pipSize.Width));
            var h = Math.Max(80, Math.Min(600, pipSize.Height));

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            Opacity = 0.95;
            BackColor = _bgColor;
            Size = new Size(w, h);
            StartPosition = FormStartPosition.Manual;
            var primaryScreen = Screen.PrimaryScreen;
            Location = primaryScreen != null
                ? new Point(primaryScreen.WorkingArea.Right - w - 20, 20)
                : new Point(Screen.GetBounds(Point.Empty).Right - w - 20, 20);

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.DoubleBuffer, true);

            int exStyle = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
            exStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOPMOST;
            NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE, exStyle);
        }

        // ── Live content capture ──────────────────────────────────────

        /// <summary>
        /// Capture the target window's content using PrintWindow.
        /// Works even when the target window is hidden.
        /// </summary>
        private void CaptureTargetWindow()
        {
            try
            {
                // Get target window size
                NativeMethods.RECT rect;
                NativeMethods.GetWindowRect(_targetWindow, out rect);
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

                Invalidate(); // trigger repaint
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PiP capture failed: {ex.Message}");
            }
        }

        private void OnCaptureTick(object? sender, EventArgs e)
        {
            CaptureTargetWindow();
        }

        // ── Painting ──────────────────────────────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);

            // Draw captured content or fallback background
            if (_captureBitmap != null)
            {
                // Draw the live content scaled to fit
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
                var iconSize = Math.Min(48, Math.Min(Width, Height) / 2);
                if (_windowIconBitmap != null)
                {
                    var iconX = (Width - iconSize) / 2;
                    var iconY = (Height - iconSize) / 2 - 5;
                    g.DrawImage(_windowIconBitmap, iconX, iconY, iconSize, iconSize);
                }
                else
                {
                    using var defaultPen = new Pen(Color.White, 2.5f);
                    var centerX = Width / 2;
                    var centerY = Height / 2 - 5;
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
            g.FillRectangle(accentBrush, 0, 0, Width, 4);

            // Draw title overlay at bottom (semi-transparent background)
            using var font = new Font("Microsoft YaHei", 8F, FontStyle.Regular);
            var titleText = _windowTitle.Length > 18 ? _windowTitle.Substring(0, 15) + "..." : _windowTitle;
            var textSize = g.MeasureString(titleText, font);
            var titleY = Height - (int)textSize.Height - 6;
            using var titleBgBrush = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
            g.FillRectangle(titleBgBrush, 0, titleY - 2, Width, textSize.Height + 4);
            using var titleBrush = new SolidBrush(Color.FromArgb(240, 240, 240));
            g.DrawString(titleText, font, titleBrush, (Width - textSize.Width) / 2, titleY);

            // Draw hint text on hover
            if (_isHovering)
            {
                using var hintFont = new Font("Microsoft YaHei", 7F);
                using var hintBrush = new SolidBrush(Color.FromArgb(200, 200, 200));
                var hintText = LocalizationManager.GetString("PiP_ClickToRestore");
                var hintSize = g.MeasureString(hintText, hintFont);
                using var hintBgBrush = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
                g.FillRectangle(hintBgBrush, 0, titleY - 18, Width, 14);
                g.DrawString(hintText, hintFont, hintBrush,
                    (Width - hintSize.Width) / 2, titleY - 16);
            }
        }

        // ── Interaction ───────────────────────────────────────────────

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _isHovering = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _isHovering = false;
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
        }

        /// <summary>Public method to restore the window from outside (e.g. hotkey toggle).</summary>
        public void RestoreWindowFromOutside()
        {
            RestoreWindow();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _isDragging = true;
                _wasDragged = false;
                _dragStartPos = Cursor.Position;
                _formStartPos = Location;
            }
            else if (e.Button == MouseButtons.Right)
            {
                RestoreWindow();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_isDragging)
            {
                var currentPos = Cursor.Position;
                var deltaX = currentPos.X - _dragStartPos.X;
                var deltaY = currentPos.Y - _dragStartPos.Y;

                if (Math.Abs(deltaX) > DRAG_THRESHOLD || Math.Abs(deltaY) > DRAG_THRESHOLD)
                    _wasDragged = true;

                if (_wasDragged)
                    Location = new Point(_formStartPos.X + deltaX, _formStartPos.Y + deltaY);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && !_wasDragged)
                RestoreWindow();
            _isDragging = false;
        }

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
                Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to restore window: {ex.Message}");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _captureTimer.Stop();
                _captureTimer.Dispose();
                _captureBitmap?.Dispose();
                _windowIconBitmap?.Dispose();
                _windowIcon?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
