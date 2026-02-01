using System;
using System.Net.Http;
using System.Threading.Tasks;
using HtmlAgilityPack;
using System.Linq;

class Program
{
    static async Task Main(string[] args)
    {
        var url = "https://kiryuu03.com/latest/";
        Console.WriteLine($"Fetching {url}...");

        try 
        {
            var handler = new HttpClientHandler();
            // Try to behave like a browser
            var client = new HttpClient(handler);
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            
            var html = await client.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            Console.WriteLine("Finding Manga Links and Time Elements...");
            var links = doc.DocumentNode.SelectNodes("//a[contains(@href, '/manga/')]");
            
            if (links != null)
            {
                Console.WriteLine($"Found {links.Count} manga links.");
                foreach (var link in links.Take(1))
                {
                    Console.WriteLine($"Link Text: {link.InnerText.Trim()}");
                    Console.WriteLine($"Link Href: {link.GetAttributeValue("href", "")}");
                    
                    var parent = link.ParentNode;
                    int depth = 0;
                    HtmlNode cardContainer = null;
                    
                    while (parent != null && depth < 10)
                    {
                        var classes = parent.GetAttributeValue("class", "");
                        Console.WriteLine($"Parent {depth}: {parent.Name} class='{classes}'");
                        
                        // Check if this parent contains the time element
                        var timeNode = parent.SelectSingleNode(".//time") 
                                    ?? parent.SelectSingleNode(".//div[contains(@class, 'text-gray-400')]//div[contains(text(), 'ago')]")
                                    ?? parent.SelectSingleNode(".//div[contains(@class, 'text-gray-400')]");
                                    
                        if (timeNode != null)
                        {
                             Console.WriteLine($"  -> Contains Time: {timeNode.InnerText.Trim()} (Tag: {timeNode.Name})");
                             // This is likely the container we want
                             if (cardContainer == null) cardContainer = parent;
                        }
                        
                        parent = parent.ParentNode;
                        depth++;
                    }
                }
            }
            else
            {
                Console.WriteLine("No manga links found!");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }
}
