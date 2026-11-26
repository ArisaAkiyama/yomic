using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Yomic.Core.Data;

namespace Yomic.Core.Services
{
    public class BackupService
    {
        private readonly string _appDataFolder;
        private readonly string _dbPath;
        private readonly string _settingsPath;
        private readonly string _coversFolder;

        public BackupService()
            : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yomic"))
        {
        }

        internal BackupService(string appDataFolder)
        {
            _appDataFolder = appDataFolder;
            _dbPath = Path.Combine(_appDataFolder, "manga.db");
            _settingsPath = Path.Combine(_appDataFolder, "settings.json");
            _coversFolder = Path.Combine(_appDataFolder, "covers");
        }

        private static void CopyLiveSqliteDatabaseOnline(string sourceDbPath, string destinationDbPath)
        {
            // Use official SQLite online backup API — safe under live WAL reads/writes and guarantees a consistent snapshot
            using var sourceConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={sourceDbPath};Mode=ReadOnly;Default Timeout=10");
            using var destConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={destinationDbPath};Mode=ReadWriteCreate;Pooling=False");
            sourceConn.Open();
            destConn.Open();
            sourceConn.BackupDatabase(destConn);
        }

        private static void ReplaceSqliteFileWithRetry(string sourceDbPath, string targetDbPath)
        {
            var walPath = targetDbPath + "-wal";
            var shmPath = targetDbPath + "-shm";
            const int maxAttempts = 5;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    // NEVER ignore locked WAL/SHM — leaving old WAL next to a restored DB causes SQLITE_CORRUPT!
                    if (File.Exists(walPath)) File.Delete(walPath);
                    if (File.Exists(shmPath)) File.Delete(shmPath);
                    File.Copy(sourceDbPath, targetDbPath, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    System.Threading.Thread.Sleep(100 * attempt);
                }
            }
        }

        private static bool IsValidSqliteDatabaseFile(string candidateDbPath)
        {
            if (!File.Exists(candidateDbPath)) return false;
            var info = new FileInfo(candidateDbPath);
            if (info.Length < 100) return false;

            byte[] header = new byte[16];
            using (var fs = new FileStream(candidateDbPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fs.Read(header, 0, 16) < 16) return false;
            }
            const string sqliteMagic = "SQLite format 3\0";
            if (Encoding.ASCII.GetString(header) != sqliteMagic) return false;

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={candidateDbPath};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            var result = cmd.ExecuteScalar()?.ToString();
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
        }

        private const long MaxBackupDbBytes = 512L * 1024 * 1024; // 512 MB cap against zip bombs
        private const long MaxBackupSettingsBytes = 5L * 1024 * 1024; // 5 MB cap for settings.json

        private static void ExtractEntryBounded(ZipArchiveEntry entry, string destinationPath, long maxBytes)
        {
            if (entry.Length > maxBytes)
            {
                throw new InvalidDataException($"Backup entry '{entry.Name}' exceeds maximum allowed size ({maxBytes} bytes).");
            }

            using var src = entry.Open();
            using var dst = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                totalRead += read;
                if (totalRead > maxBytes)
                {
                    throw new InvalidDataException($"Backup entry '{entry.Name}' exceeded maximum allowed decompressed size ({maxBytes} bytes).");
                }
                dst.Write(buffer, 0, read);
            }
        }

        private static string CreateSanitizedSettingsJson(string settingsFilePath)
        {
            var rawJson = File.ReadAllText(settingsFilePath);
            try
            {
                var node = JsonNode.Parse(rawJson) as JsonObject;
                if (node != null)
                {
                    // Strip OAuth tokens & account identity so backup archives are safe to share across machines/users
                    node["MalAccessToken"] = "";
                    node["MalRefreshToken"] = "";
                    node["MalTokenExpiresAt"] = 0;
                    node["MalUsername"] = "";
                    return node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                }
            }
            catch (Exception ex)
            {
                LogService.Warning("BackupService", $"Failed to parse settings.json for sanitization; omitting raw contents to avoid token leakage: {ex.Message}");
            }
            // Fail-closed: never leak unparsed/unsanitized settings.json into a backup archive
            return "{}";
        }

        public Task<bool> CreateBackupAsync(string destinationPath)
        {
            return Task.Run(() =>
            {
                string? tempDb = null;
                string? tempZipPath = null;
                try
                {
                    tempZipPath = Path.Combine(Path.GetTempPath(), $"YomicBackup_{Guid.NewGuid():N}.zip");

                    using (var archive = ZipFile.Open(tempZipPath, ZipArchiveMode.Create))
                    {
                        if (File.Exists(_dbPath))
                        {
                            tempDb = Path.Combine(Path.GetTempPath(), $"manga_{Guid.NewGuid():N}.db");
                            CopyLiveSqliteDatabaseOnline(_dbPath, tempDb);
                            archive.CreateEntryFromFile(tempDb, "manga.db", CompressionLevel.Fastest);
                        }

                        // Add sanitized settings.json (without sensitive OAuth tokens)
                        if (File.Exists(_settingsPath))
                        {
                            var sanitizedJson = CreateSanitizedSettingsJson(_settingsPath);
                            var settingsEntry = archive.CreateEntry("settings.json", CompressionLevel.Fastest);
                            using var entryStream = settingsEntry.Open();
                            var bytes = Encoding.UTF8.GetBytes(sanitizedJson);
                            entryStream.Write(bytes, 0, bytes.Length);
                        }
                    }

                    // Move atomically with overwrite=true (never delete destinationPath beforehand)
                    string? destDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                    File.Move(tempZipPath, destinationPath, overwrite: true);
                    tempZipPath = null; // moved successfully
                    return true;
                }
                catch (Exception ex)
                {
                    LogService.Error("BackupService", "Error creating backup", ex);
                    return false;
                }
                finally
                {
                    if (tempDb != null && File.Exists(tempDb))
                    {
                        try { File.Delete(tempDb); } catch { }
                    }
                    if (tempZipPath != null && File.Exists(tempZipPath))
                    {
                        try { File.Delete(tempZipPath); } catch { }
                    }
                }
            });
        }

        public Task<bool> RestoreBackupAsync(string sourceZipPath)
        {
            return Task.Run(() =>
            {
                string stagingDir = Path.Combine(Path.GetTempPath(), $"YomicRestore_{Guid.NewGuid():N}");
                string? rollbackDbPath = null;
                string? rollbackSettingsPath = null;
                bool dbGateHeld = false;
                try
                {
                    if (!File.Exists(sourceZipPath))
                        return false;

                    Directory.CreateDirectory(stagingDir);
                    string stagedDbPath = Path.Combine(stagingDir, "manga.db");
                    string stagedSettingsPath = Path.Combine(stagingDir, "settings.json");

                    bool hasDbEntry = false;
                    bool hasSettingsEntry = false;

                    // 1. Extract to isolated staging directory with zip-bomb bounds & validate BEFORE touching live files
                    using (var archive = ZipFile.OpenRead(sourceZipPath))
                    {
                        if (archive.Entries.Count > 100)
                        {
                            LogService.Error("BackupService", "Restore aborted: backup archive contains too many entries.");
                            return false;
                        }

                        var dbEntry = archive.GetEntry("manga.db");
                        if (dbEntry != null)
                        {
                            ExtractEntryBounded(dbEntry, stagedDbPath, MaxBackupDbBytes);
                            if (!IsValidSqliteDatabaseFile(stagedDbPath))
                            {
                                LogService.Error("BackupService", "Restore aborted: manga.db in backup failed SQLite integrity check.");
                                return false;
                            }
                            hasDbEntry = true;
                        }

                        var settingsEntry = archive.GetEntry("settings.json");
                        if (settingsEntry != null)
                        {
                            ExtractEntryBounded(settingsEntry, stagedSettingsPath, MaxBackupSettingsBytes);
                            // Validate JSON syntax (must be a JSON object) and strip any untrusted FeedbackWebhookUrl from restored backup
                            var settingsNode = JsonNode.Parse(File.ReadAllText(stagedSettingsPath)) as JsonObject;
                            if (settingsNode == null)
                            {
                                LogService.Error("BackupService", "Restore aborted: settings.json in backup is not a valid JSON object.");
                                return false;
                            }
                            var webhook = settingsNode["FeedbackWebhookUrl"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(webhook) &&
                                !Yomic.Core.Helpers.UrlSecurityHelper.IsAllowedFeedbackWebhookUrl(webhook, out _))
                            {
                                settingsNode["FeedbackWebhookUrl"] = SettingsService.DefaultFeedbackWebhookUrl;
                                File.WriteAllText(stagedSettingsPath, settingsNode.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                            }
                            hasSettingsEntry = true;
                        }
                    }

                    if (!hasDbEntry && !hasSettingsEntry)
                    {
                        return false;
                    }

                    Directory.CreateDirectory(_appDataFolder);

                    // Acquire exclusive DB maintenance gate so no background LibraryService/DownloadService/Reader
                    // write can race with ClearAllPools(), WAL deletion, file swap, or rollback.
                    MangaDbContext.DatabaseWriteGate.Wait();
                    dbGateHeld = true;

                    // 2. Replace manga.db safely with WAL-preserving rollback snapshot BEFORE removing live WAL/SHM
                    if (hasDbEntry)
                    {
                        if (File.Exists(_dbPath))
                        {
                            rollbackDbPath = _dbPath + $".pre_restore_{Guid.NewGuid():N}.bak";
                            // Capture full consistent snapshot (including uncheckpointed WAL frames) BEFORE deleting WAL!
                            CopyLiveSqliteDatabaseOnline(_dbPath, rollbackDbPath);
                        }

                        ReplaceSqliteFileWithRetry(stagedDbPath, _dbPath);
                        MangaDbContext.ResetColumnCheck();
                    }

                    // 3. Replace settings.json safely with rollback protection
                    if (hasSettingsEntry)
                    {
                        if (File.Exists(_settingsPath))
                        {
                            rollbackSettingsPath = _settingsPath + $".pre_restore_{Guid.NewGuid():N}.bak";
                            File.Copy(_settingsPath, rollbackSettingsPath, overwrite: true);
                        }
                        File.Copy(stagedSettingsPath, _settingsPath, overwrite: true);
                    }

                    // Cleanup rollback copies once everything succeeded
                    if (rollbackDbPath != null && File.Exists(rollbackDbPath)) { try { File.Delete(rollbackDbPath); } catch { } }
                    if (rollbackSettingsPath != null && File.Exists(rollbackSettingsPath)) { try { File.Delete(rollbackSettingsPath); } catch { } }

                    return true;
                }
                catch (Exception ex)
                {
                    LogService.Error("BackupService", "Error restoring backup (attempting rollback)", ex);
                    try
                    {
                        if (rollbackDbPath != null && File.Exists(rollbackDbPath))
                        {
                            ReplaceSqliteFileWithRetry(rollbackDbPath, _dbPath);
                            try { File.Delete(rollbackDbPath); } catch { }
                            MangaDbContext.ResetColumnCheck();
                        }
                        if (rollbackSettingsPath != null && File.Exists(rollbackSettingsPath))
                        {
                            File.Copy(rollbackSettingsPath, _settingsPath, overwrite: true);
                            try { File.Delete(rollbackSettingsPath); } catch { }
                        }
                    }
                    catch (Exception rollbackEx)
                    {
                        LogService.Error("BackupService", "Critical: rollback after failed restore also failed", rollbackEx);
                    }
                    return false;
                }
                finally
                {
                    if (dbGateHeld)
                    {
                        MangaDbContext.DatabaseWriteGate.Release();
                    }
                    if (Directory.Exists(stagingDir))
                    {
                        try { Directory.Delete(stagingDir, recursive: true); } catch { }
                    }
                }
            });
        }
    }
}
