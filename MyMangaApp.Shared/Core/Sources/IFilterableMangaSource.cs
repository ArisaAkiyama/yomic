using System.Collections.Generic;
using System.Threading.Tasks;
using MyMangaApp.Core.Models;

namespace MyMangaApp.Core.Sources
{
    public interface IFilterableMangaSource
    {
        /// <summary>
        /// Get manga with specific filters.
        /// </summary>
        Task<(List<Manga> Items, int TotalPages)> GetFilteredMangaAsync(int page, int statusFilter = 0, int typeFilter = 0);

        /// <summary>
        /// Get latest updated manga (paginated).
        /// </summary>
        Task<(List<Manga> Items, int TotalPages)> GetLatestMangaAsync(int page);

        /// <summary>
        /// Get full directory of manga (paginated).
        /// </summary>
        Task<(List<Manga> Items, int TotalPages)> GetMangaListAsync(int page);
    }
}
