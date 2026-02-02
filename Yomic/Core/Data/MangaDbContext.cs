using Microsoft.EntityFrameworkCore;
using Yomic.Core.Models;
using System.Linq;

namespace Yomic.Core.Data
{
    public class MangaDbContext : DbContext
    {
        public DbSet<Manga> Mangas { get; set; } = null!;
        public DbSet<Chapter> Chapters { get; set; } = null!;
        public DbSet<History> History { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            var folder = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            var path = System.IO.Path.Combine(folder, "Yomic");
            if (!System.IO.Directory.Exists(path))
            {
                System.IO.Directory.CreateDirectory(path);
            }
            var dbPath = System.IO.Path.Combine(path, "manga.db");
            
            // Log path for debugging
            System.Diagnostics.Debug.WriteLine($"[DbContext] Database Path: {dbPath}");
            
            optionsBuilder.UseSqlite($"Data Source={dbPath}");
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Manga -> Chapters (One-to-Many)
            modelBuilder.Entity<Chapter>()
                .HasOne(c => c.Manga)
                .WithMany(m => m.Chapters)
                .HasForeignKey(c => c.MangaId)
                .OnDelete(DeleteBehavior.Cascade);

            // History -> Chapter (One-to-One or Many-to-One? Usually history tracks a chapter read event)
            modelBuilder.Entity<History>()
                .HasOne(h => h.Chapter)
                .WithMany()
                .HasForeignKey(h => h.ChapterId);

            // Genre Conversion
            modelBuilder.Entity<Manga>()
                .Property(e => e.Genre)
                .HasConversion(
                    v => v == null ? string.Empty : string.Join(",", v),
                    v => string.IsNullOrEmpty(v) ? new System.Collections.Generic.List<string>() : v.Split(',', System.StringSplitOptions.RemoveEmptyEntries).ToList());
                
            base.OnModelCreating(modelBuilder);
        }
    }
}
