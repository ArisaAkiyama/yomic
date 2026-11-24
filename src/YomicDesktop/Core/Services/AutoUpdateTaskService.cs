using System;
using System.Diagnostics;
using System.IO;

namespace Yomic.Core.Services
{
    public static class AutoUpdateTaskService
    {
        private const string TaskName = "Yomic_Background_AutoUpdate";
        private static readonly object _taskLock = new object();

        public static void RegisterOrUpdateTask(int intervalHours)
        {
            lock (_taskLock)
            {
                try
                {
                    if (intervalHours <= 0)
                    {
                        UnregisterTask();
                        LogService.Info("AutoUpdateTask", "Background task disabled/removed.");
                        return;
                    }

                    string exePath = Environment.ProcessPath ?? string.Empty;
                    if (string.IsNullOrEmpty(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        LogService.Warning("AutoUpdateTask", "Cannot register task: Executable path is invalid or not an .exe.");
                        return;
                    }

                    // Windows schtasks.exe restricts /sc HOURLY /mo to 1..23; use /sc DAILY /mo 1 for 24h+
                    string scheduleType = intervalHours >= 24 ? "DAILY" : "HOURLY";
                    int modifier = intervalHours >= 24 ? Math.Max(1, intervalHours / 24) : Math.Clamp(intervalHours, 1, 23);

                    var processInfo = new ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    processInfo.ArgumentList.Add("/create");
                    processInfo.ArgumentList.Add("/tn");
                    processInfo.ArgumentList.Add(TaskName);
                    processInfo.ArgumentList.Add("/tr");
                    processInfo.ArgumentList.Add($"\"{exePath}\" --background-update");
                    processInfo.ArgumentList.Add("/sc");
                    processInfo.ArgumentList.Add(scheduleType);
                    processInfo.ArgumentList.Add("/mo");
                    processInfo.ArgumentList.Add(modifier.ToString());
                    processInfo.ArgumentList.Add("/f");

                    using var process = Process.Start(processInfo);
                    if (process == null)
                    {
                        LogService.Warning("AutoUpdateTask", "Failed to start schtasks.exe.");
                        return;
                    }

                    // Drain stdout and stderr concurrently BEFORE WaitForExit to prevent OS pipe deadlock
                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(10000))
                    {
                        try { process.Kill(entireProcessTree: true); } catch { }
                        LogService.Warning("AutoUpdateTask", "Timed out waiting for schtasks.exe.");
                        return;
                    }

                    string error = stderrTask.GetAwaiter().GetResult();
                    _ = stdoutTask.GetAwaiter().GetResult();

                    if (process.ExitCode == 0)
                    {
                        LogService.Info("AutoUpdateTask", $"Successfully registered task ({scheduleType} /mo {modifier} -> every {intervalHours}h).");
                    }
                    else
                    {
                        LogService.Warning("AutoUpdateTask", $"Failed to register task (exit={process.ExitCode}): {error}");
                    }
                }
                catch (Exception ex)
                {
                    LogService.Error("AutoUpdateTask", $"Error managing task: {ex.Message}", ex);
                }
            }
        }

        private static void UnregisterTask()
        {
            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                processInfo.ArgumentList.Add("/delete");
                processInfo.ArgumentList.Add("/tn");
                processInfo.ArgumentList.Add(TaskName);
                processInfo.ArgumentList.Add("/f");

                using var process = Process.Start(processInfo);
                process?.WaitForExit(10000);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoUpdateTask] Error removing task: {ex.Message}");
            }
        }
    }
}
