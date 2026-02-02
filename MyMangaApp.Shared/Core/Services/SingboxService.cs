using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MyMangaApp.Core.Services
{
    public class SingboxService
    {
        private static SingboxService? _instance;
        public static SingboxService Instance => _instance ??= new SingboxService();

        private Process? _singboxProcess;
        private readonly string _appDataPath;
        private readonly string _singboxExePath;
        private readonly string _configPath;
        
        public bool IsRunning => _singboxProcess != null && !_singboxProcess.HasExited;
        public string ProxyAddress => "127.0.0.1";
        public int ProxyPort => 1080;

        public event Action<bool>? StatusChanged;

        private SingboxService()
        {
            _appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyMangaApp", "singbox");
            _singboxExePath = Path.Combine(_appDataPath, "sing-box.exe");
            _configPath = Path.Combine(_appDataPath, "config.json");
            
            Directory.CreateDirectory(_appDataPath);
        }

        public async Task<bool> EnsureDownloadedAsync(IProgress<double>? progress = null)
        {
            if (File.Exists(_singboxExePath))
            {
                Console.WriteLine("[SingboxService] sing-box.exe already exists.");
                return true;
            }

            // Check bundled
            var bundledPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin_tool", "sing-box.exe");
            if (File.Exists(bundledPath))
            {
                 Console.WriteLine("[SingboxService] Found bundled sing-box, copying...");
                 File.Copy(bundledPath, _singboxExePath, true);
                 return true;
            }

            Console.WriteLine("[SingboxService] Downloading sing-box...");
            
            // sing-box releases URL (Windows AMD64)
            string downloadUrl = "https://github.com/SagerNet/sing-box/releases/download/v1.10.7/sing-box-1.10.7-windows-amd64.zip";
            string zipPath = Path.Combine(_appDataPath, "singbox.zip");

            try
            {
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromMinutes(10);
                
                using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                
                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                var downloadedBytes = 0L;
                
                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    var buffer = new byte[8192];
                    int bytesRead;
                    
                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead);
                        downloadedBytes += bytesRead;
                        
                        if (totalBytes > 0)
                        {
                            progress?.Report((double)downloadedBytes / totalBytes);
                        }
                    }
                }

                Console.WriteLine("[SingboxService] Extracting sing-box...");
                
                // Extract
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.Name.Equals("sing-box.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            entry.ExtractToFile(_singboxExePath, true);
                            break;
                        }
                    }
                }
                
                // Cleanup zip
                File.Delete(zipPath);
                
                Console.WriteLine("[SingboxService] sing-box downloaded and extracted successfully.");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SingboxService] Download failed: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> EnsureConfiguredAsync()
        {
            if (File.Exists(_configPath))
            {
                return true;
            }

            Console.WriteLine("[SingboxService] Generating WARP config...");
            
            string wgcfPath = Path.Combine(_appDataPath, "wgcf.exe");
            string wgcfAccountPath = Path.Combine(_appDataPath, "wgcf-account.toml");
            string wgcfProfilePath = Path.Combine(_appDataPath, "wgcf-profile.conf");
            
            try
            {
                // Step 1: Download wgcf if not exists (or copy from bundled)
                if (!File.Exists(wgcfPath))
                {
                    var bundledWgcf = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin_tool", "wgcf.exe");
                    if (File.Exists(bundledWgcf))
                    {
                        File.Copy(bundledWgcf, wgcfPath, true);
                        Console.WriteLine("[SingboxService] Bundled wgcf copied.");
                    }
                    else
                    {
                        Console.WriteLine("[SingboxService] Downloading wgcf...");
                        string wgcfUrl = "https://github.com/ViRb3/wgcf/releases/download/v2.2.22/wgcf_2.2.22_windows_amd64.exe";
                        
                        using var httpClient = new HttpClient();
                        httpClient.Timeout = TimeSpan.FromMinutes(5);
                        var wgcfBytes = await httpClient.GetByteArrayAsync(wgcfUrl);
                        await File.WriteAllBytesAsync(wgcfPath, wgcfBytes);
                        Console.WriteLine("[SingboxService] wgcf downloaded.");
                    }
                }
                
                // Step 2: Register WARP account if not exists
                if (!File.Exists(wgcfAccountPath))
                {
                    Console.WriteLine("[SingboxService] Registering WARP account...");
                    var registerProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = wgcfPath,
                            Arguments = "register --accept-tos",
                            WorkingDirectory = _appDataPath,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }
                    };
                    registerProcess.Start();
                    await registerProcess.WaitForExitAsync();
                    Console.WriteLine($"[SingboxService] WARP registration exit code: {registerProcess.ExitCode}");
                }
                
                // Step 3: Generate WireGuard profile
                if (!File.Exists(wgcfProfilePath))
                {
                    Console.WriteLine("[SingboxService] Generating WireGuard profile...");
                    var generateProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = wgcfPath,
                            Arguments = "generate",
                            WorkingDirectory = _appDataPath,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }
                    };
                    generateProcess.Start();
                    await generateProcess.WaitForExitAsync();
                    Console.WriteLine($"[SingboxService] WireGuard profile generation exit code: {generateProcess.ExitCode}");
                }
                
                // Step 4: Parse WireGuard config and create sing-box config
                if (File.Exists(wgcfProfilePath))
                {
                    var wgConfig = await File.ReadAllTextAsync(wgcfProfilePath);
                    await CreateWarpConfigAsync(wgConfig);
                    return true;
                }
                else
                {
                    Console.WriteLine("[SingboxService] Failed to generate WireGuard profile, using fallback.");
                    await CreateFallbackConfigAsync();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SingboxService] Config generation failed: {ex.Message}");
                await CreateFallbackConfigAsync();
                return true;
            }
        }
        
        private async Task CreateWarpConfigAsync(string wgConfig)
        {
            // Parse WireGuard config
            string privateKey = "";
            string publicKey = "";
            string endpoint = "engage.cloudflareclient.com:2408";
            string address4 = "172.16.0.2";
            string address6 = "";
            
            var lines = wgConfig.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("PrivateKey"))
                {
                    privateKey = trimmed.Split('=', 2)[1].Trim();
                }
                else if (trimmed.StartsWith("PublicKey"))
                {
                    publicKey = trimmed.Split('=', 2)[1].Trim();
                }
                else if (trimmed.StartsWith("Endpoint"))
                {
                    endpoint = trimmed.Split('=', 2)[1].Trim();
                }
                else if (trimmed.StartsWith("Address"))
                {
                    var addresses = trimmed.Split('=', 2)[1].Trim().Split(',');
                    foreach (var addr in addresses)
                    {
                        var a = addr.Trim();
                        if (a.Contains('.')) address4 = a.Split('/')[0];
                        else if (a.Contains(':')) address6 = a.Split('/')[0];
                    }
                }
            }
            
            Console.WriteLine($"[SingboxService] WARP config parsed. Endpoint: {endpoint}");
            
            // Resolve endpoint hostname to IP to avoid DNS loop
            // (sing-box can't resolve the endpoint if DNS goes through warp which needs the endpoint)
            var endpointParts = endpoint.Split(':');
            string endpointHost = endpointParts[0];
            int endpointPort = int.Parse(endpointParts[1]);
            
            string resolvedEndpoint = endpointHost;
            if (!System.Net.IPAddress.TryParse(endpointHost, out _))
            {
                try
                {
                    Console.WriteLine($"[SingboxService] Resolving {endpointHost}...");
                    var hostEntry = await System.Net.Dns.GetHostEntryAsync(endpointHost);
                    var ipAddress = hostEntry.AddressList.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    if (ipAddress != null)
                    {
                        resolvedEndpoint = ipAddress.ToString();
                        Console.WriteLine($"[SingboxService] Resolved to: {resolvedEndpoint}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SingboxService] Failed to resolve endpoint: {ex.Message}");
                    // Use known Cloudflare WARP IPs as fallback
                    resolvedEndpoint = "162.159.193.1"; // engage.cloudflareclient.com
                    Console.WriteLine($"[SingboxService] Using fallback IP: {resolvedEndpoint}");
                }
            }
            
            // Create sing-box config with WireGuard outbound
            var config = new JObject
            {
                ["log"] = new JObject { ["level"] = "info" },  // Changed to info for debugging
                ["dns"] = new JObject
                {
                    ["servers"] = new JArray
                    {
                        new JObject
                        {
                            ["tag"] = "cloudflare-udp",
                            ["address"] = "1.1.1.1",
                            ["detour"] = "warp"
                        },
                        new JObject
                        {
                            ["tag"] = "google-udp",
                            ["address"] = "8.8.8.8",
                            ["detour"] = "warp"
                        }
                    },
                    ["strategy"] = "prefer_ipv4",
                    ["final"] = "cloudflare-udp"
                },
                ["inbounds"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "socks",
                        ["tag"] = "socks-in",
                        ["listen"] = "127.0.0.1",
                        ["listen_port"] = 1080
                    },
                    new JObject
                    {
                        ["type"] = "http",
                        ["tag"] = "http-in",
                        ["listen"] = "127.0.0.1",
                        ["listen_port"] = 1081
                    }
                },
                ["outbounds"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "wireguard",
                        ["tag"] = "warp",
                        ["server"] = resolvedEndpoint,
                        ["server_port"] = endpointPort,
                        ["local_address"] = new JArray { address4 + "/32" },
                        ["private_key"] = privateKey,
                        ["peer_public_key"] = publicKey,
                        ["mtu"] = 1280
                    },
                    new JObject
                    {
                        ["type"] = "direct",
                        ["tag"] = "direct"
                    }
                },
                ["route"] = new JObject
                {
                    ["final"] = "warp"
                }
            };
            
            // Add IPv6 if available
            if (!string.IsNullOrEmpty(address6))
            {
                var warpOutbound = config["outbounds"]![0] as JObject;
                (warpOutbound!["local_address"] as JArray)!.Add(address6 + "/128");
            }

            await File.WriteAllTextAsync(_configPath, config.ToString());
            Console.WriteLine("[SingboxService] WARP WireGuard config created.");
        }

        private async Task CreateFallbackConfigAsync()
        {
            // Fallback: Direct connection with DoH (DNS over HTTPS)
            // sing-box v1.10+ requires address_resolver for DoH servers
            var config = new JObject
            {
                ["log"] = new JObject { ["level"] = "warn" },
                ["dns"] = new JObject
                {
                    ["servers"] = new JArray
                    {
                        new JObject
                        {
                            ["tag"] = "cloudflare-doh",
                            ["address"] = "https://cloudflare-dns.com/dns-query",
                            ["address_resolver"] = "bootstrap-dns"
                        },
                        new JObject
                        {
                            ["tag"] = "bootstrap-dns",
                            ["address"] = "1.1.1.1",
                            ["detour"] = "direct"
                        }
                    },
                    ["final"] = "cloudflare-doh"
                },
                ["inbounds"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "socks",
                        ["tag"] = "socks-in",
                        ["listen"] = "127.0.0.1",
                        ["listen_port"] = 1080
                    },
                    new JObject
                    {
                        ["type"] = "http",
                        ["tag"] = "http-in",
                        ["listen"] = "127.0.0.1",
                        ["listen_port"] = 1081
                    }
                },
                ["outbounds"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "direct",
                        ["tag"] = "direct"
                    }
                },
                ["route"] = new JObject
                {
                    ["final"] = "direct"
                }
            };

            await File.WriteAllTextAsync(_configPath, config.ToString());
            Console.WriteLine("[SingboxService] Fallback config created.");
        }

        private void KillExistingSingboxProcesses()
        {
            try
            {
                var processes = System.Diagnostics.Process.GetProcessesByName("sing-box");
                foreach (var proc in processes)
                {
                    try
                    {
                        Console.WriteLine($"[SingboxService] Killing orphaned sing-box process (PID: {proc.Id})...");
                        proc.Kill(true);
                        proc.WaitForExit(3000);
                        proc.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SingboxService] Failed to kill process {proc.Id}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SingboxService] Error checking for existing processes: {ex.Message}");
            }
        }

        public async Task<bool> StartAsync()
        {
            if (IsRunning)
            {
                Console.WriteLine("[SingboxService] Already running.");
                return true;
            }

            // Kill any orphaned sing-box processes from previous sessions
            KillExistingSingboxProcesses();
            
            // Small delay to ensure port is released
            await Task.Delay(500);

            if (!await EnsureDownloadedAsync() || !await EnsureConfiguredAsync())
            {
                return false;
            }

            try
            {
                Console.WriteLine("[SingboxService] Starting sing-box...");

                _singboxProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _singboxExePath,
                        Arguments = $"run -c \"{_configPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = _appDataPath
                    },
                    EnableRaisingEvents = true
                };

                _singboxProcess.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Console.WriteLine($"[sing-box] {e.Data}");
                };
                _singboxProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Console.WriteLine($"[sing-box ERROR] {e.Data}");
                };
                _singboxProcess.Exited += (s, e) =>
                {
                    Console.WriteLine("[SingboxService] sing-box process exited.");
                    StatusChanged?.Invoke(false);
                };

                _singboxProcess.Start();
                _singboxProcess.BeginOutputReadLine();
                _singboxProcess.BeginErrorReadLine();

                // Wait a bit to ensure it started
                await Task.Delay(1000);

                if (_singboxProcess.HasExited)
                {
                    Console.WriteLine("[SingboxService] sing-box failed to start.");
                    return false;
                }

                Console.WriteLine($"[SingboxService] sing-box started. SOCKS5 proxy at {ProxyAddress}:{ProxyPort}");
                StatusChanged?.Invoke(true);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SingboxService] Start failed: {ex.Message}");
                return false;
            }
        }

        public void Stop()
        {
            if (_singboxProcess == null || _singboxProcess.HasExited)
            {
                return;
            }

            try
            {
                Console.WriteLine("[SingboxService] Stopping sing-box...");
                _singboxProcess.Kill(true);
                _singboxProcess.WaitForExit(5000);
                _singboxProcess.Dispose();
                _singboxProcess = null;
                Console.WriteLine("[SingboxService] sing-box stopped.");
                StatusChanged?.Invoke(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SingboxService] Stop failed: {ex.Message}");
            }
        }

        // Simple WireGuard key pair generator (for WARP registration)
        private (string PrivateKey, string PublicKey) GenerateWireGuardKeyPair()
        {
            // Simplified - in production, use proper Curve25519 implementation
            var privateKey = new byte[32];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(privateKey);
            }
            
            // Clamp private key for Curve25519
            privateKey[0] &= 248;
            privateKey[31] &= 127;
            privateKey[31] |= 64;

            return (
                Convert.ToBase64String(privateKey),
                Convert.ToBase64String(privateKey) // Placeholder - real impl needs Curve25519
            );
        }
    }
}
