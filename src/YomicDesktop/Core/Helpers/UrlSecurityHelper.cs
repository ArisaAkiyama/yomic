using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Yomic.Core.Helpers
{
    /// <summary>
    /// Centralized security validation helpers for outbound URLs (SSRF protection),
    /// RFC 6265 cookie domain matching, and filesystem path containment.
    /// </summary>
    public static class UrlSecurityHelper
    {
        public static bool IsSafePublicHttpUrl(string? rawUrl, out Uri? uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(rawUrl)) return false;

            if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var parsed))
                return false;

            if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Normalize trailing root dots (e.g. "localhost." -> "localhost") so DNS root notation cannot bypass blocklist
            string host = parsed.IdnHost.TrimEnd('.');
            if (string.IsNullOrWhiteSpace(host)) return false;

            // Block localhost and reserved local hostnames
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("ip6-localhost", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("ip6-loopback", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // If host is an IP literal, reject loopback, private LAN, link-local, and metadata ranges
            string cleanHost = host.Trim('[', ']');
            if (IPAddress.TryParse(cleanHost, out var ip) && IsPrivateOrLoopbackIp(ip))
            {
                return false;
            }

            uri = parsed;
            return true;
        }

        public static bool IsPrivateOrLoopbackIp(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }

            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.None))
            {
                return true;
            }

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = ip.GetAddressBytes();
                // 0.0.0.0/8 (current network)
                if (b[0] == 0) return true;
                // 10.0.0.0/8 (RFC1918 private)
                if (b[0] == 10) return true;
                // 100.64.0.0/10 (CGNAT / shared address space)
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
                // 127.0.0.0/8 (loopback)
                if (b[0] == 127) return true;
                // 169.254.0.0/16 (link-local & cloud metadata 169.254.169.254)
                if (b[0] == 169 && b[1] == 254) return true;
                // 172.16.0.0/12 (RFC1918 private)
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                // 192.0.0.0/24 (RFC 6890 IETF Protocol Assignments) & 192.0.2.0/24 (TEST-NET-1)
                if (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2)) return true;
                // 192.168.0.0/16 (RFC1918 private)
                if (b[0] == 192 && b[1] == 168) return true;
                // 198.18.0.0/15 (RFC 2544 benchmarking)
                if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;
                // 198.51.100.0/24 (TEST-NET-2)
                if (b[0] == 198 && b[1] == 51 && b[2] == 100) return true;
                // 203.0.113.0/24 (TEST-NET-3)
                if (b[0] == 203 && b[1] == 0 && b[2] == 113) return true;
                // 224.0.0.0/4+ (multicast & reserved)
                if (b[0] >= 224) return true;
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal)
                {
                    return true;
                }
                byte[] b = ip.GetAddressBytes();
                // fc00::/7 (Unique Local Address)
                if ((b[0] & 0xFE) == 0xFC) return true;
                // ::/96 (IPv4-compatible IPv6, e.g. ::127.0.0.1)
                bool first8Zero = b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0 &&
                                  b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0;
                if (first8Zero && b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0) return true;
                // ::ffff:0:0/96 (RFC 2765 SIIT IPv4-translated, e.g. ::ffff:0:127.0.0.1)
                if (first8Zero && b[8] == 0xFF && b[9] == 0xFF && b[10] == 0 && b[11] == 0) return true;
                // 64:ff9b::/96 (NAT64 well-known) & 64:ff9b:1::/48 (NAT64 local-use)
                if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B)
                {
                    if ((b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 && b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0) ||
                        (b[4] == 0x00 && b[5] == 0x01))
                    {
                        return true;
                    }
                }
                // 2002::/16 (6to4 tunnel embedding IPv4 in bytes [2..5])
                if (b[0] == 0x20 && b[1] == 0x02) return true;
                // 2001:0::/32 (Teredo tunnel) & 2001:db8::/32 (Documentation)
                if (b[0] == 0x20 && b[1] == 0x01 &&
                    ((b[2] == 0x00 && b[3] == 0x00) || (b[2] == 0x0D && b[3] == 0xB8)))
                {
                    return true;
                }
                // 100::/64 (RFC 6666 Discard-Only)
                if (b[0] == 0x01 && b[1] == 0x00 && b[2] == 0 && b[3] == 0 &&
                    b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reads an HTTP response body into memory with a strict byte ceiling to prevent OOM DoS
        /// from malicious/broken CDNs or plugins returning gigantic streams.
        /// </summary>
        public static async System.Threading.Tasks.Task<byte[]> ReadContentBoundedAsync(
            System.Net.Http.HttpContent content,
            long maxBytes,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (content.Headers.ContentLength.HasValue && content.Headers.ContentLength.Value > maxBytes)
            {
                throw new InvalidDataException($"HTTP response exceeded maximum allowed size ({maxBytes} bytes).");
            }

            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            using var ms = new MemoryStream(content.Headers.ContentLength.HasValue && content.Headers.ContentLength.Value > 0
                ? (int)Math.Min(content.Headers.ContentLength.Value, 1024 * 1024)
                : 16384);

            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                totalRead += read;
                if (totalRead > maxBytes)
                {
                    throw new InvalidDataException($"HTTP response stream exceeded maximum allowed size ({maxBytes} bytes).");
                }
                ms.Write(buffer, 0, read);
            }

            return ms.ToArray();
        }

        public static bool IsAllowedFeedbackWebhookUrl(string? rawUrl, out Uri? uri)
        {
            uri = null;
            if (!IsSafePublicHttpUrl(rawUrl, out var parsed) || parsed == null) return false;
            if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
            if (!parsed.IsDefaultPort) return false;
            string host = parsed.IdnHost.TrimEnd('.');
            if (!host.Equals("script.google.com", StringComparison.OrdinalIgnoreCase) &&
                !host.Equals("script.googleusercontent.com", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            uri = parsed;
            return true;
        }

        public static bool CookieMatchesRequest(Uri targetUri, string? cookieDomain, bool isSecure = false)
        {
            if (targetUri == null || string.IsNullOrWhiteSpace(cookieDomain))
                return false;

            if (isSecure && !string.Equals(targetUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return false;

            string normalizedDomain = cookieDomain.Trim().TrimStart('.');
            if (string.IsNullOrWhiteSpace(normalizedDomain) || !normalizedDomain.Contains('.'))
                return false;

            string host = targetUri.Host;
            return host.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith("." + normalizedDomain, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsPathInsideDirectory(string? candidatePath, string? rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(rootDirectory))
                return false;

            try
            {
                string fullCandidate = Path.GetFullPath(candidatePath);
                string fullRoot = Path.GetFullPath(rootDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string rootWithSep = fullRoot + Path.DirectorySeparatorChar;

                return fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
                       fullCandidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsHostOrSubdomain(string? rawUrl, string expectedDomain)
        {
            if (string.IsNullOrWhiteSpace(rawUrl) || string.IsNullOrWhiteSpace(expectedDomain))
                return false;
            if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri))
                return false;
            string host = uri.IdnHost.TrimEnd('.');
            string target = expectedDomain.Trim().TrimStart('.').TrimEnd('.');
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(target))
                return false;
            return host.Equals(target, StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith("." + target, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Validates that <paramref name="bytes"/> matches an allowlisted image format magic signature
        /// (JPEG, PNG, WebP, GIF, BMP, AVIF/HEIF, TIFF, ICO, or strict SVG), rejecting HTML, JSON errors,
        /// plain-text blocks, truncated bodies, and corrupt undecrypted ciphertext.
        /// </summary>
        public static bool IsValidImagePayload(byte[]? bytes)
        {
            if (bytes == null || bytes.Length < 4) return false;

            // JPEG: FF D8 FF
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return true;

            // PNG: 89 50 4E 47 0D 0A 1A 0A
            if (bytes.Length >= 8 &&
                bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
                bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
                return true;

            // GIF: GIF87a / GIF89a
            if (bytes.Length >= 6 &&
                bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38 &&
                (bytes[4] == 0x37 || bytes[4] == 0x39) && bytes[5] == 0x61)
                return true;

            // WebP: RIFF....WEBP
            if (bytes.Length >= 12 &&
                bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
                return true;

            // BMP: BM (minimum 14-byte header)
            if (bytes.Length >= 14 && bytes[0] == 0x42 && bytes[1] == 0x4D)
                return true;

            // TIFF: II*\0 or MM\0*
            if ((bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0x2A && bytes[3] == 0x00) ||
                (bytes[0] == 0x4D && bytes[1] == 0x4D && bytes[2] == 0x00 && bytes[3] == 0x2A))
                return true;

            // ICO: 00 00 01 00
            if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x00)
                return true;

            // AVIF / HEIF ISO-BMFF container: ....ftyp + image brand
            if (bytes.Length >= 12 &&
                bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
            {
                string brand = Encoding.ASCII.GetString(bytes, 8, 4);
                if (brand is "avif" or "avis" or "mif1" or "msf1" or "heic" or "heix" or "hevc" or "hevx" or "jxl ")
                {
                    return true;
                }
            }

            // JPEG XL codestream (FF 0A) or container (00 00 00 0C 4A 58 4C 20 0D 0A 87 0A)
            if (bytes[0] == 0xFF && bytes[1] == 0x0A)
                return true;
            if (bytes.Length >= 12 &&
                bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x00 && bytes[3] == 0x0C &&
                bytes[4] == 0x4A && bytes[5] == 0x58 && bytes[6] == 0x4C && bytes[7] == 0x20)
                return true;

            return false;
        }

        private static readonly HttpClient _dnsGuardClient = new() { Timeout = TimeSpan.FromSeconds(3) };
        private static readonly ConcurrentDictionary<string, (IPAddress[] Ips, bool IsSafe, DateTime ExpiresUtc)> _resolvedHostSafetyCache =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, (List<IPAddress> Ips, DateTime ExpiresUtc)> _sharedDohCache =
            new(StringComparer.OrdinalIgnoreCase);

        // Internal seams for deterministic unit testing of split-horizon DNS & DNS-rebinding guards
        internal static Func<string, CancellationToken, Task<IPAddress[]>>? SystemDnsResolverOverride { get; set; }
        internal static Func<string, int, CancellationToken, Task<IPAddress[]>>? DoHResolverOverride { get; set; }

        internal static void ClearResolvedHostSafetyCache() => _resolvedHostSafetyCache.Clear();

        internal static void ClearAllDnsCaches()
        {
            _resolvedHostSafetyCache.Clear();
            _sharedDohCache.Clear();
        }

        /// <summary>
        /// Shared DoH-first host resolver (cached 5m, parallel A+AAAA, system DNS fallback only when DoH yields 0 IPs)
        /// used by UpdateService and any service needing DoH + socket-level SSRF protection.
        /// </summary>
        internal static async Task<List<IPAddress>> ResolvePublicIpsWithDoHAsync(string host, CancellationToken cancellationToken = default)
        {
            var result = new List<IPAddress>();
            if (string.IsNullOrWhiteSpace(host)) return result;

            if (IPAddress.TryParse(host, out var literalIp))
            {
                if (!IsPrivateOrLoopbackIp(literalIp)) result.Add(literalIp);
                return result;
            }

            int dohProvider = Yomic.Core.Sources.HttpSource.DnsOverHttpsProviderGetter?.Invoke() ?? 2;
            string cacheKey = $"{dohProvider}:{host}";
            if (dohProvider != 0 &&
                _sharedDohCache.TryGetValue(cacheKey, out var cached) &&
                cached.ExpiresUtc > DateTime.UtcNow &&
                cached.Ips.Count > 0)
            {
                return cached.Ips.Where(ip => !IsPrivateOrLoopbackIp(ip)).ToList();
            }

            if (dohProvider != 0)
            {
                var dohIps = await ResolveViaConfiguredDoHAsync(host, dohProvider, cancellationToken).ConfigureAwait(false);
                foreach (var ip in dohIps)
                {
                    if (!result.Contains(ip)) result.Add(ip);
                }
            }

            if (result.Count == 0)
            {
                var sysIps = await ResolveViaSystemDnsAsync(host, cancellationToken).ConfigureAwait(false);
                foreach (var ip in sysIps)
                {
                    if (!result.Contains(ip)) result.Add(ip);
                }
            }

            if (dohProvider != 0 && result.Count > 0)
            {
                if (_sharedDohCache.Count >= 256) _sharedDohCache.Clear();
                _sharedDohCache[cacheKey] = (new List<IPAddress>(result), DateTime.UtcNow.AddMinutes(5));
            }

            return result.Where(ip => !IsPrivateOrLoopbackIp(ip)).ToList();
        }

        private static string[] GetDoHGuardTemplates(int dohProvider) => dohProvider switch
        {
            1 => new[] { "https://1.1.1.1/dns-query?name={0}&type={1}", "https://cloudflare-dns.com/dns-query?name={0}&type={1}" },
            3 => new[] { "https://dns.adguard-dns.com/resolve?name={0}&type={1}", "https://94.140.14.14/resolve?name={0}&type={1}" },
            _ => new[] { "https://8.8.8.8/resolve?name={0}&type={1}", "https://dns.google/resolve?name={0}&type={1}", "https://1.1.1.1/dns-query?name={0}&type={1}" }
        };

        private static async Task<IPAddress[]> ResolveViaConfiguredDoHAsync(string host, int dohProvider, CancellationToken cancellationToken)
        {
            if (DoHResolverOverride != null)
            {
                return await DoHResolverOverride(host, dohProvider, cancellationToken).ConfigureAwait(false);
            }

            if (dohProvider == 0 || Yomic.Core.Sources.HttpSource.IsOfflineModeProvider?.Invoke() == true)
            {
                return Array.Empty<IPAddress>();
            }

            var resolved = new List<IPAddress>();
            var templates = GetDoHGuardTemplates(dohProvider);

            foreach (var template in templates)
            {
                async Task<(List<IPAddress> Found, bool Ok)> QueryDoHAsync(string qType, int recType)
                {
                    var found = new List<IPAddress>();
                    try
                    {
                        using var req = new HttpRequestMessage(
                            HttpMethod.Get,
                            string.Format(template, Uri.EscapeDataString(host), qType));
                        req.Headers.TryAddWithoutValidation("Accept", "application/dns-json");
                        using var res = await _dnsGuardClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                        if (!res.IsSuccessStatusCode) return (found, false);
                        var bytes = await ReadContentBoundedAsync(res.Content, 256 * 1024, cancellationToken).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(bytes);
                        if (doc.RootElement.TryGetProperty("Answer", out var answers))
                        {
                            foreach (var ans in answers.EnumerateArray())
                            {
                                if (ans.TryGetProperty("type", out var t) && t.GetInt32() == recType &&
                                    ans.TryGetProperty("data", out var d) &&
                                    IPAddress.TryParse(d.GetString(), out var ip) &&
                                    !found.Contains(ip))
                                {
                                    found.Add(ip);
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

                var aTask = QueryDoHAsync("A", 1);
                var aaaaTask = QueryDoHAsync("AAAA", 28);
                await Task.WhenAll(aTask, aaaaTask).ConfigureAwait(false);
                var aRes = await aTask.ConfigureAwait(false);
                var aaaaRes = await aaaaTask.ConfigureAwait(false);

                foreach (var ip in aRes.Found.Concat(aaaaRes.Found))
                {
                    if (!resolved.Contains(ip)) resolved.Add(ip);
                }

                if (resolved.Count > 0 || (aRes.Ok && aaaaRes.Ok))
                {
                    break;
                }
            }

            return resolved.ToArray();
        }

        private static async Task<IPAddress[]> ResolveViaSystemDnsAsync(string host, CancellationToken cancellationToken)
        {
            if (SystemDnsResolverOverride != null)
            {
                return await SystemDnsResolverOverride(host, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return Array.Empty<IPAddress>();
            }
        }

        /// <summary>
        /// Resolves <paramref name="host"/> against BOTH System DNS (used by WebView2/Chromium) AND the user's configured DoH
        /// provider in parallel. Returns <c>IsSafe = false</c> if ANY IP from EITHER resolver is private/loopback/link-local,
        /// defeating split-horizon DNS and hosts-file SSRF bypasses.
        /// </summary>
        public static async Task<(IPAddress[] Ips, bool IsSafe)> ResolveAndValidateHostForBrowserAsync(
            string host,
            bool bypassCache = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return (Array.Empty<IPAddress>(), false);
            }

            // Enforce Offline Mode BEFORE literal IP fast-path so public IP literals cannot bypass Offline Mode
            if (Yomic.Core.Sources.HttpSource.IsOfflineModeProvider?.Invoke() == true)
            {
                return (Array.Empty<IPAddress>(), false);
            }

            host = host.Trim().TrimEnd('.').Trim('[', ']');
            if (IPAddress.TryParse(host, out var literalIp))
            {
                return (new[] { literalIp }, !IsPrivateOrLoopbackIp(literalIp));
            }

            int dohProvider = Yomic.Core.Sources.HttpSource.DnsOverHttpsProviderGetter?.Invoke() ?? 2;
            string cacheKey = $"{dohProvider}:{host}";

            if (!bypassCache &&
                _resolvedHostSafetyCache.TryGetValue(cacheKey, out var cached) &&
                cached.ExpiresUtc > DateTime.UtcNow)
            {
                return (cached.Ips, cached.IsSafe);
            }

            // Always query System DNS (what WebView2 & Chromium actually connect through) in parallel with DoH
            var sysTask = ResolveViaSystemDnsAsync(host, cancellationToken);
            var dohTask = ResolveViaConfiguredDoHAsync(host, dohProvider, cancellationToken);
            await Task.WhenAll(sysTask, dohTask).ConfigureAwait(false);

            var sysIps = await sysTask.ConfigureAwait(false);
            var dohIps = await dohTask.ConfigureAwait(false);

            var combined = new List<IPAddress>(sysIps.Length + dohIps.Length);
            foreach (var ip in sysIps.Concat(dohIps))
            {
                if (!combined.Contains(ip)) combined.Add(ip);
            }

            // Strict split-horizon defense: ANY private/loopback IP from EITHER System DNS OR DoH immediately fails closed!
            bool anyPrivate = combined.Any(IsPrivateOrLoopbackIp);
            bool isSafe = combined.Count > 0 && !anyPrivate;
            var ipArray = combined.ToArray();

            if (_resolvedHostSafetyCache.Count >= 512) _resolvedHostSafetyCache.Clear();
            // Short 5s cache minimizes DNS-rebinding exposure while avoiding redundant lookups during burst subresource loads
            _resolvedHostSafetyCache[cacheKey] = (ipArray, isSafe, DateTime.UtcNow.AddSeconds(5));
            return (ipArray, isSafe);
        }

        public static async Task<bool> IsSafeResolvedPublicUrlAsync(
            string? rawUrl,
            bool bypassCache = false,
            CancellationToken cancellationToken = default)
        {
            if (!IsSafePublicHttpUrl(rawUrl, out var uri) || uri == null)
                return false;

            var (_, isSafe) = await ResolveAndValidateHostForBrowserAsync(
                uri.IdnHost.TrimEnd('.').Trim('[', ']'),
                bypassCache,
                cancellationToken).ConfigureAwait(false);
            return isSafe;
        }
    }
}
