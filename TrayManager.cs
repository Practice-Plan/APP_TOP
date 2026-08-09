using System;
using System.Drawing;
using System.Windows.Forms;

namespace WindowTopTool
{
    /// <summary>
    /// Manages system tray icon and context menu
    /// </summary>
    public class TrayManager : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private ContextMenuStrip _contextMenu;
        private readonly WindowPinManager _pinManager;
        private bool _disposed = false;
        private Icon? _baseIcon;

        public event EventHandler? ExitRequested;
        public event EventHandler<IntPtr>? PinWindowRequested;
        public event EventHandler? UnpinAllRequested;
        public event EventHandler? SettingsRequested;

        public TrayManager(WindowPinManager pinManager)
        {
            _pinManager = pinManager;
            _baseIcon = LoadIconFromFile();
            _contextMenu = CreateContextMenu();
            _notifyIcon = CreateNotifyIcon();

            _pinManager.WindowsChanged += OnWindowsChanged;
            LocalizationManager.LanguageChanged += OnLanguageChanged;
            UpdateTrayIcon();
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            // Rebuild the context menu so item labels follow the new language.
            var oldMenu = _contextMenu;
            oldMenu.Items.Clear();
            oldMenu.Dispose();
            _contextMenu = CreateContextMenu();
            _notifyIcon.ContextMenuStrip = _contextMenu;
            UpdateTrayIcon();
        }

        private ContextMenuStrip CreateContextMenu()
        {
            var menu = new ContextMenuStrip();

            // Pin current active window
            var pinActiveItem = new ToolStripMenuItem(LocalizationManager.GetString("Tray_PinActiveWindow"));
            pinActiveItem.Click += (s, e) => PinCurrentActiveWindow();
            menu.Items.Add(pinActiveItem);

            // Separator
            menu.Items.Add(new ToolStripSeparator());

            // Window Pin Options
            var pinOptionsItem = new ToolStripMenuItem(LocalizationManager.GetString("Tray_WindowPinOptions"));
            pinOptionsItem.Click += (s, e) => ShowWindowPinMenu();
            menu.Items.Add(pinOptionsItem);

            // Separator
            menu.Items.Add(new ToolStripSeparator());

            // Settings
            var settingsItem = new ToolStripMenuItem(LocalizationManager.GetString("Tray_Settings"));
            settingsItem.Click += (s, e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(settingsItem);

            // Unpin all
            var unpinAllItem = new ToolStripMenuItem(LocalizationManager.GetString("Tray_UnpinAll"));
            unpinAllItem.Click += (s, e) => UnpinAllRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(unpinAllItem);

            // Separator
            menu.Items.Add(new ToolStripSeparator());

            // Exit
            var exitItem = new ToolStripMenuItem(LocalizationManager.GetString("Tray_Exit"));
            exitItem.Click += (s, e) => ExitRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(exitItem);

            return menu;
        }

        private NotifyIcon CreateNotifyIcon()
        {
            var icon = new NotifyIcon
            {
                Icon = _baseIcon ?? CreateDefaultIcon(),
                Text = AppConfig.AppDisplayName,
                Visible = true,
                ContextMenuStrip = _contextMenu
            };

            // Left click shows settings
            icon.Click += (s, e) =>
            {
                if (((MouseEventArgs)e).Button == MouseButtons.Left)
                {
                    SettingsRequested?.Invoke(this, EventArgs.Empty);
                }
            };

            // Double click shows window pin menu
            icon.DoubleClick += (s, e) => ShowWindowPinMenu();

            return icon;
        }

        /// <summary>
        /// Pin the current active foreground window
        /// </summary>
        private void PinCurrentActiveWindow()
        {
            try
            {
                // Get current foreground window
                var activeWindow = NativeMethods.GetForegroundWindow();

                if (activeWindow == IntPtr.Zero)
                {
                    ShowNotification(
                        LocalizationManager.GetString("Tray_OperationFailed"),
                        LocalizationManager.GetString("Tray_NoActiveWindow"),
                        ToolTipIcon.Warning);
                    return;
                }

                // Check if already pinned
                if (_pinManager.IsPinned(activeWindow))
                {
                    // Unpin it
                    var title = GetWindowTitle(activeWindow);
                    _pinManager.UnpinWindow(activeWindow);
                    ShowNotification(
                        LocalizationManager.GetString("Tray_Unpinned"),
                        string.IsNullOrEmpty(title) ? LocalizationManager.GetString("Tray_UnnamedWindow") : title,
                        ToolTipIcon.Info);
                }
                else
                {
                    // Pin it
                    var result = _pinManager.PinWindow(activeWindow);

                    if (result)
                    {
                        var title = GetWindowTitle(activeWindow);
                        ShowNotification(
                            LocalizationManager.GetString("Tray_Pinned"),
                            string.IsNullOrEmpty(title) ? LocalizationManager.GetString("Tray_UnnamedWindow") : title,
                            ToolTipIcon.Info);
                    }
                    else
                    {
                        ShowNotification(
                            LocalizationManager.GetString("Tray_CannotPin"),
                            LocalizationManager.GetString("Tray_CannotPinBody"),
                            ToolTipIcon.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error pinning active window: {ex.Message}");
                ShowNotification(
                    LocalizationManager.GetString("Tray_OperationError"),
                    LocalizationManager.GetString("Tray_OperationErrorBody"),
                    ToolTipIcon.Error);
            }
        }

        /// <summary>
        /// Show window pin menu with all available windows
        /// </summary>
        private void ShowWindowPinMenu()
        {
            var menu = new ContextMenuStrip();

            // Add header
            var headerItem = new ToolStripLabel(LocalizationManager.GetString("Tray_SelectWindowHeader"))
            {
                Enabled = false,
                Font = new Font(menu.Font, FontStyle.Bold)
            };
            menu.Items.Add(headerItem);
            menu.Items.Add(new ToolStripSeparator());

            // Add currently pinned windows
            if (_pinManager.PinnedCount > 0)
            {
                var pinnedHeader = new ToolStripLabel(LocalizationManager.GetString("Tray_PinnedHeader"))
                {
                    Enabled = false,
                    ForeColor = Color.Green
                };
                menu.Items.Add(pinnedHeader);

                foreach (var window in _pinManager.PinnedWindows)
                {
                    var item = new ToolStripMenuItem(window.Title)
                    {
                        Checked = true,
                        CheckState = CheckState.Checked
                    };
                    var hWnd = window.Handle;
                    item.Click += (s, e) => PinWindowRequested?.Invoke(this, hWnd);
                    menu.Items.Add(item);
                }
                menu.Items.Add(new ToolStripSeparator());
            }

            // Add currently open windows
            var openWindowsHeader = new ToolStripLabel(LocalizationManager.GetString("Tray_OpenWindowsHeader"))
            {
                Enabled = false,
                ForeColor = Color.Blue
            };
            menu.Items.Add(openWindowsHeader);

            // Titles that belong to this application and must be hidden from
            // the window list. The settings form title is localized, so compare
            // against the current localized value.
            var settingsTitle = LocalizationManager.GetString("Settings_FormTitle");

            // Enumerate all visible windows
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                // Validate window before adding to menu
                if (WindowValidator.IsValidWindow(hWnd))
                {
                    var title = GetWindowTitle(hWnd);
                    if (!string.IsNullOrEmpty(title) && title != AppConfig.AppDisplayName && title != settingsTitle)
                    {
                        var isPinned = _pinManager.IsPinned(hWnd);
                        var item = new ToolStripMenuItem(title)
                        {
                            Checked = isPinned
                        };
                        var capturedHWnd = hWnd;
                        item.Click += (s, e) => _pinManager.TogglePin(capturedHWnd);
                        menu.Items.Add(item);
                    }
                }
                return true;
            }, IntPtr.Zero);

            // Show menu at cursor position
            menu.Show(Cursor.Position);
        }

        /// <summary>
        /// Get window title from handle
        /// </summary>
        private string GetWindowTitle(IntPtr hWnd)
        {
            var length = NativeMethods.GetWindowTextLength(hWnd);
            if (length == 0)
                return string.Empty;

            var builder = new System.Text.StringBuilder(length + 1);
            NativeMethods.GetWindowText(hWnd, builder, builder.Capacity);
            return builder.ToString();
        }

        /// <summary>
        /// Try to load icon from icon.ico file
        /// </summary>
        private Icon? LoadIconFromFile()
        {
            try
            {
                var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    return new Icon(iconPath);
                }
            }
            catch
            {
                // Ignore icon loading errors
            }
            return null;
        }

        /// <summary>
        /// Update tray icon based on pinned windows count
        /// </summary>
        private void UpdateTrayIcon()
        {
            var count = _pinManager.PinnedCount;

            if (_baseIcon != null)
            {
                _notifyIcon.Icon = count > 0 ? CreateActiveIcon(count, _baseIcon) : _baseIcon;
            }
            else
            {
                _notifyIcon.Icon = count > 0 ? CreateActiveIcon(count, CreateDefaultIcon()) : CreateDefaultIcon();
            }

            _notifyIcon.Text = count > 0 ? $"{AppConfig.AppDisplayName} ({count} pinned)" : AppConfig.AppDisplayName;
        }

        private void OnWindowsChanged(object? sender, EventArgs e)
        {
            UpdateTrayIcon();
        }

        /// <summary>
        /// Show balloon notification
        /// </summary>
        public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
        {
            // Check if notifications are enabled
            if (!AppConfig.Instance.ShowNotifications)
                return;

            // Check specific notification type
            if (icon == ToolTipIcon.Info && !AppConfig.Instance.ShowPinNotifications)
                return;

            if ((icon == ToolTipIcon.Error || icon == ToolTipIcon.Warning) && !AppConfig.Instance.ShowErrorNotifications)
                return;

            _notifyIcon.ShowBalloonTip(AppConfig.Instance.TooltipDuration, title, message, icon);
        }

        /// <summary>
        /// Create default icon (gray pin)
        /// </summary>
        private Icon CreateDefaultIcon()
        {
            using var bitmap = new Bitmap(32, 32);
            using var graphics = Graphics.FromImage(bitmap);

            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Draw modern pin icon - circle with stem
            using var pen = new Pen(Color.FromArgb(120, 120, 130), 2.5f);
            var rect = new Rectangle(7, 5, 18, 18);
            graphics.DrawEllipse(pen, rect);

            // Draw stem
            graphics.DrawLine(pen, 16, 23, 16, 30);

            // Draw inner highlight
            using var highlightPen = new Pen(Color.FromArgb(160, 160, 170), 1f);
            graphics.DrawEllipse(highlightPen, 10, 8, 12, 12);

            var hIcon = bitmap.GetHicon();
            return Icon.FromHandle(hIcon);
        }

        /// <summary>
        /// Create active icon (with count badge)
        /// </summary>
        private Icon CreateActiveIcon(int count, Icon baseIcon)
        {
            using var bitmap = new Bitmap(32, 32);
            using var graphics = Graphics.FromImage(bitmap);

            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Draw base icon
            graphics.DrawIcon(baseIcon, 0, 0);

            // Draw count badge with modern style
            var badgeRect = new Rectangle(18, 18, 14, 14);
            using var badgeBrush = new SolidBrush(Color.FromArgb(46, 204, 113));
            graphics.FillEllipse(badgeBrush, badgeRect);

            // Badge border
            using var badgePen = new Pen(Color.White, 1.5f);
            graphics.DrawEllipse(badgePen, badgeRect);

            // Draw count text
            using var font = new Font("Arial", 7, FontStyle.Bold);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            // Position text in center of badge
            var textPos = new RectangleF(badgeRect.X, badgeRect.Y, badgeRect.Width, badgeRect.Height);
            graphics.DrawString(count.ToString(), font, Brushes.White, textPos, format);

            var hIcon = bitmap.GetHicon();
            return Icon.FromHandle(hIcon);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                LocalizationManager.LanguageChanged -= OnLanguageChanged;
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _contextMenu.Dispose();
                _baseIcon?.Dispose();
                _disposed = true;
            }
        }
    }
}