using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json.Nodes;

class Program
{
    static async Task Main(string[] args)
    {
        string[] userAgents = new[]
        {
            "Yomic/1.0.3",
            "Tachiyomi/1.0",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36", // Spoofed Chrome
            "", // Empty User-Agent
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/115.0", // Spoofed Firefox
        };

        foreach (var ua in userAgents)
        {
            Console.WriteLine($"\n=== Testing User-Agent: '{ua}' ===");
            await TestUaAsync(ua);
        }
    }

    static async Task TestUaAsync(string userAgent)
    {
        var dohClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, token) =>
            {
                var host = context.DnsEndPoint.Host;
                IPAddress? ipAddress = null;

                try
                {
                    string dohUrl = $"https://8.8.8.8/resolve?name={host}&type=A";
                    var request = new HttpRequestMessage(HttpMethod.Get, dohUrl);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
                    var response = await dohClient.SendAsync(request, token);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync(token);
                        var obj = JsonNode.Parse(json);
                        var answers = obj?["Answer"]?.AsArray();
                        if (answers != null)
                        {
                            foreach (var ans in answers)
                            {
                                var ipStr = ans?["data"]?.ToString();
                                if (ipStr != null && IPAddress.TryParse(ipStr, out var parsedIp))
                                {
                                    ipAddress = parsedIp;
                                    break; 
                                }
                            }
                        }
                    }
                }
                catch {}

                if (ipAddress == null)
                {
                    var entry = await Dns.GetHostEntryAsync(host, token);
                    ipAddress = entry.AddressList.FirstOrDefault();
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(ipAddress!, context.DnsEndPoint.Port, token);
                return new NetworkStream(socket, ownsSocket: true);
            },
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = delegate { return true; },
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
            },
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.All
        };

        using var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(5);
        
        var requestMessage = new HttpRequestMessage(HttpMethod.Get, "https://api.mangadex.org/manga?limit=1");
        if (!string.IsNullOrEmpty(userAgent))
        {
            requestMessage.Headers.Add("User-Agent", userAgent);
        }
        requestMessage.Headers.Add("Accept", "application/json");

        try
        {
            var response = await client.SendAsync(requestMessage);
            Console.WriteLine($"Status: {response.StatusCode}");
            var content = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Length: {content.Length}");
            if (content.Contains("400: Unsupported Browser"))
            {
                Console.WriteLine("Result: Blocked with Unsupported Browser page");
            }
            else
            {
                Console.WriteLine($"Result preview: {content.Substring(0, Math.Min(content.Length, 150))}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed: {ex.Message}");
        }
    }
}
