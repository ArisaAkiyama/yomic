using System;
using System.Net.Http;
using System.Threading.Tasks;

class Program {
    static async Task Main() {
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        try {
            var html = await client.GetStringAsync("https://104.21.1.148/advanced-search/");
            Console.WriteLine(html.Substring(0, 500));
        } catch (Exception ex) {
            Console.WriteLine(ex.Message);
        }
    }
}
