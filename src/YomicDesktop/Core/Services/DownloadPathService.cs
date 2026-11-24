using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Yomic.Core.Helpers;
using Yomic.Core.Models;

namespace Yomic.Core.Services
{
    public static class DownloadPathService
    {
        public const string TempSuffix = ".tmp";
        public const string CbzExtension = ".cbz";
        private static readonly NaturalStringComparer NaturalComparer = new(descending: false);
        private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".avif"
        };

        public static string BaseDirectory
        {
            get
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(appData, "Yomic", "Downloads");
            }
        }

        public static string CbzExtractCacheDirectory
        {
            get
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(appData, "Yomic", "Cache", "CbzExtracted");
            }
        }

        public static string GetSourceDirectory(long sourceId)
        {
            return Path.Combine(BaseDirectory, sourceId.ToString());
        }

        public static string GetMangaDirectory(Manga manga)
        {
            var segment = SanitizePathSegment(manga.Title ?? "Unknown");
            if (segment.Length > 80)
            {
                segment = segment[..80].TrimEnd('.', ' ');
                if (string.IsNullOrWhiteSpace(segment)) segment = "Unknown";
            }
            return Path.Combine(GetSourceDirectory(manga.Source), segment);
        }

        public static string GetChapterDirectory(Manga manga, Chapter chapter)
        {
            return Path.Combine(GetMangaDirectory(manga), GetChapterDirectoryName(chapter.Name, chapter.Url));
        }

        public static string GetChapterCbzPath(Manga manga, Chapter chapter)
        {
            return GetChapterDirectory(manga, chapter) + CbzExtension;
        }

        public static string GetChapterTempDirectory(Manga manga, Chapter chapter)
        {
            return GetChapterDirectory(manga, chapter) + TempSuffix;
        }

        public static string? FindCompletedChapterCbz(Manga manga, Chapter chapter)
        {
            var mangaDir = GetMangaDirectory(manga);
            foreach (var chapterDir in GetChapterDirectoryCandidates(mangaDir, chapter.Name, chapter.Url))
            {
                var cbzPath = chapterDir + CbzExtension;
                if (File.Exists(cbzPath) && new FileInfo(cbzPath).Length > 0)
                {
                    return cbzPath;
                }
            }

            // Only check the "Unknown" folder when this manga itself has no real title (prevents cross-manga collisions)
            if (string.IsNullOrWhiteSpace(manga.Title) || string.Equals(manga.Title.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                var fallbackMangaDir = Path.Combine(GetSourceDirectory(manga.Source), "Unknown");
                foreach (var chapterDir in GetChapterDirectoryCandidates(fallbackMangaDir, chapter.Name, chapter.Url))
                {
                    var cbzPath = chapterDir + CbzExtension;
                    if (File.Exists(cbzPath) && new FileInfo(cbzPath).Length > 0)
                    {
                        return cbzPath;
                    }
                }
            }

            return null;
        }

        public static string? ExtractCbzToCacheDirectory(string cbzPath)
            => ExtractCbzToCacheDirectory(cbzPath, CbzExtractCacheDirectory);

        internal static string? ExtractCbzToCacheDirectory(string cbzPath, string cacheRootDir)
        {
            if (string.IsNullOrWhiteSpace(cbzPath) || !File.Exists(cbzPath))
            {
                return null;
            }

            string? tempExtractDir = null;
            try
            {
                var info = new FileInfo(cbzPath);
                var cacheKey = ShortHash($"{cbzPath}_{info.Length}_{info.LastWriteTimeUtc.Ticks}");
                var extractDir = Path.Combine(cacheRootDir, cacheKey);
                if (Directory.Exists(extractDir) && GetReadableFiles(extractDir).Count > 0)
                {
                    return extractDir;
                }

                Directory.CreateDirectory(cacheRootDir);
                // Extract atomically to a sibling temp folder first so partial/interrupted extracts never poison the cache
                tempExtractDir = Path.Combine(cacheRootDir, $"{cacheKey}_{Guid.NewGuid():N}.extracting");
                Directory.CreateDirectory(tempExtractDir);

                using (var archive = ZipFile.OpenRead(cbzPath))
                {
                    const int MaxCbzEntries = 600;
                    const long MaxCbzTotalBytes = 350L * 1024 * 1024; // 350 MB cap against zip bombs
                    if (archive.Entries.Count > MaxCbzEntries)
                    {
                        throw new InvalidDataException($"CBZ archive exceeds maximum allowed entry count ({MaxCbzEntries}).");
                    }

                    long totalExtracted = 0;
                    var buffer = new byte[81920];
                    int idx = 0;
                    foreach (var entry in archive.Entries.OrderBy(e => e.FullName, NaturalComparer))
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        var ext = Path.GetExtension(entry.Name);
                        if (!ImageExtensions.Contains(ext)) continue;
                        var destPath = Path.Combine(tempExtractDir, $"{idx:D4}{ext}");
                        using (var src = entry.Open())
                        using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                        {
                            int read;
                            while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                totalExtracted += read;
                                if (totalExtracted > MaxCbzTotalBytes)
                                {
                                    throw new InvalidDataException($"CBZ archive exceeded maximum allowed decompressed size ({MaxCbzTotalBytes} bytes).");
                                }
                                dst.Write(buffer, 0, read);
                            }
                        }
                        idx++;
                    }
                }

                if (GetReadableFiles(tempExtractDir).Count == 0)
                {
                    try { Directory.Delete(tempExtractDir, true); } catch { }
                    return null;
                }

                if (Directory.Exists(extractDir) && GetReadableFiles(extractDir).Count == 0)
                {
                    try { Directory.Delete(extractDir, true); } catch { }
                }

                if (!Directory.Exists(extractDir))
                {
                    try
                    {
                        Directory.Move(tempExtractDir, extractDir);
                        tempExtractDir = null;
                    }
                    catch
                    {
                        // Another concurrent reader finished moving extractDir first
                    }
                }

                return Directory.Exists(extractDir) && GetReadableFiles(extractDir).Count > 0 ? extractDir : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (tempExtractDir != null && Directory.Exists(tempExtractDir))
                {
                    try { Directory.Delete(tempExtractDir, true); } catch { }
                }
            }
        }

        public static bool DeleteDownloadedChapter(Manga manga, Chapter chapter)
        {
            bool deleted = false;
            var dir = FindCompletedChapterDirectory(manga, chapter);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                try
                {
                    Directory.Delete(dir, true);
                    deleted = true;
                }
                catch { }
            }

            var cbz = FindCompletedChapterCbz(manga, chapter);
            if (!string.IsNullOrEmpty(cbz) && File.Exists(cbz))
            {
                try
                {
                    File.Delete(cbz);
                    deleted = true;
                }
                catch { }
            }

            return deleted;
        }

        public static long GetDownloadedChapterSizeBytes(Manga manga, Chapter chapter)
        {
            var cbz = FindCompletedChapterCbz(manga, chapter);
            if (!string.IsNullOrEmpty(cbz) && File.Exists(cbz))
            {
                try { return new FileInfo(cbz).Length; } catch { }
            }

            var dir = FindCompletedChapterDirectory(manga, chapter);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                try
                {
                    return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                        .Sum(f => new FileInfo(f).Length);
                }
                catch { }
            }

            return 0;
        }

        public static string? FindCompletedChapterDirectory(Manga manga, Chapter chapter)
        {
            // Pure read-only lookup: NEVER move or return active/paused .tmp download directories!
            var mangaDir = GetMangaDirectory(manga);
            foreach (var chapterDir in GetChapterDirectoryCandidates(mangaDir, chapter.Name, chapter.Url))
            {
                if (IsCompletedChapterDirectory(chapterDir))
                {
                    return chapterDir;
                }
            }

            if (string.IsNullOrWhiteSpace(manga.Title) || string.Equals(manga.Title.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                var fallbackMangaDir = Path.Combine(GetSourceDirectory(manga.Source), "Unknown");
                foreach (var chapterDir in GetChapterDirectoryCandidates(fallbackMangaDir, chapter.Name, chapter.Url))
                {
                    if (IsCompletedChapterDirectory(chapterDir))
                    {
                        return chapterDir;
                    }
                }
            }

            return null;
        }

        public static bool IsChapterDownloaded(Manga manga, Chapter chapter)
        {
            return FindCompletedChapterDirectory(manga, chapter) != null
                || FindCompletedChapterCbz(manga, chapter) != null;
        }

        public static bool HasAnyDownloadedChapter(Manga manga)
        {
            var mangaDir = GetMangaDirectory(manga);
            if (!Directory.Exists(mangaDir))
            {
                return false;
            }

            try
            {
                return Directory.EnumerateDirectories(mangaDir)
                    .Any(d => !d.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase) && IsCompletedChapterDirectory(d))
                    || Directory.EnumerateFiles(mangaDir, "*" + CbzExtension)
                    .Any(f => new FileInfo(f).Length > 0);
            }
            catch
            {
                return false;
            }
        }

        public static int GetDownloadedChaptersCount(Manga manga)
        {
            var mangaDir = GetMangaDirectory(manga);
            if (!Directory.Exists(mangaDir))
            {
                return 0;
            }

            try
            {
                int dirCount = Directory.EnumerateDirectories(mangaDir)
                    .Count(d => !d.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase) && IsCompletedChapterDirectory(d));
                int cbzCount = Directory.EnumerateFiles(mangaDir, "*" + CbzExtension)
                    .Count(f => new FileInfo(f).Length > 0);
                return dirCount + cbzCount;
            }
            catch
            {
                return 0;
            }
        }

        public static IReadOnlyList<string> GetReadableFiles(string chapterDir, bool includeTempDirectory = false)
        {
            if (!Directory.Exists(chapterDir))
            {
                return Array.Empty<string>();
            }

            if (!includeTempDirectory && chapterDir.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return Array.Empty<string>();
            }

            return Directory.GetFiles(chapterDir)
                .Where(IsReadablePageFile)
                .OrderBy(f => Path.GetFileName(f), NaturalComparer)
                .ToList();
        }

        public static string SanitizePathSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "Unknown";
            }

            var sanitized = string.Join("_", value.Split(Path.GetInvalidFileNameChars()));
            sanitized = sanitized.Trim().TrimEnd('.', ' ');
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return "Unknown";
            }

            // Guard against Windows reserved DOS device names (including multi-dot forms like "CON.a.b")
            int firstDot = sanitized.IndexOf('.');
            string firstStem = firstDot >= 0 ? sanitized[..firstDot] : sanitized;
            string baseName = Path.GetFileNameWithoutExtension(sanitized);
            if (WindowsReservedNames.Contains(sanitized) ||
                WindowsReservedNames.Contains(baseName) ||
                WindowsReservedNames.Contains(firstStem))
            {
                sanitized = "_" + sanitized;
            }

            return sanitized;
        }

        public static string GetChapterDirectoryName(string chapterName, string chapterUrl)
        {
            var sanitized = SanitizePathSegment(chapterName);
            var hash = ShortHash(chapterUrl);
            var maxBaseLength = Math.Max(1, 120 - hash.Length - 1);
            if (sanitized.Length > maxBaseLength)
            {
                sanitized = sanitized[..maxBaseLength].TrimEnd('.', ' ');
            }

            return $"{sanitized}_{hash}";
        }

        private static IEnumerable<string> GetChapterDirectoryCandidates(string mangaDir, string chapterName, string chapterUrl)
        {
            yield return Path.Combine(mangaDir, GetChapterDirectoryName(chapterName, chapterUrl));
            yield return Path.Combine(mangaDir, SanitizePathSegment(chapterName));
        }

        private static bool IsCompletedChapterDirectory(string chapterDir)
        {
            return Directory.Exists(chapterDir) && GetReadableFiles(chapterDir).Count > 0;
        }

        private static bool IsReadablePageFile(string path)
        {
            var extension = Path.GetExtension(path);
            return !extension.Equals(".part", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(TempSuffix, StringComparison.OrdinalIgnoreCase)
                && ImageExtensions.Contains(extension);
        }

        private static string ShortHash(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                value = "missing-url";
            }

            var bytes = MD5.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(bytes).ToLowerInvariant()[..6];
        }
    }
}
