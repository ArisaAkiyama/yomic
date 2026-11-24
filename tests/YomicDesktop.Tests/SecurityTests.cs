using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Yomic.Core.Helpers;
using Yomic.Core.Services;

namespace YomicDesktop.Tests;

public class SecurityTests
{
    [Theory]
    [InlineData("http://localhost/admin")]
    [InlineData("http://localhost./admin")]
    [InlineData("https://sub.localhost./api")]
    [InlineData("http://metadata.google.internal./computeMetadata/v1/")]
    [InlineData("http://127.0.0.1:49152/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://172.16.0.5/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public void IsSafePublicHttpUrl_RejectsUnsafeOrLocalUrls(string url)
    {
        Assert.False(UrlSecurityHelper.IsSafePublicHttpUrl(url, out _));
    }

    [Theory]
    [InlineData("https://mangadex.org/title/12345")]
    [InlineData("https://api.github.com/repos/ArisaAkiyama/yomic/releases/latest")]
    [InlineData("http://example.com/page.jpg")]
    public void IsSafePublicHttpUrl_AllowsPublicHttpUrls(string url)
    {
        Assert.True(UrlSecurityHelper.IsSafePublicHttpUrl(url, out var parsed));
        Assert.NotNull(parsed);
    }

    [Theory]
    [InlineData("https://script.google.com/macros/s/AKfycb123/exec", true)]
    [InlineData("https://script.googleusercontent.com/macros/echo?user_content_key=123", true)]
    [InlineData("http://script.google.com/macros/s/AKfycb123/exec", false)]
    [InlineData("https://evil.example/script.google.com", false)]
    [InlineData("https://localhost./macros", false)]
    public void IsAllowedFeedbackWebhookUrl_EnforcesGoogleAppsScriptAllowlist(string url, bool expected)
    {
        Assert.Equal(expected, UrlSecurityHelper.IsAllowedFeedbackWebhookUrl(url, out _));
    }

    [Fact]
    public void CookieMatchesRequest_EnforcesDomainBoundaryAndSecureFlag()
    {
        var realHost = new Uri("https://api.shinigami.asia/v1/chapters");
        var evilHost = new Uri("https://evilshinigami.asia/v1/chapters");
        var httpHost = new Uri("http://shinigami.asia/");

        Assert.True(UrlSecurityHelper.CookieMatchesRequest(realHost, ".shinigami.asia", isSecure: true));
        Assert.False(UrlSecurityHelper.CookieMatchesRequest(evilHost, "shinigami.asia", isSecure: false));
        Assert.False(UrlSecurityHelper.CookieMatchesRequest(httpHost, "shinigami.asia", isSecure: true));
        Assert.True(UrlSecurityHelper.CookieMatchesRequest(httpHost, "shinigami.asia", isSecure: false));
    }

    [Fact]
    public void IsPathInsideDirectory_PreventsPrefixAndTraversalBypass()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "YomicPathTest_Base");
        string siblingDir = Path.Combine(Path.GetTempPath(), "YomicPathTest_BaseBackup");

        Assert.True(UrlSecurityHelper.IsPathInsideDirectory(Path.Combine(baseDir, "sub", "img.jpg"), baseDir));
        Assert.False(UrlSecurityHelper.IsPathInsideDirectory(Path.Combine(siblingDir, "img.jpg"), baseDir));
        Assert.False(UrlSecurityHelper.IsPathInsideDirectory(Path.Combine(baseDir, "..", "other.db"), baseDir));
    }

    [Fact]
    public async Task ReadContentBoundedAsync_RejectsPayloadsExceedingCap()
    {
        var smallBytes = Encoding.UTF8.GetBytes("hello yomic");
        using var okContent = new ByteArrayContent(smallBytes);
        var readOk = await UrlSecurityHelper.ReadContentBoundedAsync(okContent, 64);
        Assert.Equal(smallBytes, readOk);

        var largeBytes = new byte[2048];
        using var tooBigContent = new StreamContent(new MemoryStream(largeBytes));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UrlSecurityHelper.ReadContentBoundedAsync(tooBigContent, 512));
    }

    [Theory]
    [InlineData("https://github.com/ArisaAkiyama/yomic/releases/download/v2.3.0/Yomic_Setup_v2.3.0.exe", true)]
    [InlineData("https://github.com/ArisaAkiyama/yomic/releases/tag/v2.3.0", true)]
    [InlineData("https://objects.githubusercontent.com/attacker-repo/evil.exe", false)]
    [InlineData("https://release-assets.githubusercontent.com/attacker/evil.exe", false)]
    [InlineData("https://github.com/attacker/yomic/releases/download/v1.0/evil.exe", false)]
    [InlineData("http://github.com/ArisaAkiyama/yomic/releases/download/v2.3.0/Yomic.exe", false)]
    [InlineData(@"\\attacker\share\Yomic_Update.exe", false)]
    public void UpdateService_IsAllowedUpdateUrl_RestrictsToOfficialRepo(string url, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsAllowedUpdateUrl(url, out _));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.5.4", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("1.1.1.1", false)]
    [InlineData("8.8.8.8", false)]
    public void IsPrivateOrLoopbackIp_ClassifiesCorrectly(string ipText, bool expectedPrivate)
    {
        var ip = IPAddress.Parse(ipText);
        Assert.Equal(expectedPrivate, UrlSecurityHelper.IsPrivateOrLoopbackIp(ip));
    }
}
