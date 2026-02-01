using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MyMangaApp.Core.Sources;
using MyMangaApp.Core.Models;
using System.IO;

namespace MyMangaApp.Core.Services
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
        private readonly string _extensionsFilePath;
        private readonly string _pluginsDir;
        
        // Track loaded paths to avoid duplicates and for saving
        private readonly HashSet<string> _loadedExtensionPaths = new();

        // Track source ID to file path mapping (needed because LoadFromStream loses Assembly.Location)
        private readonly Dictionary<long, string> _sourceIdToPath = new();

        // Simple in-memory cache: Key -> Entry
        private readonly ConcurrentDictionary<string, SourceCacheEntry> _cache = new();
        private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(30);

        public event Action? OnSourcesChanged;

        public SourceManager()
        {
            _sources = new List<IMangaSource>();
            
            // Define path in AppData
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appDir = System.IO.Path.Combine(appData, "MyMangaApp");
            if (!System.IO.Directory.Exists(appDir)) System.IO.Directory.CreateDirectory(appDir);
            
            _extensionsFilePath = System.IO.Path.Combine(appDir, "extensions.json");
            _pluginsDir = System.IO.Path.Combine(appDir, "Plugins");
            if (!System.IO.Directory.Exists(_pluginsDir)) System.IO.Directory.CreateDirectory(_pluginsDir);
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

        public void RemoveSource(long id)
        {
            lock (_sourcesLock)
            {
                var source = _sources.FirstOrDefault(s => s.Id == id);
                if (source != null)
                {
                    _sources.Remove(source);
                    
                    // Remove from persistence and DELETE file if plugin
                    try
                    {
                        string? path = null;
                        if (_sourceIdToPath.TryGetValue(id, out path) || 
                            (!string.IsNullOrEmpty(source.GetType().Assembly.Location) && (path = source.GetType().Assembly.Location) != null))
                        {
                             // 1. Remove from persistence list
                            if (_loadedExtensionPaths.Contains(path))
                            {
                                _loadedExtensionPaths.Remove(path);
                                SaveExtensions();
                            }
                            
                            // 2. Delete file if it is in Plugins folder
                            // Check if path is inside Plugins directory
                            var fullPath = System.IO.Path.GetFullPath(path);
                            var pluginsPath = System.IO.Path.GetFullPath(_pluginsDir);
                            
                            if (fullPath.StartsWith(pluginsPath, StringComparison.OrdinalIgnoreCase))
                            {
                                System.Diagnostics.Debug.WriteLine($"[SourceManager] Deleting plugin file: {fullPath}");
                                try
                                {
                                    if (System.IO.File.Exists(fullPath))
                                    {
                                        System.IO.File.Delete(fullPath);
                                    }
                                }
                                catch (Exception deleteEx)
                                {
                                     System.Diagnostics.Debug.WriteLine($"[SourceManager] Failed to delete file {fullPath}: {deleteEx.Message}");
                                }
                            }

                            if (_sourceIdToPath.ContainsKey(id)) _sourceIdToPath.Remove(id);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SourceManager] Failed to remove persistence for source {id}: {ex.Message}");
                    }

                    OnSourcesChanged?.Invoke();
                }
            }
        }

        #endregion

        #region Extension Persistence & Loading

        public void LoadExtensions()
        {
            try
            {
                // 1. Load from JSON (User added external paths)
                if (System.IO.File.Exists(_extensionsFilePath))
                {
                    var json = System.IO.File.ReadAllText(_extensionsFilePath);
                    var paths = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(json);
                    
                    if (paths != null)
                    {
                        foreach (var path in paths)
                        {
                            LoadExtensionAssembly(path, saveAfter: false);
                        }
                    }
                }
                
                // 2. Scan Plugins Directory
                if (System.IO.Directory.Exists(_pluginsDir))
                {
                    var dlls = System.IO.Directory.GetFiles(_pluginsDir, "*.dll");
                    foreach (var dll in dlls)
                    {
                        LoadExtensionAssembly(dll, saveAfter: false);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SourceManager] Failed to load extensions: {ex.Message}");
            }
        }

        public IMangaSource? InstallPlugin(string sourcePath)
        {
            try
            {
                if (!System.IO.File.Exists(sourcePath)) return null;

                // 1. Check if it's already in the plugins folder
                var fullSourcePath = System.IO.Path.GetFullPath(sourcePath);
                var fullPluginsDir = System.IO.Path.GetFullPath(_pluginsDir);

                string destPath = sourcePath;

                if (!fullSourcePath.StartsWith(fullPluginsDir, StringComparison.OrdinalIgnoreCase))
                {
                    // It's an external file, copy it!
                    var fileName = System.IO.Path.GetFileName(sourcePath);
                    destPath = System.IO.Path.Combine(_pluginsDir, fileName);

                    // Ensure directory exists (constructor does it, but safety check)
                    if (!System.IO.Directory.Exists(_pluginsDir)) System.IO.Directory.CreateDirectory(_pluginsDir);

                    System.IO.File.Copy(sourcePath, destPath, overwrite: true);
                }

                // 2. Load from the (new) location
                // saveAfter=false is redundant because LoadExtensionAssembly detects it's in Plugins dir, 
                // but explicit is fine.
                return LoadExtensionAssembly(destPath, saveAfter: false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SourceManager] Failed to install plugin {sourcePath}: {ex.Message}");
                return null;
            }
        }

        public IMangaSource? LoadExtensionAssembly(string path, bool saveAfter = true)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return null;

                // Load Assembly from MEMORY to avoid file locking
                // This allows us to delete the file later even if the assembly is loaded
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
                        
                            // Only add to persistence list if it's NOT in the Plugins folder
                            bool isPlugin = System.IO.Path.GetFullPath(path).StartsWith(System.IO.Path.GetFullPath(_pluginsDir), StringComparison.OrdinalIgnoreCase);
                            
                            if (!isPlugin)
                            {
                                _loadedExtensionPaths.Add(path);
                            }
                        }
                        
                        if (saveAfter) SaveExtensions();
                        
                        return source;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SourceManager] Failed to load assembly {path}: {ex.Message}");
            }
            return null;
        }

        public IMangaSource? PeekExtension(string path)
        {
            // Peek also uses memory loading just to be safe/consistent
            if (!System.IO.File.Exists(path)) throw new System.IO.FileNotFoundException("Extension DLL not found", path);

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

        private void SaveExtensions()
        {
            try
            {
                lock (_sourcesLock)
                {
                    var json = Newtonsoft.Json.JsonConvert.SerializeObject(_loadedExtensionPaths, Newtonsoft.Json.Formatting.Indented);
                    System.IO.File.WriteAllText(_extensionsFilePath, json);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SourceManager] Failed to save extensions: {ex.Message}");
            }
        }

        public void ClearAllExtensions()
        {
            lock (_sourcesLock)
            {
                _sources.Clear();
                _loadedExtensionPaths.Clear();
                _sourceIdToPath.Clear();
            }

            // 1. Delete extensions.json
            if (System.IO.File.Exists(_extensionsFilePath))
            {
                try { System.IO.File.Delete(_extensionsFilePath); } catch { }
            }

            // 2. Delete Plugins folder contents
            if (System.IO.Directory.Exists(_pluginsDir))
            {
                try
                {
                    var files = System.IO.Directory.GetFiles(_pluginsDir);
                    foreach (var file in files)
                    {
                        try { System.IO.File.Delete(file); } catch { }
                    }
                }
                catch { }
            }
            
            // 3. Clear Cache
            ClearAllCache();

            OnSourcesChanged?.Invoke();
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
