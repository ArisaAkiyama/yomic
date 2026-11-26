using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Yomic.Core.Data;
using Yomic.Core.Sources;

namespace Yomic.Core.Services
{
    /// <summary>
    /// One-time migration service to fix Manga.Source IDs after transitioning
    /// from non-deterministic string.GetHashCode() to deterministic SHA256-based IDs.
    /// </summary>
    public static class SourceIdMigrationService
    {
        private static readonly string MigrationFlagFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Yomic", "source_id_migration_v1.done");

        public static Task RunIfNeededAsync(SourceManager sourceManager)
        {
            return RunIfNeededAsync(sourceManager, customDbPath: null, customFlagFile: null);
        }

        internal static async Task RunIfNeededAsync(SourceManager sourceManager, string? customDbPath, string? customFlagFile)
        {
            string flagFile = string.IsNullOrWhiteSpace(customFlagFile) ? MigrationFlagFile : customFlagFile;
            try
            {
                // One-time v1 -> v2 migration: skip once completed so uninstalled/disabled extensions are never hijacked!
                if (File.Exists(flagFile))
                {
                    return;
                }

                var sources = sourceManager.GetSources();
                if (sources.Count == 0)
                {
                    LogService.Info("SourceIdMigration", "No sources loaded yet, skipping migration for now.");
                    return;
                }

                var validSourceIds = new HashSet<long>(sources.Select(s => s.Id));

                await MangaDbContext.DatabaseWriteGate.WaitAsync();
                try
                {
                    using var db = string.IsNullOrWhiteSpace(customDbPath) ? new MangaDbContext() : new MangaDbContext(customDbPath);
                    var allManga = await db.Mangas.ToListAsync();
                    var existingPairs = new HashSet<(string Url, long Source)>(
                        allManga.Select(m => ((m.Url ?? string.Empty).Trim().ToLowerInvariant(), m.Source)));

                    var orphanedManga = allManga.Where(m => !validSourceIds.Contains(m.Source)).ToList();

                    if (orphanedManga.Count == 0)
                    {
                        LogService.Info("SourceIdMigration", "All manga have valid Source IDs. Marking migration as done.");
                        MarkMigrationDone(flagFile);
                        return;
                    }

                    LogService.Info("SourceIdMigration", $"Found {orphanedManga.Count} manga with outdated Source IDs. Remapping...");

                    var domainMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    foreach (var src in sources)
                    {
                        if (!string.IsNullOrEmpty(src.BaseUrl) && Uri.TryCreate(src.BaseUrl, UriKind.Absolute, out var baseUri))
                        {
                            var host = NormalizeDomain(baseUri.Host);
                            domainMap.TryAdd(host, src.Id);
                        }
                    }

                    var keywordFallbacks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "komikcast", "komikcast" },
                        { "komiku", "komiku" },
                        { "kiryuu", "kiryuu" },
                        { "shinigami", "shinigami" },
                        { "maid", "maid" },
                        { "bacakomik", "bacakomik" },
                        { "westmanga", "westmanga" },
                        { "mgkomik", "mgkomik" },
                        { "mangadex", "mangadex" },
                        { "mangabat", "mangabat" },
                        { "manganato", "mangabat" },
                        { "mangakakalot", "mangabat" },
                        { "nhentai", "nhentai" },
                        { "weebcentral", "weebcentral" },
                        { "manhwaindo", "manhwaindo" },
                        { "softkomik", "softkomik" },
                    };

                    int fixedCount = 0;
                    foreach (var manga in orphanedManga)
                    {
                        long? matchedSourceId = null;

                        // 1. Direct legacy v1 ID mapping
                        if (manga.Source == 3639673976007021568L)
                        {
                            var kiryuuSrc = sources.FirstOrDefault(s => s.Name != null && s.Name.Contains("Kiryuu", StringComparison.OrdinalIgnoreCase));
                            if (kiryuuSrc != null) matchedSourceId = kiryuuSrc.Id;
                        }
                        else if (manga.Source == -3406828676302900168L)
                        {
                            var komikuSrc = sources.FirstOrDefault(s => s.Name != null && s.Name.Contains("Komiku", StringComparison.OrdinalIgnoreCase));
                            if (komikuSrc != null) matchedSourceId = komikuSrc.Id;
                        }

                        // 2. Exact host domain matching
                        string? mangaHost = null;
                        if (!string.IsNullOrEmpty(manga.Url) && Uri.TryCreate(manga.Url, UriKind.Absolute, out var mangaUri))
                        {
                            mangaHost = NormalizeDomain(mangaUri.Host);
                            if (matchedSourceId == null && domainMap.TryGetValue(mangaHost, out var idByDomain))
                            {
                                matchedSourceId = idByDomain;
                            }
                        }

                        // 3. Host-only keyword matching (NEVER match URL path slugs or generic "/manga/"!)
                        if (matchedSourceId == null && !string.IsNullOrEmpty(mangaHost))
                        {
                            foreach (var kv in keywordFallbacks)
                            {
                                if (mangaHost.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                                {
                                    var matchingSrc = sources.FirstOrDefault(s =>
                                        (s.Name != null && s.Name.Contains(kv.Value, StringComparison.OrdinalIgnoreCase)) ||
                                        (s.BaseUrl != null && s.BaseUrl.Contains(kv.Value, StringComparison.OrdinalIgnoreCase)));

                                    if (matchingSrc != null)
                                    {
                                        matchedSourceId = matchingSrc.Id;
                                        break;
                                    }
                                }
                            }
                        }

                        if (matchedSourceId.HasValue)
                        {
                            var normalizedUrl = (manga.Url ?? string.Empty).Trim().ToLowerInvariant();
                            if (existingPairs.Contains((normalizedUrl, matchedSourceId.Value)))
                            {
                                LogService.Warning("SourceIdMigration", $"Skipping remap for '{manga.Title}' -> {matchedSourceId.Value} (duplicate already exists).");
                                continue;
                            }

                            long oldId = manga.Source;
                            manga.Source = matchedSourceId.Value;
                            existingPairs.Add((normalizedUrl, matchedSourceId.Value));
                            fixedCount++;
                            LogService.Info("SourceIdMigration", $"Remapped '{manga.Title}': {oldId} -> {matchedSourceId.Value}");
                        }
                        else
                        {
                            LogService.Warning("SourceIdMigration", $"Could not remap '{manga.Title}' (URL: {manga.Url}, OldSource: {manga.Source})");
                        }
                    }

                    if (fixedCount > 0)
                    {
                        await db.SaveChangesAsync();
                        LogService.Success("SourceIdMigration", $"Successfully migrated {fixedCount}/{orphanedManga.Count} manga Source IDs.");
                    }

                    // Always mark the one-time v1 -> v2 migration done after running with loaded sources;
                    // remaining unmatched rows belong to uninstalled/removed extensions and must not re-trigger migration forever.
                    MarkMigrationDone(flagFile);
                }
                finally
                {
                    MangaDbContext.DatabaseWriteGate.Release();
                }
            }
            catch (Exception ex)
            {
                LogService.Error("SourceIdMigration", "Error during Source ID migration", ex);
            }
        }

        private static string NormalizeDomain(string host)
        {
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                host = host.Substring(4);
            return host.ToLowerInvariant();
        }

        private static void MarkMigrationDone(string flagFile)
        {
            try
            {
                var dir = Path.GetDirectoryName(flagFile);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(flagFile, DateTime.UtcNow.ToString("O"));
            }
            catch { }
        }
    }
}
