using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using Yomic.Core.Data;
using Yomic.Core.Helpers;
using Yomic.Core.Models;
using Yomic.Core.Services;
using Yomic.Core.Sources;

namespace YomicDesktop.IPC
{
    public partial class BackendIpcController
    {
        /* =========================================================================
         * 9. PENGATURAN, CADANGAN, PENGUMUMAN & MYANIMELIST
         * ========================================================================= */
        private async Task<object> HandleGetAnnouncementsAsync(bool force)
        {
            var list = await AnnouncementService.FetchAnnouncementsAsync(force);
            return new
            {
                announcements = MapAnnouncementsToDtos(list),
                hasUnread = AnnouncementService.HasUnreadAnnouncements()
            };
        }

        private object HandleMarkAnnouncementRead(string id)
        {
            if (string.IsNullOrWhiteSpace(id) && AnnouncementService.CachedAnnouncements.Count > 0)
            {
                id = AnnouncementService.CachedAnnouncements[0].Id;
            }
            if (!string.IsNullOrWhiteSpace(id))
            {
                AnnouncementService.MarkAsRead(id);
            }
            return new
            {
                ok = true,
                hasUnread = AnnouncementService.HasUnreadAnnouncements()
            };
        }

        private Dictionary<string, object?> HandleUpdateSetting(JsonElement? payload)
        {
            string key = GetString(payload, "key");
            if (payload == null || !payload.Value.TryGetProperty("value", out var valElem))
                return BuildSettingsDictionary();
            switch (key)
            {
                case "defaultReaderMode":
                {
                    var str = valElem.GetString();
                    SettingsService.DefaultReaderMode = str switch
                    {
                        "single" => 1,
                        "dual" => 2,
                        _ => 0
                    };
                    break;
                }
                case "readingDirection":
                {
                    var str = valElem.GetString();
                    if (!string.IsNullOrWhiteSpace(str))
                    {
                        SettingsService.ReadingDirection = str;
                    }
                    break;
                }
                case "readerFitMode":
                {
                    var str = valElem.GetString();
                    if (!string.IsNullOrWhiteSpace(str))
                    {
                        SettingsService.ReaderFitMode = str;
                    }
                    break;
                }
                case "isDarkMode":
                {
                    if (valElem.ValueKind == JsonValueKind.True || valElem.ValueKind == JsonValueKind.False)
                    {
                        bool isDark = valElem.GetBoolean();
                        SettingsService.IsDarkMode = isDark;
                        _dispatcher.InvokeAsync(() =>
                        {
                            if (_ownerWindow is MainWindow mw)
                            {
                                mw.ApplyTitleBarTheme(isDark);
                            }
                        });
                    }
                    break;
                }
                case "autoDarkReader":
                {
                    if (valElem.ValueKind == JsonValueKind.True || valElem.ValueKind == JsonValueKind.False)
                    {
                        SettingsService.AutoDarkReader = valElem.GetBoolean();
                    }
                    break;
                }
                case "maxReaderCacheSize":
                {
                    var str = valElem.GetString() ?? "500MB";
                    int mbValue = str switch
                    {
                        "250MB" => 250,
                        "500MB" => 500,
                        "1GB" => 1024,
                        "2GB" => 2048,
                        _ => 500
                    };
                    SettingsService.MaxCacheSizeMb = mbValue;
                    _ = Task.Run(() => CleanupReaderCache(mbValue));
                    break;
                }
                case "showNsfw":
                {
                    if (valElem.ValueKind == JsonValueKind.True || valElem.ValueKind == JsonValueKind.False)
                    {
                        SettingsService.ShowNsfwSources = valElem.GetBoolean();
                        SettingsService.SaveImmediate();
                    }
                    break;
                }
                case "blurExplicitCovers":
                {
                    if (valElem.ValueKind == JsonValueKind.True || valElem.ValueKind == JsonValueKind.False)
                    {
                        SettingsService.SecureScreen = valElem.GetBoolean();
                    }
                    break;
                }
                case "dohProvider":
                {
                    var str = valElem.GetString() ?? "";
                    SettingsService.DnsOverHttpsProvider = str.Contains("Cloudflare") ? 1
                        : str.Contains("Google") ? 2
                        : str.Contains("AdGuard") ? 3
                        : 0;
                    _ = NetworkService.ResetConnectionsAsync();
                    break;
                }
                case "preloadNextChapter":
                {
                    if (valElem.ValueKind == JsonValueKind.True || valElem.ValueKind == JsonValueKind.False)
                    {
                        SettingsService.PreloadNextChapter = valElem.GetBoolean();
                    }
                    break;
                }
                case "autoDownloadNextChapter":
                {
                    if (valElem.ValueKind == JsonValueKind.True || valElem.ValueKind == JsonValueKind.False)
                    {
                        SettingsService.AutoDownloadNextChapter = valElem.GetBoolean();
                    }
                    break;
                }
                case "skipFilteredChapters":
                {
                    if (valElem.ValueKind == JsonValueKind.True || valElem.ValueKind == JsonValueKind.False)
                    {
                        SettingsService.SkipFilteredChapters = valElem.GetBoolean();
                    }
                    break;
                }
                case "saveDownloadsAsCbz":
                {
                    if (valElem.ValueKind == JsonValueKind.True || valElem.ValueKind == JsonValueKind.False)
                    {
                        SettingsService.SaveDownloadsAsCbz = valElem.GetBoolean();
                    }
                    break;
                }
                case "autoDeleteAfterRead":
                {
                    if (valElem.ValueKind == JsonValueKind.True || valElem.ValueKind == JsonValueKind.False)
                    {
                        SettingsService.AutoDeleteAfterRead = valElem.GetBoolean();
                    }
                    break;
                }
                case "downloadSpeedMode":
                {
                    var str = valElem.GetString();
                    if (!string.IsNullOrWhiteSpace(str))
                    {
                        SettingsService.DownloadSpeedMode = str;
                        DownloadService.ProcessQueue();
                    }
                    break;
                }
                case "showUnreadBadges":
                {
                    if (valElem.ValueKind == JsonValueKind.True || valElem.ValueKind == JsonValueKind.False)
                    {
                        SettingsService.ShowUnreadBadges = valElem.GetBoolean();
                    }
                    break;
                }
                case "autoSyncStartup":
                {
                    if (valElem.ValueKind == JsonValueKind.True || valElem.ValueKind == JsonValueKind.False)
                    {
                        SettingsService.UpdateOnStart = valElem.GetBoolean();
                    }
                    break;
                }
                case "smartUpdate":
                {
                    if (valElem.ValueKind == JsonValueKind.True || valElem.ValueKind == JsonValueKind.False)
                    {
                        SettingsService.UseSmartUpdate = valElem.GetBoolean();
                    }
                    break;
                }
                case "backgroundUpdateInterval":
                {
                    var str = valElem.GetString() ?? "12h";
                    int hours = str switch
                    {
                        "off" => 0,
                        "6h" => 6,
                        "12h" => 12,
                        "24h" => 24,
                        _ => 12
                    };
                    SettingsService.AutoUpdateIntervalHours = hours;
                    _ = Task.Run(() => AutoUpdateTaskService.RegisterOrUpdateTask(hours));
                    break;
                }
                case "isOfflineMode":
                {
                    if (valElem.ValueKind == JsonValueKind.True || valElem.ValueKind == JsonValueKind.False)
                    {
                        SettingsService.IsOfflineMode = valElem.GetBoolean();
                    }
                    break;
                }
                case "sourceStatusInterval":
                {
                    var str = valElem.GetString() ?? "30m";
                    SettingsService.SourceStatusRefreshIntervalMinutes = str switch
                    {
                        "off" => 0,
                        "15m" => 15,
                        "1h" => 60,
                        _ => 30
                    };
                    break;
                }
                case "autoSyncTracker":
                {
                    if (valElem.ValueKind == JsonValueKind.True || valElem.ValueKind == JsonValueKind.False)
                    {
                        SettingsService.AutoSyncTracker = valElem.GetBoolean();
                    }
                    break;
                }
                case "enableImageOptimization":
                {
                    if (valElem.ValueKind == JsonValueKind.True || valElem.ValueKind == JsonValueKind.False)
                    {
                        SettingsService.EnableImageOptimization = valElem.GetBoolean();
                    }
                    break;
                }
                case "enableAutoCropBorders":
                {
                    if (valElem.ValueKind == JsonValueKind.True || valElem.ValueKind == JsonValueKind.False)
                    {
                        SettingsService.EnableAutoCropBorders = valElem.GetBoolean();
                    }
                    break;
                }
                case "maxImageDimension":
                {
                    if (valElem.TryGetInt32(out int maxDim))
                    {
                        SettingsService.MaxImageDimension = maxDim;
                    }
                    break;
                }
                case "enableImageSharpening":
                {
                    if (valElem.ValueKind == JsonValueKind.True || valElem.ValueKind == JsonValueKind.False)
                    {
                        SettingsService.EnableImageSharpening = valElem.GetBoolean();
                    }
                    break;
                }
                case "appLanguage":
                {
                    if (valElem.ValueKind == JsonValueKind.String)
                    {
                        var lang = valElem.GetString()?.Trim().ToLowerInvariant();
                        SettingsService.AppLanguage = lang == "en" ? "en" : "id";
                    }
                    break;
                }
                case "mangaDexLanguage":
                {
                    if (valElem.ValueKind == JsonValueKind.String)
                    {
                        var lang = valElem.GetString()?.Trim().ToLowerInvariant();
                        string normalized = lang == "id" ? "id" : "en";
                        SettingsService.MangaDexLanguage = normalized;
                        JsMangaSource.SelectedLanguage = normalized;
                        SourceManager.ClearAllCache();
                    }
                    break;
                }
                case "customHotkeys":
                {
                    if (valElem.ValueKind == JsonValueKind.String)
                    {
                        SettingsService.CustomHotkeysJson = valElem.GetString() ?? "";
                    }
                    else
                    {
                        SettingsService.CustomHotkeysJson = valElem.GetRawText();
                    }
                    break;
                }
            }
            SettingsService.Save();
            return BuildSettingsDictionary();
        }

        private static void CleanupReaderCache(int maxMb)
        {
            if (maxMb <= 0) return;
            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var readerCachePath = Path.Combine(localAppData, "Yomic", "Cache", "Reader");
                if (!Directory.Exists(readerCachePath)) return;
                var dirInfo = new DirectoryInfo(readerCachePath);
                var files = dirInfo.GetFiles("*", SearchOption.AllDirectories)
                                   .OrderBy(f => f.LastWriteTimeUtc)
                                   .ToList();
                long totalBytes = files.Sum(f => f.Length);
                long maxBytes = (long)maxMb * 1024 * 1024;
                if (totalBytes > maxBytes)
                {
                    long bytesToDelete = totalBytes - maxBytes;
                    long deletedBytes = 0;
                    foreach (var file in files)
                    {
                        if (deletedBytes >= bytesToDelete) break;
                        try
                        {
                            long len = file.Length;
                            file.Delete();
                            deletedBytes += len;
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Warning("Cache", $"Cleanup reader cache error: {ex.Message}");
            }
        }

        private async Task<object> HandleRefreshLibraryCoversAsync()
        {
            try
            {
                await Task.Run(() =>
                {
                    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    var coversDir = Path.Combine(localAppData, "Yomic", "covers");
                    if (Directory.Exists(coversDir))
                    {
                        foreach (var file in Directory.GetFiles(coversDir, "*", SearchOption.AllDirectories))
                        {
                            try { File.Delete(file); } catch { }
                        }
                    }
                    SourceManager.ClearAllCache();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                });
                long cacheBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var mangaList = await GetLibraryAndHistoryMangaDtosAsync();
                foreach (var m in mangaList)
                {
                    if (!string.IsNullOrWhiteSpace(m.CoverUrl))
                    {
                        string sep = m.CoverUrl.Contains('?') ? "&" : "?";
                        m.CoverUrl = $"{m.CoverUrl}{sep}t={cacheBust}";
                    }
                }
                return new
                {
                    ok = true,
                    mangaList,
                    dbSizeText = CalculateStorageSizeText()
                };
            }
            catch (Exception ex)
            {
                LogService.Error("Settings", "Failed to refresh library covers", ex);
                return new { ok = false, error = ex.Message };
            }
        }

        private async Task<object> HandleClearImageCacheAsync()
        {
            try
            {
                await Task.Run(() =>
                {
                    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    var cacheDir = Path.Combine(localAppData, "Yomic", "Cache");
                    if (Directory.Exists(cacheDir))
                    {
                        try
                        {
                            Directory.Delete(cacheDir, true);
                        }
                        catch
                        {
                            foreach (var f in Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories))
                            {
                                try { File.Delete(f); } catch { }
                            }
                        }
                        try { Directory.CreateDirectory(Path.Combine(cacheDir, "Reader")); } catch { }
                    }
                    var coversDir = Path.Combine(localAppData, "Yomic", "covers");
                    if (Directory.Exists(coversDir))
                    {
                        foreach (var f in Directory.GetFiles(coversDir, "*", SearchOption.AllDirectories))
                        {
                            try { File.Delete(f); } catch { }
                        }
                    }
                    SourceManager.ClearAllCache();
                    ImageProxy.ClearRamCache();
                });
                await NetworkService.ResetConnectionsAsync();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            catch { }
            return new { ok = true, dbSizeText = CalculateStorageSizeText() };
        }

        private async Task<object> HandleClearNonLibraryCacheAsync()
        {
            await LibraryService.ClearNonLibraryMangasAsync();
            _discoveredManga.Clear();
            return new { ok = true, dbSizeText = CalculateStorageSizeText() };
        }

        private async Task<object> HandleExportLogsAsync()
        {
            string? destPath = null;
            await _dispatcher.InvokeAsync(() =>
            {
                var dlg = new SaveFileDialog
                {
                    Title = "Ekspor Log Aplikasi Yomic",
                    Filter = "Log File (*.log)|*.log",
                    FileName = $"yomic_diagnostic_{DateTime.Now:yyyyMMdd_HHmm}.log"
                };
                if (dlg.ShowDialog(_ownerWindow) == true)
                {
                    destPath = dlg.FileName;
                }
            });
            if (string.IsNullOrWhiteSpace(destPath))
                return new { ok = false, cancelled = true };
            LogService.CloseAndFlush();
            if (File.Exists(LogService.LogFilePath))
            {
                // Copy via FileShare.ReadWrite stream so an active log writer never causes IOException
                using var srcStream = new FileStream(LogService.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dstStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await srcStream.CopyToAsync(dstStream);
            }
            else
            {
                await File.WriteAllTextAsync(destPath, "Yomic Log File\r\n");
            }
            return new { ok = true, cancelled = false, path = destPath };
        }

        private Dictionary<string, object?> HandleResetSettings()
        {
            SettingsService.Reset();
            JsMangaSource.SelectedLanguage = string.Equals(SettingsService.MangaDexLanguage, "id", StringComparison.OrdinalIgnoreCase) ? "id" : "en";
            SettingsService.SaveImmediate();
            AutoUpdateTaskService.RegisterOrUpdateTask(SettingsService.AutoUpdateIntervalHours);
            _ = NetworkService.ResetConnectionsAsync();
            return BuildSettingsDictionary();
        }

        private async Task<object> HandleClearAllDataAsync()
        {
            try
            {
                // 0. Cancel active downloads & suppress queue re-save on exit
                _suppressDownloadSaveOnDispose = true;
                try
                {
                    DownloadService.Pause();
                    foreach (var req in DownloadService.AllDownloads.ToList())
                    {
                        DownloadService.Cancel(req);
                    }
                    DownloadService.ClearCompleted();
                }
                catch { }
                // 1. Clear Library (DB + Downloads + Covers + Image RAM Cache)
                ImageProxy.ClearRamCache();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                await LibraryService.ClearDatabaseAsync();
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var cacheDir = Path.Combine(localAppData, "Yomic", "Cache");
                if (Directory.Exists(cacheDir))
                {
                    try { Directory.Delete(cacheDir, true); } catch { }
                }
                var queueFileV2 = Path.Combine(DownloadPathService.BaseDirectory, "queue_v2.json");
                if (File.Exists(queueFileV2))
                {
                    try { File.Delete(queueFileV2); } catch { }
                }
                var legacyQueueFile = Path.Combine(localAppData, "Yomic", "downloads_queue.json");
                if (File.Exists(legacyQueueFile))
                {
                    try { File.Delete(legacyQueueFile); } catch { }
                }
                var statusCacheFile = Path.Combine(localAppData, "Yomic", "source_status_cache.json");
                if (File.Exists(statusCacheFile))
                {
                    try { File.Delete(statusCacheFile); } catch { }
                }
                // 2. Clear Extensions & Residual AppData (Plugins + Cache + Icons + Migration markers)
                var roamingYomic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Yomic");
                foreach (var residualFile in new[] { "extensions_index.json", "source_id_migration_v1.done" })
                {
                    var p = Path.Combine(roamingYomic, residualFile);
                    if (File.Exists(p)) { try { File.Delete(p); } catch { } }
                }
                var iconsDir = Path.Combine(roamingYomic, "Icons");
                if (Directory.Exists(iconsDir))
                {
                    try { Directory.Delete(iconsDir, recursive: true); } catch { }
                }
                SourceManager.ClearAllCache();
                SourceManager.DeleteAllUserPlugins();
                _discoveredManga.Clear();
                _chapterTotalPagesCache.Clear();
                _mangaLastTotalPagesCache.Clear();
                _chapterLastPageReadCache.Clear();
                // 3. Clear Settings
                SettingsService.Reset();
                SettingsService.SaveImmediate();
                AutoUpdateTaskService.RegisterOrUpdateTask(SettingsService.AutoUpdateIntervalHours);
                _ = NetworkService.ResetConnectionsAsync();
                // 4. Wait 3 seconds, cleanly release Puppeteer + mutex, schedule automatic restart safely via PowerShell, and exit
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    if (CloudflareBypassService.IsInstanceCreated)
                    {
                        try { await CloudflareBypassService.Instance.DisposeAsync(); } catch { }
                    }
                    YomicDesktop.App.ReleaseSingleInstanceMutexForRestart();
                    try
                    {
                        var exePath = Environment.ProcessPath;
                        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                        {
                            var psPath = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                            var startInfo = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = File.Exists(psPath) ? psPath : "powershell.exe",
                                CreateNoWindow = true,
                                UseShellExecute = false,
                                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                                WorkingDirectory = Path.GetDirectoryName(exePath) ?? ""
                            };
                            startInfo.Environment["YOMIC_RESTART_EXE"] = exePath;
                            startInfo.ArgumentList.Add("-NoProfile");
                            startInfo.ArgumentList.Add("-NonInteractive");
                            startInfo.ArgumentList.Add("-WindowStyle");
                            startInfo.ArgumentList.Add("Hidden");
                            startInfo.ArgumentList.Add("-Command");
                            startInfo.ArgumentList.Add("Start-Sleep -Seconds 2; Start-Process -FilePath $env:YOMIC_RESTART_EXE");
                            using var restartProc = System.Diagnostics.Process.Start(startInfo);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogService.Error("BackendIpc", "Error scheduling auto-restart", ex);
                    }
                    try
                    {
                        _dispatcher.Invoke(() =>
                        {
                            System.Windows.Application.Current?.Shutdown();
                        });
                    }
                    catch { }
                    await Task.Delay(400);
                    Environment.Exit(0);
                });
                return new
                {
                    ok = true,
                    message = L("Semua data berhasil dihapus. Aplikasi akan dimuat ulang otomatis dalam 3 detik.", "All data cleared. The application will automatically reload in 3 seconds.")
                };
            }
            catch (Exception ex)
            {
                LogService.Error("BackendIpc", "Error clearing all data", ex);
                return new
                {
                    ok = false,
                    message = L($"Gagal menghapus data: {ex.Message}", $"Failed to clear data: {ex.Message}")
                };
            }
        }

        private async Task<object> HandleCreateBackupAsync()
        {
            string? destPath = null;
            await _dispatcher.InvokeAsync(() =>
            {
                var dlg = new SaveFileDialog
                {
                    Title = L("Simpan Cadangan Yomic (.yomicbackup)", "Save Yomic Backup (.yomicbackup)"),
                    Filter = "Yomic Backup (*.yomicbackup)|*.yomicbackup|ZIP Archive (*.zip)|*.zip",
                    FileName = $"YomicBackup_{DateTime.Now:yyyyMMdd_HHmm}.yomicbackup"
                };
                if (dlg.ShowDialog(_ownerWindow) == true)
                {
                    destPath = dlg.FileName;
                }
            });
            if (string.IsNullOrWhiteSpace(destPath))
                return new { ok = false, saved = false, cancelled = true };
            SettingsService.SaveImmediate();
            bool success = await BackupService.CreateBackupAsync(destPath);
            string formattedTime = DateTime.Now.ToString("dd MMM yyyy, HH:mm");
            string formattedSize = "";
            if (success && File.Exists(destPath))
            {
                var fileInfo = new FileInfo(destPath);
                double kb = fileInfo.Length / 1024.0;
                formattedSize = kb > 1024 ? $"{(kb / 1024.0):F2} MB" : $"{kb:F0} KB";
                SettingsService.LastBackupTime = formattedTime;
                SettingsService.LastBackupSize = formattedSize;
                SettingsService.SaveImmediate();
            }
            return new
            {
                ok = success,
                saved = success,
                cancelled = false,
                lastBackupText = success
                    ? (!string.IsNullOrEmpty(formattedSize)
                        ? L($"Cadangan terakhir: {formattedTime} ({formattedSize})", $"Last backup: {formattedTime} ({formattedSize})")
                        : L($"Cadangan terakhir: {formattedTime}", $"Last backup: {formattedTime}"))
                    : L("Gagal membuat cadangan", "Failed to create backup")
            };
        }

        private async Task<object> HandleRestoreBackupAsync()
        {
            string? srcPath = null;
            await _dispatcher.InvokeAsync(() =>
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Pilih Berkas Cadangan Yomic (.yomicbackup)",
                    Filter = "Yomic Backup (*.yomicbackup;*.zip)|*.yomicbackup;*.zip"
                };
                if (dlg.ShowDialog(_ownerWindow) == true)
                {
                    srcPath = dlg.FileName;
                }
            });
            if (string.IsNullOrWhiteSpace(srcPath))
                return new { ok = false, restored = false, cancelled = true };
            DownloadService.Pause();
            bool success = await BackupService.RestoreBackupAsync(srcPath);
            if (success)
            {
                SettingsService.Reload();
                JsMangaSource.SelectedLanguage = SettingsService.MangaDexLanguage;
                AutoUpdateTaskService.RegisterOrUpdateTask(SettingsService.AutoUpdateIntervalHours);
                _ = NetworkService.ResetConnectionsAsync();
                _discoveredManga.Clear();
                _chapterLastPageReadCache.Clear();
                _chapterTotalPagesCache.Clear();
                _mangaLastTotalPagesCache.Clear();
                using var restoreDb = new MangaDbContext();
                MangaDbContext.EnsureMangaColumnsExist(restoreDb, forceRecheck: true);
            }
            var bootstrap = await BuildBootstrapAsync();
            return new
            {
                ok = success,
                restored = success,
                cancelled = false,
                bootstrap
            };
        }

        private async Task<Dictionary<string, object?>> HandleMalToggleConnectAsync()
        {
            if (MalService.IsConnected)
            {
                MalService.Disconnect();
                return BuildSettingsDictionary();
            }
            // Start PKCE OAuth flow with state CSRF protection on port 49152 (prevent concurrent listener collisions)
            if (Interlocked.CompareExchange(ref _isMalAuthInProgress, 1, 0) != 0)
            {
                return BuildSettingsDictionary();
            }
            try
            {
                string verifier = MyAnimeListService.GenerateCodeVerifier();
                string expectedState = MyAnimeListService.GenerateOAuthState();
                string authUrl = MalService.GetAuthorizationUrl(verifier, expectedState);
                const string redirectUri = "http://127.0.0.1:49152/";
                using var listener = new System.Net.HttpListener();
                listener.Prefixes.Add(redirectUri);
                listener.Start();
                using var browserProc = Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
                var deadline = DateTime.UtcNow.AddMinutes(2);
                string? code = null;
                bool authApproved = false;
                while (DateTime.UtcNow < deadline)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) break;
                    var listenTask = listener.GetContextAsync();
                    var timeoutTask = Task.Delay(remaining);
                    var completedTask = await Task.WhenAny(listenTask, timeoutTask);
                    if (completedTask == timeoutTask)
                    {
                        try { listener.Stop(); } catch { }
                        _ = listenTask.ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
                        LogService.Warning("MyAnimeList", "MyAnimeList OAuth login timed out after 2 minutes.");
                        return BuildSettingsDictionary();
                    }
                    var context = await listenTask;
                    var request = context.Request;
                    var response = context.Response;
                    // Ignore browser secondary requests like /favicon.ico
                    if (!string.Equals(request.Url?.AbsolutePath, "/", StringComparison.Ordinal))
                    {
                        response.StatusCode = 404;
                        response.OutputStream.Close();
                        continue;
                    }
                    string? returnedState = request.QueryString["state"];
                    string? oauthError = request.QueryString["error"];
                    string? candidateCode = request.QueryString["code"];
                    bool stateMatches = !string.IsNullOrEmpty(returnedState) &&
                        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                            Encoding.UTF8.GetBytes(returnedState),
                            Encoding.UTF8.GetBytes(expectedState));
                    // Reject unrelated/stale local requests without aborting the active OAuth listener
                    if (!stateMatches)
                    {
                        LogService.Warning("MyAnimeList", "Ignored OAuth callback with missing or mismatched state.");
                        response.StatusCode = 400;
                        response.OutputStream.Close();
                        continue;
                    }
                    authApproved = string.IsNullOrEmpty(oauthError) && !string.IsNullOrEmpty(candidateCode);
                    if (authApproved)
                    {
                        code = candidateCode;
                    }
                    else
                    {
                        LogService.Warning("MyAnimeList", $"OAuth authorization cancelled/denied (error={oauthError ?? "none"}).");
                    }
                    string htmlBody = authApproved
                        ? (IsEn
                            ? "</head><body><h2>Login Successful!</h2><p>MyAnimeList authorization succeeded. You may close this browser window now and return to Yomic.</p></body></html>"
                            : "</head><body><h2>Login Sukses!</h2><p>Otorisasi MyAnimeList berhasil. Anda dapat menutup jendela browser ini sekarang dan kembali ke aplikasi Yomic.</p></body></html>")
                        : (IsEn
                            ? "</head><body><h2 style=\"color:#f38ba8;\">Authorization Cancelled or Invalid</h2><p>MyAnimeList login did not complete. You may close this window and retry from Yomic.</p></body></html>"
                            : "</head><body><h2 style=\"color:#f38ba8;\">Otorisasi Dibatalkan atau Tidak Valid</h2><p>Login MyAnimeList gagal atau dibatalkan. Anda dapat menutup jendela ini dan mencoba lagi dari Yomic.</p></body></html>");
                    byte[] buffer = Encoding.UTF8.GetBytes(
                        "<html><head><meta charset=\"utf-8\"><title>Yomic Auth</title>" +
                        "<style>body { font-family: sans-serif; text-align: center; padding: 50px; background: #11111b; color: #cdd6f4; } h2 { color: #a6e3a1; }</style>" +
                        htmlBody);
                    response.ContentLength64 = buffer.Length;
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    response.OutputStream.Close();
                    listener.Stop();
                    break;
                }
                if (authApproved && !string.IsNullOrEmpty(code))
                {
                    bool success = await MalService.AuthenticateAsync(code, verifier);
                    if (success)
                    {
                        await MalService.FetchUserProfileNameAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Error("MyAnimeList", $"OAuth connection error: {ex.Message}", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _isMalAuthInProgress, 0);
            }
            return BuildSettingsDictionary();
        }

        private async Task<object> HandleCheckAppUpdateAsync()
        {
            var info = await UpdateService.CheckForUpdatesAsync();
            return new
            {
                isUpdateAvailable = info.IsUpdateAvailable,
                currentVersion = info.CurrentVersion,
                latestVersion = info.LatestVersion,
                downloadUrl = info.DownloadUrl,
                releaseNotes = info.ReleaseNotes,
                publishedAt = info.PublishedAt
            };
        }

        private async Task<object> HandleInstallAppUpdateAsync(string downloadUrl)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                var info = await UpdateService.CheckForUpdatesAsync();
                downloadUrl = info.DownloadUrl;
            }
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                return new { ok = false, error = L("URL unduhan pembaruan tidak ditemukan.", "Update download URL not found.") };
            }
            if (!UpdateService.IsTrustedInstallUrl(downloadUrl, out _))
            {
                LogService.Warning("BackendIpc", $"Blocked untrusted app:install-update URL: {downloadUrl}");
                return new { ok = false, error = L("URL pembaruan tidak berasal dari repositori resmi Yomic.", "Update URL is not from the official Yomic repository.") };
            }
            bool ok = await UpdateService.DownloadAndInstallUpdateAsync(
                downloadUrl,
                onProgress: null,
                onDetailedProgress: (progress, downloadedBytes, totalBytes, stage) =>
                {
                    PushEvent("event:app-update-progress", new
                    {
                        progress = Math.Round(progress, 1),
                        downloadedBytes,
                        totalBytes,
                        stage
                    });
                });
            return new { ok };
        }

        private async Task<object> HandleMalSearchAsync(string query)
        {
            if (!MalService.IsConnected)
            {
                return new { ok = false, error = L("Akun MyAnimeList belum terhubung. Silakan hubungkan di menu Pengaturan.", "MyAnimeList account is not connected. Please connect it in Settings.") };
            }
            if (string.IsNullOrWhiteSpace(query))
            {
                return new { ok = true, results = new List<MalSearchResultDto>() };
            }
            var results = await MalService.SearchMangaAsync(query.Trim());
            var dtoList = results.Select(r => new MalSearchResultDto
            {
                Id = r.Id,
                Title = r.Title,
                CoverUrl = r.CoverUrl,
                InUserList = r.InUserList,
                UserStatus = r.UserStatus,
                UserChaptersRead = r.UserChaptersRead,
                UserScore = r.UserScore,
                TotalChapters = r.TotalChapters
            }).ToList();
            return new { ok = true, results = dtoList };
        }

        private async Task<object> HandleMalGetTrackAsync(JsonElement? payload)
        {
            string mangaId = GetString(payload, "mangaId");
            long remoteId = GetLong(payload, "remoteId", 0);
            bool autoBind = GetBool(payload, "autoBind", false);
            var manga = await ResolveMangaAsync(mangaId);
            if (manga == null || manga.Id <= 0 || remoteId <= 0)
            {
                return new { ok = false, error = L("Parameter pelacakan tidak valid.", "Invalid tracking parameters.") };
            }
            if (!MalService.IsConnected)
            {
                return new { ok = false, error = L("Akun MyAnimeList belum terhubung.", "MyAnimeList account is not connected.") };
            }
            var remoteTrack = await MalService.FetchMangaStatusAsync(remoteId, manga.Id);
            if (remoteTrack == null)
            {
                return new { ok = false, error = L("Gagal mengambil data dari MyAnimeList.", "Failed to fetch data from MyAnimeList.") };
            }
            if (autoBind)
            {
                try
                {
                    using var db = new MangaDbContext();
                    var existing = await db.Tracks.FirstOrDefaultAsync(t => t.MangaId == manga.Id && t.TrackerName == "MyAnimeList");
                    if (existing == null)
                    {
                        db.Tracks.Add(new MangaTrack
                        {
                            MangaId = manga.Id,
                            TrackerName = "MyAnimeList",
                            RemoteId = remoteTrack.RemoteId,
                            Title = remoteTrack.Title,
                            TotalChapters = remoteTrack.TotalChapters,
                            Status = remoteTrack.Status,
                            Score = remoteTrack.Score,
                            LastChapterRead = remoteTrack.LastChapterRead
                        });
                    }
                    else
                    {
                        existing.RemoteId = remoteTrack.RemoteId;
                        existing.Title = remoteTrack.Title;
                        existing.TotalChapters = remoteTrack.TotalChapters;
                        existing.Status = remoteTrack.Status;
                        existing.Score = remoteTrack.Score;
                        existing.LastChapterRead = remoteTrack.LastChapterRead;
                    }
                    await db.SaveChangesAsync();
                }
                catch { }
            }
            return new
            {
                ok = true,
                track = new
                {
                    remoteId = remoteTrack.RemoteId,
                    title = remoteTrack.Title,
                    totalChapters = remoteTrack.TotalChapters,
                    status = remoteTrack.Status,
                    score = remoteTrack.Score,
                    lastChapterRead = remoteTrack.LastChapterRead
                }
            };
        }

        private async Task<object> HandleMalSaveTrackAsync(JsonElement? payload)
        {
            string mangaId = GetString(payload, "mangaId");
            string status = GetString(payload, "status", "reading");
            int chaptersRead = GetInt(payload, "chaptersRead", 0);
            int score = GetInt(payload, "score", 0);
            long remoteId = GetLong(payload, "remoteId", 0);
            string malTitle = GetString(payload, "malTitle");
            var manga = await ResolveMangaAsync(mangaId);
            if (manga == null || manga.Id <= 0) return new { ok = false, error = "Komik tidak ditemukan." };
            using var db = new MangaDbContext();
            var track = await db.Tracks.FirstOrDefaultAsync(t => t.MangaId == manga.Id && t.TrackerName == "MyAnimeList");
            if (track == null)
            {
                if (remoteId <= 0)
                {
                    return new { ok = false, error = "Pilih entri manga MyAnimeList terlebih dahulu." };
                }
                track = new MangaTrack
                {
                    MangaId = manga.Id,
                    TrackerName = "MyAnimeList",
                    RemoteId = remoteId,
                    Title = !string.IsNullOrWhiteSpace(malTitle) ? malTitle : manga.Title,
                    TotalChapters = manga.Chapters?.Count ?? 0,
                    Status = status,
                    LastChapterRead = chaptersRead,
                    Score = score
                };
                db.Tracks.Add(track);
            }
            else
            {
                if (remoteId > 0)
                {
                    track.RemoteId = remoteId;
                }
                if (!string.IsNullOrWhiteSpace(malTitle))
                {
                    track.Title = malTitle;
                }
                track.Status = status;
                track.LastChapterRead = chaptersRead;
                track.Score = score;
            }
            await db.SaveChangesAsync();
            if (MalService.IsConnected && track.RemoteId > 0)
            {
                await MalService.UpdateMangaStatusAsync(track.RemoteId, status, chaptersRead, score);
            }
            return new
            {
                ok = true,
                track = new
                {
                    remoteId = track.RemoteId,
                    title = track.Title,
                    status = track.Status,
                    score = track.Score,
                    lastChapterRead = track.LastChapterRead
                }
            };
        }

        private async Task<object> HandleMalUnbindTrackAsync(string mangaId)
        {
            var manga = await ResolveMangaAsync(mangaId);
            if (manga != null && manga.Id > 0)
            {
                using var db = new MangaDbContext();
                var tracks = await db.Tracks.Where(t => t.MangaId == manga.Id).ToListAsync();
                db.Tracks.RemoveRange(tracks);
                await db.SaveChangesAsync();
            }
            return new { ok = true };
        }

        private async Task<object> HandleSubmitFeedbackAsync(JsonElement? payload)
        {
            var message = GetString(payload, "message").Trim();
            var category = GetString(payload, "category", "bug").Trim();
            var categoryLabel = GetString(payload, "categoryLabel", "Laporan Bug").Trim();
            var screenshotBase64 = GetString(payload, "screenshotBase64").Trim();
            var screenshotName = GetString(payload, "screenshotName").Trim();
            var screenshotMimeType = GetString(payload, "screenshotMimeType").Trim();
            var hasScreenshot = !string.IsNullOrWhiteSpace(screenshotBase64);
            if (string.IsNullOrWhiteSpace(message))
            {
                return new { ok = false, error = "Silakan tuliskan deskripsi masukan Anda terlebih dahulu." };
            }
            if (message.Length > 10000)
            {
                return new { ok = false, error = "Deskripsi masukan terlalu panjang (maksimal 10.000 karakter)." };
            }
            // Cap screenshot base64 payload (~6 MB decoded max) to prevent memory/network abuse
            if (screenshotBase64.Length > 8 * 1024 * 1024)
            {
                return new { ok = false, error = L("Ukuran lampiran tangkapan layar terlalu besar (maksimal 6 MB).", "Screenshot attachment is too large (maximum 6 MB).") };
            }
            var url = SettingsService.FeedbackWebhookUrl;
            if (string.IsNullOrWhiteSpace(url) || url == "YOUR_GOOGLE_APPS_SCRIPT_URL_HERE")
            {
                url = SettingsService.DefaultFeedbackWebhookUrl;
            }
            if (!Yomic.Core.Helpers.UrlSecurityHelper.IsAllowedFeedbackWebhookUrl(url, out var validatedFeedbackUri) || validatedFeedbackUri == null)
            {
                LogService.Warning("Feedback", $"Blocked untrusted FeedbackWebhookUrl: {url}");
                return new { ok = false, error = L("URL tujuan masukan tidak valid.", "Feedback endpoint URL is not valid.") };
            }
            if (SettingsService.IsOfflineMode)
            {
                return new { ok = false, error = L("Mode Offline aktif. Nonaktifkan Mode Offline untuk mengirim masukan.", "Offline Mode is enabled. Disable Offline Mode to send feedback.") };
            }
            try
            {
                var appVersion = UpdateService.CURRENT_VERSION;
                var osVersion = $"{Environment.OSVersion.VersionString} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})";
                var language = SettingsService.AppLanguage;
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                var clientId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes($"{Environment.MachineName}|{Environment.UserName}|yomic-feedback")))
                    .Substring(0, 24).ToLowerInvariant();
                var requestBody = new
                {
                    targetOwnerEmail = "info.yomic@gmail.com",
                    spreadsheetName = "Yomic - Masukan Pengguna",
                    clientId,
                    timestamp,
                    category,
                    categoryLabel,
                    message,
                    appVersion,
                    osVersion,
                    language,
                    hasScreenshot,
                    screenshotName,
                    screenshotMimeType,
                    screenshotBase64
                };
                var jsonPayload = JsonSerializer.Serialize(requestBody);
                using var handler = new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false };
                using var client = new System.Net.Http.HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(35)
                };
                using var content = new System.Net.Http.StringContent(jsonPayload, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(validatedFeedbackUri, content);
                bool isSuccess = false;
                string? serverError = null;
                string? jsonBody = null;
                if (response.IsSuccessStatusCode)
                {
                    var respBytes = await Yomic.Core.Helpers.UrlSecurityHelper.ReadContentBoundedAsync(response.Content, 1L * 1024 * 1024);
                    jsonBody = Encoding.UTF8.GetString(respBytes);
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Redirect ||
                         response.StatusCode == System.Net.HttpStatusCode.Found ||
                         response.StatusCode == System.Net.HttpStatusCode.MovedPermanently ||
                         response.StatusCode == System.Net.HttpStatusCode.SeeOther ||
                         response.StatusCode == System.Net.HttpStatusCode.TemporaryRedirect)
                {
                    var redirectLocation = response.Headers.Location?.ToString();
                    if (Yomic.Core.Helpers.UrlSecurityHelper.IsAllowedFeedbackWebhookUrl(redirectLocation, out var redirectUri) && redirectUri != null)
                    {
                        using var followResponse = await client.GetAsync(redirectUri);
                        if (followResponse.IsSuccessStatusCode)
                        {
                            var respBytes = await Yomic.Core.Helpers.UrlSecurityHelper.ReadContentBoundedAsync(followResponse.Content, 1L * 1024 * 1024);
                            jsonBody = Encoding.UTF8.GetString(respBytes);
                        }
                    }
                }
                if (!string.IsNullOrWhiteSpace(jsonBody))
                {
                    try
                    {
                        using var respDoc = JsonDocument.Parse(jsonBody);
                        if (respDoc.RootElement.ValueKind == JsonValueKind.Object &&
                            respDoc.RootElement.TryGetProperty("ok", out var okProp) &&
                            okProp.ValueKind == JsonValueKind.True)
                        {
                            isSuccess = true;
                        }
                        else if (respDoc.RootElement.ValueKind == JsonValueKind.Object &&
                                 respDoc.RootElement.TryGetProperty("error", out var errProp) &&
                                 errProp.ValueKind == JsonValueKind.String)
                        {
                            serverError = errProp.GetString();
                        }
                    }
                    catch
                    {
                        // Non-JSON success page fallback only if response was HTTP 200
                        isSuccess = false;
                    }
                }
                if (isSuccess)
                {
                    SettingsService.LastFeedbackDate = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    SettingsService.Save();
                    LogService.Info("Feedback", $"Feedback ({categoryLabel}) sent to Google Spreadsheet (info.yomic@gmail.com).");
                    return new { ok = true };
                }
                LogService.Error("Feedback", $"Google Apps Script Spreadsheet error: {response.StatusCode} - {serverError ?? jsonBody ?? "(empty)"}");
                return new { ok = false, error = serverError ?? L("Gagal mengirim masukan ke Google Spreadsheet. Silakan coba lagi nanti.", "Failed to send feedback to Google Spreadsheet. Please try again later.") };
            }
            catch (Exception ex)
            {
                LogService.Error("Feedback", "Error sending feedback to Google Spreadsheet", ex);
                return new { ok = false, error = L("Gagal terhubung ke server Google Spreadsheet. Periksa koneksi internet Anda.", "Failed to connect to Google Spreadsheet server. Check your internet connection.") };
            }
        }
    }
}
