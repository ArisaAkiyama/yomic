using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;
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

        private readonly SemaphoreSlim _executionLimit = new(4, 4);
        private string _scriptCode = "";
        private bool _supportsStatusFilter;
        private long _id;

        public override string Name => _name;
        public override string BaseUrl => _baseUrl;
        public override string Language => _language;
        public override string Version => _version;
        public override string Description => _description;
        public override string Author => _author;
        public override string IconUrl => _iconUrl;
        public override string IconBackground => _iconBackground;
        public override string IconForeground => _iconForeground;
        public bool IsNsfw => _isNsfw;
        public override bool IsHasMorePages => _isHasMorePages;
        public bool SupportsStatusFilter => _supportsStatusFilter;
        public override bool RequiresProxy => _requiresProxy;

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
                if (obj.HasProperty("isNsfw")) _isNsfw = obj.Get("isNsfw").AsBoolean();
                if (obj.HasProperty("isHasMorePages")) _isHasMorePages = obj.Get("isHasMorePages").AsBoolean();
                if (obj.HasProperty("requiresProxy")) _requiresProxy = obj.Get("requiresProxy").AsBoolean();

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
            var engine = new Engine();

            engine.SetValue("fetch", new Func<string, JsValue, JsResponse>(FetchUrl));
            engine.SetValue("Html", new
            {
                parse = new Func<string, string, JsDocument>(HtmlParser.parse)
            });
            engine.SetValue("log", new Action<object>(o => Console.WriteLine($"[JS Extension Log] {o}")));

            engine.Execute(_scriptCode);
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

        private async Task<T> ExecuteJsAsync<T>(Func<Engine, T> action)
        {
            await _executionLimit.WaitAsync();
            try
            {
                return await Task.Run(() =>
                {
                    var engine = CreateEngine();
                    return action(engine);
                });
            }
            finally
            {
                _executionLimit.Release();
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
                var method = HttpMethod.Get;
                var headers = new Dictionary<string, string>();
                string? postBody = null;

                if (options != null && options.IsObject())
                {
                    var opts = options.AsObject();
                    var mVal = opts.Get("method");
                    if (mVal.IsString())
                    {
                        var mStr = mVal.AsString().ToUpper();
                        if (mStr == "POST") method = HttpMethod.Post;
                    }

                    var hVal = opts.Get("headers");
                    if (hVal.IsObject())
                    {
                        var hObj = hVal.AsObject();
                        foreach (var key in hObj.GetOwnPropertyKeys())
                        {
                            var keyStr = key.AsString();
                            var valStr = hObj.Get(keyStr).AsString();
                            headers[keyStr] = valStr;
                        }
                    }

                    var bVal = opts.Get("body");
                    if (bVal.IsString())
                    {
                        postBody = bVal.AsString();
                    }
                }

                // If it's a GET and no custom headers, call our built-in GetStringAsync which has cloudflare bypass built-in!
                if (method == HttpMethod.Get && headers.Count == 0)
                {
                    var content = GetStringAsync(url).GetAwaiter().GetResult();
                    return new JsResponse { body = content, status = 200 };
                }

                // Otherwise make a custom request using our client
                var request = new HttpRequestMessage(method, url);
                foreach (var h in headers)
                {
                    request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
                if (postBody != null)
                {
                    request.Content = new StringContent(postBody, System.Text.Encoding.UTF8, "application/json");
                }

                var response = Client.SendAsync(request).GetAwaiter().GetResult();
                var bodyText = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return new JsResponse { body = bodyText, status = (int)response.StatusCode };
            }
            catch (Exception ex)
            {
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
                var hasMethod = engine.Invoke("__hasMethod", "getSearchManga").AsBoolean();
                if (!hasMethod) return new List<Manga>();

                var jsResult = engine.Invoke("__callMethod", "getSearchManga", query, page);
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
            return await ExecuteJsAsync(engine =>
            {
                var hasFilteredMethod = engine.Invoke("__hasMethod", "getMangaList").AsBoolean();
                if (hasFilteredMethod)
                {
                    var jsResult = engine.Invoke("__callMethod", "getMangaList", page, status);
                    return ParsePagedMangaListFromJs(jsResult, page);
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
                    return new Manga
                    {
                        Title = obj.Get("title").AsString(),
                        Url = obj.Get("url").AsString(),
                        ThumbnailUrl = obj.Get("thumbnailUrl").AsString(),
                        Author = obj.Get("author").AsString(),
                        Status = (int)obj.Get("status").AsNumber(),
                        Description = obj.Get("description").AsString(),
                        Genre = ParseStringListFromJs(obj.Get("genre")),
                        Source = Id
                    };
                }
                return new Manga();
            });
        }

        public override async Task<List<Chapter>> GetChapterListAsync(string mangaUrl)
        {
            return await ExecuteJsAsync(engine =>
            {
                var hasMethod = engine.Invoke("__hasMethod", "getChapterList").AsBoolean();
                if (!hasMethod) return new List<Chapter>();

                var jsResult = engine.Invoke("__callMethod", "getChapterList", mangaUrl);
                var list = new List<Chapter>();
                if (jsResult.IsArray())
                {
                    var arr = jsResult.AsArray();
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var obj = arr.Get(i).AsObject();
                        list.Add(new Chapter
                        {
                            Name = obj.Get("name").AsString(),
                            Url = obj.Get("url").AsString(),
                            DateUpload = (long)obj.Get("dateUpload").AsNumber()
                        });
                    }
                }
                return list;
            });
        }

        public override async Task<List<string>> GetPageListAsync(string chapterUrl)
        {
            return await ExecuteJsAsync(engine =>
            {
                var hasMethod = engine.Invoke("__hasMethod", "getPageList").AsBoolean();
                if (!hasMethod) return new List<string>();

                var jsResult = engine.Invoke("__callMethod", "getPageList", chapterUrl);
                return ParseStringListFromJs(jsResult);
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
                        list.Add(new Manga
                        {
                            Title = obj.Get("title").AsString(),
                            Url = obj.Get("url").AsString(),
                            ThumbnailUrl = obj.Get("thumbnailUrl").AsString(),
                            Status = obj.Get("status").IsNumber() ? (int)obj.Get("status").AsNumber() : Manga.UNKNOWN,
                            Source = Id
                        });
                    }
                }
            }
            return list;
        }

        private List<string> ParseStringListFromJs(JsValue jsResult)
        {
            var list = new List<string>();
            if (jsResult.IsArray())
            {
                var arr = jsResult.AsArray();
                for (int i = 0; i < arr.Length; i++)
                {
                    list.Add(arr.Get(i).AsString());
                }
            }
            return list;
        }
    }
}
