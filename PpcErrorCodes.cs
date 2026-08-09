using System;
using System.Collections.Generic;

namespace WindowTopTool
{
    /// <summary>
    /// Mirrors the PPC server's <c>app_code.json</c> error/status/warning code
    /// registry as compile-time C# constants.
    ///
    /// The PPC server (Rust binary) is the single source of truth: every code
    /// here MUST match the corresponding entry in
    /// <c>k:\Practice Plan\main\src\app_code.json</c>. Keeping a client-side
    /// mirror avoids a runtime path dependency on the server's JSON file while
    /// preserving a stable contract between the two binaries.
    ///
    /// Code classification (by the first hex digit after <c>0x</c>):
    /// <list type="bullet">
    /// <item><c>0x0xxxx</c> — success</item>
    /// <item><c>0x1xxxx</c> — error</item>
    /// <item><c>0x2xxxx</c> — status</item>
    /// <item><c>0x3xxxx</c> — warning</item>
    /// </list>
    /// </summary>
    public static class PpcErrorCodes
    {
        // ------------------------------------------------------------------
        // Success (0x0xxxx)
        // ------------------------------------------------------------------
        public const string Success = "0x00000";
        public const string SuccessRegister = "0x00001";
        public const string SuccessUpdate = "0x00002";
        public const string SuccessDllLoaded = "0x00003";
        public const string SuccessCommandExecuted = "0x00004";

        // ------------------------------------------------------------------
        // Errors (0x1xxxx) — general
        // ------------------------------------------------------------------
        public const string ErrorUnknown = "0x10000";
        public const string ErrorUnsupported = "0x10001";
        public const string ErrorNotFound = "0x10002";
        public const string ErrorSignatureInvalid = "0x10003";
        public const string ErrorHashMismatch = "0x10004";
        public const string ErrorRegistrationFailed = "0x10005";
        public const string ErrorUpdateFailed = "0x10006";
        public const string ErrorAppNotRegistered = "0x10007";
        public const string ErrorInvalidParameters = "0x10008";
        public const string ErrorFileNotFound = "0x10009";
        public const string ErrorPermissionDenied = "0x10010";
        public const string ErrorDllNotFound = "0x10011";
        public const string ErrorDllLoadFailed = "0x10012";
        public const string ErrorConfigParseFailed = "0x10013";
        public const string ErrorDatabaseError = "0x10014";
        public const string ErrorConnectionFailed = "0x10015";
        public const string ErrorTimeout = "0x10016";

        // ------------------------------------------------------------------
        // Errors (0x1xxxx) — TOP_APP production scenarios
        // ------------------------------------------------------------------
        public const string ErrorPpcNotRunning = "0x10017";
        public const string ErrorPpcAuthFailed = "0x10018";
        public const string ErrorPpcVersionUnsupported = "0x10019";
        public const string ErrorPpcResponseInvalid = "0x10020";
        public const string ErrorWindowPinFailed = "0x10021";
        public const string ErrorWindowNotFound = "0x10022";
        public const string ErrorHotkeyRegisterFailed = "0x10023";
        public const string ErrorConfigSaveFailed = "0x10024";
        public const string ErrorConfigLoadFailed = "0x10025";
        public const string ErrorStatePersistenceFailed = "0x10026";
        public const string ErrorAdminRequired = "0x10027";
        public const string ErrorLocalizationLoadFailed = "0x10028";
        public const string ErrorSingleInstanceViolation = "0x10029";
        public const string ErrorHookInstallFailed = "0x10030";
        public const string ErrorLogWriteFailed = "0x10031";
        public const string ErrorDllCallFailed = "0x10032";

        // ------------------------------------------------------------------
        // Status (0x2xxxx)
        // ------------------------------------------------------------------
        public const string StatusWaitingSignature = "0x20000";
        public const string StatusWaitingAppInfo = "0x20001";
        public const string StatusProcessing = "0x20002";
        public const string StatusInitializing = "0x20003";
        public const string StatusLoadingConfig = "0x20004";
        public const string StatusExecutingCommand = "0x20005";
        public const string StatusConnecting = "0x20006";
        public const string StatusRegistering = "0x20007";
        public const string StatusAuthenticating = "0x20008";
        public const string StatusReconnecting = "0x20009";

        // ------------------------------------------------------------------
        // Warnings (0x3xxxx)
        // ------------------------------------------------------------------
        public const string WarningDeprecatedCommand = "0x30000";
        public const string WarningOldVersion = "0x30001";
        public const string WarningHighMemoryUsage = "0x30002";
        public const string WarningPpcReconnect = "0x30003";
        public const string WarningFallbackBehavior = "0x30004";
        public const string WarningLanguageFallback = "0x30005";

        // ------------------------------------------------------------------
        // Classification helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Parse the category digit (the first hex digit after <c>0x</c>) of a
        /// status code. Returns <c>-1</c> when the code is malformed.
        /// </summary>
        private static int CategoryDigit(string code)
        {
            if (string.IsNullOrEmpty(code))
                return -1;
            var s = code.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? code.Substring(2)
                : code;
            if (s.Length == 0)
                return -1;
            return s[0] switch
            {
                >= '0' and <= '9' => s[0] - '0',
                >= 'a' and <= 'f' => s[0] - 'a' + 10,
                >= 'A' and <= 'F' => s[0] - 'A' + 10,
                _ => -1,
            };
        }

        /// <summary>True when the code is a success code (0x0xxxx).</summary>
        public static bool IsSuccess(string code) => CategoryDigit(code) == 0;

        /// <summary>True when the code is an error code (0x1xxxx).</summary>
        public static bool IsError(string code) => CategoryDigit(code) == 1;

        /// <summary>True when the code is a status code (0x2xxxx).</summary>
        public static bool IsStatus(string code) => CategoryDigit(code) == 2;

        /// <summary>True when the code is a warning code (0x3xxxx).</summary>
        public static bool IsWarning(string code) => CategoryDigit(code) == 3;

        // ------------------------------------------------------------------
        // Human-readable descriptions (English; used for logs/debug only —
        // user-facing text is handled by LocalizationManager).
        // ------------------------------------------------------------------

        private static readonly Dictionary<string, string> Descriptions = new()
        {
            [Success] = "Operation successful",
            [SuccessRegister] = "Application registration successful",
            [SuccessUpdate] = "Application update successful",
            [SuccessDllLoaded] = "DLL loaded successfully",
            [SuccessCommandExecuted] = "Command executed successfully",

            [ErrorUnknown] = "Unknown error",
            [ErrorUnsupported] = "Unsupported command",
            [ErrorNotFound] = "Resource not found",
            [ErrorSignatureInvalid] = "Invalid signature",
            [ErrorHashMismatch] = "Hash mismatch",
            [ErrorRegistrationFailed] = "Registration failed",
            [ErrorUpdateFailed] = "Update failed",
            [ErrorAppNotRegistered] = "Application not registered / authentication required",
            [ErrorInvalidParameters] = "Invalid parameters",
            [ErrorFileNotFound] = "File not found",
            [ErrorPermissionDenied] = "Permission denied",
            [ErrorDllNotFound] = "DLL not found",
            [ErrorDllLoadFailed] = "DLL load failed",
            [ErrorConfigParseFailed] = "Configuration parse failed",
            [ErrorDatabaseError] = "Database error",
            [ErrorConnectionFailed] = "Connection failed",
            [ErrorTimeout] = "Operation timeout",

            [ErrorPpcNotRunning] = "PPC server not running",
            [ErrorPpcAuthFailed] = "PPC authentication failed",
            [ErrorPpcVersionUnsupported] = "PPC version unsupported",
            [ErrorPpcResponseInvalid] = "PPC response invalid",
            [ErrorWindowPinFailed] = "Window pin failed",
            [ErrorWindowNotFound] = "Window not found",
            [ErrorHotkeyRegisterFailed] = "Hotkey registration failed",
            [ErrorConfigSaveFailed] = "Config save failed",
            [ErrorConfigLoadFailed] = "Config load failed",
            [ErrorStatePersistenceFailed] = "State persistence failed",
            [ErrorAdminRequired] = "Administrator required",
            [ErrorLocalizationLoadFailed] = "Localization load failed",
            [ErrorSingleInstanceViolation] = "Single instance violation",
            [ErrorHookInstallFailed] = "Hook install failed",
            [ErrorLogWriteFailed] = "Log write failed",
            [ErrorDllCallFailed] = "DLL call failed",

            [StatusWaitingSignature] = "Waiting for signature verification",
            [StatusWaitingAppInfo] = "Waiting for application info",
            [StatusProcessing] = "Processing",
            [StatusInitializing] = "Initializing",
            [StatusLoadingConfig] = "Loading configuration",
            [StatusExecutingCommand] = "Executing command",
            [StatusConnecting] = "Connecting to PPC server",
            [StatusRegistering] = "Registering application with PPC",
            [StatusAuthenticating] = "Authenticating with PPC",
            [StatusReconnecting] = "Reconnecting to PPC",

            [WarningDeprecatedCommand] = "Deprecated command",
            [WarningOldVersion] = "Old version detected",
            [WarningHighMemoryUsage] = "High memory usage",
            [WarningPpcReconnect] = "PPC reconnect",
            [WarningFallbackBehavior] = "Fallback behavior",
            [WarningLanguageFallback] = "Language fallback",
        };

        /// <summary>
        /// Return a short English description for a status code, or the code
        /// itself when no description is registered.
        /// </summary>
        public static string Description(string code)
        {
            if (string.IsNullOrEmpty(code))
                return "(no code)";
            return Descriptions.TryGetValue(code, out var desc) ? desc : code;
        }
    }
}
