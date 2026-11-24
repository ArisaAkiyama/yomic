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
         * 3. MANGA DETAIL & CHAPTER MANAGEMENT
         * ========================================================================= */
        private async Task<Manga?> ResolveMangaAsync(string mangaId, string? url = null, long sourceId = 0)
        {
            using var db = new MangaDbContext();
            // 1. Direct DB lookup by "db-{id}"
            if (!string.IsNullOrEmpty(mangaId) && mangaId.StartsWith("db-", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(mangaId.AsSpan(3), out long dbId))
            {
                var byId = await db.Mangas
                    .Include(m => m.Chapters)
                    .Include(m => m.Categories)
                    .Include(m => m.Tracks)
                    .FirstOrDefaultAsync(m => m.Id == dbId);
                if (byId != null) return byId;
            }
            // 2. Check in-memory discovered manga cache (from feed or search)
            if (_discoveredManga.TryGetValue(mangaId, out var cached))
            {
                url ??= cached.Url;
                if (sourceId == 0) sourceId = cached.Source;
                // Check if this specific manga for this specific source exists in DB
                if (!string.IsNullOrEmpty(cached.Url))
                {
                    long cachedMinSrc = cached.Source > long.MinValue + 2048 ? cached.Source - 2048 : long.MinValue;
                    long cachedMaxSrc = cached.Source < long.MaxValue - 2048 ? cached.Source + 2048 : long.MaxValue;
                    var byUrlAndSource = await db.Mangas
                        .Include(m => m.Chapters)
                        .Include(m => m.Categories)
                        .Include(m => m.Tracks)
                        .FirstOrDefaultAsync(m => m.Url == cached.Url && m.Source >= cachedMinSrc && m.Source <= cachedMaxSrc);
                    if (byUrlAndSource != null) return byUrlAndSource;
                }
                // If not in DB yet, return the cached manga directly (never let title collision hijack it!)
                return cached;
            }
            // 3. Lookup by URL if provided
            long minSourceId = sourceId > long.MinValue + 2048 ? sourceId - 2048 : long.MinValue;
            long maxSourceId = sourceId < long.MaxValue - 2048 ? sourceId + 2048 : long.MaxValue;
            if (!string.IsNullOrEmpty(url))
            {
                var byUrlQuery = db.Mangas
                    .Include(m => m.Chapters)
                    .Include(m => m.Categories)
                    .Include(m => m.Tracks);
                var byUrl = sourceId != 0
                    ? await byUrlQuery.FirstOrDefaultAsync(m => m.Url == url && m.Source >= minSourceId && m.Source <= maxSourceId)
                    : null;
                byUrl ??= await byUrlQuery.FirstOrDefaultAsync(m => m.Url == url);
                if (byUrl != null) return byUrl;
            }
            // 4. Last-resort fallback match by Title or slug in DB (ONLY if cached was null and source matches)
            if (!string.IsNullOrEmpty(mangaId) && !mangaId.StartsWith("src-", StringComparison.OrdinalIgnoreCase))
            {
                var normalizedId = mangaId.Replace("-", " ").Trim();
                var byTitleOrSlug = await db.Mangas
                    .Include(m => m.Chapters)
                    .Include(m => m.Categories)
                    .Include(m => m.Tracks)
                    .FirstOrDefaultAsync(m =>
                        (sourceId == 0 || (m.Source >= minSourceId && m.Source <= maxSourceId)) &&
                        ((m.Title != null && (m.Title == mangaId || m.Title.ToLower() == normalizedId.ToLower())) ||
                         (m.Url != null && (m.Url.Contains(mangaId) || m.Url.Trim('/').EndsWith(mangaId)))));
                if (byTitleOrSlug != null) return byTitleOrSlug;
            }
            return null;
        }

        private IMangaSource? ResolveSourceSafely(long sourceId, string? url = null, string? chapterUrl = null, Manga? manga = null, string? sourceName = null)
        {
            // 1. Try by sourceName if provided
            if (!string.IsNullOrEmpty(sourceName))
            {
                var byName = SourceManager.GetSourceByName(sourceName);
                if (byName != null) return byName;
            }
            var source = sourceId != 0 ? SourceManager.GetSource(sourceId) : null;
            if (source != null) return source;
            var allSources = SourceManager.GetSources();
            if (allSources.Count == 0) return null;
            // 2. Try legacy ID map
            if (sourceId == 3639673976007021568L)
            {
                source = allSources.FirstOrDefault(s => s.Name.Contains("Kiryuu", StringComparison.OrdinalIgnoreCase));
            }
            else if (sourceId == -3406828676302900168L)
            {
                source = allSources.FirstOrDefault(s => s.Name.Contains("Komiku", StringComparison.OrdinalIgnoreCase));
            }
            // 3. Try URL match
            string? checkUrl = !string.IsNullOrEmpty(chapterUrl) ? chapterUrl : (!string.IsNullOrEmpty(url) ? url : manga?.Url);
            if (source == null && !string.IsNullOrEmpty(checkUrl))
            {
                source = allSources.FirstOrDefault(s =>
                    !string.IsNullOrEmpty(s.BaseUrl) &&
                    (checkUrl.Contains(s.BaseUrl, StringComparison.OrdinalIgnoreCase) ||
                     (Uri.TryCreate(s.BaseUrl, UriKind.Absolute, out var u) && checkUrl.Contains(u.Host, StringComparison.OrdinalIgnoreCase))));
                if (source == null)
                {
                    if (checkUrl.Contains("/ch/", StringComparison.OrdinalIgnoreCase) || checkUrl.Contains("komiku", StringComparison.OrdinalIgnoreCase))
                    {
                        source = allSources.FirstOrDefault(s => s.Name.Contains("Komiku", StringComparison.OrdinalIgnoreCase));
                    }
                    else if (checkUrl.Contains("kiryuu", StringComparison.OrdinalIgnoreCase))
                    {
                        source = allSources.FirstOrDefault(s => s.Name.Contains("Kiryuu", StringComparison.OrdinalIgnoreCase));
                    }
                }
            }
            if (source != null && manga != null && manga.Source != source.Id)
            {
                manga.Source = source.Id;
                if (manga.Id > 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var db = new MangaDbContext();
                            var dbManga = await db.Mangas.FindAsync(manga.Id);
                            if (dbManga != null)
                            {
                                dbManga.Source = source.Id;
                                await db.SaveChangesAsync();
                            }
                        }
                        catch { }
                    });
                }
            }
            return source;
        }

        private async Task<MangaDto?> HandleGetMangaDetailAsync(JsonElement? payload)
        {
            string mangaId = GetString(payload, "mangaId");
            string url = GetString(payload, "url");
            long sourceId = GetLong(payload, "sourceId");
            string sourceName = GetString(payload, "sourceName");
            bool forceRefresh = GetBool(payload, "forceRefresh");
            var manga = await ResolveMangaAsync(mangaId, url, sourceId);
            if (manga == null) return null;
            JsMangaSource.SelectedLanguage = string.Equals(SettingsService.MangaDexLanguage, "id", StringComparison.OrdinalIgnoreCase) ? "id" : "en";
            var source = ResolveSourceSafely(sourceId != 0 ? sourceId : manga.Source, manga.Url, manga: manga, sourceName: sourceName);
            bool isMangaDex = (source?.Name?.Contains("MangaDex", StringComparison.OrdinalIgnoreCase) ?? false) ||
                              sourceName.Contains("MangaDex", StringComparison.OrdinalIgnoreCase) ||
                              (manga.Url != null && manga.Url.StartsWith("/title/", StringComparison.OrdinalIgnoreCase));
            bool needsRemoteFetch = forceRefresh || isMangaDex || manga.Chapters == null || manga.Chapters.Count == 0 || string.IsNullOrWhiteSpace(manga.Description);
            if (needsRemoteFetch && !SettingsService.IsOfflineMode)
            {
                // source already resolved above
                if (source != null && !string.IsNullOrEmpty(manga.Url))
                {
                    try
                    {
                        var detailsTask = source.GetMangaDetailsAsync(manga.Url);
                        var chaptersTask = source.GetChapterListAsync(manga.Url);
                        await Task.WhenAll(detailsTask, chaptersTask);
                        var details = await detailsTask;
                        var remoteChapters = await chaptersTask;
                        if (details != null)
                        {
                            if (!string.IsNullOrWhiteSpace(details.Title)) manga.Title = details.Title;
                            if (!string.IsNullOrWhiteSpace(details.Author)) manga.Author = details.Author;
                            if (!string.IsNullOrWhiteSpace(details.Artist)) manga.Artist = details.Artist;
                            if (!string.IsNullOrWhiteSpace(details.Description)) manga.Description = details.Description;
                            if (details.Genre != null && details.Genre.Count > 0) manga.Genre = details.Genre;
                            if (details.Status > 0) manga.Status = details.Status;
                            if (!string.IsNullOrWhiteSpace(details.ThumbnailUrl)) manga.ThumbnailUrl = details.ThumbnailUrl;
                            manga.Initialized = true;
                        }
                        if (remoteChapters != null && remoteChapters.Count > 0)
                        {
                            if (manga.Id > 0)
                            {
                                using (var db = new MangaDbContext())
                                {
                                    var dbManga = await db.Mangas.FindAsync(manga.Id);
                                    if (dbManga != null)
                                    {
                                        if (!string.IsNullOrWhiteSpace(manga.Title)) dbManga.Title = manga.Title;
                                        if (!string.IsNullOrWhiteSpace(manga.Author)) dbManga.Author = manga.Author;
                                        if (!string.IsNullOrWhiteSpace(manga.Artist)) dbManga.Artist = manga.Artist;
                                        if (!string.IsNullOrWhiteSpace(manga.Description)) dbManga.Description = manga.Description;
                                        if (manga.Genre != null && manga.Genre.Count > 0) dbManga.Genre = manga.Genre;
                                        if (manga.Status > 0) dbManga.Status = manga.Status;
                                        if (!string.IsNullOrWhiteSpace(manga.ThumbnailUrl)) dbManga.ThumbnailUrl = manga.ThumbnailUrl;
                                        dbManga.Initialized = true;
                                        await db.SaveChangesAsync();
                                    }
                                }
                                await LibraryService.UpdateChaptersAsync(manga, remoteChapters, isInitialLoad: !manga.Favorite && !isMangaDex);
                                manga = await ResolveMangaAsync($"db-{manga.Id}") ?? manga;
                            }
                            else
                            {
                                // Ensure it's saved in DB as non-favorite atomically so concurrent clicks never create duplicate Manga rows
                                await _mangaInsertLock.WaitAsync();
                                try
                                {
                                    using var db = new MangaDbContext();
                                    var existing = await db.Mangas.Include(m => m.Chapters)
                                        .FirstOrDefaultAsync(m => m.Url == manga.Url && m.Source == manga.Source);
                                    if (existing == null)
                                    {
                                        manga.LastUpdate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                        db.Mangas.Add(manga);
                                        await db.SaveChangesAsync();
                                    }
                                    else
                                    {
                                        manga.Id = existing.Id;
                                    }
                                }
                                finally
                                {
                                    _mangaInsertLock.Release();
                                }
                                await LibraryService.UpdateChaptersAsync(manga, remoteChapters, isInitialLoad: !isMangaDex);
                                manga = await ResolveMangaAsync($"db-{manga.Id}") ?? manga;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogService.Warning("MangaDetail", $"Failed to fetch remote details for {manga.Title}: {ex.Message}");
                    }
                }
            }
            return MapMangaToDto(manga, includeChapters: true);
        }

        private async Task<object?> HandleToggleLibraryAsync(JsonElement? payload)
        {
            string mangaId = GetString(payload, "mangaId");
            string mangaUrl = GetString(payload, "url");
            long sourceId = GetLong(payload, "sourceId");
            var manga = await ResolveMangaAsync(mangaId, mangaUrl, sourceId);
            if (manga == null) return null;
            long oldDbId = manga.Id;
            if (manga.Favorite)
            {
                await LibraryService.RemoveFromLibraryAsync(manga, deleteFiles: false);
                manga.Favorite = false;
                // Evict stale Favorite=true entries from _discoveredManga so unread deleted manga never snap back
                _discoveredManga.TryRemove(mangaId, out _);
                if (oldDbId > 0)
                {
                    _discoveredManga.TryRemove($"db-{oldDbId}", out _);
                }
                // Check if DB row still exists (e.g. had history) or was deleted (unread)
                using var checkDb = new MangaDbContext();
                var stillInDb = oldDbId > 0
                    ? await checkDb.Mangas
                        .Include(m => m.Chapters)
                        .Include(m => m.Categories)
                        .Include(m => m.Tracks)
                        .FirstOrDefaultAsync(m => m.Id == oldDbId)
                    : null;
                if (stillInDb != null)
                {
                    stillInDb.Favorite = false;
                    return MapMangaToDto(stillInDb, includeChapters: true);
                }
                manga.Id = 0;
                manga.Categories?.Clear();
                return MapMangaToDto(manga, includeChapters: true);
            }
            else
            {
                await LibraryService.AddToLibraryAsync(manga);
                if (manga.Chapters != null && manga.Chapters.Count > 0)
                {
                    await LibraryService.UpdateChaptersAsync(manga, manga.Chapters.ToList(), isInitialLoad: true);
                }
                manga.Favorite = true;
                _discoveredManga.TryRemove(mangaId, out _);
            }
            var updated = await ResolveMangaAsync($"db-{manga.Id}", manga.Url, manga.Source) ?? manga;
            return MapMangaToDto(updated, includeChapters: true);
        }

        private async Task<object?> HandleToggleBookmarkAsync(JsonElement? payload)
        {
            string mangaId = GetString(payload, "mangaId");
            string chapterUrl = GetString(payload, "chapterUrl");
            float chapterNumber = GetFloat(payload, "chapterNumber");
            string mangaUrl = GetString(payload, "url");
            long sourceId = GetLong(payload, "sourceId");
            var manga = await ResolveMangaAsync(mangaId, mangaUrl, sourceId);
            if (manga == null) return null;
            var matchedCh = manga.Chapters?.FirstOrDefault(c =>
                (!string.IsNullOrEmpty(chapterUrl) && string.Equals(c.Url, chapterUrl, StringComparison.OrdinalIgnoreCase)) ||
                Math.Abs(c.ChapterNumber - chapterNumber) < 0.01f);
            if (string.IsNullOrEmpty(chapterUrl))
            {
                chapterUrl = matchedCh?.Url ?? string.Empty;
            }
            bool hasExplicitIsBookmarked = payload.HasValue && payload.Value.ValueKind == JsonValueKind.Object &&
                                           payload.Value.TryGetProperty("isBookmarked", out _);
            bool hasExplicitBookmarked = payload.HasValue && payload.Value.ValueKind == JsonValueKind.Object &&
                                         payload.Value.TryGetProperty("bookmarked", out _);
            bool isBookmarked = hasExplicitIsBookmarked
                ? GetBool(payload, "isBookmarked")
                : hasExplicitBookmarked
                    ? GetBool(payload, "bookmarked")
                    : !(matchedCh?.Bookmark ?? false);
            if (!string.IsNullOrEmpty(chapterUrl))
            {
                await LibraryService.SetChapterBookmarkStatusAsync(
                    chapterUrl,
                    isBookmarked,
                    manga.Source,
                    manga.Url,
                    $"Chapter {chapterNumber}",
                    chapterNumber);
            }
            return new { ok = true, isBookmarked };
        }

        private async Task<MangaDto?> HandleMarkChaptersAsync(JsonElement? payload)
        {
            string mangaId = GetString(payload, "mangaId");
            string mode = GetString(payload, "mode");
            float chapterNumber = GetFloat(payload, "chapterNumber");
            string mangaUrl = GetString(payload, "mangaUrl");
            long sourceId = GetLong(payload, "sourceId");
            var manga = await ResolveMangaAsync(mangaId, mangaUrl, sourceId);
            if (manga == null) return null;
            if (mode == "all-read")
            {
                await LibraryService.MarkChaptersAsReadAsync(manga);
            }
            else if (mode == "all-unread")
            {
                await LibraryService.MarkChaptersAsUnreadAsync(manga);
            }
            else if (mode == "before-read")
            {
                await LibraryService.MarkChaptersBeforeAsReadAsync(manga, chapterNumber);
            }
            else if (mode == "single-read" || mode == "single-unread")
            {
                bool isRead = mode == "single-read";
                string chUrl = GetString(payload, "chapterUrl");
                if (string.IsNullOrEmpty(chUrl))
                {
                    var targetCh = manga.Chapters?.FirstOrDefault(c => Math.Abs(c.ChapterNumber - chapterNumber) < 0.01f);
                    chUrl = targetCh?.Url ?? string.Empty;
                }
                if (!string.IsNullOrEmpty(chUrl))
                {
                    await LibraryService.SetChapterReadStatusAsync(
                        chUrl,
                        isRead,
                        manga.Source,
                        manga.Url,
                        $"Chapter {chapterNumber}",
                        chapterNumber);
                }
            }
            var refreshed = await ResolveMangaAsync($"db-{manga.Id}", manga.Url, manga.Source) ?? manga;
            return MapMangaToDto(refreshed, includeChapters: true);
        }
    }
}
