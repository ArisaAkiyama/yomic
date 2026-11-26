using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Xunit;
using Yomic.Core.Services;

namespace YomicDesktop.Tests;

public class IntegrityAndCatalogTests
{
    private static string FindCatalogDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "YomicDesktop", "Plugins", "Catalog");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "index.min.json")))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate src/YomicDesktop/Plugins/Catalog from test base directory.");
    }

    [Fact]
    public void UpdateService_ExtractExpectedSha256_ReadsGitHubAssetDigestAndReleaseBody()
    {
        string expectedHex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("yomic-installer-binary"))).ToLowerInvariant();

        var assetWithDigest = JsonNode.Parse($"{{\"name\":\"Yomic_Setup_v2.3.0.exe\",\"digest\":\"sha256:{expectedHex}\"}}");
        Assert.Equal(expectedHex, UpdateService.ExtractExpectedSha256(assetWithDigest, "", "Yomic_Setup_v2.3.0.exe"));

        var assetWithoutDigest = JsonNode.Parse("{\"name\":\"Yomic_Setup_v2.3.0.exe\"}");
        string releaseBody = $"### Checksums\nSHA-256: `{expectedHex}`";
        Assert.Equal(expectedHex, UpdateService.ExtractExpectedSha256(assetWithoutDigest, releaseBody, "Yomic_Setup_v2.3.0.exe"));

        byte[] actualHash = SHA256.HashData(Encoding.UTF8.GetBytes("yomic-installer-binary"));
        byte[] wrongHash = SHA256.HashData(Encoding.UTF8.GetBytes("tampered-binary"));
        Assert.True(UpdateService.VerifyHashMatchesExpectedHex(actualHash, expectedHex));
        Assert.False(UpdateService.VerifyHashMatchesExpectedHex(wrongHash, expectedHex));
        Assert.False(UpdateService.VerifyHashMatchesExpectedHex(actualHash, "not-a-valid-hex"));
    }

    [Fact]
    public void ExtensionRepositoryService_VerifySha256Hex_DetectsTampering()
    {
        const string script = "var source = { name: 'Demo', version: '1.0.0' };";
        string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script))).ToLowerInvariant();

        Assert.True(ExtensionRepositoryService.VerifySha256Hex(script, sha));
        Assert.False(ExtensionRepositoryService.VerifySha256Hex(script + " ", sha));
        Assert.False(ExtensionRepositoryService.VerifySha256Hex(script, "0000"));

        // Third-party CDN (e.g. jsDelivr) MUST fail-closed when expected SHA-256 is missing
        Assert.False(ExtensionRepositoryService.ShouldAcceptRemoteScript(script, null, isThirdPartyCdn: true));
        Assert.False(ExtensionRepositoryService.ShouldAcceptRemoteScript(script, "", isThirdPartyCdn: true));
        Assert.True(ExtensionRepositoryService.ShouldAcceptRemoteScript(script, sha, isThirdPartyCdn: true));
        Assert.False(ExtensionRepositoryService.ShouldAcceptRemoteScript(script + " ", sha, isThirdPartyCdn: true));

        // Official GitHub raw origin still rejects mismatches when SHA-256 is declared
        Assert.False(ExtensionRepositoryService.ShouldAcceptRemoteScript(script + " ", sha, isThirdPartyCdn: false));
        Assert.True(ExtensionRepositoryService.ShouldAcceptRemoteScript(script, sha, isThirdPartyCdn: false));
    }

    [Fact]
    public void ExtensionRepositoryService_CalculateVersionCode_OrdersSemVerProperly()
    {
        int v100 = ExtensionRepositoryService.CalculateVersionCode("1.0.0");
        int v1012 = ExtensionRepositoryService.CalculateVersionCode("1.0.12");
        int v110 = ExtensionRepositoryService.CalculateVersionCode("1.1.0");
        int v200 = ExtensionRepositoryService.CalculateVersionCode("2.0.0");

        Assert.True(v100 < v1012);
        Assert.True(v1012 < v110);
        Assert.True(v110 < v200);
    }

    [Fact]
    public void CatalogIntegrity_AllPluginsHaveValidManifestSha256SourcesAndNoBrokenPatterns()
    {
        string catalogDir = FindCatalogDirectory();
        string indexPath = Path.Combine(catalogDir, "index.min.json");
        var entries = JsonSerializer.Deserialize<List<ExtensionEntryDto>>(File.ReadAllText(indexPath));

        Assert.NotNull(entries);
        Assert.True(entries!.Count >= 100, $"Expected >= 100 catalog entries, got {entries.Count}");

        foreach (var entry in entries)
        {
            string apk = Path.GetFileName(entry.Apk);
            string jsPath = Path.Combine(catalogDir, apk);
            Assert.True(File.Exists(jsPath), $"Missing JS plugin file for {entry.Name}: {apk}");

            // Non-empty sources with valid non-zero id and baseUrl
            Assert.NotNull(entry.Sources);
            Assert.NotEmpty(entry.Sources!);
            foreach (var src in entry.Sources!)
            {
                Assert.False(string.IsNullOrWhiteSpace(src.Id) || src.Id == "0", $"Invalid source id on {apk}");
                Assert.False(string.IsNullOrWhiteSpace(src.BaseUrl), $"Missing baseUrl on {apk}");
            }

            // Version code parity
            Assert.Equal(ExtensionRepositoryService.CalculateVersionCode(entry.Version), entry.VersionCode);

            // SHA-256 parity with on-disk JS file
            string jsText = File.ReadAllText(jsPath, Encoding.UTF8);
            Assert.True(
                ExtensionRepositoryService.VerifySha256Hex(jsText, entry.Sha256),
                $"SHA-256 mismatch in index.min.json for {apk}");

            // Regression guards against previously broken plugin patterns
            Assert.DoesNotContain(".invalid/", jsText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("querySelector(\"+ div", jsText, StringComparison.Ordinal);
            Assert.DoesNotContain(":not(noscript img)", jsText, StringComparison.Ordinal);
            Assert.DoesNotContain("let fullUrl = this.baseUrl + url;", jsText, StringComparison.Ordinal);
            Assert.DoesNotContain("let fullUrl = this.baseUrl + mangaUrl;", jsText, StringComparison.Ordinal);
            Assert.DoesNotContain("let fullUrl = this.baseUrl + chapterUrl;", jsText, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"if\s*\(\s*[a-zA-Z_$][\w$]*\.status\s*!==?\s*200\b", jsText);
            Assert.DoesNotMatch(@"if\s*\(\s*[a-zA-Z_$][\w$]*\.status\s*<\s*200\b", jsText);
            Assert.DoesNotContain(".hpage a, .hpage a.r", jsText, StringComparison.Ordinal);
            Assert.DoesNotContain("totalResults > 0 ? Math.ceil(totalResults / appPageSize) : (page + 1)", jsText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CatalogRuntime_FixtureExecution_CosmicScansAndTheMangaParsersHandleEdgeCases()
    {
        string catalogDir = FindCatalogDirectory();

        // 1. Verify cosmicscansid.js pagination, genre filter, slug extraction, and HTML error tolerance in real Jint
        var capturedUrls = new List<string>();
        var engine = new Jint.Engine(opts => opts.TimeoutInterval(TimeSpan.FromSeconds(5)).LimitRecursion(64));
        engine.SetValue("fetch", new Func<string, object?, object>((url, _) =>
        {
            capturedUrls.Add(url);
            if (url.Contains("/filter?"))
            {
                return new { status = 200, body = "{\"data\":[{\"title\":\"Solo Leveling\",\"slug\":\"solo-leveling\",\"cover\":\"https://cdn.example/c.jpg\"}],\"cursor\":{\"hasNext\":true}}" };
            }
            if (url.Contains("/readingPage/"))
            {
                return new { status = 200, body = "<html>Cloudflare 200 Challenge</html>" };
            }
            return new { status = 200, body = "<div id='readerarea'><img data-src='https://cdn.example/p1.jpg'/><img src='https://cdn.example/p2.jpg'/></div>" };
        }));
        engine.SetValue("Html", new
        {
            parse = new Func<string, string, Yomic.Core.Sources.JsDocument>(Yomic.Core.Sources.HtmlParser.parse)
        });

        engine.Execute(File.ReadAllText(Path.Combine(catalogDir, "cosmicscansid.js"), Encoding.UTF8));

        Assert.Equal("solo-leveling", engine.Evaluate("source.extractSlug('https://04.cosmicscans.to/series/solo-leveling/')").AsString());
        Assert.Equal("ch-10", engine.Evaluate("source.extractSlug('/chapter/ch-10/')").AsString());
        Assert.True(engine.Evaluate("source.safeParseJson('<html>WAF</html>') === null").AsBoolean());

        var popTotalPages = engine.Evaluate("source.getPopularManga(2).totalPages").AsNumber();
        Assert.Equal(3, popTotalPages);
        Assert.Contains(capturedUrls, u => u.Contains("page=2"));

        engine.Evaluate("source.getMangaList(1, 1, 'Action', 'Manhwa', 'latest')");
        Assert.Contains(capturedUrls, u => u.Contains("genre=action") && u.Contains("status=ongoing") && u.Contains("type=manhwa"));

        var pageCount = engine.Evaluate("source.getPageList('/chapter/ch-10/').length").AsNumber();
        Assert.Equal(2, pageCount);

        // 2. Verify themanga.js appendAllParam handles existing query strings cleanly without ?all=1?all=1
        var engine2 = new Jint.Engine(opts => opts.TimeoutInterval(TimeSpan.FromSeconds(5)).LimitRecursion(64));
        engine2.SetValue("fetch", new Func<string, object?, object>((_, _) => new { status = 404, body = "" }));
        engine2.SetValue("Html", new
        {
            parse = new Func<string, string, Yomic.Core.Sources.JsDocument>(Yomic.Core.Sources.HtmlParser.parse)
        });
        engine2.Execute(File.ReadAllText(Path.Combine(catalogDir, "themanga.js"), Encoding.UTF8));

        string themangaBase = engine2.Evaluate("source.baseUrl").AsString();
        Assert.Equal($"{themangaBase}/series/foo?all=1", engine2.Evaluate("source.appendAllParam('/series/foo')").AsString());
        Assert.Equal($"{themangaBase}/series/foo?ref=1&all=1", engine2.Evaluate("source.appendAllParam('/series/foo?ref=1')").AsString());
        Assert.Equal($"{themangaBase}/series/foo?all=1", engine2.Evaluate("source.appendAllParam('/series/foo?all=1')").AsString());
    }

    [Fact]
    public void CatalogRuntime_FixtureExecution_Batch11PluginRegressionsHandledGracefully()
    {
        string catalogDir = FindCatalogDirectory();

        // 1. bacakomikpics.js: HTTP 200 HTML WAF body must return empty items instead of throwing TypeError
        var bacaEngine = new Jint.Engine(opts => opts.TimeoutInterval(TimeSpan.FromSeconds(5)).LimitRecursion(64));
        bacaEngine.SetValue("fetch", new Func<string, object?, object>((_, _) => new { status = 200, body = "<html>Cloudflare Challenge</html>" }));
        bacaEngine.SetValue("Html", new { parse = new Func<string, string, Yomic.Core.Sources.JsDocument>(Yomic.Core.Sources.HtmlParser.parse) });
        bacaEngine.Execute(File.ReadAllText(Path.Combine(catalogDir, "bacakomikpics.js"), Encoding.UTF8));
        Assert.Equal(0, bacaEngine.Evaluate("source.getPopularManga(1).items.length").AsNumber());

        // 2. aarlas.js: HTTP 200 HTML WAF body must return empty items/chapters instead of TypeError on json.feed
        var aarlasEngine = new Jint.Engine(opts => opts.TimeoutInterval(TimeSpan.FromSeconds(5)).LimitRecursion(64));
        aarlasEngine.SetValue("fetch", new Func<string, object?, object>((_, _) => new { status = 200, body = "<html>WAF</html>" }));
        aarlasEngine.SetValue("Html", new { parse = new Func<string, string, Yomic.Core.Sources.JsDocument>(Yomic.Core.Sources.HtmlParser.parse) });
        aarlasEngine.Execute(File.ReadAllText(Path.Combine(catalogDir, "aarlas.js"), Encoding.UTF8));
        Assert.Equal(0, aarlasEngine.Evaluate("source.getPopularManga(1).items.length").AsNumber());
        Assert.Equal(0, aarlasEngine.Evaluate("source.getChapterList('https://example.com/manga').length").AsNumber());

        // 3. westmanga.js: 403 -> token-refresh retry returning HTML 200 on mirror 0 must fail over to mirror 1 and succeed
        var westUrls = new List<string>();
        int mirror0ApiHits = 0;
        var westEngine = new Jint.Engine(opts => opts.TimeoutInterval(TimeSpan.FromSeconds(5)).LimitRecursion(64));
        westEngine.SetValue("log", new Action<object?>(_ => { }));
        westEngine.SetValue("fetch", new Func<string, object?, object>((url, _) =>
        {
            westUrls.Add(url);
            if (url.Contains("/api/visit-token"))
            {
                return new { status = 200, body = "{\"token\":\"tok-test\"}" };
            }
            if (url.Contains("westmanga.my"))
            {
                mirror0ApiHits++;
                if (mirror0ApiHits == 1)
                {
                    return new { status = 403, body = "" };
                }
                return new { status = 200, body = "<html>Mirror 0 Cloudflare HTML</html>" };
            }
            return new { status = 200, body = "{\"data\":[{\"title\":\"Mirror One Success\",\"slug\":\"mirror-one\"}],\"paginator\":{\"last_page\":1}}" };
        }));
        westEngine.SetValue("Html", new { parse = new Func<string, string, Yomic.Core.Sources.JsDocument>(Yomic.Core.Sources.HtmlParser.parse) });
        westEngine.Execute(File.ReadAllText(Path.Combine(catalogDir, "westmanga.js"), Encoding.UTF8));
        Assert.Equal(1, westEngine.Evaluate("source.requestApi('/api/contents', '?page=1').data.length").AsNumber());
        Assert.Contains(westUrls, u => u.Contains("westmanga.top/api/contents"));
    }

    [Fact]
    public async System.Threading.Tasks.Task CatalogRuntime_FixtureExecution_Batch14TemplatesHandleLazyLoadPaginationMirrorsSortAndNaNDates()
    {
        string catalogDir = FindCatalogDirectory();

        // 1. MangaStream template (astralscans.js): lazy-load data-src fallback, last-page pagination, no mirror double-fetch, and sort='latest'
        var astralUrls = new List<string>();
        bool failAllMirrors = false;
        var astralEngine = new Jint.Engine(opts => opts.TimeoutInterval(TimeSpan.FromSeconds(5)).LimitRecursion(64));
        astralEngine.SetValue("log", new Action<object?>(_ => { }));
        astralEngine.SetValue("fetch", new Func<string, object?, object>((url, _) =>
        {
            astralUrls.Add(url);
            if (failAllMirrors)
            {
                return new { status = 500, body = "" };
            }
            if (url.Contains("/chapter-1"))
            {
                return new
                {
                    status = 200,
                    body = "<div id='readerarea'><img src='data:image/svg+xml;base64,PHN2Zy8+' data-src='https://cdn.example/p1.jpg' /><img src='data:image/svg+xml;base64,PHN2Zy8+' data-lazy-src='https://cdn.example/p2.jpg' /></div>"
                };
            }
            // Page 2 with ONLY Previous link (.hpage a.l) — must NOT claim totalPages = 3
            return new
            {
                status = 200,
                body = "<div class='bs'><div class='bsx'><a href='https://astralscans.example/manga/test' title='Test'><img src='https://cdn.example/c.jpg'/></a></div></div><div class='hpage'><a class='l' href='?page=1'>Previous</a></div>"
            };
        }));
        astralEngine.SetValue("Html", new { parse = new Func<string, string, Yomic.Core.Sources.JsDocument>(Yomic.Core.Sources.HtmlParser.parse) });
        astralEngine.Execute(File.ReadAllText(Path.Combine(catalogDir, "astralscans.js"), Encoding.UTF8));

        // 1a. Lazy-load data-src fallback extracts both pages instead of 0
        Assert.Equal(2, astralEngine.Evaluate("source.getPageList('/chapter-1').length").AsNumber());
        Assert.Equal("https://cdn.example/p1.jpg", astralEngine.Evaluate("source.getPageList('/chapter-1')[0]").AsString());

        // 1b. Last page with .hpage a.l does not cause infinite pagination + sort='latest' sends order=update
        astralUrls.Clear();
        Assert.Equal(2, astralEngine.Evaluate("source.getMangaList(2, 1, 'Action', 'Manhwa', 'latest').totalPages").AsNumber());
        Assert.Contains(astralUrls, u => u.Contains("order=update"));

        // 1c. All mirrors failing does NOT execute a redundant extra fetch(url)
        astralUrls.Clear();
        failAllMirrors = true;
        int mirrorCount = (int)astralEngine.Evaluate("source.mirrorUrls.length").AsNumber();
        astralEngine.Evaluate("source.requestUrl('https://astralscans.example/manga/down')");
        Assert.Equal(mirrorCount, astralUrls.Count);

        // 2. Blogger template (inazumanga.js): 0 totalResults must NOT set totalPages = page + 1, and sort='latest' passes orderby=updated
        var bloggerUrls = new List<string>();
        var bloggerEngine = new Jint.Engine(opts => opts.TimeoutInterval(TimeSpan.FromSeconds(5)).LimitRecursion(64));
        bloggerEngine.SetValue("log", new Action<object?>(_ => { }));
        bloggerEngine.SetValue("fetch", new Func<string, object?, object>((url, _) =>
        {
            bloggerUrls.Add(url);
            return new { status = 200, body = "{\"feed\":{\"openSearch$totalResults\":{\"$t\":\"0\"},\"entry\":[]}}" };
        }));
        bloggerEngine.SetValue("Html", new { parse = new Func<string, string, Yomic.Core.Sources.JsDocument>(Yomic.Core.Sources.HtmlParser.parse) });
        bloggerEngine.Execute(File.ReadAllText(Path.Combine(catalogDir, "inazumanga.js"), Encoding.UTF8));
        Assert.Equal(1, bloggerEngine.Evaluate("source.getMangaList(1, 1, null, null, 'latest').totalPages").AsNumber());
        Assert.Contains(bloggerUrls, u => u.Contains("orderby=updated"));

        // 3. JsMangaSource engine-level NaN protection: NaN dateUpload must become 0 (not long.MinValue)
        string tempPlugin = Path.Combine(Path.GetTempPath(), $"yomic_nan_date_{Guid.NewGuid():N}.js");
        await File.WriteAllTextAsync(tempPlugin, @"
var source = {
    name: 'NaNDateTest',
    baseUrl: 'https://example.com',
    language: 'en',
    version: '1.0.0',
    getPopularManga: function() { return { items: [], totalPages: 1 }; },
    getSearchManga: function() { return { items: [], totalPages: 1 }; },
    getMangaDetails: function(u) { return { title: 'T', url: u }; },
    getChapterList: function() { return [{ name: 'Ch 1', url: '/c1', dateUpload: NaN, uploadDate: NaN }]; },
    getPageList: function() { return []; }
};", Encoding.UTF8);
        try
        {
            using var jsSrc = new Yomic.Core.Sources.JsMangaSource(tempPlugin);
            var chs = await jsSrc.GetChapterListAsync("/m1");
            Assert.Single(chs);
            Assert.Equal(0L, chs[0].DateUpload);
        }
        finally
        {
            try { File.Delete(tempPlugin); } catch { }
        }
    }

    [Fact]
    public void CatalogRuntime_FixtureExecution_CustomPluginsHonorSortAndChapterParsing()
    {
        string catalogDir = FindCatalogDirectory();

        static (Jint.Engine Engine, List<string> Urls) CreatePluginEngine(
            string scriptPath,
            Func<string, object?, object> fetchImpl)
        {
            var urls = new List<string>();
            var engine = new Jint.Engine(opts => opts.TimeoutInterval(TimeSpan.FromSeconds(5)).LimitRecursion(64));
            engine.SetValue("log", new Action<object?>(_ => { }));
            engine.SetValue("fetch", new Func<string, object?, object>((url, opts) =>
            {
                urls.Add(url);
                return fetchImpl(url, opts);
            }));
            engine.SetValue("Html", new { parse = new Func<string, string, Yomic.Core.Sources.JsDocument>(Yomic.Core.Sources.HtmlParser.parse) });
            engine.Execute(File.ReadAllText(scriptPath, Encoding.UTF8));
            return (engine, urls);
        }

        // 1. MangaMillion: sort='latest' -> order=UPDATED_AT, sort='popular' -> order=TOTAL_VIEW_COUNT, genres=[], chapterNumber parsing
        static string BuildProtobufChapterRecord(string name, byte chapId)
        {
            return "\x12" + (char)name.Length + name + "\x18" + (char)chapId;
        }
        string mmChapterFixture =
            BuildProtobufChapterRecord("Chapter 12.5", 42) +
            BuildProtobufChapterRecord("#100 Special", 100) +
            BuildProtobufChapterRecord("Prologue", 5) +
            new string(' ', 24);

        var (mmEngine, mmUrls) = CreatePluginEngine(
            Path.Combine(catalogDir, "mangamillion.js"),
            (url, _) =>
            {
                if (url.Contains("/api/chapter_list"))
                    return new { status = 200, body = mmChapterFixture };
                if (url.Contains("/api/register"))
                    return new { status = 200, body = "{\"token\":\"tok_test\"}" };
                return new { status = 200, body = "" };
            });

        Assert.Equal(0, mmEngine.Evaluate("source.genres.length").AsNumber());
        mmEngine.Evaluate("source.getMangaList(1, 0, null, null, 'latest')");
        Assert.Contains(mmUrls, u => u.Contains("order=UPDATED_AT"));
        mmUrls.Clear();
        mmEngine.Evaluate("source.getMangaList(1, 0, null, null, 'popular')");
        Assert.Contains(mmUrls, u => u.Contains("order=TOTAL_VIEW_COUNT"));

        mmEngine.Execute("source.token = 'tok_cached'; var mmChs = source.getChapterList('/en/title/123');");
        Assert.Equal(3, mmEngine.Evaluate("mmChs.length").AsNumber());
        Assert.Equal(12.5, mmEngine.Evaluate("mmChs[0].chapterNumber").AsNumber());
        Assert.Equal(100, mmEngine.Evaluate("mmChs[1].chapterNumber").AsNumber());
        Assert.Equal(3, mmEngine.Evaluate("mmChs[2].chapterNumber").AsNumber());

        // 2. WestManga: sort='latest' + status=1 sends status=Ongoing and orderBy=Update
        var (wmEngine, wmUrls) = CreatePluginEngine(
            Path.Combine(catalogDir, "westmanga.js"),
            (_, _) => new { status = 200, body = "{\"data\":[],\"paginator\":{\"last_page\":1}}" });
        wmEngine.Execute("source.cachedToken = 'wm_tok';");
        wmEngine.Evaluate("source.getMangaList(1, 1, null, null, 'latest')");
        Assert.Contains(wmUrls, u => u.Contains("status=Ongoing") && u.Contains("orderBy=Update"));

        // 3. Komiku: sort='latest' + status=1 sends statusmanga=ongoing and orderby=modified
        var (kmEngine, kmUrls) = CreatePluginEngine(
            Path.Combine(catalogDir, "komiku.js"),
            (_, _) => new { status = 200, body = "<div></div>" });
        kmEngine.Evaluate("source.getMangaList(1, 1, null, null, 'latest')");
        Assert.Contains(kmUrls, u => u.Contains("statusmanga=ongoing") && u.Contains("orderby=modified"));

        // 4. WeebCentral: sort='latest' + status=1 sends Latest Updates + included_status=Ongoing
        var (wcEngine, wcUrls) = CreatePluginEngine(
            Path.Combine(catalogDir, "weebcentral.js"),
            (_, _) => new { status = 200, body = "<div></div>" });
        wcEngine.Evaluate("source.getMangaList(1, 1, null, null, 'latest')");
        Assert.Contains(wcUrls, u => (u.Contains("Latest%20Updates") || u.Contains("Latest+Updates")) && u.Contains("included_status=Ongoing"));

        // 5. Kiryuu: sort='latest' + status=1 sends orderby=modified
        var (krEngine, krUrls) = CreatePluginEngine(
            Path.Combine(catalogDir, "kiryuu.js"),
            (_, _) => new { status = 200, body = "[]" });
        krEngine.Evaluate("source.getMangaList(1, 1, null, null, 'latest')");
        Assert.Contains(krUrls, u => u.Contains("orderby=modified"));

        // 6. WeebCentral: real genre + format filters wire included_tag and included_type
        wcUrls.Clear();
        wcEngine.Evaluate("source.getMangaList(1, 1, 'Action', 'Manhwa', 'latest')");
        Assert.Contains(wcUrls, u => u.Contains("included_tag=Action") && u.Contains("included_type=Manhwa"));
    }

    [Fact]
    public void CatalogRuntime_DeclaredGenresAndFormats_AreConsumedByGetMangaList()
    {
        string catalogDir = FindCatalogDirectory();
        var jsFiles = Directory.GetFiles(catalogDir, "*.js").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        var violations = new List<string>();

        foreach (var jsPath in jsFiles)
        {
            string src = File.ReadAllText(jsPath, Encoding.UTF8);
            var gMatch = System.Text.RegularExpressions.Regex.Match(src, @"\bgenres\s*:\s*\[([\s\S]*?)\]");
            var fMatch = System.Text.RegularExpressions.Regex.Match(src, @"\bformats\s*:\s*\[([\s\S]*?)\]");
            bool hasGenres = gMatch.Success && !string.IsNullOrWhiteSpace(gMatch.Groups[1].Value);
            bool hasFormats = fMatch.Success && !string.IsNullOrWhiteSpace(fMatch.Groups[1].Value);
            if (!hasGenres && !hasFormats) continue;

            int mlIdx = src.IndexOf("getMangaList:", StringComparison.Ordinal);
            if (mlIdx < 0)
            {
                violations.Add($"{Path.GetFileName(jsPath)}: declares genres/formats without getMangaList");
                continue;
            }

            string gParam = "genre", tParam = "type";
            var sigMatch = System.Text.RegularExpressions.Regex.Match(
                src.Substring(mlIdx, Math.Min(120, src.Length - mlIdx)),
                @"getMangaList\s*:\s*function\s*\(([^)]*)\)");
            if (sigMatch.Success)
            {
                var parts = sigMatch.Groups[1].Value.Split(',').Select(p => p.Trim()).ToArray();
                if (parts.Length >= 3 && !string.IsNullOrEmpty(parts[2])) gParam = parts[2];
                if (parts.Length >= 4 && !string.IsNullOrEmpty(parts[3])) tParam = parts[3];
            }

            int braceStart = src.IndexOf('{', mlIdx);
            int depth = 0, braceEnd = braceStart;
            for (int i = braceStart; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}')
                {
                    depth--;
                    if (depth == 0) { braceEnd = i; break; }
                }
            }
            string mlBody = src.Substring(braceStart + 1, Math.Max(0, braceEnd - braceStart - 1));

            if (hasGenres && !System.Text.RegularExpressions.Regex.IsMatch(mlBody, $@"\b{System.Text.RegularExpressions.Regex.Escape(gParam)}\b"))
                violations.Add($"{Path.GetFileName(jsPath)}: declares non-empty genres[] but getMangaList ignores '{gParam}'");
            if (hasFormats && !System.Text.RegularExpressions.Regex.IsMatch(mlBody, $@"\b{System.Text.RegularExpressions.Regex.Escape(tParam)}\b"))
                violations.Add($"{Path.GetFileName(jsPath)}: declares non-empty formats[] but getMangaList ignores '{tParam}'");
        }

        Assert.True(violations.Count == 0, "Fake plugin filter declarations:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void CatalogRuntime_FixtureExecution_Batch21PluginRepairs()
    {
        string catalogDir = FindCatalogDirectory();

        static Jint.Engine CreateEngine(string scriptPath, Func<string, object?, object> fetchImpl)
        {
            var engine = new Jint.Engine(opts => opts.TimeoutInterval(TimeSpan.FromSeconds(5)).LimitRecursion(64));
            engine.SetValue("log", new Action<object?>(_ => { }));
            engine.SetValue("fetch", new Func<string, object?, object>(fetchImpl));
            engine.SetValue("Html", new { parse = new Func<string, string, Yomic.Core.Sources.JsDocument>(Yomic.Core.Sources.HtmlParser.parse) });
            engine.Execute(File.ReadAllText(scriptPath, Encoding.UTF8));
            return engine;
        }

        // 1. Shinigami: snake_case v1 API for details, chapters, and pages
        var shEngine = CreateEngine(Path.Combine(catalogDir, "shinigami.js"), (url, _) =>
        {
            if (url.Contains("/v1/manga/detail/"))
                return new { status = 200, body = "{\"data\":{\"title\":\"Test Shngm\",\"cover_image_url\":\"https://cdn.shngm.id/c.jpg\",\"status\":1,\"description\":\"Desc\"}}" };
            if (url.Contains("/v1/chapter/m1/list"))
                return new { status = 200, body = "{\"data\":[{\"chapter_id\":\"ch-99\",\"chapter_number\":12.5,\"chapter_title\":\"Boss\",\"release_date\":\"2026-01-01\"}]}" };
            if (url.Contains("/v1/chapter/detail/ch-99"))
                return new { status = 200, body = "{\"data\":{\"base_url\":\"https://storage.shngm.id\",\"chapter\":{\"path\":\"/chapter/ch-99/\",\"data\":[\"01.webp\",\"02.webp\"]}}}" };
            return new { status = 200, body = "{}" };
        });
        Assert.Equal("https://cdn.shngm.id/c.jpg", shEngine.Evaluate("source.getMangaDetails('/series/m1').thumbnailUrl").AsString());
        Assert.Equal("/chapter/ch-99", shEngine.Evaluate("source.getChapterList('/series/m1')[0].url").AsString());
        Assert.Equal("Chapter 12.5 Boss", shEngine.Evaluate("source.getChapterList('/series/m1')[0].name").AsString());
        Assert.Equal("https://storage.shngm.id/chapter/ch-99/01.webp", shEngine.Evaluate("source.getPageList('/chapter/ch-99')[0]").AsString());

        // 2. MangaDex: skips externalUrl / pages===0 chapters and paginates translated feed without 50% overlap
        var mdUrls = new List<string>();
        var mdEngine = CreateEngine(Path.Combine(catalogDir, "mangadex.js"), (url, _) =>
        {
            mdUrls.Add(url);
            if (url.Contains("/feed"))
                return new { status = 200, body = "{\"total\":3,\"data\":[{\"id\":\"ext1\",\"attributes\":{\"chapter\":\"1\",\"externalUrl\":\"https://mangaplus.shueisha.co.jp/1\",\"pages\":0}},{\"id\":\"zero2\",\"attributes\":{\"chapter\":\"2\",\"pages\":0}},{\"id\":\"ok3\",\"attributes\":{\"chapter\":\"3\",\"title\":\"Real\",\"pages\":18}}]}" };
            if (url.Contains("/chapter?"))
                return new { status = 200, body = "{\"total\":128,\"data\":[{\"id\":\"c1\",\"relationships\":[{\"type\":\"manga\",\"id\":\"m-1\"}]}]}" };
            if (url.Contains("/manga?"))
                return new { status = 200, body = "{\"data\":[{\"id\":\"m-1\",\"attributes\":{\"title\":{\"en\":\"One\"},\"status\":\"ongoing\"},\"relationships\":[]}]}" };
            return new { status = 200, body = "{}" };
        });
        Assert.Equal(1, mdEngine.Evaluate("source.getChapterList('/title/11111111-1111-1111-1111-111111111111').length").AsNumber());
        Assert.Equal("/chapter/ok3", mdEngine.Evaluate("source.getChapterList('/title/11111111-1111-1111-1111-111111111111')[0].url").AsString());
        mdUrls.Clear();
        Assert.Equal(2, mdEngine.Evaluate("source.getMangaByTranslatedChapterFeed(2, 'id', 'latest').totalPages").AsNumber());
        Assert.Contains(mdUrls, u => u.Contains("offset=64"));

        // 3. MangaFire & Kiryuu: multi-page chapter list pagination fetches page 2+
        var mfEngine = CreateEngine(Path.Combine(catalogDir, "mangafire.js"), (url, _) =>
        {
            if (url.Contains("page=1"))
            {
                var p1 = string.Join(",", Enumerable.Range(1, 200).Select(i => $"{{\"id\":\"id{i}\",\"number\":{i}}}"));
                return new { status = 200, body = $"{{\"items\":[{p1}],\"meta\":{{\"hasNext\":true}}}}" };
            }
            return new { status = 200, body = "{\"items\":[{\"id\":\"id201\",\"number\":201}],\"meta\":{\"hasNext\":false}}" };
        });
        Assert.Equal(201, mfEngine.Evaluate("source.getChapterList('/title/abc-test').length").AsNumber());

        var krChapEngine = CreateEngine(Path.Combine(catalogDir, "kiryuu.js"), (url, _) =>
        {
            if (url.Contains("/manga?slug=")) return new { status = 200, body = "[{\"id\":77}]" };
            if (url.Contains("page=1")) return new { status = 200, body = "<a href='https://kiryuu03.com/manga/x/chapter-2'>Chapter 2</a>" };
            if (url.Contains("page=2")) return new { status = 200, body = "<a href='https://kiryuu03.com/manga/x/chapter-1'>Chapter 1</a>" };
            return new { status = 200, body = "" };
        });
        Assert.Equal(2, krChapEngine.Evaluate("source.getChapterList('/manga/x/').length").AsNumber());

        // 4. OkyyKomik: scoped status ignores nav "ongoing", and Blogger chapter feed paginates start-index
        var okEngine = CreateEngine(Path.Combine(catalogDir, "okyykomik.js"), (url, _) =>
        {
            if (url.Contains("start-index=151"))
                return new { status = 200, body = "{\"feed\":{\"openSearch$totalResults\":{\"$t\":\"151\"},\"entry\":[{\"title\":{\"$t\":\"Hero Chapter 1\"},\"link\":[{\"rel\":\"alternate\",\"href\":\"https://okyykomik.blogspot.com/ch-1.html\"}]}]}}" };
            if (url.Contains("start-index=1"))
            {
                var e150 = string.Join(",", Enumerable.Range(2, 150).Select(i => $"{{\"title\":{{\"$t\":\"Hero Chapter {i}\"}},\"link\":[{{\"rel\":\"alternate\",\"href\":\"https://okyykomik.blogspot.com/ch-{i}.html\"}}]}}"));
                return new { status = 200, body = $"{{\"feed\":{{\"openSearch$totalResults\":{{\"$t\":\"151\"}},\"entry\":[{e150}]}}}}" };
            }
            return new { status = 200, body = "<html><body><nav>Ongoing Menu</nav><label data-title='Hero'></label><div id='extra-info'>Status: Tamat</div></body></html>" };
        });
        Assert.Equal(2, okEngine.Evaluate("source.getMangaDetails('/hero.html').status").AsNumber());
        Assert.Equal(151, okEngine.Evaluate("source.getChapterList('/hero.html').length").AsNumber());

        // 5. Mangabat: fallback getPageList ignores unrelated site logo <img> outside reader container
        var mbEngine = CreateEngine(Path.Combine(catalogDir, "mangabat.js"), (_, _) => new
        {
            status = 200,
            body = "<html><body><header><img src='https://mangabat.com/logo.png'/></header><div class='container-chapter-reader'><img data-src='https://cdn.mb.example/p1.jpg'/></div></body></html>"
        });
        Assert.Equal(1, mbEngine.Evaluate("source.getPageList('/ch-1').length").AsNumber());
        Assert.StartsWith("https://cdn.mb.example/p1.jpg", mbEngine.Evaluate("source.getPageList('/ch-1')[0]").AsString());
    }

    [Fact]
    public void CatalogRuntime_FixtureExecution_Batch24LazyLoadBloggerAndSinglePage()
    {
        string catalogDir = FindCatalogDirectory();

        static Jint.Engine CreateEngine(string scriptPath, Func<string, object?, object> fetchImpl)
        {
            var engine = new Jint.Engine(opts => opts.TimeoutInterval(TimeSpan.FromSeconds(5)).LimitRecursion(64));
            engine.SetValue("log", new Action<object?>(_ => { }));
            engine.SetValue("fetch", new Func<string, object?, object>(fetchImpl));
            engine.SetValue("Html", new { parse = new Func<string, string, Yomic.Core.Sources.JsDocument>(Yomic.Core.Sources.HtmlParser.parse) });
            engine.Execute(File.ReadAllText(scriptPath, Encoding.UTF8));
            return engine;
        }

        // 1. Lazy-load placeholder rejection in getPageList (data:image/svg+xml & loading.gif skipped in favor of data-src/data-lazy-src)
        string lazyHtml = "<html><body><div id='readerarea' class='chapter-content post-body entry-content reading-content separator'>" +
                          "<img src='data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=' data-src='https://cdn.example/real-01.jpg' />" +
                          "<img src='https://cdn.example/loading.gif' data-lazy-src='https://cdn.example/real-02.jpg' />" +
                          "<img src='data:image/gif;base64,R0lGODlhAQABAAAAACw=' />" +
                          "</div></body></html>";

        foreach (var pluginFile in new[] { "areakomik.js", "kiryuu.js", "inazumanga.js", "mikoroku.js" })
        {
            var eng = CreateEngine(Path.Combine(catalogDir, pluginFile), (_, _) => new { status = 200, body = lazyHtml });
            var len = eng.Evaluate("source.getPageList('https://cdn.example/ch-1').length").AsNumber();
            Assert.Equal(2, len);
            Assert.StartsWith("https://cdn.example/real-01.jpg", eng.Evaluate("source.getPageList('https://cdn.example/ch-1')[0]").AsString());
            Assert.StartsWith("https://cdn.example/real-02.jpg", eng.Evaluate("source.getPageList('https://cdn.example/ch-1')[1]").AsString());
        }

        // 2. ZeistManga Blogger pagination (aarlas.js & inazumanga.js paginate start-index past Blogger's 150-entry cap)
        foreach (var bloggerFile in new[] { "aarlas.js", "inazumanga.js" })
        {
            var eng = CreateEngine(Path.Combine(catalogDir, bloggerFile), (url, _) =>
            {
                if (url.Contains("start-index=151"))
                {
                    return new
                    {
                        status = 200,
                        body = "{\"feed\":{\"entry\":[" +
                               "{\"id\":{\"$t\":\"e151\"},\"category\":[{\"term\":\"Chapter\"}],\"title\":{\"$t\":\"Chapter 151\"},\"link\":[{\"rel\":\"alternate\",\"href\":\"https://blog.example/ch-151.html\"}]}," +
                               "{\"id\":{\"$t\":\"e152\"},\"category\":[{\"term\":\"Chapter\"}],\"title\":{\"$t\":\"Chapter 152\"},\"link\":[{\"rel\":\"alternate\",\"href\":\"https://blog.example/ch-152.html\"}]}" +
                               "]}}"
                    };
                }
                if (url.Contains("start-index=1"))
                {
                    var batch150 = string.Join(",", Enumerable.Range(1, 150).Select(i =>
                        $"{{\"id\":{{\"$t\":\"e{i}\"}},\"category\":[{{\"term\":\"Chapter\"}}],\"title\":{{\"$t\":\"Chapter {i}\"}},\"link\":[{{\"rel\":\"alternate\",\"href\":\"https://blog.example/ch-{i}.html\"}}]}}"));
                    return new { status = 200, body = $"{{\"feed\":{{\"entry\":[{batch150}]}}}}" };
                }
                return new { status = 200, body = "<html><body><article class='oh a2'><header><h1>Epic Title</h1></header></article><script>clwd.run('Epic Title')</script></body></html>" };
            });
            Assert.Equal(152, eng.Evaluate("source.getChapterList('/series/epic').length").AsNumber());
        }

        // 3. MikoRoku: empty Blogger feed returns [] (no broken /detail.html fallback)
        var mikoEngine = CreateEngine(Path.Combine(catalogDir, "mikoroku.js"), (url, _) =>
        {
            if (url.Contains("all-manga.json"))
                return new { status = 200, body = "[{\"slug\":\"demo\",\"title\":\"Demo\",\"img\":\"https://cdn.example/c.jpg\",\"rating\":\"8.0\"}]" };
            return new { status = 200, body = "{\"feed\":{\"entry\":[]}}" };
        });
        Assert.Equal(0, mikoEngine.Evaluate("source.getChapterList('/detail.html?slug=demo').length").AsNumber());

        // 4. Softkomik: missing __NEXT_DATA__ returns [] without throwing
        var softEngine = CreateEngine(Path.Combine(catalogDir, "softkomik.js"), (_, _) => new { status = 200, body = "<html><body>Blocked</body></html>" });
        Assert.Equal(0, softEngine.Evaluate("source.getPageList('/m/chapter/1').length").AsNumber());

        // 5. StoryAboutUs non-array JSON guard + single-page plugins stop on page 2 and declare isHasMorePages: false
        var sauEngine = CreateEngine(Path.Combine(catalogDir, "storyaboutus.js"), (_, _) => new { status = 200, body = "{\"error\":\"maintenance\"}" });
        Assert.Equal(0, sauEngine.Evaluate("source.getSearchManga('test', 1).items.length").AsNumber());

        foreach (var singleFile in new[] { "storyaboutus.js", "mikoroku.js", "omicaso.js", "bacakomikpics.js", "comicaso.js", "komikrealm.js", "koronaxscan.js" })
        {
            var eng = CreateEngine(Path.Combine(catalogDir, singleFile), (_, _) => new { status = 200, body = "[]" });
            Assert.False(eng.Evaluate("Boolean(source.isHasMorePages)").AsBoolean(), $"{singleFile} must set isHasMorePages: false");
            Assert.Equal(0, eng.Evaluate("source.getPopularManga(2).items.length").AsNumber());
        }
    }

    [Fact]
    public void CatalogRuntime_AllPluginsLoadAndInitializeInRealJintEngine()
    {
        string catalogDir = FindCatalogDirectory();
        var jsFiles = Directory.GetFiles(catalogDir, "*.js").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

        Assert.True(jsFiles.Length >= 100, $"Expected >= 100 JS plugin files, found {jsFiles.Length}");

        var failures = new List<string>();
        foreach (var jsPath in jsFiles)
        {
            try
            {
                using var source = new Yomic.Core.Sources.JsMangaSource(jsPath);
                if (string.IsNullOrWhiteSpace(source.Name))
                    failures.Add($"{Path.GetFileName(jsPath)}: empty Name");
                if (string.IsNullOrWhiteSpace(source.BaseUrl) || !source.BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    failures.Add($"{Path.GetFileName(jsPath)}: non-HTTPS or empty BaseUrl '{source.BaseUrl}'");
                if (source.Id == 0)
                    failures.Add($"{Path.GetFileName(jsPath)}: Id == 0");
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(jsPath)}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, "Jint catalog load failures:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void InstallerScript_AutoRelaunchesAfterSilentUpdateWithoutPostinstallTrap()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(FindCatalogDirectory(), "..", "..", "..", ".."));
        string issPath = Path.Combine(repoRoot, "Installer", "Yomic.iss");
        Assert.True(File.Exists(issPath), $"Missing installer script at {issPath}");
        string iss = File.ReadAllText(issPath, Encoding.UTF8);

        // Silent relaunch [Run] line must NOT include 'postinstall' (Inno Setup skips postinstall in /SILENT mode)
        var silentRunLine = iss.Split('\n')
            .FirstOrDefault(l => l.Contains("ShouldRelaunchAfterSilentUpdate", StringComparison.Ordinal) &&
                                 l.TrimStart().StartsWith("Filename:", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(silentRunLine), "Missing silent relaunch [Run] entry in Yomic.iss");
        Assert.DoesNotContain("postinstall", silentRunLine!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nowait", silentRunLine!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skipifnotsilent", silentRunLine!, StringComparison.OrdinalIgnoreCase);

        // Interactive Finish-page launch must remain enabled by default (not unchecked)
        var interactiveRunLine = iss.Split('\n')
            .FirstOrDefault(l => l.Contains("skipifsilent", StringComparison.OrdinalIgnoreCase) &&
                                 l.TrimStart().StartsWith("Filename:", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(interactiveRunLine), "Missing interactive [Run] entry in Yomic.iss");
        Assert.Contains("postinstall", interactiveRunLine!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unchecked", interactiveRunLine!, StringComparison.OrdinalIgnoreCase);

        // Default RELAUNCH behavior in silent mode should relaunch unless explicitly /RELAUNCH=0
        Assert.Contains("{param:RELAUNCH|1}", iss, StringComparison.Ordinal);
    }
}
