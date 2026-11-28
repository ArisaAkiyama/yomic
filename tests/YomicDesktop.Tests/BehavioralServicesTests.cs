using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using Xunit;
using Yomic.Core.Data;
using Yomic.Core.Models;
using Yomic.Core.Services;
using Yomic.Core.Sources;

namespace YomicDesktop.Tests;

public class BehavioralServicesTests
{
    private static string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"yomic_b17_{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupTempDir(string dir)
    {
        try { SqliteConnection.ClearAllPools(); } catch { }
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch { }
    }

    private static int GetFreeLoopbackPort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    [Fact]
    public async Task LibraryService_UpdateChaptersAsync_DeduplicatesPreservesReadStateAndGuardsEmptyPrune()
    {
        string tempDir = CreateTempDir("lib_ch");
        string dbPath = Path.Combine(tempDir, "test_library.db");
        try
        {
            using (var initCtx = new MangaDbContext(dbPath))
            {
                await initCtx.Database.EnsureCreatedAsync();
            }

            var library = new LibraryService(dbPath);
            var manga = new Manga
            {
                Source = 777001,
                Url = "https://example.test/manga/solo-test",
                Title = "Komik Solo Test",
                ThumbnailUrl = "https://example.test/cover.jpg"
            };

            await library.AddToLibraryAsync(manga);
            Assert.Equal("Solo Test", manga.Title);
            Assert.True(manga.Id > 0);

            // 1. Initial load: chapters added, IsNew must stay false
            int initialAdded = await library.UpdateChaptersAsync(
                manga,
                new List<Chapter>
                {
                    new() { Url = "/c1", Name = "Chapter 1", ChapterNumber = 1f, DateUpload = 1_700_000_001_000 },
                    new() { Url = "/c2", Name = "Chapter 2", ChapterNumber = 2f, DateUpload = 1_700_000_002_000 }
                },
                isInitialLoad: true);

            Assert.Equal(2, initialAdded);

            // Seed user progress on Ch 1 + an artificial duplicate row for Ch 2 in DB
            using (var ctx = new MangaDbContext(dbPath))
            {
                var dbCh1 = await ctx.Chapters.FirstAsync(c => c.Url == "/c1");
                Assert.False(dbCh1.IsNew);
                dbCh1.Read = true;
                dbCh1.Bookmark = true;
                dbCh1.IsDownloaded = true;

                ctx.Chapters.Add(new Chapter
                {
                    MangaId = manga.Id,
                    Url = "/c2",
                    Name = "Chapter 2 (Dup)",
                    ChapterNumber = 2f,
                    DateUpload = 1_700_000_002_000
                });
                await ctx.SaveChangesAsync();
            }

            // 2. Subsequent sync with duplicate incoming URLs + 2 new chapters
            int newlyAdded = await library.UpdateChaptersAsync(
                manga,
                new List<Chapter>
                {
                    new() { Url = "/c1", Name = "Chapter 1", ChapterNumber = 1f, DateUpload = 1_700_000_001_000 },
                    new() { Url = "/c1", Name = "Chapter 1 (Incoming Dup)", ChapterNumber = 1f, DateUpload = 1_700_000_001_000 },
                    new() { Url = "/c2", Name = "Chapter 2", ChapterNumber = 2f, DateUpload = 1_700_000_002_000 },
                    new() { Url = "/c3", Name = "Chapter 3", ChapterNumber = 3f, DateUpload = 1_700_000_003_000 },
                    new() { Url = "/c4", Name = "Chapter 4", ChapterNumber = 4f, DateUpload = 1_700_000_004_000 }
                },
                isInitialLoad: false);

            Assert.Equal(2, newlyAdded);

            using (var verifyCtx = new MangaDbContext(dbPath))
            {
                var dbManga = await verifyCtx.Mangas.Include(m => m.Chapters).FirstAsync(m => m.Id == manga.Id);
                Assert.True(dbManga.HasNewChapters);
                Assert.Equal(4, dbManga.Chapters.Count);
                Assert.Equal(4, dbManga.Chapters.Select(c => c.Url).Distinct().Count());

                var ch1 = dbManga.Chapters.Single(c => c.Url == "/c1");
                Assert.True(ch1.Read);
                Assert.True(ch1.Bookmark);
                Assert.True(ch1.IsDownloaded);

                var ch3 = dbManga.Chapters.Single(c => c.Url == "/c3");
                var ch4 = dbManga.Chapters.Single(c => c.Url == "/c4");
                Assert.True(ch3.IsNew);
                Assert.True(ch4.IsNew);
            }

            // 3. Empty chapter list (simulating Cloudflare/source outage) must NEVER prune existing chapters
            int emptyAdded = await library.UpdateChaptersAsync(manga, new List<Chapter>(), isInitialLoad: false, skipPrune: false);
            Assert.Equal(0, emptyAdded);

            using (var postEmptyCtx = new MangaDbContext(dbPath))
            {
                int remainingCount = await postEmptyCtx.Chapters.CountAsync(c => c.MangaId == manga.Id);
                Assert.Equal(4, remainingCount);
            }
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task LibraryService_UpdateHistoryAndDownloadStatus_PersistsWithoutDuplication()
    {
        string tempDir = CreateTempDir("lib_hist");
        string dbPath = Path.Combine(tempDir, "test_history.db");
        try
        {
            using (var initCtx = new MangaDbContext(dbPath))
            {
                await initCtx.Database.EnsureCreatedAsync();
            }

            var library = new LibraryService(dbPath);
            var firstVisit = new Manga
            {
                Source = 888002,
                Url = "https://example.test/manga/history-item",
                Title = "History Title v1",
                ThumbnailUrl = "https://example.test/cover-v1.jpg"
            };
            await library.UpdateHistoryAsync(firstVisit);

            var secondVisit = new Manga
            {
                Source = 888002,
                Url = "https://example.test/manga/history-item",
                Title = "History Title v2",
                ThumbnailUrl = "https://example.test/cover-v2.jpg"
            };
            await library.UpdateHistoryAsync(secondVisit);

            using (var ctx = new MangaDbContext(dbPath))
            {
                var rows = await ctx.Mangas.Where(m => m.Source == 888002).ToListAsync();
                Assert.Single(rows);
                Assert.False(rows[0].Favorite);
                Assert.True(rows[0].LastViewed > 0);
                Assert.Equal("History Title v2", rows[0].Title);
                Assert.Equal("https://example.test/cover-v2.jpg", rows[0].ThumbnailUrl);
            }

            // Verify UpdateChapterDownloadStatusByUrlAsync toggles IsDownloaded in isolated DB
            await library.UpdateChaptersAsync(
                secondVisit,
                new List<Chapter> { new() { Url = "https://example.test/manga/history-item/ch-1", Name = "Ch 1", ChapterNumber = 1f } },
                isInitialLoad: true);

            await library.UpdateChapterDownloadStatusByUrlAsync("https://example.test/manga/history-item/ch-1", true);
            using (var ctx = new MangaDbContext(dbPath))
            {
                var ch = await ctx.Chapters.SingleAsync(c => c.Url == "https://example.test/manga/history-item/ch-1");
                Assert.True(ch.IsDownloaded);
            }

            await library.UpdateChapterDownloadStatusByUrlAsync("https://example.test/manga/history-item/ch-1", false);
            using (var ctx = new MangaDbContext(dbPath))
            {
                var ch = await ctx.Chapters.SingleAsync(c => c.Url == "https://example.test/manga/history-item/ch-1");
                Assert.False(ch.IsDownloaded);
            }
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void DownloadService_QueuePauseCancelAndPersistence_RoundTripsIsolatedQueue()
    {
        string tempDir = CreateTempDir("dl_queue");
        string settingsPath = Path.Combine(tempDir, "settings.json");
        string dbPath = Path.Combine(tempDir, "dl_test.db");
        string dlDir = Path.Combine(tempDir, "downloads");
        try
        {
            using var settings = new SettingsService(settingsPath);
            using var network = new NetworkService(settings);
            var sourceManager = new SourceManager(deferExtensionLoad: true);
            var library = new LibraryService(dbPath);

            var manga = new Manga
            {
                Id = 9001,
                Source = 123456,
                Url = "https://example.test/manga/dl-test",
                Title = $"IsolatedDlTest_{Guid.NewGuid():N}"
            };
            var ch1 = new Chapter { Id = 1, MangaId = 9001, Url = "https://example.test/c1", Name = "Chapter 1", ChapterNumber = 1f };
            var ch2 = new Chapter { Id = 2, MangaId = 9001, Url = "https://example.test/c2", Name = "Chapter 2", ChapterNumber = 2f };

            using (var dl1 = new DownloadService(sourceManager, library, network, settings, dlDir, loadQueue: false))
            {
                // Pause first so QueueDownload never starts background network tasks
                dl1.Pause();
                dl1.QueueDownload(manga, ch1);
                dl1.QueueDownload(manga, ch1); // Duplicate must be ignored
                dl1.QueueDownload(manga, ch2);

                var queued = dl1.AllDownloads.ToList();
                Assert.Equal(2, queued.Count);

                var req2 = queued.Single(r => r.Chapter.Url == ch2.Url);
                dl1.Cancel(req2);

                Assert.Single(dl1.AllDownloads);
                dl1.SaveQueueSynchronously();
            }

            string queueFile = Path.Combine(dlDir, "queue_v2.json");
            Assert.True(File.Exists(queueFile), "Expected isolated queue_v2.json inside customBaseDir");

            using (var dl2 = new DownloadService(sourceManager, library, network, settings, dlDir, loadQueue: true))
            {
                var reloaded = dl2.AllDownloads.ToList();
                Assert.Single(reloaded);
                Assert.Equal(ch1.Url, reloaded[0].Chapter.Url);
                Assert.Equal(manga.Title, reloaded[0].Manga.Title);
                Assert.Equal("Queued", reloaded[0].Status);
            }
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task NetworkService_OptimizedHttpClient_BlocksLoopbackHttpListenerBeforeConnecting()
    {
        string tempDir = CreateTempDir("ssrf_net");
        string settingsPath = Path.Combine(tempDir, "settings.json");
        int port = GetFreeLoopbackPort();
        int listenerHits = 0;

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var acceptTask = Task.Run(async () =>
        {
            try
            {
                while (listener.IsListening)
                {
                    var ctx = await listener.GetContextAsync();
                    Interlocked.Increment(ref listenerHits);
                    byte[] body = Encoding.UTF8.GetBytes("TOP_SECRET_LOOPBACK_RESPONSE");
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                    ctx.Response.Close();
                }
            }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        });

        try
        {
            using var settings = new SettingsService(settingsPath);
            settings.DnsOverHttpsProvider = 0; // System DNS so 127.0.0.1 is resolved directly
            settings.IsOfflineMode = false;
            using var network = new NetworkService(settings);
            using var client = network.CreateOptimizedHttpClient(useCookies: false, allowAutoRedirect: false);
            client.Timeout = TimeSpan.FromSeconds(5);

            await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
                client.GetAsync($"http://127.0.0.1:{port}/secret"));

            Assert.Equal(0, Volatile.Read(ref listenerHits));
        }
        finally
        {
            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
            try { await acceptTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task JsMangaSource_FetchUrl_BlocksLoopbackHttpListenerWith403()
    {
        string tempDir = CreateTempDir("ssrf_js");
        int port = GetFreeLoopbackPort();
        int listenerHits = 0;

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var acceptTask = Task.Run(async () =>
        {
            try
            {
                while (listener.IsListening)
                {
                    var ctx = await listener.GetContextAsync();
                    Interlocked.Increment(ref listenerHits);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Close();
                }
            }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        });

        try
        {
            string pluginPath = Path.Combine(tempDir, "ssrf_probe.js");
            await File.WriteAllTextAsync(pluginPath, $@"
var source = {{
    name: 'SsrfLoopbackProbe',
    baseUrl: 'https://example.com',
    language: 'en',
    version: '1.0.0',
    getPopularManga: function() {{
        var res = fetch('http://127.0.0.1:{port}/secret');
        return {{ items: [{{ title: String(res.status), url: '/probe' }}], totalPages: 1 }};
    }},
    getSearchManga: function() {{ return {{ items: [], totalPages: 1 }}; }},
    getMangaDetails: function(u) {{ return {{ title: 'T', url: u }}; }},
    getChapterList: function() {{ return []; }},
    getPageList: function() {{ return []; }}
}};", Encoding.UTF8);

            using var jsSource = new JsMangaSource(pluginPath);
            var mangas = await jsSource.GetPopularMangaAsync(1);

            Assert.Single(mangas);
            Assert.Equal("403", mangas[0].Title);
            Assert.Equal(0, Volatile.Read(ref listenerHits));
        }
        finally
        {
            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
            try { await acceptTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void ImageProcessingService_ProcessPage_DownscalesCropsWebtoonStripsAndHandlesCorruptInput()
    {
        var svc = new ImageProcessingService();

        // 1. Standard 300x300 page with 50px solid white margins around a dark 200x200 panel
        byte[] standardPng;
        using (var bmp = new SKBitmap(300, 300))
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Navy, Style = SKPaintStyle.Fill };
            canvas.DrawRect(new SKRect(50, 50, 250, 250), paint);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            standardPng = data.ToArray();
        }

        byte[] processed = svc.ProcessPage(standardPng, new ImageProcessingOptions
        {
            EnableOptimization = true,
            AutoCropBorders = true,
            MaxDimension = 120,
            OutputFormat = SKEncodedImageFormat.Webp
        });

        Assert.NotEmpty(processed);
        using (var decoded = SKBitmap.Decode(processed))
        {
            Assert.NotNull(decoded);
            Assert.InRange(decoded.Width, 1, 120);
            Assert.InRange(decoded.Height, 1, 120);
        }

        // 2. Tall vertical webtoon strip (240x960, aspect 4.0 > 2.5): width capped to 120, height proportional (~480), NOT crushed to 120!
        byte[] stripPng;
        using (var bmp = new SKBitmap(240, 960))
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.DarkSlateGray);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            stripPng = data.ToArray();
        }

        byte[] processedStrip = svc.ProcessPage(stripPng, new ImageProcessingOptions
        {
            EnableOptimization = true,
            AutoCropBorders = false,
            MaxDimension = 120,
            OutputFormat = SKEncodedImageFormat.Webp
        });

        using (var decodedStrip = SKBitmap.Decode(processedStrip))
        {
            Assert.NotNull(decodedStrip);
            Assert.Equal(120, decodedStrip.Width);
            Assert.True(decodedStrip.Height >= 450, $"Expected tall webtoon strip height to remain proportional (~480), got {decodedStrip.Height}");
        }

        // 3. Empty and corrupt bytes must fail gracefully without throwing
        Assert.Empty(svc.ProcessPage(Array.Empty<byte>()));
        byte[] corrupt = Encoding.UTF8.GetBytes("<html>not an image</html>");
        Assert.Equal(corrupt, svc.ProcessPage(corrupt));
    }

    [Fact]
    public void SourceManager_BulkLoadExtensions_BatchesOnSourcesChangedToSingleEvent()
    {
        string tempDir = CreateTempDir("sm_batch");
        string localDir = Path.Combine(tempDir, "local");
        string userDir = Path.Combine(tempDir, "user");
        Directory.CreateDirectory(localDir);
        Directory.CreateDirectory(userDir);
        try
        {
            for (int i = 1; i <= 4; i++)
            {
                File.WriteAllText(
                    Path.Combine(userDir, $"mock_{i}.js"),
                    $@"var source = {{
                        name: 'MockSource{i}',
                        baseUrl: 'https://mock{i}.example.test',
                        language: 'en',
                        version: '1.0.0',
                        getPopularManga: function() {{ return {{ items: [], totalPages: 1 }}; }},
                        getSearchManga: function() {{ return {{ items: [], totalPages: 1 }}; }},
                        getMangaDetails: function(u) {{ return {{ title: 'T', url: u }}; }},
                        getChapterList: function() {{ return []; }},
                        getPageList: function() {{ return []; }}
                    }};",
                    Encoding.UTF8);
            }

            using var sm = new SourceManager(localDir, userDir, deferExtensionLoad: false);
            Assert.Equal(4, sm.GetSources().Count);

            int changeEvents = 0;
            sm.OnSourcesChanged += () => Interlocked.Increment(ref changeEvents);

            // Bulk reload of 4 plugins must emit OnSourcesChanged once, not 4 times
            sm.LoadPlugins();
            Assert.Equal(1, Volatile.Read(ref changeEvents));

            // Direct single-source load outside bulk mode still fires OnSourcesChanged immediately
            sm.LoadJsExtension(Path.Combine(userDir, "mock_1.js"));
            Assert.Equal(2, Volatile.Read(ref changeEvents));
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task LibraryService_CustomDbPath_IsolatesCrudCategoriesAndClear()
    {
        string tempDir = CreateTempDir("lib_iso");
        string dbPath = Path.Combine(tempDir, "isolated.db");
        try
        {
            using (var initCtx = new MangaDbContext(dbPath))
            {
                await initCtx.Database.EnsureCreatedAsync();
            }

            var library = new LibraryService(dbPath);
            var cat = new Category { Name = "Favorites", Color = "#2563EB", IsDefault = true };
            await library.AddCategoryAsync(cat);
            var cats = await library.GetCategoriesAsync();
            Assert.Single(cats);
            Assert.Equal("Favorites", cats[0].Name);

            var manga = new Manga { Source = 5551, Url = "https://example.test/m/1", Title = "Isolated One" };
            await library.AddToLibraryAsync(manga);
            await library.UpdateChaptersAsync(
                manga,
                new List<Chapter> { new() { Url = "/c1", Name = "Ch 1", ChapterNumber = 1f } },
                isInitialLoad: true);

            Assert.Equal(1, await library.GetLibraryCountAsync());
            var libMangas = await library.GetLibraryMangaAsync();
            Assert.Single(libMangas);
            Assert.NotNull(await library.GetMangaAsync(manga.Id));
            Assert.NotNull(await library.GetMangaByUrlAsync(manga.Url, manga.Source));

            await library.MarkChaptersAsReadAsync(manga);
            var readCheck = await library.GetMangaAsync(manga.Id);
            Assert.True(readCheck!.Chapters.Single().Read);

            await library.SaveLastPageReadAsync(readCheck.Chapters.Single().Url, 31, manga.Id, 1f);
            await library.MarkChaptersAsUnreadAsync(manga);
            var unreadCheck = await library.GetMangaAsync(manga.Id);
            Assert.False(unreadCheck!.Chapters.Single().Read);
            Assert.Equal(0, unreadCheck.Chapters.Single().LastPageRead);
            Assert.Equal(0, unreadCheck.LastViewed);

            // Also verify single-chapter unread resets partial page progress (e.g. Hal. 31/33 when Read is still false)
            await library.SaveLastPageReadAsync(unreadCheck.Chapters.Single().Url, 31, manga.Id, 1f);
            Assert.Equal(31L, await library.GetLastPageReadRawAsync(unreadCheck.Chapters.Single().Url, manga.Id, 1f));
            await library.SetChapterReadStatusAsync(
                unreadCheck.Chapters.Single().Url,
                isRead: false,
                sourceId: manga.Source,
                mangaUrl: manga.Url,
                chapterTitle: "Ch 1",
                chapterNumber: 1f);
            var singleResetCheck = await library.GetMangaAsync(manga.Id);
            Assert.False(singleResetCheck!.Chapters.Single().Read);
            Assert.Equal(0, singleResetCheck.Chapters.Single().LastPageRead);
            Assert.Equal(0, singleResetCheck.LastViewed);

            await library.RemoveFromLibraryAsync(manga, deleteFiles: false);
            Assert.Equal(0, await library.GetLibraryCountAsync());

            await library.ClearDatabaseAsync();
            Assert.Empty(await library.GetCategoriesAsync());
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task LibraryService_ScopedChapterStatusUpdates_DoNotCollideOnIdenticalRelativeUrls()
    {
        string tempDir = CreateTempDir("lib_scope");
        string dbPath = Path.Combine(tempDir, "scoped.db");
        try
        {
            using (var initCtx = new MangaDbContext(dbPath))
            {
                await initCtx.Database.EnsureCreatedAsync();
            }

            var library = new LibraryService(dbPath);
            var mangaA = new Manga { Source = 1001, Url = "/manga/alpha", Title = "Alpha" };
            var mangaB = new Manga { Source = 2002, Url = "/manga/beta", Title = "Beta" };
            await library.AddToLibraryAsync(mangaA);
            await library.AddToLibraryAsync(mangaB);

            // Only Manga A has /chapter-1 initially
            await library.UpdateChaptersAsync(
                mangaA,
                new List<Chapter> { new() { Url = "/chapter-1", Name = "Ch 1", ChapterNumber = 1f } },
                isInitialLoad: true);

            // Marking /chapter-1 read for Manga B must create Manga B's own chapter and NEVER mutate Manga A!
            await library.SetChapterReadStatusAsync(
                "/chapter-1",
                isRead: true,
                sourceId: mangaB.Source,
                mangaUrl: mangaB.Url,
                chapterTitle: "Beta Ch 1",
                chapterNumber: 1f);

            await library.SetChapterBookmarkStatusAsync(
                "/chapter-1",
                isBookmarked: true,
                sourceId: mangaB.Source,
                mangaUrl: mangaB.Url,
                chapterTitle: "Beta Ch 1",
                chapterNumber: 1f);

            await library.UpdateChapterDownloadStatusByUrlAsync("/chapter-1", true, mangaId: mangaB.Id);

            var reloadedA = await library.GetMangaAsync(mangaA.Id);
            var reloadedB = await library.GetMangaAsync(mangaB.Id);

            var chA = reloadedA!.Chapters.Single(c => c.Url == "/chapter-1");
            var chB = reloadedB!.Chapters.Single(c => c.Url == "/chapter-1");

            Assert.False(chA.Read);
            Assert.False(chA.Bookmark);
            Assert.False(chA.IsDownloaded);

            Assert.True(chB.Read);
            Assert.True(chB.Bookmark);
            Assert.True(chB.IsDownloaded);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void SourceManager_RemoveOldSourceIdAfterUpgradeAtSamePath_PreservesNewPluginFileOnDisk()
    {
        string tempDir = CreateTempDir("sm_upgrade_id");
        string localDir = Path.Combine(tempDir, "local");
        string userDir = Path.Combine(tempDir, "user");
        Directory.CreateDirectory(localDir);
        Directory.CreateDirectory(userDir);
        try
        {
            string pluginPath = Path.Combine(userDir, "upgrademe.js");
            File.WriteAllText(pluginPath, @"
var source = {
    name: 'UpgradeTargetV1',
    baseUrl: 'https://upgrade.example.test',
    language: 'en',
    version: '1.0.0',
    getPopularManga: function() { return { items: [], totalPages: 1 }; },
    getSearchManga: function() { return { items: [], totalPages: 1 }; },
    getMangaDetails: function(u) { return { title: 'T', url: u }; },
    getChapterList: function() { return []; },
    getPageList: function() { return []; }
};", Encoding.UTF8);

            using var sm = new SourceManager(localDir, userDir, deferExtensionLoad: false);
            var oldSrc = Assert.Single(sm.GetSources());
            long oldId = oldSrc.Id;

            // Simulate upgrade that changes source.name (and therefore derived Source.Id) at the exact same filename
            File.WriteAllText(pluginPath, @"
var source = {
    name: 'UpgradeTargetV2Renamed',
    baseUrl: 'https://upgrade.example.test',
    language: 'en',
    version: '2.0.0',
    getPopularManga: function() { return { items: [], totalPages: 1 }; },
    getSearchManga: function() { return { items: [], totalPages: 1 }; },
    getMangaDetails: function(u) { return { title: 'T', url: u }; },
    getChapterList: function() { return []; },
    getPageList: function() { return []; }
};", Encoding.UTF8);

            var newSrc = sm.InstallPlugin(pluginPath);
            Assert.NotNull(newSrc);
            Assert.NotEqual(oldId, newSrc!.Id);

            // Removing stale old ID must NOT delete the newly upgraded plugin file on disk!
            sm.RemoveSource(oldId);

            Assert.True(File.Exists(pluginPath), "Newly upgraded plugin file was erroneously deleted by RemoveSource(oldId)!");
            Assert.Null(sm.GetSource(oldId));
            Assert.NotNull(sm.GetSource(newSrc.Id));
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void UrlSecurityHelper_IsValidImagePayloadAndIsHostOrSubdomain_EnforceAllowlist()
    {
        // Valid image magic headers
        Assert.True(Yomic.Core.Helpers.UrlSecurityHelper.IsValidImagePayload(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 })); // JPEG
        Assert.True(Yomic.Core.Helpers.UrlSecurityHelper.IsValidImagePayload(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })); // PNG
        Assert.True(Yomic.Core.Helpers.UrlSecurityHelper.IsValidImagePayload(Encoding.ASCII.GetBytes("GIF89a0000"))); // GIF
        Assert.True(Yomic.Core.Helpers.UrlSecurityHelper.IsValidImagePayload(Encoding.ASCII.GetBytes("RIFF1234WEBP"))); // WebP
        Assert.True(Yomic.Core.Helpers.UrlSecurityHelper.IsValidImagePayload(new byte[] { 0x00, 0x00, 0x00, 0x1C, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'a', (byte)'v', (byte)'i', (byte)'f' })); // AVIF

        // Non-image / error / truncated / corrupt payloads must be rejected
        Assert.False(Yomic.Core.Helpers.UrlSecurityHelper.IsValidImagePayload(Array.Empty<byte>()));
        Assert.False(Yomic.Core.Helpers.UrlSecurityHelper.IsValidImagePayload(Encoding.UTF8.GetBytes("OK")));
        Assert.False(Yomic.Core.Helpers.UrlSecurityHelper.IsValidImagePayload(Encoding.UTF8.GetBytes("{\"error\":\"rate limited\"}")));
        Assert.False(Yomic.Core.Helpers.UrlSecurityHelper.IsValidImagePayload(Encoding.UTF8.GetBytes("403 Forbidden")));
        Assert.False(Yomic.Core.Helpers.UrlSecurityHelper.IsValidImagePayload(Encoding.UTF8.GetBytes("<!DOCTYPE html><html>Cloudflare</html>")));
        Assert.False(Yomic.Core.Helpers.UrlSecurityHelper.IsValidImagePayload(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x11, 0x22, 0x33, 0x44 }));

        // Strict host/subdomain check prevents path-substring Origin spoofing
        Assert.True(Yomic.Core.Helpers.UrlSecurityHelper.IsHostOrSubdomain("https://img.mangamillion.shueisha.co.jp/a.webp", "mangamillion.shueisha.co.jp"));
        Assert.False(Yomic.Core.Helpers.UrlSecurityHelper.IsHostOrSubdomain("https://evil.example/mangamillion/a.webp", "mangamillion.shueisha.co.jp"));
    }

    [Fact]
    public void CloudflareBypassService_MergeCookiesForHost_ReplacesParentDomainDuplicatesWithoutAccumulation()
    {
        var cf = new CloudflareBypassService();
        cf.MergeCookiesForHost("other.example", new[]
        {
            new PuppeteerSharp.CookieParam { Name = "session", Value = "keep_me", Domain = ".other.example" }
        });
        cf.MergeCookiesForHost("www.manga.example", new[]
        {
            new PuppeteerSharp.CookieParam { Name = "cf_clearance", Value = "old_token", Domain = ".manga.example" }
        });
        cf.MergeCookiesForHost("www.manga.example", new[]
        {
            new PuppeteerSharp.CookieParam { Name = "cf_clearance", Value = "fresh_token", Domain = ".manga.example" }
        });

        var mangaCookies = cf.SavedCookies.Where(c => c.Name == "cf_clearance").ToList();
        Assert.Single(mangaCookies);
        Assert.Equal("fresh_token", mangaCookies[0].Value);
        Assert.Contains(cf.SavedCookies, c => c.Name == "session" && c.Value == "keep_me");
    }

    [Fact]
    public async Task UpdateService_CheckForUpdatesAsync_SkipsNetworkWhenOfflineModeEnabled()
    {
        var prevProvider = HttpSource.IsOfflineModeProvider;
        try
        {
            HttpSource.IsOfflineModeProvider = () => true;
            using var updater = new UpdateService();
            var info = await updater.CheckForUpdatesAsync();
            Assert.False(info.IsUpdateAvailable);
        }
        finally
        {
            HttpSource.IsOfflineModeProvider = prevProvider;
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<(Uri Uri, bool HasAuth, bool HasCookie, string? Origin)> ObservedRequests { get; } = new();

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ObservedRequests.Add((
                request.RequestUri!,
                request.Headers.Contains("Authorization"),
                request.Headers.Contains("Cookie"),
                request.Headers.TryGetValues("Origin", out var origins) ? origins.FirstOrDefault() : null));
            return Task.FromResult(_responder(request));
        }
    }

    [Fact]
    public async Task HttpSource_SendWithSafeRedirectsCoreAsync_BlocksLoopbackRedirectAndFlagsCrossOriginHops()
    {
        // 1. 302 redirect to loopback SSRF must be blocked before second request is sent
        var ssrfHandler = new StubHttpMessageHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("http://127.0.0.1:8080/internal-admin");
            return r;
        });
        using var ssrfClient = new HttpClient(ssrfHandler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            HttpSource.SendWithSafeRedirectsCoreAsync(
                ssrfClient,
                new Uri("https://manga.example/chapter-1"),
                HttpMethod.Get,
                (uri, method, _) => new HttpRequestMessage(method, uri)));

        Assert.Single(ssrfHandler.ObservedRequests);

        // 2. Cross-origin 302 sets isCrossOrigin=true so callers strip Cookie/Authorization headers
        var crossOriginHandler = new StubHttpMessageHandler(req =>
        {
            if (req.RequestUri!.Host.Equals("manga.example", StringComparison.OrdinalIgnoreCase))
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("https://cdn.other.example/page-1.jpg");
                return r;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }) };
        });
        using var crossClient = new HttpClient(crossOriginHandler);

        using var resp = await HttpSource.SendWithSafeRedirectsCoreAsync(
            crossClient,
            new Uri("https://manga.example/page-1"),
            HttpMethod.Get,
            (uri, method, isCrossOrigin) =>
            {
                var req = new HttpRequestMessage(method, uri);
                if (!isCrossOrigin)
                {
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer secret");
                    req.Headers.TryAddWithoutValidation("Cookie", "session=secret");
                }
                return req;
            });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, crossOriginHandler.ObservedRequests.Count);
        Assert.True(crossOriginHandler.ObservedRequests[0].HasAuth);
        Assert.True(crossOriginHandler.ObservedRequests[0].HasCookie);
        Assert.False(crossOriginHandler.ObservedRequests[1].HasAuth);
        Assert.False(crossOriginHandler.ObservedRequests[1].HasCookie);
    }

    [Fact]
    public async Task DownloadService_SendPageRequestWithSafeRedirectsAsync_BlocksMetadataRedirectAndScopesOriginHeader()
    {
        // 1. 302 redirect to AWS/cloud metadata IP must throw before connecting
        var metaHandler = new StubHttpMessageHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("http://169.254.169.254/latest/meta-data/");
            return r;
        });
        using var metaClient = new HttpClient(metaHandler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            DownloadService.SendPageRequestWithSafeRedirectsAsync(
                metaClient,
                "https://cdn.example/p1.jpg",
                new Dictionary<string, string>(),
                "https://manga.example/",
                CancellationToken.None));

        Assert.Single(metaHandler.ObservedRequests);

        // 2. MangaMillion Origin header is injected ONLY for real mangamillion.shueisha.co.jp hosts, never path substrings
        var originHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var originClient = new HttpClient(originHandler);

        using (await DownloadService.SendPageRequestWithSafeRedirectsAsync(
            originClient,
            "https://img.mangamillion.shueisha.co.jp/p1.webp",
            new Dictionary<string, string>(),
            "https://mangamillion.shueisha.co.jp/",
            CancellationToken.None)) { }

        using (await DownloadService.SendPageRequestWithSafeRedirectsAsync(
            originClient,
            "https://evil.example/mangamillion/p1.webp",
            new Dictionary<string, string>(),
            "https://evil.example/",
            CancellationToken.None)) { }

        Assert.Equal(2, originHandler.ObservedRequests.Count);
        Assert.Equal("https://mangamillion.shueisha.co.jp", originHandler.ObservedRequests[0].Origin);
        Assert.Null(originHandler.ObservedRequests[1].Origin);
    }

    [Fact]
    public async Task UrlSecurityHelper_SplitHorizonDns_BlocksWhenSystemDnsReturnsPrivateEvenIfDoHReturnsPublic()
    {
        var prevSys = Yomic.Core.Helpers.UrlSecurityHelper.SystemDnsResolverOverride;
        var prevDoH = Yomic.Core.Helpers.UrlSecurityHelper.DoHResolverOverride;
        var prevOffline = HttpSource.IsOfflineModeProvider;
        var prevDohProv = HttpSource.DnsOverHttpsProviderGetter;
        try
        {
            HttpSource.IsOfflineModeProvider = () => false;
            HttpSource.DnsOverHttpsProviderGetter = () => 2;
            Yomic.Core.Helpers.UrlSecurityHelper.ClearResolvedHostSafetyCache();

            // 1. Split-horizon attack: DoH sees public IP, System DNS (used by Chromium/WebView2) resolves loopback -> MUST BLOCK
            Yomic.Core.Helpers.UrlSecurityHelper.DoHResolverOverride = (_, _, _) =>
                Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });
            Yomic.Core.Helpers.UrlSecurityHelper.SystemDnsResolverOverride = (_, _) =>
                Task.FromResult(new[] { IPAddress.Loopback });

            bool splitHorizonSafe = await Yomic.Core.Helpers.UrlSecurityHelper.IsSafeResolvedPublicUrlAsync(
                "https://split-horizon.example/ch-1",
                bypassCache: true);
            Assert.False(splitHorizonSafe);

            // 2. Both resolvers return public IP -> allowed
            Yomic.Core.Helpers.UrlSecurityHelper.SystemDnsResolverOverride = (_, _) =>
                Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });

            bool allPublicSafe = await Yomic.Core.Helpers.UrlSecurityHelper.IsSafeResolvedPublicUrlAsync(
                "https://public-only.example/ch-1",
                bypassCache: true);
            Assert.True(allPublicSafe);

            // 3. Offline Mode must fail closed
            HttpSource.IsOfflineModeProvider = () => true;
            bool offlineSafe = await Yomic.Core.Helpers.UrlSecurityHelper.IsSafeResolvedPublicUrlAsync(
                "https://public-only.example/ch-1",
                bypassCache: true);
            Assert.False(offlineSafe);
        }
        finally
        {
            Yomic.Core.Helpers.UrlSecurityHelper.SystemDnsResolverOverride = prevSys;
            Yomic.Core.Helpers.UrlSecurityHelper.DoHResolverOverride = prevDoH;
            HttpSource.IsOfflineModeProvider = prevOffline;
            HttpSource.DnsOverHttpsProviderGetter = prevDohProv;
            Yomic.Core.Helpers.UrlSecurityHelper.ClearResolvedHostSafetyCache();
        }
    }

    [Fact]
    public async Task SourceManager_LoadJsExtension_RegistersPathBeforeFiringOnSourcesChanged()
    {
        string tempDir = CreateTempDir("sm_path_order");
        string localDir = Path.Combine(tempDir, "local");
        string userDir = Path.Combine(tempDir, "user");
        try
        {
            Directory.CreateDirectory(localDir);
            Directory.CreateDirectory(userDir);
            string pluginPath = Path.Combine(userDir, "ordercheck.js");
            await File.WriteAllTextAsync(pluginPath, @"
var source = {
    name: 'OrderCheckExt',
    baseUrl: 'https://ordercheck.example',
    language: 'en',
    version: '1.0.0',
    getPopularManga: function() { return { items: [], totalPages: 1 }; },
    getSearchManga: function() { return { items: [], totalPages: 1 }; },
    getMangaDetails: function(u) { return { title: 'T', url: u }; },
    getChapterList: function() { return []; },
    getPageList: function() { return []; }
};", Encoding.UTF8);

            using var sm = new SourceManager(localDir, userDir, deferExtensionLoad: false);
            sm.RemoveSource(sm.GetSources().First().Id);

            string? pathObservedInEvent = null;
            bool installedObservedInEvent = false;
            int eventCount = 0;

            sm.OnSourcesChanged += () =>
            {
                eventCount++;
                var src = sm.GetSources().FirstOrDefault(s => s.Name.Contains("OrderCheckExt", StringComparison.OrdinalIgnoreCase));
                if (src != null)
                {
                    pathObservedInEvent = sm.GetSourcePath(src.Id);
                    installedObservedInEvent = sm.IsInstalledSource(src.Id);
                }
            };

            // Re-write file (RemoveSource deleted it) and load directly
            await File.WriteAllTextAsync(pluginPath, @"
var source = {
    name: 'OrderCheckExt',
    baseUrl: 'https://ordercheck.example',
    language: 'en',
    version: '1.0.0',
    getPopularManga: function() { return { items: [], totalPages: 1 }; },
    getSearchManga: function() { return { items: [], totalPages: 1 }; },
    getMangaDetails: function(u) { return { title: 'T', url: u }; },
    getChapterList: function() { return []; },
    getPageList: function() { return []; }
};", Encoding.UTF8);

            var loaded = sm.LoadJsExtension(pluginPath);
            Assert.NotNull(loaded);
            Assert.Equal(1, eventCount);
            Assert.Equal(pluginPath, pathObservedInEvent);
            Assert.True(installedObservedInEvent);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task MangaDbContext_EnterExclusiveMaintenanceAsync_BlocksNewReadersUntilLeaseDisposed()
    {
        string tempDir = CreateTempDir("db_maint_barrier");
        string dbPath = Path.Combine(tempDir, "barrier.db");
        try
        {
            using (var init = new MangaDbContext(dbPath))
            {
                await init.Database.EnsureCreatedAsync();
            }

            Task<int> readerTask;
            using (await MangaDbContext.EnterExclusiveMaintenanceAsync(dbPath))
            {
                readerTask = Task.Run(async () =>
                {
                    using var db = new MangaDbContext(dbPath);
                    return await db.Mangas.CountAsync();
                });

                // Reader MUST remain blocked while exclusive maintenance lease is active
                await Task.Delay(120);
                Assert.False(readerTask.IsCompleted);
            }

            // Once maintenance lease is released, reader unblocks and succeeds
            int count = await readerTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, count);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task SourceIdMigrationService_WritesDoneMarkerEvenWhenOrphanedMangaBelongsToUninstalledExtension()
    {
        string tempDir = CreateTempDir("mig_uninstalled");
        string dbPath = Path.Combine(tempDir, "mig.db");
        string flagPath = Path.Combine(tempDir, "migration.done");
        string localDir = Path.Combine(tempDir, "local");
        string userDir = Path.Combine(tempDir, "user");
        try
        {
            Directory.CreateDirectory(localDir);
            Directory.CreateDirectory(userDir);
            await File.WriteAllTextAsync(Path.Combine(userDir, "activeext.js"), @"
var source = {
    name: 'ActiveExt',
    baseUrl: 'https://active.example',
    language: 'en',
    version: '1.0.0',
    getPopularManga: function() { return { items: [], totalPages: 1 }; },
    getSearchManga: function() { return { items: [], totalPages: 1 }; },
    getMangaDetails: function(u) { return { title: 'T', url: u }; },
    getChapterList: function() { return []; },
    getPageList: function() { return []; }
};", Encoding.UTF8);

            using (var db = new MangaDbContext(dbPath))
            {
                await db.Database.EnsureCreatedAsync();
                db.Mangas.Add(new Manga
                {
                    Source = 999888777L,
                    Url = "https://uninstalled-site.invalid/manga/ghost",
                    Title = "Uninstalled Extension Manga",
                    Favorite = true
                });
                await db.SaveChangesAsync();
            }

            using var sm = new SourceManager(localDir, userDir, deferExtensionLoad: false);
            await SourceIdMigrationService.RunIfNeededAsync(sm, dbPath, flagPath);

            Assert.True(File.Exists(flagPath), "One-time migration flag must be written even when orphaned rows belong to uninstalled extensions.");
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task ExtensionRepositoryService_UninstallExtension_WorksWithoutPriorCatalogFetchAndRejectsUnknown()
    {
        string tempDir = CreateTempDir("ext_uninstall");
        string localDir = Path.Combine(tempDir, "local");
        string userDir = Path.Combine(tempDir, "user");
        string settingsPath = Path.Combine(tempDir, "settings.json");
        try
        {
            Directory.CreateDirectory(localDir);
            Directory.CreateDirectory(userDir);
            string pluginPath = Path.Combine(userDir, "demouninstall.js");
            await File.WriteAllTextAsync(pluginPath, @"
var source = {
    name: 'DemoUninstall',
    baseUrl: 'https://demo.example',
    language: 'id',
    version: '1.0.0',
    getPopularManga: function() { return { items: [], totalPages: 1 }; },
    getSearchManga: function() { return { items: [], totalPages: 1 }; },
    getMangaDetails: function(u) { return { title: 'T', url: u }; },
    getChapterList: function() { return []; },
    getPageList: function() { return []; }
};", Encoding.UTF8);

            using var settings = new SettingsService(settingsPath);
            using var network = new NetworkService(settings);
            using var sm = new SourceManager(localDir, userDir, deferExtensionLoad: false);
            var status = new SourceStatusService(sm, settings, network);
            using var repo = new ExtensionRepositoryService(sm, settings, status);

            // Uninstall by package ID WITHOUT ever fetching/populating _cachedRepoEntries
            var (ok, _) = repo.UninstallExtension("yomic.id.demouninstall");
            Assert.True(ok);
            Assert.Empty(sm.GetSources());
            Assert.False(File.Exists(pluginPath));

            // Unknown / already-removed extension must return false (never silent no-op success)
            var (okMissing, _) = repo.UninstallExtension("yomic.id.doesnotexist");
            Assert.False(okMissing);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task LibraryService_ClearAllHistoryAndProgressAsync_AtomicallyClearsReadLastPageLastViewedAndHistory()
    {
        string tempDir = CreateTempDir("clear_hist_atomic");
        string dbPath = Path.Combine(tempDir, "hist_atomic.db");
        try
        {
            using (var init = new MangaDbContext(dbPath))
            {
                await init.Database.EnsureCreatedAsync();
            }

            var library = new LibraryService(dbPath);
            var manga = new Manga
            {
                Source = 424242,
                Url = "https://example.test/manga/atomic-hist",
                Title = "Atomic History Manga"
            };
            await library.AddToLibraryAsync(manga);
            await library.UpdateChaptersAsync(
                manga,
                new List<Chapter> { new() { Url = "/c1", Name = "Ch 1", ChapterNumber = 1f } },
                isInitialLoad: true);

            using (var seed = new MangaDbContext(dbPath))
            {
                var dbManga = await seed.Mangas.Include(m => m.Chapters).SingleAsync();
                dbManga.LastViewed = 1_700_000_000_000;
                var ch = dbManga.Chapters.Single();
                ch.Read = true;
                ch.LastPageRead = 14;
                seed.History.Add(new History
                {
                    MangaId = dbManga.Id,
                    ChapterId = ch.Id,
                    LastRead = 1_700_000_000_000,
                    TimeRead = 120_000
                });
                await seed.SaveChangesAsync();
            }

            await library.ClearAllHistoryAndProgressAsync();

            using (var verify = new MangaDbContext(dbPath))
            {
                var dbManga = await verify.Mangas.Include(m => m.Chapters).SingleAsync();
                Assert.Equal(0, dbManga.LastViewed);
                var ch = dbManga.Chapters.Single();
                Assert.False(ch.Read);
                Assert.Equal(0, ch.LastPageRead);
                Assert.Empty(await verify.History.ToListAsync());
            }
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task MangaDbContext_EnsureMangaColumnsExist_RespectsExclusiveMaintenanceBarrier()
    {
        string tempDir = CreateTempDir("schema_barrier");
        string dbPath = Path.Combine(tempDir, "schema.db");
        try
        {
            using (var init = new MangaDbContext(dbPath))
            {
                await init.Database.EnsureCreatedAsync();
            }

            MangaDbContext.ResetColumnCheck();
            Task schemaTask;
            using (await MangaDbContext.EnterExclusiveMaintenanceAsync(dbPath))
            {
                schemaTask = Task.Run(() =>
                {
                    using var ctx = new MangaDbContext(dbPath);
                    MangaDbContext.EnsureMangaColumnsExist(ctx, forceRecheck: true);
                });

                await Task.Delay(120);
                Assert.False(schemaTask.IsCompleted, "EnsureMangaColumnsExist must not bypass the exclusive maintenance barrier.");
            }

            await schemaTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task LibraryService_ClearDatabaseAsync_RecreatesEmptyDbCleanlyWithExclusiveBarrier()
    {
        string tempDir = CreateTempDir("clear_db_barrier");
        string dbPath = Path.Combine(tempDir, "clear.db");
        try
        {
            using (var init = new MangaDbContext(dbPath))
            {
                await init.Database.EnsureCreatedAsync();
            }

            var library = new LibraryService(dbPath);
            await library.AddToLibraryAsync(new Manga { Source = 111, Url = "https://example.test/m1", Title = "Before Clear" });

            await library.ClearDatabaseAsync();

            using var verify = new MangaDbContext(dbPath);
            Assert.Empty(await verify.Mangas.ToListAsync());
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task ExtensionRepositoryService_UninstallExtension_HandlesDotJsFilenameAndExactSourceNameMatch()
    {
        string tempDir = CreateTempDir("ext_dotjs_exact");
        string localDir = Path.Combine(tempDir, "local");
        string userDir = Path.Combine(tempDir, "user");
        string settingsPath = Path.Combine(tempDir, "settings.json");
        try
        {
            Directory.CreateDirectory(localDir);
            Directory.CreateDirectory(userDir);

            string mirrorPath = Path.Combine(userDir, "komikumirror.js");
            string exactPath = Path.Combine(userDir, "mycustom.js");
            await File.WriteAllTextAsync(mirrorPath, @"
var source = {
    name: 'Komiku Mirror',
    baseUrl: 'https://mirror.example',
    language: 'id',
    version: '1.0.0',
    getPopularManga: function() { return { items: [], totalPages: 1 }; },
    getSearchManga: function() { return { items: [], totalPages: 1 }; },
    getMangaDetails: function(u) { return { title: 'T', url: u }; },
    getChapterList: function() { return []; },
    getPageList: function() { return []; }
};", Encoding.UTF8);
            await File.WriteAllTextAsync(exactPath, @"
var source = {
    name: 'Komiku',
    baseUrl: 'https://exact.example',
    language: 'id',
    version: '1.0.0',
    getPopularManga: function() { return { items: [], totalPages: 1 }; },
    getSearchManga: function() { return { items: [], totalPages: 1 }; },
    getMangaDetails: function(u) { return { title: 'T', url: u }; },
    getChapterList: function() { return []; },
    getPageList: function() { return []; }
};", Encoding.UTF8);

            using var settings = new SettingsService(settingsPath);
            using var network = new NetworkService(settings);
            using var sm = new SourceManager(localDir, userDir, deferExtensionLoad: false);

            // Exact match must win over earlier substring match "Komiku Mirror"
            var resolved = sm.GetSourceByName("Komiku");
            Assert.NotNull(resolved);
            Assert.Equal("Komiku", resolved!.Name);

            // Uninstalling a non-catalog plugin by .js filename must succeed
            var status = new SourceStatusService(sm, settings, network);
            using var repo = new ExtensionRepositoryService(sm, settings, status);
            var (ok, _) = repo.UninstallExtension("mycustom.js");
            Assert.True(ok);
            Assert.False(File.Exists(exactPath));
            Assert.DoesNotContain(sm.GetSources(), s => s.Name == "Komiku");
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task OfflineMode_BlocksMalServiceUpdateServiceAndPublicIpLiteralGuard()
    {
        string tempDir = CreateTempDir("offline_guards");
        string settingsPath = Path.Combine(tempDir, "settings.json");
        var prevOffline = HttpSource.IsOfflineModeProvider;
        try
        {
            using var settings = new SettingsService(settingsPath);
            settings.IsOfflineMode = true;
            settings.MalAccessToken = "dummy-token-offline-test";
            HttpSource.IsOfflineModeProvider = () => true;

            using var mal = new MyAnimeListService(settings);
            Assert.Empty(await mal.SearchMangaAsync("Solo Leveling"));
            Assert.False(await mal.UpdateMangaStatusAsync(12345, "reading", 10, 8));
            Assert.Equal(string.Empty, await mal.FetchUserProfileNameAsync());

            using var updater = new UpdateService();
            bool installOk = await updater.DownloadAndInstallUpdateAsync("https://github.com/ArisaAkiyama/yomic/releases/download/v2.4.0/Yomic_Setup_v2.4.0.exe");
            Assert.False(installOk);

            // Public IP literal must ALSO be blocked when Offline Mode is active
            bool literalSafe = await Yomic.Core.Helpers.UrlSecurityHelper.IsSafeResolvedPublicUrlAsync("https://93.184.216.34/page.jpg", bypassCache: true);
            Assert.False(literalSafe);
        }
        finally
        {
            HttpSource.IsOfflineModeProvider = prevOffline;
            CleanupTempDir(tempDir);
        }
    }
}
