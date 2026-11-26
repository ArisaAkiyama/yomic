using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Yomic.Core.Helpers;
using Yomic.Core.Models;
using Yomic.Core.Services;
using Yomic.Core.Sources;

namespace YomicDesktop.Tests;

public class CoreServicesTests
{
    [Fact]
    public void NaturalStringComparer_SortsChapterAndPageNumbersCorrectly()
    {
        var items = new[] { "10.jpg", "2.jpg", "1.jpg", "Chapter 10", "Chapter 2", "Chapter 1.5", "Chapter 1.2", "Infinity", "NaN" };
        var sorted = items.OrderBy(x => x, new NaturalStringComparer()).ToArray();

        Assert.True(Array.IndexOf(sorted, "1.jpg") < Array.IndexOf(sorted, "2.jpg"));
        Assert.True(Array.IndexOf(sorted, "2.jpg") < Array.IndexOf(sorted, "10.jpg"));
        Assert.True(Array.IndexOf(sorted, "Chapter 1.2") < Array.IndexOf(sorted, "Chapter 1.5"));
        Assert.True(Array.IndexOf(sorted, "Chapter 1.5") < Array.IndexOf(sorted, "Chapter 2"));
        Assert.True(Array.IndexOf(sorted, "Chapter 2") < Array.IndexOf(sorted, "Chapter 10"));
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("NUL", "_NUL")]
    [InlineData("CON.a.b", "_CON.a.b")]
    [InlineData("aux.txt", "_aux.txt")]
    [InlineData("Normal Title...", "Normal Title")]
    public void DownloadPathService_SanitizePathSegment_HandlesWindowsReservedNames(string input, string expected)
    {
        Assert.Equal(expected, DownloadPathService.SanitizePathSegment(input));
    }

    [Fact]
    public void DownloadPathService_ExtractCbz_RecoversFromEmptyCacheDir()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"YomicCbzTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string cbzPath = Path.Combine(tempDir, "chapter_test.cbz");

        try
        {
            using (var zip = ZipFile.Open(cbzPath, ZipArchiveMode.Create))
            {
                var e2 = zip.CreateEntry("10.jpg");
                using (var s = e2.Open()) s.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, 0, 4);

                var e1 = zip.CreateEntry("2.jpg");
                using (var s = e1.Open()) s.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE1 }, 0, 4);
            }

            string cacheRoot = Path.Combine(tempDir, "cbz_cache");
            string? extractedDir = DownloadPathService.ExtractCbzToCacheDirectory(cbzPath, cacheRoot);
            Assert.NotNull(extractedDir);
            Assert.True(Directory.Exists(extractedDir));

            var files = DownloadPathService.GetReadableFiles(extractedDir!);
            Assert.Equal(2, files.Count);

            // Simulate empty leftover extractDir bug and ensure recovery succeeds
            foreach (var f in files) File.Delete(f);
            Assert.Empty(DownloadPathService.GetReadableFiles(extractedDir!));

            string? recoveredDir = DownloadPathService.ExtractCbzToCacheDirectory(cbzPath, cacheRoot);
            Assert.NotNull(recoveredDir);
            Assert.Equal(2, DownloadPathService.GetReadableFiles(recoveredDir!).Count);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void SettingsService_ReadOnlyDispose_DoesNotOverwriteLiveSettings()
    {
        // Use isolated temp path so test ALWAYS executes real assertions and never touches live %LOCALAPPDATA%
        string tempDir = Path.Combine(Path.GetTempPath(), $"YomicSettingsTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string settingsPath = Path.Combine(tempDir, "settings.json");

        try
        {
            const string originalJson = "{\"IsDarkMode\":false,\"AppLanguage\":\"en\",\"UseSmartUpdate\":false}";
            File.WriteAllText(settingsPath, originalJson);
            var fixedStamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(settingsPath, fixedStamp);

            using (var readOnlySettings = new SettingsService(settingsPath))
            {
                Assert.False(readOnlySettings.IsDarkMode);
                Assert.Equal("en", readOnlySettings.AppLanguage);
                Assert.False(readOnlySettings.UseSmartUpdate);
            }

            // Unconditional assertions: Dispose() on a clean instance MUST NOT rewrite or touch the file
            Assert.Equal(fixedStamp, File.GetLastWriteTimeUtc(settingsPath));
            Assert.Equal(originalJson, File.ReadAllText(settingsPath));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void SettingsService_ResetAndReload_FireEventsOutsideLockWithoutDeadlock()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"YomicSettingsEventTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string settingsPath = Path.Combine(tempDir, "settings.json");

        try
        {
            using var settings = new SettingsService(settingsPath);
            settings.IsOfflineMode = true;
            settings.SaveImmediate();

            int offlineEventCount = 0;
            settings.OfflineModeChanged += _ =>
            {
                offlineEventCount++;
                // Calling SaveImmediate from another thread inside the event handler would deadlock if _saveLock were held
                var t = Task.Run(() => settings.SaveImmediate());
                Assert.True(t.Wait(TimeSpan.FromSeconds(3)), "Deadlock detected: event fired while holding _saveLock!");
            };

            settings.Reset();
            Assert.Equal(1, offlineEventCount);
            Assert.False(settings.IsOfflineMode);

            // Write IsOfflineMode=true back to disk and test Reload()
            File.WriteAllText(settingsPath, "{\"IsOfflineMode\":true}");
            settings.Reload();
            Assert.Equal(2, offlineEventCount);
            Assert.True(settings.IsOfflineMode);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void BackendIpcController_MapDownloadStatus_MapsErrorAndRetryAccurately()
    {
        var (errStatus, errText) = YomicDesktop.IPC.BackendIpcController.MapDownloadStatus("Error");
        Assert.Equal("error", errStatus);
        Assert.Equal("Gagal", errText);

        var (retryStatus, retryText) = YomicDesktop.IPC.BackendIpcController.MapDownloadStatus("Retrying (2/3)...");
        Assert.Equal("downloading", retryStatus);
        Assert.Equal("Retrying (2/3)...", retryText);

        var (dlStatus, _) = YomicDesktop.IPC.BackendIpcController.MapDownloadStatus("Downloading");
        Assert.Equal("downloading", dlStatus);

        var (doneStatus, _) = YomicDesktop.IPC.BackendIpcController.MapDownloadStatus("Completed");
        Assert.Equal("completed", doneStatus);
    }

    [Fact]
    public void WebResourceImageProxy_IsGmbrProHost_MatchesHostOnlyNotQueryString()
    {
        Assert.True(YomicDesktop.IPC.WebResourceImageProxy.IsGmbrProHost("https://gmbr.pro/img/1.jpg"));
        Assert.True(YomicDesktop.IPC.WebResourceImageProxy.IsGmbrProHost("https://cdn.gmbr.pro/img/1.jpg"));
        Assert.False(YomicDesktop.IPC.WebResourceImageProxy.IsGmbrProHost("https://127.0.0.1/?spoof=gmbr.pro"));
        Assert.False(YomicDesktop.IPC.WebResourceImageProxy.IsGmbrProHost("https://evil-gmbr.pro/img/1.jpg"));
    }

    [Fact]
    public void WebResourceImageProxy_DetectContentType_NeutralizesSvgAndDetectsMagicBytes()
    {
        byte[] pngHeader = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        byte[] jpgHeader = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 };
        byte[] svgBytes = System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>");

        Assert.Equal("image/png", YomicDesktop.IPC.WebResourceImageProxy.DetectContentType(pngHeader, "https://cdn.example.com/1.jpg"));
        Assert.Equal("image/jpeg", YomicDesktop.IPC.WebResourceImageProxy.DetectContentType(jpgHeader, "https://cdn.example.com/1.png"));
        Assert.Equal("application/octet-stream", YomicDesktop.IPC.WebResourceImageProxy.DetectContentType(svgBytes, "https://evil.example.com/payload.svg"));
    }

    [Fact]
    public void WebResourceImageProxy_TryNormalizeCurlTarget_StripsTrailingDotAndHandlesIpLiterals()
    {
        var trailingDotUri = new Uri("https://example.com./img/page1.jpg?q=1");
        Assert.True(YomicDesktop.IPC.WebResourceImageProxy.TryNormalizeCurlTarget(trailingDotUri, out var host, out var url, out var isIpLiteral));
        Assert.Equal("example.com", host);
        Assert.Equal("https://example.com/img/page1.jpg?q=1", url);
        Assert.False(isIpLiteral);
        Assert.DoesNotContain("example.com.", url);

        // Public IPv6 literal must succeed with isIpLiteral=true (so curl skips --resolve)
        var publicIpv6Uri = new Uri("https://[2606:4700:4700::1111]/img/page1.jpg");
        Assert.True(YomicDesktop.IPC.WebResourceImageProxy.TryNormalizeCurlTarget(publicIpv6Uri, out var v6Host, out var v6Url, out var v6IsIp));
        Assert.True(v6IsIp);
        Assert.Contains("2606:4700:4700::1111", v6Host);
        Assert.StartsWith("https://[2606:4700:4700::1111]/", v6Url);

        // Loopback/private IPv6 literal must be rejected
        var privateIpv6Uri = new Uri("https://[::1]/secret.jpg");
        Assert.False(YomicDesktop.IPC.WebResourceImageProxy.TryNormalizeCurlTarget(privateIpv6Uri, out _, out _, out _));
    }

    [Fact]
    public void WebResourceImageProxy_LooksLikeHtmlPayload_DetectsBomHtmlScriptAndXmlErrors()
    {
        byte[] bomDoctype = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes("  <!DOCTYPE html><html>Cloudflare</html>")).ToArray();
        byte[] scriptPayload = System.Text.Encoding.UTF8.GetBytes("<script>window.location='/'</script>");
        byte[] s3XmlError = System.Text.Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Error><Code>AccessDenied</Code></Error>");
        byte[] validPng = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        byte[] validJpg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 };

        Assert.True(YomicDesktop.IPC.WebResourceImageProxy.LooksLikeHtmlPayload(bomDoctype));
        Assert.True(YomicDesktop.IPC.WebResourceImageProxy.LooksLikeHtmlPayload(scriptPayload));
        Assert.True(YomicDesktop.IPC.WebResourceImageProxy.LooksLikeHtmlPayload(s3XmlError));
        Assert.False(YomicDesktop.IPC.WebResourceImageProxy.LooksLikeHtmlPayload(validPng));
        Assert.False(YomicDesktop.IPC.WebResourceImageProxy.LooksLikeHtmlPayload(validJpg));
    }

    [Fact]
    public void SqlitePragmaInterceptor_ConfiguresBusyTimeoutBeforeWalMode()
    {
        int busyIdx = Yomic.Core.Data.SqlitePragmaInterceptor.PragmaSql.IndexOf("busy_timeout", StringComparison.OrdinalIgnoreCase);
        int walIdx = Yomic.Core.Data.SqlitePragmaInterceptor.PragmaSql.IndexOf("journal_mode=WAL", StringComparison.OrdinalIgnoreCase);
        int fkIdx = Yomic.Core.Data.SqlitePragmaInterceptor.PragmaSql.IndexOf("foreign_keys=ON", StringComparison.OrdinalIgnoreCase);

        Assert.True(busyIdx >= 0, "busy_timeout pragma missing");
        Assert.True(walIdx > busyIdx, "busy_timeout MUST run before journal_mode=WAL");
        Assert.True(fkIdx >= 0, "foreign_keys=ON pragma missing");
    }

    [Fact]
    public void SettingsService_DpapiSecretProtection_EncryptsOnDiskAndRoundTrips()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"YomicDpapiTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string settingsPath = Path.Combine(tempDir, "settings.json");

        try
        {
            const string secretAccess = "mal_access_token_super_secret_12345";
            const string secretRefresh = "mal_refresh_token_super_secret_67890";

            using (var writer = new SettingsService(settingsPath))
            {
                writer.MalAccessToken = secretAccess;
                writer.MalRefreshToken = secretRefresh;
                writer.SaveImmediate();
            }

            string rawJson = File.ReadAllText(settingsPath);
            Assert.DoesNotContain(secretAccess, rawJson, StringComparison.Ordinal);
            Assert.DoesNotContain(secretRefresh, rawJson, StringComparison.Ordinal);
            Assert.Contains("dpapi:", rawJson, StringComparison.Ordinal);

            using (var reader = new SettingsService(settingsPath))
            {
                Assert.Equal(secretAccess, reader.MalAccessToken);
                Assert.Equal(secretRefresh, reader.MalRefreshToken);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private sealed class OfflineProbeHttpSource : HttpSource
    {
        public override long Id => 123456;
        public override string Name => "OfflineProbe";
        public override string BaseUrl => "https://example.com";
        public override string Language => "en";
        public Task<string> ProbeGetAsync(string url) => GetStringAsync(url);
        public override Task<System.Collections.Generic.List<Yomic.Core.Models.Manga>> GetPopularMangaAsync(int page) => throw new NotImplementedException();
        public override Task<System.Collections.Generic.List<Yomic.Core.Models.Manga>> GetSearchMangaAsync(string query, int page) => throw new NotImplementedException();
        public override Task<Yomic.Core.Models.Manga> GetMangaDetailsAsync(string url) => throw new NotImplementedException();
        public override Task<System.Collections.Generic.List<Yomic.Core.Models.Chapter>> GetChapterListAsync(string url) => throw new NotImplementedException();
        public override Task<System.Collections.Generic.List<string>> GetPageListAsync(string url) => throw new NotImplementedException();
    }

    [Fact]
    public async Task HttpSource_OfflineMode_ShortCircuitsRequestsWithoutNetwork()
    {
        var prev = HttpSource.IsOfflineModeProvider;
        try
        {
            HttpSource.IsOfflineModeProvider = () => true;
            using var probe = new OfflineProbeHttpSource();
            var ex = await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() => probe.ProbeGetAsync("https://example.com/test"));
            Assert.Contains("Offline Mode", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            HttpSource.IsOfflineModeProvider = prev;
        }
    }

    [Fact]
    public async Task JsMangaSource_SupportsAtobUploadDateNsfwOverrideAndRecursionLimit()
    {
        string tempPlugin = Path.Combine(Path.GetTempPath(), $"yomic_test_plugin_{Guid.NewGuid():N}.js");
        const string js = @"
var source = {
    name: 'TestNsfwPlugin',
    baseUrl: 'https://example.com',
    language: 'id',
    version: '1.0.0',
    isNsfw: true,
    getPopularManga: function(page) {
        // Test atob / btoa polyfill round-trip
        var decoded = atob(btoa('Hello Yomic'));
        return { items: [{ title: decoded, url: '/manga/test', thumbnailUrl: '' }], totalPages: 1 };
    },
    getSearchManga: function(q, page) {
        // Trigger infinite recursion to verify Jint LimitRecursion catches it without crashing CLR
        function boom(n) { return boom(n + 1); }
        return boom(0);
    },
    getMangaDetails: function(url) { return { title: 'T', url: url }; },
    getChapterList: function(url) {
        return [{ name: 'Chapter 1', url: '/ch-1', uploadDate: '2026-01-15T00:00:00Z' }];
    },
    getPageList: function(url) { return []; }
};";
        await File.WriteAllTextAsync(tempPlugin, js, Encoding.UTF8);

        try
        {
            using var jsSource = new JsMangaSource(tempPlugin);

            // Verify C# virtual override works via IMangaSource interface (not shadowed!)
            IMangaSource asInterface = jsSource;
            Assert.True(asInterface.IsNsfw);

            // Verify atob/btoa works inside Jint
            var popular = await jsSource.GetPopularMangaAsync(1);
            Assert.Single(popular);
            Assert.Equal("Hello Yomic", popular[0].Title);

            // Verify uploadDate ISO string fallback works when dateUpload is omitted
            var chapters = await jsSource.GetChapterListAsync("/manga/test");
            Assert.Single(chapters);
            Assert.True(chapters[0].DateUpload > 0);

            // Verify infinite recursion throws catchable RecursionDepthOverflowException instead of crashing the CLR process
            await Assert.ThrowsAsync<Jint.Runtime.RecursionDepthOverflowException>(
                () => jsSource.GetSearchMangaAsync("boom", 1));
        }
        finally
        {
            try { File.Delete(tempPlugin); } catch { }
        }
    }

    [Fact]
    public void DownloadService_IsValidDownloadedImagePayload_RejectsHtmlAndXmlChallengePages()
    {
        byte[] wafHtml = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><title>Just a moment...</title></html>");
        byte[] bomHtml = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("<html>Cloudflare</html>")).ToArray();
        byte[] xmlError = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Error><Code>AccessDenied</Code></Error>");
        byte[] validJpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };
        byte[] validPng = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00 };

        Assert.False(DownloadService.IsValidDownloadedImagePayload(Array.Empty<byte>()));
        Assert.False(DownloadService.IsValidDownloadedImagePayload(wafHtml));
        Assert.False(DownloadService.IsValidDownloadedImagePayload(bomHtml));
        Assert.False(DownloadService.IsValidDownloadedImagePayload(xmlError));
        Assert.True(DownloadService.IsValidDownloadedImagePayload(validJpeg));
        Assert.True(DownloadService.IsValidDownloadedImagePayload(validPng));
    }

    [Fact]
    public async Task DownloadService_CompletedGuardAndDisposePreservePartialState()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"YomicDlTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            using var settings = new SettingsService(Path.Combine(tempDir, "settings.json"));
            using var network = new NetworkService(settings);
            using var sourceMgr = new SourceManager();
            var libSvc = new LibraryService(Path.Combine(tempDir, "manga.db"));
            using var dl = new DownloadService(sourceMgr, libSvc, network, settings, Path.Combine(tempDir, "dl"), loadQueue: false);

            var completedReq = new DownloadRequest
            {
                Manga = new Yomic.Core.Models.Manga { Title = "M1", Url = "/m1" },
                Chapter = new Yomic.Core.Models.Chapter { Name = "Ch 1", Url = "/m1/c1" },
                Status = "Completed"
            };
            var activeReq = new DownloadRequest
            {
                Manga = new Yomic.Core.Models.Manga { Title = "M2", Url = "/m2" },
                Chapter = new Yomic.Core.Models.Chapter { Name = "Ch 2", Url = "/m2/c2" },
                Status = "Downloading"
            };

            dl.RegisterActiveForTesting(completedReq);
            dl.RegisterActiveForTesting(activeReq);

            // Pause/Cancel MUST NOT overwrite an already Completed active item
            dl.Pause(completedReq);
            Assert.Equal("Completed", completedReq.Status);
            Assert.False(completedReq.CancellationTokenSource.IsCancellationRequested);

            dl.Cancel(completedReq);
            Assert.Equal("Completed", completedReq.Status);
            Assert.False(completedReq.CancellationTokenSource.IsCancellationRequested);

            // Dispose MUST mark in-progress items as Paused (not Cancelled) before cancelling CTS so .tmp progress survives app exit
            dl.Dispose();
            Assert.Equal("Paused", activeReq.Status);
            Assert.True(activeReq.CancellationTokenSource.IsCancellationRequested);
            Assert.Equal("Completed", completedReq.Status);

            // Empty active list returns immediately from WaitForActiveDownloadsToStopAsync
            using var dl2 = new DownloadService(sourceMgr, libSvc, network, settings, Path.Combine(tempDir, "dl2"), loadQueue: false);
            Assert.True(await dl2.WaitForActiveDownloadsToStopAsync(TimeSpan.FromMilliseconds(200)));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task LibraryService_ConcurrentAddToLibraryAsync_CreatesSingleUniqueMangaRow()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"YomicLibConcurrency_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "manga.db");

        try
        {
            using (var initDb = new Yomic.Core.Data.MangaDbContext(dbPath))
            {
                initDb.Database.EnsureCreated();
            }

            var lib = new LibraryService(dbPath);
            var tasks = Enumerable.Range(0, 6).Select(i => lib.AddToLibraryAsync(new Yomic.Core.Models.Manga
            {
                Source = 999111,
                Url = "https://example.com/manga/concurrent-title",
                Title = $"Concurrent Title {i}",
                ThumbnailUrl = "https://example.com/cover.jpg"
            }));

            await Task.WhenAll(tasks);

            using var verifyDb = new Yomic.Core.Data.MangaDbContext(dbPath);
            int count = verifyDb.Mangas.Count(m => m.Source == 999111 && m.Url == "https://example.com/manga/concurrent-title");
            Assert.Equal(1, count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void UrlSecurityHelper_IsPrivateOrLoopbackIp_BlocksIpv6EmbeddedIpv4AndRfc6890()
    {
        string[] blockedIps =
        {
            "64:ff9b::127.0.0.1",
            "64:ff9b::169.254.169.254",
            "64:ff9b:1::1",
            "::127.0.0.1",
            "::ffff:0:127.0.0.1",
            "2002:7f00:0001::1",
            "2001:0::1",
            "2001:db8::1",
            "100::1",
            "192.0.0.1",
            "192.0.2.1",
            "198.18.0.1",
            "198.19.255.254",
            "198.51.100.1",
            "203.0.113.1"
        };

        foreach (var ipStr in blockedIps)
        {
            var ip = System.Net.IPAddress.Parse(ipStr);
            Assert.True(Yomic.Core.Helpers.UrlSecurityHelper.IsPrivateOrLoopbackIp(ip), $"Expected {ipStr} to be blocked");
            string url = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? $"http://[{ipStr}]/secret"
                : $"http://{ipStr}/secret";
            Assert.False(Yomic.Core.Helpers.UrlSecurityHelper.IsSafePublicHttpUrl(url, out _), $"Expected URL {url} to be blocked");
        }

        Assert.False(Yomic.Core.Helpers.UrlSecurityHelper.IsPrivateOrLoopbackIp(System.Net.IPAddress.Parse("2606:4700:4700::1111")));
        Assert.False(Yomic.Core.Helpers.UrlSecurityHelper.IsPrivateOrLoopbackIp(System.Net.IPAddress.Parse("1.1.1.1")));
        Assert.True(Yomic.Core.Helpers.UrlSecurityHelper.IsSafePublicHttpUrl("https://[2606:4700:4700::1111]/ok", out _));
    }

    [Fact]
    public void HttpSource_IsSameOrigin_DetectsCrossOriginRedirectsProperly()
    {
        var a = new Uri("https://Example.COM/page1");
        Assert.True(HttpSource.IsSameOrigin(a, new Uri("https://example.com./page2")));
        Assert.False(HttpSource.IsSameOrigin(a, new Uri("http://example.com/page2")));
        Assert.False(HttpSource.IsSameOrigin(a, new Uri("https://cdn.example.com/page2")));
        Assert.False(HttpSource.IsSameOrigin(a, new Uri("https://example.com:8443/page2")));
    }

    [Fact]
    public void UpdateService_ParseSha256SidecarText_ExtractsValidHexAndRejectsInvalid()
    {
        const string validHex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        Assert.Equal(validHex, UpdateService.ParseSha256SidecarText($"{validHex.ToUpperInvariant()}  Yomic_Setup_v2.3.0.exe\n"));
        Assert.Equal(validHex, UpdateService.ParseSha256SidecarText(validHex));
        Assert.Null(UpdateService.ParseSha256SidecarText("not-a-checksum"));
        Assert.Null(UpdateService.ParseSha256SidecarText(""));
    }

    [Fact]
    public void WebViewPageExtractorWindow_BuildDefaultExtractScript_SafelyEscapesNewlinesAndQuotes()
    {
        string maliciousSelector = "img');\nalert(1);\r\n//\"\\";
        string script = YomicDesktop.Views.WebViewPageExtractorWindow.BuildDefaultExtractScript(maliciousSelector);
        string? capturedSelector = null;
        bool alertTriggered = false;
        var engine = new Jint.Engine();
        engine.SetValue("alert", new Action<object?>(_ => alertTriggered = true));
        engine.SetValue("document", new
        {
            querySelectorAll = new Func<string, object[]>(sel =>
            {
                capturedSelector = sel;
                return Array.Empty<object>();
            })
        });
        engine.Execute(script);
        Assert.False(alertTriggered, "Injected alert(1) must never execute");
        Assert.Equal(maliciousSelector, capturedSelector);
    }

    [Fact]
    public void MyAnimeListService_Disconnect_DiscardsStaleTokenRefreshViaAuthEpoch()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"YomicMalEpoch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var settings = new SettingsService(Path.Combine(tempDir, "settings.json"));
            settings.MalAccessToken = "initial-access";
            settings.MalRefreshToken = "initial-refresh";
            using var mal = new MyAnimeListService(settings);

            int capturedEpoch = mal.CurrentAuthEpoch;
            mal.Disconnect();

            Assert.NotEqual(capturedEpoch, mal.CurrentAuthEpoch);
            Assert.False(mal.IsConnected);

            // Simulated in-flight refresh completing after Disconnect MUST be discarded
            bool applied = mal.TryApplyRefreshedTokens(
                capturedEpoch,
                "stale-reconnected-access",
                "stale-reconnected-refresh",
                DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds());

            Assert.False(applied);
            Assert.Equal("", settings.MalAccessToken);
            Assert.Equal("", settings.MalRefreshToken);
            Assert.False(mal.IsConnected);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SourceManager_DeferredExtensionLoad_CompletesAsynchronously()
    {
        using var sm = new SourceManager(deferExtensionLoad: true);
        await sm.EnsureExtensionsLoadedAsync();
        Assert.NotNull(sm.GetSources());
    }

    [Fact]
    public void BackendIpcController_PreloadedPageListCache_RoundTripsAndRespectsEviction()
    {
        string key = $"999999|https://example.com/ch-preload-{Guid.NewGuid():N}";
        var pages = new System.Collections.Generic.List<string>
        {
            "https://cdn.example.com/p1.jpg",
            "https://cdn.example.com/p2.jpg"
        };

        YomicDesktop.IPC.BackendIpcController.StorePreloadedPageList(key, pages);
        Assert.True(YomicDesktop.IPC.BackendIpcController.TryGetPreloadedPageList(key, out var retrieved));
        Assert.Equal(pages, retrieved);

        // Mutating caller copy must not corrupt cached list
        retrieved.Clear();
        Assert.True(YomicDesktop.IPC.BackendIpcController.TryGetPreloadedPageList(key, out var retrieved2));
        Assert.Equal(2, retrieved2.Count);
    }
}
