using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace WindowTopTool
{
    /// <summary>
    /// Lightweight JSON-based localization manager for TOP_APP.
    ///
    /// Loads one resource file per language from
    /// <c>&lt;app-base&gt;/Localization/strings.&lt;code&gt;.json</c> (flat
    /// <c>{ "key": "value" }</c> map). English (<c>en</c>) is always loaded as
    /// the fallback so a missing translation degrades gracefully instead of
    /// showing a raw key. The active language is chosen by:
    /// <list type="number">
    /// <item><see cref="AppConfig.Language"/> when set (manual override),</item>
    /// <item>otherwise the system UI culture's two-letter ISO name,</item>
    /// <item>falling back to English with a <see cref="PpcErrorCodes.WarningLanguageFallback"/> log entry.</item>
    /// </list>
    ///
    /// Arabic (<c>ar</c>) is treated as right-to-left; callers should apply
    /// <see cref="IsRightToLeft"/> to their forms. Language changes raise
    /// <see cref="LanguageChanged"/> so forms can re-apply text without restart.
    /// </summary>
    public static class LocalizationManager
    {
        /// <summary>Languages with a shipped resource file.</summary>
        private static readonly HashSet<string> SupportedLanguages = new()
        {
            "en", "zh", "fr", "ru", "ar",
        };

        private static readonly object _gate = new object();
        private static Dictionary<string, string> _current = new Dictionary<string, string>();
        private static Dictionary<string, string> _fallback = new Dictionary<string, string>();
        private static string _currentLanguage = "en";
        private static bool _initialized;

        /// <summary>Raised after the active language changes.</summary>
        public static event EventHandler? LanguageChanged;

        /// <summary>The active language code (en/zh/fr/ru/ar).</summary>
        public static string CurrentLanguage
        {
            get
            {
                lock (_gate) { return _currentLanguage; }
            }
        }

        /// <summary>True when the active language is right-to-left (Arabic).</summary>
        public static bool IsRightToLeft => CurrentLanguage == "ar";

        /// <summary>
        /// Resolve the resource directory. Looks for <c>Localization/</c> next
        /// to the running assembly, falling back to the current directory.
        /// </summary>
        private static string ResourceDirectory
        {
            get
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var candidate = Path.Combine(baseDir, "Localization");
                if (Directory.Exists(candidate))
                    return candidate;
                // Fall back to a Localization folder relative to the working
                // directory (useful when running from source without a build).
                return Path.Combine(Directory.GetCurrentDirectory(), "Localization");
            }
        }

        /// <summary>Load a language file into a dictionary. Missing file → empty dict.</summary>
        private static Dictionary<string, string> LoadFile(string code)
        {
            var path = Path.Combine(ResourceDirectory, $"strings.{code}.json");
            if (!File.Exists(path))
                return new Dictionary<string, string>();

            try
            {
                var json = File.ReadAllText(path);
                var map = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                return map ?? new Dictionary<string, string>();
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    $"Failed to load language resource file for '{code}'",
                    ex,
                    PpcErrorCodes.ErrorLocalizationLoadFailed);
                return new Dictionary<string, string>();
            }
        }

        /// <summary>
        /// Pick the initial language: explicit config override, else system
        /// culture, else English.
        /// </summary>
        private static string ResolveInitialLanguage(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var norm = configured.Trim().ToLowerInvariant();
                if (SupportedLanguages.Contains(norm))
                    return norm;
                // Unknown code in config — fall back to English.
                AppLogger.Warn(
                    $"Configured language '{configured}' is not supported; falling back to English",
                    PpcErrorCodes.WarningLanguageFallback);
                return "en";
            }

            // Detect from system UI culture.
            var iso = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            // CultureInfo uses "zh-Hans"/"zh-Hant" for Chinese variants but the
            // two-letter name is "zh" — map directly.
            if (SupportedLanguages.Contains(iso))
                return iso;

            AppLogger.Warn(
                $"System language '{iso}' has no resource file; falling back to English",
                PpcErrorCodes.WarningLanguageFallback);
            return "en";
        }

        /// <summary>
        /// Initialize the manager from <see cref="AppConfig"/> and the system
        /// culture. Safe to call once at startup (from <c>Program.Main</c>).
        /// </summary>
        public static void Initialize()
        {
            if (_initialized)
                return;

            // English is always the fallback.
            _fallback = LoadFile("en");

            var configured = AppConfig.Instance.Language;
            var lang = ResolveInitialLanguage(configured);

            lock (_gate)
            {
                _currentLanguage = lang;
                _current = lang == "en" ? new Dictionary<string, string>(_fallback) : LoadFile(lang);
            }
            _initialized = true;

            AppLogger.Info($"Localization initialized: language='{lang}', strings={_current.Count}");
        }

        /// <summary>
        /// Switch to a supported language at runtime, persist the choice to
        /// <see cref="AppConfig"/>, and raise <see cref="LanguageChanged"/>.
        /// Unsupported codes are ignored.
        /// </summary>
        public static void SetLanguage(string code)
        {
            var norm = (code ?? "").Trim().ToLowerInvariant();
            if (!SupportedLanguages.Contains(norm))
            {
                AppLogger.Warn(
                    $"Ignoring unsupported language code '{code}'",
                    PpcErrorCodes.WarningLanguageFallback);
                return;
            }

            lock (_gate)
            {
                _currentLanguage = norm;
                _current = norm == "en" ? new Dictionary<string, string>(_fallback) : LoadFile(norm);
            }

            // Persist the override so it survives restarts.
            try
            {
                AppConfig.Instance.Language = norm;
                AppConfig.Instance.Save();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to persist language preference", ex);
            }

            AppLogger.Info($"Language switched to '{norm}'");
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// Look up a localized string. Falls back to English, then to
        /// <c>[key]</c> so missing translations are visible during development.
        /// When <paramref name="args"/> are supplied, the result is formatted
        /// with <see cref="string.Format(IFormatProvider, string, object[])"/>
        /// using the invariant culture (stable for placeholders like numbers).
        /// </summary>
        public static string GetString(string key, params object[] args)
        {
            string? found;
            lock (_gate)
            {
                if (!_current.TryGetValue(key, out found) && !_fallback.TryGetValue(key, out found))
                    found = "[" + key + "]";
            }
            string value = found;

            if (args == null || args.Length == 0)
                return value;

            try
            {
                return string.Format(CultureInfo.InvariantCulture, value, args);
            }
            catch (FormatException)
            {
                // A malformed format string should never crash the UI.
                return value;
            }
        }
    }
}
