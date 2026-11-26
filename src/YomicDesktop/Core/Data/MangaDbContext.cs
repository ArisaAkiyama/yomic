using Microsoft.EntityFrameworkCore;
using Yomic.Core.Models;
using System.Linq;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Yomic.Core.Data
{
    public class MangaDbContext : DbContext
    {
        public DbSet<Manga> Mangas { get; set; } = null!;
        public DbSet<Chapter> Chapters { get; set; } = null!;
        public DbSet<History> History { get; set; } = null!;
        public DbSet<Category> Categories { get; set; } = null!;
        public DbSet<MangaTrack> Tracks { get; set; } = null!;

        private readonly string? _customDbPath;
        private static bool _hasLoggedPath = false;
        private static volatile bool _columnsEnsured = false;
        private static readonly object _columnsLock = new object();

        /// <summary>
        /// Shared process-wide SQLite write &amp; maintenance gate coordinating LibraryService upserts,
        /// IPC manga/reader-progress writes, and exclusive BackupService DB swaps without races.
        /// </summary>
        internal static readonly SemaphoreSlim DatabaseWriteGate = new(1, 1);

        // Per-DB-path reader/connection barrier so BackupService.RestoreBackupAsync blocks BOTH
        // concurrent writers and concurrent readers before ClearAllPools() / WAL deletion / file swap.
        internal sealed class DbMaintenanceState
        {
            public readonly object Sync = new();
            public int ActiveConnections;
            public bool MaintenanceActive;
            public TaskCompletionSource<bool>? MaintenanceCompletedTcs;
            public TaskCompletionSource<bool>? DrainedTcs;
        }

        internal sealed class ConnectionLease
        {
            private readonly DbMaintenanceState _state;
            private int _released;
            internal ConnectionLease(DbMaintenanceState state) => _state = state;
            public void ReleaseOnce()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    ReleaseConnectionSlot(_state);
                }
            }
        }

        private sealed class ExclusiveMaintenanceLease : IDisposable
        {
            private readonly DbMaintenanceState _state;
            private int _disposed;
            public ExclusiveMaintenanceLease(DbMaintenanceState state) => _state = state;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                TaskCompletionSource<bool>? toComplete;
                lock (_state.Sync)
                {
                    _state.MaintenanceActive = false;
                    toComplete = _state.MaintenanceCompletedTcs;
                    _state.MaintenanceCompletedTcs = null;
                    _state.DrainedTcs = null;
                }
                toComplete?.TrySetResult(true);
                DatabaseWriteGate.Release();
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DbMaintenanceState> _dbMaintenanceStates =
            new(System.StringComparer.OrdinalIgnoreCase);

        private static string NormalizeDbKey(string? dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                var folder = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
                dbPath = System.IO.Path.Combine(folder, "Yomic", "manga.db");
            }
            try { return System.IO.Path.GetFullPath(dbPath); }
            catch { return dbPath; }
        }

        private static DbMaintenanceState GetMaintenanceState(string dbPath) =>
            _dbMaintenanceStates.GetOrAdd(NormalizeDbKey(dbPath), _ => new DbMaintenanceState());

        internal static async Task<IDisposable> EnterExclusiveMaintenanceAsync(string dbPath, TimeSpan? drainTimeout = null)
        {
            await DatabaseWriteGate.WaitAsync().ConfigureAwait(false);
            var state = GetMaintenanceState(dbPath);
            Task? drainWaitTask = null;
            lock (state.Sync)
            {
                state.MaintenanceActive = true;
                state.MaintenanceCompletedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (state.ActiveConnections > 0)
                {
                    state.DrainedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    drainWaitTask = state.DrainedTcs.Task;
                }
            }

            if (drainWaitTask != null)
            {
                try
                {
                    await drainWaitTask.WaitAsync(drainTimeout ?? System.TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (System.TimeoutException)
                {
                    // Proceed after drain timeout; ReplaceSqliteFileWithRetry still retries locked file swaps safely
                }
            }

            return new ExclusiveMaintenanceLease(state);
        }

        internal static ConnectionLease AcquireConnectionSlotSync(string dbPath)
        {
            var state = GetMaintenanceState(dbPath);
            while (true)
            {
                Task? waitTask = null;
                lock (state.Sync)
                {
                    if (!state.MaintenanceActive)
                    {
                        state.ActiveConnections++;
                        return new ConnectionLease(state);
                    }
                    waitTask = state.MaintenanceCompletedTcs?.Task;
                }
                waitTask?.GetAwaiter().GetResult();
            }
        }

        internal static async Task<ConnectionLease> AcquireConnectionSlotAsync(string dbPath, CancellationToken ct)
        {
            var state = GetMaintenanceState(dbPath);
            while (true)
            {
                Task? waitTask = null;
                lock (state.Sync)
                {
                    if (!state.MaintenanceActive)
                    {
                        state.ActiveConnections++;
                        return new ConnectionLease(state);
                    }
                    waitTask = state.MaintenanceCompletedTcs?.Task;
                }
                if (waitTask != null)
                {
                    await waitTask.WaitAsync(ct).ConfigureAwait(false);
                }
            }
        }

        private static void ReleaseConnectionSlot(DbMaintenanceState state)
        {
            TaskCompletionSource<bool>? drained = null;
            lock (state.Sync)
            {
                if (state.ActiveConnections > 0)
                {
                    state.ActiveConnections--;
                }
                if (state.ActiveConnections == 0 && state.MaintenanceActive && state.DrainedTcs != null)
                {
                    drained = state.DrainedTcs;
                    state.DrainedTcs = null;
                }
            }
            drained?.TrySetResult(true);
        }

        public MangaDbContext()
        {
        }

        internal MangaDbContext(string customDbPath)
        {
            _customDbPath = customDbPath;
        }

        public static void ResetColumnCheck()
        {
            _columnsEnsured = false;
        }

        public static void EnsureMangaColumnsExist(DbContext context, bool forceRecheck = false)
        {
            if (_columnsEnsured && !forceRecheck) return;
            lock (_columnsLock)
            {
                if (_columnsEnsured && !forceRecheck) return;
                bool weOpened = false;
                DbConnection? conn = null;
                try
                {
                    // NEVER wrap GetDbConnection() in `using` — the DbContext owns its connection!
                    conn = context.Database.GetDbConnection();
                    if (conn.State != System.Data.ConnectionState.Open)
                    {
                        conn.Open();
                        weOpened = true;
                    }
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "PRAGMA table_info(Mangas);";
                    using var reader = cmd.ExecuteReader();
                    var cols = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                    while (reader.Read())
                    {
                        if (reader.FieldCount > 1) cols.Add(reader.GetString(1));
                    }
                    reader.Close();

                    if (cols.Count > 0)
                    {
                        if (!cols.Contains("ResponseETag"))
                        {
                            using var alter = conn.CreateCommand();
                            alter.CommandText = "ALTER TABLE Mangas ADD COLUMN ResponseETag TEXT;";
                            alter.ExecuteNonQuery();
                            System.Diagnostics.Debug.WriteLine("[DbContext] Added column ResponseETag to Mangas.");
                        }
                        if (!cols.Contains("ResponseLastModified"))
                        {
                            using var alter = conn.CreateCommand();
                            alter.CommandText = "ALTER TABLE Mangas ADD COLUMN ResponseLastModified TEXT;";
                            alter.ExecuteNonQuery();
                            System.Diagnostics.Debug.WriteLine("[DbContext] Added column ResponseLastModified to Mangas.");
                        }

                        // Clean up any legacy pre-FK orphaned rows so enabling PRAGMA foreign_keys=ON never breaks upgrades
                        try
                        {
                            using var cleanup = conn.CreateCommand();
                            cleanup.CommandText = @"
                                DELETE FROM History WHERE MangaId NOT IN (SELECT Id FROM Mangas) OR ChapterId NOT IN (SELECT Id FROM Chapters);
                                DELETE FROM Chapters WHERE MangaId NOT IN (SELECT Id FROM Mangas);
                                DELETE FROM Tracks WHERE MangaId NOT IN (SELECT Id FROM Mangas);
                                DELETE FROM MangaCategory WHERE MangaId NOT IN (SELECT Id FROM Mangas) OR CategoryId NOT IN (SELECT Id FROM Categories);
                            ";
                            cleanup.ExecuteNonQuery();
                        }
                        catch { }

                        // Ensure performance & lookup indexes exist on existing upgraded SQLite databases
                        bool indexesOk = false;
                        try
                        {
                            using var idxCmd = conn.CreateCommand();
                            idxCmd.CommandText = @"
                                CREATE INDEX IF NOT EXISTS IX_Mangas_Source_Url ON Mangas (Source, Url);
                                CREATE INDEX IF NOT EXISTS IX_Mangas_Favorite ON Mangas (Favorite);
                                CREATE INDEX IF NOT EXISTS IX_Chapters_MangaId_Url ON Chapters (MangaId, Url);
                                CREATE INDEX IF NOT EXISTS IX_Chapters_DateFetch ON Chapters (DateFetch);
                                CREATE INDEX IF NOT EXISTS IX_History_MangaId_LastRead ON History (MangaId, LastRead);
                            ";
                            idxCmd.ExecuteNonQuery();
                            indexesOk = true;
                        }
                        catch { }

                        if (indexesOk)
                        {
                            _columnsEnsured = true;
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DbContext] EnsureMangaColumnsExist error: {ex.Message}");
                }
                finally
                {
                    if (weOpened && conn != null && conn.State == System.Data.ConnectionState.Open)
                    {
                        try { conn.Close(); } catch { }
                    }
                }
            }
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            string dbPath;
            if (!string.IsNullOrWhiteSpace(_customDbPath))
            {
                var customDir = System.IO.Path.GetDirectoryName(_customDbPath);
                if (!string.IsNullOrEmpty(customDir) && !System.IO.Directory.Exists(customDir))
                {
                    System.IO.Directory.CreateDirectory(customDir);
                }
                dbPath = _customDbPath;
            }
            else
            {
                var folder = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
                var path = System.IO.Path.Combine(folder, "Yomic");
                if (!System.IO.Directory.Exists(path))
                {
                    System.IO.Directory.CreateDirectory(path);
                }
                dbPath = System.IO.Path.Combine(path, "manga.db");

                // Log path for debugging once
                if (!_hasLoggedPath)
                {
                    System.Diagnostics.Debug.WriteLine($"[DbContext] Database Path: {dbPath}");
                    _hasLoggedPath = true;
                }
            }

            // Do NOT use Cache=Shared with WAL — shared-cache causes SQLITE_LOCKED bypassing busy_timeout
            optionsBuilder.UseSqlite($"Data Source={dbPath};Default Timeout=10");
            optionsBuilder.AddInterceptors(new SqlitePragmaInterceptor(dbPath));
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Manga -> Chapters (One-to-Many)
            modelBuilder.Entity<Chapter>()
                .HasOne(c => c.Manga)
                .WithMany(m => m.Chapters)
                .HasForeignKey(c => c.MangaId)
                .OnDelete(DeleteBehavior.Cascade);

            // History -> Chapter (cascade delete prevents orphaned history rows when chapters are removed)
            modelBuilder.Entity<History>()
                .HasOne(h => h.Chapter)
                .WithMany()
                .HasForeignKey(h => h.ChapterId)
                .OnDelete(DeleteBehavior.Cascade);

            // Genre Conversion with proper ValueComparer so EF Core tracks List<string> mutations
            var genreComparer = new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<string>?>(
                (c1, c2) => (c1 == null && c2 == null) || (c1 != null && c2 != null && c1.SequenceEqual(c2)),
                c => c == null ? 0 : c.Aggregate(0, (a, v) => System.HashCode.Combine(a, v.GetHashCode())),
                c => c == null ? null : c.ToList());

            modelBuilder.Entity<Manga>()
                .Property(e => e.Genre)
                .HasConversion(
                    v => v == null ? string.Empty : string.Join(",", v),
                    v => string.IsNullOrEmpty(v) ? new System.Collections.Generic.List<string>() : v.Split(',', System.StringSplitOptions.RemoveEmptyEntries).ToList(),
                    genreComparer);

            // Category Mapping
            modelBuilder.Entity<Category>(entity =>
            {
                entity.HasKey(c => c.Id);
                entity.Property(c => c.Name).IsRequired();
            });

            // Many-to-Many: Manga <-> Category
            modelBuilder.Entity<Manga>()
                .HasMany(m => m.Categories)
                .WithMany(c => c.Mangas)
                .UsingEntity<Dictionary<string, object>>(
                    "MangaCategory",
                    r => r.HasOne<Category>().WithMany().HasForeignKey("CategoryId").OnDelete(DeleteBehavior.Cascade),
                    l => l.HasOne<Manga>().WithMany().HasForeignKey("MangaId").OnDelete(DeleteBehavior.Cascade),
                    je =>
                    {
                        je.HasKey("MangaId", "CategoryId");
                    });
                
            // MangaTrack -> Manga (One-to-Many)
            modelBuilder.Entity<MangaTrack>()
                .HasOne(t => t.Manga)
                .WithMany(m => m.Tracks)
                .HasForeignKey(t => t.MangaId)
                .OnDelete(DeleteBehavior.Cascade);

            // Performance & Lookup Indexes
            modelBuilder.Entity<Manga>()
                .HasIndex(m => new { m.Source, m.Url })
                .HasDatabaseName("IX_Mangas_Source_Url");
            modelBuilder.Entity<Manga>()
                .HasIndex(m => m.Favorite)
                .HasDatabaseName("IX_Mangas_Favorite");
            modelBuilder.Entity<Chapter>()
                .HasIndex(c => new { c.MangaId, c.Url })
                .HasDatabaseName("IX_Chapters_MangaId_Url");
            modelBuilder.Entity<Chapter>()
                .HasIndex(c => c.DateFetch)
                .HasDatabaseName("IX_Chapters_DateFetch");
            modelBuilder.Entity<History>()
                .HasIndex(h => new { h.MangaId, h.LastRead })
                .HasDatabaseName("IX_History_MangaId_LastRead");

            base.OnModelCreating(modelBuilder);
        }
    }

    public class SqlitePragmaInterceptor : DbConnectionInterceptor
    {
        // busy_timeout MUST be configured FIRST before journal_mode=WAL so parallel burst opens never throw SQLITE_BUSY
        internal const string PragmaSql = "PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-10000; PRAGMA temp_store=MEMORY;";

        private readonly string _dbPath;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbConnection, object> _activeLeases = new();

        public SqlitePragmaInterceptor() : this(string.Empty) { }

        public SqlitePragmaInterceptor(string dbPath)
        {
            _dbPath = dbPath;
        }

        private static void AttachLease(DbConnection connection, object lease)
        {
            lock (_activeLeases)
            {
                if (_activeLeases.TryGetValue(connection, out var existing))
                {
                    // Release any stale lease if connection was reused
                    (existing as System.Action)?.Invoke();
                    _activeLeases.Remove(connection);
                }
                _activeLeases.Add(connection, lease);
            }
        }

        private static void ReleaseLease(DbConnection connection)
        {
            object? lease = null;
            lock (_activeLeases)
            {
                if (_activeLeases.TryGetValue(connection, out lease))
                {
                    _activeLeases.Remove(connection);
                }
            }
            if (lease is System.Action releaseAction)
            {
                releaseAction();
            }
        }

        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            var lease = MangaDbContext.AcquireConnectionSlotSync(_dbPath);
            AttachLease(connection, (System.Action)lease.ReleaseOnce);
            return base.ConnectionOpening(connection, eventData, result);
        }

        public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            var lease = await MangaDbContext.AcquireConnectionSlotAsync(_dbPath, cancellationToken).ConfigureAwait(false);
            AttachLease(connection, (System.Action)lease.ReleaseOnce);
            return await base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken).ConfigureAwait(false);
        }

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            using var command = connection.CreateCommand();
            command.CommandText = PragmaSql;
            command.ExecuteNonQuery();
        }

        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            using var command = connection.CreateCommand();
            command.CommandText = PragmaSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public override void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData)
        {
            ReleaseLease(connection);
            base.ConnectionClosed(connection, eventData);
        }

        public override Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
        {
            ReleaseLease(connection);
            return base.ConnectionClosedAsync(connection, eventData);
        }

        public override void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData)
        {
            ReleaseLease(connection);
            base.ConnectionFailed(connection, eventData);
        }

        public override Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            ReleaseLease(connection);
            return base.ConnectionFailedAsync(connection, eventData, cancellationToken);
        }
    }
}
