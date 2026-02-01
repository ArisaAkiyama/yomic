using System;
using System.Threading.Tasks;
using PuppeteerSharp;
using System.Linq;
using System.Collections.Generic;

namespace MyMangaApp.Core.Services
{
    public class CloudflareBypassService
    {
        private static CloudflareBypassService? _instance;
        public static CloudflareBypassService Instance => _instance ??= new CloudflareBypassService();

        private IBrowser? _browser;
        private string? _userAgent;
        private Dictionary<string, string> _cookies = new Dictionary<string, string>();

        public async Task InitializeAsync()
        {
            if (_browser != null) return;

            // Determine if we need to download browser
            var browserFetcher = new BrowserFetcher();
            await browserFetcher.DownloadAsync(); // Downloads default revision

            _browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = true, // Set to false to see it in action (debug)
                Args = new[] 
                { 
                    "--no-sandbox", 
                    "--disable-setuid-sandbox",
                    "--disable-infobars",
                    "--disable-blink-features=AutomationControlled", // Critical for Stealth
                    "--enable-features=EncryptedClientHello", // Bypass SNI Blocking
                    "--dns-over-https-url=https://cloudflare-dns.com/dns-query", // Force Chrome to use DoH
                    "--ignore-certificate-errors", // Bypass SSL/TLS cert issues
                    "--ignore-ssl-errors",
                    "--allow-running-insecure-content"
                }
            });
        }

        public async Task<(string? UserAgent, Dictionary<string, string> Cookies)> GetTokensAsync(string url)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Puppeteer] Launching Browser...");
            await InitializeAsync();

            using var page = await _browser!.NewPageAsync();
            
            // STEALTH: Mask webdriver property
            await page.EvaluateFunctionOnNewDocumentAsync(@"() => {
                Object.defineProperty(navigator, 'webdriver', {
                    get: () => undefined,
                });
                // Mock Plugins
                Object.defineProperty(navigator, 'plugins', {
                    get: () => [1, 2, 3, 4, 5],
                });
            }");

            await page.SetUserAgentAsync("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            // Navigate and Wait
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Puppeteer] Navigating to {url}...");
            await page.GoToAsync(url, new NavigationOptions { Timeout = 60000, WaitUntil = new[] { WaitUntilNavigation.Networkidle0 } });

            int retries = 0;
            while (retries < 3)
            {
                var title = await page.GetTitleAsync();
                var content = await page.GetContentAsync();

                if (!content.Contains("Just a moment") && !title.Contains("Just a moment") && !content.Contains("Enable JavaScript"))
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Puppeteer] Challenge Solved!");
                    break;
                }
                
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Puppeteer] Waiting for Cloudflare Challenge... ({retries+1}/3)");
                await Task.Delay(5000);
                retries++;
            }

            // Extract Tokens
            _userAgent = await page.EvaluateExpressionAsync<string>("navigator.userAgent");
            var cookies = await page.GetCookiesAsync(url);
            
            _cookies.Clear();
            foreach (var c in cookies)
            {
                _cookies[c.Name] = c.Value;
            }

            return (_userAgent, _cookies);
        }

        public async Task<string> GetContentAsync(string url, string? waitForSelector = null)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Puppeteer] Fetching content directly: {url}");
            await InitializeAsync();
            using var page = await _browser!.NewPageAsync();
            
            // STEALTH: Mask webdriver property
            await page.EvaluateFunctionOnNewDocumentAsync(@"() => {
                Object.defineProperty(navigator, 'webdriver', {
                    get: () => undefined,
                });
            }");

            await page.SetUserAgentAsync(_userAgent ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            
            // Navigate
            // Relaxed wait condition to prevent timeouts on ad-heavy pages
            // If waitForSelector is provided, we can be more aggressive with initial navigation
            var waitCondition = waitForSelector != null ? WaitUntilNavigation.DOMContentLoaded : WaitUntilNavigation.DOMContentLoaded;
            await page.GoToAsync(url, new NavigationOptions { Timeout = 60000, WaitUntil = new[] { waitCondition } });
            
            if (!string.IsNullOrEmpty(waitForSelector))
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Puppeteer] Waiting for selector: {waitForSelector}...");
                try 
                {
                    await page.WaitForSelectorAsync(waitForSelector, new WaitForSelectorOptions { Timeout = 10000 });
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Puppeteer] Selector found!");
                }
                catch 
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Puppeteer] Selector '{waitForSelector}' timed out. Returning current content.");
                }
            }

            // Return content (usually JSON if we are hitting API)
             int retries = 0;
            while (retries < 3)
            {
                var content = await page.GetContentAsync();
                if (!content.Contains("Just a moment") && !content.Contains("Enable JavaScript"))
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Puppeteer] Content Retrieved! (Length: {content?.Length ?? 0})");
                    return content ?? string.Empty;
                }
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Puppeteer] Waiting for content... ({retries+1}/3)");
                await Task.Delay(3000);
                retries++;
            }
            
            
            return await page.GetContentAsync() ?? string.Empty;
        }

        public async Task<string> EvaluateScriptAsync(string url, string script)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Puppeteer] Executing Script on: {url}");
            await InitializeAsync();
            using var page = await _browser!.NewPageAsync();
            await page.SetUserAgentAsync(_userAgent ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            
            await page.GoToAsync(url, new NavigationOptions { Timeout = 60000, WaitUntil = new[] { WaitUntilNavigation.Networkidle0 } });
            
            // Wait for hydration (simple delay or selector)
            await Task.Delay(5000); 

            var result = await page.EvaluateFunctionAsync<string>(script);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Puppeteer] Script Result Length: {result?.Length ?? 0}");
            return result ?? "[]";
        }

        public async Task DisposeAsync()
        {
            if (_browser != null)
            {
                await _browser.CloseAsync();
                await _browser.DisposeAsync();
                _browser = null;
            }
        }
    }
}
