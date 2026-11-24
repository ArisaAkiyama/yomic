using System;
using System.Net.Http;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;
using System.Collections.Concurrent;
using Jint;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Object;
using Yomic.Core.Models;

namespace Yomic.Core.Sources
{
    public class JsMangaSource : HttpSource, IFilterableMangaSource
    {
        private readonly string _scriptPath;
        private string _name = "";
        private string _baseUrl = "";
        private string _apiUrl = "";
        private string _language = "EN";
        private string _version = "1.0.0";
        private string _description = "JS Manga Source";
        private string _author = "Unknown";
        private string _iconUrl = "";
        private string _iconBackground = "#313244";
        private string _iconForeground = "#FF9900";
        private bool _isNsfw = false;
        private bool _isHasMorePages = true;
        private bool _requiresProxy = false;
        private bool _isCloudflareProtected = false;
        private string _chapterReadySelector = "img[data-chapter-page], [data-reader-chapter-id] img";
        private string? _pageWebViewExtractScript = null;
        private string? _userAgent = null;

        public bool IsCloudflareProtected => _isCloudflareProtected;
        public string ChapterReadySelector => _chapterReadySelector;
        public string? PageWebViewExtractScript => _pageWebViewExtractScript;

        private static readonly SemaphoreSlim _globalExecutionLimit = new(14, 14);
        private readonly ConcurrentQueue<Engine> _enginePool = new();
        private string _scriptCode = "";
        private bool _supportsStatusFilter;
        private long _id;
        private List<string> _availableGenres = new();
        private List<string> _availableFormats = new();

        private static volatile string _selectedLanguage = "en";
        public static string SelectedLanguage
        {
            get => _selectedLanguage;
            set => _selectedLanguage = value;
        }

        public override string Name => _name;
        public override string BaseUrl => _baseUrl;
        public override string ApiUrl => _apiUrl;
        public override string Language => _language;
        public override string Version => _version;
        public override string Description => _description;
        public override string Author => _author;
        public override string IconUrl => _iconUrl;
        public override string IconBackground => _iconBackground;
        public override string IconForeground => _iconForeground;
        public override bool IsNsfw => _isNsfw;
        public override bool IsHasMorePages => _isHasMorePages;
        public bool SupportsStatusFilter => _supportsStatusFilter;
        public bool SupportsGenreFilter => _availableGenres.Count > 0;
        public List<string> AvailableGenres => _availableGenres;
        public bool SupportsFormatFilter => _availableFormats.Count > 0;
        public List<string> AvailableFormats => _availableFormats;
        public override bool RequiresProxy => _requiresProxy;

        protected override void ConfigureClient(System.Net.Http.HttpClient client)
        {
            base.ConfigureClient(client);
            if (!string.IsNullOrEmpty(_userAgent))
            {
                client.DefaultRequestHeaders.UserAgent.Clear();
                client.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);
            }
        }

        public JsMangaSource(string scriptPath)
        {
            _scriptPath = scriptPath;
            Initialize();
        }

        private void Initialize()
        {
            _scriptCode = File.ReadAllText(_scriptPath);
            var engine = CreateEngine();
            _supportsStatusFilter = engine.Invoke("__hasMethod", "getMangaList").AsBoolean();

            // Read metadata properties from the "source" object
            var sourceObj = engine.GetValue("source");
            if (sourceObj != null && sourceObj.IsObject())
            {
                var obj = sourceObj.AsObject();
                
                if (obj.HasProperty("name")) _name = obj.Get("name").AsString();
                if (obj.HasProperty("baseUrl")) _baseUrl = obj.Get("baseUrl").AsString();
                if (obj.HasProperty("apiUrl")) _apiUrl = obj.Get("apiUrl").AsString();
                if (obj.HasProperty("language")) _language = obj.Get("language").AsString();
                if (obj.HasProperty("version")) _version = obj.Get("version").AsString();
                if (obj.HasProperty("description")) _description = obj.Get("description").AsString();
                if (obj.HasProperty("author")) _author = obj.Get("author").AsString();
                if (obj.HasProperty("iconUrl")) _iconUrl = obj.Get("iconUrl").AsString();
                
                if (string.IsNullOrEmpty(_iconUrl) && !string.IsNullOrEmpty(_baseUrl))
                {
                    if (Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri))
                    {
                        _iconUrl = $"https://www.google.com/s2/favicons?domain={uri.Host}&sz=128";
                    }
                }
                if (obj.HasProperty("iconBackground")) _iconBackground = obj.Get("iconBackground").AsString();
                if (obj.HasProperty("iconForeground")) _iconForeground = obj.Get("iconForeground").AsString();
                if (obj.HasProperty("isHasMorePages")) _isHasMorePages = obj.Get("isHasMorePages").AsBoolean();
                bool parsedNsfw = false;
                if (obj.HasProperty("isNsfw"))
                {
                    var nsfwVal = obj.Get("isNsfw");
                    if (nsfwVal.IsBoolean()) parsedNsfw = nsfwVal.AsBoolean();
                    else if (nsfwVal.IsNumber()) parsedNsfw = nsfwVal.AsNumber() > 0;
                    else if (nsfwVal.IsString()) parsedNsfw = bool.TryParse(nsfwVal.AsString(), out var b) ? b : nsfwVal.AsString() == "1";
                }
                if (!parsedNsfw && obj.HasProperty("nsfw"))
                {
                    var nsfwVal = obj.Get("nsfw");
                    if (nsfwVal.IsBoolean()) parsedNsfw = nsfwVal.AsBoolean();
                    else if (nsfwVal.IsNumber()) parsedNsfw = nsfwVal.AsNumber() > 0;
                    else if (nsfwVal.IsString()) parsedNsfw = bool.TryParse(nsfwVal.AsString(), out var b) ? b : nsfwVal.AsString() == "1";
                }
                if (!parsedNsfw)
                {
                    var adultKeywords = new[]
                    {
                        "18+", "dewasa", "hentai", "doujin", "smut", "crot", "porn", "xxx",
                        "ecchi", "eromanga", "nhentai", "nekopoi", "mangapoi", "kumapoi", "kumopoi",
                        "mangasusu", "r-18", "r18", "mature", "adult", "guro", "yaoi", "yuri"
                    };
                    var combined = $"{_name} {_description} {_baseUrl}".ToLowerInvariant();
                    if (adultKeywords.Any(kw => combined.Contains(kw, StringComparison.Ordinal)))
                    {
                        parsedNsfw = true;
                    }
                }
                _isNsfw = parsedNsfw;
                if (obj.HasProperty("requiresProxy")) _requiresProxy = obj.Get("requiresProxy").AsBoolean();
                if (obj.HasProperty("isCloudflareProtected")) _isCloudflareProtected = obj.Get("isCloudflareProtected").AsBoolean();
                if (obj.HasProperty("chapterReadySelector"))
                {
                    var sel = obj.Get("chapterReadySelector");
                    if (sel.IsString() && !string.IsNullOrWhiteSpace(sel.AsString()))
                        _chapterReadySelector = sel.AsString();
                }
                if (obj.HasProperty("pageWebViewExtractScript"))
                {
                    var scr = obj.Get("pageWebViewExtractScript");
                    if (scr.IsString() && !string.IsNullOrWhiteSpace(scr.AsString()))
                        _pageWebViewExtractScript = scr.AsString();
                }
                if (obj.HasProperty("userAgent")) _userAgent = obj.Get("userAgent").AsString();

                if (string.IsNullOrEmpty(_userAgent) &&
                    ((_name ?? "").Contains("MangaDex", StringComparison.OrdinalIgnoreCase) ||
                     (_baseUrl ?? "").Contains("mangadex.org", StringComparison.OrdinalIgnoreCase)))
                {
                    _userAgent = "Yomic/1.0.3";
                }

                if (obj.HasProperty("genres"))
                {
                    _availableGenres = ParseStringListFromJs(obj.Get("genres"));
                }
                if (obj.HasProperty("formats"))
                {
                    _availableFormats = ParseStringListFromJs(obj.Get("formats"));
                }

                var idVal = obj.Get("id");
                if (idVal.IsNumber())
                {
                    _id = (long)idVal.AsNumber();
                }
                else
                {
                    _id = GenerateStableId();
                }
            }
            else
            {
                throw new Exception("Script does not define a global 'source' object.");
            }
        }

        private Engine CreateEngine()
        {
            var engine = new Engine(cfg => cfg
                .TimeoutInterval(TimeSpan.FromSeconds(30))
                .MaxStatements(2_000_000)
                .LimitRecursion(64)
                .LimitMemory(64 * 1024 * 1024)
                .MaxArraySize(100_000));

            engine.SetValue("fetch", new Func<string, JsValue, JsResponse>(FetchUrl));
            engine.SetValue("Html", new
            {
                parse = new Func<string, string, JsDocument>(HtmlParser.parse)
            });
            engine.SetValue("log", new Action<object>(o => {
                if (!SilenceLogs) Console.WriteLine($"[JS Extension Log] {o}");
            }));
            engine.SetValue("atob", new Func<string, string>(DecodeBase64Latin1));
            engine.SetValue("btoa", new Func<string, string>(EncodeBase64Latin1));

            engine.Execute(_scriptCode);
            ApplySelectedLanguageToEngine(engine);
            engine.Execute(@"
                globalThis.__callMethod = function(methodName, ...args) {
                    return source[methodName].apply(source, args);
                };
                globalThis.__hasMethod = function(methodName) {
                    return typeof source === 'object' && source !== null && typeof source[methodName] === 'function';
                };
            ");

            return engine;
        }

        private static void ApplySelectedLanguageToEngine(Engine engine)
        {
            var sourceVal = engine.GetValue("source");
            if (sourceVal.IsObject())
            {
                sourceVal.AsObject().Set("selectedLanguage", _selectedLanguage);
            }
        }

        private Engine RentEngine()
        {
            if (_enginePool.TryDequeue(out var engine))
            {
                // IMPORTANT: Always refresh selectedLanguage before reuse.
                // The engine pool caches engines, so if user changes language,
                // old engines from the pool still have the previous language.
                ApplySelectedLanguageToEngine(engine);
                return engine;
            }
            return CreateEngine();
        }

        private void ReturnEngine(Engine engine)
        {
            if (_enginePool.Count < 2)
            {
                _enginePool.Enqueue(engine);
            }
        }

        private sealed class ChapterCacheContext
        {
            public string? RequestETag;
            public string? RequestLastModified;
            public string? ExtractedETag;
            public string? ExtractedLastModified;
            public bool Was304;
            public bool WasPartial;
        }

        private static readonly System.Threading.AsyncLocal<ChapterCacheContext?> _asyncCacheContext = new();

        private async Task<T> ExecuteJsAsync<T>(Func<Engine, T> action)
        {
            await _globalExecutionLimit.WaitAsync();
            Engine? engine = null;
            bool succeeded = false;
            try
            {
                return await Task.Run(() =>
                {
                    engine = RentEngine();
                    var result = action(engine);
                    succeeded = true;
                    return result;
                });
            }
            finally
            {
                // Only recycle healthy engines back to the pool; ALWAYS release semaphore even if RentEngine throws
                if (succeeded && engine != null)
                {
                    ReturnEngine(engine);
                }
                _globalExecutionLimit.Release();
            }
        }

        private long GenerateStableId()
        {
            var hashName = "JS_" + Name + "_" + Language;
            var hash = System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes(hashName));
            return BitConverter.ToInt64(hash, 0);
        }

        private JsResponse FetchUrl(string url, JsValue options)
        {
            try
            {
                if (!Yomic.Core.Helpers.UrlSecurityHelper.IsSafePublicHttpUrl(url, out var validatedUri) || validatedUri == null)
                {
                    return new JsResponse { body = "Blocked unsafe URL.", status = 403 };
                }

                var method = HttpMethod.Get;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? postBody = null;

                if (options != null && options.IsObject())
                {
                    var opts = options.AsObject();
                    var mVal = opts.Get("method");
                    if (mVal.IsString())
                    {
                        var mStr = mVal.AsString().ToUpperInvariant();
                        method = mStr switch
                        {
                            "POST" => HttpMethod.Post,
                            "PUT" => HttpMethod.Put,
                            "DELETE" => HttpMethod.Delete,
                            "PATCH" => HttpMethod.Patch,
                            "HEAD" => HttpMethod.Head,
                            _ => HttpMethod.Get
                        };
                    }

                    var hVal = opts.Get("headers");
                    if (hVal.IsObject())
                    {
                        var hObj = hVal.AsObject();
                        foreach (var key in hObj.GetOwnPropertyKeys())
                        {
                            var keyStr = key.AsString();
                            var propVal = hObj.Get(keyStr);
                            if (propVal.IsString())
                                headers[keyStr] = propVal.AsString();
                            else if (!propVal.IsNull() && !propVal.IsUndefined())
                                headers[keyStr] = propVal.ToString();
                        }
                    }

                    var bVal = opts.Get("body");
                    if (bVal.IsString())
                    {
                        postBody = bVal.AsString();
                    }
                }

                var cacheCtx = _asyncCacheContext.Value;
                if (cacheCtx != null)
                {
                    if (!string.IsNullOrEmpty(cacheCtx.RequestETag) && !headers.ContainsKey("If-None-Match"))
                    {
                        headers["If-None-Match"] = cacheCtx.RequestETag;
                    }
                    if (!string.IsNullOrEmpty(cacheCtx.RequestLastModified) && !headers.ContainsKey("If-Modified-Since"))
                    {
                        headers["If-Modified-Since"] = cacheCtx.RequestLastModified;
                    }
                }

                if (Yomic.Core.Services.CloudflareBypassService.IsInstanceCreated)
                {
                    var cfBypass = Yomic.Core.Services.CloudflareBypassService.Instance;
                    var savedCookies = cfBypass.SavedCookies;
                    bool matchedHostCookies = false;
                    if (savedCookies.Count > 0 && !headers.ContainsKey("Cookie"))
                    {
                        var matchingCookies = savedCookies
                            .Where(c => Yomic.Core.Helpers.UrlSecurityHelper.CookieMatchesRequest(validatedUri, c.Domain, c.Secure ?? false))
                            .Select(c => $"{c.Name}={c.Value}")
                            .ToList();
                        if (matchingCookies.Count > 0)
                        {
                            headers["Cookie"] = string.Join("; ", matchingCookies);
                            matchedHostCookies = true;
                        }
                    }
                    // Only override User-Agent with CF bypass UA when this host actually uses CF cookies (never clobber MangaDex custom UA)
                    if (matchedHostCookies && !string.IsNullOrEmpty(cfBypass.BypassUserAgent) && !headers.ContainsKey("User-Agent"))
                    {
                        headers["User-Agent"] = cfBypass.BypassUserAgent;
                    }
                }

                const long MaxJsFetchBytes = 25L * 1024 * 1024;
                using var reqCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

                // If it's a GET and no custom headers, check media type or binary bytes cleanly
                if (method == HttpMethod.Get && headers.Count == 0)
                {
                    using var responseNoHead = Client.GetAsync(validatedUri, HttpCompletionOption.ResponseHeadersRead, reqCts.Token).GetAwaiter().GetResult();
                    if (responseNoHead.StatusCode == System.Net.HttpStatusCode.NotModified)
                    {
                        if (cacheCtx != null) cacheCtx.Was304 = true;
                        return new JsResponse { body = "", status = 304 };
                    }

                    if (responseNoHead.StatusCode == System.Net.HttpStatusCode.OK && cacheCtx != null)
                    {
                        if (responseNoHead.Headers.ETag != null) cacheCtx.ExtractedETag = responseNoHead.Headers.ETag.Tag;
                        if (responseNoHead.Content.Headers.LastModified != null) cacheCtx.ExtractedLastModified = responseNoHead.Content.Headers.LastModified.Value.ToString("r");
                    }

                    var bytesNoHead = Yomic.Core.Helpers.UrlSecurityHelper.ReadContentBoundedAsync(
                        responseNoHead.Content, MaxJsFetchBytes, reqCts.Token).GetAwaiter().GetResult();
                    var mediaTypeNoHead = responseNoHead.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                    string content;
                    if (mediaTypeNoHead.Contains("protobuf") || mediaTypeNoHead.Contains("octet-stream") || Array.IndexOf(bytesNoHead, (byte)0) >= 0)
                    {
                        content = System.Text.Encoding.Latin1.GetString(bytesNoHead);
                    }
                    else
                    {
                        content = System.Text.Encoding.UTF8.GetString(bytesNoHead);
                    }
                    return new JsResponse { body = content, status = (int)responseNoHead.StatusCode };
                }

                // Otherwise make a custom request using our client, properly preserving Content-Type
                using var request = new HttpRequestMessage(method, validatedUri)
                {
                    Version = System.Net.HttpVersion.Version20,
                    VersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower
                };
                string? requestedContentType = null;
                foreach (var h in headers)
                {
                    if (string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        requestedContentType = h.Value;
                        continue;
                    }
                    request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
                if (postBody != null)
                {
                    string mediaTypeOnly = "application/json";
                    if (!string.IsNullOrWhiteSpace(requestedContentType))
                    {
                        mediaTypeOnly = requestedContentType.Split(';')[0].Trim();
                    }
                    request.Content = new StringContent(postBody, System.Text.Encoding.UTF8, mediaTypeOnly);
                    if (!string.IsNullOrWhiteSpace(requestedContentType) && requestedContentType.Contains(';'))
                    {
                        // Remove default Content-Type before re-adding full parameterized header to prevent duplicates
                        request.Content.Headers.Remove("Content-Type");
                        request.Content.Headers.TryAddWithoutValidation("Content-Type", requestedContentType);
                    }
                }

                using var response = Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, reqCts.Token).GetAwaiter().GetResult();

                if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    if (cacheCtx != null) cacheCtx.Was304 = true;
                    return new JsResponse { body = "", status = 304 };
                }

                if (response.StatusCode == System.Net.HttpStatusCode.OK && cacheCtx != null)
                {
                    if (response.Headers.ETag != null) cacheCtx.ExtractedETag = response.Headers.ETag.Tag;
                    if (response.Content.Headers.LastModified != null) cacheCtx.ExtractedLastModified = response.Content.Headers.LastModified.Value.ToString("r");
                }

                var responseBytes = Yomic.Core.Helpers.UrlSecurityHelper.ReadContentBoundedAsync(
                    response.Content, MaxJsFetchBytes, reqCts.Token).GetAwaiter().GetResult();
                var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                string bodyText;
                if (mediaType.Contains("protobuf") || mediaType.Contains("octet-stream") || Array.IndexOf(responseBytes, (byte)0) >= 0)
                {
                    bodyText = System.Text.Encoding.Latin1.GetString(responseBytes);
                }
                else
                {
                    bodyText = System.Text.Encoding.UTF8.GetString(responseBytes);
                }
                if (!SilenceLogs) Console.WriteLine($"[JsMangaSource.FetchUrl] {method} {url} -> Status {(int)response.StatusCode}, Bytes {responseBytes.Length}");
                return new JsResponse { body = bodyText, status = (int)response.StatusCode };
            }
            catch (Exception ex)
            {
                if (!SilenceLogs) Console.WriteLine($"[JsMangaSource] Fetch failed for {url}. Error: {ex}");
                return new JsResponse { body = ex.Message, status = 500 };
            }
        }
        
        public override long Id => _id;

        public override async Task<List<Manga>> GetPopularMangaAsync(int page)
        {
            return await ExecuteJsAsync(engine =>
            {
                var hasMethod = engine.Invoke("__hasMethod", "getPopularManga").AsBoolean();
                if (!hasMethod) return new List<Manga>();

                var jsResult = engine.Invoke("__callMethod", "getPopularManga", page);
                return ParseMangaListFromJs(jsResult);
            });
        }

        public override async Task<List<Manga>> GetSearchMangaAsync(string query, int page)
        {
            return await ExecuteJsAsync(engine =>
            {
                var methodName = "getSearchManga";
                var hasMethod = engine.Invoke("__hasMethod", methodName).AsBoolean();
                if (!hasMethod)
                {
                    methodName = "searchManga";
                    hasMethod = engine.Invoke("__hasMethod", methodName).AsBoolean();
                }

                if (!hasMethod) return new List<Manga>();

                var jsResult = engine.Invoke("__callMethod", methodName, query, page);
                return ParseMangaListFromJs(jsResult);
            });
        }

        private (List<Manga> Items, int TotalPages) ParsePagedMangaListFromJs(Jint.Native.JsValue jsResult, int currentPage)
        {
            if (jsResult.IsArray())
            {
                var items = ParseMangaListFromJs(jsResult);
                return (items, items.Count > 0 ? currentPage + 1 : currentPage);
            }
            else if (jsResult.IsObject())
            {
                var obj = jsResult.AsObject();
                var itemsJs = obj.Get("items");
                var items = itemsJs.IsArray() ? ParseMangaListFromJs(itemsJs) : new List<Manga>();
                
                var totalPagesJs = obj.Get("totalPages");
                int totalPages = totalPagesJs.IsNumber() ? (int)totalPagesJs.AsNumber() : (items.Count > 0 ? currentPage + 1 : currentPage);
                
                return (items, totalPages);
            }
            return (new List<Manga>(), currentPage);
        }

        public async Task<(List<Manga> Items, int TotalPages)> GetLatestMangaAsync(int page)
        {
            return await ExecuteJsAsync(engine =>
            {
                var hasMethod = engine.Invoke("__hasMethod", "getLatestUpdates").AsBoolean();
                if (!hasMethod) return (new List<Manga>(), page);

                var jsResult = engine.Invoke("__callMethod", "getLatestUpdates", page);
                return ParsePagedMangaListFromJs(jsResult, page);
            });
        }

        public async Task<(List<Manga> Items, int TotalPages)> GetMangaListAsync(int page)
        {
            return await ExecuteJsAsync(engine =>
            {
                var hasMethod = engine.Invoke("__hasMethod", "getPopularManga").AsBoolean();
                if (!hasMethod) return (new List<Manga>(), page);

                var jsResult = engine.Invoke("__callMethod", "getPopularManga", page);
                return ParsePagedMangaListFromJs(jsResult, page);
            });
        }

        public async Task<(List<Manga> Items, int TotalPages)> GetMangaListAsync(int page, int status)
        {
            return await GetMangaListAsync(page, status, null, null);
        }

        public async Task<(List<Manga> Items, int TotalPages)> GetMangaListAsync(int page, int status, List<string>? genres)
        {
            return await GetMangaListAsync(page, status, genres, null);
        }

        public async Task<(List<Manga> Items, int TotalPages)> GetMangaListAsync(int page, int status, List<string>? genres, List<string>? formats)
        {
            return await GetMangaListAsync(page, status, genres, formats, null);
        }

        public async Task<(List<Manga> Items, int TotalPages)> GetMangaListAsync(int page, int status, List<string>? genres, List<string>? formats, string? sort)
        {
            return await ExecuteJsAsync(engine =>
            {
                var hasFilteredMethod = engine.Invoke("__hasMethod", "getMangaList").AsBoolean();
                if (hasFilteredMethod)
                {
                    var genresJs = genres != null ? JsValue.FromObject(engine, genres.ToArray()) : JsValue.Undefined;
                    var formatsJs = formats != null ? JsValue.FromObject(engine, formats.ToArray()) : JsValue.Undefined;
                    var sortJs = !string.IsNullOrEmpty(sort) ? (JsValue)sort : JsValue.Undefined;
                    var jsResult = engine.Invoke("__callMethod", "getMangaList", page, status, genresJs, formatsJs, sortJs);
                    return ParsePagedMangaListFromJs(jsResult, page);
                }

                if (string.Equals(sort, "latest", StringComparison.OrdinalIgnoreCase))
                {
                    var hasLatestMethod = engine.Invoke("__hasMethod", "getLatestUpdates").AsBoolean();
                    if (hasLatestMethod)
                    {
                        var latestResult = engine.Invoke("__callMethod", "getLatestUpdates", page);
                        return ParsePagedMangaListFromJs(latestResult, page);
                    }
                }

                var hasMethod = engine.Invoke("__hasMethod", "getPopularManga").AsBoolean();
                if (!hasMethod) return (new List<Manga>(), page);

                var fallbackResult = engine.Invoke("__callMethod", "getPopularManga", page);
                return ParsePagedMangaListFromJs(fallbackResult, page);
            });
        }

        public override async Task<Manga> GetMangaDetailsAsync(string url)
        {
            return await ExecuteJsAsync(engine =>
            {
                var hasMethod = engine.Invoke("__hasMethod", "getMangaDetails").AsBoolean();
                if (!hasMethod) return new Manga();

                var jsResult = engine.Invoke("__callMethod", "getMangaDetails", url);
                if (jsResult.IsObject())
                {
                    var obj = jsResult.AsObject();
                    var thumbUrl = GetSafeString(obj, "thumbnailUrl");
                    if (!string.IsNullOrEmpty(thumbUrl) && !string.IsNullOrEmpty(_userAgent) && !thumbUrl.Contains("|UserAgent="))
                    {
                        thumbUrl += $"|UserAgent={_userAgent}";
                    }

                    return new Manga
                    {
                        Title = GetSafeString(obj, "title"),
                        Url = GetSafeString(obj, "url"),
                        ThumbnailUrl = thumbUrl,
                        Author = GetSafeString(obj, "author"),
                        Status = (int)GetSafeNumber(obj, "status"),
                        Description = GetSafeString(obj, "description"),
                        Genre = ParseStringListFromJs(obj.Get("genre").IsUndefined() ? obj.Get("genres") : obj.Get("genre")),
                        Source = Id
                    };
                }
                return new Manga();
            });
        }

        private List<Chapter> ParseChapterListFromJs(JsValue jsResult)
        {
            var list = new List<Chapter>();
            if (jsResult.IsArray())
            {
                var arr = jsResult.AsArray();
                for (int i = 0; i < arr.Length; i++)
                {
                    var obj = arr.Get(i).AsObject();
                    var name = GetSafeString(obj, "name", GetSafeString(obj, "title"));
                    
                    var jsNumVal = obj.Get("chapterNumber");
                    if (jsNumVal.IsUndefined()) jsNumVal = obj.Get("chapter");
                    
                    float chapterNumber = -1;
                    if (jsNumVal.IsNumber())
                    {
                        chapterNumber = (float)jsNumVal.AsNumber();
                    }
                    else if (jsNumVal.IsString() && float.TryParse(jsNumVal.AsString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float parsedJsNum))
                    {
                        chapterNumber = parsedJsNum;
                    }
                    else
                    {
                        chapterNumber = ParseChapterNumber(name);
                    }

                    list.Add(new Chapter
                    {
                        Name = name,
                        Url = GetSafeString(obj, "url"),
                        DateUpload = ParseChapterDateUpload(obj),
                        ChapterNumber = chapterNumber
                    });
                }
            }
            return list;
        }

        private static string DecodeBase64Latin1(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            string cleaned = input.Trim().Replace('-', '+').Replace('_', '/');
            int mod = cleaned.Length % 4;
            if (mod == 2) cleaned += "==";
            else if (mod == 3) cleaned += "=";
            byte[] bytes = Convert.FromBase64String(cleaned);
            return System.Text.Encoding.Latin1.GetString(bytes);
        }

        private static string EncodeBase64Latin1(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            byte[] bytes = System.Text.Encoding.Latin1.GetBytes(input);
            return Convert.ToBase64String(bytes);
        }

        private static long ParseChapterDateUpload(ObjectInstance obj)
        {
            long numeric = (long)GetSafeNumber(obj, "dateUpload");
            if (numeric > 0)
            {
                // Normalize Unix seconds (< 10^11) to milliseconds
                return numeric < 100_000_000_000L ? numeric * 1000L : numeric;
            }

            foreach (var fallbackKey in new[] { "uploadDate", "dateUploaded" })
            {
                var val = obj.Get(fallbackKey);
                if (val.IsNumber())
                {
                    long num = (long)val.AsNumber();
                    if (num > 0) return num < 100_000_000_000L ? num * 1000L : num;
                }
                else if (val.IsString())
                {
                    var raw = val.AsString().Trim();
                    if (string.IsNullOrEmpty(raw)) continue;
                    if (long.TryParse(raw, out long rawNum) && rawNum > 0)
                    {
                        return rawNum < 100_000_000_000L ? rawNum * 1000L : rawNum;
                    }
                    if (DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var dto))
                    {
                        return dto.ToUnixTimeMilliseconds();
                    }
                }
            }

            return 0;
        }

        public override async Task<List<Chapter>> GetChapterListAsync(string mangaUrl)
        {
            return await ExecuteJsAsync(engine =>
            {
                var hasMethod = engine.Invoke("__hasMethod", "getChapterList").AsBoolean();
                if (!hasMethod) return new List<Chapter>();

                var jsResult = engine.Invoke("__callMethod", "getChapterList", mangaUrl);
                return ParseChapterListFromJs(jsResult);
            });
        }

        public override async Task<List<Chapter>> GetLatestChaptersAsync(string mangaUrl)
        {
            return await ExecuteJsAsync(engine =>
            {
                var ctx = _asyncCacheContext.Value;
                var hasMethod = engine.Invoke("__hasMethod", "getLatestChapters").AsBoolean();
                if (hasMethod)
                {
                    var jsResult = engine.Invoke("__callMethod", "getLatestChapters", mangaUrl);
                    var latest = ParseChapterListFromJs(jsResult);
                    if (latest.Count > 0)
                    {
                        if (ctx != null) ctx.WasPartial = true;
                        return latest;
                    }
                }

                if (ctx != null) ctx.WasPartial = false;
                var hasStandard = engine.Invoke("__hasMethod", "getChapterList").AsBoolean();
                if (!hasStandard) return new List<Chapter>();

                var fallbackResult = engine.Invoke("__callMethod", "getChapterList", mangaUrl);
                return ParseChapterListFromJs(fallbackResult);
            });
        }

        public override async Task<ChapterListCacheResult> GetChapterListWithCacheAsync(string mangaUrl, string? eTag, string? lastModified)
        {
            var ctx = new ChapterCacheContext
            {
                RequestETag = eTag,
                RequestLastModified = lastModified
            };
            var prev = _asyncCacheContext.Value;
            _asyncCacheContext.Value = ctx;

            try
            {
                var chapters = await GetLatestChaptersAsync(mangaUrl);

                if (ctx.Was304)
                {
                    return new ChapterListCacheResult { IsNotModified = true };
                }

                return new ChapterListCacheResult
                {
                    IsNotModified = false,
                    IsPartial = ctx.WasPartial,
                    Chapters = chapters,
                    ETag = ctx.ExtractedETag,
                    LastModified = ctx.ExtractedLastModified
                };
            }
            finally
            {
                _asyncCacheContext.Value = prev;
            }
        }

        public override async Task<List<string>> GetPageListAsync(string chapterUrl)
        {
            var (pages, _) = await GetPageListWithStatusAsync(chapterUrl);
            return pages;
        }

        public async Task<(List<string> Pages, bool RequiresVerification)> GetPageListWithStatusAsync(string chapterUrl)
        {
            return await ExecuteJsAsync(engine =>
            {
                var hasMethod = engine.Invoke("__hasMethod", "getPageList").AsBoolean();
                if (!hasMethod) return (new List<string>(), false);

                var jsResult = engine.Invoke("__callMethod", "getPageList", chapterUrl);
                var pages = ParseStringListFromJs(jsResult, true);

                bool requiresVerification = false;
                var srcVal = engine.GetValue("source");
                if (srcVal != null && srcVal.IsObject())
                {
                    var flag = srcVal.AsObject().Get("lastRequiresVerification");
                    if (flag.IsBoolean()) requiresVerification = flag.AsBoolean();
                }

                return (pages, requiresVerification);
            });
        }

        private List<Manga> ParseMangaListFromJs(JsValue jsResult)
        {
            var list = new List<Manga>();
            var target = jsResult;
            if (jsResult.IsObject() && !jsResult.IsArray())
            {
                var obj = jsResult.AsObject();
                var itemsVal = obj.Get("items");
                if (itemsVal.IsArray())
                {
                    target = itemsVal;
                }
            }

            if (target.IsArray())
            {
                var arr = target.AsArray();
                for (int i = 0; i < arr.Length; i++)
                {
                    var item = arr.Get(i);
                    if (item.IsObject())
                    {
                        var obj = item.AsObject();
                        var thumbUrl = GetSafeString(obj, "thumbnailUrl");
                        if (!string.IsNullOrEmpty(thumbUrl) && !string.IsNullOrEmpty(_userAgent) && !thumbUrl.Contains("|UserAgent="))
                        {
                            thumbUrl += $"|UserAgent={_userAgent}";
                        }

                        list.Add(new Manga
                        {
                            Title = GetSafeString(obj, "title"),
                            Url = GetSafeString(obj, "url"),
                            ThumbnailUrl = thumbUrl,
                            Status = obj.Get("status").IsNumber() ? (int)obj.Get("status").AsNumber() : Manga.UNKNOWN,
                            Source = Id
                        });
                    }
                }
            }
            return list;
        }

        private List<string> ParseStringListFromJs(JsValue jsResult, bool isUrlList = false)
        {
            var list = new List<string>();
            if (jsResult.IsArray())
            {
                var arr = jsResult.AsArray();
                for (int i = 0; i < arr.Length; i++)
                {
                    var pageUrl = GetSafeString(arr.Get(i));
                    if (isUrlList && !string.IsNullOrEmpty(pageUrl) && !string.IsNullOrEmpty(_userAgent) && !pageUrl.Contains("|UserAgent="))
                    {
                        pageUrl += $"|UserAgent={_userAgent}";
                    }
                    list.Add(pageUrl);
                }
            }
            return list;
        }

        private static string GetSafeString(JsValue value, string defaultValue = "")
        {
            return value.IsString() ? value.AsString() : defaultValue;
        }

        private static string GetSafeString(ObjectInstance obj, string propertyName, string defaultValue = "")
        {
            if (obj == null) return defaultValue;
            var val = obj.Get(propertyName);
            return val.IsString() ? val.AsString() : defaultValue;
        }

        private static double GetSafeNumber(ObjectInstance obj, string propertyName, double defaultValue = 0)
        {
            if (obj == null) return defaultValue;
            var val = obj.Get(propertyName);
            return val.IsNumber() ? val.AsNumber() : defaultValue;
        }

        private static readonly System.Text.RegularExpressions.Regex _chapterNumberRegex = 
            new(@"\b(?:ch|chapter|chap|chp)\.?\s*([0-9]+(?:\.[0-9]+)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        public static float ParseChapterNumber(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            
            var match = _chapterNumberRegex.Match(name);
            if (match.Success && float.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float num))
            {
                return num;
            }
            
            // Fallback: try to find any decimal number in the string
            var numbers = System.Text.RegularExpressions.Regex.Matches(name, @"([0-9]+(?:\.[0-9]+)?)");
            if (numbers.Count > 0 && float.TryParse(numbers[0].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float fallbackNum))
            {
                return fallbackNum;
            }
            
            return -1;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                while (_enginePool.TryDequeue(out _)) { }
            }
            base.Dispose(disposing);
        }
    }
}
