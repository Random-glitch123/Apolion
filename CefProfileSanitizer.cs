using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Apolion
{
    /// <summary>
    /// Stops CEF's Chrome runtime from resurrecting the previous browser session as
    /// native "Chromium" windows.
    ///
    /// CEF reuses a normal Chrome profile, including Chrome's session-restore
    /// service. When that profile is marked as crashed, Chromium re-creates the
    /// tabs/windows from the last session as top-level browsers that are created
    /// internally, <b>without</b> Apolion's <c>CefClient</c>:
    ///
    ///   cef.log: "Creating a chrome browser without a client"
    ///
    /// Because such a browser never passes through <see cref="ApolionLifeSpanHandler"/>
    /// it appears as a stray "<page title> - Chromium" window. Apolion already
    /// restores its own tabs from <see cref="SettingsManager"/>, so Chrome's session
    /// data is redundant and safe to drop.
    ///
    /// The profile is marked as crashed on every exit because Cef.Shutdown() currently
    /// fails with a thread-affinity error (a separate, known issue), so the flag is
    /// normalized here before Cef.Initialize instead.
    /// </summary>
    internal static class CefProfileSanitizer
    {
        private static readonly Regex ExitTypeRegex =
            new("\"exit_type\"\\s*:\\s*\"[^\"]*\"", RegexOptions.Compiled);

        private static readonly Regex ProfileObjectRegex =
            new("\"profile\"\\s*:\\s*\\{", RegexOptions.Compiled);

        /// <summary>
        /// Runs profile sanitization on a background thread to avoid blocking CEF initialization.
        /// Returns a task that completes when sanitization is done (fire-and-forget safe).
        /// </summary>
        public static Task PrepareForStartupAsync(string? cachePath)
        {
            if (string.IsNullOrWhiteSpace(cachePath))
                return Task.CompletedTask;

            // Run on thread pool to avoid blocking the CEF owner thread
            return Task.Run(() =>
            {
                try
                {
                    string profileDirectory = Path.Combine(cachePath, "Default");
                    if (!Directory.Exists(profileDirectory)) return;

                    int removed = ClearSavedSessions(Path.Combine(profileDirectory, "Sessions"));
                    bool cleaned = MarkProfileCleanlyExited(Path.Combine(profileDirectory, "Preferences"));

                    if (removed > 0 || cleaned)
                    {
                        AppLogger.Info(
                            $"[SESSION] Prevented native Chromium session restore " +
                            $"(removed {removed} file(s), exit_type normalized: {cleaned})");
                    }
                }
                catch (Exception ex)
                {
                    // Never block startup because profile housekeeping failed.
                    AppLogger.Debug($"CEF profile sanitizer skipped: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Synchronous version for backwards compatibility (used during shutdown cleanup).
        /// </summary>
        public static void PrepareForStartup(string? cachePath)
        {
            if (string.IsNullOrWhiteSpace(cachePath)) return;

            try
            {
                string profileDirectory = Path.Combine(cachePath, "Default");
                if (!Directory.Exists(profileDirectory)) return;

                int removed = ClearSavedSessions(Path.Combine(profileDirectory, "Sessions"));
                bool cleaned = MarkProfileCleanlyExited(Path.Combine(profileDirectory, "Preferences"));

                if (removed > 0 || cleaned)
                {
                    AppLogger.Info(
                        $"[SESSION] Prevented native Chromium session restore " +
                        $"(removed {removed} file(s), exit_type normalized: {cleaned})");
                }
            }
            catch (Exception ex)
            {
                // Never block startup because profile housekeeping failed.
                AppLogger.Debug($"CEF profile sanitizer skipped: {ex.Message}");
            }
        }

        /// <summary>
        /// Deletes Chrome's native "restore last session" data. Apolion's own saved
        /// tabs (SettingsManager) are untouched.
        /// </summary>
        private static int ClearSavedSessions(string sessionsDirectory)
        {
            if (!Directory.Exists(sessionsDirectory)) return 0;

            int removed = 0;
            foreach (string pattern in new[] { "Session_*", "Tabs_*" })
            {
                foreach (string file in Directory.GetFiles(sessionsDirectory, pattern))
                {
                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch
                    {
                        // A locked file is not fatal; the exit_type fix below still
                        // prevents the restore in the common case.
                    }
                }
            }

            return removed;
        }

        /// <summary>
        /// Rewrites "profile.exit_type" to "Normal" so Chrome does not treat the
        /// previous run as a crash. Only the two keys are touched; the rest of the
        /// preferences file is preserved byte for byte.
        /// </summary>
        private static bool MarkProfileCleanlyExited(string preferencesPath)
        {
            if (!File.Exists(preferencesPath)) return false;

            string json;
            try
            {
                json = File.ReadAllText(preferencesPath);
            }
            catch
            {
                return false;
            }

            string updated = ExitTypeRegex.Replace(json, "\"exit_type\":\"Normal\"", 1);

            if (!ExitTypeRegex.IsMatch(json))
            {
                // Older/partial profiles may not have the key yet. Chrome's default
                // for a missing value is "Crashed", so add it explicitly.
                updated = ProfileObjectRegex.Replace(
                    updated, "\"profile\":{\"exit_type\":\"Normal\",", 1);
            }

            if (string.Equals(updated, json, StringComparison.Ordinal)) return false;

            try
            {
                // Chrome writes UTF-8 without a BOM; match that.
                File.WriteAllText(preferencesPath, updated, new UTF8Encoding(false));
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
