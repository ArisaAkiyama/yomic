using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

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
                // 192.168.0.0/16 (RFC1918 private)
                if (b[0] == 192 && b[1] == 168) return true;
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
    }
}
