using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Reflection;

namespace MyMangaApp.Core.Services
{
    public class UpdateService
    {
        private const string GITHUB_API_URL = "https://api.github.com/repos/ArisaAkiyama/yomic/releases/latest";
        private const string CURRENT_VERSION = "1.0.0"; // Should ideally come from Assembly

        public class UpdateInfo
        {
            public bool IsUpdateAvailable { get; set; }
            public string LatestVersion { get; set; } = string.Empty;
            public string DownloadUrl { get; set; } = string.Empty;
            public string ReleaseNotes { get; set; } = string.Empty;
        }

        public async Task<UpdateInfo> CheckForUpdatesAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Yomic-Desktop-App");

                var response = await client.GetStringAsync(GITHUB_API_URL);
                var json = JObject.Parse(response);

                string latestVersionTag = json["tag_name"]?.ToString() ?? string.Empty;
                string downloadUrl = json["html_url"]?.ToString() ?? string.Empty;
                string body = json["body"]?.ToString() ?? string.Empty;

                // Clean up version string (remove 'v' prefix if present)
                string cleanLatest = latestVersionTag.TrimStart('v');
                string cleanCurrent = CURRENT_VERSION.TrimStart('v');

                if (Version.TryParse(cleanLatest, out var latest) && Version.TryParse(cleanCurrent, out var current))
                {
                    if (latest > current)
                    {
                        return new UpdateInfo
                        {
                            IsUpdateAvailable = true,
                            LatestVersion = latestVersionTag,
                            DownloadUrl = downloadUrl,
                            ReleaseNotes = body
                        };
                    }
                }

                return new UpdateInfo
                {
                    IsUpdateAvailable = false,
                    LatestVersion = latestVersionTag
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Update check failed: {ex.Message}");
                // Fail silently or return error state
                return new UpdateInfo { IsUpdateAvailable = false };
            }
        }
    }
}
