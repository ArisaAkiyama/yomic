using System.Collections.Generic;
using System.Threading.Tasks;
using Yomic.Core.Models;

namespace Yomic.Core.Sources
{
    public interface IMangaSource
    {
        long Id { get; }
        string Name { get; }
        string BaseUrl { get; }
        string Language { get; } // e.g. "EN", "ID"
        bool IsHasMorePages { get; } // For pagination check

        // Fetching
        Task<List<Manga>> GetPopularMangaAsync(int page);
        Task<List<Manga>> GetSearchMangaAsync(string query, int page);
        
        // Contextual Details
        Task<Manga> GetMangaDetailsAsync(string url);
        Task<List<Chapter>> GetChapterListAsync(string mangaUrl);
        Task<List<string>> GetPageListAsync(string chapterUrl);
    }
}
