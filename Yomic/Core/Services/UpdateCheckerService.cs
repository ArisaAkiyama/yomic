using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Yomic.Core.Data;
using Yomic.Core.Models;

namespace Yomic.Core.Services
{
    public class UpdateResult
    {
        public string MangaTitle { get; set; } = string.Empty;
        public string MangaUrl { get; set; } = string.Empty;
        public int NewChapterCount => NewChapters.Count;
        public List<Chapter> NewChapters { get; set; } = new List<Chapter>();
    }

    public class UpdateCheckerService
    {
        private readonly SourceManager _sourceManager;
        private readonly LibraryService _libraryService;
        private readonly SemaphoreSlim _semaphore;

        public UpdateCheckerService(SourceManager sourceManager, LibraryService libraryService)
        {
            _sourceManager = sourceManager;
            _libraryService = libraryService;
            // Limit to 3 concurrent requests to prevent IP bans
            _semaphore = new SemaphoreSlim(3);
        }

        /// <summary>
        /// Checks for updates for the provided list of manga.
        /// Performs "Smart Diff" to avoid false positives on initial import.
        /// </summary>
        public async Task<List<UpdateResult>> CheckForUpdatesAsync(List<Manga> libraryManga)
        {
            var results = new List<UpdateResult>();
            var tasks = new List<Task>();

            // Capture start time for "Last Check" reference if needed, 
            // though we rely on per-manga LastUpdate logic.
            
            foreach (var manga in libraryManga)
            {
                tasks.Add(ProcessMangaUpdateAsync(manga, results));
            }

            await Task.WhenAll(tasks);
            return results;
        }

        private async Task ProcessMangaUpdateAsync(Manga manga, List<UpdateResult> results)
        {
            await _semaphore.WaitAsync();
            try
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateChecker] Starting check for: {manga.Title}");
                var source = _sourceManager.GetSource(manga.Source);
                if (source == null) return;

                // 1. Fetch Remote Chapters
                var remoteChapters = await source.GetChapterListAsync(manga.Url);
                System.Diagnostics.Debug.WriteLine($"[UpdateChecker] Fetched {remoteChapters?.Count ?? 0} chapters from source for {manga.Title}");
                if (remoteChapters == null || remoteChapters.Count == 0) return;

                // 2. Get Local State (Snapshot)
                // We access DB directly here to get a clean state before UpdateChaptersAsync modifies it
                List<string> existingUrls;
                bool isInitialImport = false;
                long lastUpdate = manga.LastUpdate; // Epoch

                using (var context = new MangaDbContext())
                {
                    var dbManga = await context.Mangas
                        .Include(m => m.Chapters)
                        .FirstOrDefaultAsync(m => m.Id == manga.Id);

                    if (dbManga == null) return; // Should not happen for library items

                    existingUrls = dbManga.Chapters.Select(c => c.Url).ToList();
                    isInitialImport = dbManga.Chapters.Count == 0;
                    System.Diagnostics.Debug.WriteLine($"[UpdateChecker] Local state for {manga.Title}: {existingUrls.Count} existing chapters. Initial Import: {isInitialImport}");
                }

                // 3. Smart Diff Logic
                // Identify TRULY new chapters (present in remote, missing in local)
                var newChapters = remoteChapters
                    .Where(r => !existingUrls.Contains(r.Url))
                    .ToList();
                
                System.Diagnostics.Debug.WriteLine($"[UpdateChecker] Diff result for {manga.Title}: {newChapters.Count} new chapters found (not in local DB).");

                // 4. Save to Database (Always Sync)
                // This ensures DB is up-to-date regardless of notification rules
                if (newChapters.Count > 0 || remoteChapters.Count != existingUrls.Count)
                {
                    await _libraryService.UpdateChaptersAsync(manga, remoteChapters);
                }

                // 5. Determine if we should Notify
                // Rule 1: Initial Import -> Do NOT notify
                if (isInitialImport)
                {
                    System.Diagnostics.Debug.WriteLine($"[UpdateChecker] Initial import for {manga.Title}. Suppressing notification.");
                    return;
                }

                // Rule 2: Regular Update -> Notify
                if (newChapters.Count > 0)
                {
                    // Filter: Ensure date is newer than last check (Optimization)
                    // Note: UpdateChaptersAsync logic usually handles the sorting, 
                    // but here we double-check if specific 'Smart' rules apply.
                    // For now, if it wasn't in DB, valid assumption is it's new.
                    
                    // We can refine 'New' by DateUpload if available from Source
                    // var trulyNew = newChapters.Where(c => c.DateUpload > lastUpdate).ToList(); 
                    // However, relying on DateUpload can be flaky if source has bad dates.
                    // The "ID not in DB" check is the most robust anchor.

                    var result = new UpdateResult
                    {
                        MangaTitle = manga.Title,
                        MangaUrl = manga.Url,
                        NewChapters = newChapters
                    };

                    lock (results)
                    {
                        results.Add(result);
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"[UpdateChecker] Found {newChapters.Count} updates for {manga.Title}");
                }
            }
            catch (Exception ex)
            {
                // Error Handling: Log and Continue (Skip this manga)
                System.Diagnostics.Debug.WriteLine($"[UpdateChecker] Error checking {manga.Title}: {ex.Message}");
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}
