using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Yomic.Core.Data;
using Yomic.Core.Models;
using Yomic.Core.Services;

namespace YomicDesktop.Tests;

public class BackupAndDatabaseTests
{
    [Fact]
    public async Task BackupService_RejectsZipBombCorruptSqliteAndNonObjectSettings()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"YomicBackupTest_{Guid.NewGuid():N}");
        string fakeAppData = Path.Combine(tempDir, "appdata");
        Directory.CreateDirectory(fakeAppData);
        var backupService = new BackupService(fakeAppData);

        try
        {
            // 1. Archive with > 100 entries must be rejected immediately
            string tooManyEntriesZip = Path.Combine(tempDir, "too_many.yomicbackup");
            using (var zip = ZipFile.Open(tooManyEntriesZip, ZipArchiveMode.Create))
            {
                for (int i = 0; i < 105; i++)
                {
                    var e = zip.CreateEntry($"f_{i}.txt");
                    using var s = e.Open();
                    s.WriteByte((byte)'a');
                }
            }
            Assert.False(await backupService.RestoreBackupAsync(tooManyEntriesZip));

            // 2. Archive containing invalid (non-SQLite) manga.db must be rejected BEFORE touching live DB
            string corruptDbZip = Path.Combine(tempDir, "corrupt_db.yomicbackup");
            using (var zip = ZipFile.Open(corruptDbZip, ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("manga.db");
                using var s = e.Open();
                var bogus = Encoding.UTF8.GetBytes("NOT A SQLITE DATABASE");
                s.Write(bogus, 0, bogus.Length);
            }
            Assert.False(await backupService.RestoreBackupAsync(corruptDbZip));

            // 3. Archive with non-object settings.json (e.g. JSON array) must be rejected
            string arraySettingsZip = Path.Combine(tempDir, "array_settings.yomicbackup");
            using (var zip = ZipFile.Open(arraySettingsZip, ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("settings.json");
                using var s = e.Open();
                var bytes = Encoding.UTF8.GetBytes("[1,2,3]");
                s.Write(bytes, 0, bytes.Length);
            }
            Assert.False(await backupService.RestoreBackupAsync(arraySettingsZip));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task BackupService_CreateAndRestore_SanitizesMalSecretsAndPreservesWalData()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"YomicBackupRoundTrip_{Guid.NewGuid():N}");
        string fakeAppData = Path.Combine(tempDir, "appdata");
        Directory.CreateDirectory(fakeAppData);
        string dbPath = Path.Combine(fakeAppData, "manga.db");
        string settingsPath = Path.Combine(fakeAppData, "settings.json");
        string backupZipPath = Path.Combine(tempDir, "export.yomicbackup");

        try
        {
            // Seed a real MangaDbContext database (WAL enabled via SqlitePragmaInterceptor)
            using (var db = new MangaDbContext(dbPath))
            {
                db.Database.EnsureCreated();
                db.Mangas.Add(new Manga
                {
                    Source = 12345,
                    Url = "/manga/backup-hero",
                    Title = "Backup Hero",
                    Favorite = true
                });
                db.SaveChanges();
            }

            // Seed settings.json with MAL secrets & username
            File.WriteAllText(settingsPath, "{\"IsDarkMode\":false,\"MalAccessToken\":\"secret-token\",\"MalRefreshToken\":\"secret-refresh\",\"MalUsername\":\"arisa\"}");

            var backupService = new BackupService(fakeAppData);
            Assert.True(await backupService.CreateBackupAsync(backupZipPath));
            Assert.True(File.Exists(backupZipPath));

            // Verify exported backup stripped MAL tokens AND MalUsername
            using (var zip = ZipFile.OpenRead(backupZipPath))
            {
                var sEntry = zip.GetEntry("settings.json");
                Assert.NotNull(sEntry);
                using var sr = new StreamReader(sEntry!.Open());
                var exportedNode = JsonNode.Parse(sr.ReadToEnd()) as JsonObject;
                Assert.NotNull(exportedNode);
                Assert.Equal("", exportedNode!["MalAccessToken"]?.ToString());
                Assert.Equal("", exportedNode["MalRefreshToken"]?.ToString());
                Assert.Equal("", exportedNode["MalUsername"]?.ToString());
                Assert.False(exportedNode["IsDarkMode"]?.GetValue<bool>());
            }

            // Mutate live DB, then restore from backup and confirm original state returns cleanly
            using (var db = new MangaDbContext(dbPath))
            {
                db.Mangas.RemoveRange(db.Mangas);
                db.SaveChanges();
                Assert.Equal(0, db.Mangas.Count());
            }

            Assert.True(await backupService.RestoreBackupAsync(backupZipPath));

            using (var db = new MangaDbContext(dbPath))
            {
                var restored = db.Mangas.SingleOrDefault();
                Assert.NotNull(restored);
                Assert.Equal("Backup Hero", restored!.Title);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void MangaDbContext_RealPragmaInterceptor_EnforcesForeignKeysAndCascadeDeletes()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"YomicDbFkTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "manga.db");

        try
        {
            using (var db = new MangaDbContext(dbPath))
            {
                db.Database.EnsureCreated();

                // 1. Verify SqlitePragmaInterceptor actually activated PRAGMA foreign_keys = 1 on this connection
                var conn = db.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open) conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA foreign_keys;";
                    long fkEnabled = Convert.ToInt64(cmd.ExecuteScalar());
                    Assert.Equal(1L, fkEnabled);
                }

                // 2. Seed Manga -> Chapter -> History
                var manga = new Manga
                {
                    Source = 999,
                    Url = "/manga/fk-test",
                    Title = "FK Test Manga",
                    Favorite = true
                };
                db.Mangas.Add(manga);
                db.SaveChanges();

                var chapter = new Chapter
                {
                    MangaId = manga.Id,
                    Url = "/chapter/1",
                    Name = "Chapter 1",
                    ChapterNumber = 1f
                };
                db.Chapters.Add(chapter);
                db.SaveChanges();

                db.History.Add(new History
                {
                    MangaId = manga.Id,
                    ChapterId = chapter.Id,
                    LastRead = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
                db.SaveChanges();

                // 3. Orphan Chapter insert referencing nonexistent MangaId MUST fail under real FK enforcement
                using (var badCmd = conn.CreateCommand())
                {
                    badCmd.CommandText = "INSERT INTO Chapters (MangaId, Url, Name, ChapterNumber, Read, Bookmark, LastPageRead, DateFetch, DateUpload, SourceOrder, IsDownloaded) VALUES (999999, '/orphan', 'Orphan', 1, 0, 0, 0, 0, 0, 0, 0);";
                    Assert.Throws<SqliteException>(() => badCmd.ExecuteNonQuery());
                }

                // 4. Deleting parent Manga must cascade-delete Chapters and History
                db.Mangas.Remove(manga);
                db.SaveChanges();

                Assert.Equal(0, db.Chapters.Count());
                Assert.Equal(0, db.History.Count());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void MangaDbContext_CreatesAllFivePerformanceIndexesInSqliteMaster()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"YomicDbIdxTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "manga.db");

        try
        {
            using var db = new MangaDbContext(dbPath);
            db.Database.EnsureCreated();
            MangaDbContext.EnsureMangaColumnsExist(db, forceRecheck: true);

            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) conn.Open();
            var indexNames = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index';";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    indexNames.Add(reader.GetString(0));
                }
            }

            Assert.Contains("IX_Mangas_Source_Url", indexNames);
            Assert.Contains("IX_Mangas_Favorite", indexNames);
            Assert.Contains("IX_Chapters_MangaId_Url", indexNames);
            Assert.Contains("IX_Chapters_DateFetch", indexNames);
            Assert.Contains("IX_History_MangaId_LastRead", indexNames);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
