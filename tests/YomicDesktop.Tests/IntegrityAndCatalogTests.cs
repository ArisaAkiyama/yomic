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
}
