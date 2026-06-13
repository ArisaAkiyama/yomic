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

    public class SourceManager
    {
        private readonly List<IMangaSource> _sources;
        private readonly object _sourcesLock = new();
        private readonly string _localPluginsDir; // Bundled (Program Files)
        private readonly string _userPluginsDir;  // User (AppData)
        
        // Track source ID to file path mapping
        private readonly Dictionary<long, string> _sourceIdToPath = new();

        // Simple in-memory cache: Key -> Entry
        private readonly ConcurrentDictionary<string, SourceCacheEntry> _cache = new();
        private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(30);

        public event Action? OnSourcesChanged;
        
        public SourceManager()
        {
            _sources = new List<IMangaSource>();
            
            // 1. Define Bundled Plugins Path (App Directory)
            _localPluginsDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins");
            if (!System.IO.Directory.Exists(_localPluginsDir)) System.IO.Directory.CreateDirectory(_localPluginsDir);

            // 2. Define User Plugins Path (AppData)
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _userPluginsDir = System.IO.Path.Combine(appData, "Yomic", "Plugins");
            if (!System.IO.Directory.Exists(_userPluginsDir)) System.IO.Directory.CreateDirectory(_userPluginsDir);
            
            LoadExtensions();
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
                return _sources.FirstOrDefault(s => s.Id == id);
            }
        }

        public void AddSource(IMangaSource source)
        {
            lock (_sourcesLock)
            {
                if (!_sources.Any(s => s.Id == source.Id))
                {
                    _sources.Add(source);
                    OnSourcesChanged?.Invoke();
                }
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
            lock (_sourcesLock)
            {
                var source = _sources.FirstOrDefault(s => s.Id == id);
                if (source != null)
                {
                    _sources.Remove(source);
                    
                    // 1. Delete the path currently tracked
                    if (_sourceIdToPath.TryGetValue(id, out var path))
                    {
                        SafeDeleteFile(System.IO.Path.GetFullPath(path));
                        _sourceIdToPath.Remove(id);
                    }

                    // 2. Also aggressively delete from both plugin directories using the filename or Assembly Name
                    if (source is JsMangaSource)
                    {
                        string jsName = System.IO.Path.GetFileName(path);
                        if (!string.IsNullOrEmpty(jsName))
                        {
                            SafeDeleteFile(System.IO.Path.Combine(_localPluginsDir, jsName));
                            SafeDeleteFile(System.IO.Path.Combine(_userPluginsDir, jsName));
                        }
                    }
                    else
                    {
                        string dllName = source.GetType().Assembly.GetName().Name + ".dll";
                        if (!string.IsNullOrEmpty(dllName) && dllName != "Yomic.Shared.dll")
                        {
                            SafeDeleteFile(System.IO.Path.Combine(_localPluginsDir, dllName));
                            SafeDeleteFile(System.IO.Path.Combine(_userPluginsDir, dllName));
                        }
                    }

                    OnSourcesChanged?.Invoke();
                }
            }
        }
        
        // Helper to check if a source is System (Bundled)
        public bool IsSystemSource(long id)
        {
             if (_sourceIdToPath.TryGetValue(id, out var path))
             {
                 var fullPath = System.IO.Path.GetFullPath(path);
                 var fullLocalDir = System.IO.Path.GetFullPath(_localPluginsDir);
                 return fullPath.StartsWith(fullLocalDir, StringComparison.OrdinalIgnoreCase);
             }
             return false;
        }

        // Helper to check if a source is actually installed (System or User) vs just loaded from random path
        public bool IsInstalledSource(long id)
        {
             if (_sourceIdToPath.TryGetValue(id, out var path))
             {
                 var fullPath = System.IO.Path.GetFullPath(path);
                 var fullUserDir = System.IO.Path.GetFullPath(_userPluginsDir);
                 var fullLocalDir = System.IO.Path.GetFullPath(_localPluginsDir);
                 
                 return fullPath.StartsWith(fullUserDir, StringComparison.OrdinalIgnoreCase) ||
                        fullPath.StartsWith(fullLocalDir, StringComparison.OrdinalIgnoreCase);
             }
             return false;
        }
        
        public string? GetSourcePath(long id)
        {
            if (_sourceIdToPath.TryGetValue(id, out var path)) return path;
            return null;
        }

        #endregion

        #region Extension Loading

        private void LoadExtensions()
        {
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

                // 1. Scan Bundled Plugins (Program Files)
                if (System.IO.Directory.Exists(_localPluginsDir))
                {
                    var dlls = System.IO.Directory.GetFiles(_localPluginsDir, "*.dll");
                    foreach (var dll in dlls)
                    {
                        LoadExtensionAssembly(dll);
                    }
                    var jss = System.IO.Directory.GetFiles(_localPluginsDir, "*.js");
                    foreach (var js in jss)
                    {
                        LoadJsExtension(js);
                    }
                }

                // 2. Scan User Plugins (AppData)
                if (System.IO.Directory.Exists(_userPluginsDir))
                {
                    var dlls = System.IO.Directory.GetFiles(_userPluginsDir, "*.dll");
                    foreach (var dll in dlls)
                    {
                        LoadExtensionAssembly(dll);
                    }
                    var jss = System.IO.Directory.GetFiles(_userPluginsDir, "*.js");
                    foreach (var js in jss)
                    {
                        LoadJsExtension(js);
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Error("SourceManager", $"Failed to load extensions", ex);
            }
        }

        public IMangaSource? InstallPlugin(string sourcePath)
        {
            try
            {
                if (!System.IO.File.Exists(sourcePath)) return null;

                // Copy to User Plugins Directory
                var fileName = System.IO.Path.GetFileName(sourcePath);
                var destPath = System.IO.Path.Combine(_userPluginsDir, fileName);

                // If verifying existence or overwrite needed
                System.IO.File.Copy(sourcePath, destPath, overwrite: true);

                // Load from the NEW location
                if (fileName.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                {
                    return LoadJsExtension(destPath);
                }
                else
                {
                    return LoadExtensionAssembly(destPath);
                }
            }
            catch (Exception ex) 
            {
                 LogService.Error("SourceManager", $"Failed to install plugin: {ex.Message}");
                 return null;
            }
        }

        public IMangaSource? LoadExtensionAssembly(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return null;

                // Load Assembly from MEMORY to avoid file locking
                byte[] assemblyBytes = System.IO.File.ReadAllBytes(path);
                using var stream = new System.IO.MemoryStream(assemblyBytes);

                var loadContext = new ExtensionLoadContext(path);
                var assembly = loadContext.LoadFromStream(stream);
                
                var types = assembly.GetTypes();
                
                var sourceType = types.FirstOrDefault(t => 
                    t.GetInterfaces().Any(i => i.FullName == typeof(IMangaSource).FullName) 
                    && !t.IsInterface && !t.IsAbstract);

                if (sourceType != null)
                {
                    var source = (IMangaSource?)Activator.CreateInstance(sourceType);
                    if (source != null)
                    {
                        AddSource(source);
                        
                        lock (_sourcesLock)
                        {
                            // Track path explicitly since Location is empty for Stream loaded assemblies
                            _sourceIdToPath[source.Id] = path;
                        }
                        
                        return source;
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Error("SourceManager", $"Failed to load assembly {path}", ex);
            }
            return null;
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

            if (path.EndsWith(".js", System.StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return new JsMangaSource(path);
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to peek JS extension: {ex.Message}");
                }
            }

            byte[] assemblyBytes = System.IO.File.ReadAllBytes(path);
            using var stream = new System.IO.MemoryStream(assemblyBytes);

            var loadContext = new ExtensionLoadContext(path);
            var assembly = loadContext.LoadFromStream(stream);
            
            var types = assembly.GetTypes();
            
            var sourceType = types.FirstOrDefault(t => 
                t.GetInterfaces().Any(i => i.FullName == typeof(IMangaSource).FullName) 
                && !t.IsInterface && !t.IsAbstract);

            if (sourceType != null)
            {
                try
                {
                    return (IMangaSource?)Activator.CreateInstance(sourceType);
                }
                catch (System.Reflection.TargetInvocationException ex)
                {
                    throw new Exception(ex.InnerException?.Message ?? ex.Message);
                }
            }

            return null;
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
            // Clear all cache entries containing this source ID
            var keysToRemove = _cache.Keys.Where(k => k.Contains($"source_{sourceId}") || k.Contains($"_{sourceId}_")).ToList();
            foreach (var key in keysToRemove)
            {
                _cache.TryRemove(key, out _);
            }
            System.Console.WriteLine($"[SourceManager] Invalidated {keysToRemove.Count} cache entries for source {sourceId}");
        }

        public void ClearAllCache()
        {
            _cache.Clear();
        }

        #endregion
    }
}
