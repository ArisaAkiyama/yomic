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
         * 5. PUSTAKA & RIWAYAT (LIBRARY & HISTORY LIST)
         * ========================================================================= */
        private async Task<List<MangaDto>> GetLibraryAndHistoryMangaDtosAsync()
        {
            using var db = new MangaDbContext();
            var mangas = await db.Mangas
                .AsNoTracking()
                .Include(m => m.Chapters)
                .Include(m => m.Categories)
                .Include(m => m.Tracks)
                .Where(m => m.Favorite || m.LastViewed > 0)
                .OrderByDescending(m => m.LastViewed)
                .ThenByDescending(m => m.LastUpdate)
                .ToListAsync();
            return mangas.Select(m => MapMangaToDto(m, includeChapters: true)).ToList();
        }

        private async Task<List<MangaDto>> GetHistoryMangaDtosAsync()
        {
            using var db = new MangaDbContext();
            var mangas = await db.Mangas
                .AsNoTracking()
                .Include(m => m.Chapters)
                .Include(m => m.Categories)
                .Include(m => m.Tracks)
                .Where(m => m.LastViewed > 0)
                .OrderByDescending(m => m.LastViewed)
                .ToListAsync();
            return mangas.Select(m => MapMangaToDto(m, includeChapters: true)).ToList();
        }

        private async Task<List<CategoryDto>> HandleGetCategoriesAsync()
        {
            var categories = await LibraryService.GetCategoriesAsync();
            return MapCategoriesToDtos(categories);
        }

        private async Task<List<CategoryDto>> HandleAddCategoryAsync(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                await LibraryService.AddCategoryAsync(new Category { Name = name.Trim() });
            }
            var categories = await LibraryService.GetCategoriesAsync();
            return MapCategoriesToDtos(categories);
        }

        private async Task<List<CategoryDto>> HandleDeleteCategoryAsync(long categoryId)
        {
            await LibraryService.DeleteCategoryAsync(categoryId);
            var categories = await LibraryService.GetCategoriesAsync();
            return MapCategoriesToDtos(categories);
        }

        private async Task<object> HandleSetMangaCategoriesAsync(JsonElement? payload)
        {
            var mangaId = GetString(payload, "mangaId");
            var mangaUrl = GetString(payload, "mangaUrl");
            var sourceId = GetLong(payload, "sourceId");
            var manga = await ResolveMangaAsync(mangaId, mangaUrl, sourceId);
            if (manga == null)
            {
                return new { ok = false, error = "Manga tidak ditemukan" };
            }
            var categoryIds = new List<long>();
            if (payload.HasValue && payload.Value.TryGetProperty("categoryIds", out var catProp) && catProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in catProp.EnumerateArray())
                {
                    if (el.TryGetInt64(out var cid))
                    {
                        categoryIds.Add(cid);
                    }
                }
            }
            await LibraryService.SetMangaCategoriesAsync(manga.Url, manga.Source, categoryIds);
            return new { ok = true, mangaId, categoryIds };
        }

        private async Task<object> HandleSyncLibraryAsync(bool forceRefresh)
        {
            if (Interlocked.CompareExchange(ref _isSyncingLibrary, 1, 0) != 0)
            {
                return new { alreadyRunning = true };
            }
            try
            {
                var progress = new Progress<(int current, int total)>(p =>
                {
                    PushEvent("event:library-sync-progress", new
                    {
                        active = true,
                        current = p.current,
                        total = p.total
                    });
                });
                int newChaptersCount = await LibraryService.UpdateAllLibraryMangaAsync(SourceManager, forceRefresh, progress);
                var mangaList = await GetLibraryAndHistoryMangaDtosAsync();
                var activityUpdates = await GetActivityUpdatesDtosAsync();
                return new
                {
                    newChaptersCount,
                    mangaList,
                    activityUpdates
                };
            }
            finally
            {
                Interlocked.Exchange(ref _isSyncingLibrary, 0);
            }
        }

        /* =========================================================================
         * 6. PEMBARUAN (UPDATES)
         * ========================================================================= */
        private async Task<List<ActivityUpdateDto>> GetActivityUpdatesDtosAsync()
        {
            var recentChapters = await LibraryService.GetRecentChaptersAsync(300);
            var result = new List<ActivityUpdateDto>();
            // Group by MangaId + Date bucket so multiple chapters fetched on the same day collapse cleanly with (+N bab)
            var grouped = recentChapters
                .Where(c => c.Manga != null)
                .GroupBy(c => new
                {
                    c.MangaId,
                    DayBucket = DateTimeOffset.FromUnixTimeMilliseconds(c.DateFetch > 0 ? c.DateFetch : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).LocalDateTime.Date
                });
            foreach (var grp in grouped)
            {
                var ordered = grp.OrderByDescending(c => c.ChapterNumber).ToList();
                var latest = ordered.First();
                var manga = latest.Manga;
                var src = SourceManager.GetSource(manga.Source);
                string sourceName = src?.Name?.Replace("Yomic:", "", StringComparison.OrdinalIgnoreCase).Trim() ?? "Sumber Komik";
                // Target chapter for reading: prioritize the earliest UNREAD chapter in this batch (anti-spoiler)
                var unreadInBatch = ordered.Where(c => !c.Read).OrderBy(c => c.ChapterNumber).ToList();
                var targetChapter = unreadInBatch.FirstOrDefault() ?? ordered.Last();
                result.Add(new ActivityUpdateDto
                {
                    Id = $"upd-{latest.Id}",
                    MangaId = MakeMangaFrontendId(manga),
                    MangaTitle = manga.Title,
                    ChapterNumber = (float)Math.Round(targetChapter.ChapterNumber >= 0 ? targetChapter.ChapterNumber : 1, 2),
                    ChapterTitle = CleanChapterSubTitle(targetChapter.Name, targetChapter.ChapterNumber),
                    ChapterUrl = targetChapter.Url,
                    SourceName = sourceName,
                    TimeAgo = FormatRelativeTime(latest.DateFetch),
                    DateFetch = latest.DateFetch,
                    IsNew = ordered.Any(c => !c.Read),
                    ExtraChaptersCount = Math.Max(0, ordered.Count - 1),
                    CoverUrl = WebResourceImageProxy.BuildCoverProxyUrl(manga.Id, manga.ThumbnailUrl, manga.Source),
                    FirstUnreadChapterNumber = (float)Math.Round(targetChapter.ChapterNumber >= 0 ? targetChapter.ChapterNumber : 1, 2),
                    FirstUnreadChapterTitle = CleanChapterSubTitle(targetChapter.Name, targetChapter.ChapterNumber),
                    FirstUnreadChapterUrl = targetChapter.Url
                });
            }
            return result.OrderByDescending(u => u.DateFetch).ToList();
        }

        private async Task<object> HandleClearAllUpdatesAsync()
        {
            await LibraryService.ClearAllUpdatesAsync();
            return new { ok = true };
        }

        private async Task<object> HandleMarkAllUpdatesReadAsync()
        {
            int count = await LibraryService.MarkAllUpdatesReadAsync();
            return new { ok = true, count };
        }

        /* =========================================================================
         * 7. RIWAYAT (HISTORY)
         * ========================================================================= */
        private async Task<object> HandleRemoveHistoryItemAsync(string mangaId)
        {
            var manga = await ResolveMangaAsync(mangaId);
            if (manga != null && manga.Id > 0)
            {
                await MangaDbContext.DatabaseWriteGate.WaitAsync();
                try
                {
                    using var db = new MangaDbContext();
                    var dbManga = await db.Mangas.FindAsync(manga.Id);
                    if (dbManga != null)
                    {
                        dbManga.LastViewed = 0;
                        var chIds = await db.Chapters.Where(c => c.MangaId == dbManga.Id).Select(c => c.Id).ToListAsync();
                        var histRows = await db.History.Where(h => chIds.Contains(h.ChapterId)).ToListAsync();
                        db.History.RemoveRange(histRows);
                        await db.SaveChangesAsync();
                    }
                }
                finally
                {
                    MangaDbContext.DatabaseWriteGate.Release();
                }
            }
            return new { ok = true };
        }

        private async Task<object> HandleClearAllHistoryAsync()
        {
            await LibraryService.ClearAllHistoryAndProgressAsync();
            // Clear in-memory reader progress & image caches so reopening previously read chapters starts at page 1
            _chapterLastPageReadCache.Clear();
            _chapterTotalPagesCache.Clear();
            _mangaLastTotalPagesCache.Clear();
            ImageProxy.ClearRamCache();
            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var readerCacheDir = Path.Combine(localAppData, "Yomic", "Cache", "Reader");
                if (Directory.Exists(readerCacheDir))
                {
                    Directory.Delete(readerCacheDir, true);
                    Directory.CreateDirectory(readerCacheDir);
                }
                SourceManager.ClearAllCache();
                await NetworkService.ResetConnectionsAsync();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            catch { }
            var updatedMangas = await GetLibraryAndHistoryMangaDtosAsync();
            return new
            {
                ok = true,
                dbSizeText = CalculateStorageSizeText(),
                mangaList = updatedMangas
            };
        }
    }
}
