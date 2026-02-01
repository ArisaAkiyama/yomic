using System;
using System.Net.Http;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.Net;
using MyMangaApp.Core.Services;

namespace MyMangaApp.Core.Sources
{
    public abstract class HttpSource : IMangaSource
    {
        public abstract long Id { get; }
        public abstract string Name { get; }
        public abstract string BaseUrl { get; }
        public virtual string Language => "EN"; // Default to EN
        public virtual bool IsHasMorePages => true;
        
        // DoH Client for bootstrapping resolution
        private static readonly HttpClient _dohClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        // Dynamic client management
        private HttpClient? _client;
        private bool _lastProxyState = false;
        protected readonly CookieContainer _cookieContainer = new CookieContainer();
        
        // Property that gets/creates HttpClient with current proxy state
        protected HttpClient Client
        {
            get
            {
                bool currentProxyState = SingboxService.Instance.IsRunning;
                
                // Recreate client if proxy state changed or client doesn't exist
                if (_client == null || currentProxyState != _lastProxyState)
                {
                    _client?.Dispose();
                    _client = CreateHttpClient(currentProxyState);
                    _lastProxyState = currentProxyState;
                    Console.WriteLine($"[HttpSource] HttpClient created with proxy: {currentProxyState}");
                }
                
                return _client;
            }
        }

        protected HttpSource()
        {
            // Client will be created lazily on first access via the Client property
        }
        
        private HttpClient CreateHttpClient(bool useProxy)
        {
            SocketsHttpHandler handler;
            
            if (useProxy)
            {
                // When using SOCKS5 proxy, don't use custom ConnectCallback
                Console.WriteLine($"[HttpSource] Creating client with SOCKS5 proxy at {SingboxService.Instance.ProxyAddress}:{SingboxService.Instance.ProxyPort}");
                handler = new SocketsHttpHandler
                {
                    Proxy = new WebProxy($"socks5://{SingboxService.Instance.ProxyAddress}:{SingboxService.Instance.ProxyPort}"),
                    UseProxy = true,
                    SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = delegate { return true; },
                        EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
                    },
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                    UseCookies = true,
                    CookieContainer = _cookieContainer
                };
            }
            else
            {
                // When not using proxy, use custom DoH ConnectCallback
                handler = new SocketsHttpHandler
                {
                    ConnectCallback = async (context, token) =>
                    {
                        var host = context.DnsEndPoint.Host;
                        System.Net.IPAddress? ipAddress = null;
                        var logBuilder = new System.Text.StringBuilder();

                        var dohQueries = new[]
                        {
                            (Provider: "https://cloudflare-dns.com/dns-query?name={0}&type=A", Type: "IPv4"),
                            (Provider: "https://dns.google/resolve?name={0}&type=A", Type: "IPv4"),
                        };

                        foreach (var (template, type) in dohQueries)
                        {
                            try
                            {
                                string dohUrl = string.Format(template, host);
                                var request = new HttpRequestMessage(HttpMethod.Get, dohUrl);
                                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/dns-json"));
                                
                                var response = await _dohClient.SendAsync(request, token);
                                if (response.IsSuccessStatusCode)
                                {
                                    var json = await response.Content.ReadAsStringAsync(token);
                                    var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                                    var answer = obj["Answer"];
                                    if (answer != null && answer.HasValues)
                                    {
                                        var ipStr = answer.First?["data"]?.ToString();
                                        if (System.Net.IPAddress.TryParse(ipStr, out var parsedIp))
                                        {
                                            ipAddress = parsedIp;
                                            logBuilder.AppendLine($"[DoH] Resolved {host} ({type}) via {dohUrl} -> {ipAddress}");
                                            break;
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                logBuilder.AppendLine($"[DoH] Failed {template}: {ex.Message}");
                            }
                        }

                        if (ipAddress == null)
                        {
                            logBuilder.AppendLine("[DoH] All providers failed. Falling back to System DNS.");
                            var entry = await System.Net.Dns.GetHostEntryAsync(host, token);
                            ipAddress = entry.AddressList.FirstOrDefault();
                        }
                        
                        System.Diagnostics.Debug.WriteLine(logBuilder.ToString());
                        if (ipAddress == null) 
                        {
                            Console.WriteLine($"[DoH] FAILED resolving {host}!");
                            throw new Exception($"Could not resolve host: {host}\nLogs:\n{logBuilder}");
                        }

                        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                        try
                        {
                            await socket.ConnectAsync(ipAddress, context.DnsEndPoint.Port, token);
                            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                        }
                        catch
                        {
                            socket.Dispose();
                            throw;
                        }
                    },
                    SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = delegate { return true; },
                        EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
                    },
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                    UseCookies = true,
                    CookieContainer = _cookieContainer
                };
            }
            
            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(15); // Increased timeout for proxy
            
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            
            return client;
        }

        public abstract Task<List<Models.Manga>> GetPopularMangaAsync(int page);
        public abstract Task<List<Models.Manga>> GetSearchMangaAsync(string query, int page);
        public abstract Task<Models.Manga> GetMangaDetailsAsync(string url);
        public abstract Task<List<Models.Chapter>> GetChapterListAsync(string mangaUrl);
        public abstract Task<List<string>> GetPageListAsync(string chapterUrl);

        protected async Task<string> GetStringAsync(string url)
        {
            try
            {
                return await GetStringAsyncInternal(url);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [HttpSource] Request Error ({ex.GetType().Name}): {ex.Message}");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [HttpSource] Attempting Cloudflare/Puppeteer Fallback...");
                
                try
                {
                    // Strategy 1: Update Tokens and Retry HTTP
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [HttpSource] Step 1: Fetching Tokens via Headless Browser from {BaseUrl}...");
                    var (ua, cookies) = await CloudflareBypassService.Instance.GetTokensAsync(BaseUrl);
                    
                    if (!string.IsNullOrEmpty(ua)) Client.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
                    if (cookies != null && cookies.Count > 0)
                    {
                         // _cookieContainer = new CookieContainer(); // Cannot reassign readonly
                         var targetHost = new Uri(BaseUrl).Host;
                         foreach (var kv in cookies)
                         {
                             _cookieContainer.Add(new System.Net.Cookie(kv.Key, kv.Value, "/", targetHost));
                         }
                    }

                    // Retry Internal
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [HttpSource] Step 2: Retrying HTTP Request with new tokens...");
                    return await GetStringAsyncInternal(url);
                }
                catch (Exception retryEx)
                {
                     Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [HttpSource] HTTP Retry Failed: {retryEx.Message}");
                     Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [HttpSource] Step 3: Switching to HEADLESS BROWSER FETCH (Slow but Reliable)");
                     
                     // Strategy 2: Fetch Content directly via Puppeteer
                     return await CloudflareBypassService.Instance.GetContentAsync(url);
                }
            }
        }

        private async Task<string> GetStringAsyncInternal(string url)
        {
            // Ensure Referer is set to BaseUrl (Important for hotlink protection/anti-bot)
            if (Client.DefaultRequestHeaders.Referrer == null)
            {
                try { Client.DefaultRequestHeaders.Referrer = new Uri(BaseUrl); } catch { }
            }

            WriteToLog($"Downloading: {url}");
            var response = await Client.GetAsync(url);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                var preview = new string(errorContent.Take(200).ToArray());
                WriteToLog($"Failed: {response.StatusCode}. Content: {preview}..."); 
                throw new HttpRequestException($"Request failed with {response.StatusCode}", null, response.StatusCode); 
            }
            
            WriteToLog($"Success! ({response.Content.Headers.ContentLength} bytes)");
            return await response.Content.ReadAsStringAsync();
        }

        private void WriteToLog(string message)
        {
            string log = $"[{DateTime.Now:HH:mm:ss}] [HttpSource] {message}";
            Console.WriteLine(log);
        }

        protected Task<string> ForceBrowserFetchAsync(string url)
        {
            return ForceBrowserFetchAsync(url, null);
        }

        protected async Task<string> ForceBrowserFetchAsync(string url, string? waitForSelector = null)
        {
             Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [HttpSource] FORCING HEADLESS BROWSER FETCH for: {url} (Wait: {waitForSelector ?? "None"})");
             return await CloudflareBypassService.Instance.GetContentAsync(url, waitForSelector);
        }
    }
}
