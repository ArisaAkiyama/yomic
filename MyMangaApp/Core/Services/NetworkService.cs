using System;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using ReactiveUI;
using Avalonia.Threading;

namespace MyMangaApp.Core.Services
{
    public class NetworkService : ReactiveObject
    {
        private bool _isOnline = true;
        public bool IsOnline
        {
            get => _isOnline;
            set => this.RaiseAndSetIfChanged(ref _isOnline, value);
        }

        public bool IsInternetAvailable => IsOnline;

        public event EventHandler<bool>? StatusChanged;
        
        private Timer? _pollingTimer;
        
        // Debounce/Grace period settings
        private int _consecutiveFailures = 0;
        private const int FailureThreshold = 2; // Increased to 2 to prevent flaky notifications
        private const int PollingIntervalSeconds = 3; // More frequent checks (was 5)

        private readonly SettingsService _settingsService;

        public NetworkService(SettingsService settingsService)
        {
            _settingsService = settingsService;

            // Initial check
            _ = CheckConnectivityAsync();

            // Hook into network availability changes (backup)
            // Hook into network availability changes
            NetworkChange.NetworkAvailabilityChanged += (s, e) =>
            {
                if (!e.IsAvailable)
                {
                    // OS reports network lost - Go offline immediately irrespective of threshold
                    Dispatcher.UIThread.Post(() => 
                    {
                        if (IsOnline)
                        {
                            IsOnline = false;
                            StatusChanged?.Invoke(this, false);
                            _consecutiveFailures = FailureThreshold; // Sync failure count
                            System.Diagnostics.Debug.WriteLine($"[NetworkService] Immediate Offline via OS Event.");
                        }
                    });
                }
                else
                {
                    // Network became available - verify actual internet connectivity
                    _ = CheckConnectivityAsync();
                }
            };

            // Hook into Settings Offline Mode
            _settingsService.OfflineModeChanged += (isOffline) => 
            {
                // Trigger immediate check to update status
                _ = CheckConnectivityAsync();
            };
            
            // Start polling (keep generic lambda for Timer)
            _pollingTimer = new Timer(async _ => 
            {
                await CheckConnectivityAsync();
            }, null, TimeSpan.FromSeconds(PollingIntervalSeconds), TimeSpan.FromSeconds(PollingIntervalSeconds));
        }

        // Fallback constructor for designer if needed, but better to enforce DI
        public NetworkService() : this(new SettingsService()) { }

        public async Task<bool> CheckConnectivityAsync()
        {
            // 1. Enforce Offline Mode from Settings
            if (_settingsService.IsOfflineMode)
            {
                if (IsOnline)
                {
                    IsOnline = false;
                    StatusChanged?.Invoke(this, false);
                    System.Diagnostics.Debug.WriteLine($"[NetworkService] Forced Offline via Settings.");
                }
                return false; 
            }

            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync("8.8.8.8", 2000);
                var isConnected = reply.Status == IPStatus.Success;
                
                if (isConnected)
                {
                    // Success: reset failure counter and immediately go online
                    _consecutiveFailures = 0;
                    
                    if (!IsOnline)
                    {
                        IsOnline = true;
                        StatusChanged?.Invoke(this, true);
                        System.Diagnostics.Debug.WriteLine($"[NetworkService] Status changed: Online (recovered)");
                    }
                }
                else
                {
                    // Failure: increment counter
                    _consecutiveFailures++;
                    System.Diagnostics.Debug.WriteLine($"[NetworkService] Ping failed. Consecutive failures: {_consecutiveFailures}/{FailureThreshold}");
                    
                    // Only go offline if threshold exceeded
                    if (_consecutiveFailures >= FailureThreshold && IsOnline)
                    {
                        IsOnline = false;
                        StatusChanged?.Invoke(this, false);
                        System.Diagnostics.Debug.WriteLine($"[NetworkService] Status changed: Offline (after {FailureThreshold} failures)");
                    }
                }
                
                return IsOnline;
            }
            catch
            {
                // Exception counts as failure
                _consecutiveFailures++;
                System.Diagnostics.Debug.WriteLine($"[NetworkService] Check exception. Consecutive failures: {_consecutiveFailures}/{FailureThreshold}");
                
                if (_consecutiveFailures >= FailureThreshold && IsOnline)
                {
                    IsOnline = false;
                    StatusChanged?.Invoke(this, false);
                    System.Diagnostics.Debug.WriteLine($"[NetworkService] Status changed: Offline (exception, after {FailureThreshold} failures)");
                }
                return false;
            }
        }

        /// <summary>
        /// Creates an HttpClient with DNS-over-HTTPS (DoH) support to bypass ISP blocking.
        /// Uses SOCKS5 proxy when sing-box VPN is running.
        /// </summary>
        public System.Net.Http.HttpClient CreateOptimizedHttpClient()
        {
             if (_settingsService.IsOfflineMode)
             {
                 throw new System.Net.WebException("Application is in Offline Mode.");
             }

             // Check if sing-box proxy is running
             bool useProxy = SingboxService.Instance.IsRunning;
             
             System.Net.Http.SocketsHttpHandler handler;
             
             if (useProxy)
             {
                 // When using SOCKS5 proxy, don't use custom ConnectCallback
                 // The proxy handles all connections
                 Console.WriteLine("[NetworkService] Creating HttpClient with SOCKS5 proxy");
                 handler = new System.Net.Http.SocketsHttpHandler
                 {
                     Proxy = new System.Net.WebProxy($"socks5://{SingboxService.Instance.ProxyAddress}:{SingboxService.Instance.ProxyPort}"),
                     UseProxy = true,
                     SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                     {
                         RemoteCertificateValidationCallback = delegate { return true; },
                         EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
                     },
                     PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                     AutomaticDecompression = System.Net.DecompressionMethods.All,
                     UseCookies = true
                 };
             }
             else
             {
                 // When not using proxy, use custom DoH ConnectCallback
                 handler = new System.Net.Http.SocketsHttpHandler
                 {
                    // Custom connection logic to use DoH resolved IP
                    ConnectCallback = async (context, token) =>
                    {
                        var host = context.DnsEndPoint.Host;
                        System.Net.IPAddress? ipAddress = null;

                        // List of DoH providers (IPv4)
                        var dohQueries = new[]
                        {
                            "https://cloudflare-dns.com/dns-query?name={0}&type=A",
                            "https://dns.google/resolve?name={0}&type=A",
                        };
                        
                        using var dohClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };

                        foreach (var template in dohQueries)
                        {
                            try
                            {
                                string dohUrl = string.Format(template, host);
                                var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, dohUrl);
                                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/dns-json"));
                                
                                var response = await dohClient.SendAsync(request, token);
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
                                            break; 
                                        }
                                    }
                                }
                            }
                            catch {}
                        }

                        // Fallback to standard DNS
                        if (ipAddress == null)
                        {
                            var entry = await System.Net.Dns.GetHostEntryAsync(host, token);
                            ipAddress = entry.AddressList.FirstOrDefault();
                        }
                        
                        if (ipAddress == null) throw new Exception($"Could not resolve host: {host}");

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
                    UseCookies = true
                 };
             }
            
            var client = new System.Net.Http.HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(30);
            
            // Add required headers (MangaDex requires User-Agent)
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8");
            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            
            return client;
        }
    }
}
