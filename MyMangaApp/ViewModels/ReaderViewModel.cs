using ReactiveUI;
using System;
using System.Reactive;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace MyMangaApp.ViewModels
{
    public static class MathConverters
    {
        public static Avalonia.Data.Converters.IValueConverter AddOne { get; } = 
            new Avalonia.Data.Converters.FuncValueConverter<int, string>(val => (val + 1).ToString());
    }

    public enum ReaderMode
    {
        Webtoon,
        Single,
        Double
    }

    public class PageViewModel : ReactiveObject
    {
        public string Url { get; }
        private readonly Core.Services.NetworkService _networkService;
        
        private Avalonia.Media.Imaging.Bitmap? _image;
        public Avalonia.Media.Imaging.Bitmap? Image
        {
            get => _image;
            set => this.RaiseAndSetIfChanged(ref _image, value);
        }

        private bool _isLoading = true;
        public bool IsLoading
        {
            get => _isLoading;
            set => this.RaiseAndSetIfChanged(ref _isLoading, value);
        }

        private string _error = string.Empty;
        public string Error
        {
            get => _error;
            set => this.RaiseAndSetIfChanged(ref _error, value);
        }

        public PageViewModel(string url, Core.Services.NetworkService networkService)
        {
            Url = url;
            _networkService = networkService;
            _ = LoadImage();
        }

        private async System.Threading.Tasks.Task LoadImage()
        {
            try
            {
                // Local File
                if (!Url.StartsWith("http"))
                {
                    if (System.IO.File.Exists(Url))
                    {
                         using var stream = System.IO.File.OpenRead(Url);
                         var bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                         
                         Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                         {
                             Image = bitmap;
                             IsLoading = false;
                         });
                         return;
                    }
                }

                // Check Network for Remote URL
                // If offline and remote URL, fail fast
                // But we shouldn't reach here if offline logic is correct in ReaderVM

                string requestUrl = Url;
                var customHeaders = new Dictionary<string, string>();
                
                // Parse Custom Headers: url|Key=Value&Key2=Value2
                if (Url.Contains("|"))
                {
                    var parts = Url.Split('|', 2); // Split only on first pipe
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
                
                Console.WriteLine($"[PageVM] Fetching: {requestUrl}");
                Console.WriteLine($"[PageVM] Headers: {string.Join(", ", customHeaders.Select(kv => $"{kv.Key}={kv.Value}"))}");
                
                // Use Optimized Client (Proxy Aware + Default Headers)
                using var client = _networkService.CreateOptimizedHttpClient();
                
                // Construct Request
                var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, requestUrl);
                
                // Add Referer header
                if (customHeaders.ContainsKey("Referer"))
                {
                    req.Headers.Referrer = new Uri(customHeaders["Referer"]);
                }
                else
                {
                    req.Headers.Referrer = new Uri("https://komiku.org");
                }
                
                // Add Origin header (critical for some CDNs)
                if (customHeaders.ContainsKey("Origin"))
                {
                    req.Headers.TryAddWithoutValidation("Origin", customHeaders["Origin"]);
                }
                
                if (customHeaders.ContainsKey("User-Agent"))
                {
                    req.Headers.UserAgent.TryParseAdd(customHeaders["User-Agent"]);
                }
                
                // CRITICAL: Add Sec-Fetch-* headers that Chrome sends for image requests
                // These Client Hints are used by CDNs to verify requests come from real browsers
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "image");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
                req.Headers.TryAddWithoutValidation("Sec-Ch-Ua", "\"Chromium\";v=\"120\", \"Google Chrome\";v=\"120\", \"Not_A Brand\";v=\"8\"");
                req.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Mobile", "?0");
                req.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Platform", "\"Windows\"");
                
                // Accept header for images
                req.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");

                var response = await client.SendAsync(req);
                Console.WriteLine($"[PageVM] Response: {response.StatusCode}, ContentType: {response.Content.Headers.ContentType}, Size: {response.Content.Headers.ContentLength}");
                
                if (!response.IsSuccessStatusCode)
                {
                     throw new Exception($"HTTP {response.StatusCode} {response.ReasonPhrase}");
                }
                
                var data = await response.Content.ReadAsByteArrayAsync();
                
                // Log first few bytes to detect if it's the placeholder
                if (data.Length > 20)
                {
                    Console.WriteLine($"[PageVM] First 20 bytes: {BitConverter.ToString(data.Take(20).ToArray())}");
                }
                
                using var remoteStream = new System.IO.MemoryStream(data);
                
                var remoteBitmap = new Avalonia.Media.Imaging.Bitmap(remoteStream);
                
                Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                {
                    Image = remoteBitmap;
                    IsLoading = false;
                });
            }
            catch (Exception ex)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                {
                    Error = "Failed: " + ex.Message;
                    IsLoading = false;
                });
            }
        }
    }

    public class ReaderViewModel : ViewModelBase
    {
        private readonly MainWindowViewModel _mainViewModel;
        private readonly Core.Services.SourceManager? _sourceManager;
        private readonly Core.Services.NetworkService _networkService;
        private readonly Core.Services.LibraryService? _libraryService;
        private ChapterItem? _currentChapter;
        private List<ChapterItem>? _allChapters;
        private int _currentChapterIndex;

        private string _title = "";
        public string Title
        {
            get => _title;
            set => this.RaiseAndSetIfChanged(ref _title, value);
        }

        private string _chapterTitle = "";
        public string ChapterTitle
        {
            get => _chapterTitle;
            set => this.RaiseAndSetIfChanged(ref _chapterTitle, value);
        }

        public ObservableCollection<PageViewModel> Pages { get; } = new();

        private int _currentPageIndex;
        public int CurrentPageIndex
        {
            get => _currentPageIndex;
            set 
            {
                this.RaiseAndSetIfChanged(ref _currentPageIndex, value);
                this.RaisePropertyChanged(nameof(CurrentPage));
            }
        }
        
        public PageViewModel? CurrentPage => Pages.Count > _currentPageIndex && _currentPageIndex >= 0 ? Pages[_currentPageIndex] : null;

        public ReactiveCommand<Unit, Unit> BackCommand { get; }
        
        public bool HasNextChapter => _allChapters != null && _currentChapterIndex > 0;
        public bool HasPrevChapter => _allChapters != null && _currentChapterIndex < _allChapters.Count - 1;
        
        public bool IsOnline => _networkService.IsOnline;

        
        private readonly long _sourceId;
        private readonly string _mangaTitle;
        private readonly string _mangaUrl;

        public ReaderViewModel(MainWindowViewModel mainViewModel, Core.Services.SourceManager? sourceManager, 
                               ChapterItem? chapter, System.Collections.Generic.List<ChapterItem>? allChapters, 
                               Core.Services.NetworkService? networkService,
                               Core.Services.LibraryService? libraryService = null,
                               long sourceId = 3, string mangaTitle = "", string mangaUrl = "")
        {
            _mainViewModel = mainViewModel;
            _sourceManager = sourceManager;
            _currentChapter = chapter;
            _allChapters = allChapters;
            _networkService = networkService ?? new Core.Services.NetworkService();
            _libraryService = libraryService;
            _sourceId = sourceId;
            _mangaTitle = mangaTitle;
            _mangaUrl = mangaUrl;

            // Find current chapter index in the list
            if (_allChapters != null && chapter != null)
            {
                _currentChapterIndex = _allChapters.FindIndex(c => c.Url == chapter.Url);
                if (_currentChapterIndex < 0) _currentChapterIndex = 0; // Fallback
                System.Diagnostics.Debug.WriteLine($"[ReaderVM] Chapter Index: {_currentChapterIndex} of {_allChapters.Count}");
            }
            
            if (chapter != null && _sourceManager != null)
            {
                ChapterTitle = chapter.Title;
                // If Title prop is not set, set it
                if (string.IsNullOrEmpty(Title) && !string.IsNullOrEmpty(mangaTitle)) Title = mangaTitle;
                
                // Mark as read immediately
                _ = MarkCurrentChapterAsReadAsync();
                
                System.Threading.Tasks.Task.Run(LoadPages);
            }

            BackCommand = ReactiveCommand.Create(() => 
            {
                if (CustomBackAction != null) CustomBackAction();
                else _mainViewModel.GoBack();
            });

            NextPageCommand = ReactiveCommand.Create(() => 
            {
                 if (CurrentPageIndex < Pages.Count - 1) CurrentPageIndex++;
                 else if (HasNextChapter) SwitchToChapter(_allChapters![_currentChapterIndex - 1], _currentChapterIndex - 1); // Logic depends on list order
            });
            PrevPageCommand = ReactiveCommand.Create(() => 
            {
                 if (CurrentPageIndex > 0) CurrentPageIndex--;
                 else if (HasPrevChapter) SwitchToChapter(_allChapters![_currentChapterIndex + 1], _currentChapterIndex + 1);
            });
            ToggleMenuCommand = ReactiveCommand.Create(() => { IsMenuVisible = !IsMenuVisible; });
            
            SetModeCommand = ReactiveCommand.Create<ReaderMode>(mode => 
            {
                IsWebtoon = mode == ReaderMode.Webtoon;
                this.RaisePropertyChanged(nameof(IsPaged));
            });
            
            NextChapterCommand = ReactiveCommand.Create(() => 
            {
                 if (HasNextChapter) SwitchToChapter(_allChapters![_currentChapterIndex - 1], _currentChapterIndex - 1);
            });
            
            PrevChapterCommand = ReactiveCommand.Create(() => 
            {
                 if (HasPrevChapter) SwitchToChapter(_allChapters![_currentChapterIndex + 1], _currentChapterIndex + 1);
            });
        }
        
        // ... (commands)
        public ReactiveCommand<Unit, Unit> NextPageCommand { get; }
        public ReactiveCommand<Unit, Unit> PrevPageCommand { get; }
        public ReactiveCommand<Unit, Unit> ToggleMenuCommand { get; }
        
        public ReactiveCommand<ReaderMode, Unit> SetModeCommand { get; }
        public ReactiveCommand<Unit, Unit> NextChapterCommand { get; }
        public ReactiveCommand<Unit, Unit> PrevChapterCommand { get; }

        private bool _isMenuVisible = true;
        public bool IsMenuVisible { get => _isMenuVisible; set => this.RaiseAndSetIfChanged(ref _isMenuVisible, value); }

        private bool _isWebtoon = true;
        public bool IsWebtoon 
        { 
            get => _isWebtoon; 
            set => this.RaiseAndSetIfChanged(ref _isWebtoon, value); 
        }
        
        public bool IsPaged => !IsWebtoon;
        
        public Action? CustomBackAction { get; set; }

        private async System.Threading.Tasks.Task MarkCurrentChapterAsReadAsync()
        {
            if (_currentChapter == null || _libraryService == null) return;
            
            try
            {
                // Update UI optimistically
                _currentChapter.IsRead = true;
                
                // Persist to DB
                // Pass extra info to support Online/Non-Library persistance
                string mangaUrlForDb = _mangaUrl;
                if (string.IsNullOrEmpty(mangaUrlForDb) && _sourceId > 0 && !string.IsNullOrEmpty(_currentChapter.Url))
                {
                    // Fallback infer from ChapterURL if we really have to, but hopefully _mangaUrl is set
                    // Assuming standard format like /manga/slug/chapter/slug
                    // This is risky, so relying on passed arg is best.
                }

                await _libraryService.MarkChapterAsReadAsync(
                    _currentChapter.Url, 
                    _sourceId, 
                    _mangaUrl, 
                    _currentChapter.Title, 
                    -1 // Chapter number parsing is complex, skipping for now
                );
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ReaderVM] Failed to mark as read: {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task LoadPages()
        {
            if (_sourceManager == null || _currentChapter == null) return;
            
            // Check if Downloaded
            if (_currentChapter.IsDownloaded)
            {
                try
                {
                    var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    var safeMangaTitle = string.Join("_", _mangaTitle.Split(System.IO.Path.GetInvalidFileNameChars()));
                    var safeChapterName = string.Join("_", _currentChapter.Title.Split(System.IO.Path.GetInvalidFileNameChars()));
                    
                    var chapterDir = System.IO.Path.Combine(appData, "MyMangaApp", "Downloads", _sourceId.ToString(), safeMangaTitle, safeChapterName);
                    
                    if (System.IO.Directory.Exists(chapterDir))
                    {
                        var files = System.IO.Directory.GetFiles(chapterDir)
                            .OrderBy(f => f) // Ensure order by filename (000.jpg, 001.jpg)
                            .ToList();
                            
                        if (files.Count > 0)
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                            {
                                Pages.Clear();
                                foreach(var file in files)
                                {
                                    Pages.Add(new PageViewModel(file, _networkService));
                                }
                                CurrentPageIndex = 0;
                            });
                            return; // Loaded from disk, exit
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ReaderVM] Error loading from disk: {ex}");
                }
            }
            
            if (!IsOnline)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                {
                     Pages.Clear();
                     ChapterTitle = "OFFLINE: Requires Internet Connection";
                });
                return;
            }

            // Remote Load
            var source = _sourceManager.GetSource(_sourceId); 
            if (source != null)
            {
                try 
                {
                    var urls = await source.GetPageListAsync(_currentChapter.Url);
                    // ...
                    
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                    {
                        Pages.Clear();
                        if (urls.Count > 0)
                        {
                            foreach(var url in urls)
                            {
                                Pages.Add(new PageViewModel(url, _networkService));
                            }
                            // Reset index
                            CurrentPageIndex = 0;
                        }
                        else
                        {
                             ChapterTitle = "Error: No pages found.";
                        }
                    });
                }
                catch (Exception ex)
                {
                     Avalonia.Threading.Dispatcher.UIThread.Post(() => ChapterTitle = "Error: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Switches to a new chapter in-place (for standalone windows)
        /// </summary>
        private void SwitchToChapter(ChapterItem newChapter, int newIndex)
        {
            _currentChapter = newChapter;
            _currentChapterIndex = newIndex;
            
            // Update UI
            ChapterTitle = newChapter.Title;
            Pages.Clear();
            
            // Notify property changes for navigation button states
            this.RaisePropertyChanged(nameof(HasPrevChapter));
            this.RaisePropertyChanged(nameof(HasNextChapter));
            
            // Mark the new chapter as read
            _ = MarkCurrentChapterAsReadAsync();
            
            // Reload pages
            System.Threading.Tasks.Task.Run(LoadPages);
        }
    }
}
