using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

/// <summary>
/// Dedicated updater executable for Apolion.
/// Replaces files after the main application exits.
/// 
/// Args:
///   [0] = install directory (target)
///   [1] = source staging directory (extracted update)
///   [2] = remove-list.txt path (optional, may be empty)
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: Apolion Updater.exe <installDir> <sourceDir> [removeListPath]");
            return 1;
        }

        string installDir = args[0];
        string sourceDir = args[1];
        string removeListPath = args.Length > 2 ? args[2] : string.Empty;

        try
        {
            // 1. Wait for Apolion.exe and Apolion Helper.exe to exit
            WaitForProcessesToExit();

            // 2. Clean CEF cache directories
            CleanupCefCache();

            // 3. Copy update files (robocopy for robustness)
            CopyUpdateFiles(sourceDir, installDir);

            // 4. Process remove list (delete obsolete files)
            if (!string.IsNullOrWhiteSpace(removeListPath) && File.Exists(removeListPath))
            {
                ProcessRemoveList(installDir, removeListPath);
            }

            // 5. Start Apolion.exe
            StartApolion(installDir);

            // 6. Cleanup temp folder
            CleanupTempFolder();

            return 0;
        }
        catch (Exception ex)
        {
            LogError($"Updater failed: {ex.Message}");
            // Try to start Apolion anyway so user isn't left with broken install
            try { StartApolion(installDir); } catch { }
            return 1;
        }
    }

    private static void WaitForProcessesToExit()
    {
        const int maxWaitSeconds = 120;
        int waited = 0;

        while (waited < maxWaitSeconds)
        {
            bool apolionRunning = IsProcessRunning("Apolion");
            bool helperRunning = IsProcessRunning("Apolion Helper");

            if (!apolionRunning && !helperRunning)
                return;

            Thread.Sleep(1000);
            waited++;
        }

        // Force kill if still running (shouldn't happen with 120s timeout)
        foreach (var proc in Process.GetProcessesByName("Apolion"))
        {
            try { proc.Kill(); } catch { }
        }
        foreach (var proc in Process.GetProcessesByName("Apolion Helper"))
        {
            try { proc.Kill(); } catch { }
        }
    }

    private static bool IsProcessRunning(string name)
    {
        try
        {
            return Process.GetProcessesByName(name).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void CleanupCefCache()
    {
        string[] cefCacheRelativeDirectories =
        {
            "Sessions",
            Path.Combine("Default", "Code Cache"),
            Path.Combine("Default", "GPUCache"),
            "GrShaderCache",
            Path.Combine("Default", "DawnGraphiteCache"),
            Path.Combine("Default", "DawnWebGPUCache"),
            Path.Combine("Default", "Service Worker"),
            Path.Combine("Default", "blob_storage"),
            Path.Combine("Default", "Cache"),
            Path.Combine("Default", "Media Cache"),
            Path.Combine("Default", "IndexedDB"),
            Path.Combine("Default", "Local Storage"),
            Path.Combine("Default", "Session Storage"),
            Path.Combine("Default", "File System"),
            Path.Combine("Default", "databases"),
            "ShaderCache",
            "Visited Links"
        };

        string cefCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Apolion",
            "CefCache");

        if (!Directory.Exists(cefCachePath))
            return;

        foreach (string relativeDirectory in cefCacheRelativeDirectories)
        {
            try
            {
                string directory = Path.Combine(cefCachePath, relativeDirectory);
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
                // Best effort
            }
        }

        try
        {
            foreach (string pattern in new[] { "*.log", "*.tmp" })
            {
                foreach (string file in Directory.GetFiles(cefCachePath, pattern))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch
        {
            // Best effort
        }
    }

    private static void CopyUpdateFiles(string sourceDir, string targetDir)
    {
        // Use robocopy for reliable copying with retries
        // /E = copy subdirs including empty, /COPY:DAT = copy Data, Attributes, Timestamps
        // /R:5 /W:1 = retry 5 times with 1 second wait
        // /XD logs /XF *.log *.tmp = exclude logs and temp files
        var psi = new ProcessStartInfo
        {
            FileName = "robocopy.exe",
            Arguments = $"\"{sourceDir}\" \"{targetDir}\" /E /COPY:DAT /R:5 /W:1 /XD logs /XF *.log *.tmp",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var proc = Process.Start(psi);
        proc?.WaitForExit();

        // Robocopy exit codes: 0-7 = success, 8+ = error
        if (proc != null && proc.ExitCode >= 8)
        {
            throw new IOException($"Robocopy failed with exit code {proc.ExitCode}");
        }
    }

    private static void ProcessRemoveList(string installDir, string removeListPath)
    {
        try
        {
            string[] lines = File.ReadAllLines(removeListPath);
            foreach (string line in lines)
            {
                string relativePath = line.Trim();
                if (string.IsNullOrEmpty(relativePath))
                    continue;

                string fullPath = Path.GetFullPath(Path.Combine(installDir, relativePath));
                string installDirFull = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

                // Safety: ensure the path is within the install directory
                if (!fullPath.StartsWith(installDirFull, StringComparison.OrdinalIgnoreCase))
                {
                    LogError($"Remove list entry escapes install directory: {relativePath}");
                    continue;
                }

                try
                {
                    if (File.Exists(fullPath))
                        File.Delete(fullPath);
                }
                catch (Exception ex)
                {
                    LogError($"Failed to delete {relativePath}: {ex.Message}");
                }
            }

            // Clean up empty directories
            try
            {
                foreach (string dir in Directory.GetDirectories(installDir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir).Length == 0)
                            Directory.Delete(dir);
                    }
                    catch { }
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            LogError($"Failed to process remove list: {ex.Message}");
        }
    }

    private static void StartApolion(string installDir)
    {
        string exePath = Path.Combine(installDir, "Apolion.exe");
        if (File.Exists(exePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true
            });
        }
    }

    private static void CleanupTempFolder()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "Apolion_Update");
        try
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
        catch { }
    }

    private static void LogError(string message)
    {
        try
        {
            string logPath = Path.Combine(Path.GetTempPath(), "Apolion_Update.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { }
    }
}