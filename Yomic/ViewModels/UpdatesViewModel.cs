using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Yomic.Core.Services;
using Yomic.Core.Models;
using ReactiveUI;
using Avalonia.Threading;
using System.Collections.Generic;

namespace Yomic.ViewModels
{
    public class UpdatesViewModel : ViewModelBase
    {
        private readonly LibraryService _libraryService;
        private readonly NetworkService _networkService;
        private readonly SourceManager _sourceManager;
        private readonly DownloadService _downloadService;
        private readonly MainWindowViewModel _mainVM;

        private ObservableCollection<UpdatesGroupParams> _groupedUpdates = new();
        public ObservableCollection<UpdatesGroupParams> GroupedUpdates
        {
            get => _groupedUpdates;
            set 
            {
                this.RaiseAndSetIfChanged(ref _groupedUpdates, value);
                this.RaisePropertyChanged(nameof(HasItems));
                this.RaisePropertyChanged(nameof(UpdatesCount));
            }
        }

        public bool HasItems => GroupedUpdates.Count > 0;
        public int UpdatesCount => GroupedUpdates.Sum(g => g.Items.Count);
        
        private bool _isEmpty;
        public bool IsEmpty
        {
            get => _isEmpty;
            set => this.RaiseAndSetIfChanged(ref _isEmpty, value);
        }

        private bool _isOffline;
        public bool IsOffline
        {
            get => _isOffline;
            set => this.RaiseAndSetIfChanged(ref _isOffline, value);
        }

        public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
        public ReactiveCommand<ChapterItem, Unit> OpenChapterCommand { get; }
        public ReactiveCommand<Unit, Unit> GoToLibraryCommand { get; }

        public UpdatesViewModel(LibraryService libraryService, NetworkService networkService, SourceManager sourceManager, DownloadService downloadService, MainWindowViewModel mainVM)
        {
            _libraryService = libraryService;
            _networkService = networkService;
            _sourceManager = sourceManager;
            _downloadService = downloadService;
            _mainVM = mainVM;

            // Offline Logic
            IsOffline = !_networkService.IsOnline;
            _networkService.StatusChanged += (s, isOnline) =>
            {
                Dispatcher.UIThread.Post(() => IsOffline = !isOnline);
            };

            GroupedUpdates = new ObservableCollection<UpdatesGroupParams>();
            RefreshCommand = ReactiveCommand.CreateFromTask(RefreshUpdatesAsync);
            OpenChapterCommand = ReactiveCommand.CreateFromTask<ChapterItem>(OpenChapter);
            GoToLibraryCommand = ReactiveCommand.Create(() => _mainVM.GoToLibrary());
            MarkPrevReadCommand = ReactiveCommand.CreateFromTask<ChapterItem>(MarkPreviousReadAsync);
            
            // Subscribe to Background Updates
            _libraryService.UpdatesFound += async (sender, count) => 
            {
                Dispatcher.UIThread.Post(async () => 
                {
                    await LoadUpdatesAsync();
                    if (count > 0)
                    {
                         _mainVM.NotificationVM.Show($"Found {count} new chapters!");
                    }
                });
            };

            _ = LoadUpdatesAsync();
        }

        public ReactiveCommand<ChapterItem, Unit> MarkPrevReadCommand { get; }

        private async Task MarkPreviousReadAsync(ChapterItem item)
        {
            if (item.MangaRef == null) return;
            
            // Extract Chapter Number
            // Note: ChapterItem doesn't hold number directly usually, relying on Title parsing or DB model.
            // But we can try to find it from DB if MangaRef is populated.
            // Actually, we need to know the number.
            // Let's assume we can re-fetch or extract.
            // To be safe, let's fetch the specific chapter first to get its number.
            
            // Or better: Let's assume LibraryService can handle URL lookup?
            // Wait, LibraryService needs (Manga, number).
            // We need to fetch THIS chapter to get its number.
            
            // Simpler: Extract number from title if possible, or fetch via URL
            // Let's try fetching via URL
            try
            {
                // We need to fetch the Chapter details to get its Number, 
                // because ChapterItem (UI model) might not exposure it.
                // Assuming we can get it via Service.
                
                // Hack: Pass URL and MangaRef to Service, service looks up Chapter Number.
                // But my Service method asks for number.
                
                // Let's do it here:
                var chapters = await _libraryService.GetRecentChaptersAsync(500); // Re-fetching is expensive but safe.
                var thisChapter = chapters.FirstOrDefault(c => c.Url == item.Url);
                
                if (thisChapter != null)
                {
                     await _libraryService.MarkChaptersBeforeAsReadAsync(item.MangaRef, thisChapter.ChapterNumber);
                     // Refresh
                     await LoadUpdatesAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdatesVM] Error marking prev read: {ex}");
            }
        }

        private async Task OpenChapter(ChapterItem item)
        {
            // We need to navigate to Reader directly
            // But Reader needs a full Chapter object and list of chapters
            // Simpler for now: Navigate to Manga Detail and then Auto-Open? 
            // Or ideally: ReaderViewModel accepts (Manga, ChapterItem).
            
            // Let's resolve the full manga first
            if (item.MangaRef != null)
            {
                // Create minimal object safely using standard helper
                var mangaItem = MangaItem.FromCoreManga(item.MangaRef);
                
                // Go to detail first (safe)
                _mainVM.GoToDetail(mangaItem);
            }
        }

        private bool _isLibraryEmpty;
        public bool IsLibraryEmpty
        {
            get => _isLibraryEmpty;
            set => this.RaiseAndSetIfChanged(ref _isLibraryEmpty, value);
        }

        public async Task LoadUpdatesAsync()
        {
            try
            {
                // Check if library is empty first
                var libraryItems = await _libraryService.GetLibraryMangaAsync();
                Dispatcher.UIThread.Post(() => IsLibraryEmpty = libraryItems.Count == 0);

                // Check if library is empty first (Optimized)
                var libraryCount = await _libraryService.GetLibraryCountAsync();
                
                if (libraryCount == 0)
                {
                    IsLibraryEmpty = true;
                    IsEmpty = true;
                    return;
                }
                
                IsLibraryEmpty = false;
                var chapters = await _libraryService.GetRecentChaptersAsync(100); // Get last 100 updates
                
                if (chapters.Count == 0)
                {
                    Dispatcher.UIThread.Post(() => IsEmpty = true);
                    return;
                }

                // Group by Date
                var groups = chapters
                    .GroupBy(c => GetTimeHeader(c.DateFetch))
                    .Select(g => new UpdatesGroupParams
                    {
                        Header = g.Key,
                        Items = new ObservableCollection<ChapterItem>(g.Select(c => new ChapterItem(null!, null!) 
                        {
                            Title = c.Name,
                            Url = c.Url,
                            // Store Manga Ref for navigation
                            MangaRef = c.Manga,
                            // Formatted time ago
                            Date = GetTimeAgo(c.DateFetch),
                            IsRead = c.Read,
                            IsDownloaded = c.IsDownloaded
                        }))
                    })
                    .ToList();

                Dispatcher.UIThread.Post(() =>
                {
                    GroupedUpdates = new ObservableCollection<UpdatesGroupParams>(groups);
                    IsEmpty = GroupedUpdates.Count == 0;
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdatesVM] Error loading: {ex}");
            }
        }

        private bool _isRefreshing;
        public bool IsRefreshing
        {
            get => _isRefreshing;
            set => this.RaiseAndSetIfChanged(ref _isRefreshing, value);
        }

        private string _statusMessage = "";
        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }

        private async Task RefreshUpdatesAsync()
        {
             if (IsRefreshing) return;
             IsRefreshing = true;
             StatusMessage = "Checking for updates...";
             
             try
             {
                 // Trigger Library Update
                 int newChapters = await _libraryService.UpdateAllLibraryMangaAsync(_sourceManager);
                 
                 await LoadUpdatesAsync();
                 
                 if (newChapters > 0)
                 {
                     StatusMessage = $"Found {newChapters} new chapters!";
                 }
                 else
                 {
                     StatusMessage = "Library is up to date.";
                 }
                 
                 // Clear message after delay
                 await Task.Delay(3000);
                 if (!IsRefreshing) StatusMessage = ""; 
             }
             finally
             {
                 IsRefreshing = false;
             }
        }

        private string GetTimeHeader(long dateFetch)
        {
            var date = DateTimeOffset.FromUnixTimeMilliseconds(dateFetch);
            var now = DateTimeOffset.Now;
            
            if (date.Date == now.Date) return "Today";
            if (date.Date == now.AddDays(-1).Date) return "Yesterday";
            if (date > now.AddDays(-7)) return "This Week";
            if (date > now.AddDays(-30)) return "This Month";
            return "Older";
        }

        private string GetTimeAgo(long dateFetch)
        {
            var date = DateTimeOffset.FromUnixTimeMilliseconds(dateFetch);
            var diff = DateTimeOffset.Now - date;
            
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
            return $"{(int)diff.TotalDays}d ago";
        }
    }
    
    // Helper Class for Grouping
    public class UpdatesGroupParams
    {
        public string Header { get; set; } = "";
        public ObservableCollection<ChapterItem> Items { get; set; } = new();
    }
}
