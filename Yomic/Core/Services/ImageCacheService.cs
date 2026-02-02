using System;
using System.Collections.Concurrent;
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
    }
}
