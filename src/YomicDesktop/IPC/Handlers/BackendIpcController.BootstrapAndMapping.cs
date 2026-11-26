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
         * BOOTSTRAP & MAPPING HELPERS
         * ========================================================================= */
        public async Task<AppBootstrapDto> BuildBootstrapAsync()
        {
            await SourceManager.EnsureExtensionsLoadedAsync();
            var extensionsTask = GetExtensionsDtosAsync(false);
            var mangasTask = GetLibraryAndHistoryMangaDtosAsync();
            var updatesTask = GetActivityUpdatesDtosAsync();
            var categoriesTask = LibraryService.GetCategoriesAsync();
            // Never block initial WebView navigation on remote announcement HTTP fetches (up to 16s);
            // return cached announcements immediately and refresh asynchronously via event:announcements-updated.
            var announcements = AnnouncementService.CachedAnnouncements;
            if (announcements.Count == 0 && !SettingsService.IsOfflineMode)
            {
                _ = Task.Run(async () =>
                {
                    try { await AnnouncementService.FetchAnnouncementsAsync(false); }
                    catch (Exception ex) { LogService.Debug("IPC", $"Background announcement fetch failed: {ex.Message}"); }
                });
            }
            await Task.WhenAll(extensionsTask, mangasTask, updatesTask, categoriesTask);
            var categories = await categoriesTask;
            return new AppBootstrapDto
            {
                MangaList = await mangasTask,
                ActivityUpdates = await updatesTask,
                Downloads = GetDownloadsQueueList(),
                Extensions = await extensionsTask,
                Categories = MapCategoriesToDtos(categories),
                Settings = BuildSettingsDictionary(),
                Announcements = MapAnnouncementsToDtos(announcements),
                HasUnreadAnnouncements = AnnouncementService.HasUnreadAnnouncements()
            };
        }

        private static List<CategoryDto> MapCategoriesToDtos(IEnumerable<Category> list)
        {
            return list.Select(c => new CategoryDto
            {
                Id = c.Id,
                Name = c.Name,
                Color = c.Color,
                SortOrder = c.SortOrder,
                IsDefault = c.IsDefault,
                UpdateExcluded = c.UpdateExcluded
            }).ToList();
        }

        private static List<AnnouncementDto> MapAnnouncementsToDtos(IEnumerable<Announcement> list)
        {
            return list.Select(a => new AnnouncementDto
            {
                Id = a.Id,
                Title = a.Title,
                Body = a.Body,
                Date = a.Date,
                Type = a.Type,
                Url = a.Url,
                HasUrl = a.HasUrl,
                TypeDisplay = a.TypeDisplay,
                BadgeBackground = a.BadgeBackground
            }).ToList();
        }

        private bool IsEn => string.Equals(SettingsService.AppLanguage, "en", StringComparison.OrdinalIgnoreCase);
        private string L(string id, string en) => IsEn ? en : id;
        private Dictionary<string, object?> BuildSettingsDictionary()
        {
            string lastBackupText = !string.IsNullOrWhiteSpace(SettingsService.LastBackupTime)
                ? (!string.IsNullOrWhiteSpace(SettingsService.LastBackupSize)
                    ? L($"Cadangan terakhir: {SettingsService.LastBackupTime} ({SettingsService.LastBackupSize})", $"Last backup: {SettingsService.LastBackupTime} ({SettingsService.LastBackupSize})")
                    : L($"Cadangan terakhir: {SettingsService.LastBackupTime}", $"Last backup: {SettingsService.LastBackupTime}"))
                : L("Siap dicadangkan (.yomicbackup)", "Ready to back up (.yomicbackup)");
            string malUser = MalService.IsConnected
                ? (!string.IsNullOrWhiteSpace(SettingsService.MalUsername) ? SettingsService.MalUsername : "MyAnimeList User")
                : "";
            return new Dictionary<string, object?>
            {
                ["defaultReaderMode"] = SettingsService.DefaultReaderMode switch
                {
                    1 => "single",
                    2 => "dual",
                    _ => "webtoon"
                },
                ["readingDirection"] = string.IsNullOrWhiteSpace(SettingsService.ReadingDirection) ? "ltr" : SettingsService.ReadingDirection,
                ["readerFitMode"] = string.IsNullOrWhiteSpace(SettingsService.ReaderFitMode) ? "fit-width" : SettingsService.ReaderFitMode,
                ["isDarkMode"] = SettingsService.IsDarkMode,
                ["autoDarkReader"] = SettingsService.AutoDarkReader,
                ["showNsfw"] = SettingsService.ShowNsfwSources,
                ["blurExplicitCovers"] = SettingsService.SecureScreen,
                ["dohProvider"] = SettingsService.DnsOverHttpsProvider switch
                {
                    1 => "Cloudflare (1.1.1.1)",
                    2 => "Google (8.8.8.8)",
                    3 => "AdGuard (94.140.14.14)",
                    _ => "Nonaktif"
                },
                ["downloadsFolder"] = DownloadPathService.BaseDirectory,
                ["dbSizeText"] = CalculateStorageSizeText(),
                ["lastBackupText"] = lastBackupText,
                ["isMalConnected"] = MalService.IsConnected,
                ["malConnectedUser"] = malUser,
                ["autoSyncTracker"] = SettingsService.AutoSyncTracker,
                ["preloadNextChapter"] = SettingsService.PreloadNextChapter,
                ["autoDownloadNextChapter"] = SettingsService.AutoDownloadNextChapter,
                ["skipFilteredChapters"] = SettingsService.SkipFilteredChapters,
                ["saveDownloadsAsCbz"] = SettingsService.SaveDownloadsAsCbz,
                ["autoDeleteAfterRead"] = SettingsService.AutoDeleteAfterRead,
                ["downloadSpeedMode"] = string.IsNullOrWhiteSpace(SettingsService.DownloadSpeedMode) ? "fast" : SettingsService.DownloadSpeedMode,
                ["maxReaderCacheSize"] = SettingsService.MaxCacheSizeMb switch
                {
                    250 => "250MB",
                    1024 => "1GB",
                    2048 => "2GB",
                    _ => "500MB"
                },
                ["showUnreadBadges"] = SettingsService.ShowUnreadBadges,
                ["appLanguage"] = string.Equals(SettingsService.AppLanguage, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "id",
                ["mangaDexLanguage"] = string.Equals(SettingsService.MangaDexLanguage, "id", StringComparison.OrdinalIgnoreCase) ? "id" : "en",
                ["enableImageOptimization"] = SettingsService.EnableImageOptimization,
                ["enableAutoCropBorders"] = SettingsService.EnableAutoCropBorders,
                ["maxImageDimension"] = SettingsService.MaxImageDimension,
                ["enableImageSharpening"] = SettingsService.EnableImageSharpening,
                ["autoSyncStartup"] = SettingsService.UpdateOnStart,
                ["smartUpdate"] = SettingsService.UseSmartUpdate,
                ["backgroundUpdateInterval"] = SettingsService.AutoUpdateIntervalHours switch
                {
                    0 => "off",
                    6 => "6h",
                    12 => "12h",
                    24 => "24h",
                    _ => "12h"
                },
                ["isOfflineMode"] = SettingsService.IsOfflineMode,
                ["sourceStatusInterval"] = SettingsService.SourceStatusRefreshIntervalMinutes switch
                {
                    0 => "off",
                    15 => "15m",
                    30 => "30m",
                    60 => "1h",
                    _ => "30m"
                },
                ["customHotkeys"] = ParseCustomHotkeys(SettingsService.CustomHotkeysJson)
            };
        }

        private static Dictionary<string, string> ParseCustomHotkeys(string json)
        {
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (map != null) return map;
                }
                catch { }
            }
            return new Dictionary<string, string>();
        }

        private static string CalculateStorageSizeText()
        {
            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var yomicDir = Path.Combine(localAppData, "Yomic");
                if (!Directory.Exists(yomicDir)) return "0.0 MB";
                long bytes = 0;
                var coversDir = Path.Combine(yomicDir, "covers");
                if (Directory.Exists(coversDir))
                {
                    bytes += new DirectoryInfo(coversDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                }
                var cacheDir = Path.Combine(yomicDir, "Cache");
                if (Directory.Exists(cacheDir))
                {
                    bytes += new DirectoryInfo(cacheDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                }
                double mb = bytes / (1024.0 * 1024.0);
                return $"{mb:0.1} MB";
            }
            catch
            {
                return "0.0 MB";
            }
        }

        private static string MakeMangaFrontendId(Manga m)
        {
            if (m.Id > 0) return $"db-{m.Id}";
            var hashBytes = MD5.HashData(Encoding.UTF8.GetBytes($"{m.Source}:{m.Url}"));
            var hex = Convert.ToHexString(hashBytes).ToLowerInvariant()[..12];
            return $"src-{m.Source}-{hex}";
        }

        private MangaDto MapMangaToDto(Manga m, bool includeChapters = false)
        {
            var id = MakeMangaFrontendId(m);
            if (_discoveredManga.Count >= 2000 && !_discoveredManga.ContainsKey(id))
            {
                // Evict transient browsed entries (src-*) first to keep memory bounded during long browsing sessions
                foreach (var key in _discoveredManga.Keys.Where(k => k.StartsWith("src-", StringComparison.Ordinal)).Take(500).ToList())
                {
                    _discoveredManga.TryRemove(key, out _);
                }
                if (_discoveredManga.Count >= 2000)
                {
                    _discoveredManga.Clear();
                }
            }
            _discoveredManga[id] = m;
            var src = SourceManager.GetSource(m.Source);
            string sourceName = src?.Name?.Replace("Yomic:", "", StringComparison.OrdinalIgnoreCase).Trim() ?? "Sumber Komik";
            var chapters = m.Chapters?.ToList() ?? new List<Chapter>();
            var naturalComparer = new NaturalStringComparer(descending: true);
            var sortedChapters = chapters
                .OrderByDescending(c => c.ChapterNumber)
                .ThenBy(c => c.Name, naturalComparer)
                .ToList();
            int totalCh = sortedChapters.Count;
            int unreadCount = sortedChapters.Count(c => !c.Read);
            int downloadedCount = 0;
            foreach (var ch in sortedChapters)
            {
                bool isDl = ch.IsDownloaded || DownloadPathService.IsChapterDownloaded(m, ch);
                ch.IsDownloaded = isDl;
                if (isDl) downloadedCount++;
            }
            var lastReadChObj = sortedChapters.FirstOrDefault(c => c.Read || c.LastPageRead > 0)
                                ?? sortedChapters.LastOrDefault();
            float lastReadChNum = lastReadChObj != null && lastReadChObj.ChapterNumber > 0
                ? lastReadChObj.ChapterNumber
                : (totalCh > 0 ? 1 : 1);
            int lastReadTotalPages = 0;
            if (lastReadChObj != null && !string.IsNullOrEmpty(lastReadChObj.Url) && _chapterTotalPagesCache.TryGetValue(MakeChapterCacheKey(m.Source, lastReadChObj.Url), out int cachedPages))
            {
                lastReadTotalPages = cachedPages;
            }
            else if (_mangaLastTotalPagesCache.TryGetValue(m.Id, out int cachedMangaPages))
            {
                lastReadTotalPages = cachedMangaPages;
            }
            int lastReadPage = 1;
            if (lastReadChObj != null && lastReadChObj.LastPageRead > 0)
            {
                long rawVal = lastReadChObj.LastPageRead;
                if (rawVal > 1000)
                {
                    if (lastReadTotalPages > 0)
                    {
                        lastReadPage = (int)Math.Clamp(Math.Ceiling((rawVal / 1_000_000.0) * lastReadTotalPages), 1, lastReadTotalPages);
                    }
                    else
                    {
                        lastReadPage = 1;
                    }
                }
                else
                {
                    lastReadPage = (int)rawVal;
                }
            }
            var genres = m.Genre?.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().ToList() ?? new List<string>();
            string type = DetectComicType(genres, sourceName, m.Url);
            string statusStr = m.Status == Manga.COMPLETED || m.Status == Manga.PUBLISHING_FINISHED
                ? "Completed"
                : "Ongoing";
            string category = "reading";
            if (statusStr == "Completed" && unreadCount == 0 && totalCh > 0)
                category = "completed";
            else if (totalCh > 0 && unreadCount == totalCh && m.LastViewed == 0)
                category = "plan";
            var (rating, cleanSynopsis) = ExtractRatingAndCleanDescription(m.Description);
            uint paletteHash = unchecked((uint)(m.Title?.GetHashCode() ?? 0));
            var pal = CoverPalettes[paletteHash % CoverPalettes.Length];
            var track = m.Tracks?.FirstOrDefault();
            var dto = new MangaDto
            {
                Id = id,
                DbId = m.Id,
                SourceId = m.Source,
                Url = m.Url,
                Title = string.IsNullOrWhiteSpace(m.Title) ? "Tanpa Judul" : m.Title,
                AltTitle = sourceName,
                Author = string.IsNullOrWhiteSpace(m.Author) ? "Unknown" : m.Author,
                Artist = string.IsNullOrWhiteSpace(m.Artist) ? (string.IsNullOrWhiteSpace(m.Author) ? "Studio" : m.Author) : m.Artist,
                Status = statusStr,
                Type = type,
                Rating = rating,
                UnreadCount = unreadCount,
                DownloadedCount = downloadedCount,
                TotalChapters = totalCh,
                LastReadChapter = lastReadChNum,
                LastReadChapterUrl = lastReadChObj?.Url ?? string.Empty,
                LastReadPage = lastReadPage,
                LastReadTotalPages = lastReadTotalPages,
                LastReadTimeAgo = FormatRelativeTime(m.LastViewed),
                LastViewedEpoch = m.LastViewed,
                LastUpdateEpoch = m.LastUpdate,
                SourceName = sourceName,
                Category = category,
                CategoryIds = m.Categories?.Select(c => c.Id).ToList() ?? new List<long>(),
                Genres = genres.Count > 0 ? genres : new List<string> { type, "Action" },
                Synopsis = string.IsNullOrWhiteSpace(cleanSynopsis)
                    ? L("Klik Segarkan untuk memuat sinopsis dan daftar bab terbaru dari sumber.", "Click Refresh to load the latest synopsis and chapter list from the source.")
                    : cleanSynopsis,
                CoverPalette = new[] { pal.Bg, pal.Primary, pal.Secondary },
                CoverAccent = pal.Primary,
                CoverUrl = WebResourceImageProxy.BuildCoverProxyUrl(m.Id, m.ThumbnailUrl, m.Source),
                RawCoverUrl = m.ThumbnailUrl ?? string.Empty,
                InLibrary = m.Favorite,
                MalTracked = track != null,
                MalId = track?.RemoteId ?? 0,
                MalTitle = track?.Title ?? string.Empty,
                MalStatus = track?.Status ?? "reading",
                MalScore = (int)(track?.Score ?? 9)
            };
            if (includeChapters || sortedChapters.Count > 0)
            {
                int fallbackNum = sortedChapters.Count;
                dto.Chapters = sortedChapters.Select((c, idx) =>
                {
                    float num = c.ChapterNumber > 0 ? c.ChapterNumber : Math.Max(1, fallbackNum - idx);
                    return new ChapterDto
                    {
                        Id = c.Id > 0 ? c.Id : idx + 1,
                        DbId = c.Id,
                        Number = (float)Math.Round(num, 2),
                        Title = CleanChapterSubTitle(c.Name, num),
                        Url = c.Url,
                        ReleaseDate = c.DateUpload > 0 ? FormatRelativeDate(c.DateUpload) : "Tersedia",
                        DateUpload = c.DateUpload,
                        IsRead = c.Read,
                        IsDownloaded = c.IsDownloaded,
                        IsBookmarked = c.Bookmark,
                        LastPageRead = (int)c.LastPageRead,
                        Scanlator = c.Scanlator
                    };
                }).ToList();
            }
            return dto;
        }

        private static string CleanChapterSubTitle(string rawName, float chNum)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;
            var cleaned = Regex.Replace(rawName, @"^(?:Chapter|Ch\.?|Bab|Episode|Ep\.?)\s*[0-9]+(?:\.[0-9]+)?\s*[-:–—]?\s*", "", RegexOptions.IgnoreCase).Trim();

            if (string.Equals(cleaned, rawName, StringComparison.OrdinalIgnoreCase))

            {

                var numStr = chNum.ToString(CultureInfo.InvariantCulture);

                if (rawName.Contains(numStr)) return string.Empty;

            }

            return cleaned;

        }

        private static (double Rating, string CleanDescription) ExtractRatingAndCleanDescription(string? description)
        {
            if (string.IsNullOrWhiteSpace(description))
                return (0.0, string.Empty);

            var match = Regex.Match(description, @"Rating:\s*([0-9.]+)(/10)?", RegexOptions.IgnoreCase);
            if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
            {
                var cleaned = description.Replace(match.Value, "").Trim();
                cleaned = Regex.Replace(cleaned, @"\r?\n\r?\n+", "\n\n");
                return (Math.Clamp(val, 1.0, 10.0), cleaned);
            }

            return (0.0, description.Trim());
        }

        private static string DetectComicType(List<string> genres, string sourceName, string url)
        {
            if (genres.Any(g => g.Contains("Manhwa", StringComparison.OrdinalIgnoreCase))) return "Manhwa";
            if (genres.Any(g => g.Contains("Manhua", StringComparison.OrdinalIgnoreCase))) return "Manhua";
            if (genres.Any(g => g.Contains("Manga", StringComparison.OrdinalIgnoreCase))) return "Manga";

            if (sourceName.Contains("Manhwa", StringComparison.OrdinalIgnoreCase) || url.Contains("manhwa", StringComparison.OrdinalIgnoreCase))
                return "Manhwa";
            if (sourceName.Contains("Manhua", StringComparison.OrdinalIgnoreCase) || url.Contains("manhua", StringComparison.OrdinalIgnoreCase))
                return "Manhua";
            if (sourceName.Contains("Manga", StringComparison.OrdinalIgnoreCase) || url.Contains("manga", StringComparison.OrdinalIgnoreCase))
                return "Manga";

            return "Manga";
        }

        private static string FormatRelativeTime(long epochMs)

        {

            if (epochMs <= 0) return "Belum dibaca";

            try

            {

                var dt = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).LocalDateTime;

                var diff = DateTime.Now - dt;

                if (diff.TotalMinutes < 1) return "Baru saja";

                if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} menit lalu";

                if (diff.TotalHours < 24 && DateTime.Now.Date == dt.Date) return $"{(int)diff.TotalHours} jam lalu";

                if (DateTime.Now.Date.AddDays(-1) == dt.Date) return "Kemarin";

                if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} hari lalu";

                return dt.ToString("dd MMM yyyy", new CultureInfo("id-ID"));

            }

            catch

            {

                return "Baru saja";

            }

        }

        private static string FormatRelativeDate(long epochMs)

        {

            if (epochMs <= 0) return "Tersedia";

            try

            {

                // Some sources return seconds instead of ms

                if (epochMs < 10_000_000_000L) epochMs *= 1000L;

                var dt = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).LocalDateTime;

                var diff = DateTime.Now - dt;

                if (diff.TotalHours < 1) return "Baru saja";

                if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} jam lalu";

                if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} hari lalu";

                return dt.ToString("dd MMM yyyy", new CultureInfo("id-ID"));

            }

            catch

            {

                return "Tersedia";

            }

        }

    }
}
