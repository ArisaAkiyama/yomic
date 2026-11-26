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
         * 8. UNDUHAN (DOWNLOADS)
         * ========================================================================= */
        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "Tersimpan";
            if (bytes < 1024 * 1024)
            {
                double kb = bytes / 1024.0;
                return $"{kb:0.0} KB";
            }
            double mb = bytes / (1024.0 * 1024.0);
            return $"{mb:0.0} MB";
        }

        private static string FormatDirectorySize(string dirPath)
        {
            try
            {
                if (!Directory.Exists(dirPath)) return "Tersimpan";
                var dirInfo = new DirectoryInfo(dirPath);
                long bytes = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                if (bytes <= 0)
                {
                    int fileCount = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Count();
                    return fileCount > 0 ? $"{fileCount} hal." : "Tersimpan";
                }
                if (bytes < 1024 * 1024)
                {
                    double kb = bytes / 1024.0;
                    return $"{kb:0.0} KB";
                }
                double mb = bytes / (1024.0 * 1024.0);
                return $"{mb:0.1} MB";
            }
            catch
            {
                return "Tersimpan";
            }
        }

        internal static (string Status, string SpeedText) MapDownloadStatus(string? rawStatus)
        {
            string s = rawStatus ?? string.Empty;
            if (string.Equals(s, "Downloading", StringComparison.OrdinalIgnoreCase))
            {
                return ("downloading", "Mengunduh...");
            }
            if (s.StartsWith("Retrying", StringComparison.OrdinalIgnoreCase))
            {
                return ("downloading", s);
            }
            if (string.Equals(s, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                return ("completed", "Tersimpan");
            }
            if (string.Equals(s, "Paused", StringComparison.OrdinalIgnoreCase))
            {
                return ("paused", "Dijeda");
            }
            if (string.Equals(s, "Error", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                return ("error", "Gagal");
            }
            return ("queued", "Dalam Antrean");
        }

        private List<DownloadQueueDto> GetDownloadsQueueList()
        {
            var all = DownloadService.AllDownloads.ToList();
            return all.Select((req, idx) =>
            {
                var src = SourceManager.GetSource(req.Manga.Source);
                string sourceName = src?.Name?.Replace("Yomic:", "", StringComparison.OrdinalIgnoreCase).Trim() ?? "Sumber Komik";
                var (mappedStatus, speedText) = MapDownloadStatus(req.Status);
                string sizeText;
                if (mappedStatus == "completed")
                {
                    long bytes = DownloadPathService.GetDownloadedChapterSizeBytes(req.Manga, req.Chapter);
                    sizeText = bytes > 0 ? FormatBytes(bytes) : "Tersimpan";
                }
                else
                {
                    sizeText = $"{req.Progress}%";
                }
                float chNum = req.Chapter.ChapterNumber >= 0 ? req.Chapter.ChapterNumber : 1;
                return new DownloadQueueDto
                {
                    Id = MakeDownloadQueueItemId(req.Manga.Source, req.Chapter.Url),
                    MangaId = MakeMangaFrontendId(req.Manga),
                    MangaTitle = req.Manga.Title,
                    ChapterLabel = !string.IsNullOrWhiteSpace(req.Chapter.Name) ? req.Chapter.Name : $"Chapter {chNum}",
                    ChapterNumber = chNum,
                    ChapterUrl = req.Chapter.Url,
                    CoverUrl = WebResourceImageProxy.BuildCoverProxyUrl(req.Manga.Id, req.Manga.ThumbnailUrl, req.Manga.Source),
                    SourceName = sourceName,
                    ProgressPercent = Math.Clamp(req.Progress, 0, 100),
                    SpeedText = speedText,
                    SizeText = sizeText,
                    Status = mappedStatus
                };
            }).ToList();
        }

        private async Task<List<DownloadQueueDto>> HandleEnqueueDownloadAsync(JsonElement? payload)
        {
            string mangaId = GetString(payload, "mangaId");
            string chapterUrl = GetString(payload, "chapterUrl");
            float chapterNumber = GetFloat(payload, "chapterNumber", -1);
            string mode = GetString(payload, "mode", "single"); // single | all | unread
            var manga = await ResolveMangaAsync(mangaId);
            if (manga == null || manga.Chapters == null)
                return GetDownloadsQueueList();
            if (mode == "all")
            {
                foreach (var ch in manga.Chapters.OrderBy(c => c.ChapterNumber))
                {
                    if (!DownloadPathService.IsChapterDownloaded(manga, ch))
                    {
                        DownloadService.QueueDownload(manga, ch);
                    }
                }
            }
            else if (mode == "unread")
            {
                foreach (var ch in manga.Chapters.Where(c => !c.Read).OrderBy(c => c.ChapterNumber))
                {
                    if (!DownloadPathService.IsChapterDownloaded(manga, ch))
                    {
                        DownloadService.QueueDownload(manga, ch);
                    }
                }
            }
            else
            {
                var ch = manga.Chapters.FirstOrDefault(c =>
                    (!string.IsNullOrEmpty(chapterUrl) && string.Equals(c.Url, chapterUrl, StringComparison.OrdinalIgnoreCase)) ||
                    Math.Abs(c.ChapterNumber - chapterNumber) < 0.01f);
                if (ch != null)
                {
                    DownloadService.QueueDownload(manga, ch);
                }
            }
            return GetDownloadsQueueList();
        }

        private List<DownloadQueueDto> HandleTogglePauseDownload(JsonElement? payload)
        {
            string id = GetString(payload, "id");
            string chapterUrl = GetString(payload, "chapterUrl");
            var req = DownloadService.AllDownloads.FirstOrDefault(r =>
                MakeDownloadQueueItemId(r.Manga?.Source ?? 0, r.Chapter.Url) == id ||
                $"dl-{ComputeShortHash(r.Chapter.Url)}" == id ||
                (!string.IsNullOrEmpty(chapterUrl) && string.Equals(r.Chapter.Url, chapterUrl, StringComparison.OrdinalIgnoreCase)));
            if (req != null)
            {
                if (req.Status == "Paused")
                    DownloadService.Resume(req);
                else
                    DownloadService.Pause(req);
            }
            return GetDownloadsQueueList();
        }

        private List<DownloadQueueDto> HandlePauseAllDownloads()
        {
            DownloadService.Pause();
            return GetDownloadsQueueList();
        }

        private List<DownloadQueueDto> HandleResumeAllDownloads()
        {
            DownloadService.Resume();
            return GetDownloadsQueueList();
        }

        private async Task<List<DownloadQueueDto>> HandleCancelDownloadAsync(JsonElement? payload)
        {
            string id = GetString(payload, "id");
            string chapterUrl = GetString(payload, "chapterUrl");
            var req = DownloadService.AllDownloads.FirstOrDefault(r =>
                MakeDownloadQueueItemId(r.Manga?.Source ?? 0, r.Chapter.Url) == id ||
                $"dl-{ComputeShortHash(r.Chapter.Url)}" == id ||
                (!string.IsNullOrEmpty(chapterUrl) && string.Equals(r.Chapter.Url, chapterUrl, StringComparison.OrdinalIgnoreCase)));
            if (req != null)
            {
                bool wasActive = req.Status == "Downloading" || req.Status.StartsWith("Retrying", StringComparison.OrdinalIgnoreCase);
                // Cancel active/queued download FIRST so file handles are released and worker cannot overwrite IsDownloaded = true
                DownloadService.Cancel(req);
                if (wasActive)
                {
                    await DownloadService.WaitForActiveDownloadsToStopAsync(TimeSpan.FromSeconds(3));
                }

                if (req.Status == "Completed" || (req.Manga != null && req.Chapter != null && DownloadPathService.IsChapterDownloaded(req.Manga, req.Chapter)))
                {
                    try
                    {
                        if (req.Manga != null && req.Chapter != null)
                        {
                            DownloadPathService.DeleteDownloadedChapter(req.Manga, req.Chapter);
                        }
                    }
                    catch { }
                    if (req.Chapter != null)
                    {
                        req.Chapter.IsDownloaded = false;
                        if (!string.IsNullOrEmpty(req.Chapter.Url))
                        {
                            await LibraryService.UpdateChapterDownloadStatusByUrlAsync(req.Chapter.Url, false, req.Manga?.Id ?? 0);
                        }
                    }
                }
                else if (req.Manga != null && req.Chapter != null)
                {
                    // Also clean up any partial .tmp folder left behind by a previously paused download
                    try
                    {
                        var tempDir = DownloadPathService.GetChapterTempDirectory(req.Manga, req.Chapter);
                        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                    }
                    catch { }
                }
            }
            return GetDownloadsQueueList();
        }

        private List<DownloadQueueDto> HandleClearCompletedDownloads()
        {
            DownloadService.ClearCompleted();
            return GetDownloadsQueueList();
        }

        private async Task<object> HandleDeleteChapterDownloadAsync(JsonElement? payload)
        {
            string mangaId = GetString(payload, "mangaId");
            string chapterUrl = GetString(payload, "chapterUrl");
            float chapterNumber = GetFloat(payload, "chapterNumber", -1);
            var manga = await ResolveMangaAsync(mangaId);
            if (manga?.Chapters != null)
            {
                var ch = manga.Chapters.FirstOrDefault(c =>
                    (!string.IsNullOrEmpty(chapterUrl) && string.Equals(c.Url, chapterUrl, StringComparison.OrdinalIgnoreCase)) ||
                    Math.Abs(c.ChapterNumber - chapterNumber) < 0.01f);
                if (ch != null)
                {
                    // Cancel active/queued download for this chapter FIRST so file handles close before deleting on disk
                    var matchReqs = DownloadService.AllDownloads.Where(r =>
                        (r.Manga?.Source == manga.Source && string.Equals(r.Chapter.Url, ch.Url, StringComparison.OrdinalIgnoreCase)) ||
                        (r.Manga?.Id == manga.Id && Math.Abs(r.Chapter.ChapterNumber - ch.ChapterNumber) < 0.01f))
                        .ToList();
                    bool anyWasActive = matchReqs.Any(r => r.Status == "Downloading" || r.Status.StartsWith("Retrying", StringComparison.OrdinalIgnoreCase));
                    foreach (var matchReq in matchReqs)
                    {
                        DownloadService.Cancel(matchReq);
                    }
                    if (anyWasActive)
                    {
                        await DownloadService.WaitForActiveDownloadsToStopAsync(TimeSpan.FromSeconds(3));
                    }

                    DownloadPathService.DeleteDownloadedChapter(manga, ch);
                    ch.IsDownloaded = false;
                    await LibraryService.UpdateChapterDownloadStatusByUrlAsync(ch.Url, false, manga.Id);
                }
            }
            return new { ok = true, downloads = GetDownloadsQueueList() };
        }

        private async Task<object> HandleDeleteMangaDownloadsAsync(string mangaId)
        {
            var manga = await ResolveMangaAsync(mangaId);
            if (manga != null)
            {
                // Cancel active/queued downloads for this manga FIRST so file handles are released before Directory.Delete
                var mangaReqs = DownloadService.AllDownloads
                    .Where(r => r.Manga?.Id == manga.Id || (r.Manga?.Url != null && r.Manga.Url == manga.Url && r.Manga.Source == manga.Source))
                    .ToList();
                foreach (var mr in mangaReqs)
                {
                    DownloadService.Cancel(mr);
                }
                if (mangaReqs.Count > 0)
                {
                    await DownloadService.WaitForActiveDownloadsToStopAsync(TimeSpan.FromSeconds(3));
                }

                var mangaDir = DownloadPathService.GetMangaDirectory(manga);
                if (Directory.Exists(mangaDir))
                {
                    try { Directory.Delete(mangaDir, true); } catch { }
                }
                if (manga.Chapters != null && manga.Chapters.Count > 0)
                {
                    foreach (var ch in manga.Chapters)
                    {
                        ch.IsDownloaded = false;
                    }
                    if (manga.Id > 0)
                    {
                        await LibraryService.MarkAllChaptersNotDownloadedAsync(manga.Id);
                    }
                    else
                    {
                        foreach (var ch in manga.Chapters)
                        {
                            await LibraryService.UpdateChapterDownloadStatusByUrlAsync(ch.Url, false, 0);
                        }
                    }
                }
            }
            return new { ok = true, downloads = GetDownloadsQueueList() };
        }
    }
}
