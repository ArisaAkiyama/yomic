using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Reactive;
using ReactiveUI;
using Yomic.Core.Sources;
using Yomic.Core.Models;
using System.Linq;
using System.Reactive.Linq;
using Yomic.Core.Services;

namespace Yomic.ViewModels
{
    public class SourceFeedViewModel : ViewModelBase
    {
        private readonly IMangaSource _source;
        private readonly MainWindowViewModel _mainVm;
        private readonly SourceManager _sourceManager;
        private readonly ImageCacheService _imageCacheService;
        
        private string _searchText = "";
        public string SearchText 
        { 
            get => _searchText; 
            set => this.RaiseAndSetIfChanged(ref _searchText, value); 
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => this.RaiseAndSetIfChanged(ref _isLoading, value);
        }

        private string? _errorMessage;
        public string? ErrorMessage
        {
            get => _errorMessage;
            set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
        }

        private bool _hasError;
        public bool HasError
        {
            get => _hasError;
            set => this.RaiseAndSetIfChanged(ref _hasError, value);
        }

        private bool _isMangaDexBlocked;
        public bool IsMangaDexBlocked
        {
            get => _isMangaDexBlocked;
            set 
            {
                this.RaiseAndSetIfChanged(ref _isMangaDexBlocked, value);
                this.RaisePropertyChanged(nameof(IsPaginationVisible));
            }
        }

        public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }

        // Pagination Properties
        private int _currentPage = 1;
        public int CurrentPage
        {
            get => _currentPage;
            set => this.RaiseAndSetIfChanged(ref _currentPage, value);
        }

        private int _totalPages = 1;
        public int TotalPages
        {
            get => _totalPages;
            set => this.RaiseAndSetIfChanged(ref _totalPages, value);
        }

        public string PageInfo => $"Halaman {CurrentPage} dari {TotalPages}";
        public bool HasPrevPage => CurrentPage > 1;
        public bool HasNextPage => CurrentPage < TotalPages;

        public ObservableCollection<MangaItem> MangaList { get; } = new();

        public ReactiveCommand<Unit, Unit> SearchCommand { get; }
        public ReactiveCommand<MangaItem, Unit> OpenMangaCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }
        public ReactiveCommand<Unit, Unit> PrevPageCommand { get; }
        public ReactiveCommand<Unit, Unit> NextPageCommand { get; }
        public ReactiveCommand<Unit, Unit> FilterCommand { get; }
        public ReactiveCommand<Unit, Unit> LatestModeCommand { get; }
        public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

        public string SourceName => _source.Name;
        
        // Show English flag indicator for MangaDex source
        public bool IsMangaDex => _source.Name.Contains("MangaDex", System.StringComparison.OrdinalIgnoreCase);
        
        // MangaDex Language Toggle (EN/ID)
        private bool _isEnglish = true; // Default: English
        public bool IsEnglish
        {
            get => _isEnglish;
            set
            {
                this.RaiseAndSetIfChanged(ref _isEnglish, value);
                this.RaisePropertyChanged(nameof(LanguageFlag));
                this.RaisePropertyChanged(nameof(LanguageCode));
            }
        }
        
        public string LanguageFlag => IsEnglish ? "🇬🇧" : "🇮🇩";
        public string LanguageCode => IsEnglish ? "EN" : "ID";
        public ReactiveCommand<Unit, Unit> ToggleLanguageCommand { get; }

        private bool _isLatestMode = false; // Default: Directory Mode (false) vs Pustaka Mode (true)
        public bool IsLatestMode 
        { 
            get => _isLatestMode; 
            set => this.RaiseAndSetIfChanged(ref _isLatestMode, value); 
        }
        public bool IsPaginationVisible => !_isLatestMode && !IsMangaDexBlocked;

        // Filter Dialog
        private FilterDialogViewModel? _filterVM;
        public FilterDialogViewModel FilterVM => _filterVM ??= new FilterDialogViewModel();

        private bool _isFilterOpen;
        public bool IsFilterOpen
        {
            get => _isFilterOpen;
            set => this.RaiseAndSetIfChanged(ref _isFilterOpen, value);
        }

        public ReactiveCommand<Unit, Unit> CloseFilterCommand { get; }

        private readonly NetworkService _networkService;
        
        public SourceFeedViewModel(IMangaSource source, MainWindowViewModel mainVm, SourceManager sourceManager, ImageCacheService imageCacheService, NetworkService networkService)
        {
            _source = source;
            _mainVm = mainVm;
            _sourceManager = sourceManager;
            _imageCacheService = imageCacheService;
            _networkService = networkService; // Store for usage

            SearchCommand = ReactiveCommand.CreateFromTask(async () => await PerformSearch());
            OpenMangaCommand = ReactiveCommand.Create<MangaItem>(OpenManga);

            BackCommand = ReactiveCommand.Create(() => { _mainVm.GoToBrowse(); });
            OpenSettingsCommand = ReactiveCommand.Create(() => { _mainVm.GoToSettings(); });


            // Refresh: Force reload ignoring cache (except for Latest mode where API is inconsistent)
            RefreshCommand = ReactiveCommand.CreateFromTask(async () => 
            {
                // COMPLETELY SKIP refresh for Latest mode - KomikCast API returns inconsistent data
                // Exception: If we are in an error/blocked state, we MUST allow refresh to retry connection
                if (_isLatestMode && !HasError && !IsMangaDexBlocked)
                {
                    // Latest mode (Healthy): DO NOTHING - preserve existing data
                    // User can toggle Latest off/on to get fresh data if needed
                    return;
                }
                
                // Directory mode: normal refresh behavior
                string cacheKey = GetCacheKey();
                _sourceManager.InvalidateCache(cacheKey); 
                await LoadMangaList(append: false, forceRefresh: true);
            });

            // Open Filter Popup
            FilterCommand = ReactiveCommand.Create(() => 
            {
                IsFilterOpen = true;
            });

            // Close Filter Popup
            CloseFilterCommand = ReactiveCommand.Create(() =>
            {
                IsFilterOpen = false;
            });

            // Wire up filter dialog events
            FilterVM.OnApply += () =>
            {
                IsFilterOpen = false;
                CurrentPage = 1;
                _ = LoadMangaList(append: false);
            };

            FilterVM.OnClose += () =>
            {
                IsFilterOpen = false;
            };

            // Latest Updates Toggle
            LatestModeCommand = ReactiveCommand.Create(() => 
            {
                IsLatestMode = !IsLatestMode;
                CurrentPage = 1;
                this.RaisePropertyChanged(nameof(IsPaginationVisible));
                _ = LoadMangaList(append: false);
            });

            // MangaDex Language Toggle (EN/ID)
            ToggleLanguageCommand = ReactiveCommand.Create(() =>
            {
                IsEnglish = !IsEnglish;
                
                // Set MangaDexSource.SelectedLanguage via reflection (extension loaded dynamically)
                var newLang = IsEnglish ? "en" : "id";
                var sourceType = _source.GetType();
                var langProp = sourceType.GetProperty("SelectedLanguage", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (langProp != null)
                {
                    langProp.SetValue(null, newLang);
                    System.Console.WriteLine($"[SourceFeed] Set MangaDex language to: {newLang}");
                }
                else
                {
                    System.Console.WriteLine($"[SourceFeed] WARNING: Could not find SelectedLanguage property on {sourceType.Name}");
                }
                
                // Invalidate ALL caches for this source to force fresh data with new language
                _sourceManager.InvalidateAllCachesForSource(_source.Id);
                
                // Clear current list for immediate visual feedback
                MangaList.Clear();
                
                CurrentPage = 1;
                // Force refresh to bypass any remaining cache
                _ = LoadMangaList(append: false, forceRefresh: true);
            });

            PrevPageCommand = ReactiveCommand.Create(() => 
            {
                if (HasPrevPage)
                {
                    CurrentPage--;
                    _ = LoadMangaList();
                }
            });
            
            NextPageCommand = ReactiveCommand.Create(() => 
            {
                if (HasNextPage)
                {
                    CurrentPage++;
                    _ = LoadMangaList(append: _isLatestMode); // Append if in Latest Mode (Infinite Scroll)
                }
            });

            // Initial Load (Fire and forget safely)
            System.Threading.Tasks.Task.Run(async () => await LoadMangaList(append: false));

            // Auto-retry when network status changes to online and we are blocked
            _networkService.StatusChanged += (s, isOnline) =>
            {
                if (isOnline && IsMangaDexBlocked)
                {
                    System.Diagnostics.Debug.WriteLine("[SourceFeedVM] Network recovered while blocked. Auto-refreshing...");
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        RefreshCommand.Execute().Subscribe();
                    });
                }
            };
        }

        private async Task LoadPopular()
        {
            CurrentPage = 1;
            await LoadMangaList(append: false);
        }

        private System.Threading.CancellationTokenSource? _cts;

        private async Task PerformSearch()
        {
            var query = SearchText; // Capture current query at start
            
            if (string.IsNullOrWhiteSpace(query))
            {
                await LoadPopular();
                return;
            }

            // Cancel previous image loads (Popular list or previous search)
            _cts?.Cancel();
            _cts = new System.Threading.CancellationTokenSource();
            var token = _cts.Token;

            IsLoading = true;
            HasError = false;
            ErrorMessage = null;

            // Reset pagination for search (since interface doesn't support search pagination yet)
            CurrentPage = 1;
            TotalPages = 1;
            
            // Only clear if query is still valid
            if (SearchText != query) return;
            
            Avalonia.Threading.Dispatcher.UIThread.Post(() => MangaList.Clear());
            
            this.RaisePropertyChanged(nameof(PageInfo));
            this.RaisePropertyChanged(nameof(HasPrevPage));
            this.RaisePropertyChanged(nameof(HasNextPage));

            System.Diagnostics.Debug.WriteLine($"[SourceFeedVM] Searching for: {query}");

            // Try Cache for Search
            string cacheKey = $"SEARCH:{_source.Id}:{query}:{CurrentPage}";
            var cached = _sourceManager.GetCachedResult(cacheKey);
            if (cached != null)
            {
                System.Diagnostics.Debug.WriteLine($"[SourceFeedVM] Search Cache Hit: {cacheKey}");
                Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                {
                    foreach (var m in cached.Items)
                    {
                        MangaList.Add(ConvertToVm(m, token));
                    }
                    IsLoading = false;
                });
                return;
            }

            try
            {
                var mangas = await _source.GetSearchMangaAsync(query, 1);
                
                // STALENESS CHECK: If user typed more while we were waiting, discard result
                if (SearchText != query) 
                {
                     System.Diagnostics.Debug.WriteLine($"[SourceFeedVM] Discarding stale result for '{query}' (Current: '{SearchText}')");
                     return;
                }

                // Cache Search Results
                _sourceManager.SetCachedResult(cacheKey, mangas, 1);

                Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                {
                    if (mangas.Count == 0)
                    {
                        // Maybe show "No results" message? For now just empty list.
                        ErrorMessage = "No results found.";
                        // HasError = true; // Optional
                    }

                    foreach (var m in mangas)
                    {
                        MangaList.Add(ConvertToVm(m, token));
                    }
                });
            }
            catch (System.Exception ex)
            {
                // STALENESS CHECK
                if (SearchText != query) return;

                System.Diagnostics.Debug.WriteLine($"[SourceFeedVM] Error searching: {ex}");
                ErrorMessage = $"Search failed: {ex.Message}";
                HasError = true;
            }
            finally
            {
                // Only turn off loading if we are the latest query
                if (SearchText == query)
                {
                    IsLoading = false;
                }
            }
        }
        
        private void OpenManga(MangaItem item)
        {
            // For now, we just open details. 
            // In future, we need to pass the REAL Manga object, not just the display item.
            _mainVm.GoToDetail(item);
        }

        private string GetCacheKey()
        {
             // Key format: "SourceId:Page:Mode:FilterHash" [Search is handled separately]
             int statusFilter = FilterVM.GetStatusFilter();
             int typeFilter = FilterVM.GetTypeFilter();
             return $"{_source.Id}:{CurrentPage}:{IsLatestMode}:{statusFilter}:{typeFilter}";
        }

        private async Task LoadMangaList(bool append = false, bool forceRefresh = false)
        {
            if (IsLoading) return; // Prevent double trigger
            
            // Cancel previous image loads if we are resetting list
            if (!append)
            {
                _cts?.Cancel();
                _cts = new System.Threading.CancellationTokenSource();
            }
            var token = _cts?.Token ?? System.Threading.CancellationToken.None;

            IsLoading = true;
            HasError = false;
            ErrorMessage = null;
            IsMangaDexBlocked = false; // Reset blocking flag behavior
            
            if (!append)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => MangaList.Clear());
            }
            
            string modeName = _isLatestMode ? "Latest Updates (Pustaka)" : "Directory";
            string cacheKey = GetCacheKey();
            
            System.Diagnostics.Debug.WriteLine($"[SourceFeedVM] LoadMangaList started for {_source.Name} [{modeName}], Page {CurrentPage}, Append: {append}, Key: {cacheKey}");
            
            // CHECK CACHE (Only if not appending and not forcing refresh)
            
            if (!forceRefresh && !append) 
            {
                var cached = _sourceManager.GetCachedResult(cacheKey);
                if (cached != null)
                {
                     System.Diagnostics.Debug.WriteLine($"[SourceFeedVM] Cache Hit for {cacheKey}");
                     Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                     {
                        TotalPages = cached.TotalPages;
                        this.RaisePropertyChanged(nameof(PageInfo));
                        this.RaisePropertyChanged(nameof(HasPrevPage));
                        this.RaisePropertyChanged(nameof(HasNextPage));
                        
                        foreach (var m in cached.Items)
                        {
                            MangaList.Add(ConvertToVm(m, token));
                        }
                        IsLoading = false;
                     });
                     
                     // Helper: Pre-fill next page if list is small in Latest Mode
                     if (_isLatestMode && cached.Items.Count < 20 && HasNextPage)
                     {
                         // Trigger next page logic (simplified)
                     }
                     return;
                }
            }

            try
            {
                // Use IFilterableMangaSource if supported
                if (_source is IFilterableMangaSource filterable)
                {
                    System.Collections.Generic.List<Manga> items;
                    int totalPages;

                    // Get filter values
                    int statusFilter = FilterVM.GetStatusFilter();
                    int typeFilter = FilterVM.GetTypeFilter();
                    
                    // Use filtered endpoint if filters are active (status 1-2 for Ongoing/Completed, or type)
                    bool hasServerFilter = (statusFilter >= 1 && statusFilter <= 2) || typeFilter > 0;
                    
                    if (hasServerFilter)
                    {
                        // Use server-side filtering via pustaka endpoint
                        (items, totalPages) = await filterable.GetFilteredMangaAsync(CurrentPage, statusFilter, typeFilter);
                    }
                    else if (_isLatestMode)
                    {
                        (items, totalPages) = await filterable.GetLatestMangaAsync(CurrentPage);
                    }
                    else
                    {
                        (items, totalPages) = await filterable.GetMangaListAsync(CurrentPage); // Rename to GetMangaDirectoryAsync? Or just use GetPopularMangaAsync?
                        // KomikuSource.GetPopularMangaAsync -> uses Pustaka/page/X (This IS what GetMangaListAsync did essentially, but GetMangaListAsync returns tuple with totalPages!)
                        // Standard GetPopularMangaAsync returns only List<Manga>.
                        // So IFilterableMangaSource gives us (List, int TotalPages).
                    }
                    
                    // Apply remaining client-side filters (status 3-6 not supported by server)
                    if (statusFilter > 2)
                    {
                        items = items.Where(m => m.Status == statusFilter).ToList();
                    }
                    
                    // Update Cache
                    _sourceManager.SetCachedResult(cacheKey, items, totalPages);
                    
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                    {
                        TotalPages = totalPages;
                        this.RaisePropertyChanged(nameof(PageInfo));
                        this.RaisePropertyChanged(nameof(HasPrevPage));
                        this.RaisePropertyChanged(nameof(HasNextPage));
                        
                        foreach (var m in items)
                        {
                            // Avoid duplicates when appending
                            if (!append || !MangaList.Any(x => x.MangaUrl == m.Url))
                            {
                                MangaList.Add(ConvertToVm(m, token));
                            }
                        }
                    });
                    
                    System.Diagnostics.Debug.WriteLine($"[SourceFeedVM] Fetched {items.Count} items, Total Pages: {totalPages}");
                }
                else
                {
                    // Fallback for other sources
                    var mangas = await _source.GetPopularMangaAsync(CurrentPage);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                    {
                        foreach (var m in mangas)
                        {
                            MangaList.Add(ConvertToVm(m, token));
                        }
                    });
                }
            }

            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SourceFeedVM] Error loading manga list: {ex}");
                var innerMsg = ex.InnerException != null ? $"\nDetails: {ex.InnerException.Message}" : "";
                ErrorMessage = $"Failed to load content: {ex.Message}{innerMsg}";
                HasError = true;

                // 2. Detection Logic (Error Case)
                // Only treat as blocked if VPN is NOT running. If VPN is running, it's a genuine error/timeout.
                if (IsMangaDex && _networkService.IsOnline && !SingboxService.Instance.IsRunning)
                {
                    IsMangaDexBlocked = true;
                }
            }
            finally
            {
                IsLoading = false;
                System.Diagnostics.Debug.WriteLine($"[SourceFeedVM] LoadMangaList finished. IsLoading = {IsLoading}");

                // 3. Detection Logic (Empty List Case) behavior
                // Only treat as blocked if VPN is NOT running.
                if (IsMangaDex && !HasError && MangaList.Count == 0 && _networkService.IsOnline && !SingboxService.Instance.IsRunning)
                {
                     // If we are online, it's MangaDex, and empty result => Likely blocked
                     IsMangaDexBlocked = true;
                }

                // PRE-FILL Logic: If in Latest Mode (Infinite Scroll) and list is small, load next page automatically
                // This ensures enough content to enable scrolling
                if (_isLatestMode && MangaList.Count < 20 && HasNextPage)
                {
                     System.Diagnostics.Debug.WriteLine($"[SourceFeedVM] Pre-filling: List count {MangaList.Count} is low. Loading Page {CurrentPage + 1}...");
                     // Trigger Next Page Command safely on UI thread
                     Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                     {
                        // Explicitly use Unit.Default and Observer
                        NextPageCommand.Execute(System.Reactive.Unit.Default)
                                       .Subscribe(System.Reactive.Observer.Create<System.Reactive.Unit>(_ => { }));
                     });
                }
            }
        }

        private MangaItem ConvertToVm(Manga m, System.Threading.CancellationToken token = default)
        {
            var vm = new MangaItem
            {
                Title = m.Title,
                CoverUrl = m.ThumbnailUrl,
                SourceId = m.Source,
                MangaUrl = m.Url,
                LastUpdate = m.LastUpdate 
            };
            
            // Trigger background image load with cancellation
            _ = LoadCoverAsync(vm, token);
            
            return vm;
        }

        private async Task LoadCoverAsync(MangaItem item, System.Threading.CancellationToken token)
        {
            if (string.IsNullOrEmpty(item.CoverUrl)) return;
            
            // 1. Check Memory Cache
            var cachedBitmap = _imageCacheService.GetImage(item.CoverUrl);
            if (cachedBitmap != null)
            {
                 item.CoverBitmap = cachedBitmap;
                 return;
            }

            try
            {
                token.ThrowIfCancellationRequested();

                string requestUrl = item.CoverUrl;
                var customHeaders = new System.Collections.Generic.Dictionary<string, string>();

                // Parse Custom Headers: url|Key=Value&Key2=Value2
                if (item.CoverUrl.Contains("|"))
                {
                    var parts = item.CoverUrl.Split('|', 2);
                    requestUrl = parts[0];
                    if (parts.Length > 1)
                    {
                        var headers = parts[1].Split('&');
                        foreach (var header in headers)
                        {
                            var pair = header.Split('=', 2);
                            if (pair.Length == 2)
                            {
                                customHeaders[pair[0].Trim()] = pair[1].Trim();
                            }
                        }
                    }
                }

                // Create Optimized Client (Using NetworkService logic)
                using var client = _networkService.CreateOptimizedHttpClient();
                
                // Create request
                var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, requestUrl);
                
                // Apply Headers
                if (customHeaders.ContainsKey("Referer"))
                {
                    req.Headers.Referrer = new System.Uri(customHeaders["Referer"]);
                }
                else if (!requestUrl.Contains("envira-cdn"))
                {
                    // Default Fallback (Skip for Envira CDN as it fails with cross-origin referer)
                    try 
                    {
                        req.Headers.Referrer = new System.Uri(_source.BaseUrl);
                    }
                    catch (Exception refEx)
                    {
                        Console.WriteLine($"[SourceFeedVM] Warning: Failed to set default referer: {refEx.Message}");
                    }
                }

                if (customHeaders.ContainsKey("User-Agent"))
                {
                     req.Headers.UserAgent.TryParseAdd(customHeaders["User-Agent"]);
                }

                // Use ResponseHeadersRead to avoid buffering entire image before processing
                using var resp = await client.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, token);
                
                if (resp.IsSuccessStatusCode)
                {
                    // Copy to memory stream
                    using var networkStream = await resp.Content.ReadAsStreamAsync(token);
                    using var memoryStream = new System.IO.MemoryStream();
                    await networkStream.CopyToAsync(memoryStream, token);
                    memoryStream.Position = 0;
                    
                    token.ThrowIfCancellationRequested();

                    // Decode bitmap (heavy op) on thread pool
                    var bitmap = await Task.Run(() => new Avalonia.Media.Imaging.Bitmap(memoryStream), token);
                    
                    // SAVE TO CACHE (Use original full URL as key to match)
                    _imageCacheService.AddImage(item.CoverUrl, bitmap);

                    // UI Update
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                    {
                        if (!token.IsCancellationRequested)
                            item.CoverBitmap = bitmap;
                    });
                }
                else
                {
                     Console.WriteLine($"[CoverImage] Failed: {item.Title} - {resp.StatusCode} ({requestUrl})");
                }
            }
            catch (System.OperationCanceledException)
            {
                // Expected on scroll
            }
            catch (System.Exception ex)
            {
                Console.WriteLine($"[SourceFeedVM] Image load error for '{item.Title}': {ex.Message}");
            }
        }

        // Helper methods for type filtering
        private bool IsManhwa(Manga m)
        {
            var title = m.Title?.ToLower() ?? "";
            var url = m.Url?.ToLower() ?? "";
            return title.Contains("manhwa") || url.Contains("manhwa") || 
                   title.Contains("korean") || title.Contains("webtoon");
        }

        private bool IsManhua(Manga m)
        {
            var title = m.Title?.ToLower() ?? "";
            var url = m.Url?.ToLower() ?? "";
            return title.Contains("manhua") || url.Contains("manhua") || 
                   title.Contains("chinese") || title.Contains("cultivation");
        }
    }
}
