using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using MyMangaApp.Core.Services;
using ReactiveUI;
using Avalonia.Threading;
using MyMangaApp.Core.Models;
using System.Collections.Generic;
using System.Net.Http;
using System.IO;
using Avalonia.Media.Imaging;
using Microsoft.EntityFrameworkCore;

namespace MyMangaApp.ViewModels
{
    public class HistoryViewModel : ViewModelBase
    {
        private static readonly HttpClient _httpClient = new();
        private static readonly Dictionary<string, Bitmap> _coverCache = new();
        private static readonly string _cacheFolder;

        static HistoryViewModel()
        {
            _httpClient.DefaultRequestHeaders.Add("Referer", "https://komiku.org/");
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _cacheFolder = Path.Combine(appData, "MyMangaApp", "covers");
            if (!Directory.Exists(_cacheFolder))
            {
                Directory.CreateDirectory(_cacheFolder);
            }
        }

        private readonly LibraryService _libraryService;
        private readonly NetworkService _networkService;
        private readonly SourceManager _sourceManager;

        private ObservableCollection<MangaItem> _historyItems = new();
        public ObservableCollection<MangaItem> HistoryItems
        {
            get => _historyItems;
            set => this.RaiseAndSetIfChanged(ref _historyItems, value);
        }

        private bool _hasItems;
        public bool HasItems
        {
            get => _hasItems;
            set => this.RaiseAndSetIfChanged(ref _hasItems, value);
        }

        private bool _isOffline;
        public bool IsOffline
        {
            get => _isOffline;
            set => this.RaiseAndSetIfChanged(ref _isOffline, value);
        }

        public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearHistoryCommand { get; }
        public ReactiveCommand<MangaItem, Unit> OpenMangaCommand { get; }

        private readonly MainWindowViewModel _mainVM;

        public HistoryViewModel(LibraryService libraryService, NetworkService networkService, SourceManager sourceManager, MainWindowViewModel mainVM)
        {
            _libraryService = libraryService;
            _networkService = networkService;
            _sourceManager = sourceManager;
            _mainVM = mainVM;

            HistoryItems = new ObservableCollection<MangaItem>();

            // Offline Logic
            IsOffline = !_networkService.IsOnline;
            _networkService.StatusChanged += (s, isOnline) =>
            {
                Dispatcher.UIThread.Post(() => IsOffline = !isOnline);
            };

            RefreshCommand = ReactiveCommand.CreateFromTask(LoadHistory);
            ClearHistoryCommand = ReactiveCommand.CreateFromTask(ClearHistoryAsync);
            
            OpenMangaCommand = ReactiveCommand.CreateFromTask<MangaItem>(async item => 
            {
                // 1. Update DB timestamp
                var manga = new MyMangaApp.Core.Models.Manga 
                {
                    Url = item.MangaUrl,
                    Source = item.SourceId,
                    Title = item.Title,
                    ThumbnailUrl = item.CoverUrl
                };
                await _libraryService.UpdateHistoryAsync(manga);

                // 2. Navigate
                Dispatcher.UIThread.Post(() => _mainVM.GoToDetail(item));
                
                // 3. Local UI Update (Move to top)
                Dispatcher.UIThread.Post(() => 
                {
                    var existing = HistoryItems.FirstOrDefault(x => x.MangaUrl == item.MangaUrl);
                    if (existing != null)
                    {
                        HistoryItems.Remove(existing);
                        existing.LastReadTime = "Just now";
                        HistoryItems.Insert(0, existing);
                        HasItems = true;
                    }
                });
            });
            
            // Initial load
            _ = LoadHistory();
        }

        public async Task ClearHistoryAsync()
        {
            try
            {
                using var context = new MyMangaApp.Core.Data.MangaDbContext();
                // Execute raw SQL for performance or batch update
                // We just want to set LastViewed = 0 for all items
                await context.Database.ExecuteSqlRawAsync("UPDATE Mangas SET LastViewed = 0 WHERE LastViewed > 0");
                
                Dispatcher.UIThread.Post(() =>
                {
                    HistoryItems.Clear();
                    HasItems = false;
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HistoryVM] Error clearing history: {ex}");
            }
        }

        public async Task LoadHistory()
        {
            try
            {
                using var context = new MyMangaApp.Core.Data.MangaDbContext();
                var history = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                    System.Linq.Queryable.OrderByDescending(
                        System.Linq.Queryable.Where(context.Mangas, m => m.LastViewed > 0), 
                        m => m.LastViewed)
                );

                Dispatcher.UIThread.Post(() =>
                {
                    HistoryItems.Clear();
                    foreach (var m in history)
                    {
                        var item = new MangaItem
                        {
                            Title = m.Title,
                            CoverUrl = m.ThumbnailUrl,
                            SourceId = m.Source,
                            MangaUrl = m.Url,
                            LastReadTime = GetTimeAgo(m.LastViewed),
                            Status = m.Status // Include status from DB
                        };
                        HistoryItems.Add(item);
                        
                        // Load cover image
                        _ = LoadCoverAsync(item);
                    }
                    HasItems = HistoryItems.Count > 0;
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HistoryVM] Error loading history: {ex}");
            }
        }
        
        private string GetTimeAgo(long dateFetch)
        {
            if (dateFetch <= 0) return "Unknown";
            var date = DateTimeOffset.FromUnixTimeMilliseconds(dateFetch);
            var diff = DateTimeOffset.Now - date;
            
            if (diff.TotalMinutes < 1) return "Just now";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
            if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
            return date.ToString("MMM dd");
        }

        private async Task LoadCoverAsync(MangaItem item)
        {
            if (string.IsNullOrEmpty(item.CoverUrl)) return;

            try
            {
                var cacheKey = GetCacheKey(item.CoverUrl);

                // 1. Check memory cache
                if (_coverCache.TryGetValue(cacheKey, out var cachedBitmap))
                {
                    Dispatcher.UIThread.Post(() => item.CoverBitmap = cachedBitmap);
                    return;
                }

                // 2. Check disk cache
                var cachePath = Path.Combine(_cacheFolder, cacheKey);
                bool loadedFromDisk = false;
                
                if (File.Exists(cachePath))
                {
                    try 
                    {
                        using var stream = File.OpenRead(cachePath);
                        var bitmap = new Bitmap(stream);
                        _coverCache[cacheKey] = bitmap;
                        Dispatcher.UIThread.Post(() => item.CoverBitmap = bitmap);
                        loadedFromDisk = true;
                    }
                    catch 
                    {
                        try { File.Delete(cachePath); } catch { }
                    }
                }

                if (loadedFromDisk) return;

                // 3. Download from network
                
                // Parse Headers
                string requestUrl = item.CoverUrl;
                var customHeaders = new Dictionary<string, string>();
                if (item.CoverUrl.Contains("|"))
                {
                    var parts = item.CoverUrl.Split('|', 2);
                    requestUrl = parts[0];
                    if (parts.Length > 1)
                    {
                        var headers = parts[1].Split('&');
                        foreach(var h in headers)
                        {
                             var pair = h.Split('=', 2);
                             if (pair.Length == 2) customHeaders[pair[0].Trim()] = pair[1].Trim();
                        }
                    }
                }

                var req = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                
                // Set Referer based on Source ID
                if (customHeaders.ContainsKey("Referer")) 
                {
                    req.Headers.Referrer = new Uri(customHeaders["Referer"]);
                }
                else 
                {
                    // Source ID Logic
                     if (item.SourceId == 2) // KomikCast
                     {
                         req.Headers.Referrer = new Uri("https://komikcast.ch/");
                     }
                     else if (item.SourceId == 4) // Mangabats
                     {
                         req.Headers.Referrer = new Uri("https://www.mangabats.com/");
                     }
                     else if (item.SourceId == 5) // MangaDex
                     {
                         req.Headers.Referrer = new Uri("https://mangadex.org/");
                         System.Console.WriteLine($"[HistoryVM] Using Referer: https://mangadex.org/ for {item.Title}");
                     }
                     else 
                     {
                         req.Headers.Referrer = new Uri("https://komiku.org/"); // Fallback
                     }
                }

                if (customHeaders.ContainsKey("User-Agent")) req.Headers.UserAgent.TryParseAdd(customHeaders["User-Agent"]);
                
                // Use NetworkService to create optimized client (handles compression, etc)
                using var client = _networkService.CreateOptimizedHttpClient();
                using var response = await client.SendAsync(req);
                
                if (!response.IsSuccessStatusCode) 
                {
                    System.Diagnostics.Debug.WriteLine($"[HistoryVM] Download failed for {item.Title}: {response.StatusCode}");
                    return; 
                }

                var data = await response.Content.ReadAsByteArrayAsync();
                
                // Validate data
                if (data.Length < 100) // Too small to be valid image
                {
                     System.Diagnostics.Debug.WriteLine($"[HistoryVM] Downloaded data too small ({data.Length} bytes) for {item.Title}");
                     return;
                }
                
                await File.WriteAllBytesAsync(cachePath, data);

                using var memStream = new MemoryStream(data);
                var downloadedBitmap = new Bitmap(memStream);
                _coverCache[cacheKey] = downloadedBitmap;
                Dispatcher.UIThread.Post(() => item.CoverBitmap = downloadedBitmap);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HistoryVM] LoadCover Error for {item.Title} ({item.CoverUrl}): {ex.Message}");
                // If cache file is corrupt/invalid, delete it so we try again next time
                try { 
                    var cacheKey = GetCacheKey(item.CoverUrl);
                    var cachePath = Path.Combine(_cacheFolder, cacheKey);
                    if (File.Exists(cachePath)) File.Delete(cachePath); 
                } catch { }
            }
        }

        private string GetCacheKey(string url)
        {
            // Clean URL first
            string cleanUrl = url.Contains("|") ? url.Split('|')[0] : url;

            using var md5 = System.Security.Cryptography.MD5.Create();
            var inputBytes = System.Text.Encoding.ASCII.GetBytes(cleanUrl);
            var hashBytes = md5.ComputeHash(inputBytes);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < hashBytes.Length; i++)
            {
                sb.Append(hashBytes[i].ToString("X2"));
            }

            var ext = ".jpg";
            try
            {
                ext = Path.GetExtension(new Uri(cleanUrl).AbsolutePath);
                if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            }
            catch { }

            return $"{sb}{ext}";
        }
    }
}
