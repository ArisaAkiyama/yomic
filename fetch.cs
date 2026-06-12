using System;
using System.Net.Http;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        var html = await client.GetStringAsync("https://komikstation.org/manga/tales-of-demons-and-gods/");
        System.IO.File.WriteAllText("komikstation_test.html", html);
        Console.WriteLine("Done");
    }
}
