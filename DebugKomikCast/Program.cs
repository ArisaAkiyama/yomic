using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace DebugKomikCast
{
    class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                string url = "https://komikcast05.com/";
                Console.WriteLine($"Fetching {url}...");

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
                
                var html = await client.GetStringAsync(url);

                // ID from previous run: 126437 (Nano Machine) or 94205 (Magic Emperor)
                string searchId = "126437";
                Console.WriteLine($"Searching for ID {searchId} in entire HTML...");
                
                if (html.Contains(searchId))
                {
                    int index = html.IndexOf(searchId);
                    int start = Math.Max(0, index - 500);
                    int length = Math.Min(html.Length - start, 1000);
                    Console.WriteLine($"Found at index {index}. Context:");
                    Console.WriteLine(html.Substring(start, length));
                }
                else
                {
                    Console.WriteLine("ID not found in raw HTML text.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }
}
