using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace WindowTopTool
{
    /// <summary>
    /// Picture-in-Picture window for hidden pinned windows
    /// </summary>
    public class PiPWindow : Form
    {
        private readonly IntPtr _targetWindow;
        private readonly string _windowTitle;
        private readonly Icon? _windowIcon;
        private readonly Bitmap? _windowIconBitmap;
        private readonly Size _originalSize;
        private readonly Point _originalLocation;

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

        public PiPWindow(IntPtr targetWindow, string windowTitle, Icon? windowIcon, Size originalSize, Point originalLocation)
        {
            _targetWindow = targetWindow;
            _windowTitle = windowTitle;
            _windowIcon = windowIcon;
            _originalSize = originalSize;
            _originalLocation = originalLocation;

            // Create icon bitmap for drawing
            if (windowIcon != null)
            {
                var bitmap = windowIcon.ToBitmap();
                if (bitmap != null)
                {
                    _windowIconBitmap = new Bitmap(bitmap, new Size(48, 48));
                }
            }

            InitializeForm();
        }

        private void InitializeForm()
        {
            // Calculate PiP size (1:10 ratio)
            var pipWidth = _originalSize.Width / 10;
            var pipHeight = _originalSize.Height / 10;

            // Ensure minimum size
            pipWidth = Math.Max(pipWidth, 80);
            pipHeight = Math.Max(pipHeight, 80);

            // Set form properties
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            Opacity = 0.92;
            BackColor = _bgColor;
            Size = new Size(pipWidth, pipHeight);
            StartPosition = FormStartPosition.Manual;
            var primaryScreen = Screen.PrimaryScreen;
            Location = primaryScreen != null
                ? new Point(primaryScreen.WorkingArea.Right - pipWidth - 20, 20)
                : new Point(Screen.GetBounds(Point.Empty).Right - pipWidth - 20, 20);

            // Enable double buffering for smooth drawing
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.DoubleBuffer, true);

            // Set window style
            int exStyle = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
            exStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOPMOST;
            NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE, exStyle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);

            // Draw gradient background
            var bgGradient = _isHovering ? _bgColorHover : _bgColor;
            using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                rect,
                bgGradient,
                Color.FromArgb(
                    Math.Max(0, bgGradient.R - 10),
                    Math.Max(0, bgGradient.G - 10),
                    Math.Max(0, bgGradient.B - 10)),
                System.Drawing.Drawing2D.LinearGradientMode.Vertical))
            {
                g.FillRectangle(brush, rect);
            }

            // Draw accent border (thicker on hover)
            var borderWidth = _isHovering ? 3 : 2;
            using var pen = new Pen(_isHovering ? _accentColor : _borderColor, borderWidth);
            g.DrawRectangle(pen, rect);

            // Draw top accent bar
            using var accentBrush = new SolidBrush(_accentColor);
            g.FillRectangle(accentBrush, 0, 0, Width, 4);

            // Draw icon
            var iconSize = Math.Min(48, Math.Min(Width, Height) / 2);
            if (_windowIconBitmap != null)
            {
                var iconX = (Width - iconSize) / 2;
                var iconY = (Height - iconSize) / 2 - 5;
                g.DrawImage(_windowIconBitmap, iconX, iconY, iconSize, iconSize);
            }
            else
            {
                // Draw default icon
                using var defaultPen = new Pen(Color.White, 2.5f);
                var centerX = Width / 2;
                var centerY = Height / 2 - 5;
                var radius = Math.Min(iconSize / 2, 20);
                g.DrawEllipse(defaultPen, centerX - radius, centerY - radius, radius * 2, radius * 2);
                g.DrawLine(defaultPen, centerX, centerY + (int)radius, centerX, centerY + (int)radius + 12);
            }

            // Draw window title (truncated) at bottom
            using var font = new Font("Microsoft YaHei", 8F, FontStyle.Regular);
            using var titleBrush = new SolidBrush(Color.FromArgb(220, 220, 220));
            var titleText = _windowTitle.Length > 18 ? _windowTitle.Substring(0, 15) + "..." : _windowTitle;
            var textSize = g.MeasureString(titleText, font);
            var titleY = Height - (int)textSize.Height - 6;
            // Draw subtle background for title
            using var titleBgBrush = new SolidBrush(Color.FromArgb(0, 0, 0, 80));
            g.FillRectangle(titleBgBrush, 4, titleY - 2, Width - 8, textSize.Height + 4);
            g.DrawString(titleText, font, titleBrush, (Width - textSize.Width) / 2, titleY);

            // Draw hint text
            if (_isHovering)
            {
                using var hintFont = new Font("Microsoft YaHei", 7F);
                using var hintBrush = new SolidBrush(Color.FromArgb(180, 180, 180));
                var hintText = LocalizationManager.GetString("PiP_ClickToRestore");
                var hintSize = g.MeasureString(hintText, hintFont);
                g.DrawString(hintText, hintFont, hintBrush, (Width - hintSize.Width) / 2, titleY - 16);
            }
        }

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
            // Restore is handled in OnMouseUp to avoid double-restore
            // (OnMouseUp fires for clicks that aren't drags)
        }

        /// <summary>
        /// Public method to restore the window from outside (e.g., hotkey toggle)
        /// </summary>
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
                // Right click restores window
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
                {
                    _wasDragged = true;
                }

                if (_wasDragged)
                {
                    Location = new Point(_formStartPos.X + deltaX, _formStartPos.Y + deltaY);
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && !_wasDragged)
            {
                // This was a click, not a drag - restore window
                RestoreWindow();
            }
            _isDragging = false;
        }

        private void RestoreWindow()
        {
            try
            {
                // Restore original size and position
                NativeMethods.MoveWindow(_targetWindow,
                    _originalLocation.X,
                    _originalLocation.Y,
                    _originalSize.Width,
                    _originalSize.Height,
                    true);

                // Show the window
                NativeMethods.ShowWindow(_targetWindow, 9); // SW_RESTORE
                NativeMethods.SetForegroundWindow(_targetWindow);

                WindowRestored?.Invoke(this, new WindowRestoredEventArgs(_targetWindow));

                // Close PiP window
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
                _windowIconBitmap?.Dispose();
                _windowIcon?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}