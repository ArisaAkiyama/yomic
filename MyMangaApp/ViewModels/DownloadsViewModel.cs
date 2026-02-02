using System.Collections.ObjectModel;
using System.Linq;
using ReactiveUI;
using System.Reactive;
using System.IO;
using Avalonia.Media.Imaging;
using System.Net.Http;
using System;

namespace MyMangaApp.ViewModels
{
    public class DownloadItemViewModel : ViewModelBase
    {
        private readonly Core.Services.DownloadRequest _request;
        public Core.Services.DownloadRequest Request => _request;
        
        public string Title => _request.Manga.Title ?? "Unknown Manga";
        public string ChapterName => _request.Chapter.Name;
        public string Status => _request.Status;
        public int Progress => _request.Progress;
        
        public string StatusColor => _request.Status switch 
        {
            "Downloading" => "#FF9900",
            "Completed" => "#A6E3A1",
            "Error" => "#F38BA8",
            "Paused" => "#F9E2AF",
            "Queued" => "#CDD6F4",
            _ => "#6C7086"
        };

        public bool IsActive => _request.Status == "Downloading";
        public bool IsIndeterminate => IsActive && Progress == 0;
        
        private Avalonia.Media.Imaging.Bitmap? _coverBitmap;
        public Avalonia.Media.Imaging.Bitmap? CoverBitmap
        {
            get => _coverBitmap;
            set => this.RaiseAndSetIfChanged(ref _coverBitmap, value);
        }

        // Commands
        public System.Windows.Input.ICommand CancelCommand { get; }
        public ReactiveCommand<Unit, Unit> ExportCommand { get; }

        public DownloadItemViewModel(Core.Services.DownloadRequest request, Core.Services.DownloadService service, MainWindowViewModel mainVM)
        {
            _request = request;
            CancelCommand = ReactiveCommand.Create(() => service.Cancel(request));
            
            ExportCommand = ReactiveCommand.CreateFromTask(async () => 
            {
                try
                {
                    var path = await service.ExportToCbz(request);
                    mainVM.ShowNotification($"Saved to: {Path.GetFileName(path)}");
                    
                    // Optional: Open folder?
                }
                catch (Exception ex)
                {
                    mainVM.ShowNotification($"Export Failed: {ex.Message}");
                }
            });

            _ = LoadCoverAsync();
        }

        private async System.Threading.Tasks.Task LoadCoverAsync()
        {
             if (string.IsNullOrEmpty(_request.Manga.ThumbnailUrl)) return;
             
             try
             {
                 var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                 var cacheFolder = Path.Combine(appData, "MyMangaApp", "covers");
                 if (!Directory.Exists(cacheFolder)) Directory.CreateDirectory(cacheFolder);

                 var cacheKey = GetCacheKey(_request.Manga.ThumbnailUrl);
                 var filePath = Path.Combine(cacheFolder, cacheKey);

                 if (File.Exists(filePath))
                 {
                     using var stream = File.OpenRead(filePath);
                     CoverBitmap = new Bitmap(stream);
                 }
                 else
                 {
                     // Parse Headers
                     string requestUrl = _request.Manga.ThumbnailUrl;
                     var customHeaders = new System.Collections.Generic.Dictionary<string, string>();
                     if (_request.Manga.ThumbnailUrl.Contains("|"))
                     {
                         var parts = _request.Manga.ThumbnailUrl.Split('|', 2);
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

                     using var client = new HttpClient();
                     var req = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                     
                     if (customHeaders.ContainsKey("Referer")) 
                     {
                        req.Headers.Referrer = new Uri(customHeaders["Referer"]);
                     }
                     else if (_request.Manga.Source == 4) // Mangabats (ID=4)
                     {
                        req.Headers.Referrer = new Uri("https://www.mangabats.com/");
                     }
                     else 
                     {
                        req.Headers.Referrer = new Uri(requestUrl); // Fallback
                     }

                     if (customHeaders.ContainsKey("User-Agent")) req.Headers.UserAgent.TryParseAdd(customHeaders["User-Agent"]);
                     else req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

                     using var response = await client.SendAsync(req);
                     
                     if (!response.IsSuccessStatusCode) return; // Prevent saving error pages

                     var data = await response.Content.ReadAsByteArrayAsync();
                     await File.WriteAllBytesAsync(filePath, data);
                     
                     using var stream = new MemoryStream(data);
                     CoverBitmap = new Bitmap(stream);
                 }
             }
             catch { /* Ignore errors for now */ }
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
            
            string ext = ".jpg";
            try 
            {
                ext = Path.GetExtension(new Uri(cleanUrl).AbsolutePath);
                if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            }
            catch {}
            
            return $"{sb.ToString()}{ext}";
        }
        
        // Helper to refresh UI
        public void RaisePropertyChanges()
        {
            this.RaisePropertyChanged(nameof(Status));
            this.RaisePropertyChanged(nameof(Progress));
            this.RaisePropertyChanged(nameof(StatusColor));
            this.RaisePropertyChanged(nameof(IsActive));
            this.RaisePropertyChanged(nameof(IsIndeterminate));
        }
    }

    public class DownloadsViewModel : ViewModelBase
    {
        private readonly MainWindowViewModel _mainViewModel;
        private readonly Core.Services.DownloadService _downloadService;



        public ObservableCollection<DownloadItemViewModel> QueuedItems { get; set; } = new();

        private bool _hasDownloads;
        public bool HasDownloads
        {
            get => _hasDownloads;
            set => this.RaiseAndSetIfChanged(ref _hasDownloads, value);
        }

        public int QueueCount => QueuedItems.Count(x => x.Status != "Completed" && x.Status != "Error" && x.Status != "Cancelled");
        public bool HasQueue => QueueCount > 0;

        public ReactiveCommand<Unit, Unit> ResumeAllCommand { get; }
        public ReactiveCommand<Unit, Unit> PauseAllCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearCompletedCommand { get; }

        public DownloadsViewModel(MainWindowViewModel mainViewModel, Core.Services.DownloadService downloadService)
        {
            _mainViewModel = mainViewModel;
            _downloadService = downloadService;
            
            ResumeAllCommand = ReactiveCommand.Create(() => _downloadService.Resume());
            PauseAllCommand = ReactiveCommand.Create(() => _downloadService.Pause());
            ClearCompletedCommand = ReactiveCommand.Create(() => _downloadService.ClearCompleted());

            // Subscribe to events
            _downloadService.QueueChanged += OnQueueChanged;
            _downloadService.ProgressChanged += OnProgressChanged;
            _downloadService.StatusChanged += OnStatusChanged;
            _downloadService.IsDownloadingChanged += (s, isDownloading) => 
            {
                 Avalonia.Threading.Dispatcher.UIThread.Post(() => _mainViewModel.IsDownloading = isDownloading);
            };
            
            RefreshList();
        }

        private void OnQueueChanged(object? sender, Core.Services.DownloadRequest e)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(RefreshList);
        }

        private void OnStatusChanged(object? sender, Core.Services.DownloadRequest e)
        {
             Avalonia.Threading.Dispatcher.UIThread.Post(() => 
             {
                 var item = QueuedItems.FirstOrDefault(x => x.Title == e.Manga.Title && x.ChapterName == e.Chapter.Name);
                 
                 // If cancelled, remove from list to simulate "Delete" behavior
                 if (e.Status == "Cancelled")
                 {
                     if (item != null) QueuedItems.Remove(item);
                     UpdateState();
                     return;
                 }

                 item?.RaisePropertyChanges();
                 this.RaisePropertyChanged(nameof(QueueCount));
                 this.RaisePropertyChanged(nameof(HasQueue));
                 
                 // Also check global status
                 if (e.Status == "Completed")
                 {
                     _mainViewModel.ShowNotification($"Downloaded: {e.Chapter.Name}");
                 }
                 
                 if (e.Status == "Completed")
                 {
                     _mainViewModel.ShowNotification($"Downloaded: {e.Chapter.Name}");
                 }
                 
                 // Removed Manual IsDownloading calculation (Handled by Service Event)
             });
        }

        private void OnProgressChanged(object? sender, Core.Services.DownloadRequest e)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => 
            {
                var item = QueuedItems.FirstOrDefault(x => x.Title == e.Manga.Title && x.ChapterName == e.Chapter.Name);
                item?.RaisePropertyChanges();
                
                if (e.Status == "Downloading")
                {
                    _mainViewModel.DownloadProgress = e.Progress;
                }
            });
        }

        private void RefreshList()
        {
            var sourceList = _downloadService.AllDownloads.Where(x => x.Status != "Cancelled").ToList();
            
            // 1. Remove items not in source
            for (int i = QueuedItems.Count - 1; i >= 0; i--)
            {
                if (!sourceList.Contains(QueuedItems[i].Request))
                {
                    QueuedItems.RemoveAt(i);
                }
            }

            // 2. Add or Reorder
            for (int i = 0; i < sourceList.Count; i++)
            {
                var req = sourceList[i];
                if (i < QueuedItems.Count && QueuedItems[i].Request == req)
                {
                    continue; // Match
                }

                var existing = QueuedItems.FirstOrDefault(x => x.Request == req);
                if (existing != null)
                {
                    // Move
                    int oldIndex = QueuedItems.IndexOf(existing);
                    QueuedItems.Move(oldIndex, i);
                }
                else
                {
                    // Insert
                    QueuedItems.Insert(i, new DownloadItemViewModel(req, _downloadService, _mainViewModel));
                }
            }
            
            UpdateState();
            this.RaisePropertyChanged(nameof(QueueCount));
            this.RaisePropertyChanged(nameof(HasQueue));
        }

        private void UpdateState()
        {
            HasDownloads = QueuedItems.Any();
            this.RaisePropertyChanged(nameof(QueueCount));
            this.RaisePropertyChanged(nameof(HasQueue));
        }
    }
}
