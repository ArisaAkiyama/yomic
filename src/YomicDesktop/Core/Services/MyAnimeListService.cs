using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Diagnostics;
using Yomic.Core.Models;
using System.Security.Cryptography;

namespace Yomic.Core.Services
{
    public class MyAnimeListService : IDisposable
    {
        private const string ClientId = "2178a6ea66e6d2c4356ab0fd4d298ce4"; // Public client ID for Yomic desktop client
        private const string RedirectUri = "http://127.0.0.1:49152/";
        private const string TokenUrl = "https://myanimelist.net/v1/oauth2/token";
        
        private readonly SettingsService _settingsService;
        private readonly HttpClient _httpClient;

        public MyAnimeListService(SettingsService settingsService)
        {
            _settingsService = settingsService;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        }

        public bool IsConnected => !string.IsNullOrEmpty(_settingsService.MalAccessToken);

        public string GetAuthorizationUrl(string codeChallenge, string? state = null)
        {
            var url = $"https://myanimelist.net/v1/oauth2/authorize?response_type=code" +
                      $"&client_id={ClientId}" +
                      $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
                      $"&code_challenge_method=plain" +
                      $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}";
            if (!string.IsNullOrEmpty(state))
            {
                url += $"&state={Uri.EscapeDataString(state)}";
            }
            return url;
        }

        public static string GenerateOAuthState()
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static string GenerateCodeVerifier()
        {
            // Generate a cryptographically unbiased 128-character PKCE verifier
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
            var result = new StringBuilder(128);
            for (int i = 0; i < 128; i++)
            {
                result.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);
            }
            return result.ToString();
        }

        public async Task<bool> AuthenticateAsync(string authCode, string codeVerifier)
        {
            try
            {
                var values = new Dictionary<string, string>
                {
                    { "client_id", ClientId },
                    { "grant_type", "authorization_code" },
                    { "code", authCode },
                    { "code_verifier", codeVerifier },
                    { "redirect_uri", RedirectUri }
                };

                using var content = new FormUrlEncodedContent(values);
                using var response = await _httpClient.PostAsync(TokenUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync();
                    LogService.Error("MyAnimeList", $"Token exchange failed: {err}");
                    return false;
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                _settingsService.MalAccessToken = root.GetProperty("access_token").GetString() ?? "";
                _settingsService.MalRefreshToken = root.GetProperty("refresh_token").GetString() ?? "";
                var expiresIn = root.GetProperty("expires_in").GetInt64();
                _settingsService.MalTokenExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (expiresIn * 1000);
                _settingsService.Save();

                LogService.Success("MyAnimeList", "Successfully authenticated with MyAnimeList!");
                return true;
            }
            catch (Exception ex)
            {
                LogService.Error("MyAnimeList", "Authentication exception", ex);
                return false;
            }
        }

        private int _authEpoch = 0;

        internal int CurrentAuthEpoch => System.Threading.Volatile.Read(ref _authEpoch);

        internal bool TryApplyRefreshedTokens(int capturedEpoch, string accessToken, string refreshToken, long expiresAtMs)
        {
            if (System.Threading.Volatile.Read(ref _authEpoch) != capturedEpoch)
            {
                LogService.Info("MyAnimeList", "Discarded stale token refresh result because account was disconnected.");
                return false;
            }
            if (!string.IsNullOrEmpty(accessToken)) _settingsService.MalAccessToken = accessToken;
            if (!string.IsNullOrEmpty(refreshToken)) _settingsService.MalRefreshToken = refreshToken;
            if (expiresAtMs > 0) _settingsService.MalTokenExpiresAt = expiresAtMs;
            _settingsService.SaveImmediate();
            return true;
        }

        public void Disconnect()
        {
            System.Threading.Interlocked.Increment(ref _authEpoch);
            _settingsService.MalAccessToken = "";
            _settingsService.MalRefreshToken = "";
            _settingsService.MalTokenExpiresAt = 0;
            _settingsService.MalUsername = "";
            _settingsService.Save();
            LogService.Info("MyAnimeList", "Disconnected account.");
        }

        public async Task<string> FetchUserProfileNameAsync()
        {
            if (!IsConnected) return "";
            int capturedEpoch = System.Threading.Volatile.Read(ref _authEpoch);

            try
            {
                using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Get, "https://api.myanimelist.net/v2/users/@me");
                if (request.Headers.Authorization == null || System.Threading.Volatile.Read(ref _authEpoch) != capturedEpoch)
                {
                    return "";
                }
                using var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode) return "";

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("name", out var nameProp))
                {
                    var username = nameProp.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(username) &&
                        System.Threading.Volatile.Read(ref _authEpoch) == capturedEpoch &&
                        IsConnected)
                    {
                        _settingsService.MalUsername = username;
                        _settingsService.Save();
                    }
                    return username;
                }
            }
            catch (Exception ex)
            {
                LogService.Warning("MyAnimeList", $"Failed to fetch user profile name: {ex.Message}");
            }

            return "";
        }

        private readonly System.Threading.SemaphoreSlim _refreshLock = new(1, 1);

        private async Task EnsureValidTokenAsync()
        {
            if (!IsConnected) return;

            // Refresh token 5 minutes before it expires (serialized to prevent concurrent refresh_token rotation failure)
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now + 300000 >= _settingsService.MalTokenExpiresAt)
            {
                await _refreshLock.WaitAsync();
                try
                {
                    if (!IsConnected) return;
                    var recheckNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (recheckNow + 300000 >= _settingsService.MalTokenExpiresAt)
                    {
                        await RefreshTokenAsync();
                    }
                }
                finally
                {
                    _refreshLock.Release();
                }
            }
        }

        private async Task<bool> RefreshTokenAsync()
        {
            int capturedEpoch = System.Threading.Volatile.Read(ref _authEpoch);
            string currentRefreshToken = _settingsService.MalRefreshToken;
            if (string.IsNullOrEmpty(currentRefreshToken))
            {
                return false;
            }

            try
            {
                LogService.Info("MyAnimeList", "Refreshing Access Token...");
                var values = new Dictionary<string, string>
                {
                    { "client_id", ClientId },
                    { "grant_type", "refresh_token" },
                    { "refresh_token", currentRefreshToken }
                };

                using var content = new FormUrlEncodedContent(values);
                using var response = await _httpClient.PostAsync(TokenUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync();
                    LogService.Error("MyAnimeList", $"Token refresh failed: {err}");
                    return false;
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string newAccess = root.TryGetProperty("access_token", out var atProp) && atProp.ValueKind == JsonValueKind.String ? (atProp.GetString() ?? "") : "";
                string newRefresh = root.TryGetProperty("refresh_token", out var rtProp) && rtProp.ValueKind == JsonValueKind.String ? (rtProp.GetString() ?? "") : "";
                long newExpires = root.TryGetProperty("expires_in", out var expProp) && expProp.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (expProp.GetInt64() * 1000)
                    : 0;

                if (!TryApplyRefreshedTokens(capturedEpoch, newAccess, newRefresh, newExpires))
                {
                    return false;
                }

                LogService.Success("MyAnimeList", "Token refreshed successfully.");
                return true;
            }
            catch (Exception ex)
            {
                LogService.Error("MyAnimeList", "Token refresh exception", ex);
                return false;
            }
        }

        private async Task<HttpRequestMessage> CreateAuthenticatedRequestAsync(HttpMethod method, string url)
        {
            await EnsureValidTokenAsync();

            var req = new HttpRequestMessage(method, url);
            string token = _settingsService.MalAccessToken;
            if (!string.IsNullOrWhiteSpace(token))
            {
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }
            return req;
        }

        // Search Manga on MyAnimeList
        public async Task<List<MalSearchResult>> SearchMangaAsync(string query)
        {
            var results = new List<MalSearchResult>();
            if (!IsConnected) return results;

            try
            {
                var url = $"https://api.myanimelist.net/v2/manga?q={Uri.EscapeDataString(query)}&limit=10&fields=main_picture,my_list_status,num_chapters&nsfw=true";
                using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Get, url);
                using var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode) return results;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var dataArray))
                {
                    foreach (var item in dataArray.EnumerateArray())
                    {
                        var node = item.GetProperty("node");
                        var id = node.GetProperty("id").GetInt64();
                        var title = node.GetProperty("title").GetString() ?? "";
                        string? coverUrl = null;
                        if (node.TryGetProperty("main_picture", out var pictureObj) &&
                            pictureObj.TryGetProperty("medium", out var medProp))
                        {
                            coverUrl = medProp.GetString();
                        }

                        int totalChapters = node.TryGetProperty("num_chapters", out var numChProp) && numChProp.ValueKind == JsonValueKind.Number
                            ? numChProp.GetInt32()
                            : 0;

                        bool inUserList = false;
                        string? userStatus = null;
                        int userChaptersRead = 0;
                        int userScore = 0;

                        if (node.TryGetProperty("my_list_status", out var myStatusObj) && myStatusObj.ValueKind == JsonValueKind.Object)
                        {
                            inUserList = true;
                            if (myStatusObj.TryGetProperty("status", out var stProp))
                                userStatus = stProp.GetString();
                            if (myStatusObj.TryGetProperty("num_chapters_read", out var crProp) && crProp.ValueKind == JsonValueKind.Number)
                                userChaptersRead = crProp.GetInt32();
                            if (myStatusObj.TryGetProperty("score", out var scProp) && scProp.ValueKind == JsonValueKind.Number)
                                userScore = scProp.GetInt32();
                        }

                        results.Add(new MalSearchResult
                        {
                            Id = id,
                            Title = title,
                            CoverUrl = coverUrl,
                            InUserList = inUserList,
                            UserStatus = userStatus,
                            UserChaptersRead = userChaptersRead,
                            UserScore = userScore,
                            TotalChapters = totalChapters
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Error("MyAnimeList", "Search exception", ex);
            }

            return results;
        }

        // Fetch User List Status for a specific MAL Manga ID
        public async Task<MangaTrack?> FetchMangaStatusAsync(long malId, long localMangaId)
        {
            if (!IsConnected) return null;

            try
            {
                var url = $"https://api.myanimelist.net/v2/manga/{malId}?fields=my_list_status,num_chapters";
                using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Get, url);
                using var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode) return null;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var title = root.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String
                    ? titleProp.GetString() ?? ""
                    : "";
                var totalChapters = root.TryGetProperty("num_chapters", out var chaptersProp) && chaptersProp.ValueKind == JsonValueKind.Number
                    ? chaptersProp.GetInt32()
                    : 0;
                
                string status = "reading";
                int score = 0;
                int readChapters = 0;

                if (root.TryGetProperty("my_list_status", out var myStatusObj) && myStatusObj.ValueKind == JsonValueKind.Object)
                {
                    status = myStatusObj.TryGetProperty("status", out var sProp) && sProp.ValueKind == JsonValueKind.String
                        ? sProp.GetString() ?? "reading"
                        : "reading";
                    score = myStatusObj.TryGetProperty("score", out var scProp) && scProp.ValueKind == JsonValueKind.Number
                        ? scProp.GetInt32()
                        : 0;
                    readChapters = myStatusObj.TryGetProperty("num_chapters_read", out var numProp) && numProp.ValueKind == JsonValueKind.Number
                        ? numProp.GetInt32()
                        : 0;
                }

                return new MangaTrack
                {
                    MangaId = localMangaId,
                    RemoteId = malId,
                    Title = title,
                    TotalChapters = totalChapters,
                    Status = status,
                    Score = score,
                    LastChapterRead = readChapters
                };
            }
            catch (Exception ex)
            {
                LogService.Error("MyAnimeList", "Fetch status exception", ex);
                return null;
            }
        }

        // Update User List Status for a Manga on MyAnimeList
        public async Task<bool> UpdateMangaStatusAsync(long malId, string status, int numChaptersRead, int score)
        {
            if (!IsConnected) return false;

            try
            {
                var url = $"https://api.myanimelist.net/v2/manga/{malId}/my_list_status";
                using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Patch, url);

                var values = new Dictionary<string, string>
                {
                    { "status", status },
                    { "num_chapters_read", numChaptersRead.ToString() },
                    { "score", score.ToString() }
                };

                using var formContent = new FormUrlEncodedContent(values);
                request.Content = formContent;
                using var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync();
                    LogService.Error("MyAnimeList", $"Update list status failed: {err}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                LogService.Error("MyAnimeList", "Update status exception", ex);
                return false;
            }
        }

        public void Dispose()
        {
            try { _httpClient.Dispose(); } catch { }
            try { _refreshLock.Dispose(); } catch { }
        }
    }

    public class MalSearchResult
    {
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public string? CoverUrl { get; set; }
        public bool InUserList { get; set; }
        public string? UserStatus { get; set; }
        public int UserChaptersRead { get; set; }
        public int UserScore { get; set; }
        public int TotalChapters { get; set; }
    }
}
