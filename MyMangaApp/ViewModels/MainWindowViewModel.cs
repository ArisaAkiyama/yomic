using ReactiveUI;
using System;
using System.Reactive;
using System.Collections.ObjectModel;

namespace MyMangaApp.ViewModels
{
    // Model sederhana untuk satu komik
    public class MangaItem : ViewModelBase
    {
        public string Title { get; set; } = string.Empty;
        public string? CoverUrl { get; set; } 
        public string? UnreadCount { get; set; }
        public string? LastReadTime { get; set; }
        public int Status { get; set; } // 1=Ongoing, 2=Completed, 5=Hiatus, 6=Cancelled
        
        public string StatusString => Status switch
        {
            1 => "Ongoing",
            2 => "Completed",
            5 => "Hiatus",
            6 => "Cancelled",
            _ => "Unknown"
        };
        
        // Context for Fetching Details
        public long SourceId { get; set; }
        public string MangaUrl { get; set; } = string.Empty; // This corresponds to Manga.Url (ID)

        private Avalonia.Media.Imaging.Bitmap? _coverBitmap;
        public Avalonia.Media.Imaging.Bitmap? CoverBitmap
        {
            get => _coverBitmap;
            set => this.RaiseAndSetIfChanged(ref _coverBitmap, value);
        }
        
        // Formatted Last Update String
        public long LastUpdate { get; set; }
        
        public string LastUpdateString 
        { 
            get 
            {
                if (LastUpdate == 0) return "";
                var time = DateTimeOffset.FromUnixTimeMilliseconds(LastUpdate);
                var diff = DateTimeOffset.Now - time;
                
                if (diff.TotalMinutes < 1) return "Just now";
                if (diff.TotalMinutes < 60) return $"{Math.Max(1, (int)diff.TotalMinutes)}m ago";
                if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
                if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
                return time.ToString("dd MMM yyyy");
            }
        }

        public string Description { get; set; } = string.Empty;

        // Helper to Create from Core Model safely
        public static MangaItem FromCoreManga(Core.Models.Manga m)
        {
            return new MangaItem
            {
                Title = m.Title,
                CoverUrl = m.ThumbnailUrl,
                SourceId = m.Source,
                MangaUrl = m.Url,
                Status = m.Status,
                LastUpdate = m.LastUpdate
            };
        }
    }

    public class MainWindowViewModel : ViewModelBase
    {
        public NotificationViewModel NotificationVM { get; } = new NotificationViewModel();

        private ViewModelBase? _currentPage;
        public ViewModelBase? CurrentPage
        {
            get => _currentPage;
            set 
            {
                this.RaiseAndSetIfChanged(ref _currentPage, value);
                this.RaisePropertyChanged(nameof(IsReaderMode));
            }
        }

        // True when CurrentPage is ReaderViewModel (used to hide sidebar)
        public bool IsReaderMode => _currentPage is ReaderViewModel;

        private LibraryViewModel? _libraryVM;
        public LibraryViewModel LibraryVM 
        { 
            get => _libraryVM ??= new LibraryViewModel(this, _libraryService, _networkService, _imageCacheService);
        }

        private readonly Core.Services.SourceManager _sourceManager;
        public Core.Services.SourceManager SourceManager => _sourceManager;
        
        private readonly Core.Services.LibraryService _libraryService;
        private readonly Core.Services.NetworkService _networkService;
        private readonly Core.Services.DownloadService _downloadService;

        public Core.Services.NetworkService NetworkService => _networkService;

        public Core.Services.DownloadService DownloadService => _downloadService;
        
        private readonly Core.Services.SettingsService _settingsService;
        public Core.Services.SettingsService SettingsService => _settingsService;
        
        public Core.Services.LibraryService LibraryService => _libraryService;
        
        private readonly Core.Services.ImageCacheService _imageCacheService;
        public Core.Services.ImageCacheService ImageCacheService => _imageCacheService;

        private bool _isPaneOpen = true;
        public bool IsPaneOpen
        {
            get => _isPaneOpen;
            set => this.RaiseAndSetIfChanged(ref _isPaneOpen, value);
        }

        public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> TogglePaneCommand { get; }

        public MainWindowViewModel(Core.Services.SourceManager sourceManager, 
                                   Core.Services.LibraryService libraryService, 
                                   Core.Services.NetworkService networkService,
                                   Core.Services.DownloadService downloadService,
                                   Core.Services.SettingsService settingsService,
                                   Core.Services.ImageCacheService imageCacheService)
        {
            _sourceManager = sourceManager;
            _libraryService = libraryService;
            _networkService = networkService;
            _downloadService = downloadService;
            _settingsService = settingsService;
            _imageCacheService = imageCacheService; // Store it
            
            // Subscribe to Network Status
            _networkService.StatusChanged += (s, isOnline) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (isOnline)
                         NotificationVM.Show("You are back online.", NotificationType.Success);
                    else
                         NotificationVM.Show("You are offline. Check your connection.", NotificationType.Error);
                });
            };

            TogglePaneCommand = ReactiveCommand.Create(() => { IsPaneOpen = !IsPaneOpen; });
            CheckFirstRun();
        }

        // Default constructor for Designer Preview (Optional, but good practice)
        public MainWindowViewModel() 
        {
            _sourceManager = new Core.Services.SourceManager(); // Fallback for designer
            _libraryService = new Core.Services.LibraryService();
            _networkService = new Core.Services.NetworkService();
            _downloadService = new Core.Services.DownloadService(_sourceManager, _libraryService, _networkService);
            _settingsService = new Core.Services.SettingsService();
            _imageCacheService = new Core.Services.ImageCacheService();
            TogglePaneCommand = ReactiveCommand.Create(() => { IsPaneOpen = !IsPaneOpen; });
            CheckFirstRun();
        }

        // Navigation History
        private readonly System.Collections.Generic.Stack<ViewModelBase> _navigationStack = new();

        public void GoBack()
        {
            if (_navigationStack.Count > 0)
            {
                CurrentPage = _navigationStack.Pop();
                
                // Refresh library when navigating back to it
                if (CurrentPage == LibraryVM)
                {
                    _ = LibraryVM.RefreshLibrary();
                }
            }
            else
            {
                // Default fallback
                GoToLibrary();
            }
        }

        public void GoToDetail(MangaItem item)
        {
            if (item == null) return;
            if (CurrentPage != null) _navigationStack.Push(CurrentPage);
            CurrentPage = new MangaDetailViewModel(item, this, _sourceManager, _libraryService, _networkService, _downloadService, _imageCacheService);
        }

        public void GoToLibrary()
        {
            if (CurrentPage != LibraryVM)
            {
                _navigationStack.Clear();
                CurrentPage = LibraryVM;
            }
            // Always refresh
            _ = LibraryVM.RefreshLibrary();
        }

        public void GoToReader(ChapterItem? chapter = null, System.Collections.Generic.List<ChapterItem>? allChapters = null, long sourceId = 3, string mangaTitle = "", string mangaUrl = "")
        {
            if (CurrentPage != null) _navigationStack.Push(CurrentPage);
            CurrentPage = new ReaderViewModel(this, _sourceManager, chapter, allChapters, _networkService, _libraryService, sourceId, mangaTitle, mangaUrl);
        }

        private BrowseViewModel? _browseVM;
        public BrowseViewModel BrowseVM
        {
            get => _browseVM ??= new BrowseViewModel(this, _sourceManager, _networkService);
        }

        public void GoToBrowse()
        {
            if (CurrentPage != BrowseVM)
            {
                _navigationStack.Clear();
                CurrentPage = BrowseVM;
            }
        }



        private SettingsViewModel? _settingsVM;
        public SettingsViewModel SettingsVM
        {
            get => _settingsVM ??= new SettingsViewModel(this, _libraryService, _settingsService, _sourceManager);
        }

        public void GoToSettings()
        {
            if (CurrentPage != SettingsVM)
            {
                CurrentPage = SettingsVM;
            }
        }

        private UpdatesViewModel? _updatesVM;
        public UpdatesViewModel UpdatesVM
        {
            get => _updatesVM ??= new UpdatesViewModel(_libraryService, _networkService, _sourceManager, _downloadService, this);
        }

        public void GoToUpdates()
        {
            if (CurrentPage != UpdatesVM)
            {
                CurrentPage = UpdatesVM;
                // Auto-refresh when entering updates view
                _ = UpdatesVM.LoadUpdatesAsync();
            }
        }

        private HistoryViewModel? _historyVM;
        public HistoryViewModel HistoryVM
        {
            get => _historyVM ??= new HistoryViewModel(_libraryService, _networkService, _sourceManager, this);
        }

        public void GoToHistory()
        {
            if (CurrentPage != HistoryVM)
            {
                CurrentPage = HistoryVM;
                // Always refresh history when navigating to it
                _ = HistoryVM.LoadHistory();
            }
        }

        private DownloadsViewModel? _downloadsVM;
        public DownloadsViewModel DownloadsVM
        {
            get => _downloadsVM ??= new DownloadsViewModel(this, _downloadService);
        }

        public void GoToDownloads()
        {
            if (CurrentPage != DownloadsVM)
            {
                CurrentPage = DownloadsVM;
            }
        }

        private ExtensionsViewModel? _extensionsVM;
        public ExtensionsViewModel ExtensionsVM
        {
            get => _extensionsVM ??= new ExtensionsViewModel(_sourceManager);
        }

        public void GoToExtensions()
        {
            if (CurrentPage != ExtensionsVM)
            {
                CurrentPage = ExtensionsVM;
            }
        }

        public void ShowNotification(string message, NotificationType type = NotificationType.Info)
        {
            NotificationVM.Show(message, type);
        }

        private WelcomeViewModel? _welcomeVM;
        public WelcomeViewModel WelcomeVM
        {
            get => _welcomeVM ??= new WelcomeViewModel(this);
        }

        // Read from settings
        public bool IsFirstRun 
        { 
            get => _settingsService.IsFirstRun;
            set 
            {
                if (_settingsService.IsFirstRun != value)
                {
                    _settingsService.IsFirstRun = value;
                    _settingsService.Save();
                    this.RaisePropertyChanged();
                }
            }
        } 

        public async void CheckFirstRun()
        {
            if (IsFirstRun)
            {
                CurrentPage = WelcomeVM;
            }
            else
            {
                CurrentPage = LibraryVM;
                
                // App Update Check
                if (_settingsService.CheckAppUpdateOnStart)
                {
                     // Placeholder check
                     // In future: Use an UpdateService
                     // For now, silent unless we want to show "Checked"
                }

                // Check if we need to update library on startup
                if (_settingsService.UpdateOnStart)
                {
                    ShowNotification("Updating library...");
                    int count = await _libraryService.UpdateAllLibraryMangaAsync(_sourceManager);
                    if (count > 0)
                    {
                        ShowNotification($"Library updated: {count} manga refreshed.");
                        // Refresh library view
                        _ = LibraryVM.RefreshLibrary();
                    }
                    else
                    {
                         // Optional: Show "No updates found" or just silent
                         // ShowNotification("Library up to date.");
                    }
                }
            }
        }

        public void CompleteOnboarding()
        {
            IsFirstRun = false;
            GoToLibrary();
        }

        private bool _isDownloading;
        public bool IsDownloading
        {
            get => _isDownloading;
            set => this.RaiseAndSetIfChanged(ref _isDownloading, value);
        }

        private int _downloadProgress;
        public int DownloadProgress
        {
            get => _downloadProgress;
            set => this.RaiseAndSetIfChanged(ref _downloadProgress, value);
        }
    }
}
