using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace Yomic.Core.Services
{
    public class BackupService
    {
        private readonly string _appDataFolder;
        private readonly string _dbPath;
        private readonly string _settingsPath;
        private readonly string _coversFolder;

        public BackupService()
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _appDataFolder = Path.Combine(folder, "Yomic");
            _dbPath = Path.Combine(_appDataFolder, "manga.db");
            _settingsPath = Path.Combine(_appDataFolder, "settings.json");
            _coversFolder = Path.Combine(_appDataFolder, "covers");
        }

        public async Task<bool> CreateBackupAsync(string destinationPath)
        {
            try
            {
                // Create a temporary zip file
                string tempZipPath = Path.Combine(Path.GetTempPath(), $"YomicBackup_{Guid.NewGuid()}.zip");

                if (File.Exists(tempZipPath))
                    File.Delete(tempZipPath);

                using (var archive = ZipFile.Open(tempZipPath, ZipArchiveMode.Create))
                {
                    // Add manga.db
                    if (File.Exists(_dbPath))
                    {
                        // To avoid locking issues, we might need to copy the db to a temp file first, 
                        // but SQLite usually allows reading if WAL mode is on. 
                        // To be safe, we'll try to add it directly. 
                        // If it fails due to locking, we will catch the exception.
                        
                        // Create a temporary copy of the DB to avoid lock issues
                        string tempDb = Path.Combine(Path.GetTempPath(), $"manga_{Guid.NewGuid()}.db");
                        File.Copy(_dbPath, tempDb, true);
                        archive.CreateEntryFromFile(tempDb, "manga.db");
                        File.Delete(tempDb);
                    }

                    // Add settings.json
                    if (File.Exists(_settingsPath))
                    {
                        archive.CreateEntryFromFile(_settingsPath, "settings.json");
                    }

                    // Add covers cache
                    if (Directory.Exists(_coversFolder))
                    {
                        foreach (var file in Directory.GetFiles(_coversFolder))
                        {
                            var fileName = Path.GetFileName(file);
                            archive.CreateEntryFromFile(file, $"covers/{fileName}");
                        }
                    }
                }

                // Move the temp zip to the final destination
                if (File.Exists(destinationPath))
                    File.Delete(destinationPath);
                
                File.Move(tempZipPath, destinationPath);
                return true;
            }
            catch (Exception ex)
            {
                LogService.Error("BackupService", "Error creating backup", ex);
                return false;
            }
        }

        public async Task<bool> RestoreBackupAsync(string sourceZipPath)
        {
            try
            {
                if (!File.Exists(sourceZipPath))
                    return false;

                // Extract to a temp directory first to verify contents
                string tempDir = Path.Combine(Path.GetTempPath(), $"YomicRestore_{Guid.NewGuid()}");
                Directory.CreateDirectory(tempDir);

                ZipFile.ExtractToDirectory(sourceZipPath, tempDir, overwriteFiles: true);

                bool restoredDb = false;
                bool restoredSettings = false;

                // Move manga.db
                string extractedDb = Path.Combine(tempDir, "manga.db");
                if (File.Exists(extractedDb))
                {
                    if (File.Exists(_dbPath))
                    {
                        // Clear connection pools to release any file locks held by EF Core / SQLite
                        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                        File.Delete(_dbPath);
                    }
                    
                    File.Move(extractedDb, _dbPath);
                    restoredDb = true;
                }

                // Move settings.json
                string extractedSettings = Path.Combine(tempDir, "settings.json");
                if (File.Exists(extractedSettings))
                {
                    if (File.Exists(_settingsPath))
                        File.Delete(_settingsPath);

                    File.Move(extractedSettings, _settingsPath);
                    restoredSettings = true;
                }

                // Restore covers cache
                string extractedCovers = Path.Combine(tempDir, "covers");
                if (Directory.Exists(extractedCovers))
                {
                    if (!Directory.Exists(_coversFolder))
                    {
                        Directory.CreateDirectory(_coversFolder);
                    }

                    foreach (var file in Directory.GetFiles(extractedCovers))
                    {
                        string fileName = Path.GetFileName(file);
                        string destPath = Path.Combine(_coversFolder, fileName);
                        if (File.Exists(destPath))
                        {
                            File.Delete(destPath);
                        }
                        File.Move(file, destPath);
                    }
                }

                // Cleanup
                Directory.Delete(tempDir, true);

                return restoredDb || restoredSettings;
            }
            catch (Exception ex)
            {
                LogService.Error("BackupService", "Error restoring backup", ex);
                return false;
            }
        }
    }
}
