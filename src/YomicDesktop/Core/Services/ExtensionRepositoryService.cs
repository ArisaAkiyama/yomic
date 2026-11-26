using System;

using System.Collections.Generic;

using System.IO;

using System.Linq;

using System.Net;

using System.Net.Http;

using System.Net.Sockets;

using System.Text.Json;

using System.Text.Json.Serialization;

using System.Text.RegularExpressions;

using System.Threading;

using System.Threading.Tasks;

using Yomic.Core.Sources;

namespace Yomic.Core.Services

{

    public class ExtensionSourceDto

    {

        [JsonPropertyName("name")]

        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("lang")]

        public string Lang { get; set; } = string.Empty;

        [JsonPropertyName("id")]

        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("baseUrl")]

        public string BaseUrl { get; set; } = string.Empty;

    }

    public class ExtensionEntryDto

    {

        [JsonPropertyName("name")]

        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("pkg")]

        public string Pkg { get; set; } = string.Empty;

        [JsonPropertyName("apk")]

        public string Apk { get; set; } = string.Empty;

        [JsonPropertyName("lang")]

        public string Lang { get; set; } = string.Empty;

        [JsonPropertyName("code")]

        public int VersionCode { get; set; }

        [JsonPropertyName("version")]

        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("nsfw")]

        public int Nsfw { get; set; }

        [JsonPropertyName("icon")]

        public string IconUrl { get; set; } = string.Empty;

        [JsonPropertyName("sha256")]

        public string Sha256 { get; set; } = string.Empty;

        [JsonPropertyName("sources")]

        public List<ExtensionSourceDto>? Sources { get; set; }

    }

    public class ExtensionFullState

    {

        public string Id { get; set; } = string.Empty;

        public string Pkg { get; set; } = string.Empty;

        public string ScriptFileName { get; set; } = string.Empty;

        public long SourceId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public int VersionCode { get; set; }

        public string Lang { get; set; } = "ID";

        public bool IsNsfw { get; set; }

        public bool Installed { get; set; }

        public bool HasUpdate { get; set; }

        public bool IsCloudflareProtected { get; set; }

        public string BaseUrl { get; set; } = string.Empty;

        public string AccentColor { get; set; } = "#2563EB";

        public string FileSizeText { get; set; } = string.Empty;

        public string IconUrl { get; set; } = string.Empty;

        public string Status { get; set; } = "online";

    }

    public class ExtensionRepositoryService : IDisposable

    {

        private readonly SourceManager _sourceManager;

        private readonly SettingsService _settingsService;

        private readonly SourceStatusService _statusService;

        private readonly HttpClient _httpClient;

        private static readonly HttpClient _dohClient = new() { Timeout = TimeSpan.FromSeconds(4) };

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (List<IPAddress> Ips, DateTime ExpiresUtc)> _repoDohCache =
            new(StringComparer.OrdinalIgnoreCase);

        private const string RepoOwner = "ArisaAkiyama";

        private const string RepoName = "extension-yomic";

        private const string RepoBranch = "repo";

        private const string MainBranch = "main";

        private List<ExtensionEntryDto> _cachedRepoEntries = new();

        private static readonly string[] AccentPalette =

        {

            "#2563EB", "#059669", "#7C3AED", "#DC2626", "#D97706",

            "#0891B2", "#4F46E5", "#DB2777", "#0D9488", "#EA580C"

        };

        public ExtensionRepositoryService(

            SourceManager sourceManager,

            SettingsService settingsService,

            SourceStatusService statusService)

        {

            _sourceManager = sourceManager;

            _settingsService = settingsService;

            _statusService = statusService;

            var handler = new SocketsHttpHandler

            {

                AutomaticDecompression = DecompressionMethods.All,

                ConnectTimeout = TimeSpan.FromSeconds(10),

                PooledConnectionLifetime = TimeSpan.FromMinutes(2),

                ConnectCallback = async (context, token) =>
                {
                    if (_settingsService.IsOfflineMode)
                    {
                        throw new HttpRequestException("Offline Mode is enabled.");
                    }

                    var host = context.DnsEndPoint.Host;
                    var port = context.DnsEndPoint.Port;
                    var candidateIps = new List<IPAddress>();
                    int dohProvider = _settingsService.DnsOverHttpsProvider;

                    if (IPAddress.TryParse(host, out var literalIp))
                    {
                        candidateIps.Add(literalIp);
                    }
                    else if (dohProvider != 0)
                    {
                        string cacheKey = $"{dohProvider}:{host}";
                        if (_repoDohCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresUtc > DateTime.UtcNow && cached.Ips.Count > 0)
                        {
                            candidateIps.AddRange(cached.Ips);
                        }
                        else
                        {
                            string[] dohTemplates = dohProvider switch
                            {
                                1 => new[] { "https://1.1.1.1/dns-query?name={0}&type={1}", "https://cloudflare-dns.com/dns-query?name={0}&type={1}" },
                                3 => new[] { "https://dns.adguard-dns.com/resolve?name={0}&type={1}", "https://94.140.14.14/resolve?name={0}&type={1}" },
                                _ => new[] { "https://8.8.8.8/resolve?name={0}&type={1}", "https://dns.google/resolve?name={0}&type={1}", "https://1.1.1.1/dns-query?name={0}&type={1}" }
                            };

                            foreach (var template in dohTemplates)
                            {
                                async Task<(List<IPAddress> Found, bool Ok)> QueryTypeAsync(string qType, int recType)
                                {
                                    var found = new List<IPAddress>();
                                    try
                                    {
                                        var dohUrl = string.Format(template, Uri.EscapeDataString(host), qType);
                                        using var dnsReq = new HttpRequestMessage(HttpMethod.Get, dohUrl);
                                        dnsReq.Headers.TryAddWithoutValidation("Accept", "application/dns-json");
                                        using var dnsRes = await _dohClient.SendAsync(dnsReq, token);
                                        if (!dnsRes.IsSuccessStatusCode) return (found, false);
                                        var dnsBytes = await Yomic.Core.Helpers.UrlSecurityHelper.ReadContentBoundedAsync(dnsRes.Content, 256 * 1024, token);
                                        using var doc = JsonDocument.Parse(dnsBytes);
                                        if (doc.RootElement.TryGetProperty("Answer", out var answers))
                                        {
                                            foreach (var ans in answers.EnumerateArray())
                                            {
                                                if (ans.TryGetProperty("type", out var t) && t.GetInt32() == recType &&
                                                    ans.TryGetProperty("data", out var d) &&
                                                    IPAddress.TryParse(d.GetString(), out var parsedIp) &&
                                                    !found.Contains(parsedIp))
                                                {
                                                    found.Add(parsedIp);
                                                }
                                            }
                                        }
                                        return (found, true);
                                    }
                                    catch
                                    {
                                        return (found, false);
                                    }
                                }

                                var aTask = QueryTypeAsync("A", 1);
                                var aaaaTask = QueryTypeAsync("AAAA", 28);
                                await Task.WhenAll(aTask, aaaaTask);
                                var aRes = await aTask;
                                var aaaaRes = await aaaaTask;
                                foreach (var ip in aRes.Found.Concat(aaaaRes.Found))
                                {
                                    if (!candidateIps.Contains(ip)) candidateIps.Add(ip);
                                }
                                if (aRes.Ok || aaaaRes.Ok) break;
                            }

                            if (candidateIps.Count > 0)
                            {
                                if (_repoDohCache.Count >= 256) _repoDohCache.Clear();
                                _repoDohCache[cacheKey] = (new List<IPAddress>(candidateIps), DateTime.UtcNow.AddMinutes(5));
                            }
                        }
                    }

                    IPAddress[] addresses = candidateIps.Count > 0
                        ? candidateIps.ToArray()
                        : await Dns.GetHostAddressesAsync(host, token);

                    var publicAddresses = addresses.Where(ip => !Yomic.Core.Helpers.UrlSecurityHelper.IsPrivateOrLoopbackIp(ip)).ToArray();
                    if (publicAddresses.Length == 0)
                    {
                        throw new HttpRequestException($"Blocked connection to private/loopback IP for host: {host}");
                    }

                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

                    try

                    {

                        await socket.ConnectAsync(publicAddresses, port, token);

                        return new NetworkStream(socket, ownsSocket: true);

                    }

                    catch

                    {

                        socket.Dispose();

                        throw;

                    }

                }

            };

            _httpClient = new HttpClient(handler)

            {

                Timeout = TimeSpan.FromSeconds(15)

            };

            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Yomic/2.0");

            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");

        }

        private string GetCacheFilePath()

        {

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            var dir = Path.Combine(appData, "Yomic");

            Directory.CreateDirectory(dir);

            return Path.Combine(dir, "extensions_index.json");

        }

        private string GetBundledCatalogDir()

        {

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", "Catalog");

        }

        private static string CleanExtensionName(string rawName)

        {

            if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;

            var cleaned = rawName.Replace("Yomic:", "", StringComparison.OrdinalIgnoreCase).Trim();

            cleaned = Regex.Replace(cleaned, @"\s*\([A-Za-z]+\)\s*$", "").Trim();

            return cleaned;

        }

        public static bool IsAdultIndicator(params string?[] candidates)
        {
            if (candidates == null) return false;
            foreach (var c in candidates)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                var low = c.ToLowerInvariant();
                if (low.Contains("18+") || low.Contains("dewasa") || low.Contains("hentai") ||
                    low.Contains("doujin") || low.Contains("smut") || low.Contains("crot") ||
                    low.Contains("porn") || low.Contains("xxx") || low.Contains("ecchi") ||
                    low.Contains("eromanga") || low.Contains("nhentai") || low.Contains("nekopoi") ||
                    low.Contains("mangapoi") || low.Contains("kumapoi") || low.Contains("kumopoi") ||
                    low.Contains("mangasusu") || low.Contains("r-18") || low.Contains("r18") ||
                    low.Contains("mature") || low.Contains("adult") || low.Contains("guro") ||
                    low.Contains("yaoi") || low.Contains("yuri"))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool VerifySha256Hex(string content, string? expectedSha256Hex)
        {
            if (string.IsNullOrEmpty(content) || string.IsNullOrWhiteSpace(expectedSha256Hex)) return false;
            var cleaned = expectedSha256Hex.Trim();
            if (cleaned.Length != 64) return false;
            try
            {
                byte[] actual = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));
                byte[] expected = Convert.FromHexString(cleaned);
                return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch
            {
                return false;
            }
        }

        public static int CalculateVersionCode(string version)

        {

            if (string.IsNullOrEmpty(version)) return 1;

            var cleanVer = version.TrimStart('v', 'V').Trim();

            var parts = cleanVer.Split('.');

            if (parts.Length >= 2 &&

                int.TryParse(parts[0], out int major) &&

                int.TryParse(parts[1], out int minor))

            {

                int patch = 0;

                if (parts.Length >= 3) int.TryParse(parts[2], out patch);

                return (major * 10000) + (minor * 100) + patch;

            }

            return 1;

        }

        private static string NormalizeLang(string? rawLang)

        {

            if (string.IsNullOrWhiteSpace(rawLang)) return "ID";

            var upper = rawLang.Trim().ToUpperInvariant();

            if (upper == "ID" || upper == "IN") return "ID";

            if (upper == "EN") return "EN";

            return "ALL";

        }

        private static string PickAccentColor(string key)

        {

            if (string.IsNullOrEmpty(key)) return AccentPalette[0];

            uint hash = 2166136261;

            foreach (char c in key)

            {

                hash ^= c;

                hash *= 16777619;

            }

            return AccentPalette[hash % AccentPalette.Length];

        }

        private string GetPluginFileSizeText(string? scriptFileName, string? fallbackName = null)

        {

            try

            {

                var pluginsDir = SourceManager.GetUserPluginsDirectory();

                if (!Directory.Exists(pluginsDir)) return string.Empty;

                if (!string.IsNullOrEmpty(scriptFileName))
                {
                    var exactPath = ResolveSafePluginChildPath(pluginsDir, scriptFileName);
                    if (exactPath != null && File.Exists(exactPath))
                    {
                        double kb = new FileInfo(exactPath).Length / 1024.0;
                        return $"{kb:0.#} KB";
                    }
                }

                if (!string.IsNullOrEmpty(fallbackName))

                {

                    var cleanTarget = CleanExtensionName(fallbackName);

                    foreach (var file in Directory.GetFiles(pluginsDir, "*.js"))

                    {

                        var fileNameNoExt = Path.GetFileNameWithoutExtension(file);

                        if (string.Equals(fileNameNoExt, cleanTarget, StringComparison.OrdinalIgnoreCase) ||

                            cleanTarget.Replace(" ", "").Contains(fileNameNoExt, StringComparison.OrdinalIgnoreCase))

                        {

                            double kb = new FileInfo(file).Length / 1024.0;

                            return $"{kb:0.#} KB";

                        }

                    }

                }

                // Also check bundled catalog size if not installed yet
                if (!string.IsNullOrEmpty(scriptFileName))
                {
                    var catalogPath = ResolveSafePluginChildPath(GetBundledCatalogDir(), scriptFileName);
                    if (catalogPath != null && File.Exists(catalogPath))
                    {
                        double kb = new FileInfo(catalogPath).Length / 1024.0;
                        return $"{kb:0.#} KB";
                    }
                }

            }

            catch { }

            return string.Empty;

        }

        private async Task<List<ExtensionEntryDto>> LoadBundledCatalogEntriesAsync()
        {
            var bundledIndexCandidates = new[]
            {
                Path.Combine(GetBundledCatalogDir(), "index.min.json"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", "Catalog", "index.min.json"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", "index.min.json")
            };

            foreach (var bundledIndex in bundledIndexCandidates)
            {
                if (File.Exists(bundledIndex))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(bundledIndex);
                        var parsed = JsonSerializer.Deserialize<List<ExtensionEntryDto>>(json);
                        if (parsed != null && parsed.Count > 0)
                        {
                            return parsed;
                        }
                    }
                    catch { }
                }
            }

            return new List<ExtensionEntryDto>();
        }

        private static int ExtractScriptVersionCode(string? jsContent)
        {
            if (string.IsNullOrWhiteSpace(jsContent)) return 0;
            var m = Regex.Match(jsContent, @"\bversion\s*:\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            return m.Success ? CalculateVersionCode(m.Groups[1].Value) : 0;
        }

        private static List<ExtensionEntryDto> MergeCatalogEntries(
            List<ExtensionEntryDto>? primary,
            List<ExtensionEntryDto>? secondary)
        {
            var map = new Dictionary<string, ExtensionEntryDto>(StringComparer.OrdinalIgnoreCase);

            static int EffectiveVersionCode(ExtensionEntryDto item)
            {
                int parsedCode = CalculateVersionCode(item.Version);
                return Math.Max(item.VersionCode, parsedCode);
            }

            void Upsert(ExtensionEntryDto item, bool preferOnTie)
            {
                if (item == null) return;
                string key = !string.IsNullOrWhiteSpace(item.Pkg)
                    ? item.Pkg.Trim()
                    : (!string.IsNullOrWhiteSpace(item.Apk) ? Path.GetFileNameWithoutExtension(item.Apk) : CleanExtensionName(item.Name));
                if (string.IsNullOrWhiteSpace(key)) return;

                if (!map.TryGetValue(key, out var existing))
                {
                    map[key] = item;
                }
                else
                {
                    int itemCode = EffectiveVersionCode(item);
                    int existingCode = EffectiveVersionCode(existing);
                    if (itemCode > existingCode || (itemCode == existingCode && preferOnTie))
                    {
                        map[key] = item;
                    }
                }
            }

            if (secondary != null)
            {
                foreach (var item in secondary) Upsert(item, preferOnTie: false);
            }
            if (primary != null)
            {
                foreach (var item in primary) Upsert(item, preferOnTie: true);
            }

            return map.Values.OrderBy(e => CleanExtensionName(e.Name), StringComparer.OrdinalIgnoreCase).ToList();
        }

        private readonly object _indexFileLock = new();

        private void WriteIndexCacheAtomically(string cachePath, string json)
        {
            lock (_indexFileLock)
            {
                string? dir = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmpPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    File.WriteAllText(tmpPath, json, System.Text.Encoding.UTF8);
                    File.Move(tmpPath, cachePath, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                }
            }
        }

        public async Task<List<ExtensionEntryDto>> FetchCatalogEntriesAsync(bool forceOnline = false)
        {
            var cachePath = GetCacheFilePath();

            // 1. In-memory cache
            if (!forceOnline && _cachedRepoEntries.Count > 0)
            {
                return _cachedRepoEntries;
            }

            var bundledEntries = await LoadBundledCatalogEntriesAsync();

            // 2. User AppData disk cache (merged with bundled catalog so all 100+ extensions are always available)
            if (!forceOnline && File.Exists(cachePath))
            {
                try
                {
                    var cachedJson = await File.ReadAllTextAsync(cachePath);
                    var parsed = JsonSerializer.Deserialize<List<ExtensionEntryDto>>(cachedJson);
                    if (parsed != null && parsed.Count > 0)
                    {
                        _cachedRepoEntries = MergeCatalogEntries(parsed, bundledEntries);
                        try
                        {
                            var mergedJson = JsonSerializer.Serialize(_cachedRepoEntries);
                            if (!string.Equals(mergedJson, cachedJson, StringComparison.Ordinal))
                            {
                                WriteIndexCacheAtomically(cachePath, mergedJson);
                            }
                        }
                        catch { }
                        return _cachedRepoEntries;
                    }
                }
                catch { }
            }

            // 3. Fast local bundled catalog (Plugins/Catalog/index.min.json)
            if (!forceOnline && bundledEntries.Count > 0)
            {
                _cachedRepoEntries = bundledEntries;
                return _cachedRepoEntries;
            }

            // 4. Try official GitHub origins ONLY for index.min.json (NEVER trust unverified third-party CDNs
            // to supply manifest SHA-256 digests, otherwise a compromised CDN could forge both index.min.json and .js)
            if (!_settingsService.IsOfflineMode)
            {
                string cacheBuster = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                string[] indexUrls =
                {
                    $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/{MainBranch}/index.min.json?t={cacheBuster}",
                    $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/{RepoBranch}/index.min.json?t={cacheBuster}"
                };

                foreach (var url in indexUrls)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        if (response.IsSuccessStatusCode)
                        {
                            var jsonBytes = await Yomic.Core.Helpers.UrlSecurityHelper.ReadContentBoundedAsync(response.Content, 10L * 1024 * 1024, cts.Token);
                            var json = System.Text.Encoding.UTF8.GetString(jsonBytes);
                            var parsed = JsonSerializer.Deserialize<List<ExtensionEntryDto>>(json);
                            if (parsed != null && parsed.Count > 0)
                            {
                                _cachedRepoEntries = MergeCatalogEntries(parsed, bundledEntries);
                                try
                                {
                                    var mergedJson = JsonSerializer.Serialize(_cachedRepoEntries);
                                    WriteIndexCacheAtomically(cachePath, mergedJson);
                                }
                                catch { }
                                return _cachedRepoEntries;
                            }
                        }
                    }
                    catch { }
                }
            }

            if (_cachedRepoEntries.Count == 0 && bundledEntries.Count > 0)
            {
                _cachedRepoEntries = bundledEntries;
            }

            return _cachedRepoEntries;
        }

        public async Task<List<ExtensionFullState>> GetExtensionsListAsync(bool forceRefresh = false)

        {

            var entries = await FetchCatalogEntriesAsync(forceRefresh);

            var installedSources = _sourceManager.GetSources();

            var showNsfw = _settingsService.ShowNsfwSources;

            var result = new List<ExtensionFullState>();

            var installedMatchIds = new HashSet<long>();

            foreach (var entry in entries)

            {

                var cleanName = CleanExtensionName(entry.Name);

                var scriptFile = !string.IsNullOrEmpty(entry.Apk)
                    ? entry.Apk
                    : $"{entry.Pkg.Split('.').LastOrDefault() ?? cleanName.ToLowerInvariant()}.js";

                // Match against installed IMangaSource
                var installedMatch = installedSources.FirstOrDefault(s =>
                    string.Equals(CleanExtensionName(s.Name), cleanName, StringComparison.OrdinalIgnoreCase) ||
                    (entry.Sources != null && entry.Sources.Any(es =>
                        string.Equals(es.Name, s.Name, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(es.BaseUrl) && string.Equals(es.BaseUrl.TrimEnd('/'), s.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))));

                if (installedMatch != null)
                {
                    installedMatchIds.Add(installedMatch.Id);
                }

                bool isNsfw = entry.Nsfw == 1 || (installedMatch != null && installedMatch.IsNsfw) || IsAdultIndicator(entry.Name, cleanName, entry.Pkg, entry.Apk, entry.Sources?.FirstOrDefault()?.Name, entry.Sources?.FirstOrDefault()?.BaseUrl);
                if (isNsfw && !showNsfw) continue;

                bool isInstalled = installedMatch != null;

                bool hasUpdate = false;

                string displayVersion = $"v{entry.Version.TrimStart('v', 'V')}";

                long sourceId = 0;

                string baseUrl = entry.Sources?.FirstOrDefault()?.BaseUrl ?? string.Empty;

                bool isCfProtected = false;

                string statusStr = "online";

                if (installedMatch != null)

                {

                    installedMatchIds.Add(installedMatch.Id);

                    sourceId = installedMatch.Id;

                    baseUrl = installedMatch.BaseUrl;

                    int installedCode = CalculateVersionCode(installedMatch.Version);

                    hasUpdate = entry.VersionCode > installedCode;

                    displayVersion = hasUpdate

                        ? $"v{installedMatch.Version.TrimStart('v', 'V')} → v{entry.Version.TrimStart('v', 'V')}"

                        : $"v{installedMatch.Version.TrimStart('v', 'V')}";

                    var st = _statusService.GetStatus(installedMatch.Id).Status;

                    statusStr = MapStatusToString(st);

                    isCfProtected = installedMatch is ICloudflareBypassable || (installedMatch is HttpSource hs && hs.RequiresProxy) || st == SourceStatus.Warning;

                }

                string extId = !string.IsNullOrEmpty(entry.Pkg)

                    ? entry.Pkg

                    : Path.GetFileNameWithoutExtension(scriptFile);

                result.Add(new ExtensionFullState

                {

                    Id = extId,

                    Pkg = entry.Pkg,

                    ScriptFileName = scriptFile,

                    SourceId = sourceId,

                    Name = cleanName,

                    Version = displayVersion,

                    VersionCode = entry.VersionCode,

                    Lang = NormalizeLang(entry.Lang),

                    IsNsfw = isNsfw,

                    Installed = isInstalled,

                    HasUpdate = hasUpdate,

                    IsCloudflareProtected = isCfProtected,

                    BaseUrl = baseUrl,

                    AccentColor = PickAccentColor(cleanName),

                    FileSizeText = GetPluginFileSizeText(scriptFile, cleanName),

                    IconUrl = YomicDesktop.IPC.WebResourceImageProxy.BuildIconProxyUrl(entry.Pkg,
                        !string.IsNullOrEmpty(entry.IconUrl) ? entry.IconUrl
                        : (entry.Pkg == "luvyaa" || entry.Pkg == "mangafire" || entry.Pkg == "softkomik")
                            ? $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/{MainBranch}/icons/{entry.Pkg}.png"
                            : installedMatch != null && !string.IsNullOrEmpty(installedMatch.IconUrl)
                                ? installedMatch.IconUrl
                                : !string.IsNullOrEmpty(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var bUri)
                                    ? $"https://www.google.com/s2/favicons?domain={bUri.Host}&sz=128"
                                    : string.Empty),

                    Status = statusStr

                });

            }

            // Include any custom/local JS plugins that are installed in %APPDATA%\Yomic\Plugins but not in index.min.json

            foreach (var src in installedSources)

            {

                if (installedMatchIds.Contains(src.Id)) continue;

                var cleanName = CleanExtensionName(src.Name);
                bool isNsfw = src.IsNsfw;
                if (!isNsfw)
                {
                    var matchEntry = entries.FirstOrDefault(e =>
                        string.Equals(CleanExtensionName(e.Name), cleanName, StringComparison.OrdinalIgnoreCase) ||
                        (e.Sources != null && e.Sources.Any(es =>
                            string.Equals(es.Name, src.Name, StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrEmpty(es.BaseUrl) && string.Equals(es.BaseUrl.TrimEnd('/'), src.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))));
                    if (matchEntry != null && matchEntry.Nsfw == 1) isNsfw = true;
                }
                if (!isNsfw)
                {
                    isNsfw = IsAdultIndicator(cleanName, src.Name, src.Description, src.BaseUrl);
                }

                if (isNsfw && !showNsfw) continue;

                var st = _statusService.GetStatus(src.Id).Status;

                bool isCf = src is ICloudflareBypassable || (src is HttpSource hs2 && hs2.RequiresProxy) || st == SourceStatus.Warning;

                result.Add(new ExtensionFullState

                {

                    Id = $"local.{src.Id}",

                    Pkg = $"local.{src.Id}",

                    ScriptFileName = $"{cleanName.ToLowerInvariant().Replace(" ", "")}.js",

                    SourceId = src.Id,

                    Name = cleanName,

                    Version = $"v{src.Version.TrimStart('v', 'V')}",

                    VersionCode = CalculateVersionCode(src.Version),

                    Lang = NormalizeLang(src.Language),

                    IsNsfw = isNsfw,

                    Installed = true,

                    HasUpdate = false,

                    IsCloudflareProtected = isCf,

                    BaseUrl = src.BaseUrl,

                    AccentColor = PickAccentColor(cleanName),

                    FileSizeText = GetPluginFileSizeText(null, cleanName),

                    IconUrl = YomicDesktop.IPC.WebResourceImageProxy.BuildIconProxyUrl(
                        cleanName.ToLowerInvariant().Replace(" ", ""),
                        (!string.IsNullOrEmpty(src.IconUrl) && !src.IconUrl.Contains("softkomik.com") && !src.IconUrl.EndsWith("favicon.ico")) ? src.IconUrl
                        : (src.Id.ToString().ToLowerInvariant().Contains("softkomik") || cleanName.ToLowerInvariant().Contains("softkomik"))
                            ? $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/{MainBranch}/icons/softkomik.png"
                            : !string.IsNullOrEmpty(src.IconUrl) ? src.IconUrl : string.Empty),

                    Status = MapStatusToString(st)

                });

            }

            return result

                .OrderByDescending(x => x.Installed)

                .ThenByDescending(x => x.HasUpdate)

                .ThenBy(x => x.Name)

                .ToList();

        }

        public static string MapStatusToString(SourceStatus status)

        {

            return status switch

            {

                SourceStatus.Online => "online",

                SourceStatus.Warning => "warning",

                SourceStatus.Offline => "offline",

                SourceStatus.Checking => "checking",

                _ => "online"

            };

        }

        public async Task<(bool Success, string Message)> InstallOrUpdateAsync(string idOrPkg, Action<int>? onProgress = null)

        {

            try

            {

                var entries = await FetchCatalogEntriesAsync(false);

                var entry = entries.FirstOrDefault(e =>

                    string.Equals(e.Pkg, idOrPkg, StringComparison.OrdinalIgnoreCase) ||

                    string.Equals(Path.GetFileNameWithoutExtension(e.Apk), idOrPkg, StringComparison.OrdinalIgnoreCase) ||

                    string.Equals(CleanExtensionName(e.Name), idOrPkg, StringComparison.OrdinalIgnoreCase));

                string scriptFileName = entry?.Apk ?? (idOrPkg.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? idOrPkg : $"{idOrPkg}.js");

                if (string.IsNullOrEmpty(scriptFileName) && entry != null)
                {
                    scriptFileName = $"{entry.Pkg.Split('.').Last()}.js";
                }

                string cleanFileName = Path.GetFileName(scriptFileName);
                string langFolder = string.Equals(entry?.Lang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "id";

                onProgress?.Invoke(15);

                string? jsContent = null;

                // 1. Try downloading from GitHub / CDN mirrors if online (supports src/{lang}/{file}.js and root {file}.js)
                if (!_settingsService.IsOfflineMode)
                {
                    string cb = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                    string escapedFileName = Uri.EscapeDataString(cleanFileName);
                    string[] scriptUrls =
                    {
                        $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/{MainBranch}/src/{langFolder}/{escapedFileName}?t={cb}",
                        $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/{MainBranch}/{escapedFileName}?t={cb}",
                        $"https://cdn.jsdelivr.net/gh/{RepoOwner}/{RepoName}@{MainBranch}/src/{langFolder}/{escapedFileName}?t={cb}",
                        $"https://fastly.jsdelivr.net/gh/{RepoOwner}/{RepoName}@{MainBranch}/src/{langFolder}/{escapedFileName}?t={cb}",
                        $"https://cdn.jsdelivr.net/gh/{RepoOwner}/{RepoName}@{MainBranch}/{escapedFileName}?t={cb}"
                    };

                    foreach (var url in scriptUrls)
                    {
                        try
                        {
                            bool isThirdPartyCdn = Uri.TryCreate(url, UriKind.Absolute, out var scriptUri) &&
                                                   !string.Equals(scriptUri.IdnHost, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase);

                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                            using var resp = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                            if (resp.IsSuccessStatusCode)
                            {
                                var textBytes = await Yomic.Core.Helpers.UrlSecurityHelper.ReadContentBoundedAsync(resp.Content, 2L * 1024 * 1024, cts.Token);
                                var text = System.Text.Encoding.UTF8.GetString(textBytes);
                                if (!string.IsNullOrWhiteSpace(text) && !text.TrimStart().StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Fail-closed SHA-256 verification on third-party CDNs and whenever manifest provides a SHA-256 digest
                                    if (!ShouldAcceptRemoteScript(text, entry?.Sha256, isThirdPartyCdn))
                                    {
                                        LogService.Warning("ExtensionRepo", $"Rejected remote script '{cleanFileName}' from {url}: missing or mismatched SHA-256 checksum.");
                                        continue;
                                    }
                                    jsContent = text;
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                }

                onProgress?.Invoke(65);

                // 2. Also check bundled Plugins/Catalog/{cleanFileName} and prefer it if online failed or is older
                string? bundledJsContent = null;
                var bundledCandidates = new[]
                {
                    Path.Combine(GetBundledCatalogDir(), "src", langFolder, cleanFileName),
                    Path.Combine(GetBundledCatalogDir(), cleanFileName)
                };
                foreach (var bundledScriptPath in bundledCandidates)
                {
                    if (File.Exists(bundledScriptPath))
                    {
                        bundledJsContent = await File.ReadAllTextAsync(bundledScriptPath);
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(jsContent))
                {
                    jsContent = bundledJsContent;
                }
                else if (!string.IsNullOrWhiteSpace(bundledJsContent) &&
                         ExtractScriptVersionCode(bundledJsContent) > ExtractScriptVersionCode(jsContent))
                {
                    jsContent = bundledJsContent;
                }

                scriptFileName = cleanFileName;

                if (string.IsNullOrWhiteSpace(jsContent))

                {
                    bool isEn = string.Equals(_settingsService.AppLanguage, "en", StringComparison.OrdinalIgnoreCase);
                    return (false, isEn ? $"Failed to download script file {scriptFileName}." : $"Gagal mengunduh berkas skrip {scriptFileName}.");
                }

                var pluginsDir = SourceManager.GetUserPluginsDirectory();
                Directory.CreateDirectory(pluginsDir);

                var targetPath = ResolveSafePluginChildPath(pluginsDir, scriptFileName);
                if (targetPath == null)
                {
                    bool isEnBadName = string.Equals(_settingsService.AppLanguage, "en", StringComparison.OrdinalIgnoreCase);
                    return (false, isEnBadName ? $"Invalid script filename: {scriptFileName}." : $"Nama berkas skrip tidak valid: {scriptFileName}.");
                }

                // Stage and validate the downloaded script BEFORE replacing any existing working extension
                var stagingPath = Path.Combine(pluginsDir, $".staging_{Guid.NewGuid():N}.js");
                string? backupPath = null;
                long? staleOldSourceId = null;
                try
                {
                    await File.WriteAllTextAsync(stagingPath, jsContent);
                    var previewSource = _sourceManager.PeekExtension(stagingPath);
                    using var previewDisposable = previewSource as IDisposable;
                    if (previewSource == null)
                    {
                        try { File.Delete(stagingPath); } catch { }
                        bool isEnInvalid = string.Equals(_settingsService.AppLanguage, "en", StringComparison.OrdinalIgnoreCase);
                        return (false, isEnInvalid ? $"Invalid script {scriptFileName}." : $"Skrip {scriptFileName} tidak valid.");
                    }

                    if (entry != null)
                    {
                        var cleanName = CleanExtensionName(entry.Name);
                        var existingSrc = _sourceManager.GetSources().FirstOrDefault(s =>
                            string.Equals(CleanExtensionName(s.Name), cleanName, StringComparison.OrdinalIgnoreCase));
                        if (existingSrc != null && existingSrc.Id != previewSource.Id)
                        {
                            staleOldSourceId = existingSrc.Id;
                        }
                    }

                    // Keep rollback copy of existing file until atomic move + InstallPlugin succeed
                    if (File.Exists(targetPath))
                    {
                        backupPath = Path.Combine(pluginsDir, $".backup_{Guid.NewGuid():N}.js");
                        File.Copy(targetPath, backupPath, overwrite: true);
                    }

                    File.Move(stagingPath, targetPath, overwrite: true);
                }
                catch
                {
                    try { if (File.Exists(stagingPath)) File.Delete(stagingPath); } catch { }
                    try { if (backupPath != null && File.Exists(backupPath)) File.Delete(backupPath); } catch { }
                    throw;
                }

                onProgress?.Invoke(90);

                var installedSource = _sourceManager.InstallPlugin(targetPath);
                bool installed = installedSource != null;

                onProgress?.Invoke(100);

                if (installed)
                {
                    if (staleOldSourceId.HasValue)
                    {
                        try { _sourceManager.RemoveSource(staleOldSourceId.Value); } catch { }
                    }
                    try { if (backupPath != null && File.Exists(backupPath)) File.Delete(backupPath); } catch { }
                    var displayName = entry != null ? CleanExtensionName(entry.Name) : Path.GetFileNameWithoutExtension(scriptFileName);
                    bool isEn = string.Equals(_settingsService.AppLanguage, "en", StringComparison.OrdinalIgnoreCase);
                    return (true, isEn ? $"Extension {displayName} installed." : $"Ekstensi {displayName} berhasil dipasang.");
                }

                // Restore prior working extension file if InstallPlugin failed
                if (backupPath != null && File.Exists(backupPath))
                {
                    try { File.Move(backupPath, targetPath, overwrite: true); } catch { }
                }
                else
                {
                    try { if (File.Exists(targetPath)) File.Delete(targetPath); } catch { }
                }
                bool isEnInstallFail = string.Equals(_settingsService.AppLanguage, "en", StringComparison.OrdinalIgnoreCase);
                return (false, isEnInstallFail ? $"Invalid script {scriptFileName}." : $"Skrip {scriptFileName} tidak valid.");
            }
            catch (Exception ex)
            {
                LogService.Error("ExtensionRepo", $"Failed to install extension {idOrPkg}", ex);
                bool isEnErr = string.Equals(_settingsService.AppLanguage, "en", StringComparison.OrdinalIgnoreCase);
                return (false, isEnErr ? $"Failed to install extension: {ex.Message}" : $"Gagal memasang ekstensi: {ex.Message}");
            }

        }

        public (bool Success, string Message) UninstallExtension(string idOrPkg)

        {

            try

            {

                var entries = _cachedRepoEntries;

                var entry = entries.FirstOrDefault(e =>

                    string.Equals(e.Pkg, idOrPkg, StringComparison.OrdinalIgnoreCase) ||

                    string.Equals(CleanExtensionName(e.Name), idOrPkg, StringComparison.OrdinalIgnoreCase));

                string cleanTarget = entry != null ? CleanExtensionName(entry.Name) : idOrPkg;

                var installedSrc = _sourceManager.GetSources().FirstOrDefault(s =>

                    string.Equals(CleanExtensionName(s.Name), cleanTarget, StringComparison.OrdinalIgnoreCase) ||

                    string.Equals($"local.{s.Id}", idOrPkg, StringComparison.OrdinalIgnoreCase) ||

                    string.Equals(s.Id.ToString(), idOrPkg, StringComparison.OrdinalIgnoreCase));

                if (installedSrc != null)

                {

                    _sourceManager.RemoveSource(installedSrc.Id);

                }

                var pluginsDir = SourceManager.GetUserPluginsDirectory();

                if (entry != null && !string.IsNullOrEmpty(entry.Apk))
                {
                    var apkPath = ResolveSafePluginChildPath(pluginsDir, entry.Apk);
                    if (apkPath != null && File.Exists(apkPath))
                    {
                        File.Delete(apkPath);
                    }
                }

                bool isEnUninst = string.Equals(_settingsService.AppLanguage, "en", StringComparison.OrdinalIgnoreCase);
                return (true, isEnUninst ? $"Extension {cleanTarget} uninstalled." : $"Ekstensi {cleanTarget} berhasil dicopot.");
            }
            catch (Exception ex)
            {
                LogService.Error("ExtensionRepo", $"Failed to uninstall {idOrPkg}", ex);
                bool isEnUninstErr = string.Equals(_settingsService.AppLanguage, "en", StringComparison.OrdinalIgnoreCase);
                return (false, isEnUninstErr ? $"Failed to uninstall extension: {ex.Message}" : $"Gagal mencopot ekstensi: {ex.Message}");
            }

        }

        private static string? ResolveSafePluginChildPath(string baseDir, string? rawFileName)
        {
            if (string.IsNullOrWhiteSpace(baseDir) || string.IsNullOrWhiteSpace(rawFileName))
                return null;

            string fileName = Path.GetFileName(rawFileName.Trim());
            if (string.IsNullOrWhiteSpace(fileName) ||
                fileName == "." ||
                fileName == ".." ||
                !fileName.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                string fullBase = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string candidate = Path.GetFullPath(Path.Combine(baseDir, fileName));
                return candidate.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase) ? candidate : null;
            }
            catch
            {
                return null;
            }
        }

        public async Task<int> AutoUpdateInstalledExtensionsAsync()
        {
            int updatedCount = 0;
            try
            {
                var list = await GetExtensionsListAsync(forceRefresh: !_settingsService.IsOfflineMode);
                var outdated = list.Where(e => e.Installed && e.HasUpdate).ToList();
                foreach (var ext in outdated)
                {
                    var (ok, _) = await InstallOrUpdateAsync(ext.Id);
                    if (ok)
                    {
                        updatedCount++;
                        LogService.Info("ExtensionRepo", $"Auto-updated extension '{ext.Name}' to {ext.Version}.");
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Warning("ExtensionRepo", $"Auto-update check failed: {ex.Message}");
            }
            return updatedCount;
        }

        /// <summary>
        /// Determines whether a downloaded remote plugin script should be accepted.
        /// Third-party CDN mirrors (e.g. jsDelivr) are fail-closed and MUST provide a matching SHA-256 digest.
        /// Official GitHub raw origin requires a matching SHA-256 whenever the manifest declares one.
        /// </summary>
        public static bool ShouldAcceptRemoteScript(string? content, string? expectedSha256Hex, bool isThirdPartyCdn)
        {
            if (string.IsNullOrWhiteSpace(content)) return false;

            bool hasExpected = !string.IsNullOrWhiteSpace(expectedSha256Hex);
            if (isThirdPartyCdn && !hasExpected)
            {
                return false;
            }

            if (hasExpected)
            {
                return VerifySha256Hex(content, expectedSha256Hex);
            }

            return true;
        }

        public void Dispose()
        {
            try { _httpClient.Dispose(); } catch { }
        }

    }

}

