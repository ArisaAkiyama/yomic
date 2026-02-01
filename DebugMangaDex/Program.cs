using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace DebugMangaDex
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Testing MangaDex API v5 Connection in C# (Explicit TLS)...");

            try
            {
                var handler = new HttpClientHandler();
                handler.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
                
                using var client = new HttpClient(handler);
                client.DefaultRequestHeaders.Add("User-Agent", "MyMangaApp/1.0 (Test)");

                string url = "https://api.mangadex.org/manga?limit=5";
                Console.WriteLine($"Fetching {url}...");

                var response = await client.GetAsync(url);
                
                Console.WriteLine($"Status Code: {response.StatusCode}");
                response.EnsureSuccessStatusCode();

                string content = await response.Content.ReadAsStringAsync();
                Console.WriteLine("Success! Data received.");
                Console.WriteLine($"Length: {content.Length}");
                Console.WriteLine($"Preview: {content.Substring(0, Math.Min(200, content.Length))}...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner: {ex.InnerException.Message}");
                }
            }
        }
    }
}
