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
         * 1. EKSTENSI (EXTENSIONS)
         * ========================================================================= */
        private async Task<List<ExtensionDto>> GetExtensionsDtosAsync(bool forceRefresh)
        {
            var list = await ExtensionRepoService.GetExtensionsListAsync(forceRefresh);
            var sources = SourceManager.GetSources();
            return list.Select(ext =>
            {
                var src = ext.SourceId != 0 ? sources.FirstOrDefault(s => s.Id == ext.SourceId) : null;
                var filterable = src as IFilterableMangaSource;
                return new ExtensionDto
                {
                    Id = ext.Id,
                    Pkg = ext.Pkg,
                    SourceId = ext.SourceId,
                    Name = ext.Name,
                    Lang = ext.Lang,
                    Version = ext.Version,
                    IsNsfw = ext.IsNsfw,
                    Installed = ext.Installed,
                    HasUpdate = ext.HasUpdate,
                    IsCloudflareProtected = ext.IsCloudflareProtected,
                    FileSizeText = ext.FileSizeText,
                    BaseUrl = ext.BaseUrl,
                    AccentColor = ext.AccentColor,
                    IconUrl = ext.IconUrl,
                    Status = ext.Status,
                    SupportsStatusFilter = filterable?.SupportsStatusFilter ?? false,
                    SupportsGenreFilter = filterable?.SupportsGenreFilter ?? false,
                    AvailableGenres = filterable?.AvailableGenres ?? new List<string>(),
                    SupportsFormatFilter = filterable?.SupportsFormatFilter ?? false,
                    AvailableFormats = filterable?.AvailableFormats ?? new List<string>()
                };
            }).ToList();
        }

        private async Task<List<ExtensionDto>> HandleInstallExtensionAsync(string id)
        {
            var (success, msg) = await ExtensionRepoService.InstallOrUpdateAsync(id, pct =>
            {
                PushEvent("event:ext-progress", new { id, progress = pct });
            });
            if (!success)
            {
                LogService.Error("BackendIpc", $"Failed to install extension {id}: {msg}");
            }
            return await GetExtensionsDtosAsync(false);
        }

        private async Task<List<ExtensionDto>> HandleUninstallExtensionAsync(string id)
        {
            ExtensionRepoService.UninstallExtension(id);
            return await GetExtensionsDtosAsync(false);
        }

        private async Task<object> HandleVerifyCloudflareAsync(string id, string baseUrl)
        {
            string targetUrl = baseUrl;
            if (string.IsNullOrWhiteSpace(targetUrl))
            {
                var exts = await ExtensionRepoService.GetExtensionsListAsync(false);
                targetUrl = exts.FirstOrDefault(e => e.Id == id)?.BaseUrl ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(targetUrl))
            {
                return new { success = false, verified = false, message = "URL sumber tidak ditemukan." };
            }
            if (!Yomic.Core.Helpers.UrlSecurityHelper.IsSafePublicHttpUrl(targetUrl, out _))
            {
                LogService.Warning("BackendIpc", $"Blocked unsafe Cloudflare verification URL: {targetUrl}");
                return new { success = false, verified = false, message = L("URL sumber tidak valid atau tidak diizinkan.", "Invalid or disallowed source URL.") };
            }
            try
            {
                var (ua, cookies) = await CloudflareBypassService.Instance.SolveInteractiveAsync(targetUrl);
                bool solved = cookies != null && cookies.Count > 0;
                _ = SourceStatusService.CheckAllSourcesAsync();
                return new
                {
                    success = solved,
                    verified = solved,
                    message = solved
                        ? L($"Verifikasi Cloudflare untuk {targetUrl} berhasil!", $"Cloudflare verification for {targetUrl} succeeded!")
                        : L($"Sesi browser verifikasi {targetUrl} selesai.", $"Browser verification session for {targetUrl} finished.")
                };
            }
            catch (Exception ex)
            {
                return new { success = false, verified = false, message = L($"Gagal memverifikasi Cloudflare: {ex.Message}", $"Failed to verify Cloudflare: {ex.Message}") };
            }
        }

        private async Task<object> HandleManualPluginInstallAsync()
        {
            string? selectedFile = null;
            await _dispatcher.InvokeAsync(() =>
            {
                var dlg = new OpenFileDialog
                {
                    Title = L("Pilih Berkas Plugin Ekstensi Yomic (.js)", "Select Yomic Extension Plugin File (.js)"),
                    Filter = "JavaScript Plugin (*.js)|*.js"
                };
                if (dlg.ShowDialog(_ownerWindow) == true)
                {
                    selectedFile = dlg.FileName;
                }
            });
            if (string.IsNullOrWhiteSpace(selectedFile))
            {
                return new { success = false, cancelled = true, message = L("Pemasangan dibatalkan.", "Installation cancelled.") };
            }
            var src = SourceManager.InstallPlugin(selectedFile);
            var extensions = await GetExtensionsDtosAsync(false);
            string installMsg = L("Berkas plugin .js tidak valid.", "Invalid .js plugin file.");
            if (src != null)
            {
                if (src.IsNsfw && !SettingsService.ShowNsfwSources)
                {
                    installMsg = L(
                        $"Plugin {src.Name} (18+) berhasil dipasang. Aktifkan 'Tampilkan Sumber Dewasa (18+)' di Pengaturan untuk menampilkannya.",
                        $"Plugin {src.Name} (18+) installed. Enable 'Show Adult Sources (18+)' in Settings to display it.");
                }
                else
                {
                    installMsg = L($"Plugin {src.Name} berhasil dipasang.", $"Plugin {src.Name} installed.");
                }
            }
            return new
            {
                success = src != null,
                cancelled = false,
                message = installMsg,
                extensions
            };
        }

        /* =========================================================================
         * 2. JELAJAHI SUMBER (BROWSE & SOURCE CATALOG FEED)
         * ========================================================================= */
        private async Task<List<ExtensionDto>> HandleRefreshSourceStatusAsync()
        {
            await SourceStatusService.CheckAllSourcesAsync();
            return await GetExtensionsDtosAsync(false);
        }

        private async Task<SourceFeedResultDto> HandleFetchSourceFeedAsync(JsonElement? payload)
        {
            long sourceId = GetLong(payload, "sourceId");
            string extId = GetString(payload, "extId");
            string pkg = GetString(payload, "pkg");
            string sourceName = GetString(payload, "sourceName");
            int page = Math.Max(1, GetInt(payload, "page", 1));
            string sort = GetString(payload, "sort", "popular");
            string query = GetString(payload, "query").Trim();
            string statusFilter = GetString(payload, "status", "all");
            string typeFilter = GetString(payload, "type", "all");
            string genreFilter = GetString(payload, "genre", "Semua");
            IMangaSource? source = null;
            // 1. Prefer exact sourceId lookup FIRST to prevent substring collisions from fuzzy name matching
            if (sourceId != 0)
            {
                source = SourceManager.GetSource(sourceId);
            }
            // 2. Fallback to matching by sourceName / pkg / extId when sourceId is absent or unmapped
            if (source == null && !string.IsNullOrEmpty(sourceName))
            {
                source = SourceManager.GetSourceByName(sourceName);
            }
            if (source == null && !string.IsNullOrEmpty(pkg))
            {
                source = SourceManager.GetSourceByName(pkg);
            }
            if (source == null && !string.IsNullOrEmpty(extId))
            {
                source = SourceManager.GetSourceByName(extId);
            }
            // 3. Try from extension repository catalog
            if (source == null && (!string.IsNullOrEmpty(extId) || !string.IsNullOrEmpty(pkg)))
            {
                var exts = await ExtensionRepoService.GetExtensionsListAsync(false);
                var matchedExt = exts.FirstOrDefault(e =>
                    string.Equals(e.Id, extId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.Pkg, pkg, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(sourceName) && string.Equals(e.Name, sourceName, StringComparison.OrdinalIgnoreCase)));
                if (matchedExt != null && matchedExt.SourceId != 0)
                {
                    source = SourceManager.GetSource(matchedExt.SourceId);
                }
            }
            if (source == null)
            {
                LogService.Warning("BackendIpc", $"[source:fetch-feed] No matching source found for sourceId={sourceId}, extId='{extId}', pkg='{pkg}', sourceName='{sourceName}'");
                return new SourceFeedResultDto { Page = page, TotalPages = 1, HasMore = false };
            }
            if (!SettingsService.ShowNsfwSources && (source.IsNsfw || ExtensionRepositoryService.IsAdultIndicator(source.Name, source.Description, source.BaseUrl)))
            {
                LogService.Info("BackendIpc", $"[source:fetch-feed] Blocked NSFW source '{source.Name}' because ShowNsfwSources is disabled.");
                return new SourceFeedResultDto { SourceId = source.Id, Page = page, TotalPages = 1, HasMore = false, Items = new List<MangaDto>() };
            }
            int statusCode = statusFilter.Equals("Ongoing", StringComparison.OrdinalIgnoreCase)
                ? 1
                : statusFilter.Equals("Completed", StringComparison.OrdinalIgnoreCase)
                    ? 2
                    : 0;
            List<string>? genresList = !string.IsNullOrWhiteSpace(genreFilter) && !genreFilter.Equals("Semua", StringComparison.OrdinalIgnoreCase)
                ? new List<string> { genreFilter }
                : null;
            List<string>? formatsList = !string.IsNullOrWhiteSpace(typeFilter) && !typeFilter.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? new List<string> { typeFilter }
                : null;
            string reqMdLang = GetString(payload, "mangaDexLanguage");
            if (!string.IsNullOrWhiteSpace(reqMdLang))
            {
                string normMd = string.Equals(reqMdLang.Trim(), "id", StringComparison.OrdinalIgnoreCase) ? "id" : "en";
                if (!string.Equals(SettingsService.MangaDexLanguage, normMd, StringComparison.OrdinalIgnoreCase))
                {
                    SettingsService.MangaDexLanguage = normMd;
                    SettingsService.Save();
                }
                JsMangaSource.SelectedLanguage = normMd;
            }
            else
            {
                JsMangaSource.SelectedLanguage = string.Equals(SettingsService.MangaDexLanguage, "id", StringComparison.OrdinalIgnoreCase) ? "id" : "en";
            }
            List<Manga> rawItems;
            int totalPages = page;
            if (!string.IsNullOrWhiteSpace(query))
            {
                rawItems = await source.GetSearchMangaAsync(query, page);
                totalPages = rawItems.Count > 0 ? page + 1 : page;
            }
            else if (source is IFilterableMangaSource filterableSrc)
            {
                if (statusCode != 0 || genresList != null || formatsList != null)
                {
                    var res = await filterableSrc.GetMangaListAsync(page, statusCode, genresList, formatsList, sort);
                    rawItems = res.Items;
                    totalPages = res.TotalPages;
                }
                else if (sort.Equals("latest", StringComparison.OrdinalIgnoreCase))
                {
                    var res = await filterableSrc.GetLatestMangaAsync(page);
                    rawItems = res.Items;
                    totalPages = res.TotalPages;
                }
                else
                {
                    var res = await filterableSrc.GetMangaListAsync(page);
                    rawItems = res.Items;
                    totalPages = res.TotalPages;
                }
            }
            else
            {
                rawItems = await source.GetPopularMangaAsync(page);
                totalPages = rawItems.Count > 0 ? page + 1 : page;
            }
            if (!SettingsService.ShowNsfwSources)
            {
                rawItems = rawItems.Where(m =>
                {
                    if (ExtensionRepositoryService.IsAdultIndicator(m.Title, m.Description, m.Url)) return false;
                    if (m.Genre != null && m.Genre.Any(g => ExtensionRepositoryService.IsAdultIndicator(g))) return false;
                    return true;
                }).ToList();
            }
            // Cross-reference with SQLite DB so existing Library/History manga keep their DB ID and InLibrary status
            using var db = new MangaDbContext();
            var urls = rawItems.Select(m => m.Url).Where(u => !string.IsNullOrEmpty(u)).Distinct().ToList();
            var dbList = await db.Mangas
                .Include(m => m.Chapters)
                .Include(m => m.Categories)
                .Include(m => m.Tracks)
                .Where(m => m.Source == source.Id && urls.Contains(m.Url))
                .ToListAsync();
            var dbMatches = dbList
                .GroupBy(m => m.Url, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(x => x.Favorite)
                          .ThenByDescending(x => x.Chapters?.Count ?? 0)
                          .ThenByDescending(x => x.Id)
                          .First(),
                    StringComparer.OrdinalIgnoreCase);
            var dtos = new List<MangaDto>();
            foreach (var item in rawItems)
            {
                item.Source = source.Id;
                if (dbMatches.TryGetValue(item.Url, out var dbManga))
                {
                    if (string.IsNullOrEmpty(dbManga.ThumbnailUrl) && !string.IsNullOrEmpty(item.ThumbnailUrl))
                    {
                        dbManga.ThumbnailUrl = item.ThumbnailUrl;
                    }
                    dtos.Add(MapMangaToDto(dbManga, includeChapters: false));
                }
                else
                {
                    dtos.Add(MapMangaToDto(item, includeChapters: false));
                }
            }
            return new SourceFeedResultDto
            {
                SourceId = source.Id,
                Page = page,
                TotalPages = Math.Max(page, totalPages),
                HasMore = page < totalPages || (rawItems.Count >= 12 && source.IsHasMorePages),
                Items = dtos
            };
        }
    }
}
