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
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Yomic.ViewModels
{
    public class SourceFeedViewModel : ViewModelBase, IDisposable
    {
        private readonly IMangaSource _source;
        private readonly MainWindowViewModel _mainVm;
        private readonly SourceManager _sourceManager;
        private readonly ImageCacheService _imageCacheService;
        
        private readonly Bitmap? _gbFlag;
        private readonly Bitmap? _idFlag;
        
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
            set 
            {
                this.RaiseAndSetIfChanged(ref _isLoading, value);
                this.RaisePropertyChanged(nameof(IsPaginationVisible));
            }
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

        private bool _isRefreshing;
        public bool IsRefreshing
        {
            get => _isRefreshing;
            set => this.RaiseAndSetIfChanged(ref _isRefreshing, value);
        }

        // IsBusy: Controls the full-screen blocking overlay (Initial Load, Refresh, Search)
        // Does NOT track background pagination (infinite scroll)
        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set => this.RaiseAndSetIfChanged(ref _isBusy, value);
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

        public string PageInfo => $"Page {CurrentPage} of {TotalPages}";
        public bool HasPrevPage => CurrentPage > 1;
        public bool HasNextPage => CurrentPage < TotalPages;
        
        private string _targetPageInput = "";
        public string TargetPageInput
        {
            get => _targetPageInput;
            set => this.RaiseAndSetIfChanged(ref _targetPageInput, value);
        }

        public ObservableCollection<MangaItem> MangaList { get; } = new();
        public bool IsSourceEmpty => MangaList.Count == 0;

        public ReactiveCommand<Unit, Unit> SearchCommand { get; }
        public ReactiveCommand<MangaItem, Unit> OpenMangaCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }
        public ReactiveCommand<Unit, Unit> PrevPageCommand { get; }
        public ReactiveCommand<Unit, Unit> NextPageCommand { get; }
        public ReactiveCommand<Unit, Unit> GoToPageCommand { get; }
        public ReactiveCommand<Unit, Unit> LatestModeCommand { get; }
        public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

        public ReactiveCommand<Unit, Unit> CloseErrorCommand { get; }
        public ReactiveCommand<Unit, Unit> ContactDevCommand { get; }
        public ReactiveCommand<Unit, Unit> DownloadUpdateCommand { get; }

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
                this.RaisePropertyChanged(nameof(LanguageFlagBitmap));
            }
        }
        
        public string LanguageFlag => IsEnglish ? "🇬🇧" : "🇮🇩";
        public string LanguageCode => IsEnglish ? "EN" : "ID";
        public Bitmap? LanguageFlagBitmap => IsEnglish ? _gbFlag : _idFlag;
        public ReactiveCommand<Unit, Unit> ToggleLanguageCommand { get; }

        private bool _isLatestMode = false; // Default: Directory Mode (false) vs Pustaka Mode (true)
        public bool IsLatestMode 
        { 
            get => _isLatestMode; 
            set => this.RaiseAndSetIfChanged(ref _isLatestMode, value); 
        }
        public bool IsPaginationVisible => !_isLatestMode && !IsMangaDexBlocked && !IsLoading;

        private readonly NetworkService _networkService;
        
        public SourceFeedViewModel(IMangaSource source, MainWindowViewModel mainVm, SourceManager sourceManager, ImageCacheService imageCacheService, NetworkService networkService)
        {
            _source = source;
            _mainVm = mainVm;
            _sourceManager = sourceManager;
            _imageCacheService = imageCacheService;
            _networkService = networkService; // Store for usage

            try
            {
                _gbFlag = new Bitmap(AssetLoader.Open(new Uri("avares://Yomic/Assets/Flags/gb.png")));
                _idFlag = new Bitmap(AssetLoader.Open(new Uri("avares://Yomic/Assets/Flags/id.png")));
            }
            catch (Exception ex)
            {
                LogService.Error("SourceFeedVM", "Failed to load flag icons", ex);
                // Fallback or handle nulls if necessary, but for now we assume assets exist
            }

            SearchCommand = ReactiveCommand.CreateFromTask(async () => await PerformSearch());
            OpenMangaCommand = ReactiveCommand.Create<MangaItem>(OpenManga);
            
            MangaList.CollectionChanged += (s, e) => this.RaisePropertyChanged(nameof(IsSourceEmpty));

            BackCommand = ReactiveCommand.Create(() => 
            { 
                // Clear all cache for this source when leaving
                ClearSourceCache();
                _mainVm.GoToBrowse(); 
            });
            OpenSettingsCommand = ReactiveCommand.Create(() => { _mainVm.GoToSettings(); });


            // Refresh: Force reload ignoring cache (except for Latest mode where API is inconsistent)
            RefreshCommand = ReactiveCommand.CreateFromTask(async () => 
            {
                
                IsRefreshing = true;
                try 
                {
                    // Directory mode: normal refresh behavior
                    string cacheKey = GetCacheKey();
                    _sourceManager.InvalidateCache(cacheKey); 
                    await LoadMangaList(append: false, forceRefresh: true);
                }
                finally
                {
                    IsRefreshing = false;
                }
            });

            // Extension Error Banner Commands
            CloseErrorCommand = ReactiveCommand.Create(() => 
            {
                HasError = false;
                ErrorMessage = null;
            });

            ContactDevCommand = ReactiveCommand.Create(() => 
            {
                _mainVm.RequestFeedbackDialog?.Invoke();
            });

            DownloadUpdateCommand = ReactiveCommand.Create(() => 
            {
                _mainVm.ShowUpdateDialog();
            });


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
                    LogService.Info("SourceFeed", $"Set MangaDex language to: {newLang}");
                }
                else
                {
                    LogService.Warning("SourceFeed", $"Could not find SelectedLanguage property on {sourceType.Name}");
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

            GoToPageCommand = ReactiveCommand.Create(() => 
            {
                if (int.TryParse(TargetPageInput, out int target))
                {
                    // Clamp to valid bounds
                    if (target < 1) target = 1;
                    if (target > TotalPages) target = TotalPages;
                    
                    if (CurrentPage != target)
                    {
                        CurrentPage = target;
                        _ = LoadMangaList(append: false); // Direct jump replaces content
                    }
                }
                TargetPageInput = ""; // Clear input after go
            });

            // Initial Load (Fire and forget safely)
            System.Threading.Tasks.Task.Run(async () => await LoadMangaList(append: false));

            // Auto-retry when network status changes to online and we are blocked
            _networkService.StatusChanged += (s, isOnline) =>
            {
                if (isOnline && IsMangaDexBlocked)
                {
                    LogService.Debug("SourceFeedVM", "Network recovered while blocked. Auto-refreshing...");
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

            MonitorLoadingSpeed();
            IsLoading = true;
            IsBusy = true; // Block UI for search
            HasError = false;
            ErrorMessage = null;

            // Reset pagination for search
            CurrentPage = 1;
            TotalPages = 1; // Search results are currently flat 1-page lists in most sources
            
            // Only clear if query is still valid
            if (SearchText != query) return;
            
            Avalonia.Threading.Dispatcher.UIThread.Post(() => MangaList.Clear());
            
            this.RaisePropertyChanged(nameof(PageInfo));
            this.RaisePropertyChanged(nameof(HasPrevPage));
            this.RaisePropertyChanged(nameof(HasNextPage));

            LogService.Debug("SourceFeedVM", $"Searching for: {query}");

            // Try Cache for Search
            string cacheKey = $"SEARCH:{_source.Id}:{query}:{CurrentPage}";
            var cached = _sourceManager.GetCachedResult(cacheKey);
            if (cached != null)
            {
                LogService.Debug("SourceFeedVM", $"Search Cache Hit: {cacheKey}");
                Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                {
                    foreach (var m in cached.Items)
                    {
                        MangaList.Add(ConvertToVm(m, token));
                    }
                    IsLoading = false;
                    IsBusy = false;
                });
                return;
            }

            try
            {
                var mangas = await _source.GetSearchMangaAsync(query, 1);
                
                // STALENESS CHECK: If user typed more while we were waiting, discard result
                if (SearchText != query) 
                {
                     LogService.Debug("SourceFeedVM", $"Discarding stale result for '{query}' (Current: '{SearchText}')");
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
                    IsBusy = false;
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
             // Key format: "SourceId:Page:Mode" [Search is handled separately]
             return $"{_source.Id}:{CurrentPage}:{IsLatestMode}";
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

            MonitorLoadingSpeed();
            IsLoading = true;
            if (!append) IsBusy = true; // Block UI on initial load or full refresh, but NOT on append
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
                        IsBusy = false;
                     });
                     
                     // Helper: Pre-fill next page if list is small in Latest Mode
                     if (_isLatestMode && cached.Items.Count < 20 && HasNextPage)
                     {
                         // Trigger next page logic
                          Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                          {
                             // Explicitly use Unit.Default and Observer
                             NextPageCommand.Execute(System.Reactive.Unit.Default)
                                            .Subscribe(System.Reactive.Observer.Create<System.Reactive.Unit>(_ => { }));
                          });
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
                    if (_isLatestMode)
                    {
                        (items, totalPages) = await filterable.GetLatestMangaAsync(CurrentPage);
                    }
                    else
                    {
                        (items, totalPages) = await filterable.GetMangaListAsync(CurrentPage);
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
                    
                    LogService.Debug("SourceFeedVM", $"Fetched {items.Count} items, Total Pages: {totalPages}");
                }
                else
                {
                    // Fallback for other sources
                    var mangas = await _source.GetPopularMangaAsync(CurrentPage);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                    {
                        if (mangas.Count == 0)
                        {
                            TotalPages = CurrentPage; // Hit the end!
                        }
                        else
                        {
                            TotalPages = _source.IsHasMorePages ? 999 : CurrentPage;
                        }
                        
                        this.RaisePropertyChanged(nameof(PageInfo));
                        this.RaisePropertyChanged(nameof(HasPrevPage));
                        this.RaisePropertyChanged(nameof(HasNextPage));

                        foreach (var m in mangas)
                        {
                            // Avoid duplicates when appending
                            if (!append || !MangaList.Any(x => x.MangaUrl == m.Url))
                            {
                                MangaList.Add(ConvertToVm(m, token));
                            }
                        }
                    });
                    
                    LogService.Debug("SourceFeedVM", $"Fetched {mangas.Count} items, Total Pages: {TotalPages}");
                }
            }

            catch (System.Exception ex)
            {
                LogService.Error("SourceFeedVM", $"Error loading manga list: {ex.Message}", ex);
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
                if (!append) IsBusy = false;
                LogService.Debug("SourceFeedVM", $"LoadMangaList finished. IsLoading = {IsLoading}");

                // 3. Detection Logic (Empty List Case) behavior
                // REMOVED: Do not assume empty list means blocked. It could just be no results.

                // PRE-FILL Logic: If in Latest Mode (Infinite Scroll) and list is small, load next page automatically
                // This ensures enough content to enable scrolling
                if (_isLatestMode && MangaList.Count < 20 && HasNextPage)
                {
                     LogService.Debug("SourceFeedVM", $"Pre-filling: List count {MangaList.Count} is low. Loading Page {CurrentPage + 1}...");
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
                SourceId = _source.Id,
                MangaUrl = m.Url,
                LastUpdate = m.LastUpdate,
                Status = m.Status
            };
            
            
            return vm;
        }

        private bool _hasShownVpnTip = false;

        private void MonitorLoadingSpeed()
        {
            if (_hasShownVpnTip || !IsMangaDex || Core.Services.SingboxService.Instance.IsRunning) return;

            Task.Delay(8000).ContinueWith(t => 
            {
                if (IsLoading && _networkService.IsOnline)
                {
                     Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                     {
                         if (IsLoading && !_hasShownVpnTip) // Double check
                         {
                             _mainVm.ShowNotification("Loading taking too long? Try enabling VPN Bypass in Settings.", NotificationType.Warning);
                             _hasShownVpnTip = true;
                         }
                     });
                }
            });
        }

        /// <summary>
        /// Clears all cache (images and data) for the current source.
        /// Called when navigating back to free up memory.
        /// </summary>
        private void ClearSourceCache()
        {
            try
            {
                // 1. Clear image cache for this source
                _imageCacheService.ClearForSource(_source.BaseUrl);
                
                // 2. Clear data cache from SourceManager
                _sourceManager.InvalidateAllCachesForSource(_source.Id);
                
                // 3. Clear MangaList
                MangaList.Clear();
                
                // Force GC to reclaim memory
                GC.Collect(0, GCCollectionMode.Optimized);
                
                LogService.Info("SourceFeed", $"Cleared cache for source: {_source.Name}");
            }
            catch (Exception ex)
            {
                LogService.Error("SourceFeed", $"Error clearing cache: {ex.Message}", ex);
            }
        }

        public void Dispose()
        {
            // Fully purge source cache and bitmaps when disposed
            ClearSourceCache();
            _cts?.Cancel();
            _cts?.Dispose();
            
            _gbFlag?.Dispose();
            _idFlag?.Dispose();

            System.Diagnostics.Debug.WriteLine("[SourceFeedVM] Disposed and memory references cleared.");
        }
    }
}
