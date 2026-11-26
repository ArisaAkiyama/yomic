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
    public partial class BackendIpcController : IDisposable
    {
        private readonly Window _ownerWindow;
        private readonly Dispatcher _dispatcher;
        private readonly Action<string> _sendToWebUi;
        public SettingsService SettingsService { get; }
        public NetworkService NetworkService { get; }
        public SourceManager SourceManager { get; }
        public SourceStatusService SourceStatusService { get; }
        public ExtensionRepositoryService ExtensionRepoService { get; }
        public LibraryService LibraryService { get; }
        public DownloadService DownloadService { get; }
        public BackupService BackupService { get; }
        public MyAnimeListService MalService { get; }
        public AnnouncementService AnnouncementService { get; }
        public UpdateService UpdateService { get; }
        public WebResourceImageProxy ImageProxy { get; }
        // In-memory cache of manga discovered in Source Catalog Feeds (id -> Manga model)
        private readonly ConcurrentDictionary<string, Manga> _discoveredManga = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, int> _chapterTotalPagesCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<long, int> _mangaLastTotalPagesCache = new();
        private static readonly ConcurrentDictionary<string, long> _chapterLastPageReadCache = new(StringComparer.OrdinalIgnoreCase);
        private static string MakeChapterCacheKey(long sourceId, string chapterUrl) => $"{sourceId}|{chapterUrl}";
        private static string MakeDownloadQueueItemId(long sourceId, string chapterUrl) => $"dl-{ComputeShortHash($"{sourceId}|{chapterUrl}")}";
        // Bounded short-TTL cache + single-flight gate for preloaded next-chapter page lists
        private static readonly ConcurrentDictionary<string, (List<string> Pages, DateTime ExpiresUtc)> _preloadedPageListCache = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, byte> _preloadingChapterUrls = new(StringComparer.Ordinal);
        private const int MaxPreloadedPageListEntries = 32;
        private static readonly TimeSpan PreloadedPageListTtl = TimeSpan.FromMinutes(10);

        internal static bool TryGetPreloadedPageList(string key, out List<string> pages)
        {
            if (_preloadedPageListCache.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresUtc > DateTime.UtcNow && entry.Pages.Count > 0)
                {
                    pages = new List<string>(entry.Pages);
                    return true;
                }
                _preloadedPageListCache.TryRemove(key, out _);
            }
            pages = new List<string>();
            return false;
        }

        internal static void StorePreloadedPageList(string key, List<string> pages)
        {
            if (string.IsNullOrEmpty(key) || pages == null || pages.Count == 0) return;
            var now = DateTime.UtcNow;
            if (_preloadedPageListCache.Count >= MaxPreloadedPageListEntries)
            {
                foreach (var kv in _preloadedPageListCache)
                {
                    if (kv.Value.ExpiresUtc <= now)
                    {
                        _preloadedPageListCache.TryRemove(kv.Key, out _);
                    }
                }
                if (_preloadedPageListCache.Count >= MaxPreloadedPageListEntries)
                {
                    var oldestKey = _preloadedPageListCache.OrderBy(kv => kv.Value.ExpiresUtc).Select(kv => kv.Key).FirstOrDefault();
                    if (oldestKey != null) _preloadedPageListCache.TryRemove(oldestKey, out _);
                }
            }
            _preloadedPageListCache[key] = (new List<string>(pages), now.Add(PreloadedPageListTtl));
        }
        private DateTime _lastDownloadEventPush = DateTime.MinValue;
        private static readonly SemaphoreSlim _mangaInsertLock = MangaDbContext.DatabaseWriteGate;
        private static readonly SemaphoreSlim _readerProgressDbLock = MangaDbContext.DatabaseWriteGate;
        private int _isSyncingLibrary = 0;
        private int _isMalAuthInProgress = 0;
        private volatile bool _suppressDownloadSaveOnDispose = false;
        private static readonly (string Bg, string Primary, string Secondary)[] CoverPalettes =
        {
            ("#071426", "#0066CC", "#38BDF8"),
            ("#0F172A", "#2563EB", "#60A5FA"),
            ("#111827", "#0284C7", "#7DD3FC"),
            ("#081C24", "#0D9488", "#2DD4BF"),
            ("#17153B", "#4F46E5", "#818CF8"),
            ("#1E1B4B", "#6366F1", "#A5B4FC")
        };
        public BackendIpcController(Window ownerWindow, Action<string> sendToWebUi)
        {
            _ownerWindow = ownerWindow;
            _dispatcher = ownerWindow.Dispatcher;
            _sendToWebUi = sendToWebUi;
            using (var initDb = new MangaDbContext())
            {
                initDb.Database.EnsureCreated();
                MangaDbContext.EnsureMangaColumnsExist(initDb);
            }
            SettingsService = new SettingsService();
            HttpSource.IsOfflineModeProvider = () => SettingsService.IsOfflineMode;
            HttpSource.DnsOverHttpsProviderGetter = () => SettingsService.DnsOverHttpsProvider;
            JsMangaSource.SelectedLanguage = string.Equals(SettingsService.MangaDexLanguage, "id", StringComparison.OrdinalIgnoreCase) ? "id" : "en";
            NetworkService = new NetworkService(SettingsService);
            // Defer Jint plugin initialization to a background thread so MainWindow shows immediately without UI-thread freeze
            SourceManager = new SourceManager(deferExtensionLoad: true);
            SourceStatusService = new SourceStatusService(SourceManager, SettingsService, NetworkService);
            ExtensionRepoService = new ExtensionRepositoryService(SourceManager, SettingsService, SourceStatusService);
            LibraryService = new LibraryService();
            DownloadService = new DownloadService(SourceManager, LibraryService, NetworkService, SettingsService);
            BackupService = new BackupService();
            MalService = new MyAnimeListService(SettingsService);
            AnnouncementService = new AnnouncementService(SettingsService, NetworkService);
            UpdateService = new UpdateService();
            ImageProxy = new WebResourceImageProxy(_dispatcher, NetworkService, SourceManager, SettingsService);
            // Wire real-time events to WebUI
            AnnouncementService.NewAnnouncementDetected += (_, list) =>
            {
                PushEvent("event:announcements-updated", new
                {
                    announcements = MapAnnouncementsToDtos(list),
                    hasUnread = AnnouncementService.HasUnreadAnnouncements(),
                    isNewAlert = true
                });
            };
            AnnouncementService.AnnouncementsUpdated += (_, list) =>
            {
                PushEvent("event:announcements-updated", new
                {
                    announcements = MapAnnouncementsToDtos(list),
                    hasUnread = AnnouncementService.HasUnreadAnnouncements(),
                    isNewAlert = false
                });
            };
            UpdateService.UpdateAvailableDetected += (_, info) =>
            {
                PushEvent("event:app-update-available", new
                {
                    isUpdateAvailable = info.IsUpdateAvailable,
                    currentVersion = info.CurrentVersion,
                    latestVersion = info.LatestVersion,
                    downloadUrl = info.DownloadUrl,
                    releaseNotes = info.ReleaseNotes,
                    publishedAt = info.PublishedAt
                });
            };
            AnnouncementService.StartRealtimeMonitoring(15);
            if (SettingsService.CheckAppUpdateOnStart)
            {
                UpdateService.StartRealtimeMonitoring(60);
            }
            if (MalService.IsConnected && string.IsNullOrWhiteSpace(SettingsService.MalUsername))
            {
                _ = Task.Run(async () => await MalService.FetchUserProfileNameAsync());
            }
            if (SettingsService.MaxCacheSizeMb > 0)
            {
                _ = Task.Run(() => CleanupReaderCache(SettingsService.MaxCacheSizeMb));
            }
            SourceStatusService.SourceStatusUpdated += OnSourceStatusUpdated;
            SourceStatusService.SourceCheckProgress += (current, total) =>
            {
                PushEvent("event:source-check-progress", new
                {
                    active = current < total,
                    current,
                    total
                });
            };
            DownloadService.ProgressChanged += OnDownloadProgressChanged;
            DownloadService.StatusChanged += OnDownloadStatusChanged;
            DownloadService.QueueChanged += OnDownloadQueueChanged;
            _ = Task.Run(async () =>
            {
                try
                {
                    await SourceManager.EnsureExtensionsLoadedAsync();
                    await SourceIdMigrationService.RunIfNeededAsync(SourceManager);
                    var updatedExts = await ExtensionRepoService.AutoUpdateInstalledExtensionsAsync();
                    if (updatedExts > 0)
                    {
                        var dtos = await GetExtensionsDtosAsync(false);
                        PushEvent("event:extensions-updated", dtos);
                    }
                }
                catch (Exception ex)
                {
                    LogService.Warning("IPC", $"Startup migration/auto-update failed: {ex.Message}");
                }
            });
        }

        private void PushEvent(string type, object? payload)
        {
            try
            {
                var msg = new IpcResponseEnvelope
                {
                    Type = type,
                    Ok = true,
                    Payload = payload
                };
                var json = JsonSerializer.Serialize(msg);
                _dispatcher.InvokeAsync(() => _sendToWebUi(json));
            }
            catch (Exception ex)
            {
                LogService.Debug("IPC", $"Failed to push event {type}: {ex.Message}");
            }
        }

        private void OnSourceStatusUpdated(long sourceId, SourceStatus status, string tooltip)
        {
            PushEvent("event:source-status", new
            {
                sourceId,
                status = ExtensionRepositoryService.MapStatusToString(status),
                tooltip
            });
        }

        private void OnDownloadProgressChanged(object? sender, DownloadRequest req)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastDownloadEventPush).TotalMilliseconds < 180 && req.Progress < 100)
                return;
            _lastDownloadEventPush = now;
            PushEvent("event:downloads-updated", GetDownloadsQueueList());
        }

        private void OnDownloadStatusChanged(object? sender, DownloadRequest req)
        {
            PushEvent("event:downloads-updated", GetDownloadsQueueList());
        }

        private void OnDownloadQueueChanged(object? sender, DownloadRequest req)
        {
            PushEvent("event:downloads-updated", GetDownloadsQueueList());
        }

        public Task<bool> TryHandleMessageAsync(string rawJson)
        {
            IpcEnvelope? env;
            try
            {
                env = JsonSerializer.Deserialize<IpcEnvelope>(rawJson);
            }
            catch
            {
                return Task.FromResult(false);
            }
            if (env == null || string.IsNullOrWhiteSpace(env.Type))
                return Task.FromResult(false);
            if (env.Type.StartsWith("ui:", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(false); // Handled by MainWindow directly
            var reqId = env.RequestId;
            var type = env.Type;
            var payload = env.Payload;
            _ = Task.Run(async () =>
            {
                try
                {
                    object? result = await DispatchRequestAsync(type, payload);
                    SendResponse(type, reqId, true, result, null);
                }
                catch (Exception ex)
                {
                    LogService.Error("IPC", $"Error handling {type}", ex);
                    SendResponse(type, reqId, false, null, ex.Message);
                }
            });
            return Task.FromResult(true);
        }

        private void SendResponse(string type, string? requestId, bool ok, object? payload, string? error)
        {
            if (string.IsNullOrEmpty(requestId)) return;
            var resp = new IpcResponseEnvelope
            {
                Type = $"{type}:response",
                RequestId = requestId,
                Ok = ok,
                Error = error,
                Payload = payload
            };
            var json = JsonSerializer.Serialize(resp);
            _dispatcher.InvokeAsync(() => _sendToWebUi(json));
        }

        private async Task<object?> DispatchRequestAsync(string type, JsonElement? payload)
        {
            return type switch
            {
                "backend:bootstrap" => await BuildBootstrapAsync(),
                // 1. EKSTENSI (EXTENSIONS)
                "ext:list" => await GetExtensionsDtosAsync(GetBool(payload, "forceRefresh") || GetBool(payload, "refreshRemote")),
                "ext:install" => await HandleInstallExtensionAsync(GetString(payload, "id")),
                "ext:update" => await HandleInstallExtensionAsync(GetString(payload, "id")),
                "ext:uninstall" => await HandleUninstallExtensionAsync(GetString(payload, "id")),
                "ext:verify-cloudflare" => await HandleVerifyCloudflareAsync(GetString(payload, "id"), GetString(payload, "baseUrl")),
                "ext:install-manual" => await HandleManualPluginInstallAsync(),
                // 2. JELAJAHI SUMBER (BROWSE & SOURCE CATALOG FEED)
                "browse:refresh-status" => await HandleRefreshSourceStatusAsync(),
                "source:fetch-feed" => await HandleFetchSourceFeedAsync(payload),
                // 3. MANGA DETAIL & CHAPTER MANAGEMENT
                "manga:get-detail" => await HandleGetMangaDetailAsync(payload),
                "manga:toggle-library" => await HandleToggleLibraryAsync(payload),
                "manga:toggle-bookmark" => await HandleToggleBookmarkAsync(payload),
                "manga:mark-chapters" => await HandleMarkChaptersAsync(payload),
                // 4. READER (PEMBACA KOMIK)
                "reader:get-pages" => await HandleGetReaderPagesAsync(payload),
                "reader:save-progress" => await HandleSaveReaderProgressAsync(payload),
                // 5. PUSTAKA (LIBRARY) & KATEGORI
                "library:get-list" => await GetLibraryAndHistoryMangaDtosAsync(),
                "library:sync-all" => await HandleSyncLibraryAsync(GetBool(payload, "forceRefresh", true)),
                "library:get-categories" => await HandleGetCategoriesAsync(),
                "library:add-category" => await HandleAddCategoryAsync(GetString(payload, "name")),
                "library:delete-category" => await HandleDeleteCategoryAsync(GetLong(payload, "categoryId")),
                "library:set-manga-categories" => await HandleSetMangaCategoriesAsync(payload),
                "library:get-statistics" => await LibraryService.GetReadingStatisticsAsync(),
                // 6. PEMBARUAN (UPDATES)
                "updates:get-list" => await GetActivityUpdatesDtosAsync(),
                "updates:clear-all" => await HandleClearAllUpdatesAsync(),
                "updates:mark-all-read" => await HandleMarkAllUpdatesReadAsync(),
                // 7. RIWAYAT (HISTORY)
                "history:get-list" => await GetHistoryMangaDtosAsync(),
                "history:remove-item" => await HandleRemoveHistoryItemAsync(GetString(payload, "mangaId")),
                "history:clear-all" => await HandleClearAllHistoryAsync(),
                // 8. UNDUHAN (DOWNLOADS)
                "downloads:get-list" => GetDownloadsQueueList(),
                "downloads:enqueue" => await HandleEnqueueDownloadAsync(payload),
                "downloads:toggle-pause" => HandleTogglePauseDownload(payload),
                "downloads:pause-all" => HandlePauseAllDownloads(),
                "downloads:resume-all" => HandleResumeAllDownloads(),
                "downloads:cancel" => await HandleCancelDownloadAsync(payload),
                "downloads:clear-completed" => HandleClearCompletedDownloads(),
                "downloads:delete-chapter" => await HandleDeleteChapterDownloadAsync(payload),
                "downloads:delete-manga" => await HandleDeleteMangaDownloadsAsync(GetString(payload, "mangaId")),
                // 9. PENGATURAN, CADANGAN, PENGUMUMAN & MYANIMELIST
                "announcements:get-list" => await HandleGetAnnouncementsAsync(GetBool(payload, "force")),
                "announcements:mark-read" => HandleMarkAnnouncementRead(GetString(payload, "id")),
                "settings:update" => HandleUpdateSetting(payload),
                "settings:refresh-covers" => await HandleRefreshLibraryCoversAsync(),
                "settings:clear-cache" => await HandleClearImageCacheAsync(),
                "settings:clear-non-library" => await HandleClearNonLibraryCacheAsync(),
                "settings:export-logs" => await HandleExportLogsAsync(),
                "settings:reset-all" => HandleResetSettings(),
                "data:clear-all" => await HandleClearAllDataAsync(),
                "backup:create" => await HandleCreateBackupAsync(),
                "backup:restore" => await HandleRestoreBackupAsync(),
                "mal:toggle-connect" => await HandleMalToggleConnectAsync(),
                "mal:search" => await HandleMalSearchAsync(GetString(payload, "query")),
                "mal:get-track" => await HandleMalGetTrackAsync(payload),
                "mal:save-track" => await HandleMalSaveTrackAsync(payload),
                "mal:unbind-track" => await HandleMalUnbindTrackAsync(GetString(payload, "mangaId")),
                "app:check-update" => await HandleCheckAppUpdateAsync(),
                "app:install-update" => await HandleInstallAppUpdateAsync(GetString(payload, "downloadUrl")),
                "feedback:submit" => await HandleSubmitFeedbackAsync(payload),
                _ => null
            };
        }

        /* =========================================================================
         * JSON PAYLOAD EXTRACTORS
         * ========================================================================= */
        private static string ComputeShortHash(string input)
        {
            var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input ?? string.Empty));
            return Convert.ToHexString(bytes).ToLowerInvariant()[..10];
        }

        private static string GetString(JsonElement? elem, string prop, string defaultVal = "")
        {
            if (elem.HasValue && elem.Value.ValueKind == JsonValueKind.Object &&
                elem.Value.TryGetProperty(prop, out var v))
            {
                if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? defaultVal;
                if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
            }
            return defaultVal;
        }

        private static int GetInt(JsonElement? elem, string prop, int defaultVal = 0)
        {
            if (elem.HasValue && elem.Value.ValueKind == JsonValueKind.Object &&
                elem.Value.TryGetProperty(prop, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int s)) return s;
            }
            return defaultVal;
        }

        private static long GetLong(JsonElement? elem, string prop, long defaultVal = 0)
        {
            if (elem.HasValue && elem.Value.ValueKind == JsonValueKind.Object &&
                elem.Value.TryGetProperty(prop, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l)) return l;
                if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out long s)) return s;
            }
            return defaultVal;
        }

        private static float GetFloat(JsonElement? elem, string prop, float defaultVal = 0f)
        {
            if (elem.HasValue && elem.Value.ValueKind == JsonValueKind.Object &&
                elem.Value.TryGetProperty(prop, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out float f)) return f;
                if (v.ValueKind == JsonValueKind.String && float.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out float s)) return s;
            }
            return defaultVal;
        }

        private static double GetDouble(JsonElement? elem, string prop, double defaultVal = 0.0)
        {
            if (elem.HasValue && elem.Value.ValueKind == JsonValueKind.Object &&
                elem.Value.TryGetProperty(prop, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) return d;
                if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double s)) return s;
            }
            return defaultVal;
        }

        private static bool GetBool(JsonElement? elem, string prop, bool defaultVal = false)
        {
            if (elem.HasValue && elem.Value.ValueKind == JsonValueKind.Object &&
                elem.Value.TryGetProperty(prop, out var v))
            {
                if (v.ValueKind == JsonValueKind.True) return true;
                if (v.ValueKind == JsonValueKind.False) return false;
            }
            return defaultVal;
        }

        private static void SafeDispose(Action action)
        {
            try
            {
                action();
            }
            catch { }
        }

        public void Dispose()
        {
            if (!_suppressDownloadSaveOnDispose)
            {
                SafeDispose(() => DownloadService.SaveQueueSynchronously());
            }
            SafeDispose(() => DownloadService.Dispose());
            SafeDispose(() => ExtensionRepoService.Dispose());
            SafeDispose(() => SourceManager.Dispose());
            SafeDispose(() => ImageProxy.Dispose());
            SafeDispose(() => MalService.Dispose());
            SafeDispose(() => SettingsService.Dispose());
            SafeDispose(() => AnnouncementService.Dispose());
            SafeDispose(() => UpdateService.Dispose());
            SafeDispose(() => SourceStatusService.Dispose());
            SafeDispose(() => NetworkService.Dispose());
            SafeDispose(() => LogService.CloseAndFlush());
        }
    }
}
