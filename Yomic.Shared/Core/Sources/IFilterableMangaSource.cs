using System.Collections.Generic;
using System.Threading.Tasks;
using Yomic.Core.Models;

namespace Yomic.Core.Sources
{
    public interface IFilterableMangaSource
    {

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
