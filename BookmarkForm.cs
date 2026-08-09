using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WindowTopTool
{
    /// <summary>
    /// Bookmark window that appears on screen right edge when window is hidden
    /// </summary>
    public class BookmarkForm : Form
    {
        private readonly IntPtr _targetWindow;
        private readonly string _windowTitle;
        private readonly Icon? _windowIcon;
        private readonly Bitmap? _windowIconBitmap;

        private bool _isHovering = false;
        private System.Windows.Forms.Timer? _hoverTimer;
        private bool _hasShownWindow = false;

        public event EventHandler<WindowRestoredEventArgs>? WindowRestored;

        public IntPtr TargetWindow => _targetWindow;
        public string WindowTitle => _windowTitle;

        public BookmarkForm(IntPtr targetWindow, string windowTitle, Icon? windowIcon)
        {
            _targetWindow = targetWindow;
            _windowTitle = windowTitle;
            _windowIcon = windowIcon;

            // Create icon bitmap for drawing
            if (windowIcon != null)
            {
                _windowIconBitmap = new Bitmap(windowIcon.ToBitmap(), new Size(32, 32));
            }

            InitializeForm();
            InitializeTimer();
        }

        private void InitializeForm()
        {
            var config = AppConfig.Instance;

            // Set form properties
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            Opacity = 0.9;
            BackColor = Color.FromArgb(52, 152, 219); // Blue color
            Size = new Size(config.BookmarkWidth, config.BookmarkHeight);

            // Enable double buffering for smooth drawing
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.DoubleBuffer, true);

            // Make window clickable through
            StartPosition = FormStartPosition.Manual;

            // Set window style for layered window
            int exStyle = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
            exStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOPMOST;
            NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE, exStyle);

            // Set window transparency key
            TransparencyKey = Color.FromArgb(1, 1, 1);
        }

        private void InitializeTimer()
        {
            _hoverTimer = new System.Windows.Forms.Timer
            {
                Interval = 300 // 300ms hover delay
            };
            _hoverTimer.Tick += OnHoverTimer;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Draw bookmark shape
            var config = AppConfig.Instance;
            var path = CreateBookmarkPath(ClientRectangle);

            // Fill background
            using var brush = new LinearGradientBrush(
                ClientRectangle,
                _isHovering ? Color.FromArgb(41, 128, 185) : Color.FromArgb(52, 152, 219),
                _isHovering ? Color.FromArgb(52, 152, 219) : Color.FromArgb(41, 128, 185),
                LinearGradientMode.Vertical
            );
            g.FillPath(brush, path);

            // Draw border
            using var pen = new Pen(Color.FromArgb(41, 128, 185), 2);
            g.DrawPath(pen, path);

            // Draw icon
            if (_windowIconBitmap != null)
            {
                var iconX = (Width - 32) / 2;
                var iconY = (Height - 32) / 2;
                g.DrawImage(_windowIconBitmap, iconX, iconY, 32, 32);
            }
            else
            {
                // Draw default icon
                DrawDefaultIcon(g);
            }
        }

        private GraphicsPath CreateBookmarkPath(Rectangle rect)
        {
            var path = new GraphicsPath();
            int cornerRadius = 10;
            int tailWidth = 8;

            // Left side (rounded)
            path.AddArc(rect.X, rect.Y, cornerRadius, cornerRadius, 180, 90);
            path.AddLine(rect.X + cornerRadius, rect.Y, rect.Right - tailWidth, rect.Y);

            // Top-right corner (tail)
            path.AddLine(rect.Right - tailWidth, rect.Y, rect.Right, rect.Y + rect.Height / 2);
            path.AddLine(rect.Right, rect.Y + rect.Height / 2, rect.Right - tailWidth, rect.Bottom);

            // Bottom side
            path.AddLine(rect.Right - tailWidth, rect.Bottom, rect.X + cornerRadius, rect.Bottom);

            // Bottom-left corner (rounded)
            path.AddArc(rect.X, rect.Bottom - cornerRadius, cornerRadius, cornerRadius, 90, 90);

            path.CloseFigure();
            return path;
        }

        private void DrawDefaultIcon(Graphics g)
        {
            // Draw a simple pin icon
            using var pen = new Pen(Color.White, 3);
            var centerX = Width / 2;
            var centerY = Height / 2;

            // Draw circle
            g.DrawEllipse(pen, centerX - 12, centerY - 12, 24, 24);

            // Draw pin
            g.DrawLine(pen, centerX, centerY + 12, centerX, centerY + 25);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _isHovering = true;
            _hoverTimer?.Start();
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _isHovering = false;
            _hoverTimer?.Stop();
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            RestoreWindow();
        }

        private void OnHoverTimer(object? sender, EventArgs e)
        {
            _hoverTimer?.Stop();
            if (_isHovering && !_hasShownWindow)
            {
                RestoreWindow();
            }
        }

        private void RestoreWindow()
        {
            if (_hasShownWindow)
                return;

            _hasShownWindow = true;

            // Show the target window
            if (NativeMethods.IsWindow(_targetWindow))
            {
                NativeMethods.ShowWindow(_targetWindow, 9); // SW_RESTORE
                NativeMethods.SetForegroundWindow(_targetWindow);

                WindowRestored?.Invoke(this, new WindowRestoredEventArgs(_targetWindow));
            }

            // Hide bookmark
            Hide();
        }

        /// <summary>
        /// Position bookmark on screen right edge
        /// </summary>
        public void PositionBookmark(int yPosition, Screen screen)
        {
            var workingArea = screen.WorkingArea;
            Location = new Point(workingArea.Right - Width, yPosition);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hoverTimer?.Stop();
                _hoverTimer?.Dispose();
                _windowIconBitmap?.Dispose();
                _windowIcon?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    public class WindowRestoredEventArgs : EventArgs
    {
        public IntPtr WindowHandle { get; }

        public WindowRestoredEventArgs(IntPtr windowHandle)
        {
            WindowHandle = windowHandle;
        }
    }
}