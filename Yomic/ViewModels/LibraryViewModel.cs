using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using System.Net.Http;
using System.IO;
using System.Linq;
using System;
using System.Reactive.Linq;

namespace Yomic.ViewModels
{
    public enum LibrarySortMode
    {
        TitleAsc,     // A-Z
        TitleDesc,    // Z-A
        DateModified  // Most recent first
    }

    public class LibraryViewModel : ViewModelBase
    {
        private static readonly HttpClient _httpClient = new();
        // private static readonly Dictionary<string, Bitmap> _coverCache = new(); // REMOVED (Memory Leak)
        private static readonly string _cacheFolder;
        
        private List<MangaItem> _allItems = new(); // Store all items offline
        public ObservableCollection<MangaItem> LibraryItems { get; set; } = new();

        private readonly MainWindowViewModel _mainVM;
        private readonly Core.Services.LibraryService _libraryService;
        private readonly Core.Services.ImageCacheService _imageCacheService;
        private readonly Core.Services.SettingsService _settingsService;
        


        static LibraryViewModel()
        {
             // Configure HttpClient
             _httpClient.DefaultRequestHeaders.Add("Referer", "https://komiku.org/");
             _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            // Setup cache folder
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _cacheFolder = Path.Combine(appData, "Yomic", "covers");
            if (!Directory.Exists(_cacheFolder))
            {
                Directory.CreateDirectory(_cacheFolder);
            }
        }

        private string _searchText = string.Empty;
        public string SearchText
        {
            get => _searchText;
            set => this.RaiseAndSetIfChanged(ref _searchText, value);
        }

        private bool _isLibraryEmpty;
        public bool IsLibraryEmpty
        {
             get => _isLibraryEmpty;
             set => this.RaiseAndSetIfChanged(ref _isLibraryEmpty, value);
        }

        private bool _hasNoResults;
        public bool HasNoResults
        {
             get => _hasNoResults;
             set => this.RaiseAndSetIfChanged(ref _hasNoResults, value);
        }

        private bool _isEmpty;
        public bool IsEmpty
        {
             get => _isEmpty;
             set => this.RaiseAndSetIfChanged(ref _isEmpty, value);
        }

        private bool _isRefreshing;
        public bool IsRefreshing
        {
            get => _isRefreshing;
            set => this.RaiseAndSetIfChanged(ref _isRefreshing, value);
        }

        // Commands
        public ReactiveCommand<MangaItem, Unit> OpenMangaCommand { get; }
        public ReactiveCommand<Unit, Unit> GoToBrowseCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenFilterCommand { get; }
        public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
        public ReactiveCommand<MangaItem, Unit> MarkAsReadCommand { get; }
        public ReactiveCommand<MangaItem, Unit> RemoveMangaCommand { get; }
        public ReactiveCommand<MangaItem, Unit> DeleteMangaCommand { get; }

        private bool _isFilterVisible;
        public bool IsFilterVisible
        {
            get => _isFilterVisible;
            set => this.RaiseAndSetIfChanged(ref _isFilterVisible, value);
        }

        private LibrarySortMode _selectedSortMode = LibrarySortMode.TitleAsc;
        public LibrarySortMode SelectedSortMode
        {
            get => _selectedSortMode;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedSortMode, value);
                FilterLibrary();
            }
        }

        public ReactiveCommand<LibrarySortMode, Unit> SetSortModeCommand { get; }
        
        // Helper for UI
        public bool HasItems => LibraryItems.Count > 0;
        
        private readonly Core.Services.NetworkService _networkService;
        
        private bool _isOnline = true;
        public bool IsOnline
        {
            get => _isOnline;
            set => this.RaiseAndSetIfChanged(ref _isOnline, value);
        }

        public LibraryViewModel(MainWindowViewModel mainViewModel, 
                                Core.Services.LibraryService libraryService, 
                                Core.Services.NetworkService networkService,
                                Core.Services.ImageCacheService imageCacheService,
                                Core.Services.SettingsService settingsService)
        {
            _mainVM = mainViewModel;
            _libraryService = libraryService;
            _networkService = networkService;
            _imageCacheService = imageCacheService;
            _settingsService = settingsService;

            // Load persisted sort preference
            _selectedSortMode = (LibrarySortMode)_settingsService.LibrarySortMode;

            // Throttled search (300ms debounce)
            this.WhenAnyValue(x => x.SearchText)
                .Throttle(TimeSpan.FromMilliseconds(300))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => FilterLibrary());

            // Bind to NetworkService
            IsOnline = _networkService.IsOnline;
            _networkService.StatusChanged += (s, online) => 
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                {
                    var wasOffline = !IsOnline;
                    IsOnline = online;
                    
                    // Auto-reload when coming back online
                    if (wasOffline && online)
                    {
                        System.Diagnostics.Debug.WriteLine("[LibraryVM] Internet reconnected - auto-reloading...");
                        _ = RefreshLibrary();
                    }
                });
            };

            OpenMangaCommand = ReactiveCommand.Create<MangaItem>(item => 
            {
                mainViewModel.GoToDetail(item);
            });

            GoToBrowseCommand = ReactiveCommand.Create(() => 
            {
                mainViewModel.GoToBrowse();
            });

            OpenFilterCommand = ReactiveCommand.Create(() => 
            {
                IsFilterVisible = !IsFilterVisible;
            });

            MarkAsReadCommand = ReactiveCommand.CreateFromTask<MangaItem>(async item => 
            {
                var manga = new Core.Models.Manga { Url = item.MangaUrl, Source = item.SourceId };
                await _libraryService.MarkChaptersAsReadAsync(manga);
                
                // UI Update
                // Assuming "0" or null hides the badge? 
                // Let's set it to null or empty if 0 is hidden by converter, 
                // but usually "0" is hidden if using IsNotNull converter? 
                // The XAML uses IsNotNull converter. So null is safer for hiding.
                item.UnreadCount = null; // Or "0" if textblock handles it.
                // Re-raise property change just in case UnreadCount setter doesn't if logic depends on something else
                // But MangaItem.UnreadCount setter (ViewModelBase) usually does it.
            });

            RemoveMangaCommand = ReactiveCommand.CreateFromTask<MangaItem>(async item => 
            {
                var manga = new Core.Models.Manga { Url = item.MangaUrl, Source = item.SourceId };
                await _libraryService.RemoveFromLibraryAsync(manga, deleteFiles: false);
                
                // UI Update - Delayed to let ContextMenu close
                await Task.Delay(100);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                {
                    _allItems.Remove(item);
                    LibraryItems.Remove(item);
                    IsEmpty = LibraryItems.Count == 0;
                    this.RaisePropertyChanged(nameof(HasItems));
                });
            });
            
            DeleteMangaCommand = ReactiveCommand.CreateFromTask<MangaItem>(async item => 
            {
                var manga = new Core.Models.Manga { Url = item.MangaUrl, Source = item.SourceId };
                await _libraryService.RemoveFromLibraryAsync(manga, deleteFiles: true);
                
                // UI Update - Delayed to let ContextMenu close
                await Task.Delay(100);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                {
                    _allItems.Remove(item);
                    LibraryItems.Remove(item);
                    IsEmpty = LibraryItems.Count == 0;
                    this.RaisePropertyChanged(nameof(HasItems));
                });
            });

            // Manual Refresh Button - Force reload covers from network
            RefreshCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                if (IsOnline)
                {
                    await ForceRefreshLibrary();
                }
            });

            // Sort Mode Command
            SetSortModeCommand = ReactiveCommand.Create<LibrarySortMode>(mode =>
            {
                SelectedSortMode = mode;
                // Persist to settings
                _settingsService.LibrarySortMode = (int)mode;
                _settingsService.Save();
            });
            
            // Initial load
            _ = RefreshLibrary();
        }

        /// <summary>
        /// Soft refresh - only reload from database, use cached images
        /// </summary>
        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => this.RaiseAndSetIfChanged(ref _isLoading, value);
        }

        /// <summary>
        /// Soft refresh - only reload from database, use cached images
        /// </summary>
        public async Task RefreshLibrary()
        {
            IsLoading = true;
            try
            {
                System.Diagnostics.Debug.WriteLine("[LibraryVM] Soft refresh (cached images)...");
                var mangas = await _libraryService.GetLibraryMangaAsync();
                System.Diagnostics.Debug.WriteLine($"[LibraryVM] Found {mangas.Count} items in DB.");
                
                await MergeLibraryItems(mangas, forceDownload: false);

                System.Diagnostics.Debug.WriteLine($"[LibraryVM] UI Updated. Items: {LibraryItems.Count}");
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading library: {ex}");
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Hard refresh - reload from database AND download fresh covers
        /// </summary>
        public async Task ForceRefreshLibrary()
        {
            IsRefreshing = true;
            try
            {
                if (IsOnline)
                {
                    System.Diagnostics.Debug.WriteLine("[LibraryVM] Checking for latest updates from sources...");
                    await _libraryService.UpdateAllLibraryMangaAsync(_mainVM.SourceManager);
                }

                System.Diagnostics.Debug.WriteLine("[LibraryVM] Hard refresh (redownload covers)...");
                var mangas = await _libraryService.GetLibraryMangaAsync();
                
                await MergeLibraryItems(mangas, forceDownload: true);
            }
            finally
            {
                IsRefreshing = false;
            }
        }
        
        private async Task MergeLibraryItems(List<Core.Models.Manga> mangas, bool forceDownload)
        {
             // Create Lookup for New State
             var newMangaDict = mangas.ToDictionary(m => m.Url + "|" + m.Source);
             
             // 1. Remove deleted items from _allItems
             var toRemove = new List<MangaItem>();
             foreach (var existing in _allItems)
             {
                 var key = existing.MangaUrl + "|" + existing.SourceId;
                 if (!newMangaDict.ContainsKey(key))
                 {
                     toRemove.Add(existing);
                 }
             }
             foreach (var item in toRemove) _allItems.Remove(item);
             
             // 2. Add or Update items
             foreach (var m in mangas)
             {
                 var key = m.Url + "|" + m.Source;
                 var existingItem = _allItems.FirstOrDefault(x => x.MangaUrl == m.Url && x.SourceId == m.Source);
                 
                 // Calculate Unread Count
                 int unread = m.Chapters?.Count(c => !c.Read) ?? 0;
                 string? unreadString = unread > 0 ? unread.ToString() : null;

                 if (existingItem != null)
                 {
                     // Update existing
                     if (existingItem.Title != m.Title) existingItem.Title = m.Title;
                     if (existingItem.UnreadCount != unreadString) existingItem.UnreadCount = unreadString;
                     
                     // Check cover
                     if (existingItem.CoverUrl != m.ThumbnailUrl || forceDownload)
                     {
                         existingItem.CoverUrl = m.ThumbnailUrl;
                         if (forceDownload) _ = DownloadAndCacheCoverAsync(existingItem);
                         else _ = LoadCoverFromCacheAsync(existingItem);
                     }
                 }
                 else
                 {
                     // Add New
                     var newItem = new MangaItem
                     {
                          Title = m.Title,
                          CoverUrl = m.ThumbnailUrl,
                          SourceId = m.Source,
                          MangaUrl = m.Url,
                          UnreadCount = unreadString
                     };
                     _allItems.Add(newItem);
                     
                     if (forceDownload) _ = DownloadAndCacheCoverAsync(newItem);
                     else _ = LoadCoverFromCacheAsync(newItem);
                 }
             }
             
             FilterLibrary();
        }

        private void FilterLibrary()
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => 
            {
                 var query = string.IsNullOrWhiteSpace(SearchText) 
                     ? _allItems 
                     : _allItems.Where(x => x.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

                 // Apply sort based on SelectedSortMode
                 IEnumerable<MangaItem> sorted = SelectedSortMode switch
                 {
                     LibrarySortMode.TitleAsc => query.OrderBy(x => x.Title),
                     LibrarySortMode.TitleDesc => query.OrderByDescending(x => x.Title),
                     LibrarySortMode.DateModified => query.OrderByDescending(x => x.LastUpdate),
                     _ => query.OrderBy(x => x.Title)
                 };
                      
                 var source = sorted.ToList();

                 // Smart Sync for ObservableCollection (Prevents full UI rebuild/flicker)
                 
                 // 1. Remove items not in source
                 var toRemove = LibraryItems.Where(i => !source.Contains(i)).ToList();
                 foreach(var item in toRemove) LibraryItems.Remove(item);
                 
                 // 2. Add / Move items
                 // Since we want to respect Sort Order, we might need to Move items or Insert at correct index.
                 // For simplicity in this Smart Sync, we'll just Ensure items exist, 
                 // BUT `LibraryItems` ObservableCollection won't automatically resort if we just Add.
                 // We need to sync the ORDER too.
                 
                 LibraryItems.Clear();
                 foreach(var item in source) LibraryItems.Add(item);
                 
                 // NOTE: The optimization above (Smart Sync) was:
                 // 1. Remove missing
                 // 2. Add new
                 // But valid Sorting requires re-ordering. 
                 // For a Library size < 1000, Clearing and Adding sorted list is actually fast enough provided we reused the OBJECTS (which we did in MergeLibraryItems).
                 // The "Flicker" comes from creating NEW instances + downloading images.
                 // Since _allItems keeps the instances with cached Bitmaps, Clear+Add here is visually instant.
                 
                 IsLibraryEmpty = _allItems.Count == 0;
                 IsEmpty = LibraryItems.Count == 0;
                 HasNoResults = !IsLibraryEmpty && IsEmpty;

                 this.RaisePropertyChanged(nameof(HasItems));
            });
        }

        /// <summary>
        /// Load cover from memory/disk cache first.
        /// OFFLINE MODE: Does NOT automatically download if missing. 
        /// Use ForceRefreshLibrary (Refresh button) to download missing covers.
        /// </summary>
        private async Task LoadCoverFromCacheAsync(MangaItem item)
        {
            if (string.IsNullOrEmpty(item.CoverUrl)) return;
            
            try
            {
                // 1. Check shared memory cache (WeakRef)
                var cachedBitmap = _imageCacheService.GetImage(item.CoverUrl);
                if (cachedBitmap != null)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => item.CoverBitmap = cachedBitmap);
                    return;
                }
                
                // 2. Check disk cache
                var cacheKey = GetCacheKey(item.CoverUrl);
                var cachePath = Path.Combine(_cacheFolder, cacheKey);
                if (File.Exists(cachePath))
                {
                    byte[] data = await File.ReadAllBytesAsync(cachePath);
                    using var stream = new MemoryStream(data);
                    var bitmap = new Bitmap(stream);
                    
                    // Add to shared cache
                    _imageCacheService.AddImage(item.CoverUrl, bitmap);
                    
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => item.CoverBitmap = bitmap);
                    return;
                }
                
                // 3. Fallback: Auto-Download if Online and missing
                // This heals "Blank Covers" automatically
                if (IsOnline)
                {
                    _ = DownloadAndCacheCoverAsync(item);
                }
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"[LibraryVM] LoadCover Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Download cover from network and save to cache
        /// </summary>
        private async Task DownloadAndCacheCoverAsync(MangaItem item)
        {
            if (string.IsNullOrEmpty(item.CoverUrl)) return;
            if (!IsOnline) return; // Prevent network access if offline
            
            try
            {
                // Parse Headers
                string requestUrl = item.CoverUrl;
                var customHeaders = new System.Collections.Generic.Dictionary<string, string>();
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
                
                // Apply Headers
                if (customHeaders.ContainsKey("Referer")) req.Headers.Referrer = new Uri(customHeaders["Referer"]);
                else req.Headers.Referrer = new Uri("https://komiku.org/");

                if (customHeaders.ContainsKey("User-Agent")) req.Headers.UserAgent.TryParseAdd(customHeaders["User-Agent"]);
                
                // Use Optimized Client
                using var client = _networkService.CreateOptimizedHttpClient();
                using var response = await client.SendAsync(req);
                var data = await response.Content.ReadAsByteArrayAsync();
                
                var cacheKey = GetCacheKey(item.CoverUrl);
                var cachePath = Path.Combine(_cacheFolder, cacheKey);
                
                // Save to disk
                await File.WriteAllBytesAsync(cachePath, data);
                
                // Load to memory
                // IMPORTANT: Use UI Thread for Bitmap creation if needed, OR create here and pass? 
                // Avalonia Bitmaps can be created on bg thread usually.
                using var stream = new MemoryStream(data);
                var bitmap = new Bitmap(stream);
                
                _imageCacheService.AddImage(item.CoverUrl, bitmap);
                
                Avalonia.Threading.Dispatcher.UIThread.Post(() => item.CoverBitmap = bitmap);
            }
            catch { }
        }

        private string GetCacheKey(string url)
        {
            // Clean URL first
            string cleanUrl = url.Contains("|") ? url.Split('|')[0] : url;

            // Use MD5 for stable hash across sessions
            // Use ORIGINAL url for hash to differentiate (e.g. if we have different headers?)
            // No, use cleanUrl for extension logic, but original for hash? 
            // Let's use cleanUrl for everything to be safe.
            
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var inputBytes = System.Text.Encoding.ASCII.GetBytes(cleanUrl);
                var hashBytes = md5.ComputeHash(inputBytes);
                // Convert to hex string
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < hashBytes.Length; i++)
                {
                    sb.Append(hashBytes[i].ToString("X2"));
                }
                
                string ext = ".jpg";
                try 
                {
                    ext = Path.GetExtension(new Uri(cleanUrl).AbsolutePath);
                    if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                }
                catch {}
                
                return $"{sb.ToString()}{ext}";
            }
        }
    }
}
