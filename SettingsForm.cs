using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace WindowTopTool
{
    /// <summary>
    /// Settings form for Window Top Tool configuration
    /// </summary>
    public class SettingsForm : Form
    {
        private readonly WindowPinManager _pinManager;
        private readonly AppConfig _config;
        private NumericUpDown? _opacityNumeric;
        private NumericUpDown? _edgeThresholdNumeric;
        private NumericUpDown? _autoSaveIntervalNumeric;
        private CheckBox? _showNotificationsCheckBox;
        private CheckBox? _showPinNotificationsCheckBox;
        private CheckBox? _showErrorNotificationsCheckBox;
        private Button? _saveButton;
        private Button? _closeButton;
        private ListBox? _pinnedWindowsList;
        private Button? _unpinSelectedButton;
        private GroupBox? _notificationsGroupBox;
        private ListBox? _windowList;
        private Button? _refreshButton;
        private System.Windows.Forms.Timer? _refreshTimer;
        private TextBox? _searchBox;
        private TabControl? _tabControl;
        private ComboBox? _languageComboBox;
        private bool _subscribedToWindowsChanged;
        /// <summary>Guard flag: true while the form is rebuilding its UI in
        /// response to a language change. Timer callbacks and window-list
        /// refreshes are skipped while this is set to prevent access to
        /// disposed/null controls.</summary>
        private bool _isRebuilding;

        public SettingsForm(WindowPinManager pinManager)
        {
            _pinManager = pinManager;
            _config = AppConfig.Instance;

            InitializeComponents();
            LoadSettings();
            RefreshPinnedWindows();
            RefreshWindowList();

            // Subscribe to configuration changes
            _config.ConfigurationChanged += OnConfigurationChanged;
            LocalizationManager.LanguageChanged += OnLanguageChanged;

            // Setup auto-refresh timer
            _refreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _refreshTimer.Tick += (s, e) => RefreshWindowList();
            _refreshTimer.Start();
        }

        private void InitializeComponents()
        {
            // Form settings
            Text = LocalizationManager.GetString("Settings_FormTitle");
            Size = new Size(670, 720);
            MinimumSize = new Size(600, 600);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei", 9F);
            BackColor = Color.FromArgb(250, 250, 252);
            ApplyRtlLayout();

            _tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Padding = new Point(12, 8),
                Font = new Font("Microsoft YaHei", 9F)
            };

            // Tab 1: Window Management
            var windowTab = new TabPage(LocalizationManager.GetString("Settings_TabWindow"));
            BuildWindowManagementTab(windowTab);
            _tabControl.TabPages.Add(windowTab);

            // Tab 2: General Settings
            var settingsTab = new TabPage(LocalizationManager.GetString("Settings_TabGeneral"));
            BuildSettingsTab(settingsTab);
            _tabControl.TabPages.Add(settingsTab);

            // Tab 3: About / Info
            var aboutTab = new TabPage(LocalizationManager.GetString("Settings_TabAbout"));
            BuildAboutTab(aboutTab);
            _tabControl.TabPages.Add(aboutTab);

            Controls.Add(_tabControl);

            // Event handlers - subscribe once so rebuilds don't duplicate handlers
            if (!_subscribedToWindowsChanged)
            {
                _pinManager.WindowsChanged += OnWindowsChangedInternal;
                _subscribedToWindowsChanged = true;
            }
        }

        /// <summary>
        /// Apply right-to-left layout when the active language requires it
        /// (Arabic). Resets to LTR for other languages.
        /// </summary>
        private void ApplyRtlLayout()
        {
            if (LocalizationManager.IsRightToLeft)
            {
                RightToLeft = RightToLeft.Yes;
                RightToLeftLayout = true;
            }
            else
            {
                RightToLeft = RightToLeft.No;
                RightToLeftLayout = false;
            }
        }

        /// <summary>
        /// Rebuild the entire form surface after a language change so all
        /// labels, tabs, and the RTL layout follow the new language.
        ///
        /// The rebuild is <b>deferred</b> via <see cref="BeginInvoke"/> so that
        /// the LanguageChanged event — raised synchronously from
        /// <see cref="LocalizationManager.SetLanguage"/>, which is called from
        /// the language ComboBox's SelectedIndexChanged handler — does not tear
        /// down the ComboBox while its event handler is still on the call
        /// stack. Without deferral, disposing the tab control mid-handler
        /// causes re-entrancy crashes (ObjectDisposedException / NRE).
        /// </summary>
        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            if (IsDisposed || _isRebuilding)
                return;

            // Always defer to the next message-pump cycle, even on the UI
            // thread, so the current event-handler chain unwinds first.
            BeginInvoke((Action)OnLanguageChangedImpl);
        }

        private void OnLanguageChangedImpl()
        {
            if (IsDisposed || _isRebuilding)
                return;

            _isRebuilding = true;
            try
            {
                // Remember the current tab so we can restore it after rebuild.
                var selectedTab = _tabControl?.SelectedIndex ?? 0;

                // Tear down the current tab control (disposes all child controls).
                if (_tabControl != null)
                {
                    Controls.Remove(_tabControl);
                    _tabControl.Dispose();
                    _tabControl = null;
                }

                // Reset the per-build control references (they are recreated below).
                _opacityNumeric = null;
                _edgeThresholdNumeric = null;
                _autoSaveIntervalNumeric = null;
                _showNotificationsCheckBox = null;
                _showPinNotificationsCheckBox = null;
                _showErrorNotificationsCheckBox = null;
                _saveButton = null;
                _closeButton = null;
                _pinnedWindowsList = null;
                _unpinSelectedButton = null;
                _notificationsGroupBox = null;
                _windowList = null;
                _refreshButton = null;
                _searchBox = null;
                _languageComboBox = null;

                InitializeComponents();
                LoadSettings();
                RefreshPinnedWindows();
                RefreshWindowList();

                if (_tabControl != null && selectedTab >= 0 && selectedTab < _tabControl.TabCount)
                    _tabControl.SelectedIndex = selectedTab;
            }
            finally
            {
                _isRebuilding = false;
            }
        }

        private void OnWindowsChangedInternal(object? sender, EventArgs e)
        {
            if (_isRebuilding)
                return; // Controls are being torn down/recreated; skip.
            RefreshPinnedWindows();
            RefreshWindowList();
        }

        #region Tab 1: Window Management

        private void BuildWindowManagementTab(TabPage tab)
        {
            tab.Padding = new Padding(15);
            var yOffset = 10;

            // Pinned Windows Section
            var pinnedLabel = new Label
            {
                Text = LocalizationManager.GetString("Settings_PinnedWindows"),
                Location = new Point(15, yOffset),
                Size = new Size(200, 22),
                Font = new Font(Font, FontStyle.Bold)
            };
            tab.Controls.Add(pinnedLabel);
            yOffset += 25;

            // Toolbar for pinned windows
            var unpinAllButton = new Button
            {
                Text = LocalizationManager.GetString("Settings_UnpinAll"),
                Location = new Point(385, yOffset - 25),
                Size = new Size(75, 26),
                BackColor = Color.FromArgb(231, 76, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            unpinAllButton.FlatAppearance.BorderSize = 0;
            unpinAllButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(192, 57, 43);
            unpinAllButton.Click += (s, e) =>
            {
                _pinManager.UnpinAll();
                RefreshPinnedWindows();
                RefreshWindowList();
            };
            tab.Controls.Add(unpinAllButton);

            _pinnedWindowsList = new ListBox
            {
                Location = new Point(15, yOffset),
                Size = new Size(610, 100),
                SelectionMode = SelectionMode.MultiExtended,
                IntegralHeight = false
            };
            tab.Controls.Add(_pinnedWindowsList);

            _unpinSelectedButton = new Button
            {
                Text = LocalizationManager.GetString("Settings_UnpinSelected"),
                Location = new Point(475, yOffset - 25),
                Size = new Size(75, 26),
                BackColor = Color.FromArgb(243, 156, 18),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            _unpinSelectedButton.FlatAppearance.BorderSize = 0;
            _unpinSelectedButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(211, 84, 0);
            _unpinSelectedButton.Click += OnUnpinSelected;
            tab.Controls.Add(_unpinSelectedButton);

            yOffset += 110;

            AddHorizontalSeparator(tab, ref yOffset);

            // Available Windows Section
            var availableLabel = new Label
            {
                Text = LocalizationManager.GetString("Settings_AvailableWindows"),
                Location = new Point(15, yOffset),
                Size = new Size(250, 22),
                Font = new Font(Font, FontStyle.Bold)
            };
            tab.Controls.Add(availableLabel);
            yOffset += 28;

            // Search box
            var searchLabel = new Label
            {
                Text = LocalizationManager.GetString("Settings_Search"),
                Location = new Point(15, yOffset),
                Size = new Size(40, 24),
                TextAlign = ContentAlignment.MiddleLeft
            };
            tab.Controls.Add(searchLabel);

            _searchBox = new TextBox
            {
                Location = new Point(55, yOffset),
                Size = new Size(200, 24),
                PlaceholderText = LocalizationManager.GetString("Settings_SearchPlaceholder")
            };
            _searchBox.TextChanged += (s, e) => FilterWindowList();
            tab.Controls.Add(_searchBox);

            // Filter indicator
            var filterInfo = new Label
            {
                Text = LocalizationManager.GetString("Settings_FilterHint"),
                Location = new Point(270, yOffset + 2),
                Size = new Size(300, 20),
                ForeColor = Color.DarkGray,
                Font = new Font(Font, FontStyle.Italic),
                AutoSize = true
            };
            tab.Controls.Add(filterInfo);

            yOffset += 30;

            _windowList = new ListBox
            {
                Location = new Point(15, yOffset),
                Size = new Size(610, 260),
                SelectionMode = SelectionMode.MultiExtended,
                IntegralHeight = false
            };
            _windowList.DoubleClick += OnWindowDoubleClick;
            tab.Controls.Add(_windowList);

            _refreshButton = new Button
            {
                Text = LocalizationManager.GetString("Settings_RefreshList"),
                Location = new Point(15, yOffset - 28),
                Size = new Size(70, 26),
                BackColor = Color.FromArgb(52, 152, 219),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            _refreshButton.FlatAppearance.BorderSize = 0;
            _refreshButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(41, 128, 185);
            _refreshButton.Click += (s, e) => RefreshWindowList();
            tab.Controls.Add(_refreshButton);
        }

        #endregion

        #region Tab 2: General Settings

        private void BuildSettingsTab(TabPage tab)
        {
            tab.Padding = new Padding(15);
            var yOffset = 10;

            // Window Settings Group
            var windowGroupBox = new GroupBox
            {
                Text = LocalizationManager.GetString("Settings_WindowSettings"),
                Location = new Point(15, yOffset),
                Size = new Size(610, 140),
                Font = new Font(Font, FontStyle.Bold)
            };

            var groupY = 28;
            AddLabeledControl(windowGroupBox, ref groupY, LocalizationManager.GetString("Settings_DefaultOpacity"),
                _opacityNumeric = new NumericUpDown
                {
                    Location = new Point(200, groupY - 2),
                    Size = new Size(75, 22),
                    Minimum = 20,
                    Maximum = 100,
                    Value = 78
                });
            groupY += 32;

            AddLabeledControl(windowGroupBox, ref groupY, LocalizationManager.GetString("Settings_EdgeThreshold"),
                _edgeThresholdNumeric = new NumericUpDown
                {
                    Location = new Point(200, groupY - 2),
                    Size = new Size(75, 22),
                    Minimum = 5,
                    Maximum = 50,
                    Value = 12
                });
            groupY += 32;

            AddLabeledControl(windowGroupBox, ref groupY, LocalizationManager.GetString("Settings_AutoSaveInterval"),
                _autoSaveIntervalNumeric = new NumericUpDown
                {
                    Location = new Point(200, groupY - 2),
                    Size = new Size(75, 22),
                    Minimum = 10,
                    Maximum = 300,
                    Value = 30
                });

            tab.Controls.Add(windowGroupBox);
            yOffset += 150;

            // Notification Settings Group
            _notificationsGroupBox = new GroupBox
            {
                Text = LocalizationManager.GetString("Settings_NotificationSettings"),
                Location = new Point(15, yOffset),
                Size = new Size(610, 130),
                Font = new Font(Font, FontStyle.Bold)
            };

            var notifyYOffset = 28;
            _showNotificationsCheckBox = new CheckBox
            {
                Text = LocalizationManager.GetString("Settings_EnableNotifications"),
                Location = new Point(20, notifyYOffset),
                Size = new Size(300, 22)
            };
            _showNotificationsCheckBox.CheckedChanged += (s, e) =>
            {
                UpdateNotificationDependents();
                AutoSaveOnChange();
            };
            _notificationsGroupBox.Controls.Add(_showNotificationsCheckBox);
            notifyYOffset += 28;

            _showPinNotificationsCheckBox = new CheckBox
            {
                Text = LocalizationManager.GetString("Settings_ShowPinNotifications"),
                Location = new Point(20, notifyYOffset),
                Size = new Size(300, 22)
            };
            _showPinNotificationsCheckBox.CheckedChanged += (s, e) => AutoSaveOnChange();
            _notificationsGroupBox.Controls.Add(_showPinNotificationsCheckBox);
            notifyYOffset += 28;

            _showErrorNotificationsCheckBox = new CheckBox
            {
                Text = LocalizationManager.GetString("Settings_ShowErrorNotifications"),
                Location = new Point(20, notifyYOffset),
                Size = new Size(300, 22)
            };
            _showErrorNotificationsCheckBox.CheckedChanged += (s, e) => AutoSaveOnChange();
            _notificationsGroupBox.Controls.Add(_showErrorNotificationsCheckBox);

            tab.Controls.Add(_notificationsGroupBox);
            yOffset += 140;

            // Language selector
            var langLabel = new Label
            {
                Text = LocalizationManager.GetString("Settings_LanguageLabel"),
                Location = new Point(15, yOffset + 12),
                Size = new Size(70, 24),
                TextAlign = ContentAlignment.MiddleLeft
            };
            tab.Controls.Add(langLabel);

            _languageComboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(90, yOffset + 10),
                Size = new Size(180, 24)
            };
            // Populate language choices (display name, code).
            var languages = new[]
            {
                ("English", "en"),
                ("中文", "zh"),
                ("Français", "fr"),
                ("Русский", "ru"),
                ("العربية", "ar"),
            };
            foreach (var (displayName, code) in languages)
                _languageComboBox.Items.Add(new LanguageOption(displayName, code));
            _languageComboBox.DisplayMember = "DisplayName";
            // Select the current language without firing the change handler.
            var currentLang = LocalizationManager.CurrentLanguage;
            for (int i = 0; i < _languageComboBox.Items.Count; i++)
            {
                if (((LanguageOption)_languageComboBox.Items[i]!).Code == currentLang)
                {
                    _languageComboBox.SelectedIndex = i;
                    break;
                }
            }
            _languageComboBox.SelectedIndexChanged += (s, e) =>
            {
                if (_languageComboBox.SelectedItem is LanguageOption opt)
                    LocalizationManager.SetLanguage(opt.Code);
            };
            tab.Controls.Add(_languageComboBox);

            // Buttons
            _saveButton = new Button
            {
                Text = LocalizationManager.GetString("Settings_Save"),
                Location = new Point(350, yOffset + 10),
                Size = new Size(110, 35),
                BackColor = Color.FromArgb(46, 204, 113),
                ForeColor = Color.White,
                Font = new Font(Font, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            _saveButton.FlatAppearance.BorderSize = 0;
            _saveButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(39, 174, 96);
            _saveButton.Click += OnSave;
            tab.Controls.Add(_saveButton);

            _closeButton = new Button
            {
                Text = LocalizationManager.GetString("Settings_Close"),
                Location = new Point(470, yOffset + 10),
                Size = new Size(110, 35),
                BackColor = Color.FromArgb(149, 165, 166),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            _closeButton.FlatAppearance.BorderSize = 0;
            _closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(127, 140, 141);
            _closeButton.Click += (s, e) => Close();
            tab.Controls.Add(_closeButton);
        }

        /// <summary>A language entry for the settings drop-down.</summary>
        private sealed class LanguageOption
        {
            public string DisplayName { get; }
            public string Code { get; }
            public LanguageOption(string displayName, string code)
            {
                DisplayName = displayName;
                Code = code;
            }
            public override string ToString() => DisplayName;
        }

        #endregion

        #region Tab 3: About / Info

        private void BuildAboutTab(TabPage tab)
        {
            tab.Padding = new Padding(15);

            var yPos = 15;

            // App title
            var titleLabel = new Label
            {
                Text = LocalizationManager.GetString("About_Title"),
                Location = new Point(15, yPos),
                Size = new Size(610, 28),
                Font = new Font("Microsoft YaHei", 13F, FontStyle.Bold),
                ForeColor = Color.FromArgb(44, 62, 80),
                TextAlign = ContentAlignment.MiddleCenter
            };
            tab.Controls.Add(titleLabel);
            yPos += 35;

            var subtitleLabel = new Label
            {
                Text = LocalizationManager.GetString("About_Subtitle"),
                Location = new Point(15, yPos),
                Size = new Size(610, 20),
                Font = new Font(Font, FontStyle.Italic),
                ForeColor = Color.Gray,
                TextAlign = ContentAlignment.MiddleCenter
            };
            tab.Controls.Add(subtitleLabel);
            yPos += 35;

            // Section: Feature list
            var featureHeader = new Label
            {
                Text = LocalizationManager.GetString("About_FeaturesHeader"),
                Location = new Point(15, yPos),
                Size = new Size(610, 22),
                Font = new Font(Font, FontStyle.Bold),
                ForeColor = Color.SteelBlue
            };
            tab.Controls.Add(featureHeader);
            yPos += 28;

            var features = new[]
            {
                (LocalizationManager.GetString("About_Feature_Pin_Name"), LocalizationManager.GetString("About_Feature_Pin_Desc")),
                (LocalizationManager.GetString("About_Feature_ClickThrough_Name"), LocalizationManager.GetString("About_Feature_ClickThrough_Desc")),
                (LocalizationManager.GetString("About_Feature_Mini_Name"), LocalizationManager.GetString("About_Feature_Mini_Desc")),
                (LocalizationManager.GetString("About_Feature_PiP_Name"), LocalizationManager.GetString("About_Feature_PiP_Desc")),
                (LocalizationManager.GetString("About_Feature_Edge_Name"), LocalizationManager.GetString("About_Feature_Edge_Desc")),
                (LocalizationManager.GetString("About_Feature_Opacity_Name"), LocalizationManager.GetString("About_Feature_Opacity_Desc")),
                (LocalizationManager.GetString("About_Feature_Persist_Name"), LocalizationManager.GetString("About_Feature_Persist_Desc")),
                (LocalizationManager.GetString("About_Feature_Tray_Name"), LocalizationManager.GetString("About_Feature_Tray_Desc")),
            };

            foreach (var (name, desc) in features)
            {
                // Feature name
                var nameLabel = new Label
                {
                    Text = "  " + name,
                    Location = new Point(15, yPos),
                    Size = new Size(610, 20),
                    Font = new Font(Font, FontStyle.Bold),
                    ForeColor = Color.FromArgb(52, 73, 94)
                };
                tab.Controls.Add(nameLabel);
                yPos += 20;

                // Feature description
                var descLabel = new Label
                {
                    Text = "      " + desc,
                    Location = new Point(15, yPos),
                    Size = new Size(610, 34),
                    ForeColor = Color.DimGray
                };
                tab.Controls.Add(descLabel);
                yPos += 36;
            }

            yPos += 10;

            // Separator
            var sep = new Panel
            {
                Location = new Point(15, yPos),
                Size = new Size(610, 2),
                BackColor = Color.FromArgb(220, 220, 220)
            };
            tab.Controls.Add(sep);
            yPos += 12;

            // Usage note
            var noteLabel = new Label
            {
                Text = LocalizationManager.GetString("About_UsageNote"),
                Location = new Point(15, yPos),
                Size = new Size(610, 70),
                ForeColor = Color.DimGray,
                Font = new Font(Font, FontStyle.Italic)
            };
            tab.Controls.Add(noteLabel);
        }

        #endregion

        private void AddHorizontalSeparator(TabPage tab, ref int yOffset)
        {
            var separator = new Panel
            {
                Location = new Point(15, yOffset + 3),
                Size = new Size(610, 2),
                BackColor = Color.FromArgb(220, 220, 220)
            };
            tab.Controls.Add(separator);
            yOffset += 12;
        }

        private void AddLabeledControl(GroupBox groupBox, ref int yOffset, string labelText, Control control)
        {
            var label = new Label
            {
                Text = labelText,
                Location = new Point(20, yOffset),
                Size = new Size(170, 22),
                Font = new Font(groupBox.Font, FontStyle.Regular)
            };
            groupBox.Controls.Add(label);
            groupBox.Controls.Add(control);
            yOffset += 28;
        }

        private void LoadSettings()
        {
            if (_opacityNumeric != null)
                _opacityNumeric.Value = (int)((_config.DefaultOpacity / 255.0) * 100);

            if (_edgeThresholdNumeric != null)
                _edgeThresholdNumeric.Value = _config.EdgeThreshold;

            if (_autoSaveIntervalNumeric != null)
                _autoSaveIntervalNumeric.Value = _config.AutoSaveInterval;

            if (_showNotificationsCheckBox != null)
                _showNotificationsCheckBox.Checked = _config.ShowNotifications;

            if (_showPinNotificationsCheckBox != null)
                _showPinNotificationsCheckBox.Checked = _config.ShowPinNotifications;

            if (_showErrorNotificationsCheckBox != null)
                _showErrorNotificationsCheckBox.Checked = _config.ShowErrorNotifications;

            UpdateNotificationDependents();
        }

        private void UpdateNotificationDependents()
        {
            if (_showNotificationsCheckBox != null)
            {
                var enabled = _showNotificationsCheckBox.Checked;
                if (_showPinNotificationsCheckBox != null)
                    _showPinNotificationsCheckBox.Enabled = enabled;
                if (_showErrorNotificationsCheckBox != null)
                    _showErrorNotificationsCheckBox.Enabled = enabled;
            }
        }

        private void AutoSaveOnChange()
        {
            // Auto-save when toggles change
            try
            {
                SaveSettingsInternal();
            }
            catch
            {
                // Ignore errors during auto-save
            }
        }

        private void RefreshPinnedWindows()
        {
            if (_pinnedWindowsList == null || IsDisposed)
                return;

            try
            {
                _pinnedWindowsList.BeginUpdate();
                _pinnedWindowsList.Items.Clear();
                foreach (var window in _pinManager.PinnedWindows)
                {
                    var processName = GetProcessName(window.Handle);
                    _pinnedWindowsList.Items.Add($"[{processName}] {window.Title}");
                }
                _pinnedWindowsList.EndUpdate();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error refreshing pinned windows: {ex.Message}");
            }
        }

        private readonly List<IntPtr> _cachedWindowHandles = new List<IntPtr>();
        private readonly List<string> _cachedWindowTitles = new List<string>();
        private readonly List<string> _cachedProcessNames = new List<string>();

        private void RefreshWindowList()
        {
            if (_windowList == null || IsDisposed)
                return;

            try
            {
                // Cache window data
                _cachedWindowHandles.Clear();
                _cachedWindowTitles.Clear();
                _cachedProcessNames.Clear();

                NativeMethods.EnumWindows((hWnd, lParam) =>
                {
                    if (WindowValidator.IsValidWindow(hWnd))
                    {
                        var title = GetWindowTitle(hWnd);
                        // Hide the app's own settings window from the list. The
                        // title is localized, so compare against the current
                        // localized form title.
                        var ownTitle = LocalizationManager.GetString("Settings_FormTitle");
                        if (!string.IsNullOrEmpty(title) && title != ownTitle && title != AppConfig.AppDisplayName)
                        {
                            _cachedWindowHandles.Add(hWnd);
                            _cachedWindowTitles.Add(title);
                            _cachedProcessNames.Add(GetProcessName(hWnd));
                        }
                    }
                    return true;
                }, IntPtr.Zero);

                FilterWindowList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error refreshing window list: {ex.Message}");
            }
        }

        private void FilterWindowList()
        {
            if (_windowList == null || IsDisposed)
                return;

            var filter = _searchBox?.Text?.Trim().ToLower() ?? "";

            _windowList.BeginUpdate();
            _windowList.Items.Clear();

            for (int i = 0; i < _cachedWindowHandles.Count; i++)
            {
                var title = _cachedWindowTitles[i];
                var processName = _cachedProcessNames[i];

                if (filter.Length > 0)
                {
                    if (!title.ToLower().Contains(filter) && !processName.ToLower().Contains(filter))
                        continue;
                }

                var isPinned = _pinManager.IsPinned(_cachedWindowHandles[i]);
                var prefix = isPinned ? "✓ " : "  ";
                _windowList.Items.Add($"{prefix}[{processName}] {title}");
            }

            _windowList.EndUpdate();
        }

        private string GetProcessName(IntPtr hWnd)
        {
            try
            {
                NativeMethods.GetWindowThreadProcessId(hWnd, out uint processId);
                if (processId > 0)
                {
                    var process = System.Diagnostics.Process.GetProcessById((int)processId);
                    return process.ProcessName;
                }
            }
            catch { }
            return LocalizationManager.GetString("Common_Unknown");
        }

        private string GetWindowTitle(IntPtr hWnd)
        {
            var length = NativeMethods.GetWindowTextLength(hWnd);
            if (length == 0)
                return string.Empty;

            var sb = new System.Text.StringBuilder(length + 1);
            NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private IntPtr? GetWindowFromListIndex(int listIndex)
        {
            var filter = _searchBox?.Text?.Trim().ToLower() ?? "";
            var visibleIndex = 0;

            for (int i = 0; i < _cachedWindowHandles.Count; i++)
            {
                var title = _cachedWindowTitles[i];
                var processName = _cachedProcessNames[i];

                if (filter.Length > 0)
                {
                    if (!title.ToLower().Contains(filter) && !processName.ToLower().Contains(filter))
                        continue;
                }

                if (visibleIndex == listIndex)
                    return _cachedWindowHandles[i];

                visibleIndex++;
            }

            return null;
        }

        private void OnWindowDoubleClick(object? sender, EventArgs e)
        {
            if (_windowList == null || _windowList.SelectedIndex < 0)
                return;

            var targetWindow = GetWindowFromListIndex(_windowList.SelectedIndex);
            if (targetWindow.HasValue)
            {
                _pinManager.TogglePin(targetWindow.Value);
                RefreshWindowList();
            }
        }

        private void OnUnpinSelected(object? sender, EventArgs e)
        {
            if (_pinnedWindowsList?.SelectedIndices == null || _pinnedWindowsList.SelectedIndices.Count == 0)
                return;

            var windowsToUnpin = new List<IntPtr>();
            foreach (int index in _pinnedWindowsList.SelectedIndices)
            {
                var idx = 0;
                foreach (var window in _pinManager.PinnedWindows)
                {
                    if (idx == index)
                    {
                        windowsToUnpin.Add(window.Handle);
                        break;
                    }
                    idx++;
                }
            }

            foreach (var hWnd in windowsToUnpin)
            {
                _pinManager.UnpinWindow(hWnd);
            }
        }

        private void OnSave(object? sender, EventArgs e)
        {
            try
            {
                SaveSettingsInternal();
                MessageBox.Show(
                    LocalizationManager.GetString("Settings_SaveSuccessBody"),
                    LocalizationManager.GetString("Settings_SaveSuccessTitle"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (System.UnauthorizedAccessException)
            {
                MessageBox.Show(
                    LocalizationManager.GetString("Settings_SaveFailedPermissionBody"),
                    LocalizationManager.GetString("Settings_SaveFailedTitle"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    LocalizationManager.GetString("Settings_SaveFailedBody", ex.Message),
                    LocalizationManager.GetString("Settings_SaveFailedTitle"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void SaveSettingsInternal()
        {
            if (_opacityNumeric != null)
                _config.DefaultOpacity = (byte)(_opacityNumeric.Value * 255 / 100);

            if (_edgeThresholdNumeric != null)
                _config.EdgeThreshold = (int)_edgeThresholdNumeric.Value;

            if (_autoSaveIntervalNumeric != null)
                _config.AutoSaveInterval = (int)_autoSaveIntervalNumeric.Value;

            if (_showNotificationsCheckBox != null)
                _config.ShowNotifications = _showNotificationsCheckBox.Checked;

            if (_showPinNotificationsCheckBox != null)
                _config.ShowPinNotifications = _showPinNotificationsCheckBox.Checked;

            if (_showErrorNotificationsCheckBox != null)
                _config.ShowErrorNotifications = _showErrorNotificationsCheckBox.Checked;

            _config.Save();
        }

        private void OnConfigurationChanged(object? sender, EventArgs e)
        {
            LoadSettings();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                _pinManager.WindowsChanged -= OnWindowsChangedInternal;
                _config.ConfigurationChanged -= OnConfigurationChanged;
                LocalizationManager.LanguageChanged -= OnLanguageChanged;
                _refreshTimer?.Stop();
                _refreshTimer?.Dispose();
            }
            catch
            {
                // Ignore errors during cleanup
            }
            base.OnFormClosing(e);
        }
    }
}
