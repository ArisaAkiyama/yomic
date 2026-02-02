using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Yomic.Core.Models;
using Yomic.ViewModels; // For notification presumably, or just independent

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

    public class DownloadService
    {
        private readonly SourceManager _sourceManager;
        private readonly LibraryService _libraryService;
        private readonly NetworkService _networkService;
        private readonly string _downloadBaseDir;

        // Queue management
        private readonly ConcurrentQueue<DownloadRequest> _queue = new();
        private readonly List<DownloadRequest> _history = new(); // Completed or Error
        private readonly object _historyLock = new(); // Thread lock
        private DownloadRequest? _currentDownload;
        private bool _isProcessing;
        private bool _isPaused;

        // Events
        public event EventHandler<DownloadRequest>? QueueChanged;
        public event EventHandler<DownloadRequest>? ProgressChanged;
        public event EventHandler<DownloadRequest>? StatusChanged;
        public event EventHandler<bool>? IsDownloadingChanged;

        public bool IsDownloading => _currentDownload != null;

        public IEnumerable<DownloadRequest> AllDownloads
        {
            get
            {
                lock (_historyLock)
                {
                    // Return a snapshot to prevent iteration errors
                    var historySnapshot = _history.ToList();
                    return historySnapshot
                        .Concat(_currentDownload != null ? new[] { _currentDownload } : Array.Empty<DownloadRequest>())
                        .Concat(_queue);
                }
            }
        }

        public DownloadService(SourceManager sourceManager, LibraryService libraryService, NetworkService networkService)
        {
            _sourceManager = sourceManager;
            _libraryService = libraryService;
            _networkService = networkService;
            
            // Base directory: AppData/Yomic/Downloads
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _downloadBaseDir = Path.Combine(appData, "Yomic", "Downloads");
            if (!Directory.Exists(_downloadBaseDir))
                Directory.CreateDirectory(_downloadBaseDir);
            
            LoadQueue();
        }

        private void SaveQueue()
        {
            try
            {
                var options = new System.Text.Json.JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
                };
                
                // Save both Queue and History (only active history, maybe?)
                // Actually, let's just save the Queue + Active Download
                // History is less critical, but good for UX. Let's save all.
                
                List<DownloadRequest> historySnapshot;
                lock (_historyLock)
                {
                    historySnapshot = _history.ToList();
                }

                var data = new 
                {
                    Queue = _queue.ToList(),
                    History = historySnapshot,
                    Current = _currentDownload
                };
                
                string json = System.Text.Json.JsonSerializer.Serialize(data, options);
                string path = Path.Combine(_downloadBaseDir, "queue_v2.json");
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DownloadService] Error saving queue: {ex.Message}");
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
                    PropertyNameCaseInsensitive = true,
                    ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles 
                };
                
                var data = System.Text.Json.JsonSerializer.Deserialize<QueueData>(json, options);
                
                if (data != null)
                {
                    if (data.Current != null && data.Current.Status == "Downloading")
                    {
                        // Reset status to queued if it was interrupted
                        data.Current.Status = "Queued"; 
                        _queue.Enqueue(data.Current);
                    }
                    
                    if (data.Queue != null)
                    {
                        foreach(var item in data.Queue)
                        {
                            item.CancellationTokenSource = new CancellationTokenSource(); // Recreate CTS
                            if (item.Status == "Downloading") item.Status = "Queued"; // Reset interrupted
                            _queue.Enqueue(item);
                        }
                    }

                    if (data.History != null)
                    {
                        lock (_historyLock)
                        {
                            _history.AddRange(data.History);
                        }
                    }
                    
                    // Trigger update
                    QueueChanged?.Invoke(this, new DownloadRequest());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DownloadService] Error loading queue: {ex.Message}");
            }
        }
        
        private class QueueData
        {
            public List<DownloadRequest>? Queue { get; set; }
            public List<DownloadRequest>? History { get; set; }
            public DownloadRequest? Current { get; set; }
        }

        public void QueueDownload(Manga manga, Chapter chapter)
        {
            // Check if already in queue or downloading (use Url for identification, not Id)
            if (_queue.Any(x => x.Chapter.Url == chapter.Url) || _currentDownload?.Chapter.Url == chapter.Url)
                return;

            var request = new DownloadRequest
            {
                Manga = manga,
                Chapter = chapter,
                Status = "Queued"
            };

            _queue.Enqueue(request);
            SaveQueue(); // Save
            QueueChanged?.Invoke(this, request);

            ProcessQueue();
        }

        private readonly object _processingLock = new();

        private void ProcessQueue()
        {
            DownloadRequest? requestToStart = null;

            lock (_processingLock)
            {
                if (_isProcessing || _isPaused) return;

                if (_queue.TryDequeue(out var request))
                {
                    _isProcessing = true; // CLAIMED
                    _currentDownload = request;
                    requestToStart = request;
                }
            }

            if (requestToStart != null)
            {
                SaveQueue(); // Save (Dequeued) Update UI state
                IsDownloadingChanged?.Invoke(this, true); 
                
                requestToStart.Status = "Downloading";
                StatusChanged?.Invoke(this, requestToStart);

                // Run async in background (fire and forget from void method perspective)
                _ = ExecuteDownloadAsync(requestToStart);
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
                             
                             System.Diagnostics.Debug.WriteLine($"[DownloadService] Retry {request.RetryCount} for {request.Chapter.Name}");
                             await Task.Delay(2000 * request.RetryCount); 
                        }
                        else
                        {
                            // Final failure
                            request.Status = "Error";
                            System.Diagnostics.Debug.WriteLine($"[DownloadService] Error: {ex.Message}");
                            StatusChanged?.Invoke(this, request);
                            break; // Exit loop to finally
                        }
                    }
                }
            }
            finally
            {
                // Completion Logic
                if (request.Status == "Completed" || request.Status == "Error" || request.Status == "Cancelled")
                {
                    lock (_historyLock)
                    {
                        _history.Add(request);
                    }
                }
                else if (request.Status == "Paused")
                {
                    // Re-enqueue for later
                    request.CancellationTokenSource = new CancellationTokenSource(); // Reset token for next run
                    _queue.Enqueue(request);
                    QueueChanged?.Invoke(this, request);
                }
                
                // Release Lock and Continue
                lock (_processingLock)
                {
                    _currentDownload = null;
                    _isProcessing = false; // RELEASED
                }

                IsDownloadingChanged?.Invoke(this, false); 
                SaveQueue(); // Save (History updated)
                
                // Trigger next item
                ProcessQueue();
            }
        }

        private async Task DownloadChapterAsync(DownloadRequest request)
        {
            try 
            {
                Console.WriteLine($"[DownloadService] Starting download for: {request.Chapter.Name}");
                Console.WriteLine($"[DownloadService] Manga Source ID: {request.Manga.Source}");
                Console.WriteLine($"[DownloadService] Chapter URL: {request.Chapter.Url}");
                
                var source = _sourceManager.GetSource(request.Manga.Source);
                if (source == null)
                {
                    Console.WriteLine($"[DownloadService] ERROR: Source not found for ID {request.Manga.Source}");
                    throw new Exception("Source not found");
                }
                Console.WriteLine($"[DownloadService] Using source: {source.Name}");

                // 1. Get Pages
                Console.WriteLine($"[DownloadService] Fetching page list...");
                var pages = await source.GetPageListAsync(request.Chapter.Url);
                Console.WriteLine($"[DownloadService] GetPageListAsync returned: {pages?.Count ?? 0} pages");
                
                if (pages == null || pages.Count == 0)
                {
                    Console.WriteLine($"[DownloadService] ERROR: No pages found!");
                    throw new Exception("No pages found in chapter");
                }
                
                // Log first 3 page URLs
                for (int p = 0; p < Math.Min(3, pages.Count); p++)
                {
                    Console.WriteLine($"[DownloadService] Page {p}: {pages[p].Substring(0, Math.Min(80, pages[p].Length))}...");
                }
                
                // 2. Prepare Directory
                // Sanitize paths
                var safeMangaTitle = string.Join("_", request.Manga.Title.Split(Path.GetInvalidFileNameChars()));
                var safeChapterName = string.Join("_", request.Chapter.Name.Split(Path.GetInvalidFileNameChars()));
                
                var chapterDir = Path.Combine(_downloadBaseDir, request.Manga.Source.ToString(), safeMangaTitle, safeChapterName);
                Console.WriteLine($"[DownloadService] Download dir: {chapterDir}");
                
                if (!Directory.Exists(chapterDir))
                    Directory.CreateDirectory(chapterDir);

                // 3. Download Images (Parallel with limit)
                int total = pages.Count;
                int completed = 0;
                int failedCount = 0;
                
                // Use Optimized Client with DoH
                using var client = _networkService.CreateOptimizedHttpClient();
                
                // Construct full Referer URL from source BaseUrl
                string refererUrl = source.BaseUrl;
                try
                {
                    if (request.Chapter.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        refererUrl = request.Chapter.Url;
                    }
                    else
                    {
                        refererUrl = source.BaseUrl.TrimEnd('/') + request.Chapter.Url;
                    }
                    client.DefaultRequestHeaders.Referrer = new Uri(refererUrl);
                    Console.WriteLine($"[DownloadService] Referer set to: {refererUrl}");
                }
                catch (Exception ex)
                {
                    // Fallback - use source base URL
                    Console.WriteLine($"[DownloadService] Referer error: {ex.Message}, using base URL");
                    client.DefaultRequestHeaders.Referrer = new Uri(source.BaseUrl);
                }
                
                // Use semaphore to limit concurrent downloads (4 at a time for speed)
                var semaphore = new System.Threading.SemaphoreSlim(4);
                var downloadTasks = new List<Task>();
                
                Console.WriteLine($"[DownloadService] Starting parallel download of {total} images...");

                for (int i = 0; i < total; i++)
                {
                    int index = i; // Capture for closure
                    var pageUrl = pages[i];
                    
                    downloadTasks.Add(Task.Run(async () =>
                    {
                        await semaphore.WaitAsync(request.CancellationTokenSource.Token);
                        try
                        {
                            if (request.CancellationTokenSource.IsCancellationRequested) return;
                            
                            // Parse Headers
                            string requestUrl = pageUrl;
                            var customHeaders = new Dictionary<string, string>();
                            if (pageUrl.Contains("|"))
                            {
                                var parts = pageUrl.Split('|', 2);
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

                            var ext = Path.GetExtension(requestUrl).Split('?')[0];
                            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                            var filePath = Path.Combine(chapterDir, $"{index:D3}{ext}");
                            var tempFilePath = filePath + ".part";

                            // Atomic Check: If final file exists, it's done. 
                            if (!File.Exists(filePath))
                            {
                                var req = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                                if (customHeaders.ContainsKey("Referer")) req.Headers.Referrer = new Uri(customHeaders["Referer"]);
                                // refererUrl already constructed above with proper base URL

                                if (customHeaders.ContainsKey("User-Agent")) req.Headers.UserAgent.TryParseAdd(customHeaders["User-Agent"]);
                                else req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

                                using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, request.CancellationTokenSource.Token);
                                response.EnsureSuccessStatusCode();
                                var data = await response.Content.ReadAsByteArrayAsync();
                                
                                // Write to .part file first
                                await File.WriteAllBytesAsync(tempFilePath, data);
                                
                                // Atomic Rename (Move)
                                File.Move(tempFilePath, filePath, overwrite: true);
                            }

                            // Update Progress (thread-safe increment)
                            int current = Interlocked.Increment(ref completed);
                            request.Progress = (int)((double)current / total * 100);
                            ProgressChanged?.Invoke(this, request);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref failedCount);
                            System.Diagnostics.Debug.WriteLine($"[DownloadService] Error downloading page {index}: {ex.Message}");
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }));
                }

                await Task.WhenAll(downloadTasks);
                
                if (request.CancellationTokenSource.IsCancellationRequested)
                {
                    if (request.Status != "Paused") request.Status = "Cancelled";
                    // If Paused, it keeps "Paused" status set in Pause()
                }

                if (failedCount > 0)
                {
                    // Throw to trigger retry in ExecuteDownloadAsync
                    throw new Exception($"{failedCount} pages failed to download. Retrying chapter..."); 
                }

                // 4. Mark Complete
                // Only if NOT Cancelled/Paused
                if (request.Status != "Cancelled" && request.Status != "Paused")
                {
                    request.Status = "Completed";
                    request.Chapter.IsDownloaded = true;
                    StatusChanged?.Invoke(this, request);

                    // 5. Update DB (use URL since ID might be 0 for dynamic chapters)
                    await _libraryService.UpdateChapterDownloadStatusByUrlAsync(request.Chapter.Url, true);
                    
                    System.Diagnostics.Debug.WriteLine($"[DownloadService] Downloaded {request.Chapter.Name}");
                }
            }
            catch (Exception ex)
            {
                 // If cancelled/paused, we might catch TaskCanceledException here or generic Exception
                 if (request.Status == "Paused" || request.Status == "Cancelled") 
                 {
                     // Expected interrupt
                 }
                 else
                 {
                    System.Diagnostics.Debug.WriteLine($"[DownloadService] Failed: {ex}");
                    throw;
                 }
            }
        }

        public void Pause()
        {
            _isPaused = true;
            
            // Cancel current download if active
            lock (_processingLock)
            {
                if (_currentDownload != null && _currentDownload.Status == "Downloading")
                {
                    _currentDownload.Status = "Paused"; // Mark as Paused so excution logic knows it's not a cancellation
                    _currentDownload.CancellationTokenSource.Cancel();
                    StatusChanged?.Invoke(this, _currentDownload);
                }
            }
            
            // Visually update queued items to Paused? Optional, but good feedback.
            foreach(var item in _queue)
            {
                 if (item.Status == "Queued")
                 {
                     item.Status = "Paused";
                     StatusChanged?.Invoke(this, item);
                 }
            }
        }

        public void Resume()
        {
            _isPaused = false;
            
            // Re-queue items that were Paused (if any are just stuck in Paused state in Queue)
             foreach(var item in _queue)
            {
                 if (item.Status == "Paused")
                 {
                     item.Status = "Queued";
                     StatusChanged?.Invoke(this, item);
                 }
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
            // If it's current
            if (_currentDownload == request)
            {
                request.Status = "Cancelled";
                StatusChanged?.Invoke(this, request); // Update UI
                request.CancellationTokenSource.Cancel();
                // It will be handled in catch/finally
            }
            // If in history
            else 
            {
                bool removed = false;
                lock (_historyLock)
                {
                    if (_history.Contains(request))
                    {
                         _history.Remove(request);
                         removed = true;
                    }
                }
                
                if (removed)
                {
                    QueueChanged?.Invoke(this, request);
                    SaveQueue();
                }
                // If in queue
                else
                {
                     // ...
                     request.Status = "Cancelled";
                     request.CancellationTokenSource.Cancel();
                     StatusChanged?.Invoke(this, request); // Update UI so it shows as Cancelled/Removed
                     SaveQueue();
                }
            }
        }
        public async Task<string> ExportToCbz(DownloadRequest request)
        {
            if (request.Status != "Completed") throw new InvalidOperationException("Download must be completed to export.");

            // 1. Locate Source Directory
            var safeMangaTitle = string.Join("_", request.Manga.Title.Split(Path.GetInvalidFileNameChars()));
            var safeChapterName = string.Join("_", request.Chapter.Name.Split(Path.GetInvalidFileNameChars()));
            var chapterDir = Path.Combine(_downloadBaseDir, request.Manga.Source.ToString(), safeMangaTitle, safeChapterName);

            if (!Directory.Exists(chapterDir)) throw new FileNotFoundException("Chapter files not found.", chapterDir);

            // 2. Prepare Destination
            // Save to User's Downloads folder / Yomic_Exports
            var userDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var exportFolder = Path.Combine(userDownloads, "Yomic_Exports");
            if (!Directory.Exists(exportFolder)) Directory.CreateDirectory(exportFolder);

            var fileName = $"{safeMangaTitle} - {safeChapterName}.cbz";
            var destPath = Path.Combine(exportFolder, fileName);

            // 3. Zip it (Create CBZ)
            // Use Task.Run for blocking IO
            await Task.Run(() =>
            {
                if (File.Exists(destPath)) File.Delete(destPath);
                System.IO.Compression.ZipFile.CreateFromDirectory(chapterDir, destPath);
            });

            return destPath;
        }
    }
}
