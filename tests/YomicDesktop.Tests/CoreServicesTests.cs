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
    public void WebResourceImageProxy_TryNormalizeCurlTarget_StripsTrailingDotConsistently()
    {
        var trailingDotUri = new Uri("https://example.com./img/page1.jpg?q=1");
        Assert.True(YomicDesktop.IPC.WebResourceImageProxy.TryNormalizeCurlTarget(trailingDotUri, out var host, out var url));
        Assert.Equal("example.com", host);
        Assert.Equal("https://example.com/img/page1.jpg?q=1", url);
        Assert.DoesNotContain("example.com.", url);
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
}
