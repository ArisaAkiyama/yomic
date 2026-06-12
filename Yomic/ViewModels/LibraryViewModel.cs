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
        DateModified, // Most recent first
        UnreadCountDesc, // Most unread first
        LastReadDesc  // Recently read first
    }

    public enum LibraryFilterMode
    {
        All,
        UnreadOnly,
        OngoingOnly,
        CompletedOnly
    }

    public class LibraryViewModel : ViewModelBase
    {
        private static readonly string _cacheFolder;
        
        private List<MangaItem> _allItems = new(); // Store all items offline
        public ObservableCollection<MangaItem> LibraryItems { get; set; } = new();

        private readonly MainWindowViewModel _mainVM;
        private readonly Core.Services.LibraryService _libraryService;
        private readonly Core.Services.ImageCacheService _imageCacheService;
        private readonly Core.Services.SettingsService _settingsService;
        


        static LibraryViewModel()
        {
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

        private string _selectedSourceFilter = "All";
        public string SelectedSourceFilter
        {
            get => _selectedSourceFilter;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedSourceFilter, value);
                FilterLibrary();
            }
        }

        public ObservableCollection<string> AvailableSources { get; } = new ObservableCollection<string> { "All" };

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
        public ReactiveCommand<MangaItem, Unit> MarkAsUnreadCommand { get; }
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

        private LibraryFilterMode _selectedFilterMode = LibraryFilterMode.All;
        public LibraryFilterMode SelectedFilterMode
        {
            get => _selectedFilterMode;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedFilterMode, value);
                FilterLibrary();
            }
        }

        public ReactiveCommand<LibrarySortMode, Unit> SetSortModeCommand { get; }
        public ReactiveCommand<LibraryFilterMode, Unit> SetFilterModeCommand { get; }
        public Func<MangaItem, Task<bool>>? ConfirmDeleteFromDiskAsync { get; set; }
        
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

            OpenMangaCommand = ReactiveCommand.CreateFromTask<MangaItem>(async item => 
            {
                // Clear the badge in UI immediately
                item.HasNewChapters = false;
                
                // Clear the badge in DB (Fire and forget or await? Better await to ensure consistency but don't block navigation too much)
                // We can do it in background if needed, but let's await for safety.
                // However, navigation should be fast.
                _ = _libraryService.MarkMangaAsSeenAsync(item.MangaUrl, item.SourceId);

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
                item.UnreadCount = null;
                item.HasNewChapters = false;
            });

            // COMMAND: Mark as Unread (Clear Read Story)
            MarkAsUnreadCommand = ReactiveCommand.CreateFromTask<MangaItem>(async item => 
            {
                var manga = new Core.Models.Manga { Url = item.MangaUrl, Source = item.SourceId };
                // Call backend to mark all chapters as unread
                await _libraryService.MarkChaptersAsUnreadAsync(manga);
                
                // Update UI: Restore Unread Count
                var dbManga = await _libraryService.GetMangaByUrlAsync(item.MangaUrl, item.SourceId);
                if (dbManga != null && dbManga.Chapters != null)
                {
                    int unread = dbManga.Chapters.Count; // Since all are unread now
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                    {
                        item.UnreadCount = unread > 0 ? unread.ToString() : null;
                    });
                }
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
                var confirmed = ConfirmDeleteFromDiskAsync == null || await ConfirmDeleteFromDiskAsync(item);
                if (!confirmed)
                {
                    return;
                }

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

            // Filter Mode Command
            SetFilterModeCommand = ReactiveCommand.Create<LibraryFilterMode>(mode =>
            {
                SelectedFilterMode = mode;
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
                     if (existingItem.UnreadCount != unreadString) 
                     {
                         existingItem.UnreadCount = unreadString;
                         // If unread count changes and is > 0, show badge?
                         // Only if strictly increasing? Or just if > 0?
                         // User wants to hide on click. If we update library and find new chapters, should it reappear?
                         // Yes. If Unread > 0, show badge. Click hides it.
                         // But if we just click, unread count might stay same.
                         // So we only set IsNewBadgeVisible = true if Unread > 0 AND we are updating.
                         // Only update if changed to avoid unnecessary notifications
                         if (existingItem.HasNewChapters != m.HasNewChapters)
                             existingItem.HasNewChapters = m.HasNewChapters;
                     }
                      
                       if (existingItem.CoverUrl != m.ThumbnailUrl)
                       {
                           existingItem.CoverUrl = m.ThumbnailUrl;
                       }

                        // Update Status and LastViewed
                        if (existingItem.Status != m.Status) existingItem.Status = m.Status;
                        if (existingItem.LastViewed != m.LastViewed) existingItem.LastViewed = m.LastViewed;
                        if (string.IsNullOrEmpty(existingItem.SourceName)) existingItem.SourceName = _mainVM.SourceManager.GetSource(m.Source)?.Name;
                   }
                  else
                  {
                      // Add New
                      var newItem = new MangaItem
                      {
                           Title = m.Title,
                           CoverUrl = m.ThumbnailUrl,
                           SourceId = m.Source,
                           SourceName = _mainVM.SourceManager.GetSource(m.Source)?.Name,
                           MangaUrl = m.Url,
                           UnreadCount = unreadString,
                           Status = m.Status, // Fix: Map Status
                           LastViewed = m.LastViewed // Fix: Map LastViewed
                      };
                      newItem.HasNewChapters = m.HasNewChapters;
                      
                       _allItems.Add(newItem);
                 }
              }
             
             // 3. Update AvailableSources
             Avalonia.Threading.Dispatcher.UIThread.Post(() => 
             {
                 var sourcesInItems = _allItems
                     .Select(x => x.SourceName)
                     .Where(name => !string.IsNullOrEmpty(name))
                     .Select(name => name!)
                     .Distinct()
                     .OrderBy(x => x)
                     .ToList();
                 var desiredSources = new List<string> { "All" };
                 desiredSources.AddRange(sourcesInItems);

                 // Sync
                 var toRemoveSource = AvailableSources.Where(s => !desiredSources.Contains(s)).ToList();
                 foreach (var s in toRemoveSource) AvailableSources.Remove(s);

                 foreach (var s in desiredSources)
                 {
                     if (!AvailableSources.Contains(s)) AvailableSources.Add(s);
                 }

                 // Fix Order natively
                 for (int i = 0; i < desiredSources.Count; i++)
                 {
                     if (AvailableSources[i] != desiredSources[i])
                     {
                         var oldIndex = AvailableSources.IndexOf(desiredSources[i]);
                         AvailableSources.Move(oldIndex, i);
                     }
                 }

                 if (!AvailableSources.Contains(SelectedSourceFilter))
                 {
                     SelectedSourceFilter = "All";
                 }
                 else
                 {
                      // Re-trigger filter just in case
                      FilterLibrary();
                 }
             });
        }

        private void FilterLibrary()
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => 
            {
                 var query = _allItems.AsEnumerable();

                 // 1. Apply Search
                 if (!string.IsNullOrWhiteSpace(SearchText))
                 {
                     query = query.Where(x => x.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
                 }

                 // 2. Apply Source Filter
                 if (!string.IsNullOrEmpty(SelectedSourceFilter) && SelectedSourceFilter != "All")
                 {
                     query = query.Where(x => string.Equals(x.SourceName, SelectedSourceFilter, StringComparison.OrdinalIgnoreCase));
                 }

                 // 2. Apply Filter
                 query = SelectedFilterMode switch
                 {
                     LibraryFilterMode.UnreadOnly => query.Where(x => !string.IsNullOrEmpty(x.UnreadCount) && x.UnreadCount != "0"),
                     LibraryFilterMode.OngoingOnly => query.Where(x => x.Status == 1),
                     LibraryFilterMode.CompletedOnly => query.Where(x => x.Status == 2),
                     _ => query
                 };

                 // 3. Apply Sort
                 IEnumerable<MangaItem> sorted = SelectedSortMode switch
                 {
                     LibrarySortMode.TitleAsc => query.OrderBy(x => x.Title),
                     LibrarySortMode.TitleDesc => query.OrderByDescending(x => x.Title),
                     LibrarySortMode.DateModified => query.OrderByDescending(x => x.LastUpdate),
                     LibrarySortMode.UnreadCountDesc => query.OrderByDescending(x => int.TryParse(x.UnreadCount, out int c) ? c : 0),
                     LibrarySortMode.LastReadDesc => query.OrderByDescending(x => x.LastViewed),
                     _ => query.OrderBy(x => x.Title)
                 };
                      
                 var source = sorted.ToList();

                 // Smart Sync for ObservableCollection (Prevents full UI rebuild/flicker)
                 
                 // 1. Remove items not in source
                 var toRemove = LibraryItems.Where(i => !source.Contains(i)).ToList();
                 foreach(var item in toRemove) LibraryItems.Remove(item);
                 
                 // 2. Sync order and add new
                 for (int i = 0; i < source.Count; i++)
                 {
                     var item = source[i];
                     int existingIndex = LibraryItems.IndexOf(item);
                     
                     if (existingIndex == -1)
                     {
                         // Not in list, insert at correct index
                         LibraryItems.Insert(i, item);
                     }
                     else if (existingIndex != i)
                     {
                         // In list but wrong index, move it
                         LibraryItems.Move(existingIndex, i);
                     }
                 }
                 
                 IsLibraryEmpty = _allItems.Count == 0;
                 IsEmpty = LibraryItems.Count == 0;
                 HasNoResults = !IsLibraryEmpty && IsEmpty;

                 this.RaisePropertyChanged(nameof(HasItems));
            });
        }

    }
}
