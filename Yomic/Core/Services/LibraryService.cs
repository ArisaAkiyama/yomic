using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Yomic.Core.Data;
using Yomic.Core.Models;
using System.Linq;

namespace Yomic.Core.Services
{
    public class LibraryService
    {
        // Event triggered when new chapters are found
        public event EventHandler<int>? UpdatesFound;

        public async Task<List<Manga>> GetLibraryMangaAsync()
        {
            using var context = new MangaDbContext();
            return await context.Mangas
                                .Include(m => m.Chapters)
                                .Where(m => m.Favorite)
                                .OrderBy(m => m.Title)
                                .ToListAsync();
        }

        public async Task<int> GetLibraryCountAsync()
        {
            using var context = new MangaDbContext();
            return await context.Mangas.CountAsync(m => m.Favorite);
        }

        public async Task<Manga?> GetMangaAsync(long id)
        {
            using var context = new MangaDbContext();
            return await context.Mangas
                .Include(m => m.Chapters)
                .FirstOrDefaultAsync(m => m.Id == id);
        }

        public async Task<Manga?> GetMangaByUrlAsync(string url, long sourceId)
        {
            using var context = new MangaDbContext();
            return await context.Mangas
                .Include(m => m.Chapters)
                .FirstOrDefaultAsync(m => m.Url == url && m.Source == sourceId);
        }

        public async Task AddToLibraryAsync(Manga manga, int chapterCount = 0)
        {
            try
            {
                // Sanitize Title (Remove "Komik " or "Manga " prefix if present)
                if (!string.IsNullOrEmpty(manga.Title))
                {
                    string originalTitle = manga.Title;
                    if (originalTitle.StartsWith("Komik ", StringComparison.OrdinalIgnoreCase))
                        manga.Title = originalTitle.Substring(6).Trim();
                    else if (originalTitle.StartsWith("Manga ", StringComparison.OrdinalIgnoreCase))
                        manga.Title = originalTitle.Substring(6).Trim();
                }

                Console.WriteLine("========================================");
                Console.WriteLine($"[Library] SAVING TO DATABASE: {manga.Title}");
                Console.WriteLine($" - Author   : {manga.Author}");
                Console.WriteLine($" - Status   : {manga.Status}");
                Console.WriteLine($" - Synopsis : {(manga.Description != null && manga.Description.Length > 50 ? manga.Description.Substring(0, 50) + "..." : manga.Description)}");
                Console.WriteLine($" - Chapters : {chapterCount} (Detected)");
                Console.WriteLine("========================================");

                using var context = new MangaDbContext();
                
                // Check if exists
                var existing = await context.Mangas.FirstOrDefaultAsync(m => m.Url == manga.Url && m.Source == manga.Source);
                if (existing != null)
                {
                    existing.Favorite = true;
                    // Update other fields
                    existing.Title = manga.Title;
                    existing.ThumbnailUrl = manga.ThumbnailUrl;
                    existing.Author = manga.Author;
                    existing.Artist = manga.Artist;
                    existing.Genre = manga.Genre;
                    existing.Status = manga.Status;
                    existing.Description = manga.Description;
                    existing.Initialized = true;
                    context.Update(existing);
                    System.Diagnostics.Debug.WriteLine($"[LibraryService] Updated existing manga: {manga.Title}");
                }
                else
                {
                    manga.Favorite = true;
                    manga.DateAdded = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                    await context.Mangas.AddAsync(manga);
                    System.Diagnostics.Debug.WriteLine($"[LibraryService] Added new manga: {manga.Title}");
                }
                
                await context.SaveChangesAsync();
                System.Diagnostics.Debug.WriteLine($"[LibraryService] Saved changes to DB.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LibraryService] Error adding to library: {ex}");
            }
        }

        public async Task UpdateHistoryAsync(Manga manga)
        {
            try
            {
                using var context = new MangaDbContext();
                
                // Check if exists
                var existing = await context.Mangas.FirstOrDefaultAsync(m => m.Url == manga.Url && m.Source == manga.Source);
                
                if (existing != null)
                {
                    existing.LastViewed = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                    // Update details just in case, but keep IsFavorite as is
                    if (string.IsNullOrEmpty(existing.ThumbnailUrl)) existing.ThumbnailUrl = manga.ThumbnailUrl;
                    if (string.IsNullOrEmpty(existing.Title)) existing.Title = manga.Title;
                    
                    context.Update(existing);
                }
                else
                {
                    // Add new non-favorite entry
                    manga.Favorite = false;
                    manga.LastViewed = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                    manga.DateAdded = 0; // Not added to library
                    manga.Initialized = false; // Details might be partial
                    
                    await context.Mangas.AddAsync(manga);
                }
                
                await context.SaveChangesAsync();
                System.Diagnostics.Debug.WriteLine($"[LibraryService] History updated for: {manga.Title}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LibraryService] Error updating history: {ex.Message}");
            }
        }

        public async Task RemoveFromLibraryAsync(Manga manga, bool deleteFiles = false)
        {
             using var context = new MangaDbContext();
             Manga? existing = null;

             // Prioritize ID lookup if available
             if (manga.Id > 0)
             {
                 existing = await context.Mangas.FindAsync(manga.Id);
             }
             
             // Fallback to URL lookup if ID not found or invalid
             if (existing == null)
             {
                 existing = await context.Mangas.FirstOrDefaultAsync(m => m.Url == manga.Url && m.Source == manga.Source);
             }

             if (existing != null)
             {
                 existing.Favorite = false;
                 context.Update(existing);
                 await context.SaveChangesAsync();
                 
                 System.Diagnostics.Debug.WriteLine($"[LibraryService] Removed from library (Unfavorited): {existing.Title}");
                 
                 if (deleteFiles)
                 {
                     try 
                     {
                        var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
                        var safeTitle = string.Join("_", existing.Title.Split(System.IO.Path.GetInvalidFileNameChars()));
                        var mangaDir = System.IO.Path.Combine(appData, "Yomic", "Downloads", existing.Source.ToString(), safeTitle);

                        if (System.IO.Directory.Exists(mangaDir))
                        {
                            System.IO.Directory.Delete(mangaDir, true);
                            System.Diagnostics.Debug.WriteLine($"[LibraryService] Deleted files for: {existing.Title}");
                        }
                     }
                     catch (Exception ex)
                     {
                         System.Diagnostics.Debug.WriteLine($"[LibraryService] Error deleting files: {ex.Message}");
                     }
                 }
             }
        }

        public async Task DeleteChapterDownloadAsync(Manga manga, Chapter chapter)
        {
            try
            {
                // 1. Update DB First (to prevent "Ghost" downloaded status if file delete succeeds but DB fails)
                using var context = new MangaDbContext();
                var dbManga = await context.Mangas.FirstOrDefaultAsync(m => m.Url == manga.Url && m.Source == manga.Source);
                if (dbManga != null)
                {
                    var dbChapter = await context.Chapters.FirstOrDefaultAsync(c => c.MangaId == dbManga.Id && c.Url == chapter.Url);
                    if (dbChapter != null)
                    {
                        dbChapter.IsDownloaded = false;
                        context.Update(dbChapter);
                        await context.SaveChangesAsync();
                    }
                }

                // 2. Delete files
                var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
                var safeMangaTitle = string.Join("_", manga.Title.Split(System.IO.Path.GetInvalidFileNameChars()));
                var safeChapterName = string.Join("_", chapter.Name.Split(System.IO.Path.GetInvalidFileNameChars()));
                var chapterDir = System.IO.Path.Combine(appData, "Yomic", "Downloads", manga.Source.ToString(), safeMangaTitle, safeChapterName);

                if (System.IO.Directory.Exists(chapterDir))
                {
                    System.IO.Directory.Delete(chapterDir, true);
                    System.Diagnostics.Debug.WriteLine($"[LibraryService] Deleted chapter download: {chapter.Name}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LibraryService] Error deleting chapter download: {ex.Message}");
            }
        }

        // Global lock for Database writes to prevent SQLite "database is locked" errors during parallel updates
        private static readonly System.Threading.SemaphoreSlim _dbLock = new(1, 1);

        public async Task<int> UpdateChaptersAsync(Manga manga, List<Chapter> chapters, bool isInitialLoad = false)
        {
            await _dbLock.WaitAsync();
            try
            {
                using var context = new MangaDbContext();
                var dbManga = await context.Mangas
                    .Include(m => m.Chapters)
                    .FirstOrDefaultAsync(m => m.Url == manga.Url && m.Source == manga.Source);
                
                if (dbManga != null)
                {
                    // Advanced Sync Strategy (Soft Sync + Prune):
                    // 1. Identify Existing vs New vs Missing
                    // 2. Update Existing (Name, etc)
                    // 3. Add New
                    // 4. Remove Missing (Ghost chapters) - ONLY if not downloaded
                    
                    var dbChaptersDict = dbManga.Chapters.ToDictionary(c => c.Url);
                    var newChapters = new List<Chapter>();
                    long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                    
                    // FIX: Feed Spam (Initial Load)
                    // If DB has 0 chapters, this is the First Fetch.
                    // We should NOT mark these as "New Updates" (DateFetch = 0).
                    bool isEmptyLibrary = dbManga.Chapters.Count == 0;
                    
                    // Track URLs found in source to identify missing ones later
                    var sourceUrls = new HashSet<string>();

                    foreach (var ch in chapters)
                    {
                        sourceUrls.Add(ch.Url);
                        
                        if (dbChaptersDict.TryGetValue(ch.Url, out var existingChapter))
                        {
                            // UPDATE EXISTING
                            // Fix "Rename" issues (e.g. "Ch. 1" -> "Chapter 1")
                            if (existingChapter.Name != ch.Name)
                            {
                                existingChapter.Name = ch.Name;
                            }
                        }
                        else
                        {
                            // ADD NEW
                            ch.MangaId = dbManga.Id;
                            // Mark as "Old" (0) if this is the first time fetching chapters for this manga
                            ch.DateFetch = (isInitialLoad || isEmptyLibrary) ? 0 : now; 
                            newChapters.Add(ch);
                        }
                    }

                    // PRUNE MISSING (Fix Ghost Chapters)
                    // If a URL is in DB but NOT in Source -> It's deleted/renamed on server.
                    // We delete it ONLY if it is NOT downloaded.
                    var missingChapters = dbManga.Chapters.Where(c => !sourceUrls.Contains(c.Url)).ToList();
                    int prunedCount = 0;
                    
                    foreach (var missing in missingChapters)
                    {
                        if (!missing.IsDownloaded)
                        {
                            context.Chapters.Remove(missing);
                            prunedCount++;
                        }
                    }

                    if (newChapters.Count > 0)
                    {
                        await context.Chapters.AddRangeAsync(newChapters);
                        Console.WriteLine($"[Library] SYNC: {newChapters.Count} new, {prunedCount} pruned, {dbManga.Chapters.Count - newChapters.Count - prunedCount} updated/verified.");
                    }
                    else if (prunedCount > 0)
                    {
                        Console.WriteLine($"[Library] SYNC: {prunedCount} ghost chapters removed.");
                    }
                    
                    // Update LastUpdate timestamp for the Manga
                    dbManga.LastUpdate = now;
                    
                    await context.SaveChangesAsync();
                    return newChapters.Count;
                }
                return 0;
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"[LibraryService] Error updating chapters: {ex}");
                 return 0;
            }
            finally
            {
                _dbLock.Release();
            }
        }

        public async Task<List<Chapter>> GetRecentChaptersAsync(int limit = 50)
        {
            try
            {
                using var context = new MangaDbContext();
                // Get chapters fetched recently (DateFetch > 0), from Favorite mangas
                // We need to Includes Manga to get Title/Cover
                var recent = await context.Chapters
                    .Include(c => c.Manga)
                    .Where(c => c.Manga.Favorite && c.DateFetch > 0)
                    .OrderByDescending(c => c.DateFetch)
                    .Take(limit)
                    .ToListAsync();
                    
                return recent;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LibraryService] Error fetching recent chapters: {ex}");
                return new List<Chapter>();
            }
        }

        public async Task UpdateChapterDownloadStatusAsync(long chapterId, bool isDownloaded)
        {
            using var context = new MangaDbContext();
            var chapter = await context.Chapters.FindAsync(chapterId);
            if (chapter != null)
            {
                chapter.IsDownloaded = isDownloaded;
                await context.SaveChangesAsync();
            }
        }

        public async Task UpdateChapterDownloadStatusByUrlAsync(string chapterUrl, bool isDownloaded)
        {
            using var context = new MangaDbContext();
            var chapter = await context.Chapters.FirstOrDefaultAsync(c => c.Url == chapterUrl);
            if (chapter != null)
            {
                chapter.IsDownloaded = isDownloaded;
                await context.SaveChangesAsync();
                System.Diagnostics.Debug.WriteLine($"[LibraryService] Updated download status for: {chapter.Name}");
            }
        }

        public async Task MarkChapterAsReadAsync(string chapterUrl, long sourceId = 0, string mangaUrl = "", string chapterTitle = "", float chapterNumber = -1)
        {
            try
            {
                using var context = new MangaDbContext();
                var chapter = await context.Chapters.FirstOrDefaultAsync(c => c.Url == chapterUrl);
                
                if (chapter != null)
                {
                    chapter.Read = true;
                    await context.SaveChangesAsync();
                    System.Diagnostics.Debug.WriteLine($"[LibraryService] Marked chapter as read: {chapter.Name}");
                }
                else if (sourceId > 0 && !string.IsNullOrEmpty(mangaUrl)) 
                {
                    // Fallback: Chapter doesn't exist (Online reading), create it
                     var manga = await context.Mangas.FirstOrDefaultAsync(m => m.Url == mangaUrl && m.Source == sourceId);
                     if (manga != null)
                     {
                         var newChapter = new Chapter
                         {
                             MangaId = manga.Id,
                             Url = chapterUrl,
                             Name = chapterTitle,
                             ChapterNumber = chapterNumber,
                             Read = true,
                             DateFetch = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                             DateUpload = 0 // Unknown
                         };
                         await context.Chapters.AddAsync(newChapter);
                         await context.SaveChangesAsync();
                         System.Diagnostics.Debug.WriteLine($"[LibraryService] Created and marked online chapter as read: {chapterTitle}");
                     }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LibraryService] Error marking as read: {ex}");
            }
        }

        public async Task MarkChaptersAsReadAsync(Manga manga)
        {
            using var context = new MangaDbContext();
            Manga? existing = null;
            
            if (manga.Id > 0) existing = await context.Mangas.Include(m => m.Chapters).FirstOrDefaultAsync(m => m.Id == manga.Id);
            
            if (existing == null)
            {
                existing = await context.Mangas.Include(m => m.Chapters)
                                       .FirstOrDefaultAsync(m => m.Url == manga.Url && m.Source == manga.Source);
            }
            
            if (existing != null && existing.Chapters != null)
            {
                foreach (var ch in existing.Chapters)
                {
                    ch.Read = true;
                }
                await context.SaveChangesAsync();
                System.Diagnostics.Debug.WriteLine($"[LibraryService] Marked all chapters read for: {existing.Title}");
            }
        }

        public async Task MarkChaptersBeforeAsReadAsync(Manga manga, float chapterNumber)
        {
             await _dbLock.WaitAsync(); // Use lock to be safe
             try
             {
                 using var context = new MangaDbContext();
                 // Find Manga ID first
                 var dbManga = await context.Mangas.FirstOrDefaultAsync(m => m.Id == manga.Id || (m.Url == manga.Url && m.Source == manga.Source));

                 if (dbManga != null)
                 {
                     // Get all unread chapters strictly before this number
                     var chapters = await context.Chapters
                         .Where(c => c.MangaId == dbManga.Id && c.ChapterNumber < chapterNumber && !c.Read)
                         .ToListAsync();
                     
                     if (chapters.Any())
                     {
                         foreach(var c in chapters) c.Read = true;
                         await context.SaveChangesAsync();
                         System.Diagnostics.Debug.WriteLine($"[LibraryService] Marked {chapters.Count} previous chapters as read for {dbManga.Title}");
                     }
                 }
             }
             finally
             {
                 _dbLock.Release();
             }
        }

        public async Task<int> UpdateAllLibraryMangaAsync(SourceManager sourceManager)
        {
            System.Diagnostics.Debug.WriteLine("[LibraryService] Starting parallel library update...");
            int updatedCount = 0;
            try
            {
                var libraryManga = await GetLibraryMangaAsync();
                
                // Process in parallel (Limit 5 to avoid timeouts/bans)
                var options = new ParallelOptions { MaxDegreeOfParallelism = 5 };
                
                await Parallel.ForEachAsync(libraryManga, options, async (manga, token) =>
                {
                    try
                    {
                        var source = sourceManager.GetSource(manga.Source);
                        if (source != null)
                        {
                            var chapters = await source.GetChapterListAsync(manga.Url);
                            int newCount = await UpdateChaptersAsync(manga, chapters);
                            
                            if (newCount > 0)
                            {
                                System.Threading.Interlocked.Add(ref updatedCount, newCount);
                                System.Diagnostics.Debug.WriteLine($"[LibraryService] Updated {manga.Title}: Found {newCount} new chapters");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[LibraryService] Error updating {manga.Title}: {ex.Message}");
                    }
                });
                
                System.Diagnostics.Debug.WriteLine($"[LibraryService] Update complete. Total new: {updatedCount}");
                
                // Notify subscribers (e.g. UpdatesViewModel)
                UpdatesFound?.Invoke(this, updatedCount);
                
                return updatedCount;
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"[LibraryService] Error in UpdateAllLibraryMangaAsync: {ex.Message}");
            }
            return updatedCount;
        }

        public async Task ClearDatabaseAsync()
        {
            try
            {
                using var context = new MangaDbContext();
                await context.Database.EnsureDeletedAsync();
                await context.Database.EnsureCreatedAsync();
                System.Diagnostics.Debug.WriteLine("[LibraryService] Database cleared and recreated.");

                // Also delete downloaded files
                var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
                var downloadsFolder = System.IO.Path.Combine(appData, "Yomic", "Downloads");
                var coversFolder = System.IO.Path.Combine(appData, "Yomic", "covers");

                if (System.IO.Directory.Exists(downloadsFolder))
                {
                    System.IO.Directory.Delete(downloadsFolder, true);
                    System.Diagnostics.Debug.WriteLine("[LibraryService] Downloads folder deleted.");
                }

                if (System.IO.Directory.Exists(coversFolder))
                {
                    System.IO.Directory.Delete(coversFolder, true);
                    System.Diagnostics.Debug.WriteLine("[LibraryService] Covers cache folder deleted.");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LibraryService] Error clearing database: {ex.Message}");
                throw;
            }
        }
    }
}
