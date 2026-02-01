using System;
using System.Collections.Generic;

namespace MyMangaApp.Core.Models
{
    public class Manga
    {
        public long Id { get; set; }
        public long Source { get; set; }
        public string Url { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        
        public string? Artist { get; set; }
        public string? Author { get; set; }
        public string? Description { get; set; }
        public List<string>? Genre { get; set; }
        public int Status { get; set; }
        public string? ThumbnailUrl { get; set; }
        
        public bool Favorite { get; set; }
        public long LastUpdate { get; set; } // Epoch
        public long LastViewed { get; set; } // Epoch
        public long NextUpdate { get; set; } // Epoch
        public bool Initialized { get; set; }

        // Viewer & Filter flags (Bitmask)
        public long ViewerFlags { get; set; }
        public long ChapterFlags { get; set; }
        public long CoverLastModified { get; set; }
        public long DateAdded { get; set; }

        // Relationships
        public virtual ICollection<Chapter> Chapters { get; set; } = new List<Chapter>();
        public virtual ICollection<History> History { get; set; } = new List<History>();

        // Constants (from Mihon)
        public const int UNKNOWN = 0;
        public const int ONGOING = 1;
        public const int COMPLETED = 2;
        public const int LICENSED = 3;
        public const int PUBLISHING_FINISHED = 4;
        public const int CANCELLED = 5;
        public const int ON_HIATUS = 6;
    }
}
