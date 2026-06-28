using System;
using System.Diagnostics;
using System.IO;

namespace Yomic.Core.Services
{
    public static class AutoUpdateTaskService
    {
        private const string TaskName = "Yomic_Background_AutoUpdate";

        public static void RegisterOrUpdateTask(int intervalHours)
        {
            try
            {
                // Unregister existing task first to ensure clean state
                UnregisterTask();

                if (intervalHours <= 0)
                {
                    // Disabled, so we just stop here
                    System.Diagnostics.Debug.WriteLine("[AutoUpdateTask] Task disabled/removed.");
                    return;
                }

                string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Debug.WriteLine("[AutoUpdateTask] Cannot register task: Executable path is invalid or not an .exe.");
                    return;
                }

                // Register new task using schtasks
                // /sc HOURLY /mo <interval> 
                string command = $"/create /tn \"{TaskName}\" /tr \"\\\"{exePath}\\\" --background-update\" /sc HOURLY /mo {intervalHours} /f";

                var processInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = command,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(processInfo);
                process?.WaitForExit();

                if (process?.ExitCode == 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[AutoUpdateTask] Successfully registered task for every {intervalHours} hours.");
                }
                else
                {
                    string error = process?.StandardError.ReadToEnd() ?? "Unknown error";
                    System.Diagnostics.Debug.WriteLine($"[AutoUpdateTask] Failed to register task: {error}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AutoUpdateTask] Error managing task: {ex.Message}");
            }
        }

        private static void UnregisterTask()
        {
            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/delete /tn \"{TaskName}\" /f",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };

                using var process = Process.Start(processInfo);
                process?.WaitForExit();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AutoUpdateTask] Error removing task: {ex.Message}");
            }
        }
    }
}
