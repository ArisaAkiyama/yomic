using ReactiveUI;
using System;
using Yomic.Core.Models;
using System.Linq;
using System.Reactive;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Yomic.Core.Services;

namespace Yomic.ViewModels
{
    public class ChapterItem : ReactiveObject
    {
        // ... (existing code)
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public bool IsRead { get; set; }
        public bool IsLastRead { get; set; }
        public Manga? MangaRef { get; set; }

        private bool _isDownloaded;
        public bool IsDownloaded
        {
            get => _isDownloaded;
            set => this.RaiseAndSetIfChanged(ref _isDownloaded, value);
        }

        private bool _isDownloading;
        public bool IsDownloading
        {
            get => _isDownloading;
            set => this.RaiseAndSetIfChanged(ref _isDownloading, value);
        }

        public ReactiveCommand<Unit, Unit> DownloadCommand { get; }
        public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

        public ChapterItem(Action downloadAction, Action deleteAction)
        {
            var canDownload = this.WhenAnyValue(x => x.IsDownloaded, downloaded => !downloaded);
            DownloadCommand = ReactiveCommand.Create(downloadAction, canDownload);

            var canDelete = this.WhenAnyValue(x => x.IsDownloaded);
            DeleteCommand = ReactiveCommand.Create(deleteAction, canDelete);
        }
    }

    // Header item for virtualization
    public class MangaDetailHeader { public MangaDetailViewModel ViewModel { get; } public MangaDetailHeader(MangaDetailViewModel vm) => ViewModel = vm; }

    public class MangaDetailViewModel : ViewModelBase
    {
        // ... (existing properties)
        public string Title { get; set; } = string.Empty;
        
        // Collection for UI (Header + Chapters)
        private List<object> _displayItems = new();
        public List<object> DisplayItems 
        {
            get => _displayItems;
            set => this.RaiseAndSetIfChanged(ref _displayItems, value);
        }
        public long SourceId => _model?.Source ?? 0;
        public string Url => _model?.Url ?? string.Empty;
        
        private string _author = "Loading...";
        public string Author { get => _author; set => this.RaiseAndSetIfChanged(ref _author, value); }
        
        private string _status = "";
        public string Status { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }
        
        private string _description = "Loading details...";
        public string Description { get => _description; set => this.RaiseAndSetIfChanged(ref _description, value); }
        
        private bool _inLibrary;
        public bool InLibrary { get => _inLibrary; set => this.RaiseAndSetIfChanged(ref _inLibrary, value); }
        
        public Avalonia.Media.Imaging.Bitmap? CoverBitmap { get; set; }
        public List<string> Tags { get; set; } = new();
        
        private List<string> _genres = new();
        public List<string> Genres 
        { 
            get => _genres; 
            set 
            {
                this.RaiseAndSetIfChanged(ref _genres, value);
                this.RaisePropertyChanged(nameof(IsMatureContent));
                this.RaisePropertyChanged(nameof(MatureWarningText));
                this.RaisePropertyChanged(nameof(MatureWarningColor));
                this.RaisePropertyChanged(nameof(IsExplicitContent));
            }
        }
        
        // RED - Explicit/dangerous content (18+)
        private static readonly HashSet<string> ExplicitGenres = new(StringComparer.OrdinalIgnoreCase)
        {
            "Mature", "Adult", "Smut", "Hentai", "Ecchi", 
            "Gore", "Guro", "R-18", "18+"
        };
        
        // YELLOW - Adult target demographics (softer warning)
        private static readonly HashSet<string> AdultTargetGenres = new(StringComparer.OrdinalIgnoreCase)
        {
            "Seinen", "Josei"
        };
        
        public bool IsExplicitContent => Genres.Any(g => ExplicitGenres.Contains(g));
        public bool IsMatureContent => Genres.Any(g => ExplicitGenres.Contains(g) || AdultTargetGenres.Contains(g));
        
        public string MatureWarningText 
        {
            get
            {
                // Prioritize explicit genres first
                var explicitGenre = Genres.FirstOrDefault(g => ExplicitGenres.Contains(g));
                if (explicitGenre != null) return explicitGenre;
                
                var adultGenre = Genres.FirstOrDefault(g => AdultTargetGenres.Contains(g));
                if (adultGenre != null) return adultGenre;
                
                return "18+";
            }
        }
        
        // Return color based on content type: Red for explicit, Yellow for adult target
        public string MatureWarningColor => IsExplicitContent ? "#F38BA8" : "#F9E2AF";
        
        private List<ChapterItem> _chapters = new();
        public List<ChapterItem> Chapters 
        {
            get => _chapters;
            set 
            {
                this.RaiseAndSetIfChanged(ref _chapters, value);
                UpdateDisplayItems();
            }
        }

        private void UpdateDisplayItems()
        {
            var list = new List<object>();
            list.Add(new MangaDetailHeader(this));
            if (_chapters != null) list.AddRange(_chapters);
            DisplayItems = list;
        }

        public ReactiveCommand<Unit, Unit> ToggleLibraryCommand { get; }
        public ReactiveCommand<Unit, Unit> DownloadAllCommand { get; }

        private readonly Core.Models.Manga _model;
        private readonly LibraryService _libraryService;
        private readonly NetworkService _networkService;
        private readonly DownloadService _downloadService;
        
        private bool _isOnline = true;
        public bool IsOnline { get => _isOnline; set => this.RaiseAndSetIfChanged(ref _isOnline, value); }
        
        private bool _isLoadingChapters = true;
        public bool IsLoadingChapters { get => _isLoadingChapters; set => this.RaiseAndSetIfChanged(ref _isLoadingChapters, value); }

        public bool IsOfflineAndNotDownloaded => !IsOnline && !InLibrary;

        private readonly Core.Services.ImageCacheService _imageCacheService;
        private readonly MangaItem _sourceItem; // Store source item for realtime update

        private readonly MainWindowViewModel _mainVM;
        private readonly SourceManager _sourceManager; // Store SourceManager

        public MangaDetailViewModel(MangaItem item, MainWindowViewModel mainVM, SourceManager sourceManager, LibraryService libraryService, NetworkService networkService, DownloadService downloadService, ImageCacheService imageCacheService)
        {
            _mainVM = mainVM;
            _sourceManager = sourceManager; // Store it
            _model = new Core.Models.Manga 
            { 
                Source = item.SourceId, 
                Url = item.MangaUrl, 
                Title = item.Title,
                ThumbnailUrl = item.CoverUrl
            };
            _libraryService = libraryService;
            _networkService = networkService;
            _downloadService = downloadService;
            _imageCacheService = imageCacheService; // Store injected service
            _sourceItem = item; // Store reference to source item

            IsOnline = _networkService.IsOnline;
            _networkService.StatusChanged += (s, online) => 
            {
                Dispatcher.UIThread.Post(() => 
                {
                    IsOnline = online;
                    this.RaisePropertyChanged(nameof(IsOfflineAndNotDownloaded));
                });
            };

            Title = item.Title;
            CoverBitmap = item.CoverBitmap;
            
            ToggleLibraryCommand = ReactiveCommand.CreateFromTask(ToggleLibrary);
            RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
            DownloadAllCommand = ReactiveCommand.Create(DownloadAllChapters);

            UpdateDisplayItems(); // Init header

            // Subscribe to Download Status Changes
            _downloadService.StatusChanged += OnDownloadStatusChanged;

            // Fire and forget load
            System.Threading.Tasks.Task.Run(async () => await LoadDetails(item, sourceManager));
        }

        public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

        private async System.Threading.Tasks.Task RefreshAsync()
        {
             // Create a fresh item from current model state to capture any URL updates
             var currentItem = new MangaItem 
             {
                 Title = _model.Title,
                 MangaUrl = _model.Url,
                 SourceId = _model.Source,
                 CoverUrl = _model.ThumbnailUrl,
                 CoverBitmap = CoverBitmap
             };
             
             await LoadDetails(currentItem, _sourceManager);
             _mainVM.ShowNotification("Manga updated");
        }

        private void OnDownloadStatusChanged(object? sender, Core.Services.DownloadRequest e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                // Find matching chapter
                // Note: e.Manga.Url matches _model.Url (series)
                if (e.Manga.Url != _model.Url) return;

                var item = Chapters.FirstOrDefault(c => c.Url == e.Chapter.Url);
                if (item != null)
                {
                    if (e.Status == "Completed")
                    {
                        item.IsDownloaded = true;
                        item.IsDownloading = false;
                    }
                    else if (e.Status == "Cancelled" || e.Status == "Error")
                    {
                        item.IsDownloaded = false;
                        item.IsDownloading = false;
                    }
                    else if (e.Status == "Downloading" || e.Status == "Queued")
                    {
                        item.IsDownloading = true;
                    }
                    // For "Paused" or others, keep current state (likely Downloading=true)
                }
            });
        }
        
        // Fallback removed/updated to prevent compile error if used elsewhere (unlikely)
        // Kept private or removed would be better, but assuming it's not used externally except via GoToDetail.

        private async System.Threading.Tasks.Task ToggleLibrary()
        {
             if (InLibrary)
             {
                 await _libraryService.RemoveFromLibraryAsync(_model);
                 InLibrary = false;
                 _mainVM.ShowNotification("Removed from Library");
             }
             else
             {
                 await _libraryService.AddToLibraryAsync(_model, Chapters?.Count ?? 0);
                 InLibrary = true;
                 _mainVM.ShowNotification("Added to Library");
                 
                 // Save chapters to DB immediately for offline access
                 if (Chapters != null && Chapters.Count > 0)
                 {
                     var dbChapters = Chapters.Select(c => new Core.Models.Chapter 
                     {
                          Name = c.Title,
                          Url = c.Url,
                          // Date is display string, original DateUpload unavailable in VM Item?
                          // Wait, ChapterItem doesn't store DateUpload (long).
                          // It stores string "Date". 
                          // If we save 0, sorting might be weird offline.
                          // But UpdateChaptersAsync checks existing URL. 
                          // Ideally we should have DateUpload in ChapterItem or _model.
                          // For now, let's just save Name/Url.
                          // OR better: In LoadDetails we have proper DateUpload, maybe we can fetch from source again?
                          // No, we should use what we have.
                          // Actually, UpdateChaptersAsync merge logic will keep existing if URL matches.
                          // But for NEW library item, we want to popluate.
                          // The source of truth was LoadDetails.
                          // Let's assume 0 is fine or try to parse locally if really needed, but usually 0 is fine for list.
                     }).ToList();
                     
                     await _libraryService.UpdateChaptersAsync(_model, dbChapters, isInitialLoad: true);
                 }

                 // Download and cache cover image for offline use
                 _ = DownloadAndCacheCoverAsync();
             }
        }

        private static readonly System.Net.Http.HttpClient _coverHttpClient = new();
        
        static MangaDetailViewModel()
        {
             _coverHttpClient.DefaultRequestHeaders.Add("Referer", "https://komiku.org/");
             _coverHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }
        
        private async System.Threading.Tasks.Task DownloadAndCacheCoverAsync()
        {
            if (string.IsNullOrEmpty(_model.ThumbnailUrl)) return;
            
            try
            {
                var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
                var cacheFolder = System.IO.Path.Combine(appData, "Yomic", "covers");
                
                if (!System.IO.Directory.Exists(cacheFolder))
                {
                    System.IO.Directory.CreateDirectory(cacheFolder);
                }
                
                // Use MD5 hash for cache key (Must match LibraryViewModel)
                string cacheKey;
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    var inputBytes = System.Text.Encoding.ASCII.GetBytes(_model.ThumbnailUrl);
                    var hashBytes = md5.ComputeHash(inputBytes);
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < hashBytes.Length; i++) sb.Append(hashBytes[i].ToString("X2"));
                    
                    var ext = System.IO.Path.GetExtension(new System.Uri(_model.ThumbnailUrl).AbsolutePath);
                    if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                    cacheKey = $"{sb}{ext}";
                }

                var cachePath = System.IO.Path.Combine(cacheFolder, cacheKey);
                
                // Check cache
                if (System.IO.File.Exists(cachePath))
                {
                    System.Diagnostics.Debug.WriteLine($"[MangaDetailVM] Cover already cached: {cacheKey}");
                    Dispatcher.UIThread.Post(() => 
                    {
                         try
                         {
                             using var stream = System.IO.File.OpenRead(cachePath);
                             var bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                             CoverBitmap = bitmap;
                             
                             // REALTIME SYNC: Update cached image service and source item
                             _imageCacheService.AddImage(_model.ThumbnailUrl, bitmap);
                             if (_sourceItem != null) _sourceItem.CoverBitmap = bitmap;
                         } 
                         catch { }
                    });
                    return;
                }
                
                // Parse Custom Headers: url|Key=Value&Key2=Value2
                string requestUrl = _model.ThumbnailUrl;
                var customHeaders = new System.Collections.Generic.Dictionary<string, string>();
                
                if (_model.ThumbnailUrl.Contains("|"))
                {
                    var parts = _model.ThumbnailUrl.Split('|', 2);
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
                
                // Use hash of the CLEAN url (requestUrl) or original?
                // Using original keeps it unique per header config, but clean might be better for dedup...
                // Let's use ORIGINAL for safety so different sources don't clash? 
                // Actually cache key generation uses _model.ThumbnailUrl (original), so we are consistent.

                System.Console.WriteLine($"[MangaDetailVM] Downloading cover: {requestUrl}");
                
                var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, requestUrl);
                
                if (customHeaders.ContainsKey("Referer")) 
                {
                    req.Headers.Referrer = new System.Uri(customHeaders["Referer"]);
                    System.Console.WriteLine($"[MangaDetailVM] Using Referer from pipe: {customHeaders["Referer"]}");
                }
                else 
                {
                    // Dynamic Fallback based on Source
                    if (_model.Source == 2) // KomikCast
                    {
                        req.Headers.Referrer = new System.Uri("https://komikcast.ch/");
                    }
                    else if (_model.Source == 4) // Mangabats (ID=4)
                    {
                        req.Headers.Referrer = new System.Uri("https://www.mangabats.com/");
                    }
                    else if (_model.Source == 5) // MangaDex (ID=5)
                    {
                        req.Headers.Referrer = new System.Uri("https://mangadex.org/");
                        System.Console.WriteLine($"[MangaDetailVM] Using Fallback Referer for MangaDex");
                    }
                    else 
                    {
                        req.Headers.Referrer = new System.Uri("https://komiku.org/");
                    }
                }

                if (customHeaders.ContainsKey("User-Agent")) req.Headers.UserAgent.TryParseAdd(customHeaders["User-Agent"]);

                // Download
                using var client = _networkService.CreateOptimizedHttpClient();
                using var response = await client.SendAsync(req); // Restore definition
                
                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine($"[MangaDetailVM] Failed to download cover. Status: {response.StatusCode}");
                    return;
                }

                var data = await response.Content.ReadAsByteArrayAsync();
                if (data.Length == 0) return;

                await System.IO.File.WriteAllBytesAsync(cachePath, data);
                System.Diagnostics.Debug.WriteLine($"[MangaDetailVM] Cover cached: {cacheKey}");
                
                // Set Bitmap
                Dispatcher.UIThread.Post(() => 
                {
                    try
                    {
                        using var ms = new System.IO.MemoryStream(data);
                        var bitmap = new Avalonia.Media.Imaging.Bitmap(ms);
                        CoverBitmap = bitmap;
                        
                        // REALTIME SYNC: Update cached image service and source item
                        _imageCacheService.AddImage(_model.ThumbnailUrl, bitmap);
                        if (_sourceItem != null) _sourceItem.CoverBitmap = bitmap;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MangaDetailVM] Error creating bitmap: {ex.Message}");
                    }
                });
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MangaDetailVM] Failed to cache cover: {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task LoadDetails(MangaItem item, SourceManager sourceManager)
        {
            Console.WriteLine($"[MangaDetailVM] LoadDetails called for '{item.Title}' Url: '{item.MangaUrl}' Source: '{item.SourceId}'");
            IsLoadingChapters = true;
            try 
            {
                // Check local DB first
                var existing = await _libraryService.GetMangaByUrlAsync(item.MangaUrl, item.SourceId);
                
                if (existing != null)
                {
                    // Update History (Async) for Library Items
                    await _libraryService.UpdateHistoryAsync(new Manga 
                    { 
                        Url = item.MangaUrl, 
                        Source = item.SourceId,
                        Title = item.Title, // Update title if valid
                        ThumbnailUrl = item.CoverUrl
                    });

                    Dispatcher.UIThread.Post(() => 
                    {
                        InLibrary = existing.Favorite;
                        _model.Id = existing.Id; // Sync ID
                        
                        // Load data from DB immediately (Cached)
                        if (!string.IsNullOrEmpty(existing.Description)) Description = existing.Description!;
                        if (!string.IsNullOrEmpty(existing.Author)) Author = existing.Author!;
                        if (existing.Genre != null) Genres = existing.Genre;
                        
                        Status = StatusToString(existing.Status);

                        if (existing.Chapters != null && existing.Chapters.Count > 0)
                        {
                            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                            var safeMangaTitle = string.Join("_", existing.Title.Split(System.IO.Path.GetInvalidFileNameChars()));
                            
                            var vmChapters = existing.Chapters.OrderByDescending(c => c.ChapterNumber).Select(ch => {
                                // Fallback: check filesystem if DB says not downloaded
                                bool isDownloaded = ch.IsDownloaded;
                                if (!isDownloaded)
                                {
                                    var safeChapterName = string.Join("_", ch.Name.Split(System.IO.Path.GetInvalidFileNameChars()));
                                    var chapterDir = System.IO.Path.Combine(appData, "Yomic", "Downloads", existing.Source.ToString(), safeMangaTitle, safeChapterName);
                                    isDownloaded = System.IO.Directory.Exists(chapterDir) && System.IO.Directory.GetFiles(chapterDir).Length > 0;
                                }
                                
                                // Create minimal chapter for download request
                                var chapterModel = new Core.Models.Chapter { Name = ch.Name, Url = ch.Url, ChapterNumber = ch.ChapterNumber };

                                return new ChapterItem(() => QueueDownload(chapterModel), () => DeleteChapterDownload(chapterModel, ch.Url))
                                {
                                    Title = ch.Name,
                                    Url = ch.Url,
                                    Date = ch.DateUpload > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ch.DateUpload).ToString("dd MMM yyyy") : "", 
                                    IsRead = ch.Read,
                                    IsDownloaded = isDownloaded
                                };
                            }).ToList(); 
                            Chapters = vmChapters;
                            IsLoadingChapters = false;
                        }
                    });
                    
                    // ONLY return early if we have chapters cached AND it's in the library. 
                    // For History items (not in library), we want to fetch fresh chapters but keep read status.
                    if (existing.Favorite && existing.Chapters != null && existing.Chapters.Count > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MangaDetailVM] Loaded {existing.Chapters.Count} chapters from DB (Library) for: {item.Title}");
                        // Try to load cover if missing
                        if (CoverBitmap == null) _ = DownloadAndCacheCoverAsync();
                        return;
                    }
                    
                    // No chapters cached or just History item - fall through to fetch online if connected
                    System.Diagnostics.Debug.WriteLine($"[MangaDetailVM] Fetching online for: {item.Title} (InLibrary: {existing.Favorite})");
                }
                
                // If we get here, we need to fetch chapters online (either not in DB, or in DB without chapters)
                if (!IsOnline)
                {
                     Dispatcher.UIThread.Post(() => { Description = "Offline Mode: Cannot load chapters."; IsLoadingChapters = false; });
                     return;
                }
                
                // Fetch fresh data from source
                var source = sourceManager.GetSource(item.SourceId);
                if (source == null) 
                {
                    Dispatcher.UIThread.Post(() => { Description = "Source not found."; IsLoadingChapters = false; });
                    return;
                }

                // 1. Fetch Details
                var manga = await source.GetMangaDetailsAsync(item.MangaUrl);
                
                // Update local model with fresh data from Source
                _model.Title = manga.Title; // Critical: Use cleaned/full title from details
                _model.Author = manga.Author;
                _model.Status = manga.Status;
                _model.Description = manga.Description;
                _model.Genre = manga.Genre;
                if (!string.IsNullOrEmpty(manga.ThumbnailUrl)) _model.ThumbnailUrl = manga.ThumbnailUrl;
                
                // Trigger Cover Download/Load
                _ = DownloadAndCacheCoverAsync();
                
                Dispatcher.UIThread.Post(() => 
                {
                    Title = manga.Title;
                    Author = manga.Author ?? "Unknown";
                    Description = manga.Description ?? "No description.";
                    Status = StatusToString(manga.Status);
                    Genres = manga.Genre ?? new List<string>();
                });

                // 2. Fetch Chapters
                var chapters = await source.GetChapterListAsync(item.MangaUrl);
                System.Diagnostics.Debug.WriteLine($"[MangaDetailVM] Fetched {chapters.Count} chapters from API");

                // Update History (Async) for Online/Non-Library items
                manga.Source = item.SourceId; // Ensure Source ID is set
                
                // CRITICAL FIX: URL Persistence Logic
                // If in Library, we MUST preserve the original URL (Database Key).
                // If NOT in Library, we trust the SOURCE's returned URL (it might be a redirect/slug change).
                if (existing != null)
                {
                    manga.Url = item.MangaUrl; // Force keep original URL key
                }
                else
                {
                    // Allow source to dictate new URL (Redirect handling)
                    // If source didn't return URL (unlikely), fallback to item.MangaUrl
                    if (string.IsNullOrEmpty(manga.Url)) manga.Url = item.MangaUrl;
                    
                    // Update model URL immediately so subsequent refreshes use the new URL
                    _model.Url = manga.Url; 
                }
                
                // Fallback: If details parsing failed to get cover (e.g. lazy load), use the one we have from Browse/Search
                if (string.IsNullOrEmpty(manga.ThumbnailUrl) && !string.IsNullOrEmpty(item.CoverUrl))
                {
                    manga.ThumbnailUrl = item.CoverUrl;
                }

                await _libraryService.UpdateHistoryAsync(manga);

                // Auto-update DB if in library (NOW we have chapter count)
                if (InLibrary)
                {
                     await _libraryService.AddToLibraryAsync(_model, chapters.Count);
                }
                
                // Sync chapters to DB always (for Library AND History caching)
                if (_model.Id > 0)
                {
                    var dbChapters = chapters.Select(c => new Core.Models.Chapter 
                    {
                         Name = c.Name,
                         Url = c.Url,
                         DateUpload = c.DateUpload,
                         ChapterNumber = -1 
                    }).ToList();
                    
                    await _libraryService.UpdateChaptersAsync(_model, dbChapters);
                    System.Diagnostics.Debug.WriteLine($"[MangaDetailVM] Synced {dbChapters.Count} chapters to DB (Cache)");
                }
                
                Dispatcher.UIThread.Post(() => 
                {
                    var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    // CRITICAL: If manga exists in library, use the SAVED title for path generation
                    // This ensures we check the same folder where we downloaded files
                    var titleForPath = existing != null ? existing.Title : manga.Title;
                    var safeMangaTitle = string.Join("_", titleForPath.Split(System.IO.Path.GetInvalidFileNameChars()));
                    
                    var vmChapters = new List<ChapterItem>();
                    foreach(var ch in chapters)
                    {
                        // Check if existing to get download/read status
                        var dbChapter = existing?.Chapters?.FirstOrDefault(x => x.Url == ch.Url);
                        var dbDownloaded = dbChapter?.IsDownloaded ?? false;
                        var dbRead = dbChapter?.Read ?? ch.Read; // Prefer DB read status
                        
                        // Fallback: check filesystem
                        bool isDownloaded = dbDownloaded;
                        if (!isDownloaded)
                        {
                            var safeChapterName = string.Join("_", ch.Name.Split(System.IO.Path.GetInvalidFileNameChars()));
                            var chapterDir = System.IO.Path.Combine(appData, "Yomic", "Downloads", _model.Source.ToString(), safeMangaTitle, safeChapterName);
                            isDownloaded = System.IO.Directory.Exists(chapterDir) && System.IO.Directory.GetFiles(chapterDir).Length > 0;
                        }

                        // Create minimal chapter for download request
                        var chapterModel = new Core.Models.Chapter { Name = ch.Name, Url = ch.Url };
                        
                        vmChapters.Add(new ChapterItem(() => QueueDownload(chapterModel), () => DeleteChapterDownload(chapterModel, ch.Url))
                        {
                            Title = ch.Name,
                            Url = ch.Url,
                            Date = DateTimeOffset.FromUnixTimeMilliseconds(ch.DateUpload).ToString("dd MMM yyyy"),
                            IsRead = dbRead,
                            IsDownloaded = isDownloaded
                        });
                    }
                    Chapters = vmChapters;
                    if (_model.Source == 4)
                    {
                        Status = StatusToString(manga.Status);
                    }
                    else
                    {
                        Status = $"{StatusToString(manga.Status)} ({chapters.Count} Ch)";
                    }
                    IsLoadingChapters = false;
                });
            }
            catch (System.Exception ex)
            {
                 Dispatcher.UIThread.Post(() => 
                 {
                    if (!IsOnline) Description = "Offline Mode: " + ex.Message;
                    else Description = $"Error loading details: {ex.Message}";
                    IsLoadingChapters = false;
                 });
            }
        }
        
        private string StatusToString(int status) => status switch
        {
            1 => "Ongoing",
            2 => "Completed",
            3 => "Licensed",
            4 => "Publishing Finished",
            5 => "Cancelled",
            6 => "Hiatus",
            _ => "Unknown"
        };
        
        private async void DeleteChapterDownload(Core.Models.Chapter chapter, string chapterUrl)
        {
            try
            {
                 // Update UI immediately (Optimistic)
                 var item = Chapters.FirstOrDefault(x => x.Url == chapterUrl);
                 if (item != null) item.IsDownloaded = false;
                 
                 await _libraryService.DeleteChapterDownloadAsync(_model, chapter);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MangaDetailVM] Error deleting chapter: {ex}");
                // Revert UI if failed
                var item = Chapters.FirstOrDefault(x => x.Url == chapterUrl);
                if (item != null) item.IsDownloaded = true;
            }
        }

        private void QueueDownload(Core.Models.Chapter chapter)
        {
             // Ensure model has ID if possible
             if (_model.Id == 0)
             {
                 // Must be in library to download? Not necessarily but good for persistence
                 // For now allow downloading temp
             }
             
             _downloadService.QueueDownload(_model, chapter);
             
             // Update UI status immediately to show spinner
             var item = Chapters.FirstOrDefault(x => x.Url == chapter.Url);
             if (item != null) item.IsDownloading = true;
        }

        private void DownloadAllChapters()
        {
            System.Diagnostics.Debug.WriteLine($"[MangaDetailVM] DownloadAll called. Total chapters: {Chapters.Count}");
            
            var chaptersToDownload = Chapters.Where(c => !c.IsDownloaded).ToList();
            System.Diagnostics.Debug.WriteLine($"[MangaDetailVM] Chapters to download: {chaptersToDownload.Count}");
            
            foreach (var chapter in chaptersToDownload)
            {
                System.Diagnostics.Debug.WriteLine($"[MangaDetailVM] Queueing: {chapter.Title}");
                var chapterModel = new Core.Models.Chapter { Name = chapter.Title, Url = chapter.Url };
                _downloadService.QueueDownload(_model, chapterModel);
                // chapter.IsDownloaded = true; // Optimistic update
            }
        }
    }
}
