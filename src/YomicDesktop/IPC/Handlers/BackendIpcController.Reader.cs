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
         * 4. READER (PEMBACA KOMIK)
         * ========================================================================= */
        private async Task<ReaderPagesResultDto> HandleGetReaderPagesAsync(JsonElement? payload)
        {
            string mangaId = GetString(payload, "mangaId");
            string chapterUrl = GetString(payload, "chapterUrl");
            float chapterNumber = GetFloat(payload, "chapterNumber", 1);
            string mangaUrl = GetString(payload, "mangaUrl");
            long sourceId = GetLong(payload, "sourceId");
            string sourceName = GetString(payload, "sourceName");
            bool forceRefresh = GetBool(payload, "forceRefresh", false);
            bool manualVerify = GetBool(payload, "manualVerify", false);
            var manga = await ResolveMangaAsync(mangaId, mangaUrl, sourceId);
            long resolvedSourceId = sourceId != 0 ? sourceId : manga?.Source ?? 0;
            var source = ResolveSourceSafely(resolvedSourceId, manga?.Url ?? mangaUrl, chapterUrl, manga, sourceName);
            if (source != null) resolvedSourceId = source.Id;
            // If manga was resolved but chapters are empty, dynamically fetch chapters from source
            if (manga != null && (manga.Chapters == null || manga.Chapters.Count == 0) &&
                source != null && !string.IsNullOrEmpty(manga.Url) && !SettingsService.IsOfflineMode)
            {
                try
                {
                    var fetchedChapters = await source.GetChapterListAsync(manga.Url);
                    if (fetchedChapters != null && fetchedChapters.Count > 0)
                    {
                        manga.Chapters = fetchedChapters;
                        if (manga.Id > 0)
                        {
                            using var db = new MangaDbContext();
                            var dbManga = await db.Mangas.Include(m => m.Chapters).FirstOrDefaultAsync(m => m.Id == manga.Id);
                            if (dbManga != null)
                            {
                                await LibraryService.UpdateChaptersAsync(dbManga, fetchedChapters.ToList(), isInitialLoad: false);
                            }
                        }
                    }
                }
                catch { }
            }
            Chapter? chapter = null;
            if (manga?.Chapters != null && manga.Chapters.Count > 0)
            {
                chapter = manga.Chapters.FirstOrDefault(c =>
                    (!string.IsNullOrEmpty(chapterUrl) && string.Equals(c.Url, chapterUrl, StringComparison.OrdinalIgnoreCase)) ||
                    Math.Abs(c.ChapterNumber - chapterNumber) < 0.01f);
                if (chapter == null)
                {
                    chapter = manga.Chapters.FirstOrDefault(c => Math.Abs(c.ChapterNumber - chapterNumber) < 1.0f) ?? manga.Chapters.First();
                }
            }
            string targetChUrl = chapter?.Url ?? chapterUrl;
            string chTitle = chapter?.Name ?? $"Chapter {chapterNumber}";
            string frontendMangaId = manga != null ? MakeMangaFrontendId(manga) : mangaId;
            long savedRaw = 0;
            if (!string.IsNullOrEmpty(targetChUrl))
            {
                savedRaw = await LibraryService.GetLastPageReadRawAsync(targetChUrl, manga?.Id ?? 0, chapterNumber);
            }
            if (savedRaw <= 0 && chapter != null && chapter.LastPageRead > 0)
            {
                savedRaw = chapter.LastPageRead;
            }
            long cacheSourceId = manga?.Source ?? sourceId;
            if (savedRaw <= 0)
            {
                if (!string.IsNullOrEmpty(targetChUrl) && _chapterLastPageReadCache.TryGetValue(MakeChapterCacheKey(cacheSourceId, targetChUrl), out long cachedRaw))
                {
                    savedRaw = cachedRaw;
                }
                else if (_chapterLastPageReadCache.TryGetValue($"{mangaId}_{chapterNumber}", out long cachedNum))
                {
                    savedRaw = cachedNum;
                }
                else if (!string.IsNullOrEmpty(mangaUrl) && _chapterLastPageReadCache.TryGetValue($"{cacheSourceId}|{mangaUrl}_{chapterNumber}", out long cachedUrlNum))
                {
                    savedRaw = cachedUrlNum;
                }
            }
            double savedScrollPercent = 0;
            int savedPage = 1;
            if (savedRaw > 1000) // Percentage (Webtoon, exact Yomic v1 formula)
            {
                savedScrollPercent = savedRaw / 1_000_000.0;
            }
            else if (savedRaw > 0) // Page index (Paged/Dual)
            {
                savedPage = (int)savedRaw;
            }
            // Record LastViewed immediately when opening Reader so History (Riwayat) stays up-to-date
            if (manga != null && manga.Id > 0)
            {
                try
                {
                    await MangaDbContext.DatabaseWriteGate.WaitAsync();
                    try
                    {
                        using var db = new MangaDbContext();
                        var dbManga = await db.Mangas.FindAsync(manga.Id);
                        if (dbManga != null)
                        {
                            dbManga.LastViewed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            await db.SaveChangesAsync();
                        }
                    }
                    finally
                    {
                        MangaDbContext.DatabaseWriteGate.Release();
                    }
                }
                catch { }
            }
            // 1. Check Offline Downloaded Files First
            if (manga != null && chapter != null)
            {
                var localDir = DownloadPathService.FindCompletedChapterDirectory(manga, chapter);
                if (string.IsNullOrEmpty(localDir))
                {
                    var localCbz = DownloadPathService.FindCompletedChapterCbz(manga, chapter);
                    if (!string.IsNullOrEmpty(localCbz))
                    {
                        localDir = DownloadPathService.ExtractCbzToCacheDirectory(localCbz);
                    }
                }
                if (!string.IsNullOrEmpty(localDir))
                {
                    var localFiles = DownloadPathService.GetReadableFiles(localDir);
                    if (localFiles.Count > 0)
                    {
                        int totalPages = localFiles.Count;
                        int resolvedPage = savedPage;
                        if (savedScrollPercent > 0 && totalPages > 0)
                        {
                            resolvedPage = (int)(savedScrollPercent * Math.Max(1, totalPages - 1)) + 1;
                            resolvedPage = Math.Clamp(resolvedPage, 1, totalPages);
                        }
                        return new ReaderPagesResultDto
                        {
                            MangaId = frontendMangaId,
                            ChapterUrl = targetChUrl,
                            ChapterNumber = chapter.ChapterNumber > 0 ? chapter.ChapterNumber : chapterNumber,
                            ChapterTitle = chTitle,
                            IsOffline = true,
                            LastPageRead = resolvedPage,
                            ScrollPercent = savedScrollPercent,
                            Pages = localFiles.Select(f => WebResourceImageProxy.BuildPageProxyUrl(f, manga.Source, isLocalFile: true)).ToList()
                        };
                    }
                }
            }
            // 2. Fetch Online from IMangaSource
            bool requiresVerification = false;
            if (!SettingsService.IsOfflineMode)
            {
                // If targetChUrl is still empty and we have a source + mangaUrl, fetch chapter list directly
                if (string.IsNullOrEmpty(targetChUrl) && source != null && !string.IsNullOrEmpty(mangaUrl))
                {
                    try
                    {
                        var chList = await source.GetChapterListAsync(mangaUrl);
                        if (chList != null && chList.Count > 0)
                        {
                            var match = chList.FirstOrDefault(c => Math.Abs(c.ChapterNumber - chapterNumber) < 0.01f) ?? chList.First();
                            targetChUrl = match.Url;
                            chTitle = match.Name ?? chTitle;
                        }
                    }
                    catch { }
                }
                if (source != null && !string.IsNullOrEmpty(targetChUrl))
                {
                    try
                    {
                        string preloadKey = $"{source.Id}|{targetChUrl}";
                        if (forceRefresh)
                        {
                            _preloadedPageListCache.TryRemove(preloadKey, out _);
                        }

                        List<string> rawPages;
                        if (!forceRefresh && TryGetPreloadedPageList(preloadKey, out var cachedPreloaded))
                        {
                            rawPages = cachedPreloaded;
                        }
                        else if (source is JsMangaSource jsSource)
                        {
                            var statusRes = await jsSource.GetPageListWithStatusAsync(targetChUrl);
                            rawPages = statusRes.Pages;
                            requiresVerification = statusRes.RequiresVerification;
                            if (rawPages.Count == 0 && requiresVerification)
                            {
                                string fullChapterUrl = targetChUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                    ? targetChUrl
                                    : $"{jsSource.BaseUrl.TrimEnd('/')}/{targetChUrl.TrimStart('/')}";
                                var extracted = await Views.WebViewPageExtractorWindow.ExtractPagesAsync(
                                    _ownerWindow,
                                    _dispatcher,
                                    fullChapterUrl,
                                    jsSource.Name,
                                    jsSource.ChapterReadySelector,
                                    jsSource.PageWebViewExtractScript,
                                    L,
                                    startHidden: !manualVerify);
                                if (extracted.Count > 0)
                                {
                                    string refererSuffix = $"|Referer={jsSource.BaseUrl.TrimEnd('/')}/";
                                    rawPages = extracted
                                        .Select(u => u.Contains("|Referer=", StringComparison.OrdinalIgnoreCase) ? u : u + refererSuffix)
                                        .ToList();
                                    requiresVerification = false;
                                }
                            }
                        }
                        else
                        {
                            rawPages = await source.GetPageListAsync(targetChUrl);
                        }
                        if (rawPages.Count > 0)
                        {
                            StorePreloadedPageList(preloadKey, rawPages);
                        }
                        var proxiedPages = rawPages
                            .Where(p => !string.IsNullOrWhiteSpace(p))
                            .Select(p => WebResourceImageProxy.BuildPageProxyUrl(p, resolvedSourceId, isLocalFile: false, force: forceRefresh))
                            .ToList();
                        if (proxiedPages.Count > 0)
                        {
                            // Auto-download next chapter if enabled in Settings
                            if (SettingsService.AutoDownloadNextChapter && manga?.Chapters != null)
                            {
                                var nextCh = manga.Chapters
                                    .Where(c => c.ChapterNumber > chapterNumber)
                                    .OrderBy(c => c.ChapterNumber)
                                    .FirstOrDefault();
                                if (nextCh != null && !DownloadPathService.IsChapterDownloaded(manga, nextCh))
                                {
                                    DownloadService.QueueDownload(manga, nextCh);
                                }
                            }
                            int totalPages = proxiedPages.Count;
                            int resolvedPage = savedPage;
                            if (savedScrollPercent > 0 && totalPages > 0)
                            {
                                resolvedPage = (int)(savedScrollPercent * Math.Max(1, totalPages - 1)) + 1;
                                resolvedPage = Math.Clamp(resolvedPage, 1, totalPages);
                            }
                            return new ReaderPagesResultDto
                            {
                                MangaId = frontendMangaId,
                                ChapterUrl = targetChUrl,
                                ChapterNumber = chapter?.ChapterNumber > 0 ? chapter.ChapterNumber : chapterNumber,
                                ChapterTitle = chTitle,
                                IsOffline = false,
                                LastPageRead = resolvedPage,
                                ScrollPercent = savedScrollPercent,
                                Pages = proxiedPages
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[HandleGetReaderPagesAsync] Error fetching pages from source: {ex.Message}");
                    }
                }
            }
            return new ReaderPagesResultDto
            {
                MangaId = frontendMangaId,
                ChapterUrl = targetChUrl,
                ChapterNumber = chapterNumber,
                ChapterTitle = chTitle,
                IsOffline = SettingsService.IsOfflineMode,
                LastPageRead = savedPage,
                ScrollPercent = savedScrollPercent,
                Pages = new List<string>(),
                RequiresVerification = requiresVerification
            };
        }

        private async Task<object> HandleSaveReaderProgressAsync(JsonElement? payload)
        {
            string mangaId = GetString(payload, "mangaId");
            string chapterUrl = GetString(payload, "chapterUrl");
            float chapterNumber = GetFloat(payload, "chapterNumber", 1);
            string mangaUrl = GetString(payload, "mangaUrl");
            long sourceId = GetLong(payload, "sourceId");
            int currentPage = Math.Max(1, GetInt(payload, "currentPage", 1));
            int totalPages = GetInt(payload, "totalPages", 0);
            if (totalPages <= 0) return new { ok = false };
            string mode = GetString(payload, "mode");
            double scrollPercent = GetDouble(payload, "scrollPercent", -1.0);
            bool isCompleted = GetBool(payload, "completed") || (totalPages > 1 && currentPage >= totalPages) || (scrollPercent >= 0.995);
            bool markRead = GetBool(payload, "isRead") || isCompleted;
            int sessionReadSeconds = Math.Clamp(GetInt(payload, "sessionReadSeconds", 0), 0, 300);
            long storedValue = 0;
            if (string.Equals(mode, "webtoon", StringComparison.OrdinalIgnoreCase) && scrollPercent >= 0)
            {
                if (scrollPercent >= 0.995 || isCompleted)
                {
                    storedValue = 0;
                }
                else if (scrollPercent > 0)
                {
                    // Keep webtoon scroll values strictly > 1000 so thin top scrolls never decode as page index 1..1000
                    storedValue = Math.Max(1001L, (long)Math.Round(scrollPercent * 1_000_000L));
                }
            }
            else
            {
                storedValue = isCompleted ? 0 : currentPage;
            }
            var manga = await ResolveMangaAsync(mangaId, mangaUrl, sourceId);
            if (manga == null) return new { ok = false };
            if (string.IsNullOrEmpty(chapterUrl))
            {
                chapterUrl = manga.Chapters?.FirstOrDefault(c => Math.Abs(c.ChapterNumber - chapterNumber) < 0.01f)?.Url ?? string.Empty;
            }
            long cacheSourceId = manga.Source != 0 ? manga.Source : sourceId;
            if (!string.IsNullOrEmpty(chapterUrl))
            {
                _chapterLastPageReadCache[MakeChapterCacheKey(cacheSourceId, chapterUrl)] = storedValue;
            }
            if (chapterNumber > 0)
            {
                _chapterLastPageReadCache[$"{mangaId}_{chapterNumber}"] = storedValue;
                if (!string.IsNullOrEmpty(mangaUrl))
                {
                    _chapterLastPageReadCache[$"{cacheSourceId}|{mangaUrl}_{chapterNumber}"] = storedValue;
                }
            }
            if (!string.IsNullOrEmpty(chapterUrl) || (manga != null && manga.Id > 0))
            {
                if (totalPages > 1 && !string.IsNullOrEmpty(chapterUrl))
                {
                    _chapterTotalPagesCache[MakeChapterCacheKey(cacheSourceId, chapterUrl)] = totalPages;
                }
                if (manga != null && manga.Id > 0 && totalPages > 1)
                {
                    _mangaLastTotalPagesCache[manga.Id] = totalPages;
                }
                await LibraryService.SaveLastPageReadAsync(chapterUrl, storedValue, manga?.Id ?? 0, chapterNumber);
                if (markRead && manga != null)
                {
                    await LibraryService.SetChapterReadStatusAsync(
                        chapterUrl,
                        true,
                        manga.Source,
                        manga.Url,
                        $"Chapter {chapterNumber}",
                        chapterNumber);
                }
                // Only mark earlier chapters read & auto-delete downloaded chapter files when the chapter was ACTUALLY completed
                if (isCompleted && manga != null)
                {
                    await LibraryService.MarkChaptersBeforeAsReadAsync(manga, chapterNumber);
                    if (SettingsService.AutoDeleteAfterRead)
                    {
                        var finishedChapter = manga.Chapters?.FirstOrDefault(c =>
                            (!string.IsNullOrEmpty(chapterUrl) && string.Equals(c.Url, chapterUrl, StringComparison.OrdinalIgnoreCase)) ||
                            Math.Abs(c.ChapterNumber - chapterNumber) < 0.01f);
                        if (finishedChapter != null && DownloadPathService.IsChapterDownloaded(manga, finishedChapter))
                        {
                            if (DownloadPathService.DeleteDownloadedChapter(manga, finishedChapter))
                            {
                                finishedChapter.IsDownloaded = false;
                                if (!string.IsNullOrEmpty(finishedChapter.Url))
                                {
                                    await LibraryService.UpdateChapterDownloadStatusByUrlAsync(finishedChapter.Url, false, manga.Id);
                                }
                            }
                        }
                    }
                }
                // Update Manga.LastViewed, History & MAL track atomically inside one transaction + lock
                (long RemoteId, string Status, int ChaptersRead, int Score)? pendingMalSync = null;
                await _readerProgressDbLock.WaitAsync();
                try
                {
                    using var db = new MangaDbContext();
                    await using var tx = await db.Database.BeginTransactionAsync();
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (manga != null && manga.Id > 0)
                    {
                        var dbManga = await db.Mangas.FindAsync(manga.Id);
                        if (dbManga != null)
                        {
                            dbManga.LastViewed = now;
                        }
                    }
                    var dbCh = manga != null && manga.Id > 0
                        ? await db.Chapters.FirstOrDefaultAsync(c => c.MangaId == manga.Id && c.Url == chapterUrl)
                        : await db.Chapters.FirstOrDefaultAsync(c => c.Url == chapterUrl);
                    if (dbCh != null)
                    {
                        var hist = await db.History.FirstOrDefaultAsync(h => h.ChapterId == dbCh.Id);
                        long deltaMs = sessionReadSeconds * 1000L;
                        if (hist != null)
                        {
                            hist.MangaId = dbCh.MangaId;
                            hist.LastRead = now;
                            if (deltaMs > 0)
                            {
                                long baseMs = hist.TimeRead > 0 && hist.TimeRead < 1000 ? hist.TimeRead * 1000L : hist.TimeRead;
                                hist.TimeRead = baseMs + deltaMs;
                            }
                        }
                        else
                        {
                            db.History.Add(new History
                            {
                                ChapterId = dbCh.Id,
                                MangaId = dbCh.MangaId,
                                LastRead = now,
                                TimeRead = deltaMs
                            });
                        }
                    }
                    if (markRead && manga != null && manga.Id > 0)
                    {
                        var track = await db.Tracks.FirstOrDefaultAsync(t => t.MangaId == manga.Id && t.TrackerName == "MyAnimeList");
                        if (track != null && (int)chapterNumber > track.LastChapterRead)
                        {
                            track.LastChapterRead = (int)chapterNumber;
                            if (SettingsService.AutoSyncTracker && MalService.IsConnected && track.RemoteId > 0)
                            {
                                pendingMalSync = (track.RemoteId, track.Status, track.LastChapterRead, (int)track.Score);
                            }
                        }
                    }
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                }
                catch { }
                finally
                {
                    _readerProgressDbLock.Release();
                }
                if (pendingMalSync.HasValue)
                {
                    var sync = pendingMalSync.Value;
                    _ = MalService.UpdateMangaStatusAsync(sync.RemoteId, sync.Status, sync.ChaptersRead, sync.Score);
                }
                // Preload Next Chapter once (cached + single-flight deduped) when within 3 pages of the end of current chapter
                if (SettingsService.PreloadNextChapter && !SettingsService.IsOfflineMode && currentPage >= Math.Max(1, totalPages - 2) && manga?.Chapters != null)
                {
                    var nextCh = manga.Chapters
                        .Where(c => c.ChapterNumber > chapterNumber)
                        .OrderBy(c => c.ChapterNumber)
                        .FirstOrDefault();
                    if (nextCh != null && !string.IsNullOrEmpty(nextCh.Url))
                    {
                        var source = ResolveSourceSafely(manga.Source, manga.Url, manga: manga);
                        if (source != null)
                        {
                            string nextPreloadKey = $"{source.Id}|{nextCh.Url}";
                            if (!TryGetPreloadedPageList(nextPreloadKey, out _) && _preloadingChapterUrls.TryAdd(nextPreloadKey, 0))
                            {
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        var pages = await source.GetPageListAsync(nextCh.Url);
                                        if (pages != null && pages.Count > 0)
                                        {
                                            StorePreloadedPageList(nextPreloadKey, pages);
                                        }
                                    }
                                    catch { }
                                    finally
                                    {
                                        _preloadingChapterUrls.TryRemove(nextPreloadKey, out _);
                                    }
                                });
                            }
                        }
                    }
                }
            }
            return new { ok = true };
        }
    }
}
