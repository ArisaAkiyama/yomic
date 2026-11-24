using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Yomic.Core.Services;

namespace YomicDesktop.IPC
{
    /// <summary>
    /// Handles https://img.yomic.local/* requests inside WebView2 for Manga Covers, Reader Pages, and Extension Icons.
    /// Automatically injects Referer, User-Agent, Sec-Fetch-*, Cloudflare cookies, DoH/DPI bypass, disk caching, and AES-256-CBC decryption.
    /// </summary>
    public class WebResourceImageProxy : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly NetworkService _networkService;
        private readonly SourceManager _sourceManager;
        private readonly SettingsService? _settingsService;
        private readonly ImageProcessingService _imageProcessor;
        private readonly string _coversDir;
        private readonly string _readerCacheDir;
        private readonly string _iconsDir;

        private static readonly SemaphoreSlim _pageDownloadThrottle = new(6, 6);
        private static readonly SemaphoreSlim _coverDownloadThrottle = new(8, 8);

        // Thread-safe in-memory LRU cache (~48 MB) for instant 0ms scroll-back & chapter reopen
        private const long MaxRamCacheBytes = 48L * 1024 * 1024;
        private const int ZeroCopyFastPathMaxBytes = 1536 * 1024; // 1.5 MB
        private readonly object _ramCacheLock = new();
        private readonly Dictionary<string, LinkedListNode<(string Key, byte[] Data)>> _ramCacheMap = new(StringComparer.Ordinal);
        private readonly LinkedList<(string Key, byte[] Data)> _ramCacheOrder = new();
        private long _ramCacheBytes = 0;

        // Shared pooled HttpClient (rebuilt only when DoH provider changes) to avoid per-image socket exhaustion
        private readonly object _httpClientLock = new();
        private HttpClient? _sharedHttpClient;
        private int _sharedHttpClientDoh = -1;
        private readonly EventHandler _connectionResetHandler;
        private volatile bool _isDisposed;

        private HttpClient GetOrCreateHttpClient()
        {
            if (_settingsService?.IsOfflineMode == true)
            {
                throw new WebException("Application is in Offline Mode.");
            }
            int doh = _settingsService?.DnsOverHttpsProvider ?? 0;
            lock (_httpClientLock)
            {
                if (_sharedHttpClient != null && _sharedHttpClientDoh == doh)
                {
                    return _sharedHttpClient;
                }
                var created = _networkService.CreateOptimizedHttpClient(useCookies: false);
                created.Timeout = TimeSpan.FromSeconds(30);
                _sharedHttpClient = created;
                _sharedHttpClientDoh = doh;
                return created;
            }
        }

        private void ResetSharedHttpClient()
        {
            lock (_httpClientLock)
            {
                // Detach so in-flight requests finish safely while new requests use fresh pool
                _sharedHttpClient = null;
                _sharedHttpClientDoh = -1;
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            try { _networkService.ConnectionReset -= _connectionResetHandler; } catch { }
            ResetSharedHttpClient();
            ClearRamCache();
        }

        public void ClearRamCache()
        {
            lock (_ramCacheLock)
            {
                _ramCacheMap.Clear();
                _ramCacheOrder.Clear();
                _ramCacheBytes = 0;
            }
        }

        private static async Task WriteFileAtomicAsync(string targetPath, byte[] data)
        {
            string? dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(tempPath, data);
                File.Move(tempPath, targetPath, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        private bool TryGetRamCache(string key, out byte[]? data)
        {
            lock (_ramCacheLock)
            {
                if (_ramCacheMap.TryGetValue(key, out var node))
                {
                    _ramCacheOrder.Remove(node);
                    _ramCacheOrder.AddFirst(node);
                    data = node.Value.Data;
                    return true;
                }
            }
            data = null;
            return false;
        }

        private void PutRamCache(string key, byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length > MaxRamCacheBytes / 4) return;
            lock (_ramCacheLock)
            {
                if (_ramCacheMap.TryGetValue(key, out var existing))
                {
                    _ramCacheBytes -= existing.Value.Data.Length;
                    _ramCacheOrder.Remove(existing);
                    _ramCacheMap.Remove(key);
                }

                var node = new LinkedListNode<(string Key, byte[] Data)>((key, data));
                _ramCacheOrder.AddFirst(node);
                _ramCacheMap[key] = node;
                _ramCacheBytes += data.Length;

                while (_ramCacheBytes > MaxRamCacheBytes && _ramCacheOrder.Last != null)
                {
                    var tail = _ramCacheOrder.Last;
                    _ramCacheOrder.RemoveLast();
                    _ramCacheMap.Remove(tail.Value.Key);
                    _ramCacheBytes -= tail.Value.Data.Length;
                }
            }
        }

        private static bool CanUseZeroCopyFastPath(byte[] bytes, bool shouldCrop, bool shouldSharpen, bool shouldOpt)
        {
            if (shouldCrop || shouldSharpen) return false;
            if (!shouldOpt) return true;
            if (bytes.Length > ZeroCopyFastPathMaxBytes || bytes.Length < 12) return false;
            // JPEG: FF D8 FF
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return true;
            // WebP: RIFF....WEBP
            if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            {
                return true;
            }
            return false;
        }

        public WebResourceImageProxy(
            Dispatcher dispatcher,
            NetworkService networkService,
            SourceManager sourceManager,
            SettingsService? settingsService = null,
            ImageProcessingService? imageProcessor = null)
        {
            _dispatcher = dispatcher;
            _networkService = networkService;
            _sourceManager = sourceManager;
            _settingsService = settingsService;
            _imageProcessor = imageProcessor ?? ImageProcessingService.Instance;
            _connectionResetHandler = (_, _) => ResetSharedHttpClient();
            _networkService.ConnectionReset += _connectionResetHandler;

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _coversDir = Path.Combine(localAppData, "Yomic", "covers");
            _readerCacheDir = Path.Combine(localAppData, "Yomic", "Cache", "Reader");

            var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _iconsDir = Path.Combine(roamingAppData, "Yomic", "Icons");

            Directory.CreateDirectory(_coversDir);
            Directory.CreateDirectory(_readerCacheDir);
            Directory.CreateDirectory(_iconsDir);
        }

        public static string BuildCoverProxyUrl(long mangaId, string? rawUrl, long sourceId = 0)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return string.Empty;
            string trimmed = rawUrl.Trim();
            if (trimmed.StartsWith("https://img.yomic.local/", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }
            if (trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("data:image/svg+xml", StringComparison.OrdinalIgnoreCase) &&
                trimmed.IndexOfAny(new[] { '"', '\'', '<', '>', '\r', '\n' }) < 0)
            {
                return trimmed;
            }
            if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
            return $"https://img.yomic.local/cover?mangaId={mangaId}&sourceId={sourceId}&url={Uri.EscapeDataString(trimmed)}";
        }

        public static string BuildPageProxyUrl(string rawUrlOrLocalPath, long sourceId = 0, bool isLocalFile = false, bool force = false)
        {
            if (string.IsNullOrWhiteSpace(rawUrlOrLocalPath)) return string.Empty;
            if (isLocalFile || (!rawUrlOrLocalPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                                !rawUrlOrLocalPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                return $"https://img.yomic.local/page?local={Uri.EscapeDataString(rawUrlOrLocalPath)}";
            }
            var forceParam = force ? "&force=1" : string.Empty;
            return $"https://img.yomic.local/page?sourceId={sourceId}&url={Uri.EscapeDataString(rawUrlOrLocalPath)}{forceParam}";
        }

        public static string BuildIconProxyUrl(string pkg, string? rawUrl = null)
        {
            if (string.IsNullOrWhiteSpace(pkg)) return string.Empty;
            if (!string.IsNullOrWhiteSpace(rawUrl) && rawUrl.StartsWith("https://img.yomic.local/", StringComparison.OrdinalIgnoreCase))
            {
                return rawUrl;
            }
            var urlParam = !string.IsNullOrWhiteSpace(rawUrl) ? $"&url={Uri.EscapeDataString(rawUrl)}" : string.Empty;
            return $"https://img.yomic.local/icon?pkg={Uri.EscapeDataString(pkg)}{urlParam}";
        }

        public void Attach(CoreWebView2 webView)
        {
            webView.AddWebResourceRequestedFilter("https://img.yomic.local/*", CoreWebView2WebResourceContext.All);
            webView.WebResourceRequested += OnWebResourceRequested;
        }

        private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (sender is not CoreWebView2 webView) return;
            var uriStr = e.Request.Uri;
            if (!uriStr.StartsWith("https://img.yomic.local/", StringComparison.OrdinalIgnoreCase)) return;

            var deferral = e.GetDeferral();
            _ = HandleRequestAsync(webView, e, uriStr, deferral);
        }

        private async Task HandleRequestAsync(
            CoreWebView2 webView,
            CoreWebView2WebResourceRequestedEventArgs e,
            string uriStr,
            CoreWebView2Deferral deferral)
        {
            try
            {
                var uri = new Uri(uriStr);
                var query = ParseQueryString(uri.Query);
                var path = uri.AbsolutePath.Trim('/').ToLowerInvariant();

                byte[]? bytes = null;
                string contentType = "image/jpeg";

                if (path == "cover")
                {
                    query.TryGetValue("url", out var rawUrl);
                    query.TryGetValue("mangaId", out var mangaIdStr);
                    query.TryGetValue("sourceId", out var srcIdStr);
                    long.TryParse(mangaIdStr, out long mangaId);
                    long.TryParse(srcIdStr, out long sourceId);

                    if (!string.IsNullOrWhiteSpace(rawUrl))
                    {
                        bytes = await FetchCoverBytesAsync(mangaId, rawUrl, sourceId);
                        contentType = DetectContentType(bytes, rawUrl);
                    }
                }
                else if (path == "page")
                {
                    query.TryGetValue("crop", out var cropStr);
                    query.TryGetValue("sharpen", out var sharpenStr);
                    query.TryGetValue("opt", out var optStr);

                    bool shouldCrop = cropStr == "1" || (cropStr != "0" && (_settingsService?.EnableAutoCropBorders ?? false));
                    bool shouldSharpen = sharpenStr == "1" || (sharpenStr != "0" && (_settingsService?.EnableImageSharpening ?? false));
                    bool shouldOpt = optStr == "1" || (optStr != "0" && (_settingsService?.EnableImageOptimization ?? true));
                    int maxDim = _settingsService?.MaxImageDimension ?? 2160;

                    if (query.TryGetValue("local", out var localPath) && !string.IsNullOrWhiteSpace(localPath))
                    {
                        if (IsAllowedLocalImagePath(localPath) && File.Exists(localPath))
                        {
                            var localCacheKey = $"local_{localPath}_{(shouldCrop ? "c1" : "c0")}_{(shouldSharpen ? "s1" : "s0")}_{(shouldOpt ? "o1" : "o0")}_{maxDim}";
                            if (TryGetRamCache(localCacheKey, out var ramLocal) && ramLocal != null)
                            {
                                bytes = ramLocal;
                                contentType = DetectContentType(bytes, localPath);
                            }
                            else
                            {
                                bytes = await File.ReadAllBytesAsync(localPath);
                                contentType = DetectContentType(bytes, localPath);
                                if (bytes != null && bytes.Length > 0 && !CanUseZeroCopyFastPath(bytes, shouldCrop, shouldSharpen, shouldOpt))
                                {
                                    var opts = new ImageProcessingOptions
                                    {
                                        EnableOptimization = shouldOpt,
                                        AutoCropBorders = shouldCrop,
                                        EnableSharpening = shouldSharpen,
                                        MaxDimension = maxDim
                                    };
                                    var rawLocal = bytes;
                                    bytes = await Task.Run(() => _imageProcessor.ProcessPage(rawLocal, opts));
                                    contentType = DetectContentType(bytes, "page.webp");
                                }
                                if (bytes != null && bytes.Length > 0)
                                {
                                    PutRamCache(localCacheKey, bytes);
                                }
                            }
                        }
                    }
                    else if (query.TryGetValue("url", out var rawPageUrl) && !string.IsNullOrWhiteSpace(rawPageUrl))
                    {
                        query.TryGetValue("sourceId", out var srcIdStr);
                        long.TryParse(srcIdStr, out long sourceId);
                        query.TryGetValue("force", out var forceStr);
                        bool force = forceStr == "1";
                        bytes = await FetchPageBytesAsync(rawPageUrl, sourceId, shouldCrop, shouldSharpen, shouldOpt, maxDim, force);
                        contentType = DetectContentType(bytes, "page.webp");
                    }
                }
                else if (path == "icon")
                {
                    query.TryGetValue("pkg", out var pkg);
                    query.TryGetValue("url", out var iconUrl);
                    if (!string.IsNullOrWhiteSpace(pkg) || !string.IsNullOrWhiteSpace(iconUrl))
                    {
                        bytes = await FetchIconBytesAsync(pkg ?? "ext", iconUrl);
                        contentType = DetectContentType(bytes, iconUrl ?? "icon.png");
                    }
                }

                await _dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (bytes != null && bytes.Length > 0)
                        {
                            var ms = new MemoryStream(bytes);
                            var headers = $"Content-Type: {contentType}\r\nX-Content-Type-Options: nosniff\r\nAccess-Control-Allow-Origin: https://app.yomic.local\r\nCache-Control: public, max-age=86400";
                            e.Response = webView.Environment.CreateWebResourceResponse(ms, 200, "OK", headers);
                        }
                        else
                        {
                            e.Response = webView.Environment.CreateWebResourceResponse(Stream.Null, 404, "Not Found", "Access-Control-Allow-Origin: https://app.yomic.local");
                        }
                    }
                    finally
                    {
                        deferral.Complete();
                    }
                });
            }
            catch (Exception ex)
            {
                LogService.Debug("ImageProxy", $"Error serving {uriStr}: {ex.Message}");
                await _dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        e.Response = webView.Environment.CreateWebResourceResponse(Stream.Null, 500, "Internal Error", "Access-Control-Allow-Origin: https://app.yomic.local");
                    }
                    catch { }
                    finally
                    {
                        deferral.Complete();
                    }
                });
            }
        }

        private async Task<byte[]?> FetchCoverBytesAsync(long mangaId, string rawUrl, long sourceId)
        {
            // 1. Check local covers directory by mangaId or URL MD5
            if (mangaId > 0)
            {
                var idFile = Path.Combine(_coversDir, $"{mangaId}.jpg");
                if (File.Exists(idFile))
                {
                    try { return await File.ReadAllBytesAsync(idFile); } catch { }
                }
                var idFileAlt = Path.Combine(_coversDir, $"cover_{mangaId}.jpg");
                if (File.Exists(idFileAlt))
                {
                    try { return await File.ReadAllBytesAsync(idFileAlt); } catch { }
                }
            }

            var urlHash = ComputeMd5Hex(rawUrl);
            var cachedPath = Path.Combine(_coversDir, $"url_{urlHash}.cache");
            if (File.Exists(cachedPath))
            {
                try { return await File.ReadAllBytesAsync(cachedPath); } catch { }
            }

            // If rawUrl is a local file path
            if (!rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                IsAllowedLocalImagePath(rawUrl) &&
                File.Exists(rawUrl))
            {
                return await File.ReadAllBytesAsync(rawUrl);
            }

            await _coverDownloadThrottle.WaitAsync();
            try
            {
                var data = await DownloadImageWithHeadersAsync(rawUrl, sourceId);
                if (data != null && data.Length > 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await WriteFileAtomicAsync(cachedPath, data);
                            if (mangaId > 0)
                            {
                                var idFile = Path.Combine(_coversDir, $"{mangaId}.jpg");
                                if (!File.Exists(idFile))
                                {
                                    await WriteFileAtomicAsync(idFile, data);
                                }
                            }
                        }
                        catch { }
                    });
                }
                return data;
            }
            finally
            {
                _coverDownloadThrottle.Release();
            }
        }

        private async Task<byte[]?> FetchPageBytesAsync(
            string rawUrl,
            long sourceId,
            bool shouldCrop,
            bool shouldSharpen,
            bool shouldOpt,
            int maxDim,
            bool force = false)
        {
            if (!rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (IsAllowedLocalImagePath(rawUrl) && File.Exists(rawUrl))
                {
                    return await File.ReadAllBytesAsync(rawUrl);
                }
                return null;
            }

            var urlHash = ComputeMd5Hex(rawUrl);
            var cacheKey = $"{urlHash}_{(shouldCrop ? "c1" : "c0")}_{(shouldSharpen ? "s1" : "s0")}_{(shouldOpt ? "o1" : "o0")}_{maxDim}";

            if (!force && TryGetRamCache(cacheKey, out var ramCached) && ramCached != null && ramCached.Length > 0)
            {
                return ramCached;
            }

            var cacheFilePath = Path.Combine(_readerCacheDir, $"{cacheKey}.cache");

            if (!force && File.Exists(cacheFilePath))
            {
                try
                {
                    var cached = await File.ReadAllBytesAsync(cacheFilePath);
                    if (cached.Length > 0)
                    {
                        PutRamCache(cacheKey, cached);
                        return cached;
                    }
                }
                catch { }
            }

            // Check if we already have the raw downloaded image on disk
            byte[]? rawBytes = null;
            var rawFilePath = Path.Combine(_readerCacheDir, $"{urlHash}.raw");
            if (!force && File.Exists(rawFilePath))
            {
                try
                {
                    rawBytes = await File.ReadAllBytesAsync(rawFilePath);
                }
                catch { }
            }

            // If not found locally, download from source
            if (rawBytes == null || rawBytes.Length == 0)
            {
                await _pageDownloadThrottle.WaitAsync();
                try
                {
                    rawBytes = await DownloadImageWithHeadersAsync(rawUrl, sourceId);
                    if (rawBytes != null && rawBytes.Length > 0)
                    {
                        _ = Task.Run(async () =>
                        {
                            await WriteFileAtomicAsync(rawFilePath, rawBytes);
                        });
                    }
                }
                finally
                {
                    _pageDownloadThrottle.Release();
                }
            }

            if (rawBytes == null || rawBytes.Length == 0) return null;

            byte[] result = rawBytes;
            if (!CanUseZeroCopyFastPath(rawBytes, shouldCrop, shouldSharpen, shouldOpt))
            {
                var opts = new ImageProcessingOptions
                {
                    EnableOptimization = shouldOpt,
                    AutoCropBorders = shouldCrop,
                    EnableSharpening = shouldSharpen,
                    MaxDimension = maxDim
                };
                var sourceBytes = rawBytes;
                result = await Task.Run(() => _imageProcessor.ProcessPage(sourceBytes, opts));
            }

            PutRamCache(cacheKey, result);

            _ = Task.Run(async () =>
            {
                await WriteFileAtomicAsync(cacheFilePath, result);
            });

            return result;
        }

        private async Task<byte[]?> FetchIconBytesAsync(string pkg, string? iconUrl)
        {
            var safeName = string.Join("_", pkg.Split(Path.GetInvalidFileNameChars()));

            // 1. Check bundled Plugins/Catalog/icons/{safeName}.png
            var bundledCatalogIcon = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", "Catalog", "icons", $"{safeName}.png");
            if (File.Exists(bundledCatalogIcon))
            {
                try { return await File.ReadAllBytesAsync(bundledCatalogIcon); } catch { }
            }

            // 2. Check bundled Plugins/icons/{safeName}.png
            var bundledPluginsIcon = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", "icons", $"{safeName}.png");
            if (File.Exists(bundledPluginsIcon))
            {
                try { return await File.ReadAllBytesAsync(bundledPluginsIcon); } catch { }
            }

            // 3. Check AppData cache %AppData%/Yomic/Icons/{safeName}.png
            var iconPath = Path.Combine(_iconsDir, $"{safeName}.png");
            if (File.Exists(iconPath))
            {
                try { return await File.ReadAllBytesAsync(iconPath); } catch { }
            }

            // 4. Download from remote URL if provided
            if (!string.IsNullOrWhiteSpace(iconUrl))
            {
                var data = await DownloadImageWithHeadersAsync(iconUrl, 0);
                if (data != null && data.Length > 0)
                {
                    string tmpIcon = $"{iconPath}.{Guid.NewGuid():N}.tmp";
                    try
                    {
                        await File.WriteAllBytesAsync(tmpIcon, data);
                        File.Move(tmpIcon, iconPath, overwrite: true);
                    }
                    catch
                    {
                        try { if (File.Exists(tmpIcon)) File.Delete(tmpIcon); } catch { }
                    }
                    return data;
                }
            }

            return null;
        }

        private async Task<byte[]?> DownloadImageWithHeadersAsync(string rawUrl, long sourceId)
        {
            string requestUrl = rawUrl;
            var customHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (rawUrl.Contains('|'))
            {
                var parts = rawUrl.Split('|', StringSplitOptions.RemoveEmptyEntries);
                requestUrl = parts[0].Trim();
                for (int i = 1; i < parts.Length; i++)
                {
                    var pair = parts[i].Split('=', 2);
                    if (pair.Length == 2)
                    {
                        customHeaders[pair[0].Trim()] = pair[1].Trim();
                    }
                }
            }

            if (!Yomic.Core.Helpers.UrlSecurityHelper.IsSafePublicHttpUrl(requestUrl, out var targetUri) || targetUri == null)
            {
                LogService.Warning("ImageProxy", $"Blocked unsafe image proxy URL: {requestUrl}");
                return null;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, targetUri)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };

            // Referer header
            if (customHeaders.TryGetValue("Referer", out var customRef))
            {
                if (!string.Equals(customRef, "none", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(customRef, "null", StringComparison.OrdinalIgnoreCase) &&
                    Uri.TryCreate(customRef, UriKind.Absolute, out var refUri))
                {
                    req.Headers.Referrer = refUri;
                }
            }
            else
            {
                var src = sourceId != 0 ? _sourceManager.GetSource(sourceId) : null;
                if (src != null && !string.IsNullOrEmpty(src.BaseUrl) && Uri.TryCreate(src.BaseUrl, UriKind.Absolute, out var srcUri))
                {
                    req.Headers.Referrer = srcUri;
                }
                else if (Uri.TryCreate(requestUrl, UriKind.Absolute, out var reqUri))
                {
                    req.Headers.Referrer = new Uri(reqUri.GetLeftPart(UriPartial.Authority) + "/");
                }
            }

            if (customHeaders.TryGetValue("Origin", out var originVal))
            {
                req.Headers.TryAddWithoutValidation("Origin", originVal);
            }

            if (customHeaders.TryGetValue("User-Agent", out var ua1))
            {
                req.Headers.UserAgent.TryParseAdd(ua1);
            }
            else if (customHeaders.TryGetValue("UserAgent", out var ua2))
            {
                req.Headers.UserAgent.TryParseAdd(ua2);
            }
            else
            {
                req.Headers.UserAgent.TryParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            }

            // Inject Cloudflare cookies if present for host (thread-safe snapshot + RFC 6265 domain boundary check)
            if (CloudflareBypassService.IsInstanceCreated)
            {
                var savedCookies = CloudflareBypassService.Instance.SavedCookies;
                var relevantCookies = savedCookies
                    .Where(c => Yomic.Core.Helpers.UrlSecurityHelper.CookieMatchesRequest(targetUri, c.Domain, c.Secure ?? false))
                    .ToList();

                if (relevantCookies.Count > 0)
                {
                    var cookieHeader = string.Join("; ", relevantCookies.Select(c => $"{c.Name}={c.Value}"));
                    req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                }

                if (!string.IsNullOrEmpty(CloudflareBypassService.Instance.BypassUserAgent) &&
                    (relevantCookies.Count > 0 || requestUrl.Contains("nhentai", StringComparison.OrdinalIgnoreCase)))
                {
                    req.Headers.Remove("User-Agent");
                    req.Headers.TryAddWithoutValidation("User-Agent", CloudflareBypassService.Instance.BypassUserAgent);
                }
            }

            if (requestUrl.Contains("mangamillion", StringComparison.OrdinalIgnoreCase))
            {
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
                req.Headers.TryAddWithoutValidation("Origin", "https://mangamillion.shueisha.co.jp");
            }
            else
            {
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "image");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
            }

            req.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");

            if (_settingsService?.IsOfflineMode == true)
            {
                return null;
            }

            byte[]? data = null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var client = GetOrCreateHttpClient();
            using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Forbidden || IsGmbrProHost(requestUrl))
                {
                    var refHeader = req.Headers.Referrer?.ToString() ?? "https://www.manhwaindo.my/";
                    data = await DownloadWithCurlFallbackAsync(requestUrl, refHeader, cts.Token);
                }
                if (data == null || data.Length == 0)
                {
                    return null;
                }
            }
            else
            {
                data = await Yomic.Core.Helpers.UrlSecurityHelper.ReadContentBoundedAsync(
                    response.Content, 50L * 1024 * 1024, cts.Token);
            }

            // Decrypt AES-256-CBC if key and IV are passed in customHeaders (e.g. MangaMillion)
            if (data != null && data.Length > 0 &&
                customHeaders.TryGetValue("AesKey", out var aesKey) &&
                customHeaders.TryGetValue("AesIv", out var aesIv))
            {
                data = DecryptAes256Cbc(data, aesKey, aesIv);
            }

            // Reject HTTP-200 HTML error / Cloudflare challenge pages so they never poison disk image caches
            if (data != null && LooksLikeHtmlPayload(data))
            {
                return null;
            }

            return data;
        }

        private bool IsAllowedLocalImagePath(string candidatePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(candidatePath)) return false;
                string fullPath = Path.GetFullPath(candidatePath);

                string downloadsRoot = Path.GetFullPath(DownloadPathService.BaseDirectory);
                string cbzCacheRoot = Path.GetFullPath(DownloadPathService.CbzExtractCacheDirectory);
                string coversRoot = Path.GetFullPath(_coversDir);
                string cacheRoot = Path.GetFullPath(_readerCacheDir);
                string iconsRoot = Path.GetFullPath(_iconsDir);
                string bundledPluginsRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins"));

                bool inAllowedDir =
                    Yomic.Core.Helpers.UrlSecurityHelper.IsPathInsideDirectory(fullPath, downloadsRoot) ||
                    Yomic.Core.Helpers.UrlSecurityHelper.IsPathInsideDirectory(fullPath, cbzCacheRoot) ||
                    Yomic.Core.Helpers.UrlSecurityHelper.IsPathInsideDirectory(fullPath, coversRoot) ||
                    Yomic.Core.Helpers.UrlSecurityHelper.IsPathInsideDirectory(fullPath, cacheRoot) ||
                    Yomic.Core.Helpers.UrlSecurityHelper.IsPathInsideDirectory(fullPath, iconsRoot) ||
                    Yomic.Core.Helpers.UrlSecurityHelper.IsPathInsideDirectory(fullPath, bundledPluginsRoot);

                if (!inAllowedDir) return false;

                string ext = Path.GetExtension(fullPath);
                return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".avif", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".cache", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsGmbrProHost(string? rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl) || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
                return false;
            string host = uri.IdnHost.TrimEnd('.');
            return host.Equals("gmbr.pro", StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith(".gmbr.pro", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryNormalizeCurlTarget(Uri validUri, out string normalizedHost, out string normalizedUrl)
        {
            normalizedHost = validUri.IdnHost.TrimEnd('.');
            normalizedUrl = string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedHost)) return false;

            // Rebuild URI with trailing-dot-free host so curl --resolve ALWAYS matches the request host
            var builder = new UriBuilder(validUri)
            {
                Host = normalizedHost
            };
            normalizedUrl = builder.Uri.AbsoluteUri;
            return true;
        }

        private static async Task<byte[]?> DownloadWithCurlFallbackAsync(string url, string referer, CancellationToken ct)
        {
            try
            {
                if (!Yomic.Core.Helpers.UrlSecurityHelper.IsSafePublicHttpUrl(url, out var validUri) || validUri == null)
                {
                    return null;
                }

                // Resolve DNS up-front and pin a validated public IP via --resolve (host trailing-dot normalized on BOTH --resolve & URL)
                if (!TryNormalizeCurlTarget(validUri, out var host, out var normalizedUrl))
                {
                    return null;
                }
                IPAddress[] resolvedIps;
                try
                {
                    resolvedIps = await Dns.GetHostAddressesAsync(host, ct);
                }
                catch
                {
                    return null;
                }

                if (resolvedIps == null || resolvedIps.Length == 0)
                {
                    return null;
                }

                foreach (var ip in resolvedIps)
                {
                    if (Yomic.Core.Helpers.UrlSecurityHelper.IsPrivateOrLoopbackIp(ip))
                    {
                        return null;
                    }
                }

                var pinnedIp = resolvedIps.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                               ?? resolvedIps[0];
                string pinnedIpStr = pinnedIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? $"[{pinnedIp}]"
                    : pinnedIp.ToString();

                string safeReferer = (referer ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);

                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "curl.exe"),
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-f"); // Fail silently on HTTP >= 400 so HTML error pages never poison image caches
                psi.ArgumentList.Add("-s");
                // Do NOT pass -L (auto-redirect): prevents SSRF redirect bypasses to 127.0.0.1 / LAN via curl.exe
                psi.ArgumentList.Add("--resolve");
                psi.ArgumentList.Add($"{host}:{validUri.Port}:{pinnedIpStr}");
                psi.ArgumentList.Add("--max-filesize");
                psi.ArgumentList.Add((50L * 1024 * 1024).ToString());
                psi.ArgumentList.Add("--max-time");
                psi.ArgumentList.Add("20");
                psi.ArgumentList.Add("-H");
                psi.ArgumentList.Add($"Referer: {safeReferer}");
                psi.ArgumentList.Add("-A");
                psi.ArgumentList.Add("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                psi.ArgumentList.Add(normalizedUrl);

                using var proc = Process.Start(psi);
                if (proc == null) return null;
                try
                {
                    const long MaxCurlImageBytes = 50L * 1024 * 1024;
                    using var ms = new MemoryStream();
                    var buf = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await proc.StandardOutput.BaseStream.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                    {
                        total += read;
                        if (total > MaxCurlImageBytes)
                        {
                            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                            return null;
                        }
                        ms.Write(buf, 0, read);
                    }
                    await proc.WaitForExitAsync(ct);
                    if (proc.ExitCode != 0 || ms.Length == 0) return null;

                    var result = ms.ToArray();
                    // Reject HTML error / Cloudflare challenge pages masquerading as images
                    if (LooksLikeHtmlPayload(result)) return null;
                    return result;
                }
                catch
                {
                    try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                    return null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static bool LooksLikeHtmlPayload(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 5) return false;
            int idx = 0;
            while (idx < bytes.Length && idx < 64 && (bytes[idx] == ' ' || bytes[idx] == '\t' || bytes[idx] == '\r' || bytes[idx] == '\n'))
            {
                idx++;
            }
            if (idx >= bytes.Length || bytes[idx] != (byte)'<') return false;
            string prefix = Encoding.ASCII.GetString(bytes, idx, Math.Min(32, bytes.Length - idx)).ToLowerInvariant();
            return prefix.StartsWith("<!doctype") || prefix.StartsWith("<html") || prefix.StartsWith("<head") || prefix.StartsWith("<body") || prefix.StartsWith("<!--");
        }

        private static byte[] DecryptAes256Cbc(byte[] cipherText, string keyHex, string ivHex)
        {
            try
            {
                byte[] key = Convert.FromHexString(keyHex);
                byte[] iv = Convert.FromHexString(ivHex);
                using var aes = Aes.Create();
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using var decryptor = aes.CreateDecryptor();
                return decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);
            }
            catch
            {
                return cipherText;
            }
        }

        internal static string DetectContentType(byte[]? bytes, string urlOrPath)
        {
            if (bytes != null && bytes.Length >= 12)
            {
                // PNG: 89 50 4E 47
                if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                    return "image/png";
                // JPEG: FF D8 FF
                if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                    return "image/jpeg";
                // GIF: 47 49 46 38
                if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
                    return "image/gif";
                // WEBP: RIFF....WEBP
                if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                    bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
                    return "image/webp";
                // AVIF: ....ftypavif
                if (bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
                    return "image/avif";
            }

            if (urlOrPath.Contains(".png", StringComparison.OrdinalIgnoreCase)) return "image/png";
            if (urlOrPath.Contains(".webp", StringComparison.OrdinalIgnoreCase)) return "image/webp";
            if (urlOrPath.Contains(".avif", StringComparison.OrdinalIgnoreCase)) return "image/avif";
            if (urlOrPath.Contains(".gif", StringComparison.OrdinalIgnoreCase)) return "image/gif";
            // Never serve untrusted remote SVG as active image/svg+xml (prevents embedded script/XSS payloads)
            if (urlOrPath.Contains(".svg", StringComparison.OrdinalIgnoreCase)) return "application/octet-stream";
            return "image/jpeg";
        }

        private static string ComputeMd5Hex(string input)
        {
            var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static Dictionary<string, string> ParseQueryString(string query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(query)) return dict;
            var trimmed = query.TrimStart('?');
            foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                var key = Uri.UnescapeDataString(kv[0].Replace('+', ' '));
                var val = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : string.Empty;
                dict[key] = val;
            }
            return dict;
        }
    }
}
