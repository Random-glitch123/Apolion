using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using CefSharp;

namespace Apolion
{
    /// <summary>
    /// A selectable website language. The language is sent to websites through the
    /// Accept-Language HTTP header and exposed to JavaScript as navigator.language.
    /// </summary>
    public sealed class LanguageOption
    {
        public LanguageOption(string code, string displayName)
        {
            Code = code;
            DisplayName = displayName;
        }

        /// <summary>BCP-47 language tag, e.g. "de-DE".</summary>
        public string Code { get; }

        /// <summary>Native-language label shown in the UI.</summary>
        public string DisplayName { get; }

        /// <summary>Primary language subtag, e.g. "de" for "de-DE".</summary>
        public string PrimaryLanguage
        {
            get
            {
                int dash = Code.IndexOf('-');
                return dash > 0 ? Code[..dash] : Code;
            }
        }

        /// <summary>Locale in the underscore form used by the DevTools protocol (e.g. "de_DE").</summary>
        public string IcuLocale => Code.Replace('-', '_');

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// Owns the user's website language choice and applies it to CEF and to pages.
    /// </summary>
    public static class LanguageManager
    {
        public const string DefaultCode = "en-US";

        private static readonly LanguageOption[] _languages =
        [
            new("en-US", "English (United States)"),
            new("de-DE", "Deutsch"),
            new("es-ES", "Español"),
            new("fr-FR", "Français"),
            new("cs-CZ", "Čeština"),
            new("pl-PL", "Polski"),
            new("zh-CN", "中文 (简体)"),
            new("hi-IN", "हिन्दी"),
            new("pt-BR", "Português (Brasil)"),
            new("ar-SA", "العربية")
        ];

        public static IReadOnlyList<LanguageOption> Languages => _languages;

        private static LanguageOption _current = _languages[0];
        private static string _acceptLanguageList = BuildAcceptLanguageList(_current);
        private static volatile string _acceptLanguageHeader = BuildAcceptLanguageHeader(_current);
        
        // Cache file timestamp to avoid unnecessary reads
        private static DateTime _languageFileLastWrite = DateTime.MinValue;

        static LanguageManager()
        {
            ApplyCode(SettingsManager.GetLanguage());
        }

        public static LanguageOption Current => _current;

        public static string CurrentCode => _current.Code;

        /// <summary>Ordered, comma-separated language list for CefSettings/BrowserSettings.</summary>
        public static string AcceptLanguageList => _acceptLanguageList;

        /// <summary>Full Accept-Language header value with quality factors.</summary>
        public static string AcceptLanguageHeader => _acceptLanguageHeader;

        /// <summary>Locale in the form expected by Emulation.setLocaleOverride.</summary>
        public static string IcuLocale => _current.IcuLocale;

        /// <summary>
        /// Script injected into every frame's JS context before page scripts run, so
        /// navigator.language/languages reflect the chosen website language even on the
        /// very first navigation.
        /// </summary>
        public static string NavigatorOverrideScript
        {
            get
            {
                var langs = BuildLanguageOrder(_current);
                string langJson = JsonSerializer.Serialize(_current.Code);
                string langsJson = JsonSerializer.Serialize(langs);

                return "(function(){" +
                       "if(window.__apolionLanguageInstalled)return;" +
                       "window.__apolionLanguageInstalled=true;" +
                       "var lang=" + langJson + ";" +
                       "var langs=" + langsJson + ";" +
                       "try{" +
                       "Object.defineProperty(Navigator.prototype,'language',{get:function(){return lang;},configurable:true});" +
                       "Object.defineProperty(Navigator.prototype,'languages',{get:function(){return langs.slice();},configurable:true});" +
                       "}catch(e){}" +
                       "})();";
            }
        }

        public static bool IsSupported(string? code) => Find(code) != null;

        public static LanguageOption GetOption(string? code)
            => Find(code) ?? Find(DefaultCode)!;

        /// <summary>
        /// Best-effort suggestion for the initial picker. This is only used to preselect
        /// an item on first launch; it is never applied unless the user accepts it.
        /// </summary>
        public static string SuggestDefaultCode()
        {
            try
            {
                var culture = CultureInfo.InstalledUICulture;

                var exact = Find(culture.Name);
                if (exact != null) return exact.Code;

                string primary = culture.TwoLetterISOLanguageName.ToLowerInvariant();

                var languageMatch = _languages.FirstOrDefault(
                    l => string.Equals(l.PrimaryLanguage, primary, StringComparison.OrdinalIgnoreCase));
                if (languageMatch != null) return languageMatch.Code;
            }
            catch
            {
                // Fall through to the neutral default.
            }

            return DefaultCode;
        }

        /// <summary>
        /// Initializes language from settings. Only reads the file if it has been modified
        /// since last check (avoids unnecessary I/O on every startup).
        /// </summary>
        public static void Initialize()
        {
            try
            {
                string languageFile = Path.Combine(SettingsManager.GetAppDataFolder(), "language.dat");
                if (File.Exists(languageFile))
                {
                    var lastWrite = File.GetLastWriteTimeUtc(languageFile);
                    if (lastWrite != _languageFileLastWrite)
                    {
                        _languageFileLastWrite = lastWrite;
                        ApplyCode(SettingsManager.GetLanguage());
                    }
                }
                else if (_languageFileLastWrite != DateTime.MinValue)
                {
                    // File was deleted, reset to default
                    _languageFileLastWrite = DateTime.MinValue;
                    ApplyCode(null);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Exception("LanguageManager.Initialize", ex);
            }
        }

        /// <summary>Persists a new website language choice and refreshes the cached headers.</summary>
        public static void SetLanguage(string? code)
        {
            var option = Find(code) ?? Find(DefaultCode)!;
            ApplyCode(option.Code);
            SettingsManager.SetLanguage(option.Code);
            
            // Update cache timestamp
            try
            {
                string languageFile = Path.Combine(SettingsManager.GetAppDataFolder(), "language.dat");
                if (File.Exists(languageFile))
                {
                    _languageFileLastWrite = File.GetLastWriteTimeUtc(languageFile);
                }
            }
            catch { }
        }

        /// <summary>
        /// Updates Chromium's global accept-language preference at runtime. Requests
        /// already in flight are unaffected; new requests use the new language.
        /// </summary>
        public static void ApplyLive()
        {
            try
            {
                Cef.UIThreadTaskFactory.StartNew(() =>
                {
                    try
                    {
                        var context = Cef.GetGlobalRequestContext();
                        if (context != null && context.CanSetPreference("intl.accept_languages"))
                        {
                            context.SetPreference("intl.accept_languages", AcceptLanguageList, out _);
                        }
                    }
                    catch
                    {
                        // CEF may not be initialized (e.g. while the EULA is shown).
                    }
                });
            }
            catch
            {
                // CEF may not be initialized yet.
            }
        }

        private static void ApplyCode(string? code)
        {
            _current = Find(code) ?? Find(DefaultCode)!;
            _acceptLanguageList = BuildAcceptLanguageList(_current);
            _acceptLanguageHeader = BuildAcceptLanguageHeader(_current);
        }

        private static LanguageOption? Find(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            return _languages.FirstOrDefault(
                l => string.Equals(l.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private static List<string> BuildLanguageOrder(LanguageOption option)
        {
            var order = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string tag)
            {
                if (!string.IsNullOrWhiteSpace(tag) && seen.Add(tag))
                    order.Add(tag);
            }

            Add(option.Code);

            // Google treats "en-US" as an implicit default and can fall back to
            // the IP country's language (e.g. Dutch from a Netherlands IP). An
            // explicit en-GB entry makes English win while en-US stays primary.
            if (option.Code.Equals("en-US", StringComparison.OrdinalIgnoreCase))
                Add("en-GB");

            // Give Chinese readers a script hint so servers pick the right variant.
            if (option.Code.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase))
                Add("zh-Hant");
            else if (option.Code.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase))
                Add("zh-Hans");

            Add(option.PrimaryLanguage);

            // English is a widely understood fallback, but never displace the selection.
            if (!option.Code.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            {
                Add("en-US");
                Add("en");
            }
            else
            {
                if (!option.Code.Equals("en-US", StringComparison.OrdinalIgnoreCase))
                    Add("en-US");
                Add("en");
            }

            return order;
        }

        private static string BuildAcceptLanguageList(LanguageOption option)
            => string.Join(",", BuildLanguageOrder(option));

        private static string BuildAcceptLanguageHeader(LanguageOption option)
        {
            var order = BuildLanguageOrder(option);
            var parts = new List<string>(order.Count);
            double quality = 1.0;

            for (int i = 0; i < order.Count; i++)
            {
                if (i == 0)
                {
                    parts.Add(order[i]);
                }
                else
                {
                    quality = Math.Max(0.1, quality - 0.1);
                    parts.Add($"{order[i]};q={quality:0.0}");
                }
            }

            return string.Join(",", parts);
        }
    }
}

