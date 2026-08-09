using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WindowTopTool
{
    /// <summary>
    /// Application entry point
    /// </summary>
    internal static class Program
    {
        private const string MutexName = "WindowTopTool_SingleInstance_Mutex";

        [STAThread]
        static void Main()
        {
            // Single instance check
            using var mutex = new Mutex(true, MutexName, out var createdNew);

            if (!createdNew)
            {
                // Another instance is already running. Log the violation and
                // activate the existing window instead of starting a new one.
                AppLogger.Configure(AppConfig.Instance.LocalLogEnabled, AppConfig.Instance.PpcLogLevel);
                AppLogger.Warn(
                    "Another instance is already running; activating existing instance",
                    PpcErrorCodes.ErrorSingleInstanceViolation);
                ActivateExistingInstance();
                return;
            }

            // Before touching AppConfig, check whether a previously-migrated
            // config exists under a detected PPC installation directory. This
            // makes the app read from <ppc_path>/app/config/ on subsequent
            // launches instead of the legacy %AppData% location.
            // Skipped in portable mode — the config lives next to the exe and
            // PPC is never used.
            if (!AppConfig.IsPortable)
            {
                AppConfig.TryDetectPpcConfigPath();
            }

            // Push the logging configuration into AppLogger before any other
            // component runs, so failure paths below are captured. Safe because
            // AppConfig.Load never calls back into AppLogger.
            AppLogger.Configure(AppConfig.Instance.LocalLogEnabled, AppConfig.Instance.PpcLogLevel);

            if (AppConfig.IsPortable)
            {
                AppLogger.Info(
                    "Running in portable mode — PPC is disabled; config and logs are stored next to the executable");
            }

            // Initialize localization before any user-facing message so the
            // admin prompt and runtime-error dialog respect the system language.
            LocalizationManager.Initialize();

            // Check if running as administrator
            if (!IsRunningAsAdministrator())
            {
                AppLogger.Warn(
                    "Application is not running as administrator; some windows may be unpinnable",
                    PpcErrorCodes.ErrorAdminRequired);
                // Prompt user
                var result = MessageBox.Show(
                    LocalizationManager.GetString("Program_AdminPromptBody"),
                    LocalizationManager.GetString("Program_AdminPromptTitle"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1
                );

                if (result == DialogResult.Yes)
                {
                    RestartAsAdministrator();
                    return;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    LocalizationManager.GetString("Program_RunErrorBody", ex.Message),
                    LocalizationManager.GetString("Program_RunErrorTitle"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }

        /// <summary>
        /// Check if running with administrator privileges
        /// </summary>
        private static bool IsRunningAsAdministrator()
        {
            try
            {
                var identity = new System.Security.Principal.WindowsPrincipal(
                    System.Security.Principal.WindowsIdentity.GetCurrent()
                );
                return identity.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Restart application with administrator privileges
        /// </summary>
        private static void RestartAsAdministrator()
        {
            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(processInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    LocalizationManager.GetString("Program_RestartAdminFailedBody", ex.Message),
                    LocalizationManager.GetString("Program_RestartAdminFailedTitle"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }

        /// <summary>
        /// Activate existing instance window
        /// </summary>
        private static void ActivateExistingInstance()
        {
            try
            {
                // Find existing process
                var currentProcess = Process.GetCurrentProcess();
                var processes = Process.GetProcessesByName(currentProcess.ProcessName);

                foreach (var process in processes)
                {
                    if (process.Id != currentProcess.Id)
                    {
                        // Found existing instance
                        var hWnd = process.MainWindowHandle;
                        if (hWnd != IntPtr.Zero)
                        {
                            NativeMethods.SetForegroundWindow(hWnd);
                            NativeMethods.ShowWindow(hWnd, 9); // SW_RESTORE
                        }
                        break;
                    }
                }
            }
            catch
            {
                // Ignore errors
            }
        }
    }
}