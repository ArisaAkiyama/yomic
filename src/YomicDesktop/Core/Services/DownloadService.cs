using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
// using Avalonia.Threading;
using Yomic.Core.Models;
// using Yomic.ViewModels;

namespace Yomic.Core.Services
{
    public class DownloadRequest
    {
        public Manga Manga { get; set; } = default!;
        public Chapter Chapter { get; set; } = default!;
        public int Progress { get; set; }
        public string Status { get; set; } = "Queued";
        public int RetryCount { get; set; } = 0;
        
        [System.Text.Json.Serialization.JsonIgnore]
        public CancellationTokenSource CancellationTokenSource { get; set; } = new();
    }

    public class DownloadService : IDisposable
    {
        private readonly SourceManager _sourceManager;
        private readonly LibraryService _libraryService;
        private readonly NetworkService _networkService;
        private readonly SettingsService? _settingsService;
        private readonly string _downloadBaseDir;

        // Queue management - using List + lock for proper removal support
        private readonly List<DownloadRequest> _queue = new();
        private readonly object _queueLock = new();
        private readonly List<DownloadRequest> _history = new(); // Completed or Error
        private readonly object _historyLock = new(); // Thread lock
        private readonly List<DownloadRequest> _activeDownloads = new();
        private volatile bool _isPaused;
        private volatile bool _isDisposed;
        private readonly EventHandler _onConnectionReset;
        private readonly Action<string> _onDownloadSpeedModeChanged;
        private HttpClient? _sharedDownloadClient;
        private readonly object _httpClientLock = new();
        private System.Threading.Timer? _saveDebounceTimer;
        private readonly object _debounceLock = new();
        private readonly object _saveFileLock = new();
        private static readonly TimeSpan PageListTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan PageDownloadTimeout = TimeSpan.FromSeconds(30);

        // Events
        public event EventHandler<DownloadRequest>? QueueChanged;
        public event EventHandler<DownloadRequest>? ProgressChanged;
        public event EventHandler<DownloadRequest>? StatusChanged;
        public event EventHandler<bool>? IsDownloadingChanged;

        public bool IsDownloading
        {
            get
            {
                lock (_processingLock)
                {
                    return _activeDownloads.Count > 0;
                }
            }
        }

        public IEnumerable<DownloadRequest> AllDownloads
        {
            get
            {
                List<DownloadRequest> historySnapshot;
                lock (_historyLock)
                {
                    historySnapshot = _history.ToList();
                }

                List<DownloadRequest> activeSnapshot;
                lock (_processingLock)
                {
                    activeSnapshot = _activeDownloads.ToList();
                }

                List<DownloadRequest> queueSnapshot;
                lock (_queueLock)
                {
                    queueSnapshot = _queue.ToList();
                }

                return historySnapshot
                    .Concat(activeSnapshot)
                    .Concat(queueSnapshot);
            }
        }

        public DownloadService(SourceManager sourceManager, LibraryService libraryService, NetworkService networkService, SettingsService? settingsService = null)
            : this(sourceManager, libraryService, networkService, settingsService, DownloadPathService.BaseDirectory, loadQueue: true)
        {
        }

        internal DownloadService(
            SourceManager sourceManager,
            LibraryService libraryService,
            NetworkService networkService,
            SettingsService? settingsService,
            string customBaseDir,
            bool loadQueue)
        {
            _sourceManager = sourceManager;
            _libraryService = libraryService;
            _networkService = networkService;
            _settingsService = settingsService;

            _onConnectionReset = (_, _) => ResetSharedHttpClient();
            _onDownloadSpeedModeChanged = _ => ProcessQueue();
            _networkService.ConnectionReset += _onConnectionReset;
            if (_settingsService != null)
            {
                _settingsService.DownloadSpeedModeChanged += _onDownloadSpeedModeChanged;
            }
            
            _downloadBaseDir = customBaseDir;
            if (!Directory.Exists(_downloadBaseDir))
                Directory.CreateDirectory(_downloadBaseDir);
            
            if (loadQueue)
            {
                LoadQueue();
            }
        }

        internal void RegisterActiveForTesting(DownloadRequest req)
        {
            lock (_processingLock)
            {
                _activeDownloads.Add(req);
            }
        }

        private (int MaxChapters, int MaxPagesPerChapter) GetConcurrencyLimits()
        {
            string mode = _settingsService?.DownloadSpeedMode ?? "fast";
            return mode.ToLowerInvariant() switch
            {
                "safe" => (2, 4),
                "max" => (4, 8),
                _ => (3, 6) // "fast" (Default Recommended)
            };
        }

        private HttpClient GetOrCreateSharedHttpClient()
        {
            if (_settingsService?.IsOfflineMode == true)
            {
                throw new System.Net.WebException("Application is in Offline Mode.");
            }
            lock (_httpClientLock)
            {
                if (_sharedDownloadClient == null)
                {
                    // Disable automatic redirects so per-hop Cookie scoping prevents cross-origin cookie leakage
                    _sharedDownloadClient = _networkService.CreateOptimizedHttpClient(useCookies: false, allowAutoRedirect: false);
                    _sharedDownloadClient.Timeout = Timeout.InfiniteTimeSpan;
                }
                return _sharedDownloadClient;
            }
        }

        public void ResetSharedHttpClient()
        {
            lock (_httpClientLock)
            {
                // Detach reference without immediately disposing an in-flight HttpClient used by active page tasks
                _sharedDownloadClient = null;
            }
        }

        private void SaveQueue()
        {
            if (_isDisposed) return;
            lock (_debounceLock)
            {
                if (_isDisposed) return;
                _saveDebounceTimer?.Dispose();
                _saveDebounceTimer = new System.Threading.Timer(_ => 
                {
                    if (_isDisposed) return;
                    // NEVER hold _debounceLock while calling SaveQueueInternal() (prevents AB-BA deadlock with _processingLock/_queueLock)
                    SaveQueueInternal();
                }, null, 500, System.Threading.Timeout.Infinite);
            }
        }

        public void SaveQueueSynchronously()
        {
            lock (_debounceLock)
            {
                _saveDebounceTimer?.Dispose();
                _saveDebounceTimer = null;
            }
            SaveQueueInternal();
        }

        private void SaveQueueInternal()
        {
            try
            {
                var options = new System.Text.Json.JsonSerializerOptions 
                { 
                    WriteIndented = true
                };
                
                List<DownloadRequest> historySnapshot;
                lock (_historyLock)
                {
                    historySnapshot = _history.ToList();
                }

                List<DownloadRequest> activeSnapshot;
                lock (_processingLock)
                {
                    activeSnapshot = _activeDownloads.ToList();
                }

                List<DownloadRequest> queueSnapshot;
                lock (_queueLock)
                {
                    queueSnapshot = _queue.ToList();
                }

                var data = new QueueDataDto
                {
                    Queue = queueSnapshot.Select(ToDto).ToList(),
                    History = historySnapshot.Select(ToDto).ToList(),
                    Active = activeSnapshot.Select(ToDto).ToList(),
                    Current = activeSnapshot.FirstOrDefault() != null ? ToDto(activeSnapshot.First()) : null
                };
                
                string json = System.Text.Json.JsonSerializer.Serialize(data, options);
                string path = Path.Combine(_downloadBaseDir, "queue_v2.json");
                lock (_saveFileLock)
                {
                    string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, path, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                LogService.Error("Download", $"Error saving queue: {ex.Message}");
            }
        }

        private void LoadQueue()
        {
            try
            {
                string path = Path.Combine(_downloadBaseDir, "queue_v2.json");
                if (!File.Exists(path)) return;

                string json = File.ReadAllText(path);
                var options = new System.Text.Json.JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true
                };
                
                var data = System.Text.Json.JsonSerializer.Deserialize<QueueDataDto>(json, options);
                
                if (data != null)
                {
                    if (data.Active != null && data.Active.Count > 0)
                    {
                        lock (_queueLock)
                        {
                            foreach (var dto in data.Active)
                            {
                                if (dto.Status == "Downloading" || dto.Status == "Queued")
                                {
                                    var req = FromDto(dto);
                                    if (req == null) continue;
                                    req.Status = "Queued";
                                    if (!_queue.Any(x => x.Chapter.Url == req.Chapter.Url && x.Manga.Source == req.Manga.Source))
                                    {
                                        _queue.Add(req);
                                    }
                                }
                            }
                        }
                    }
                    else if (data.Current != null && (data.Current.Status == "Downloading" || data.Current.Status == "Queued"))
                    {
                        var req = FromDto(data.Current);
                        if (req != null)
                        {
                            req.Status = "Queued";
                            lock (_queueLock)
                            {
                                if (!_queue.Any(x => x.Chapter.Url == req.Chapter.Url && x.Manga.Source == req.Manga.Source))
                                {
                                    _queue.Add(req);
                                }
                            }
                        }
                    }
                    
                    if (data.Queue != null)
                    {
                        lock (_queueLock)
                        {
                            foreach (var dto in data.Queue)
                            {
                                var req = FromDto(dto);
                                if (req == null) continue;
                                if (req.Status == "Downloading") req.Status = "Queued";
                                if (!_queue.Any(x => x.Chapter.Url == req.Chapter.Url && x.Manga.Source == req.Manga.Source))
                                {
                                    _queue.Add(req);
                                }
                            }
                        }
                    }

                    if (data.History != null)
                    {
                        lock (_historyLock)
                        {
                            foreach (var dto in data.History)
                            {
                                var req = FromDto(dto);
                                if (req != null)
                                {
                                    _history.Add(req);
                                }
                            }
                        }
                    }
                    
                    // Trigger update
                    QueueChanged?.Invoke(this, new DownloadRequest());
                }
            }
            catch (Exception ex)
            {
                LogService.Error("Download", $"Error loading queue: {ex.Message}");
            }
        }
        
        private class MangaDto
        {
            public long Id { get; set; }
            public long Source { get; set; }
            public string Url { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string? ThumbnailUrl { get; set; }
        }

        private class ChapterDto
        {
            public long Id { get; set; }
            public long MangaId { get; set; }
            public string Url { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public float ChapterNumber { get; set; }
            public bool Read { get; set; }
            public bool Bookmark { get; set; }
            public long LastPageRead { get; set; }
            public bool IsDownloaded { get; set; }
            public string? Scanlator { get; set; }
        }

        private class DownloadRequestDto
        {
            public MangaDto Manga { get; set; } = null!;
            public ChapterDto Chapter { get; set; } = null!;
            public int Progress { get; set; }
            public string Status { get; set; } = "Queued";
            public int RetryCount { get; set; }
        }

        private class QueueDataDto
        {
            public List<DownloadRequestDto>? Queue { get; set; }
            public List<DownloadRequestDto>? History { get; set; }
            public List<DownloadRequestDto>? Active { get; set; }
            public DownloadRequestDto? Current { get; set; }
        }

        private static DownloadRequestDto ToDto(DownloadRequest req)
        {
            if (req == null) return null!;
            return new DownloadRequestDto
            {
                Progress = req.Progress,
                Status = req.Status,
                RetryCount = req.RetryCount,
                Manga = new MangaDto
                {
                    Id = req.Manga.Id,
                    Source = req.Manga.Source,
                    Url = req.Manga.Url,
                    Title = req.Manga.Title,
                    ThumbnailUrl = req.Manga.ThumbnailUrl
                },
                Chapter = new ChapterDto
                {
                    Id = req.Chapter.Id,
                    MangaId = req.Chapter.MangaId,
                    Url = req.Chapter.Url,
                    Name = req.Chapter.Name,
                    ChapterNumber = req.Chapter.ChapterNumber,
                    Read = req.Chapter.Read,
                    Bookmark = req.Chapter.Bookmark,
                    LastPageRead = req.Chapter.LastPageRead,
                    IsDownloaded = req.Chapter.IsDownloaded,
                    Scanlator = req.Chapter.Scanlator
                }
            };
        }

        private static DownloadRequest? FromDto(DownloadRequestDto? dto)
        {
            if (dto == null || dto.Manga == null || dto.Chapter == null) return null;
            var manga = new Manga
            {
                Id = dto.Manga.Id,
                Source = dto.Manga.Source,
                Url = dto.Manga.Url,
                Title = dto.Manga.Title,
                ThumbnailUrl = dto.Manga.ThumbnailUrl
            };
            var chapter = new Chapter
            {
                Id = dto.Chapter.Id,
                MangaId = dto.Chapter.MangaId,
                Url = dto.Chapter.Url,
                Name = dto.Chapter.Name,
                ChapterNumber = dto.Chapter.ChapterNumber,
                Read = dto.Chapter.Read,
                Bookmark = dto.Chapter.Bookmark,
                LastPageRead = dto.Chapter.LastPageRead,
                IsDownloaded = dto.Chapter.IsDownloaded,
                Scanlator = dto.Chapter.Scanlator,
                Manga = manga
            };
            manga.Chapters.Add(chapter);
            
            return new DownloadRequest
            {
                Manga = manga,
                Chapter = chapter,
                Progress = dto.Progress,
                Status = dto.Status,
                RetryCount = dto.RetryCount,
                CancellationTokenSource = new CancellationTokenSource()
            };
        }

        public void QueueDownload(Manga manga, Chapter chapter)
        {
            if (_isDisposed) return;
            if (DownloadPathService.IsChapterDownloaded(manga, chapter))
                return;

            DownloadRequest? addedRequest = null;
            lock (_processingLock)
            {
                if (_activeDownloads.Any(x => x.Chapter.Url == chapter.Url && x.Manga.Source == manga.Source))
                    return;

                lock (_queueLock)
                {
                    if (_queue.Any(x => x.Chapter.Url == chapter.Url && x.Manga.Source == manga.Source))
                        return;

                    addedRequest = new DownloadRequest
                    {
                        Manga = manga,
                        Chapter = chapter,
                        Status = "Queued"
                    };

                    _queue.Add(addedRequest);
                }
            }

            // Invoke SaveQueue & events strictly OUTSIDE locks to eliminate AB-BA deadlocks
            if (addedRequest != null)
            {
                SaveQueue();
                QueueChanged?.Invoke(this, addedRequest);
                ProcessQueue();
            }
        }

        private readonly object _processingLock = new();

        public void ProcessQueue()
        {
            if (_isDisposed) return;
            var requestsToStart = new List<DownloadRequest>();

            lock (_processingLock)
            {
                if (_isPaused || _isDisposed) return;

                var (maxChapters, _) = GetConcurrencyLimits();
                lock (_queueLock)
                {
                    while (_activeDownloads.Count < maxChapters)
                    {
                        // Find first non-cancelled, non-paused item in queue
                        var request = _queue.FirstOrDefault(x => x.Status != "Paused" && !x.CancellationTokenSource.IsCancellationRequested);
                        if (request == null) break;

                        _queue.Remove(request);
                        request.Status = "Downloading";
                        _activeDownloads.Add(request);
                        requestsToStart.Add(request);
                    }
                }
            }

            if (requestsToStart.Count > 0)
            {
                SaveQueue(); // Save (Dequeued) Update UI state
                IsDownloadingChanged?.Invoke(this, true);

                foreach (var requestToStart in requestsToStart)
                {
                    StatusChanged?.Invoke(this, requestToStart);

                    // Run async in background (fire and forget from void method perspective)
                    _ = ExecuteDownloadAsync(requestToStart);
                }
            }
        }

        private async Task ExecuteDownloadAsync(DownloadRequest request)
        {
            int maxRetries = 3;
            bool success = false;
            
            try
            {
                while (!success && !request.CancellationTokenSource.IsCancellationRequested)
                {
                    try
                    {
                        await DownloadChapterAsync(request);
                        success = true; // Exit loop
                    }
                    catch (Exception ex)
                    {
                        if (request.RetryCount < maxRetries)
                        {
                             request.RetryCount++;
                             request.Status = $"Retrying ({request.RetryCount}/{maxRetries})...";
                             StatusChanged?.Invoke(this, request);
                             
                             LogService.Warning("Download", $"Retry {request.RetryCount} for {request.Chapter.Name}");
                             await Task.Delay(2000 * request.RetryCount, request.CancellationTokenSource.Token); 
                        }
                        else
                        {
                            // Final failure
                            request.Status = "Error";
                            LogService.Error("Download", $"Max retries exceeded: {ex.Message}");
                            StatusChanged?.Invoke(this, request);
                            break; // Exit loop to finally
                        }
                    }
                }
            }
            finally
            {
                bool reQueued = false;
                bool stillDownloading;

                // Remove from _activeDownloads FIRST before adding to _history/_queue so AllDownloads never duplicates
                lock (_processingLock)
                {
                    _activeDownloads.Remove(request);
                    stillDownloading = _activeDownloads.Count > 0;
                }

                // Completion Logic (Cancelled items are dropped cleanly instead of sticking in _history as "Queued")
                if (request.Status == "Completed" || request.Status == "Error")
                {
                    lock (_historyLock)
                    {
                        _history.Add(request);
                    }
                }
                else if (request.Status == "Paused")
                {
                    var oldCts = request.CancellationTokenSource;
                    if (!_isDisposed)
                    {
                        request.CancellationTokenSource = new CancellationTokenSource();
                    }
                    try { oldCts.Dispose(); } catch { }
                    lock (_queueLock)
                    {
                        _queue.Add(request);
                    }
                    reQueued = true;
                }
                else if (request.Status == "Cancelled")
                {
                    try { request.CancellationTokenSource.Dispose(); } catch { }
                }

                if (reQueued)
                {
                    QueueChanged?.Invoke(this, request);
                }

                IsDownloadingChanged?.Invoke(this, stillDownloading);
                SaveQueue();

                // Trigger next item(s)
                ProcessQueue();
            }
        }

        private async Task DownloadChapterAsync(DownloadRequest request)
        {
            try 
            {
                LogService.Info("Download", $"Starting: {request.Chapter.Name}");
                LogService.Debug("Download", $"Source ID: {request.Manga.Source}, URL: {request.Chapter.Url}");
                
                var source = _sourceManager.GetSource(request.Manga.Source);
                if (source == null)
                {
                    LogService.Error("Download", $"Source not found for ID {request.Manga.Source}");
                    throw new Exception("Source not found");
                }
                LogService.Debug("Download", $"Using source: {source.Name}");

                // 1. Get Pages
                var pages = await WithTimeout(
                    source.GetPageListAsync(request.Chapter.Url),
                    PageListTimeout,
                    request.CancellationTokenSource.Token,
                    $"Fetching page list timed out after {PageListTimeout.TotalSeconds:0} seconds.");
                LogService.Info("Download", $"Fetched {pages?.Count ?? 0} pages");
                
                if (pages == null || pages.Count == 0)
                {
                    LogService.Error("Download", "No pages found in chapter!");
                    throw new Exception("No pages found in chapter");
                }
                
                // Log first few page URLs for debugging
                if (pages.Count > 0)
                {
                    LogService.Debug("Download", $"First page: {pages[0].Substring(0, Math.Min(60, pages[0].Length))}...");
                }
                
                // 2. Prepare directories. Write to a temp folder first, then promote to final only after validation.
                var chapterDir = DownloadPathService.GetChapterDirectory(request.Manga, request.Chapter);
                var tempChapterDir = DownloadPathService.GetChapterTempDirectory(request.Manga, request.Chapter);
                LogService.Debug("Download", $"Temp dir: {tempChapterDir}");
                LogService.Debug("Download", $"Final dir: {chapterDir}");

                if (DownloadPathService.IsChapterDownloaded(request.Manga, request.Chapter))
                {
                    request.Progress = 100;
                    lock (_processingLock)
                    {
                        request.Status = "Completed";
                    }
                    request.Chapter.IsDownloaded = true;
                    StatusChanged?.Invoke(this, request);
                    await _libraryService.UpdateChapterDownloadStatusByUrlAsync(request.Chapter.Url, true, request.Manga?.Id ?? 0);
                    return;
                }

                Directory.CreateDirectory(tempChapterDir);
                foreach (var partFile in Directory.GetFiles(tempChapterDir, "*.part"))
                {
                    TryDeleteFile(partFile);
                }

                // 3. Download Images (Parallel with limit)
                int total = pages.Count;
                int completed = 0;
                int failedCount = 0;
                
                // Use Shared Optimized Client with DoH & Connection Pooling
                var client = GetOrCreateSharedHttpClient();
                
                // Construct full Referer URL safely from source BaseUrl
                string refererUrl = source.BaseUrl ?? string.Empty;
                try
                {
                    if (Uri.TryCreate(request.Chapter.Url, UriKind.Absolute, out var absChapUri))
                    {
                        refererUrl = absChapUri.AbsoluteUri;
                    }
                    else if (Uri.TryCreate(source.BaseUrl, UriKind.Absolute, out var baseUri) &&
                             Uri.TryCreate(baseUri, request.Chapter.Url, out var joinedUri))
                    {
                        refererUrl = joinedUri.AbsoluteUri;
                    }
                }
                catch (Exception ex)
                {
                    LogService.Warning("Download", $"Referer initialization warning: {ex.Message}");
                }
                
                // Use semaphore to limit concurrent page downloads based on chosen speed preset
                var (_, maxPagesPerChapter) = GetConcurrencyLimits();
                using var semaphore = new System.Threading.SemaphoreSlim(maxPagesPerChapter);
                var downloadTasks = new List<Task>();
                
                LogService.Info("Download", $"Downloading {total} pages (concurrency: {maxPagesPerChapter})...");

                for (int i = 0; i < total; i++)
                {
                    int index = i; // Capture for closure
                    var pageUrl = pages[i];
                    
                    downloadTasks.Add(Task.Run(async () =>
                    {
                        var acquiredSemaphore = false;
                        try
                        {
                            await semaphore.WaitAsync(request.CancellationTokenSource.Token);
                            acquiredSemaphore = true;

                            if (request.CancellationTokenSource.IsCancellationRequested) return;
                            
                            // Parse Headers (case-insensitive so |referer= and |aeskey= work reliably)
                            string requestUrl = pageUrl;
                            var customHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            if (pageUrl.Contains("|"))
                            {
                                var parts = pageUrl.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                                requestUrl = parts[0];
                                for (int j = 1; j < parts.Length; j++)
                                {
                                    var pair = parts[j].Split(new[] { '=' }, 2);
                                    if (pair.Length == 2) customHeaders[pair[0].Trim()] = pair[1].Trim();
                                }
                            }

                            if (!Yomic.Core.Helpers.UrlSecurityHelper.IsSafePublicHttpUrl(requestUrl, out var safePageUri) || safePageUri == null)
                            {
                                LogService.Warning("Download", $"Blocked unsafe page URL: {requestUrl}");
                                return;
                            }

                            // Parse extension from URI AbsolutePath (never raw URL with ?v=1.2 query dots!)
                            string ext = Path.GetExtension(safePageUri.AbsolutePath);
                            if (string.IsNullOrEmpty(ext) ||
                                !(ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                  ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                  ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                                  ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
                                  ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
                                  ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                                  ext.Equals(".avif", StringComparison.OrdinalIgnoreCase)))
                            {
                                ext = ".jpg";
                            }
                            var filePath = Path.Combine(tempChapterDir, $"{index:D4}{ext}");
                            var tempFilePath = filePath + ".part";

                            // Atomic Check: If final file exists, it's done. 
                            if (!File.Exists(filePath))
                            {
                                // Per-page retry loop (max 5 attempts with backoff)
                                const int maxPageRetries = 5;
                                int pageRetry = 0;
                                bool pageSuccess = false;
                                
                                while (!pageSuccess && pageRetry < maxPageRetries)
                                {
                                    try
                                    {
                                        if (request.CancellationTokenSource.IsCancellationRequested) return;

                                        using var pageCts = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationTokenSource.Token);
                                        pageCts.CancelAfter(PageDownloadTimeout);

                                         try
                                         {
                                             using var response = await SendPageRequestWithSafeRedirectsAsync(
                                                 client, requestUrl, customHeaders, refererUrl, pageCts.Token);
                                            if ((int)response.StatusCode == 429 || (int)response.StatusCode == 503)
                                            {
                                                var retryAfterDelay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(2500 * (pageRetry + 1));
                                                var clampedDelay = TimeSpan.FromMilliseconds(Math.Clamp(retryAfterDelay.TotalMilliseconds, 1500, 10000));
                                                await Task.Delay(clampedDelay, request.CancellationTokenSource.Token);
                                            }
                                            response.EnsureSuccessStatusCode();
                                            var data = await Yomic.Core.Helpers.UrlSecurityHelper.ReadContentBoundedAsync(
                                                response.Content, 50L * 1024 * 1024, pageCts.Token);

                                            if (data.Length == 0)
                                            {
                                                throw new IOException("Downloaded page is empty.");
                                            }

                                            if (customHeaders.ContainsKey("AesKey") && customHeaders.ContainsKey("AesIv"))
                                            {
                                                data = DecryptAes256Cbc(data, customHeaders["AesKey"], customHeaders["AesIv"]);
                                            }

                                            if (!IsValidDownloadedImagePayload(data))
                                            {
                                                throw new InvalidDataException("Downloaded page payload is HTML/XML (possible Cloudflare/WAF block) instead of an image.");
                                            }

                                            // Write to .part file first
                                            await File.WriteAllBytesAsync(tempFilePath, data, pageCts.Token);

                                            // Atomic Rename (Move)
                                            File.Move(tempFilePath, filePath, overwrite: true);
                                            pageSuccess = true;
                                        }
                                        catch (OperationCanceledException) when (!request.CancellationTokenSource.IsCancellationRequested)
                                        {
                                            throw new TimeoutException($"Page timed out after {PageDownloadTimeout.TotalSeconds:0} seconds.");
                                        }
                                    }
                                    catch (Exception retryEx)
                                    {
                                        TryDeleteFile(tempFilePath);
                                        pageRetry++;
                                        if (pageRetry < maxPageRetries)
                                        {
                                            LogService.Debug("Download", $"Page {index} retry {pageRetry}/{maxPageRetries}: {retryEx.Message}");
                                            try
                                            {
                                                await Task.Delay(1500 * pageRetry, request.CancellationTokenSource.Token); // Exponential backoff
                                            }
                                            catch (OperationCanceledException) when (request.CancellationTokenSource.IsCancellationRequested)
                                            {
                                                return;
                                            }
                                        }
                                        else
                                        {
                                            throw; // Re-throw to outer catch
                                        }
                                    }
                                }
                            }

                            // Update Progress (thread-safe increment)
                            int current = Interlocked.Increment(ref completed);
                            request.Progress = (int)((double)current / total * 100);
                            ProgressChanged?.Invoke(this, request);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref failedCount);
                            LogService.Warning("Download", $"Page {index} failed after retries: {ex.Message}");
                        }
                        finally
                        {
                            if (acquiredSemaphore)
                            {
                                semaphore.Release();
                            }
                        }
                    }));
                }

                await Task.WhenAll(downloadTasks);
                
                if (request.CancellationTokenSource.IsCancellationRequested)
                {
                    if (request.Status != "Paused") request.Status = "Cancelled";
                    // If Paused, it keeps "Paused" status set in Pause()
                }

                if (request.Status == "Paused")
                {
                    return;
                }

                if (request.Status == "Cancelled")
                {
                    try { if (Directory.Exists(tempChapterDir)) Directory.Delete(tempChapterDir, true); } catch { }
                    return;
                }

                var downloadedFiles = DownloadPathService.GetReadableFiles(tempChapterDir, includeTempDirectory: true);
                int minRequiredPages = total <= 4 ? total : Math.Max(1, (int)Math.Ceiling(total * 0.9));

                if (downloadedFiles.Count == 0 || downloadedFiles.Count < minRequiredPages)
                {
                    throw new Exception($"Only {downloadedFiles.Count}/{total} pages downloaded (failed={failedCount}). Retrying chapter...");
                }

                // 4. Mark Complete (if all pages or >80% pages downloaded)
                if (request.Status != "Cancelled" && request.Status != "Paused")
                {
                    if (downloadedFiles.Count < total)
                    {
                        LogService.Warning("Download", $"Completed chapter with {downloadedFiles.Count}/{total} pages ({total - downloadedFiles.Count} pages skipped due to network glitch). Promoting directory.");
                    }

                    if (Directory.Exists(chapterDir))
                    {
                        try { Directory.Delete(chapterDir, true); } catch { }
                    }

                    bool saveAsCbz = _settingsService?.SaveDownloadsAsCbz == true;
                    if (saveAsCbz)
                    {
                        var cbzPath = DownloadPathService.GetChapterCbzPath(request.Manga, request.Chapter);
                        var tempCbzPath = $"{cbzPath}.{Guid.NewGuid():N}.tmp";
                        try
                        {
                            // Package to .tmp first and atomically move into place so a mid-zip crash never leaves a corrupt .cbz
                            ZipFile.CreateFromDirectory(tempChapterDir, tempCbzPath, CompressionLevel.Optimal, includeBaseDirectory: false);
                            File.Move(tempCbzPath, cbzPath, overwrite: true);
                            try { Directory.Delete(tempChapterDir, true); } catch { }
                        }
                        catch (Exception cbzEx)
                        {
                            LogService.Warning("Download", $"CBZ packaging failed, falling back to folder: {cbzEx.Message}");
                            try { if (File.Exists(tempCbzPath)) File.Delete(tempCbzPath); } catch { }
                            if (Directory.Exists(tempChapterDir))
                            {
                                Directory.Move(tempChapterDir, chapterDir);
                            }
                        }
                    }
                    else
                    {
                        try
                        {
                            Directory.Move(tempChapterDir, chapterDir);
                        }
                        catch
                        {
                            // Fallback move
                            if (Directory.Exists(tempChapterDir))
                            {
                                Directory.Move(tempChapterDir, chapterDir);
                            }
                        }
                    }

                    lock (_processingLock)
                    {
                        request.Status = "Completed";
                    }
                    request.Chapter.IsDownloaded = true;
                    StatusChanged?.Invoke(this, request);

                    // 5. Update DB (scope by MangaId when available to avoid relative chapter URL collisions)
                    await _libraryService.UpdateChapterDownloadStatusByUrlAsync(request.Chapter.Url, true, request.Manga?.Id ?? 0);
                    
                    LogService.Success("Download", $"Completed: {request.Chapter.Name}");
                }
            }
            catch (Exception ex)
            {
                 // If cancelled/paused, we might catch TaskCanceledException here or generic Exception
                 if (request.Status == "Paused")
                 {
                     // Expected interrupt — preserve .tmp for resume
                 }
                 else if (request.Status == "Cancelled")
                 {
                     try
                     {
                         var tempDir = DownloadPathService.GetChapterTempDirectory(request.Manga, request.Chapter);
                         if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                     }
                     catch { }
                 }
                 else
                 {
                   LogService.Error("Download", $"Chapter download failed", ex);
                   throw;
                 }
            }
        }

        internal static bool IsValidDownloadedImagePayload(byte[]? data)
        {
            return Yomic.Core.Helpers.UrlSecurityHelper.IsValidImagePayload(data);
        }

        internal static async Task<HttpResponseMessage> SendPageRequestWithSafeRedirectsAsync(
            HttpClient client,
            string initialUrl,
            Dictionary<string, string> customHeaders,
            string refererUrl,
            CancellationToken ct)
        {
            const int maxRedirects = 5;
            string currentUrl = initialUrl;

            for (int hop = 0; hop <= maxRedirects; hop++)
            {
                if (!Yomic.Core.Helpers.UrlSecurityHelper.IsSafePublicHttpUrl(currentUrl, out var targetUri) || targetUri == null)
                {
                    throw new HttpRequestException($"Blocked unsafe download URL: {currentUrl}");
                }

                using var req = new HttpRequestMessage(HttpMethod.Get, targetUri)
                {
                    Version = System.Net.HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };

                if (Yomic.Core.Helpers.UrlSecurityHelper.IsHostOrSubdomain(currentUrl, "mangamillion.shueisha.co.jp"))
                {
                    req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
                    req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
                    req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
                    req.Headers.TryAddWithoutValidation("Origin", "https://mangamillion.shueisha.co.jp");
                }

                if (customHeaders.TryGetValue("Referer", out var customRef))
                {
                    if (customRef != "none" && customRef != "null" &&
                        Uri.TryCreate(customRef, UriKind.Absolute, out var customRefUri))
                    {
                        req.Headers.Referrer = customRefUri;
                    }
                }
                else if (Uri.TryCreate(refererUrl, UriKind.Absolute, out var defaultRefUri))
                {
                    req.Headers.Referrer = defaultRefUri;
                }

                if (customHeaders.TryGetValue("User-Agent", out var customUa))
                {
                    req.Headers.UserAgent.TryParseAdd(customUa);
                }
                else if (customHeaders.TryGetValue("UserAgent", out var customUa2))
                {
                    req.Headers.UserAgent.TryParseAdd(customUa2);
                }
                else
                {
                    req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                }

                // Inject host-scoped Cloudflare cookies & matching User-Agent per hop (prevents cross-origin cookie leaks on 302)
                if (CloudflareBypassService.IsInstanceCreated)
                {
                    var savedCookies = CloudflareBypassService.Instance.SavedCookies;
                    var relevantCookies = savedCookies
                        .Where(c => Yomic.Core.Helpers.UrlSecurityHelper.CookieMatchesRequest(targetUri, c.Domain, c.Secure ?? false))
                        .ToList();

                    if (relevantCookies.Count > 0)
                    {
                        var cookieHeader = string.Join("; ", relevantCookies.Select(c => $"{c.Name}={c.Value}"));
                        req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                    }

                    if (!string.IsNullOrEmpty(CloudflareBypassService.Instance.BypassUserAgent) &&
                        (relevantCookies.Count > 0 || currentUrl.Contains("nhentai", StringComparison.OrdinalIgnoreCase)))
                    {
                        req.Headers.Remove("User-Agent");
                        req.Headers.TryAddWithoutValidation("User-Agent", CloudflareBypassService.Instance.BypassUserAgent);
                    }
                }

                var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                int status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location != null)
                {
                    var location = response.Headers.Location;
                    var nextUri = location.IsAbsoluteUri ? location : new Uri(targetUri, location);
                    response.Dispose();
                    if (hop == maxRedirects)
                    {
                        throw new HttpRequestException($"Too many redirects while downloading page: {initialUrl}");
                    }
                    currentUrl = nextUri.AbsoluteUri;
                    continue;
                }

                return response;
            }

            throw new HttpRequestException($"Failed to download page after redirects: {initialUrl}");
        }

        private static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout, CancellationToken cancellationToken, string timeoutMessage)
        {
            // Prevent unobserved task exceptions on the original task if it faults after timeout
            _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeoutTask = Task.Delay(timeout, timeoutCts.Token);
            var completedTask = await Task.WhenAny(task, timeoutTask);

            if (completedTask == task)
            {
                timeoutCts.Cancel();
                try
                {
                    await timeoutTask;
                }
                catch
                {
                    // Suppress TaskCanceledException of timeoutTask
                }
                return await task;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw new TimeoutException(timeoutMessage);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }

        public void Pause()
        {
            _isPaused = true;

            // Cancel all active downloads (including items transitioning from Queued -> Downloading)
            List<DownloadRequest> activeToPause;
            lock (_processingLock)
            {
                activeToPause = _activeDownloads
                    .Where(d => d.Status != "Completed" && d.Status != "Cancelled" && d.Status != "Paused")
                    .ToList();
                foreach (var active in activeToPause)
                {
                    active.Status = "Paused";
                    try { active.CancellationTokenSource.Cancel(); } catch (ObjectDisposedException) { }
                }
            }

            foreach (var active in activeToPause)
            {
                StatusChanged?.Invoke(this, active);
            }

            // Update queued items under lock, invoke StatusChanged strictly OUTSIDE lock (prevents AB-BA deadlock)
            var pausedQueued = new List<DownloadRequest>();
            lock (_queueLock)
            {
                foreach (var item in _queue)
                {
                    if (item.Status == "Queued")
                    {
                        item.Status = "Paused";
                        pausedQueued.Add(item);
                    }
                }
            }
            foreach (var item in pausedQueued)
            {
                StatusChanged?.Invoke(this, item);
            }
        }

        public void Resume()
        {
            if (_isDisposed) return;
            _isPaused = false;

            var resumedItems = new List<DownloadRequest>();
            lock (_queueLock)
            {
                foreach (var item in _queue)
                {
                    if (item.Status == "Paused")
                    {
                        item.Status = "Queued";
                        resumedItems.Add(item);
                    }
                }
            }

            foreach (var item in resumedItems)
            {
                StatusChanged?.Invoke(this, item);
            }

            ProcessQueue();
        }

        public void Pause(DownloadRequest request)
        {
            if (request == null) return;

            bool isActive = false;
            bool pausedInQueue = false;
            // Acquire _processingLock -> _queueLock atomically (same lock order as ProcessQueue/QueueDownload)
            // so a request promoted from _queue to _activeDownloads cannot slip through a lock gap.
            lock (_processingLock)
            {
                if (_activeDownloads.Contains(request) &&
                    request.Status != "Completed" &&
                    request.Status != "Cancelled")
                {
                    request.Status = "Paused";
                    try { request.CancellationTokenSource.Cancel(); } catch (ObjectDisposedException) { }
                    isActive = true;
                }
                else
                {
                    lock (_queueLock)
                    {
                        if (_queue.Contains(request))
                        {
                            request.Status = "Paused";
                            pausedInQueue = true;
                        }
                    }
                }
            }

            if (isActive)
            {
                StatusChanged?.Invoke(this, request);
                return;
            }

            if (pausedInQueue)
            {
                StatusChanged?.Invoke(this, request);
                SaveQueue();
            }
        }

        public void Resume(DownloadRequest request)
        {
            if (_isDisposed || request == null) return;

            // Clear global pause lockout so single-item Resume works even after Pause All
            _isPaused = false;

            bool resumedInQueue = false;
            lock (_queueLock)
            {
                if (_queue.Contains(request))
                {
                    request.Status = "Queued";
                    resumedInQueue = true;
                }
            }

            if (resumedInQueue)
            {
                StatusChanged?.Invoke(this, request);
                SaveQueue();
            }

            ProcessQueue();
        }

        public void ClearCompleted()
        {
            lock (_historyLock)
            {
                _history.RemoveAll(x => x.Status == "Completed" || x.Status == "Cancelled" || x.Status == "Error");
            }
            // Notify?
            QueueChanged?.Invoke(this, new DownloadRequest()); // Trigger refresh
            SaveQueue(); // Save
        }

        public void Cancel(DownloadRequest request)
        {
            if (request == null) return;

            bool isActive = false;
            bool removedFromQueue = false;
            // Acquire _processingLock -> _queueLock atomically (matching ProcessQueue lock order)
            lock (_processingLock)
            {
                if (_activeDownloads.Contains(request) && request.Status != "Completed")
                {
                    request.Status = "Cancelled";
                    try { request.CancellationTokenSource.Cancel(); } catch (ObjectDisposedException) { }
                    isActive = true;
                }
                else
                {
                    lock (_queueLock)
                    {
                        if (_queue.Contains(request))
                        {
                            _queue.Remove(request);
                            removedFromQueue = true;
                        }
                    }
                }
            }

            if (isActive)
            {
                StatusChanged?.Invoke(this, request); // Update UI
                return;
            }

            if (removedFromQueue)
            {
                request.Status = "Cancelled";
                try { request.CancellationTokenSource.Cancel(); } catch (ObjectDisposedException) { }
                QueueChanged?.Invoke(this, request);
                SaveQueue();
                return;
            }
            
            // Try to remove from history
            bool removedFromHistory = false;
            lock (_historyLock)
            {
                if (_history.Contains(request))
                {
                     _history.Remove(request);
                     removedFromHistory = true;
                }
            }
            
            if (removedFromHistory)
            {
                QueueChanged?.Invoke(this, request);
                SaveQueue();
            }
        }

        /// <summary>
        /// Waits until all active download workers have exited (up to <paramref name="timeout"/>),
        /// used before exclusive SQLite maintenance operations such as backup restore.
        /// </summary>
        public async Task<bool> WaitForActiveDownloadsToStopAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                bool anyActive;
                lock (_processingLock)
                {
                    anyActive = _activeDownloads.Count > 0;
                }
                if (!anyActive) return true;
                await Task.Delay(25).ConfigureAwait(false);
            }
            lock (_processingLock)
            {
                return _activeDownloads.Count == 0;
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _isPaused = true;

            try { _networkService.ConnectionReset -= _onConnectionReset; } catch { }
            if (_settingsService != null)
            {
                try { _settingsService.DownloadSpeedModeChanged -= _onDownloadSpeedModeChanged; } catch { }
            }

            lock (_debounceLock)
            {
                try { _saveDebounceTimer?.Dispose(); } catch { }
                _saveDebounceTimer = null;
            }

            lock (_processingLock)
            {
                foreach (var active in _activeDownloads)
                {
                    // Mark active downloads as Paused BEFORE cancelling so DownloadChapterAsync preserves .tmp progress for resume
                    if (active.Status != "Completed" && active.Status != "Cancelled")
                    {
                        active.Status = "Paused";
                    }
                    try { active.CancellationTokenSource.Cancel(); } catch (ObjectDisposedException) { }
                }
            }

            // Detach shared client without disposing synchronously if active downloads are still unwinding
            bool hasActive;
            lock (_processingLock) { hasActive = _activeDownloads.Count > 0; }
            lock (_httpClientLock)
            {
                if (!hasActive)
                {
                    try { _sharedDownloadClient?.Dispose(); } catch { }
                }
                _sharedDownloadClient = null;
            }
        }

        private static byte[] DecryptAes256Cbc(byte[] cipherText, string keyHex, string ivHex)
        {
            try
            {
                byte[] key = Convert.FromHexString(keyHex);
                byte[] iv = Convert.FromHexString(ivHex);

                using var aes = System.Security.Cryptography.Aes.Create();
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;

                using var decryptor = aes.CreateDecryptor();
                return decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DownloadService] AES Decryption Error: {ex.Message}");
                return Array.Empty<byte>();
            }
        }
    }
}
