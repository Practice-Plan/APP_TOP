using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace WindowTopTool.WinUI;

public sealed partial class SettingsView : UserControl
{
    private WindowPinManager _pinManager = null!;
    private readonly AppConfig _config = AppConfig.Instance;
    private DispatcherQueue _dispatcher = null!;
    private DispatcherQueueTimer? _windowRefreshTimer;
    private readonly List<WindowEntry> _availableWindows = new();
    private List<WindowEntry> _visibleWindows = new();
    private List<WindowEntry> _pinnedWindows = new();
    private ListView _pinnedList = null!;
    private ListView _windowList = null!;
    private TextBox _searchBox = null!;
    private NumberBox _opacityBox = null!;
    private NumberBox _edgeBox = null!;
    private NumberBox _autoSaveBox = null!;
    private NumberBox _pipWidthBox = null!;
    private NumberBox _pipHeightBox = null!;
    private ToggleSwitch _notifications = null!;
    private ToggleSwitch _pinNotifications = null!;
    private ToggleSwitch _errorNotifications = null!;
    private ComboBox _language = null!;
    private InfoBar _saveStatus = null!;
    private string _activePage = "windows";
    private bool _loading;
    private bool _isUnloaded;
    private bool _initialized;

    public event EventHandler? CloseRequested;

    /// <summary>
    /// Parameterless constructor used by XAML. Follow up with
    /// <see cref="Initialize"/> once the dependency is available.
    /// </summary>
    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Full constructor (for programmatic use when XAML is not involved).
    /// </summary>
    public SettingsView(WindowPinManager pinManager) : this()
    {
        Initialize(pinManager);
    }

    /// <summary>
    /// Second-phase initialization. Safe to call exactly once after the
    /// parameterless constructor. Sets up event subscriptions, timers and
    /// the initial page state.
    /// </summary>
    public void Initialize(WindowPinManager pinManager)
    {
        if (_initialized) return;
        _initialized = true;
        _pinManager = pinManager;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        BuildWindowsPage();
        BuildGeneralPage();
        ApplyLocalization();
        LoadSettings();
        // Setting SelectedItem triggers OnNavigationSelectionChanged, which
        // builds and assigns PageContent.Content. Do NOT assign it manually
        // here — that would add the already-parented _pinnedList/_windowList
        // to a second StackPanel and throw 0x800F1000.
        SettingsNavigationView.SelectedItem = WindowsItem;

        // Defer the first window-list refresh until after the visual tree has
        // been constructed; modifying the bound ObservableCollection before the
        // ListView is in the tree can throw a transient COMException.
        _dispatcher.TryEnqueue(RefreshWindowLists);

        _pinManager.WindowsChanged += OnWindowsChanged;
        _config.ConfigurationChanged += OnConfigurationChanged;
        LocalizationManager.LanguageChanged += OnLanguageChanged;
        Unloaded += OnViewUnloaded;

        _windowRefreshTimer = _dispatcher.CreateTimer();
        _windowRefreshTimer.Interval = TimeSpan.FromSeconds(2);
        _windowRefreshTimer.Tick += (_, _) =>
        {
            if (_activePage == "windows")
                RefreshWindowLists();
        };
        _windowRefreshTimer.Start();
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
        _windowRefreshTimer?.Stop();
        _pinManager.WindowsChanged -= OnWindowsChanged;
        _config.ConfigurationChanged -= OnConfigurationChanged;
        LocalizationManager.LanguageChanged -= OnLanguageChanged;
    }

    private void BuildWindowsPage()
    {
        _pinnedList = new ListView
        {
            Height = 130,
            SelectionMode = ListViewSelectionMode.Multiple,
            ItemsSource = _pinnedWindows,
            DisplayMemberPath = nameof(WindowEntry.Display)
        };
        _pinnedList.DoubleTapped += OnPinnedDoubleTapped;

        _searchBox = new TextBox
        {
            PlaceholderText = LocalizationManager.GetString("Settings_SearchPlaceholder"),
            MinWidth = 220
        };
        _searchBox.TextChanged += (_, _) => FilterAvailableWindows();

        _windowList = new ListView
        {
            Height = 310,
            SelectionMode = ListViewSelectionMode.Single,
            ItemsSource = _visibleWindows,
            DisplayMemberPath = nameof(WindowEntry.Display)
        };
        _windowList.DoubleTapped += OnAvailableDoubleTapped;
    }

    private UIElement BuildWindowsPageContent()
    {
        var content = CreatePageStack();
        content.Children.Add(CreatePageTitle(LocalizationManager.GetString("Settings_TabWindow")));

        var pinnedHeader = CreateToolbar(LocalizationManager.GetString("Settings_PinnedWindows"));
        var unpinSelected = CreateButton("Settings_UnpinSelected");
        unpinSelected.Click += (_, _) => UnpinSelected();
        var unpinAll = CreateButton("Settings_UnpinAll");
        unpinAll.Click += (_, _) => RunWindowAction(_pinManager.UnpinAll, "Failed to unpin all windows");
        pinnedHeader.Children.Add(unpinSelected);
        pinnedHeader.Children.Add(unpinAll);
        content.Children.Add(pinnedHeader);
        content.Children.Add(_pinnedList);

        content.Children.Add(CreateSeparator());
        var availableHeader = CreateToolbar(LocalizationManager.GetString("Settings_AvailableWindows"));
        var refresh = CreateButton("Settings_RefreshList");
        refresh.Click += (_, _) => RefreshWindowLists();
        availableHeader.Children.Add(refresh);
        content.Children.Add(availableHeader);
        content.Children.Add(_searchBox);
        content.Children.Add(_windowList);
        return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private void BuildGeneralPage()
    {
        _opacityBox = CreateNumberBox(20, 100, 1);
        _edgeBox = CreateNumberBox(5, 50, 1);
        _autoSaveBox = CreateNumberBox(10, 300, 5);
        _pipWidthBox = CreateNumberBox(120, 600, 10);
        _pipHeightBox = CreateNumberBox(80, 500, 10);
        _notifications = CreateToggle("Settings_EnableNotifications");
        _pinNotifications = CreateToggle("Settings_ShowPinNotifications");
        _errorNotifications = CreateToggle("Settings_ShowErrorNotifications");
        _language = CreateLanguageSelector();

        _notifications.Toggled += (_, _) =>
        {
            _pinNotifications.IsEnabled = _notifications.IsOn;
            _errorNotifications.IsEnabled = _notifications.IsOn;
            SaveSettings(false);
        };
        _pinNotifications.Toggled += (_, _) => SaveSettings(false);
        _errorNotifications.Toggled += (_, _) => SaveSettings(false);
        _language.SelectionChanged += OnLanguageSelectionChanged;

        _saveStatus = new InfoBar { IsOpen = false, IsClosable = true };
    }

    private UIElement BuildGeneralPageContent()
    {
        // Two-row layout: the settings content scrolls in a height-bounded
        // area (Star row) while the action buttons live in a persistent
        // footer (Auto row) that is always visible at the bottom of the page.
        // Previously the buttons were the last children of a single
        // ScrollViewer and got clipped below the window's bottom edge, so the
        // user could only ever see down to the notification toggles.
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var content = CreatePageStack();
        content.Children.Add(CreatePageTitle(LocalizationManager.GetString("Settings_TabGeneral")));
        content.Children.Add(CreateSectionTitle(LocalizationManager.GetString("Settings_WindowSettings")));
        content.Children.Add(CreateSettingRow(LocalizationManager.GetString("Settings_DefaultOpacity"), _opacityBox));
        content.Children.Add(CreateSettingRow(LocalizationManager.GetString("Settings_EdgeThreshold"), _edgeBox));
        content.Children.Add(CreateSettingRow(LocalizationManager.GetString("Settings_AutoSaveInterval"), _autoSaveBox));
        content.Children.Add(CreateSettingRow(LocalizationManager.GetString("Settings_PipWidth"), _pipWidthBox));
        content.Children.Add(CreateSettingRow(LocalizationManager.GetString("Settings_PipHeight"), _pipHeightBox));
        content.Children.Add(CreateSeparator());
        content.Children.Add(CreateSectionTitle(LocalizationManager.GetString("Settings_NotificationSettings")));
        content.Children.Add(_notifications);
        content.Children.Add(_pinNotifications);
        content.Children.Add(_errorNotifications);
        content.Children.Add(CreateSeparator());
        content.Children.Add(CreateSettingRow(LocalizationManager.GetString("Settings_LanguageLabel"), _language));

        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 0);
        root.Children.Add(scroll);

        var footer = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
            Margin = new Thickness(24, 0, 24, 18)
        };
        footer.Children.Add(_saveStatus);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var save = CreateButton("Settings_Save", true);
        save.Click += (_, _) => SaveSettings(true);
        var close = CreateButton("Settings_Close");
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        var restartAdmin = CreateButton("Settings_RestartAsAdmin");
        restartAdmin.Click += (_, _) => RestartAsAdministrator();
        // Hide the "restart as admin" button when we are already elevated —
        // there is nothing to gain from running the same process again with
        // the same privileges.
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            if (principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                restartAdmin.Visibility = Visibility.Collapsed;
        }
        catch { /* do nothing — show the button and let RestartAsAdministrator handle it */ }
        actions.Children.Add(save);
        actions.Children.Add(close);
        actions.Children.Add(restartAdmin);
        footer.Children.Add(actions);

        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        return root;
    }

    private UIElement BuildAboutPageContent()
    {
        var content = CreatePageStack();
        content.Children.Add(CreatePageTitle($"Window Top Tool  v{AppConfig.AssemblyVersion}"));
        content.Children.Add(CreateWrappedText(LocalizationManager.GetString("About_Subtitle")));
        content.Children.Add(CreateSectionTitle(LocalizationManager.GetString("About_FeaturesHeader")));

        var features = new[]
        {
            ("About_Feature_Pin_Name", "About_Feature_Pin_Desc"),
            ("About_Feature_ClickThrough_Name", "About_Feature_ClickThrough_Desc"),
            ("About_Feature_Mini_Name", "About_Feature_Mini_Desc"),
            ("About_Feature_PiP_Name", "About_Feature_PiP_Desc"),
            ("About_Feature_Edge_Name", "About_Feature_Edge_Desc"),
            ("About_Feature_Opacity_Name", "About_Feature_Opacity_Desc"),
            ("About_Feature_Persist_Name", "About_Feature_Persist_Desc"),
            ("About_Feature_Tray_Name", "About_Feature_Tray_Desc")
        };
        foreach (var (name, description) in features)
        {
            content.Children.Add(CreateSectionTitle(LocalizationManager.GetString(name)));
            content.Children.Add(CreateWrappedText(LocalizationManager.GetString(description)));
        }
        content.Children.Add(CreateSeparator());
        content.Children.Add(CreateWrappedText(LocalizationManager.GetString("About_UsageNote")));
        return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string page)
            return;

        _activePage = page;
        PageContent.Content = null;
        PageContent.Content = page switch
        {
            // Rebuild (not just re-add) because the page controls are fields
            // already parented to the previous page's StackPanel.
            "general" => RebuildGeneralPage(),
            "about" => BuildAboutPageContent(),
            _ => RebuildWindowsPage(_searchBox?.Text ?? string.Empty)
        };
        if (page == "general")
            LoadSettings();
        else if (page == "windows")
            RefreshWindowLists();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            var searchText = _searchBox?.Text ?? string.Empty;
            PageContent.Content = null;
            ApplyLocalization();
            PageContent.Content = _activePage switch
            {
                "general" => RebuildGeneralPage(),
                "about" => BuildAboutPageContent(),
                _ => RebuildWindowsPage(searchText)
            };
            if (_activePage == "general")
                LoadSettings();
        });
    }

    private UIElement RebuildGeneralPage()
    {
        BuildGeneralPage();
        return BuildGeneralPageContent();
    }

    private UIElement RebuildWindowsPage(string searchText)
    {
        BuildWindowsPage();
        _searchBox.Text = searchText;
        return BuildWindowsPageContent();
    }

    private void ApplyLocalization()
    {
        WindowsItem.Content = LocalizationManager.GetString("Settings_TabWindow");
        GeneralItem.Content = LocalizationManager.GetString("Settings_TabGeneral");
        AboutItem.Content = LocalizationManager.GetString("Settings_TabAbout");
        FlowDirection = LocalizationManager.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        if (_searchBox != null)
            _searchBox.PlaceholderText = LocalizationManager.GetString("Settings_SearchPlaceholder");
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _language.SelectedItem is not ComboBoxItem item)
            return;
        LocalizationManager.SetLanguage(item.Tag?.ToString() ?? string.Empty);
    }

    private void OnConfigurationChanged(object? sender, EventArgs e) => RunOnUiThread(LoadSettings);

    private void OnWindowsChanged(object? sender, EventArgs e) => RunOnUiThread(RefreshWindowLists);

    private void RunOnUiThread(Action action)
    {
        if (_isUnloaded)
            return;

        if (_dispatcher.HasThreadAccess)
        {
            action();
            return;
        }
        if (!_dispatcher.TryEnqueue(() => action()))
            AppLogger.Warn("Could not enqueue a settings view update on the WinUI dispatcher");
    }

    private void RefreshWindowLists()
    {
        try { RefreshPinnedWindows(); }
        catch (Exception ex) { AppLogger.Warn($"RefreshPinnedWindows: 0x{ex.HResult:X8}"); }

        try
        {
            _availableWindows.Clear();
            NativeMethods.EnumWindows((hWnd, _) =>
            {
                try
                {
                    if (!WindowValidator.IsValidWindow(hWnd))
                        return true;
                    var title = GetWindowTitle(hWnd);
                    if (string.IsNullOrWhiteSpace(title) ||
                        title == LocalizationManager.GetString("Settings_FormTitle") ||
                        title == AppConfig.AppDisplayName)
                        return true;
                    _availableWindows.Add(new WindowEntry(hWnd, title, GetProcessName(hWnd), _pinManager.IsPinned(hWnd)));
                }
                catch { /* skip problematic windows */ }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { AppLogger.Warn($"EnumWindows: 0x{ex.HResult:X8}"); }

        try { FilterAvailableWindows(); }
        catch (Exception ex) { AppLogger.Warn($"FilterAvailableWindows: 0x{ex.HResult:X8}"); }
    }

    private void RefreshPinnedWindows()
    {
        if (_pinnedList == null)
            return;
        // Build a fresh list and assign it directly. Modifying a bound
        // ObservableCollection in place throws E_UNEXPECTED (0x8000FFFF) in
        // WinUI3, so we swap the whole collection instead.
        var list = new List<WindowEntry>();
        foreach (var window in _pinManager.PinnedWindows)
            list.Add(new WindowEntry(window.Handle, window.Title, GetProcessName(window.Handle), true));
        _pinnedWindows = list;
        _pinnedList.ItemsSource = _pinnedWindows;
    }

    private void FilterAvailableWindows()
    {
        if (_windowList == null)
            return;
        var filter = _searchBox.Text.Trim();
        var list = new List<WindowEntry>();
        foreach (var entry in _availableWindows)
        {
            if (filter.Length == 0 ||
                entry.Title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                entry.Process.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                list.Add(entry);
        }
        _visibleWindows = list;
        _windowList.ItemsSource = _visibleWindows;
    }

    private void OnAvailableDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_windowList.SelectedItem is not WindowEntry window)
            return;
        RunWindowAction(() => _pinManager.TogglePin(window.Handle), "Failed to pin or unpin a window");
        RefreshWindowLists();
    }

    private void OnPinnedDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_pinnedList.SelectedItem is not WindowEntry window)
            return;
        RunWindowAction(() => _pinManager.UnpinWindow(window.Handle), "Failed to unpin a window");
        RefreshWindowLists();
    }

    private void UnpinSelected()
    {
        var selected = _pinnedList.SelectedItems.Cast<WindowEntry>().ToArray();
        RunWindowAction(() =>
        {
            foreach (var window in selected)
                _pinManager.UnpinWindow(window.Handle);
        }, "Failed to unpin selected windows");
        RefreshWindowLists();
    }

    private void RunWindowAction(Action action, string errorMessage)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLogger.Error(errorMessage, ex);
            ShowSaveStatus(LocalizationManager.GetString("Tray_OperationError"), true);
        }
    }

    private void LoadSettings()
    {
        if (_opacityBox == null)
            return;
        _loading = true;
        try
        {
            _opacityBox.Value = Math.Clamp((int)(_config.DefaultOpacity * 100.0 / 255.0), 20, 100);
            _edgeBox.Value = Math.Clamp(_config.EdgeThreshold, 5, 50);
            _autoSaveBox.Value = Math.Clamp(_config.AutoSaveInterval, 10, 300);
            _pipWidthBox.Value = Math.Clamp(_config.PiPWidth, 120, 600);
            _pipHeightBox.Value = Math.Clamp(_config.PiPHeight, 80, 500);
            _notifications.IsOn = _config.ShowNotifications;
            _pinNotifications.IsOn = _config.ShowPinNotifications;
            _errorNotifications.IsOn = _config.ShowErrorNotifications;
            _pinNotifications.IsEnabled = _config.ShowNotifications;
            _errorNotifications.IsEnabled = _config.ShowNotifications;
            SelectLanguage(LocalizationManager.CurrentLanguage);
        }
        finally
        {
            _loading = false;
        }
    }

    private void SaveSettings(bool showSuccess)
    {
        if (_loading)
            return;
        try
        {
            var opacity = ReadNumber(_opacityBox, 20, 100, (int)(_config.DefaultOpacity * 100.0 / 255.0));
            _config.DefaultOpacity = (byte)(opacity * 255 / 100);
            _config.EdgeThreshold = ReadNumber(_edgeBox, 5, 50, _config.EdgeThreshold);
            _config.AutoSaveInterval = ReadNumber(_autoSaveBox, 10, 300, _config.AutoSaveInterval);
            _config.PiPWidth = ReadNumber(_pipWidthBox, 120, 600, _config.PiPWidth);
            _config.PiPHeight = ReadNumber(_pipHeightBox, 80, 500, _config.PiPHeight);
            _config.ShowNotifications = _notifications.IsOn;
            _config.ShowPinNotifications = _pinNotifications.IsOn;
            _config.ShowErrorNotifications = _errorNotifications.IsOn;
            _config.Save();
            if (showSuccess)
                ShowSaveStatus(LocalizationManager.GetString("Settings_SaveSuccessBody"));
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLogger.Error("Permission denied while saving settings", ex);
            ShowSaveStatus(LocalizationManager.GetString("Settings_SaveFailedPermissionBody"), true);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to save settings", ex, PpcErrorCodes.ErrorConfigSaveFailed);
            ShowSaveStatus(LocalizationManager.GetString("Settings_SaveFailedBody", ex.Message), true);
        }
    }

    private void ShowSaveStatus(string message, bool isError = false)
    {
        if (_saveStatus == null)
            return;
        _saveStatus.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        _saveStatus.Message = message;
        _saveStatus.IsOpen = true;
    }

    /// <summary>
    /// Restart the application with administrator privileges. If the process
    /// is already elevated, show a non-blocking notice instead. Any unsaved
    /// settings are persisted before relaunching.
    /// </summary>
    private void RestartAsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            if (principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                ShowSaveStatus(LocalizationManager.GetString("Settings_AlreadyRunningAsAdmin"));
                return;
            }

            // Persist current settings so the elevated instance picks them up.
            try { _config.Save(); } catch { /* best effort */ }

            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                          ?? Environment.ProcessPath
                          ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };

            System.Diagnostics.Process.Start(psi);

            // Hand off to the elevated instance and quit the current one.
            App.Current.ExitApp();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User cancelled the UAC prompt — nothing to do.
            AppLogger.Info("User cancelled the elevation prompt.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to restart as administrator", ex);
            ShowSaveStatus(LocalizationManager.GetString("Program_RestartAdminFailedBody", ex.Message), true);
        }
    }

    private static int ReadNumber(NumberBox box, int minimum, int maximum, int fallback)
    {
        if (!double.IsFinite(box.Value))
            return fallback;
        return (int)Math.Clamp(Math.Round(box.Value), minimum, maximum);
    }

    private void SelectLanguage(string language)
    {
        foreach (var item in _language.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), language, StringComparison.OrdinalIgnoreCase))
            {
                _language.SelectedItem = item;
                return;
            }
        }
        _language.SelectedIndex = 0;
    }

    private static StackPanel CreatePageStack() => new()
    {
        Spacing = 12,
        Margin = new Thickness(24, 18, 24, 18)
    };

    private static TextBlock CreatePageTitle(string text) => new()
    {
        Text = text,
        FontSize = 26,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap
    };

    private static TextBlock CreateSectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 2)
    };

    private static TextBlock CreateWrappedText(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.78
    };

    private static NumberBox CreateNumberBox(double min, double max, double step) => new()
    {
        Minimum = min,
        Maximum = max,
        SmallChange = step,
        LargeChange = step * 5,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        Width = 180,
        HorizontalAlignment = HorizontalAlignment.Right
    };

    private static ToggleSwitch CreateToggle(string key) => new()
    {
        Header = LocalizationManager.GetString(key),
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private ComboBox CreateLanguageSelector()
    {
        var combo = new ComboBox { MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (name, code) in new[]
        {
            ("English", "en"), ("中文", "zh"), ("Français", "fr"), ("Русский", "ru"), ("العربية", "ar")
        })
            combo.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        return combo;
    }

    private static Grid CreateSettingRow(string label, FrameworkElement control)
    {
        var row = new Grid { ColumnSpacing = 16, MinHeight = 42 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        row.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private static StackPanel CreateToolbar(string title)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(CreateSectionTitle(title));
        return row;
    }

    private static Button CreateButton(string key, bool primary = false)
    {
        var text = LocalizationManager.GetString(key);
        var button = new Button { Content = text, MinWidth = 96 };
        if (primary)
        {
            button.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 20, 105, 170));
            button.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 255, 255, 255));
        }
        return button;
    }

    private static Border CreateSeparator() => new()
    {
        Height = 1,
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(48, 0, 0, 0)),
        Margin = new Thickness(0, 6, 0, 6)
    };

    private string GetProcessName(IntPtr hWnd)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out var processId);
            if (processId > 0)
                return Process.GetProcessById((int)processId).ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLogger.Debug($"Could not resolve process for window: {ex.Message}");
        }
        return LocalizationManager.GetString("Common_Unknown");
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var length = NativeMethods.GetWindowTextLength(hWnd);
        if (length <= 0)
            return string.Empty;
        var title = new System.Text.StringBuilder(length + 1);
        NativeMethods.GetWindowText(hWnd, title, title.Capacity);
        return title.ToString();
    }

    private sealed record WindowEntry(IntPtr Handle, string Title, string Process, bool IsPinned)
    {
        public string Display => $"{(IsPinned ? "✓ " : string.Empty)}[{Process}] {Title}";
    }
}
