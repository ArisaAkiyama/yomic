using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace Yomic.Core.Services
{
    public class ImageCacheService
    {
        // Thread-safe dictionary for bitmap cache: URL -> WeakReference<Bitmap>
        // Use WeakReference to allow GC to reclaim memory if needed.
        private readonly ConcurrentDictionary<string, WeakReference<Bitmap>> _cache = new();
        
        public Bitmap? GetImage(string url)
        {
            if (_cache.TryGetValue(url, out var weakRef))
            {
                if (weakRef.TryGetTarget(out var bitmap))
                {
                    return bitmap;
                }
                else
                {
                    // Clean up dead reference
                    _cache.TryRemove(url, out _);
                }
            }
            return null;
        }

        public void AddImage(string url, Bitmap bitmap)
        {
            if (bitmap == null) return;
            _cache.AddOrUpdate(url, new WeakReference<Bitmap>(bitmap), (k, v) => new WeakReference<Bitmap>(bitmap));
        }

        public void Clear()
        {
            _cache.Clear();
        }

        /// <summary>
        /// Clears all cached images for a specific source based on URL pattern matching
        /// </summary>
        /// <param name="sourceBaseUrl">Base URL of the source (e.g., "komikcast.fit", "komiku.org")</param>
        public void ClearForSource(string sourceBaseUrl)
        {
            if (string.IsNullOrEmpty(sourceBaseUrl)) return;
            
            // Extract domain from baseUrl for matching
            string domain = sourceBaseUrl.Replace("https://", "").Replace("http://", "").TrimEnd('/');
            
            var keysToRemove = _cache.Keys.Where(url => url.Contains(domain, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var key in keysToRemove)
            {
                _cache.TryRemove(key, out _);
            }
            
            System.Console.WriteLine($"[ImageCacheService] Cleared {keysToRemove.Count} cached images for source: {domain}");
        }
    }
}
