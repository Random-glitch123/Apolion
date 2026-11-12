using System;
using System.IO;

namespace Apolion
{
    public static class SettingsManager
    {
        private static readonly object DirLock = new();
        private static string? _appDataDir;

        // In-memory cache for frequently accessed settings
        private static string? _cachedTheme;
        private static string? _cachedLanguage;
        private static readonly object CacheLock = new();

        /// <summary>
        /// Resolves the per-user data directory. Falls back to LocalAppData and
        /// finally to a folder next to the executable if roaming profiles are
        /// unavailable (locked-down machines, broken network profiles, ...).
        /// </summary>
        public static string GetAppDataFolder()
        {
            lock (DirLock)
            {
                if (!string.IsNullOrEmpty(_appDataDir)) return _appDataDir;

                foreach (string root in new[]
                         {
                             Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                             Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                         })
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(root)) continue;
                        string dir = Path.Combine(root, "Apolion");
                        Directory.CreateDirectory(dir);
                        _appDataDir = dir;
                        return dir;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"Cannot use data folder under '{root}'", ex);
                    }
                }

                foreach (string fallback in new[]
                         {
                             Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData"),
                             Path.Combine(Path.GetTempPath(), "Apolion")
                         })
                {
                    try
                    {
                        Directory.CreateDirectory(fallback);
                        _appDataDir = fallback;
                        AppLogger.Warn($"Using fallback data folder: {fallback}");
                        return fallback;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"Fallback data folder failed: {fallback}", ex);
                    }
                }

                throw new IOException("No writable Apolion data folder is available.");
            }
        }

        private static string SettingsFile => Path.Combine(GetAppDataFolder(), "user_settings.dat");
        private static string TabsSettingsFile => Path.Combine(GetAppDataFolder(), "saved_tabs.dat");
        private static string LanguageSettingsFile => Path.Combine(GetAppDataFolder(), "language.dat");

        private static void WriteAllTextAtomic(string path, string content)
        {
            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, path, overwrite: true);
        }

        private static void WriteAllLinesAtomic(string path, string[] lines)
        {
            string tempPath = path + ".tmp";
            File.WriteAllLines(tempPath, lines);
            File.Move(tempPath, path, overwrite: true);
        }

        public static void SaveOpenTabs(string[] tabs)
        {
            try
            {
                WriteAllLinesAtomic(TabsSettingsFile, tabs);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Saving open tabs failed", ex);
            }
        }

        public static string[] LoadOpenTabs()
        {
            try
            {
                if (File.Exists(TabsSettingsFile))
                {
                    return File.ReadAllLines(TabsSettingsFile);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Loading saved tabs failed", ex);
            }
            return [];
        }

        public static void ClearSavedTabs()
        {
            try
            {
                if (File.Exists(TabsSettingsFile))
                {
                    File.Delete(TabsSettingsFile);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Clearing saved tabs failed", ex);
            }
        }

        public static string GetTheme()
        {
            lock (CacheLock)
            {
                if (!string.IsNullOrEmpty(_cachedTheme)) return _cachedTheme;
            }

            try
            {
                string theme = "Dark"; // Default
                if (File.Exists(SettingsFile))
                {
                    var saved = File.ReadAllText(SettingsFile).Trim();
                    if (saved == "Light") theme = "Light";
                    else theme = "Dark";
                }
                lock (CacheLock) _cachedTheme = theme;
                return theme;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Reading theme failed", ex);
                return "Dark";
            }
        }

        public static void SetTheme(string theme)
        {
            try
            {
                WriteAllTextAtomic(SettingsFile, theme == "Light" ? "Light" : "Dark");
                lock (CacheLock) _cachedTheme = theme == "Light" ? "Light" : "Dark";
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Saving theme failed", ex);
            }
        }

        public static bool IsDarkModeEnabled()
        {
            return GetTheme() == "Dark";
        }

        /// <summary>
        /// Preferred website language (BCP-47 tag, e.g. "de-DE").
        /// Returns an empty string when the user has not chosen one yet.
        /// </summary>
        public static string GetLanguage()
        {
            lock (CacheLock)
            {
                if (_cachedLanguage != null) return _cachedLanguage;
            }

            try
            {
                string language = "";
                if (File.Exists(LanguageSettingsFile))
                {
                    language = File.ReadAllText(LanguageSettingsFile).Trim();
                }
                lock (CacheLock) _cachedLanguage = language;
                return language;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Reading language failed", ex);
                return "";
            }
        }

        public static void SetLanguage(string languageCode)
        {
            try
            {
                WriteAllTextAtomic(LanguageSettingsFile, languageCode);
                lock (CacheLock) _cachedLanguage = languageCode;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Saving language failed", ex);
            }
        }
    }
}


