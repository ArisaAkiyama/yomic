using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Text.Json.Nodes;

namespace Yomic.Core.Services
{
    public class UpdateService : IDisposable
    {
        private const string GITHUB_API_URL = "https://api.github.com/repos/ArisaAkiyama/yomic/releases/latest";
        private const string ALLOWED_RELEASE_PATH_PREFIX = "/ArisaAkiyama/yomic/releases/";
        private volatile string? _verifiedReleaseDownloadUrl;
        private volatile string? _verifiedExpectedSha256;
        private int _isInstalling = 0;

        internal static string? ParseSha256SidecarText(string? sidecarText)
        {
            if (string.IsNullOrWhiteSpace(sidecarText)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(sidecarText, @"\b([a-fA-F0-9]{64})\b");
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
        }

        private static bool IsTrustedUpdateDownloadUri(Uri candidate)
        {
            if (!string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !candidate.IsDefaultPort)
            {
                return false;
            }
            string host = candidate.IdnHost.TrimEnd('.');
            return (host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                    candidate.AbsolutePath.StartsWith(ALLOWED_RELEASE_PATH_PREFIX, StringComparison.OrdinalIgnoreCase)) ||
                   host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                   host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }

        private static HttpClient CreateUpdateHttpClient(TimeSpan timeout)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectCallback = async (context, token) =>
                {
                    if (Yomic.Core.Sources.HttpSource.IsOfflineModeProvider?.Invoke() == true)
                    {
                        throw new HttpRequestException("Offline Mode is enabled.");
                    }

                    string host = context.DnsEndPoint.Host;
                    var publicIps = await Yomic.Core.Helpers.UrlSecurityHelper.ResolvePublicIpsWithDoHAsync(host, token).ConfigureAwait(false);
                    if (publicIps.Count == 0)
                    {
                        throw new HttpRequestException($"Blocked or unresolved update host: {host}");
                    }

                    Exception? lastEx = null;
                    foreach (var ip in publicIps)
                    {
                        var socket = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                        try
                        {
                            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                            attemptCts.CancelAfter(TimeSpan.FromSeconds(5));
                            await socket.ConnectAsync(ip, context.DnsEndPoint.Port, attemptCts.Token).ConfigureAwait(false);
                            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                        }
                        catch (Exception ex)
                        {
                            socket.Dispose();
                            lastEx = ex;
                            if (token.IsCancellationRequested) throw;
                        }
                    }

                    throw lastEx ?? new HttpRequestException($"Failed to connect to update host: {host}");
                }
            };
            var client = new HttpClient(handler) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Yomic-Desktop-App");
            return client;
        }

        private async Task<string?> TryFetchSidecarSha256Async(HttpClient client, string exeDownloadUrl, CancellationToken ct)
        {
            string sidecarUrl = exeDownloadUrl + ".sha256";
            if (!IsTrustedInstallUrl(sidecarUrl, out var sidecarUri) || sidecarUri == null)
            {
                return null;
            }
            try
            {
                Uri currentUri = sidecarUri;
                const int maxRedirects = 5;
                for (int hop = 0; hop <= maxRedirects; hop++)
                {
                    if (!IsTrustedUpdateDownloadUri(currentUri))
                    {
                        return null;
                    }
                    var hopResp = await client.GetAsync(currentUri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    int status = (int)hopResp.StatusCode;
                    if (status is 301 or 302 or 303 or 307 or 308 && hopResp.Headers.Location != null)
                    {
                        var location = hopResp.Headers.Location;
                        var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                        hopResp.Dispose();
                        if (hop == maxRedirects) return null;
                        currentUri = nextUri;
                        continue;
                    }

                    using (hopResp)
                    {
                        if (!hopResp.IsSuccessStatusCode) return null;
                        var bytes = await Yomic.Core.Helpers.UrlSecurityHelper.ReadContentBoundedAsync(hopResp.Content, 16 * 1024, ct).ConfigureAwait(false);
                        return ParseSha256SidecarText(System.Text.Encoding.UTF8.GetString(bytes));
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        public static string? ExtractExpectedSha256(JsonNode? assetNode, string? releaseBody, string? assetName)
        {
            // 1. Prefer GitHub's native release asset `digest` field ("sha256:<64-hex>")
            var digestField = assetNode?["digest"]?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(digestField))
            {
                const string prefix = "sha256:";
                var hex = digestField.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? digestField.Substring(prefix.Length).Trim()
                    : digestField;
                if (IsValidSha256Hex(hex)) return hex.ToLowerInvariant();
            }

            // 2. Fallback: look for SHA-256 checksum published in the GitHub release notes
            if (!string.IsNullOrWhiteSpace(releaseBody))
            {
                if (!string.IsNullOrWhiteSpace(assetName))
                {
                    var namedMatch = System.Text.RegularExpressions.Regex.Match(
                        releaseBody,
                        @"\b([a-fA-F0-9]{64})\b[^\r\n]*" + System.Text.RegularExpressions.Regex.Escape(assetName),
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (namedMatch.Success) return namedMatch.Groups[1].Value.ToLowerInvariant();
                }

                var labelMatch = System.Text.RegularExpressions.Regex.Match(
                    releaseBody,
                    @"SHA-?256\s*[:=]\s*`?([a-fA-F0-9]{64})`?",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (labelMatch.Success) return labelMatch.Groups[1].Value.ToLowerInvariant();
            }

            return null;
        }

        public static bool IsValidSha256Hex(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex) || hex.Length != 64) return false;
            foreach (char c in hex)
            {
                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!isHex) return false;
            }
            return true;
        }

        public static bool VerifyHashMatchesExpectedHex(byte[] actualHash, string? expectedHex)
        {
            if (actualHash == null || actualHash.Length != 32 || !IsValidSha256Hex(expectedHex)) return false;
            try
            {
                byte[] expectedBytes = Convert.FromHexString(expectedHex!);
                return CryptographicOperations.FixedTimeEquals(actualHash, expectedBytes);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsAllowedUpdateUrl(string? rawUrl, out Uri? parsedUri)
        {
            parsedUri = null;
            if (string.IsNullOrWhiteSpace(rawUrl)) return false;
            if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri)) return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
            if (!uri.IsDefaultPort) return false;

            string host = uri.IdnHost.TrimEnd('.');
            // Only accept initial release URLs strictly scoped to the official Yomic repository.
            // GitHub's download CDN (objects.githubusercontent.com / release-assets.githubusercontent.com)
            // is only permitted as a redirect target AFTER starting from the official repo release URL.
            if (string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.StartsWith(ALLOWED_RELEASE_PATH_PREFIX, StringComparison.OrdinalIgnoreCase))
            {
                parsedUri = uri;
                return true;
            }

            return false;
        }

        public bool IsTrustedInstallUrl(string? rawUrl, out Uri? parsedUri)
        {
            return IsAllowedUpdateUrl(rawUrl, out parsedUri);
        }
        public static string CURRENT_VERSION
        {
            get
            {
                var ver = Assembly.GetExecutingAssembly().GetName().Version;
                if (ver == null || (ver.Major == 1 && ver.Minor == 0 && ver.Build == 0))
                    return "2.6.2";
                return ver.ToString(3);
            }
        }

        public class UpdateInfo
        {
            public bool IsUpdateAvailable { get; set; }
            public string CurrentVersion { get; set; } = CURRENT_VERSION;
            public string LatestVersion { get; set; } = string.Empty;
            public string DownloadUrl { get; set; } = string.Empty;
            public string ExpectedSha256 { get; set; } = string.Empty;
            public string ReleaseNotes { get; set; } = string.Empty;
            public string PublishedAt { get; set; } = string.Empty;
        }

        public event EventHandler<UpdateInfo>? UpdateAvailableDetected;

        private System.Threading.Timer? _realtimeTimer;
        private string? _lastNotifiedTag;
        private int _isChecking;
        private bool _disposed;

        public void StartRealtimeMonitoring(int intervalMinutes = 60)
        {
            if (_realtimeTimer != null) return;

            CleanupStaleUpdateDirectories();

            var period = TimeSpan.FromMinutes(Math.Max(1, intervalMinutes));
            // Trigger first check after 3 seconds, then periodically every period (avoid async void callback)
            _realtimeTimer = new System.Threading.Timer(
                _ => _ = Task.Run(PerformPeriodicCheckAsync),
                null,
                TimeSpan.FromSeconds(3),
                period);
        }

        private static void CleanupStaleUpdateDirectories()
        {
            try
            {
                string updatesRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Yomic",
                    "Updates");
                if (!Directory.Exists(updatesRoot)) return;

                var cutoffUtc = DateTime.UtcNow.AddHours(-1);
                foreach (var dir in Directory.GetDirectories(updatesRoot))
                {
                    try
                    {
                        var info = new DirectoryInfo(dir);
                        if (info.LastWriteTimeUtc < cutoffUtc)
                        {
                            info.Delete(recursive: true);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        public void StopRealtimeMonitoring()
        {
            _realtimeTimer?.Dispose();
            _realtimeTimer = null;
        }

        private async Task PerformPeriodicCheckAsync()
        {
            if (_disposed || Yomic.Core.Sources.HttpSource.IsOfflineModeProvider?.Invoke() == true) return;
            if (Interlocked.CompareExchange(ref _isChecking, 1, 0) != 0) return;

            try
            {
                var info = await CheckForUpdatesAsync();
                if (info.IsUpdateAvailable && !string.Equals(_lastNotifiedTag, info.LatestVersion, StringComparison.OrdinalIgnoreCase))
                {
                    _lastNotifiedTag = info.LatestVersion;
                    UpdateAvailableDetected?.Invoke(this, info);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateService] Realtime polling check error: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _isChecking, 0);
            }
        }

        public async Task<UpdateInfo> CheckForUpdatesAsync()
        {
            if (Yomic.Core.Sources.HttpSource.IsOfflineModeProvider?.Invoke() == true)
            {
                return new UpdateInfo { IsUpdateAvailable = false };
            }
            try
            {
                using var client = CreateUpdateHttpClient(TimeSpan.FromSeconds(10));

                var response = await client.GetStringAsync(GITHUB_API_URL);
                var json = JsonNode.Parse(response);

                string latestVersionTag = json?["tag_name"]?.ToString() ?? string.Empty;
                string rawHtmlUrl = json?["html_url"]?.ToString() ?? string.Empty;
                string downloadUrl = IsAllowedUpdateUrl(rawHtmlUrl, out _) ? rawHtmlUrl : string.Empty; // Verified fallback
                string body = json?["body"]?.ToString() ?? string.Empty;
                string publishedAt = json?["published_at"]?.ToString() ?? string.Empty;

                string? expectedSha256 = null;
                // Parse exe assets and extract GitHub asset SHA-256 digest if published
                if (json?["assets"] is JsonArray assets)
                {
                    foreach (var asset in assets)
                    {
                        var url = asset?["browser_download_url"]?.ToString();
                        var name = asset?["name"]?.ToString();
                        if (!string.IsNullOrEmpty(url) &&
                            !string.IsNullOrEmpty(name) &&
                            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                            IsAllowedUpdateUrl(url, out _))
                        {
                            downloadUrl = url;
                            expectedSha256 = ExtractExpectedSha256(asset, body, name);
                            break;
                        }
                    }
                }

                // Clean up version string (remove 'v' prefix if present)
                string cleanLatest = latestVersionTag.TrimStart('v', 'V');
                string cleanCurrent = CURRENT_VERSION.TrimStart('v', 'V');

                if (Version.TryParse(cleanLatest, out var latest) && Version.TryParse(cleanCurrent, out var current))
                {
                    if (latest > current)
                    {
                        if (IsAllowedUpdateUrl(downloadUrl, out _))
                        {
                            _verifiedReleaseDownloadUrl = downloadUrl;
                            _verifiedExpectedSha256 = expectedSha256;
                        }
                        return new UpdateInfo
                        {
                            IsUpdateAvailable = true,
                            CurrentVersion = CURRENT_VERSION,
                            LatestVersion = latestVersionTag,
                            DownloadUrl = downloadUrl,
                            ExpectedSha256 = expectedSha256 ?? string.Empty,
                            ReleaseNotes = body,
                            PublishedAt = publishedAt
                        };
                    }
                }

                return new UpdateInfo
                {
                    IsUpdateAvailable = false,
                    CurrentVersion = CURRENT_VERSION,
                    LatestVersion = string.IsNullOrEmpty(latestVersionTag) ? CURRENT_VERSION : latestVersionTag,
                    DownloadUrl = downloadUrl,
                    ReleaseNotes = body,
                    PublishedAt = publishedAt
                };
            }
            catch (Exception ex)
            {
                LogService.Warning("UpdateService", $"Update check failed: {ex.Message}");
                return new UpdateInfo
                {
                    IsUpdateAvailable = false,
                    CurrentVersion = CURRENT_VERSION,
                    LatestVersion = CURRENT_VERSION
                };
            }
        }

        public async Task<bool> DownloadAndInstallUpdateAsync(
            string downloadUrl,
            Action<double>? onProgress = null,
            Action<double, long, long, string>? onDetailedProgress = null)
        {
            if (Yomic.Core.Sources.HttpSource.IsOfflineModeProvider?.Invoke() == true)
            {
                LogService.Info("UpdateService", "Skipped update download because Offline Mode is enabled.");
                onDetailedProgress?.Invoke(0.0, 0L, 0L, "error");
                return false;
            }

            if (!IsTrustedInstallUrl(downloadUrl, out var parsedUri) || parsedUri == null)
            {
                LogService.Warning("UpdateService", $"Blocked untrusted update URL: {downloadUrl}");
                onDetailedProgress?.Invoke(0.0, 0L, 0L, "error");
                return false;
            }

            if (!parsedUri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                // Only allow opening verified GitHub release web pages in the browser
                if (!string.Equals(parsedUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                {
                    onDetailedProgress?.Invoke(0.0, 0L, 0L, "error");
                    return false;
                }
                try
                {
                    using var browserProc = Process.Start(new ProcessStartInfo
                    {
                        FileName = parsedUri.AbsoluteUri,
                        UseShellExecute = true
                    });
                    return true;
                }
                catch
                {
                    onDetailedProgress?.Invoke(0.0, 0L, 0L, "error");
                    return false;
                }
            }

            if (Interlocked.CompareExchange(ref _isInstalling, 1, 0) != 0)
            {
                LogService.Warning("UpdateService", "Update download/installation is already in progress.");
                return false;
            }

            string updateDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Yomic",
                "Updates",
                Guid.NewGuid().ToString("N"));
            string tempPath = Path.Combine(updateDir, "Yomic_Update.exe");

            bool mutexReleased = false;
            try
            {
                Directory.CreateDirectory(updateDir);

                long finalTotalBytes = 0;
                long finalTotalRead = 0;
                byte[] streamedHash;

                using var overallCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                using (var client = CreateUpdateHttpClient(Timeout.InfiniteTimeSpan))
                {
                    // Follow redirects manually and validate EVERY hop BEFORE connecting
                    Uri currentUri = parsedUri;
                    HttpResponseMessage? response = null;
                    const int maxRedirects = 5;
                    for (int hop = 0; hop <= maxRedirects; hop++)
                    {
                        if (!IsTrustedUpdateDownloadUri(currentUri))
                        {
                            throw new InvalidOperationException($"Blocked untrusted update redirect host: {currentUri.Host}");
                        }

                        var hopResp = await client.GetAsync(currentUri, HttpCompletionOption.ResponseHeadersRead, overallCts.Token);
                        int status = (int)hopResp.StatusCode;
                        if (status is 301 or 302 or 303 or 307 or 308 && hopResp.Headers.Location != null)
                        {
                            var location = hopResp.Headers.Location;
                            var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                            hopResp.Dispose();
                            if (hop == maxRedirects)
                            {
                                throw new InvalidOperationException("Too many redirects while downloading update.");
                            }
                            currentUri = nextUri;
                            continue;
                        }

                        response = hopResp;
                        break;
                    }

                    if (response == null)
                    {
                        throw new InvalidOperationException("Update download failed to obtain a valid HTTP response.");
                    }
                    using var _respLifetime = response;
                    response.EnsureSuccessStatusCode();

                    const long MaxInstallerBytes = 250L * 1024 * 1024; // 250 MB safety cap
                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    if (totalBytes > MaxInstallerBytes)
                    {
                        throw new InvalidDataException($"Update package exceeds maximum allowed size ({MaxInstallerBytes / (1024 * 1024)} MB).");
                    }
                    finalTotalBytes = totalBytes > 0 ? totalBytes : 0;
                    var canValidProgress = totalBytes > 0;
                    onDetailedProgress?.Invoke(0.0, 0L, finalTotalBytes, "downloading");

                    using var contentStream = await response.Content.ReadAsStreamAsync(overallCts.Token);
                    using var fileStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, useAsync: true);
                    using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                    var totalRead = 0L;
                    var buffer = new byte[8192];
                    var lastReportTicks = Environment.TickCount64;
                    var magicBytes = new byte[2];
                    int magicCollected = 0;
                    bool checkedMagic = false;

                    while (true)
                    {
                        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
                        readCts.CancelAfter(TimeSpan.FromSeconds(30));

                        int read = await contentStream.ReadAsync(buffer, 0, buffer.Length, readCts.Token);
                        if (read <= 0) break;

                        if (totalRead + read > MaxInstallerBytes)
                        {
                            throw new InvalidDataException($"Update package exceeds maximum allowed size ({MaxInstallerBytes / (1024 * 1024)} MB).");
                        }

                        if (!checkedMagic)
                        {
                            int toCopy = Math.Min(2 - magicCollected, read);
                            Buffer.BlockCopy(buffer, 0, magicBytes, magicCollected, toCopy);
                            magicCollected += toCopy;
                            if (magicCollected == 2)
                            {
                                if (magicBytes[0] != 0x4D || magicBytes[1] != 0x5A) // 'MZ' PE header
                                {
                                    throw new InvalidDataException("Downloaded update file is not a valid Windows executable.");
                                }
                                checkedMagic = true;
                            }
                        }

                        sha256.AppendData(buffer, 0, read);
                        await fileStream.WriteAsync(buffer, 0, read, overallCts.Token);
                        totalRead += read;
                        finalTotalRead = totalRead;

                        var nowTicks = Environment.TickCount64;
                        if (nowTicks - lastReportTicks >= 120 || (canValidProgress && totalRead >= totalBytes))
                        {
                            lastReportTicks = nowTicks;
                            double pct = canValidProgress ? (double)totalRead / totalBytes * 100.0 : 0.0;
                            if (canValidProgress) onProgress?.Invoke(pct);
                            onDetailedProgress?.Invoke(pct, totalRead, finalTotalBytes, "downloading");
                        }
                    }

                    if (!checkedMagic || totalRead < 65536 || (canValidProgress && totalRead != totalBytes))
                    {
                        throw new InvalidDataException("Downloaded update file is incomplete or corrupted.");
                    }

                    await fileStream.FlushAsync(overallCts.Token);
                    streamedHash = sha256.GetHashAndReset();
                }

                // Verify file integrity right before launching (mitigates TOCTOU tampering)
                byte[] diskHash;
                using (var verifyStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    diskHash = await SHA256.HashDataAsync(verifyStream);
                }

                if (!CryptographicOperations.FixedTimeEquals(streamedHash, diskHash))
                {
                    throw new InvalidOperationException("Update installer integrity verification failed before launch.");
                }

                // Verify downloaded installer against GitHub-published SHA-256 digest (or official .sha256 sidecar)
                var pinnedSha = string.Equals(downloadUrl.Trim(), _verifiedReleaseDownloadUrl, StringComparison.OrdinalIgnoreCase)
                    ? _verifiedExpectedSha256
                    : null;
                if (string.IsNullOrWhiteSpace(pinnedSha))
                {
                    using var sidecarClient = CreateUpdateHttpClient(TimeSpan.FromSeconds(15));
                    pinnedSha = await TryFetchSidecarSha256Async(sidecarClient, downloadUrl.Trim(), overallCts.Token);
                }
                if (string.IsNullOrWhiteSpace(pinnedSha))
                {
                    throw new InvalidOperationException("Refusing to run update installer without a verifiable official SHA-256 checksum.");
                }
                if (!VerifyHashMatchesExpectedHex(diskHash, pinnedSha))
                {
                    throw new InvalidOperationException("Update installer SHA-256 digest does not match official GitHub release checksum.");
                }
                LogService.Info("UpdateService", $"Verified official release SHA-256 digest: {pinnedSha}");

                onProgress?.Invoke(100.0);
                onDetailedProgress?.Invoke(100.0, finalTotalRead, finalTotalBytes > 0 ? finalTotalBytes : finalTotalRead, "installing");

                // Release the single-instance mutex on the owning UI thread right before starting the installer
                YomicDesktop.App.ReleaseSingleInstanceMutexForRestart();
                mutexReleased = true;

                var startInfo = new ProcessStartInfo
                {
                    FileName = tempPath,
                    UseShellExecute = true,
                    Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1"
                };

                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    mutexReleased = false; // App is shutting down intentionally
                    _ = Application.Current?.Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
                    return true;
                }

                // Process failed to start: re-acquire single-instance mutex so app isn't left unguarded
                YomicDesktop.App.TryReacquireSingleInstanceMutex();
                mutexReleased = false;
                onDetailedProgress?.Invoke(0.0, 0L, 0L, "error");
                return false;
            }
            catch (Exception ex)
            {
                if (mutexReleased)
                {
                    YomicDesktop.App.TryReacquireSingleInstanceMutex();
                }
                LogService.Error("UpdateService", $"Download and install update failed: {ex.Message}", ex);
                try
                {
                    if (Directory.Exists(updateDir))
                    {
                        Directory.Delete(updateDir, recursive: true);
                    }
                }
                catch { }
                onDetailedProgress?.Invoke(0.0, 0L, 0L, "error");
                return false;
            }
            finally
            {
                Interlocked.Exchange(ref _isInstalling, 0);
            }
        }

        public void ResetLastNotifiedTag()
        {
            _lastNotifiedTag = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopRealtimeMonitoring();
        }
    }
}
