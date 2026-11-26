using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.ComponentModel;
using System.Text.Json.Nodes;


namespace Yomic.Core.Services
{
    public class NetworkService : INotifyPropertyChanged, IDisposable
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void RaisePropertyChanged(string propName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        private bool _isOnline = true;
        public bool IsOnline
        {
            get => _isOnline;
            set { if (_isOnline != value) { _isOnline = value; RaisePropertyChanged(nameof(IsOnline)); } }
        }

        public bool IsInternetAvailable => IsOnline;

        public event EventHandler<bool>? StatusChanged;
        
        private Timer? _pollingTimer;
        private readonly System.Net.Http.HttpClient _connectivityClient;
        private readonly NetworkAvailabilityChangedEventHandler _availabilityChangedHandler;
        private readonly NetworkAddressChangedEventHandler _addressChangedHandler;
        private readonly Action<bool> _offlineModeChangedHandler;
        private volatile bool _isDisposed;
        
        // Debounce/Grace period settings
        private int _consecutiveFailures = 0;
        private const int FailureThreshold = 1; // Instant offline detection on failure
        private const int PollingIntervalSeconds = 20; // Safe 20s periodic check (OS events handle instant changes)

        private readonly SettingsService _settingsService;
        private static readonly System.Net.Http.HttpClient _dohQueryClient = new() { Timeout = TimeSpan.FromSeconds(2) };
        private readonly ConcurrentDictionary<string, (List<System.Net.IPAddress> Ips, DateTime ExpiresAt)> _dohDnsCache = new(StringComparer.OrdinalIgnoreCase);

        public NetworkService(SettingsService settingsService)
        {
            _settingsService = settingsService;
            
            _connectivityClient = new System.Net.Http.HttpClient 
            { 
                Timeout = TimeSpan.FromSeconds(3) // Fast 3s HTTP timeout
            };
            _connectivityClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            // Initial check
            _ = CheckConnectivityAsync();

            // Hook into OS network availability & address changes with named delegates for proper Dispose cleanup
            _availabilityChangedHandler = (s, e) =>
            {
                if (_isDisposed) return;
                if (!e.IsAvailable)
                {
                    SetOnlineStatus(false);
                }
                else
                {
                    _ = CheckConnectivityAsync();
                }
            };
            _addressChangedHandler = (s, e) =>
            {
                if (_isDisposed) return;
                _ = CheckConnectivityAsync();
            };
            _offlineModeChangedHandler = (isOffline) =>
            {
                if (_isDisposed) return;
                if (isOffline)
                {
                    // Immediately reflect offline status even if an online probe is currently in-flight
                    SetOnlineStatus(false);
                    return;
                }
                _ = CheckConnectivityAsync();
            };

            NetworkChange.NetworkAvailabilityChanged += _availabilityChangedHandler;
            NetworkChange.NetworkAddressChanged += _addressChangedHandler;
            _settingsService.OfflineModeChanged += _offlineModeChangedHandler;
            
            // Start polling timer (avoid async void callback)
            _pollingTimer = new Timer(
                _ => { if (!_isDisposed) _ = Task.Run(CheckConnectivityAsync); },
                null,
                TimeSpan.FromSeconds(PollingIntervalSeconds),
                TimeSpan.FromSeconds(PollingIntervalSeconds));
        }

        // Fallback constructor for designer
        public NetworkService() : this(new SettingsService()) { }

        private int _signalLevel = 3; // Default 3 (Full)
        public int SignalLevel
        {
            get => _signalLevel;
            set
            {
                if (_signalLevel != value)
                {
                    _signalLevel = value;
                    Task.Run(() =>
                    {
                        RaisePropertyChanged(nameof(SignalLevel));
                        RaisePropertyChanged(nameof(IsWifiLevel0));
                        RaisePropertyChanged(nameof(IsWifiLevel1));
                        RaisePropertyChanged(nameof(IsWifiLevel2));
                        RaisePropertyChanged(nameof(IsWifiLevel3));
                        RaisePropertyChanged(nameof(StatusTooltipText));
                    });
                }
            }
        }

        public bool IsWifiLevel0 => IsOnline && SignalLevel == 0;
        public bool IsWifiLevel1 => IsOnline && SignalLevel == 1;
        public bool IsWifiLevel2 => IsOnline && SignalLevel == 2;
        public bool IsWifiLevel3 => IsOnline && SignalLevel >= 3;

        private long _currentPingMs = 0;
        public long CurrentPingMs
        {
            get => _currentPingMs;
            set
            {
                _currentPingMs = value;
                Task.Run(() => RaisePropertyChanged(nameof(StatusTooltipText)));
            }
        }

        public string StatusTooltipText
        {
            get
            {
                if (!IsOnline) return "Offline (Diskonisi / Network Disconnected)";
                string quality = SignalLevel switch
                {
                    0 => "Weak Signal",
                    1 => "Fair Signal",
                    2 => "Good Signal",
                    _ => "Excellent Connection"
                };
                return CurrentPingMs > 0 ? $"Online ({quality} - {CurrentPingMs}ms)" : $"Online ({quality})";
            }
        }

        private async Task TriggerOnlineWaveAnimationAsync(int targetLevel)
        {
            try
            {
                for (int lvl = 0; lvl <= targetLevel; lvl++)
                {
                    SignalLevel = lvl;
                    await Task.Delay(100);
                }
            }
            catch { }
        }

        private void SetOnlineStatus(bool online, int newSignalLevel = 3)
        {
            Task.Run(() =>
            {
                bool wasOffline = !IsOnline;
                if (IsOnline != online)
                {
                    IsOnline = online;
                    StatusChanged?.Invoke(this, online);
                    LogService.Info("Network", $"Status changed: {(online ? "Online" : "Offline / Disconnected")}");
                    
                    if (online && wasOffline)
                    {
                        _ = TriggerOnlineWaveAnimationAsync(newSignalLevel);
                    }
                }
                else if (online)
                {
                    SignalLevel = newSignalLevel;
                }
            });
        }

        private int _isCheckingConnectivity = 0;

        public async Task<bool> CheckConnectivityAsync()
        {
            if (_isDisposed) return false;
            // Prevent concurrent NetworkAddressChanged bursts from racing on _consecutiveFailures
            if (Interlocked.CompareExchange(ref _isCheckingConnectivity, 1, 0) != 0)
            {
                return IsOnline;
            }
            try
            {
                return await CheckConnectivityCoreAsync();
            }
            finally
            {
                Interlocked.Exchange(ref _isCheckingConnectivity, 0);
            }
        }

        private async Task<bool> CheckConnectivityCoreAsync()
        {
            // 1. Enforce Offline Mode from Settings
            if (_settingsService.IsOfflineMode)
            {
                SetOnlineStatus(false);
                return false; 
            }

            // 2. Instant OS level check: Is any network interface active?
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                SetOnlineStatus(false);
                return false;
            }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var response = await _connectivityClient.GetAsync("http://clients3.google.com/generate_204");
                sw.Stop();
                var isConnected = response.IsSuccessStatusCode;
                
                if (isConnected)
                {
                    // Re-check Offline Mode after awaiting HTTP so enabling Offline Mode mid-probe is never overwritten
                    if (_settingsService.IsOfflineMode)
                    {
                        SetOnlineStatus(false);
                        return false;
                    }
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    long rtt = sw.ElapsedMilliseconds;
                    CurrentPingMs = rtt;

                    // Calculate Signal Level based on Latency / Network Quality
                    int level = rtt switch
                    {
                        < 80 => 3,   // Excellent (0-80ms) -> wifi.svg (full)
                        < 180 => 2,  // Good (80-180ms)    -> wifi-2.svg
                        < 350 => 1,  // Fair (180-350ms)   -> wifi-1.svg
                        _ => 0       // Weak (>350ms)      -> wifi-0.svg
                    };

                    SetOnlineStatus(true, level);
                    return true;
                }
                else
                {
                    var currentFailures = Interlocked.Increment(ref _consecutiveFailures);
                    if (currentFailures >= FailureThreshold)
                    {
                        SetOnlineStatus(false);
                    }
                    return IsOnline;
                }
            }
            catch (Exception)
            {
                // Fallback: Ping Google DNS 8.8.8.8 with 1.5s timeout
                try 
                {
                    using var ping = new System.Net.NetworkInformation.Ping();
                    var reply = await ping.SendPingAsync("8.8.8.8", 1500);
                    if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                    {
                        if (_settingsService.IsOfflineMode)
                        {
                            SetOnlineStatus(false);
                            return false;
                        }
                        Interlocked.Exchange(ref _consecutiveFailures, 0);
                        long rtt = reply.RoundtripTime;
                        CurrentPingMs = rtt;

                        int level = rtt switch
                        {
                            < 80 => 3,
                            < 180 => 2,
                            < 350 => 1,
                            _ => 0
                        };

                        SetOnlineStatus(true, level);
                        return true;
                    }
                } 
                catch { }

                var currentFailures = Interlocked.Increment(ref _consecutiveFailures);
                if (currentFailures >= FailureThreshold)
                {
                    SetOnlineStatus(false);
                }
                return false;
            }
        }

        /// <summary>
        /// Resolves candidate IP addresses for <paramref name="host"/> using the configured DoH provider
        /// (cached for 5 minutes) with fallback to system DNS only when DoH yields no addresses.
        /// </summary>
        public async Task<List<System.Net.IPAddress>> ResolveHostIpsAsync(string host, CancellationToken token)
        {
            int dohProvider = _settingsService.DnsOverHttpsProvider;
            var cacheKey = $"{dohProvider}:{host}";

            if (_dohDnsCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTime.UtcNow && cached.Ips.Count > 0)
            {
                return new List<System.Net.IPAddress>(cached.Ips);
            }

            var candidateIps = new List<System.Net.IPAddress>();
            if (System.Net.IPAddress.TryParse(host, out var literalIp))
            {
                candidateIps.Add(literalIp);
            }
            else if (dohProvider == 0)
            {
                // If DoH is disabled, just use System DNS directly
                try
                {
                    var hostAddresses = await System.Net.Dns.GetHostAddressesAsync(host, token);
                    candidateIps.AddRange(hostAddresses);
                }
                catch { }
            }
            else
            {
                // List of DoH providers (IPv4 + IPv6 in parallel on first healthy provider)
                string[] dohQueries = dohProvider switch
                {
                    1 => new[] { "https://1.1.1.1/dns-query?name={0}&type={1}", "https://cloudflare-dns.com/dns-query?name={0}&type={1}" }, // Cloudflare
                    2 => new[] { "https://8.8.8.8/resolve?name={0}&type={1}", "https://dns.google/resolve?name={0}&type={1}", "https://1.1.1.1/dns-query?name={0}&type={1}" }, // Google + Cloudflare failover
                    3 => new[] { "https://dns.adguard-dns.com/resolve?name={0}&type={1}", "https://94.140.14.14/resolve?name={0}&type={1}" }, // AdGuard
                    _ => new[] { "https://8.8.8.8/resolve?name={0}&type={1}", "https://1.1.1.1/dns-query?name={0}&type={1}" } // Fallback
                };

                foreach (var template in dohQueries)
                {
                    async Task<(List<System.Net.IPAddress> Found, bool Ok)> QueryRecordAsync(string qType)
                    {
                        var found = new List<System.Net.IPAddress>();
                        try
                        {
                            string dohUrl = string.Format(template, Uri.EscapeDataString(host), qType);
                            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, dohUrl);
                            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/dns-json"));

                            using var response = await _dohQueryClient.SendAsync(request, token);
                            if (!response.IsSuccessStatusCode) return (found, false);

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
                            return (found, true);
                        }
                        catch (Exception ex)
                        {
                            LogService.Debug("NetworkService", $"DoH query ({qType}) failed: {ex.Message}");
                            return (found, false);
                        }
                    }

                    var aTask = QueryRecordAsync("A");
                    var aaaaTask = QueryRecordAsync("AAAA");
                    await Task.WhenAll(aTask, aaaaTask);
                    var aRes = await aTask;
                    var aaaaRes = await aaaaTask;

                    foreach (var ip in aRes.Found.Concat(aaaaRes.Found))
                    {
                        if (!candidateIps.Contains(ip)) candidateIps.Add(ip);
                    }

                    // Only stop failover if we resolved at least one IP OR both A and AAAA queries succeeded cleanly
                    if (candidateIps.Count > 0 || (aRes.Ok && aaaaRes.Ok)) break;
                }

                // Append system DNS addresses ONLY if DoH yielded no IPs (prevents leaking DNS queries to ISP)
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
            }

            if (candidateIps.Count > 0)
            {
                if (_dohDnsCache.Count >= 512)
                {
                    var nowUtc = DateTime.UtcNow;
                    foreach (var k in _dohDnsCache.Keys.ToList())
                    {
                        if (_dohDnsCache.TryGetValue(k, out var c) && c.ExpiresAt <= nowUtc)
                        {
                            _dohDnsCache.TryRemove(k, out _);
                        }
                    }
                    if (_dohDnsCache.Count >= 512) _dohDnsCache.Clear();
                }
                _dohDnsCache[cacheKey] = (new List<System.Net.IPAddress>(candidateIps), DateTime.UtcNow.AddMinutes(5));
            }

            return candidateIps;
        }

        /// <summary>
        /// Creates an HttpClient with DNS-over-HTTPS (DoH) support and socket-level SSRF protection
        /// (even when DoH is disabled, private/loopback/metadata IPs are blocked before TCP connect).
        /// Set <paramref name="useCookies"/> to false for shared cross-site clients (images/downloads/pings)
        /// to prevent cross-site cookie accumulation and header duplication.
        /// </summary>
        public System.Net.Http.HttpClient CreateOptimizedHttpClient(bool useCookies = false, bool allowAutoRedirect = true)
        {
             if (_settingsService.IsOfflineMode)
             {
                 throw new System.Net.WebException("Application is in Offline Mode.");
             }

             // Always use SocketsHttpHandler with our SSRF-filtering ConnectCallback (even when dohProvider == 0)
             // so DNS/302 redirects to 127.0.0.1 / LAN / cloud metadata are blocked regardless of DoH setting.
             var handler = new System.Net.Http.SocketsHttpHandler
             {
                 ConnectCallback = async (context, token) =>
                 {
                     if (_settingsService.IsOfflineMode)
                     {
                         throw new System.Net.WebException("Application is in Offline Mode.");
                     }
                     var host = context.DnsEndPoint.Host;
                     var candidateIps = await ResolveHostIpsAsync(host, token);

                     if (candidateIps.Count == 0) throw new Exception($"Could not resolve host: {host}");

                     // SSRF protection at socket level: refuse connecting to loopback/private/link-local/metadata IPs
                     var publicIps = candidateIps.Where(ip => !Yomic.Core.Helpers.UrlSecurityHelper.IsPrivateOrLoopbackIp(ip)).ToList();
                     if (publicIps.Count == 0)
                     {
                         throw new System.Net.Http.HttpRequestException($"Blocked connection to private/loopback IP for host: {host}");
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
                             return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
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
                 MaxConnectionsPerServer = 32,
                 EnableMultipleHttp2Connections = true,
                 AutomaticDecompression = System.Net.DecompressionMethods.All,
                 UseCookies = useCookies,
                 AllowAutoRedirect = allowAutoRedirect
             };
            
            var client = new System.Net.Http.HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(60);
            
            // Add required headers
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8");
            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            
            return client;
        }
        
        /// <summary>
        /// Resets all network connections by flushing DNS cache and triggering connectivity check.
        /// Call this when switching VPN on/off to ensure fresh connections.
        /// </summary>
        public async Task ResetConnectionsAsync()
        {
            try
            {
                LogService.Info("Network", "Resetting connections...");
                _dohDnsCache.Clear();
                Yomic.Core.Sources.HttpSource.ClearDnsCache();
                ExtensionRepositoryService.ClearDnsCache();
                Yomic.Core.Helpers.UrlSecurityHelper.ClearAllDnsCaches();
                
                // Flush Windows DNS cache (requires cmd)
                try
                {
                    var ipconfigPath = System.IO.Path.Combine(Environment.SystemDirectory, "ipconfig.exe");
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = System.IO.File.Exists(ipconfigPath) ? ipconfigPath : "ipconfig.exe",
                        Arguments = "/flushdns",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = false
                    };
                    using var process = System.Diagnostics.Process.Start(psi);
                    if (process != null)
                    {
                        using var flushCts = new CancellationTokenSource(3000);
                        try { await process.WaitForExitAsync(flushCts.Token); } catch { }
                    }
                    LogService.Info("Network", "DNS cache flushed");
                }
                catch (Exception ex)
                {
                    LogService.Warning("Network", $"DNS flush failed (non-critical): {ex.Message}");
                }
                
                // Reset failure counter
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                
                // Wait a moment for network to stabilize
                await Task.Delay(500);
                
                // Force connectivity check
                await CheckConnectivityAsync();
                
                // Raise event for UI to refresh
                ConnectionReset?.Invoke(this, EventArgs.Empty);
                
                LogService.Info("Network", "Connections reset complete");
            }
            catch (Exception ex)
            {
                LogService.Error("Network", "Error resetting connections", ex);
            }
        }
        
        /// <summary>
        /// Event raised when connections are reset. UI can subscribe to refresh data.
        /// </summary>
        public event EventHandler? ConnectionReset;
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            try { NetworkChange.NetworkAvailabilityChanged -= _availabilityChangedHandler; } catch { }
            try { NetworkChange.NetworkAddressChanged -= _addressChangedHandler; } catch { }
            try { _settingsService.OfflineModeChanged -= _offlineModeChangedHandler; } catch { }
            _pollingTimer?.Dispose();
            _connectivityClient?.Dispose();
        }
    }
}

