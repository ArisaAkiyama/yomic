using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Yomic.Core.Services;
using ReactiveUI;
using Avalonia.Threading;
using Yomic.Core.Models;
using System.Collections.Generic;
using System.Net.Http;
using System.IO;
using Avalonia.Media.Imaging;
using Microsoft.EntityFrameworkCore;

namespace Yomic.ViewModels
{
    public class HistoryViewModel : ViewModelBase, IDisposable
    {
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

        private bool _isRefreshing;
        public bool IsRefreshing
        {
            get => _isRefreshing;
            set => this.RaiseAndSetIfChanged(ref _isRefreshing, value);
        }

        public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearHistoryCommand { get; }
        public ReactiveCommand<MangaItem, Unit> RemoveHistoryItemCommand { get; }
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

            RefreshCommand = ReactiveCommand.CreateFromTask(async () => 
            {
                IsRefreshing = true;
                try 
                {
                    if (IsOffline)
                    {
                        await LoadHistory();
                    }
                    else
                    {
                        // Global Update requested by user
                        try 
                        {
                            LogService.Debug("History", "Refreshing global updates...");
                            await _libraryService.UpdateAllLibraryMangaAsync(_sourceManager);
                        }
                        catch (Exception ex)
                        {
                             System.Diagnostics.Debug.WriteLine($"[HistoryVM] Global update error: {ex}");
                        }
                        finally
                        {
                            await LoadHistory();
                        }
                    }
                }
                finally
                {
                    IsRefreshing = false;
                }
            });
            ClearHistoryCommand = ReactiveCommand.CreateFromTask(ClearHistoryAsync);
            RemoveHistoryItemCommand = ReactiveCommand.CreateFromTask<MangaItem>(RemoveHistoryItemAsync);
            
            OpenMangaCommand = ReactiveCommand.CreateFromTask<MangaItem>(async item => 
            {
                item.IsNewBadgeVisible = false;
                System.Console.WriteLine($"[HistoryVM] Opening manga: {item.Title}, Cover: {item.CoverUrl}");
                
                // 1. Update DB timestamp
                var manga = new Yomic.Core.Models.Manga 
                {
                    Url = item.MangaUrl,
                    Source = item.SourceId,
                    Title = item.Title,
                    ThumbnailUrl = item.CoverUrl // Note: This might be stale if UI is stale
                };
                await _libraryService.UpdateHistoryAsync(manga);

                // 2. Navigate
                Dispatcher.UIThread.Post(() => _mainVM.GoToDetail(item));
                
                // 3. Local UI Update (Move to top)
                Dispatcher.UIThread.Post(() => 
                {
                    var existing = HistoryItems.FirstOrDefault(x => x.MangaUrl == item.MangaUrl && x.SourceId == item.SourceId);
                    if (existing != null)
                    {
                        HistoryItems.Remove(existing);
                        existing.LastReadTime = "Just now";
                        // If we have a valid cover in item, use it, otherwise keep existing
                        if (!string.IsNullOrEmpty(item.CoverUrl)) existing.CoverUrl = item.CoverUrl;
                        
                        HistoryItems.Insert(0, existing);
                        HasItems = true;
                    }
                });
            });
            
            // Initial load
            _ = LoadHistory();
        }

        public async Task RemoveHistoryItemAsync(MangaItem item)
        {
            if (item == null) return;
            
            try 
            {
                 // 1. Update DB first
                 using var context = new Yomic.Core.Data.MangaDbContext();
                 await context.Database.ExecuteSqlRawAsync("UPDATE Mangas SET LastViewed = 0 WHERE Url = {0} AND Source = {1}", item.MangaUrl, item.SourceId);

                 // 2. Remove from local list after a tiny delay to let ContextMenu close 
                 // This prevents "PlatformImpl is null" error in Avalonia
                 await Task.Delay(100);
                 Dispatcher.UIThread.Post(() => 
                 {
                    HistoryItems.Remove(item);
                    HasItems = HistoryItems.Count > 0;
                 });
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"[HistoryVM] Error removing item: {ex}");
            }
        }

        public async Task ClearHistoryAsync()
        {
            try
            {
                using var context = new Yomic.Core.Data.MangaDbContext();
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
                using var context = new Yomic.Core.Data.MangaDbContext();
                // Include Chapters for UnreadCount
                var query = context.Mangas
                    .Include(m => m.Chapters)
                    .Where(m => m.LastViewed > 0)
                    .OrderByDescending(m => m.LastViewed)
                    .Take(50);

                var history = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(query);

                Dispatcher.UIThread.Post(() =>
                {
                    var newList = new System.Collections.Generic.List<MangaItem>();
                    foreach (var m in history)
                    {
                        if (m.Title.Contains("Jujutsu")) System.Console.WriteLine($"[HistoryVM] Loading {m.Title} with Cover: {m.ThumbnailUrl}");
                        
                        int unread = m.Chapters?.Count(c => !c.Read) ?? 0;
                        string? unreadString = unread > 0 ? unread.ToString() : null;

                        var item = new MangaItem
                        {
                            Title = m.Title,
                            CoverUrl = m.ThumbnailUrl,
                            SourceId = m.Source,
                            MangaUrl = m.Url,
                            LastReadTime = GetTimeAgo(m.LastViewed),
                            Status = m.Status,
                            ChapterCount = m.Chapters?.Count ?? 0,
                            UnreadCount = unreadString,
                        };
                        if (unread > 0) item.IsNewBadgeVisible = true;
                        
                        newList.Add(item);
                    }

                    // Atomic swap approach
                    HistoryItems.Clear();
                    foreach (var item in newList) HistoryItems.Add(item);
                    
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

        public void Dispose()
        {
            // 1. Clear local items
            if (HistoryItems != null)
            {
                HistoryItems.Clear();
            }

            HasItems = false;
            System.Diagnostics.Debug.WriteLine("[HistoryVM] Disposed and history items cleared.");
        }
    }
}
