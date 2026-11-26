using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Yomic.Core.Sources;
using Yomic.Core.Models;
using System.IO;

namespace Yomic.Core.Services
{
    public class SourceCacheEntry
    {
        public List<Manga> Items { get; set; } = new();
        public int TotalPages { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class SourceManager : IDisposable
    {
        private readonly List<IMangaSource> _sources;
        private readonly object _sourcesLock = new();
        private readonly string _localPluginsDir; // Bundled JS plugins (app directory)
        private readonly string _userPluginsDir;  // User JS plugins (AppData)

        // Track source ID to file path mapping
        private readonly Dictionary<long, string> _sourceIdToPath = new();

        // Simple in-memory cache: Key -> Entry
        private readonly ConcurrentDictionary<string, SourceCacheEntry> _cache = new();
        private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(30);
        private readonly Task _initialLoadTask;
        private int _bulkLoadDepth;

        public event Action? OnSourcesChanged;

        public static string GetUserPluginsDirectory()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = System.IO.Path.Combine(appData, "Yomic", "Plugins");
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            return dir;
        }

        public void LoadPlugins() => LoadExtensions();

        /// <summary>
        /// Awaits completion of background extension loading when constructed with <c>deferExtensionLoad: true</c>.
        /// </summary>
        public Task EnsureExtensionsLoadedAsync() => _initialLoadTask;

        public SourceManager(bool deferExtensionLoad = false)
            : this(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Yomic", "Plugins"),
                deferExtensionLoad)
        {
        }

        internal SourceManager(string localPluginsDir, string userPluginsDir, bool deferExtensionLoad = false)
        {
            _sources = new List<IMangaSource>();

            // 1. Define Bundled Plugins Path
            _localPluginsDir = localPluginsDir;
            if (!System.IO.Directory.Exists(_localPluginsDir)) System.IO.Directory.CreateDirectory(_localPluginsDir);

            // 2. Define User Plugins Path
            _userPluginsDir = userPluginsDir;
            if (!System.IO.Directory.Exists(_userPluginsDir)) System.IO.Directory.CreateDirectory(_userPluginsDir);

            if (deferExtensionLoad)
            {
                _initialLoadTask = Task.Run(() =>
                {
                    try { LoadExtensions(); }
                    catch (Exception ex) { LogService.Error("SourceManager", $"Deferred extension load failed: {ex.Message}"); }
                });
            }
            else
            {
                LoadExtensions();
                _initialLoadTask = Task.CompletedTask;
            }
        }

        #region Core Management (Thread Safe)

        public List<IMangaSource> GetSources()
        {
            lock (_sourcesLock)
            {
                return _sources.ToList(); // Return copy for thread safety
            }
        }

        public IMangaSource? GetSource(long id)
        {
            lock (_sourcesLock)
            {
                var exact = _sources.FirstOrDefault(s => s.Id == id);
                if (exact != null) return exact;
                // Safe unsigned distance check (prevents signed Int64 OverflowException near MinValue/MaxValue)
                return _sources.FirstOrDefault(s =>
                {
                    ulong dist = s.Id >= id ? (ulong)(s.Id - id) : (ulong)(id - s.Id);
                    return dist < 2048UL;
                });
            }
        }

        public IMangaSource? GetSourceByName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            lock (_sourcesLock)
            {
                return _sources.FirstOrDefault(s =>
                    string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(s.BaseUrl) && s.BaseUrl.Contains(name, StringComparison.OrdinalIgnoreCase)));
            }
        }

        public void AddSource(IMangaSource source)
        {
            IDisposable? replacedDisposable = null;
            lock (_sourcesLock)
            {
                var existing = _sources.FirstOrDefault(s => s.Id == source.Id);
                if (existing == null)
                {
                    _sources.Add(source);
                }
                else
                {
                    var idx = _sources.IndexOf(existing);
                    _sources[idx] = source;
                    if (!ReferenceEquals(existing, source) && existing is IDisposable d)
                    {
                        replacedDisposable = d;
                    }
                }
            }
            try { replacedDisposable?.Dispose(); } catch { }
            // Invoke event strictly outside _sourcesLock to prevent AB-BA deadlocks,
            // and suppress per-plugin bursts during bulk LoadExtensions()
            if (System.Threading.Volatile.Read(ref _bulkLoadDepth) == 0)
            {
                OnSourcesChanged?.Invoke();
            }
        }

        private void SafeDeleteFile(string path)
        {
            if (System.IO.File.Exists(path))
            {
                try
                {
                    System.IO.File.Delete(path);
                    LogService.Success("SourceManager", $"Deleted plugin: {System.IO.Path.GetFileName(path)}");
                }
                catch (Exception ex)
                {
                    try
                    {
                        var deletedPath = path + ".deleted";
                        if (System.IO.File.Exists(deletedPath)) System.IO.File.Delete(deletedPath);
                        System.IO.File.Move(path, deletedPath);
                        LogService.Success("SourceManager", $"Marked plugin for deletion: {System.IO.Path.GetFileName(path)}");
                    }
                    catch
                    {
                        LogService.Error("SourceManager", $"Failed to delete or rename plugin file: {ex.Message}");
                    }
                }
            }
        }

        public void RemoveSource(long id)
        {
            bool removed = false;
            IDisposable? disposableToRelease = null;
            string? fileToDelete = null;

            lock (_sourcesLock)
            {
                var source = _sources.FirstOrDefault(s => s.Id == id);
                if (source != null)
                {
                    _sources.Remove(source);
                    removed = true;
                    if (source is IDisposable disposable)
                    {
                        disposableToRelease = disposable;
                    }

                    if (_sourceIdToPath.TryGetValue(id, out var path))
                    {
                        _sourceIdToPath.Remove(id);
                        string? candidateDelete = null;
                        // NEVER delete bundled system plugins inside _localPluginsDir — only delete user plugins
                        if (Yomic.Core.Helpers.UrlSecurityHelper.IsPathInsideDirectory(path, _userPluginsDir))
                        {
                            candidateDelete = System.IO.Path.GetFullPath(path);
                        }
                        else
                        {
                            string? jsName = System.IO.Path.GetFileName(path);
                            if (!string.IsNullOrEmpty(jsName))
                            {
                                var userCopy = System.IO.Path.Combine(_userPluginsDir, jsName);
                                if (Yomic.Core.Helpers.UrlSecurityHelper.IsPathInsideDirectory(userCopy, _userPluginsDir))
                                {
                                    candidateDelete = System.IO.Path.GetFullPath(userCopy);
                                }
                            }
                        }

                        // Guard against upgrade ID-change self-deletion: if another active source already owns
                        // this exact file path on disk (e.g. upgraded plugin with a new Id), NEVER delete it!
                        if (!string.IsNullOrEmpty(candidateDelete))
                        {
                            bool stillOwnedByAnotherSource = _sourceIdToPath.Values.Any(otherPath =>
                            {
                                try
                                {
                                    return string.Equals(
                                        System.IO.Path.GetFullPath(otherPath),
                                        candidateDelete,
                                        StringComparison.OrdinalIgnoreCase);
                                }
                                catch
                                {
                                    return false;
                                }
                            });

                            if (!stillOwnedByAnotherSource)
                            {
                                fileToDelete = candidateDelete;
                            }
                        }
                    }
                }
            }

            if (disposableToRelease != null)
            {
                try { disposableToRelease.Dispose(); } catch { }
            }
            if (!string.IsNullOrEmpty(fileToDelete))
            {
                SafeDeleteFile(fileToDelete);
            }
            if (removed)
            {
                OnSourcesChanged?.Invoke();
            }
        }

        // Helper to check if a source is System (Bundled)
        public bool IsSystemSource(long id)
        {
            lock (_sourcesLock)
            {
                if (_sourceIdToPath.TryGetValue(id, out var path))
                {
                    return Yomic.Core.Helpers.UrlSecurityHelper.IsPathInsideDirectory(path, _localPluginsDir);
                }
                return false;
            }
        }

        // Helper to check if a source is actually installed (System or User) vs just loaded from random path
        public bool IsInstalledSource(long id)
        {
            lock (_sourcesLock)
            {
                if (_sourceIdToPath.TryGetValue(id, out var path))
                {
                    return Yomic.Core.Helpers.UrlSecurityHelper.IsPathInsideDirectory(path, _userPluginsDir) ||
                           Yomic.Core.Helpers.UrlSecurityHelper.IsPathInsideDirectory(path, _localPluginsDir);
                }
                return false;
            }
        }

        public string? GetSourcePath(long id)
        {
            lock (_sourcesLock)
            {
                return _sourceIdToPath.TryGetValue(id, out var path) ? path : null;
            }
        }

        #endregion

        #region Extension Loading

        private void LoadExtensions()
        {
            bool anyLoaded = false;
            System.Threading.Interlocked.Increment(ref _bulkLoadDepth);
            try
            {
                // Clean up .deleted files first
                var dirsToScan = new[] { _localPluginsDir, _userPluginsDir };
                foreach (var dir in dirsToScan)
                {
                    if (System.IO.Directory.Exists(dir))
                    {
                        var deletedFiles = System.IO.Directory.GetFiles(dir, "*.deleted");
                        foreach (var f in deletedFiles)
                        {
                            try { System.IO.File.Delete(f); } catch { }
                        }
                    }
                }

                // 1. Scan Bundled Root JS Plugins (exclude Plugins/Catalog offline repository)
                if (System.IO.Directory.Exists(_localPluginsDir))
                {
                    var jss = System.IO.Directory.GetFiles(_localPluginsDir, "*.js", System.IO.SearchOption.TopDirectoryOnly);
                    foreach (var js in jss)
                    {
                        if (LoadJsExtension(js) != null) anyLoaded = true;
                    }
                }

                // 2. Scan User JS Plugins (AppData) — auto-upgrade from bundled Plugins/Catalog if bundled is newer
                var catalogDir = System.IO.Path.Combine(_localPluginsDir, "Catalog");
                if (System.IO.Directory.Exists(_userPluginsDir))
                {
                    var jss = System.IO.Directory.GetFiles(_userPluginsDir, "*.js", System.IO.SearchOption.AllDirectories);
                    foreach (var js in jss)
                    {
                        TryUpgradeUserPluginFromCatalog(js, catalogDir);
                        if (LoadJsExtension(js) != null) anyLoaded = true;
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Error("SourceManager", $"Failed to load extensions", ex);
            }
            finally
            {
                if (System.Threading.Interlocked.Decrement(ref _bulkLoadDepth) == 0 && anyLoaded)
                {
                    OnSourcesChanged?.Invoke();
                }
            }
        }

        private static int ParseJsVersionCode(string jsText)
        {
            if (string.IsNullOrWhiteSpace(jsText)) return 0;
            var m = System.Text.RegularExpressions.Regex.Match(
                jsText,
                @"\bversion\s*:\s*[""']v?(\d+)\.(\d+)(?:\.(\d+))?[""']",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return 0;
            int.TryParse(m.Groups[1].Value, out int major);
            int.TryParse(m.Groups[2].Value, out int minor);
            int patch = 0;
            if (m.Groups[3].Success) int.TryParse(m.Groups[3].Value, out patch);
            return (major * 10000) + (minor * 100) + patch;
        }

        private static void TryUpgradeUserPluginFromCatalog(string userJsPath, string catalogDir)
        {
            try
            {
                if (!System.IO.Directory.Exists(catalogDir)) return;
                var fileName = System.IO.Path.GetFileName(userJsPath);
                var catalogJsPath = System.IO.Path.Combine(catalogDir, fileName);
                if (!System.IO.File.Exists(catalogJsPath)) return;

                var userCode = ParseJsVersionCode(System.IO.File.ReadAllText(userJsPath));
                var catalogText = System.IO.File.ReadAllText(catalogJsPath);
                var catalogCode = ParseJsVersionCode(catalogText);

                if (catalogCode > userCode)
                {
                    string tempPath = $"{userJsPath}.{Guid.NewGuid():N}.tmp";
                    System.IO.File.WriteAllText(tempPath, catalogText);
                    System.IO.File.Move(tempPath, userJsPath, overwrite: true);
                    LogService.Info("SourceManager", $"Auto-upgraded installed extension '{fileName}' from bundled catalog ({userCode} -> {catalogCode}).");
                }
            }
            catch { }
        }

        public IMangaSource? InstallPlugin(string sourcePath)
        {
            try
            {
                if (!System.IO.File.Exists(sourcePath)) return null;

                var fileName = System.IO.Path.GetFileName(sourcePath);
                if (!fileName.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                {
                    LogService.Error("SourceManager", $"Rejected non-JS extension: {fileName}");
                    return null;
                }

                // Copy to User Plugins Directory
                var destPath = System.IO.Path.Combine(_userPluginsDir, fileName);

                // Overwrite if already exists (update scenario), but only if source and destination are different files
                if (!string.Equals(System.IO.Path.GetFullPath(sourcePath), System.IO.Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                {
                    System.IO.File.Copy(sourcePath, destPath, overwrite: true);
                }

                // Load from the NEW location
                return LoadJsExtension(destPath);
            }
            catch (Exception ex)
            {
                LogService.Error("SourceManager", $"Failed to install plugin: {ex.Message}");
                return null;
            }
        }

        public IMangaSource? LoadJsExtension(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return null;
                var source = new JsMangaSource(path);
                AddSource(source);
                lock (_sourcesLock)
                {
                    _sourceIdToPath[source.Id] = path;
                }
                return source;
            }
            catch (Exception ex)
            {
                if (ex.InnerException?.Message.Contains("Script does not define a global 'source' object.") == true || ex.Message.Contains("Script does not define a global 'source' object."))
                {
                    LogService.Debug("SourceManager", $"Skipped JS helper file (not an extension): {path}");
                }
                else
                {
                    LogService.Error("SourceManager", $"Failed to load JS extension {path}", ex);
                }
            }
            return null;
        }

        public IMangaSource? PeekExtension(string path)
        {
            if (!System.IO.File.Exists(path)) throw new System.IO.FileNotFoundException("Extension file not found", path);

            if (!path.EndsWith(".js", System.StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("Only JS extensions are supported.");
            }

            try
            {
                return new JsMangaSource(path);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to peek JS extension: {ex.Message}");
            }
        }

        #endregion

        #region Caching

        public SourceCacheEntry? GetCachedResult(string key)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                if (DateTime.Now - entry.Timestamp < _cacheDuration)
                {
                    return entry;
                }
                else
                {
                    // Expired
                    _cache.TryRemove(key, out _);
                }
            }
            return null;
        }

        public void SetCachedResult(string key, List<Manga> items, int totalPages)
        {
            if (_cache.Count >= 256)
            {
                var now = DateTime.Now;
                foreach (var kv in _cache.ToArray())
                {
                    if (now - kv.Value.Timestamp >= _cacheDuration)
                    {
                        _cache.TryRemove(kv.Key, out _);
                    }
                }
                if (_cache.Count >= 256)
                {
                    foreach (var oldest in _cache.ToArray().OrderBy(kv => kv.Value.Timestamp).Take(64))
                    {
                        _cache.TryRemove(oldest.Key, out _);
                    }
                }
            }
            var entry = new SourceCacheEntry
            {
                Items = items,
                TotalPages = totalPages,
                Timestamp = DateTime.Now
            };
            _cache.AddOrUpdate(key, entry, (k, old) => entry);
        }

        public void InvalidateCache(string prefix)
        {
            var keysToRemove = _cache.Keys.Where(k => k.StartsWith(prefix)).ToList();
            foreach (var key in keysToRemove)
            {
                _cache.TryRemove(key, out _);
            }
        }

        public void InvalidateAllCachesForSource(long sourceId)
        {
            var cacheCount = _cache.Count;
            _cache.Clear();
            System.Console.WriteLine($"[SourceManager] Cleared all {cacheCount} cache entries after source {sourceId} refresh");
        }

        public void ClearAllCache()
        {
            _cache.Clear();
        }

        public void DeleteAllUserPlugins()
        {
            try
            {
                List<IMangaSource> toDispose;
                lock (_sourcesLock)
                {
                    toDispose = _sources.ToList();
                    _sources.Clear();
                    _sourceIdToPath.Clear();
                }

                foreach (var src in toDispose)
                {
                    if (src is IDisposable disp)
                    {
                        try { disp.Dispose(); } catch { }
                    }
                }
                _cache.Clear();

                if (System.IO.Directory.Exists(_userPluginsDir))
                {
                    foreach (var file in System.IO.Directory.GetFiles(_userPluginsDir))
                    {
                        System.IO.File.Delete(file);
                    }
                    foreach (var dir in System.IO.Directory.GetDirectories(_userPluginsDir))
                    {
                        System.IO.Directory.Delete(dir, recursive: true);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[SourceManager] Error deleting plugins: {ex.Message}");
            }
        }

        public void Dispose()
        {
            List<IMangaSource> toDispose;
            lock (_sourcesLock)
            {
                toDispose = _sources.ToList();
                _sources.Clear();
                _sourceIdToPath.Clear();
            }

            foreach (var src in toDispose)
            {
                if (src is IDisposable disp)
                {
                    try { disp.Dispose(); } catch { }
                }
            }
            _cache.Clear();
        }

        #endregion
    }
}
