using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Net.Http;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net;
using Yomic.Core.Services;
using Polly;
using Polly.Retry;
using System.Text.Json.Nodes;

namespace Yomic.Core.Sources
{
    public abstract class HttpSource : IMangaSource, IDisposable
    {
        public static bool SilenceLogs { get; set; } = false;

        /// <summary>
        /// Unique stable ID generated from the class name.
        /// This ensures no collision between extensions from different developers.
        /// </summary>
        public virtual long Id => GenerateStableId();
        
        private long GenerateStableId()
        {
            var name = GetType().FullName ?? GetType().Name;
            var bytes = System.Text.Encoding.UTF8.GetBytes(name);
            return (long)System.IO.Hashing.XxHash64.HashToUInt64(bytes);
        }
        
        public abstract string Name { get; }
        public abstract string BaseUrl { get; }
        public virtual string ApiUrl => "";
        public virtual string Language => "EN"; // Default to EN
        public virtual bool IsHasMorePages => true;
        public virtual int RateLimitDelayMs => 1000;
        
        public virtual string Version => "1.0.0";
        public virtual string IconUrl => "";
        public virtual string Description => "Manga Source";
        public virtual string Author => "Unknown";
        public virtual string IconBackground => "#313244";
        public virtual string IconForeground => "#FF9900";
        
        // DoH Client for bootstrapping resolution
        private static readonly HttpClient _dohClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        // Dynamic client management
        private HttpClient? _client;
        protected readonly CookieContainer _cookieContainer = new CookieContainer();
        
        /// <summary>
        /// Public accessor for cookie container, allowing external cookie injection (e.g., after Cloudflare CAPTCHA solving).
        /// </summary>
        public CookieContainer CookieContainer => _cookieContainer;
        
        /// <summary>
        /// Override to true in sources that require VPN proxy.
        /// Default is false, meaning most sources use direct connection with DoH.
        /// </summary>
        public virtual bool RequiresProxy => false;
        public virtual bool IsNsfw => false;

        private readonly object _clientLock = new object();

        // Property that gets/creates HttpClient safely across concurrent threads
        protected HttpClient Client
        {
            get
            {
                if (_client != null) return _client;
                lock (_clientLock)
                {
                    if (_client == null)
                    {
                        var created = CreateHttpClient();
                        // Configure headers BEFORE publishing _client to other threads
                        ConfigureClient(created);
                        _client = created;
                        if (!SilenceLogs) Console.WriteLine("[HttpSource] HttpClient created");
                    }
                    return _client;
                }
            }
        }

        /// <summary>
        /// Virtual method for extensions to configure the HttpClient with custom headers.
        /// Called every time the HttpClient is created/recreated.
        /// </summary>
        protected virtual void ConfigureClient(HttpClient client)
        {
            // Base implementation - extensions can override to add Origin, Referer, etc.
        }

        public static Func<bool>? IsOfflineModeProvider { get; set; }
        public static Func<int>? DnsOverHttpsProviderGetter { get; set; }
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (List<System.Net.IPAddress> Ips, DateTime ExpiresUtc)> _dohDnsCache =
            new(StringComparer.OrdinalIgnoreCase);

        internal static void ClearDnsCache() => _dohDnsCache.Clear();

        protected HttpSource()
        {
            // Client will be created lazily on first access via the Client property
        }
        
        private HttpClient CreateHttpClient()
        {
            SocketsHttpHandler handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, token) =>
                {
                    if (IsOfflineModeProvider?.Invoke() == true)
                    {
                        throw new HttpRequestException("Offline Mode is enabled.");
                    }

                    var host = context.DnsEndPoint.Host;
                    var candidateIps = new List<System.Net.IPAddress>();
                    var logBuilder = new System.Text.StringBuilder();

                    int dohProvider = DnsOverHttpsProviderGetter?.Invoke() ?? 2;
                    if (System.Net.IPAddress.TryParse(host, out var literalIp))
                    {
                        candidateIps.Add(literalIp);
                    }
                    else if (dohProvider != 0)
                    {
                        string cacheKey = $"{dohProvider}:{host}";
                        if (_dohDnsCache.TryGetValue(cacheKey, out var cachedDns) && cachedDns.ExpiresUtc > DateTime.UtcNow && cachedDns.Ips.Count > 0)
                        {
                            candidateIps.AddRange(cachedDns.Ips);
                        }
                        else
                        {
                            var dohBases = dohProvider switch
                            {
                                1 => new[] { "https://1.1.1.1/dns-query?name={0}&type={1}", "https://cloudflare-dns.com/dns-query?name={0}&type={1}" },
                                3 => new[] { "https://dns.adguard-dns.com/resolve?name={0}&type={1}", "https://94.140.14.14/resolve?name={0}&type={1}" },
                                _ => new[] { "https://8.8.8.8/resolve?name={0}&type={1}", "https://dns.google/resolve?name={0}&type={1}", "https://cloudflare-dns.com/dns-query?name={0}&type={1}" }
                            };

                            // Query A and AAAA in parallel against the first healthy DoH provider (HTTP 200 JSON)
                            // so IPv4-only sites never incur 4 sequential DoH round-trips per connection.
                            foreach (var template in dohBases)
                            {
                                async Task<(List<System.Net.IPAddress> Found, bool Ok, string? ErrorLog)> QueryTypeAsync(string qType)
                                {
                                    var found = new List<System.Net.IPAddress>();
                                    try
                                    {
                                        string dohUrl = string.Format(template, Uri.EscapeDataString(host), qType);
                                        using var request = new HttpRequestMessage(HttpMethod.Get, dohUrl);
                                        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/dns-json"));

                                        using var response = await _dohClient.SendAsync(request, token);
                                        if (!response.IsSuccessStatusCode) return (found, false, null);

                                        var jsonBytes = await Yomic.Core.Helpers.UrlSecurityHelper.ReadContentBoundedAsync(response.Content, 256 * 1024, token);
                                        var json = System.Text.Encoding.UTF8.GetString(jsonBytes);
                                        var obj = JsonNode.Parse(json);
                                        var answers = obj?["Answer"] as JsonArray;
                                        if (answers != null)
                                        {
                                            foreach (var ans in answers)
                                            {
                                                var ipStr = ans?["data"]?.ToString();
                                                if (System.Net.IPAddress.TryParse(ipStr, out var parsedIp) && !found.Contains(parsedIp))
                                                {
                                                    found.Add(parsedIp);
                                                }
                                            }
                                        }
                                        return (found, true, null);
                                    }
                                    catch (Exception ex)
                                    {
                                        return (found, false, $"[DoH] Failed {template} ({qType}): {ex.Message}");
                                    }
                                }

                                var aTask = QueryTypeAsync("A");
                                var aaaaTask = QueryTypeAsync("AAAA");
                                await Task.WhenAll(aTask, aaaaTask);
                                var aRes = await aTask;
                                var aaaaRes = await aaaaTask;

                                // Append to non-thread-safe StringBuilder strictly sequentially after Task.WhenAll
                                if (!string.IsNullOrEmpty(aRes.ErrorLog)) logBuilder.AppendLine(aRes.ErrorLog);
                                if (!string.IsNullOrEmpty(aaaaRes.ErrorLog)) logBuilder.AppendLine(aaaaRes.ErrorLog);

                                foreach (var ip in aRes.Found.Concat(aaaaRes.Found))
                                {
                                    if (!candidateIps.Contains(ip))
                                    {
                                        candidateIps.Add(ip);
                                        logBuilder.AppendLine($"[DoH] Resolved {host} -> {ip}");
                                    }
                                }

                                // Only stop failover if we actually resolved IPs OR both A & AAAA queries succeeded cleanly
                                if (candidateIps.Count > 0 || (aRes.Ok && aaaaRes.Ok)) break;
                            }

                            if (candidateIps.Count > 0)
                            {
                                if (_dohDnsCache.Count >= 512) _dohDnsCache.Clear();
                                _dohDnsCache[cacheKey] = (new List<System.Net.IPAddress>(candidateIps), DateTime.UtcNow.AddMinutes(5));
                            }
                        }
                    }

                    // Fall back to System DNS ONLY when DoH yielded 0 IPs (prevents leaking DNS queries to ISP)
                    if (candidateIps.Count == 0)
                    {
                        try
                        {
                            var hostAddresses = await System.Net.Dns.GetHostAddressesAsync(host, token);
                            foreach (var ip in hostAddresses)
                            {
                                if (!candidateIps.Contains(ip))
                                {
                                    candidateIps.Add(ip);
                                }
                            }
                        }
                        catch { }
                    }
                    
                    if (!SilenceLogs) System.Diagnostics.Debug.WriteLine(logBuilder.ToString());
                    if (candidateIps.Count == 0) 
                    {
                        if (!SilenceLogs) Console.WriteLine($"[DoH] FAILED resolving {host}!");
                        throw new Exception($"Could not resolve host: {host}\nLogs:\n{logBuilder}");
                    }

                    // SSRF socket guard: refuse connecting to loopback/private/link-local/metadata IPs
                    var publicIps = candidateIps.Where(ip => !Yomic.Core.Helpers.UrlSecurityHelper.IsPrivateOrLoopbackIp(ip)).ToList();
                    if (publicIps.Count == 0)
                    {
                        throw new HttpRequestException($"Blocked connection to private/loopback IP for host: {host}");
                    }

                    Exception? lastConnectEx = null;
                    foreach (var ip in publicIps)
                    {
                        var socket = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                        try
                        {
                            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                            attemptCts.CancelAfter(TimeSpan.FromSeconds(3)); // 3s cap per IP attempt to quickly bypass unresponsive routes

                            await socket.ConnectAsync(ip, context.DnsEndPoint.Port, attemptCts.Token);
                            var networkStream = new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                            return new DpiBypassStream(networkStream);
                        }
                        catch (Exception ex)
                        {
                            socket.Dispose();
                            lastConnectEx = ex;
                            if (token.IsCancellationRequested) throw;
                        }
                    }

                    throw lastConnectEx ?? new Exception($"Failed to connect to any resolved IP for {host}");
                },
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
                },
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                UseCookies = true,
                CookieContainer = _cookieContainer,
                AllowAutoRedirect = false
            };
            
            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(30); // Increased timeout to 30s for slow sites/proxies
            
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            
            return client;
        }

        public abstract Task<List<Models.Manga>> GetPopularMangaAsync(int page);
        public abstract Task<List<Models.Manga>> GetSearchMangaAsync(string query, int page);
        public abstract Task<Models.Manga> GetMangaDetailsAsync(string url);
        public abstract Task<List<Models.Chapter>> GetChapterListAsync(string mangaUrl);
        public abstract Task<List<string>> GetPageListAsync(string chapterUrl);

        public virtual async Task<List<Models.Chapter>> GetLatestChaptersAsync(string mangaUrl)
        {
            return await GetChapterListAsync(mangaUrl);
        }

        public virtual async Task<ChapterListCacheResult> GetChapterListWithCacheAsync(string mangaUrl, string? eTag, string? lastModified)
        {
            var chapters = await GetLatestChaptersAsync(mangaUrl);
            return new ChapterListCacheResult { Chapters = chapters };
        }

        protected async Task<string> GetStringAsync(string url)
        {
            if (IsOfflineModeProvider?.Invoke() == true)
            {
                throw new HttpRequestException("Offline Mode is enabled.");
            }

            try
            {
                return await GetStringAsyncInternal(url);
            }
            catch (Exception ex)
            {
                WriteToLog($"Request Error ({ex.GetType().Name}): {ex.Message}");
                if (IsOfflineModeProvider?.Invoke() == true)
                {
                    throw;
                }
                if (ex is System.IO.InvalidDataException || ex is OperationCanceledException)
                {
                    WriteToLog($"Non-Cloudflare failure ({ex.GetType().Name}). Skipping Cloudflare Fallback.");
                    throw;
                }
                if (ex is HttpRequestException httpEx)
                {
                    if (IsSecurityOrOfflineRejection(httpEx))
                    {
                        WriteToLog("Blocked/Offline request error. Skipping Cloudflare Fallback.");
                        throw;
                    }
                    var code = httpEx.StatusCode;
                    bool isCloudflareLike =
                        code == System.Net.HttpStatusCode.Forbidden ||
                        code == System.Net.HttpStatusCode.ServiceUnavailable ||
                        code == System.Net.HttpStatusCode.TooManyRequests;
                    if (code.HasValue && !isCloudflareLike)
                    {
                        WriteToLog($"HTTP {(int)code.Value} ({code.Value}). Skipping Cloudflare Fallback.");
                        throw;
                    }
                }
                WriteToLog("Attempting Cloudflare/Puppeteer Fallback...");
                
                // FAST PATH: Only route directly to headless fetch when pre-solved cookies match THIS target URL's host
                if (CloudflareBypassService.IsInstanceCreated && CloudflareBypassService.Instance.HasPreSolvedCookiesFor(url))
                {
                    WriteToLog("Pre-solved cookies detected for target host! Skipping to HEADLESS BROWSER FETCH...");
                    return await CloudflareBypassService.Instance.GetContentAsync(url);
                }

                try
                {
                    // Strategy 1: Update Tokens and Retry HTTP
                    WriteToLog($"Step 1: Fetching Tokens via Headless Browser from {BaseUrl}...");
                    var (ua, cookies) = await CloudflareBypassService.Instance.GetTokensAsync(BaseUrl);
                    if (cookies != null && cookies.Count > 0)
                    {
                         var targetHost = new Uri(BaseUrl).Host;
                         foreach (var kv in cookies)
                         {
                             try
                             {
                                 _cookieContainer.Add(new System.Net.Cookie(kv.Key, kv.Value, "/", targetHost));
                             }
                             catch (System.Net.CookieException)
                             {
                                 // Ignore malformed cookie values containing commas/semicolons
                             }
                         }
                    }

                    // Retry Internal (passing UA per-request without mutating shared DefaultRequestHeaders)
                    WriteToLog("Step 2: Retrying HTTP Request with new tokens...");
                    return await GetStringAsyncInternal(url, ua);
                }
                catch (Exception retryEx)
                {
                     WriteToLog($"HTTP Retry Failed: {retryEx.Message}");
                     WriteToLog("Step 3: Switching to HEADLESS BROWSER FETCH (Slow but Reliable)");
                     
                     // Strategy 2: Fetch Content directly via Puppeteer
                     return await CloudflareBypassService.Instance.GetContentAsync(url);
                }
            }
        }

        private static bool IsSecurityOrOfflineRejection(HttpRequestException ex)
        {
            if (string.IsNullOrEmpty(ex.Message)) return false;
            return ex.Message.Contains("Offline Mode", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("Blocked ", StringComparison.OrdinalIgnoreCase);
        }

        private static readonly ResiliencePipeline _httpRetryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>(ex =>
                    {
                        if (IsSecurityOrOfflineRejection(ex)) return false;
                        if (ex.StatusCode is System.Net.HttpStatusCode code)
                        {
                            int status = (int)code;
                            if (status >= 400 && status < 500 &&
                                code != System.Net.HttpStatusCode.RequestTimeout &&
                                code != System.Net.HttpStatusCode.TooManyRequests)
                            {
                                return false;
                            }
                        }
                        return true;
                    })
                    .Handle<TaskCanceledException>()
                    .Handle<TimeoutException>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(500),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    if (!SilenceLogs) Console.WriteLine($"[Polly Retry #{args.AttemptNumber}] Network glitch: {args.Outcome.Exception?.Message}. Retrying in {args.RetryDelay.TotalMilliseconds:F0}ms...");
                    return default;
                }
            })
            .Build();

        internal static bool IsSameOrigin(Uri a, Uri b)
        {
            return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(a.IdnHost.TrimEnd('.'), b.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) &&
                   a.Port == b.Port;
        }

        /// <summary>
        /// Sends an HTTP request and follows up to <paramref name="maxRedirects"/> redirects manually,
        /// validating every hop with <see cref="Yomic.Core.Helpers.UrlSecurityHelper.IsSafePublicHttpUrl"/>
        /// and flagging cross-origin hops so callers never leak raw Cookie or Authorization headers across origins.
        /// </summary>
        protected Task<HttpResponseMessage> SendWithSafeRedirectsAsync(
            Uri initialUri,
            HttpMethod initialMethod,
            Func<Uri, HttpMethod, bool, HttpRequestMessage> requestFactory,
            CancellationToken cancellationToken = default,
            int maxRedirects = 5)
            => SendWithSafeRedirectsCoreAsync(Client, initialUri, initialMethod, requestFactory, cancellationToken, maxRedirects);

        internal static async Task<HttpResponseMessage> SendWithSafeRedirectsCoreAsync(
            HttpClient client,
            Uri initialUri,
            HttpMethod initialMethod,
            Func<Uri, HttpMethod, bool, HttpRequestMessage> requestFactory,
            CancellationToken cancellationToken = default,
            int maxRedirects = 5)
        {
            Uri currentUri = initialUri;
            HttpMethod currentMethod = initialMethod;

            for (int hop = 0; hop <= maxRedirects; hop++)
            {
                if (!Yomic.Core.Helpers.UrlSecurityHelper.IsSafePublicHttpUrl(currentUri.AbsoluteUri, out var safeUri) || safeUri == null)
                {
                    throw new HttpRequestException($"Blocked redirect to unsafe URL: {currentUri}");
                }

                bool isCrossOrigin = hop > 0 && !IsSameOrigin(initialUri, safeUri);
                using var req = requestFactory(safeUri, currentMethod, isCrossOrigin);
                var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                int status = (int)response.StatusCode;

                if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location != null)
                {
                    var location = response.Headers.Location;
                    var nextUri = location.IsAbsoluteUri ? location : new Uri(safeUri, location);
                    response.Dispose();
                    if (hop == maxRedirects)
                    {
                        throw new HttpRequestException($"Too many redirects while fetching: {initialUri}");
                    }
                    if (status == 303 || ((status == 301 || status == 302) && currentMethod == HttpMethod.Post))
                    {
                        currentMethod = HttpMethod.Get;
                    }
                    currentUri = nextUri;
                    continue;
                }

                return response;
            }

            throw new HttpRequestException($"Failed to complete request after redirects: {initialUri}");
        }

        private async Task<string> GetStringAsyncInternal(string url, string? overrideUserAgent = null)
        {
            if (IsOfflineModeProvider?.Invoke() == true)
            {
                throw new HttpRequestException("Offline Mode is enabled.");
            }
            if (!Yomic.Core.Helpers.UrlSecurityHelper.IsSafePublicHttpUrl(url, out var validatedUri) || validatedUri == null)
            {
                throw new HttpRequestException($"Blocked unsafe outbound URL: {url}");
            }

            return await _httpRetryPipeline.ExecuteAsync(async (context) =>
            {
                WriteToLog($"Downloading: {url}");
                using var response = await SendWithSafeRedirectsAsync(
                    validatedUri,
                    HttpMethod.Get,
                    (hopUri, hopMethod, _) =>
                    {
                        var req = new HttpRequestMessage(hopMethod, hopUri);
                        if (!string.IsNullOrWhiteSpace(BaseUrl) && Uri.TryCreate(BaseUrl, UriKind.Absolute, out var refUri))
                        {
                            req.Headers.Referrer = refUri;
                        }
                        if (!string.IsNullOrWhiteSpace(overrideUserAgent))
                        {
                            req.Headers.TryAddWithoutValidation("User-Agent", overrideUserAgent);
                        }
                        return req;
                    });
                
                if (!response.IsSuccessStatusCode)
                {
                    var errBytes = await Yomic.Core.Helpers.UrlSecurityHelper.ReadContentBoundedAsync(response.Content, 256 * 1024);
                    var errorContent = System.Text.Encoding.UTF8.GetString(errBytes);
                    var preview = new string(errorContent.Take(200).ToArray());
                    WriteToLog($"Failed: {response.StatusCode}. Content: {preview}..."); 
                    throw new HttpRequestException($"Request failed with {response.StatusCode}", null, response.StatusCode); 
                }
                
                WriteToLog($"Success! ({response.Content.Headers.ContentLength} bytes)");
                var bodyBytes = await Yomic.Core.Helpers.UrlSecurityHelper.ReadContentBoundedAsync(response.Content, 15L * 1024 * 1024);
                return System.Text.Encoding.UTF8.GetString(bodyBytes);
            });
        }

        private void WriteToLog(string message)
        {
            if (SilenceLogs) return;
            string log = $"[{DateTime.Now:HH:mm:ss}] [HttpSource] {message}";
            Console.WriteLine(log);
        }

        protected Task<string> ForceBrowserFetchAsync(string url)
        {
            return ForceBrowserFetchAsync(url, null);
        }

        protected async Task<string> ForceBrowserFetchAsync(string url, string? waitForSelector = null)
        {
            if (IsOfflineModeProvider?.Invoke() == true)
            {
                throw new HttpRequestException("Offline Mode is enabled.");
            }
            WriteToLog($"FORCING HEADLESS BROWSER FETCH for: {url} (Wait: {waitForSelector ?? "None"})");
            return await CloudflareBypassService.Instance.GetContentAsync(url, waitForSelector);
        }

        private bool _isDisposed;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            if (disposing)
            {
                HttpClient? clientToDispose;
                lock (_clientLock)
                {
                    clientToDispose = _client;
                    _client = null;
                }
                try { clientToDispose?.Dispose(); } catch { }
            }
        }
    }

    public class DpiBypassStream : Stream
    {
        private readonly Stream _innerStream;
        private volatile bool _isFirstWrite = true;

        public DpiBypassStream(Stream innerStream)
        {
            _innerStream = innerStream;
        }

        public override bool CanRead => _innerStream.CanRead;
        public override bool CanSeek => _innerStream.CanSeek;
        public override bool CanWrite => _innerStream.CanWrite;
        public override long Length => _innerStream.Length;
        public override long Position
        {
            get => _innerStream.Position;
            set => _innerStream.Position = value;
        }

        public override void Flush() => _innerStream.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _innerStream.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => _innerStream.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _innerStream.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _innerStream.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);
        public override void SetLength(long value) => _innerStream.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_isFirstWrite && count > 5 && buffer[offset] == 0x16)
            {
                _isFirstWrite = false;
                _innerStream.Write(buffer, offset, 5);
                _innerStream.Flush();
                _innerStream.Write(buffer, offset + 5, count - 5);
            }
            else
            {
                _innerStream.Write(buffer, offset, count);
            }
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_isFirstWrite && count > 5 && buffer[offset] == 0x16)
            {
                _isFirstWrite = false;
                await _innerStream.WriteAsync(buffer.AsMemory(offset, 5), cancellationToken);
                await _innerStream.FlushAsync(cancellationToken);
                await _innerStream.WriteAsync(buffer.AsMemory(offset + 5, count - 5), cancellationToken);
            }
            else
            {
                await _innerStream.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_isFirstWrite && buffer.Length > 5 && buffer.Span[0] == 0x16)
            {
                _isFirstWrite = false;
                await _innerStream.WriteAsync(buffer.Slice(0, 5), cancellationToken);
                await _innerStream.FlushAsync(cancellationToken);
                await _innerStream.WriteAsync(buffer.Slice(5), cancellationToken);
            }
            else
            {
                await _innerStream.WriteAsync(buffer, cancellationToken);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _innerStream.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
