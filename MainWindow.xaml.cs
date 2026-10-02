using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WindowTopTool;

public sealed partial class MainWindow : Window
{
    private bool _forceClose;
    private bool _closePromptShowing;

    public MainWindow()
    {
        InitializeComponent();
        AppLogger.Info("MainWindow.ctor: start");
        try
        {
            AppWindow.Title = "Window Top Tool";
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 760));

            // Set the title-bar / taskbar icon from the shipped icon.ico.
            var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (File.Exists(iconPath))
                AppWindow.SetIcon(iconPath);
        }
        catch (Exception ex) { AppLogger.Warn($"MainWindow icon setup failed: {ex.Message}"); }

        AppLogger.Info($"MainWindow.ctor: AppWindow.Id={AppWindow.Id}");
        AppLogger.Info($"MainWindow.ctor: ClientSize={AppWindow.ClientSize}");

        // Intercept the window close (X) so we can honour the user's
        // exit / minimize-to-tray preference (see OnAppWindowClosing).
        AppWindow.Closing += OnAppWindowClosing;
        AppLogger.Info("MainWindow.ctor: done");
    }

    public MainWindow(WindowPinManager pinManager) : this()
    {
        AppLogger.Info("MainWindow.ctor(pinManager): initializing SettingsView");
        SettingsView.Initialize(pinManager);
        // "Close window" in the settings footer hides the window back to the
        // tray rather than terminating the app, consistent with the tray's
        // Settings/Exit model for this resident tool.
        SettingsView.CloseRequested += (_, _) =>
        {
            try { AppWindow.Hide(); }
            catch (Exception ex) { AppLogger.Warn($"Hiding main window failed: {ex.Message}"); }
        };
        AppLogger.Info("MainWindow.ctor(pinManager): SettingsView initialized");
    }

    /// <summary>
    /// Handles the window close (X) button. Honours the persisted
    /// <see cref="AppConfig.CloseAction"/> preference; when set to "prompt"
    /// (the default) it asks the user whether to exit or minimize to tray,
    /// optionally remembering the choice.
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // A real shutdown (App.ExitApp → ForceClose) must be allowed through.
        if (_forceClose)
            return;

        switch (AppConfig.Instance.CloseAction)
        {
            case "tray":
                args.Cancel = true;
                try { sender.Hide(); }
                catch (Exception ex) { AppLogger.Warn($"Minimize-to-tray failed: {ex.Message}"); }
                return;
            case "exit":
                // Let the window close normally; the process exiting triggers
                // App's ProcessExit → Shutdown cleanup (state save, tray dispose).
                return;
            default: // "prompt"
                args.Cancel = true;
                _ = ShowClosePromptAsync(sender);
                return;
        }
    }

    private async Task ShowClosePromptAsync(AppWindow sender)
    {
        if (_closePromptShowing)
            return;
        _closePromptShowing = true;
        try
        {
            var remember = new CheckBox
            {
                Content = LocalizationManager.GetString("Close_Remember"),
                Margin = new Thickness(0, 12, 0, 0)
            };
            var body = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = LocalizationManager.GetString("Close_PromptBody"),
                        TextWrapping = TextWrapping.Wrap
                    },
                    remember
                }
            };

            var dialog = new ContentDialog
            {
                XamlRoot = (Content as FrameworkElement)?.XamlRoot,
                Title = LocalizationManager.GetString("Close_Title"),
                Content = body,
                PrimaryButtonText = LocalizationManager.GetString("Close_Exit"),
                SecondaryButtonText = LocalizationManager.GetString("Close_Tray")
            };

            var result = await dialog.ShowAsync();
            string chosen = result switch
            {
                ContentDialogResult.Primary => "exit",
                ContentDialogResult.Secondary => "tray",
                _ => string.Empty // dismissed — keep the window open
            };
            if (chosen.Length == 0)
                return;

            if (remember.IsChecked == true)
            {
                try
                {
                    AppConfig.Instance.CloseAction = chosen;
                    AppConfig.Instance.Save();
                }
                catch (Exception ex) { AppLogger.Error("Failed to persist close preference", ex); }
            }

            if (chosen == "tray")
            {
                try { sender.Hide(); }
                catch (Exception ex) { AppLogger.Warn($"Hiding main window failed: {ex.Message}"); }
            }
            else
            {
                App.Current.ExitApp();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Close prompt failed", ex);
        }
        finally
        {
            _closePromptShowing = false;
        }
    }

    /// <summary>
    /// Bypasses the close-to-tray guard and actually destroys the window.
    /// Used by <see cref="App.ExitApp"/> during a real shutdown.
    /// </summary>
    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }
}
