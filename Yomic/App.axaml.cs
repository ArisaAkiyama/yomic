using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Yomic.ViewModels;
using Yomic.Views;
using Yomic.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Yomic
{
    public partial class App : Application
    {
        public static SettingsService? SettingsService { get; private set; }
        public App()
        {
            // Catch unhandled exceptions
            System.AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                if (args.ExceptionObject as System.Exception is System.Exception ex)
                {
                    Yomic.Core.Services.LogService.Error("Global", "Unhandled Exception", ex);
                }
            };

            // Catch unobserved task exceptions
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                Yomic.Core.Services.LogService.Error("Global", "Unobserved Task Exception", args.Exception);
                args.SetObserved(); // Prevent app crash
            };
        }

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Run Source ID migration before loading extensions
                // This ensures old hardcoded IDs are updated to new hash-based IDs
                Core.Services.SourceIdMigrationService.RunMigrationIfNeeded();
                
                var sourceManager = new Core.Services.SourceManager();
                // Load persisted JS extensions - auto-loaded in constructor.
                
                var settingsService = new Core.Services.SettingsService();
                SettingsService = settingsService;
                var libraryService = new Core.Services.LibraryService();
                var networkService = new Core.Services.NetworkService(settingsService);
                var downloadService = new Core.Services.DownloadService(sourceManager, libraryService, networkService);
                var imageCacheService = new Core.Services.ImageCacheService();
                var secureImageService = new Core.Services.SecureImageService(networkService, imageCacheService);
                
                // Static Injection for Attached Property
                Yomic.Views.Helpers.SecureImageLoader.Service = secureImageService;
                
                // Apply Theme
                RequestedThemeVariant = settingsService.IsDarkMode ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
                
                // Ensure Database is Created
                using (var context = new Core.Data.MangaDbContext())
                {
                    try
                    {
                        context.Database.Migrate();
                        System.Diagnostics.Debug.WriteLine($"[App] Database migrated successfully.");
                    }
                    catch (System.Exception ex)
                    {
                        // 1. Handle "Duplicate Column" (Migration mismatch)
                        if (ex.Message.Contains("duplicate column name") && ex.Message.Contains("LastViewed"))
                        {
                            System.Diagnostics.Debug.WriteLine($"[App] 'LastViewed' column exists. Syncing migration history...");
                            try
                            {
                                string fixHistory = "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('20260123075531_AddLastViewedToManga', '9.0.0');";
                                context.Database.ExecuteSqlRaw(fixHistory);
                            }
                            catch { /* Ignore if already in history */ }
                        }
                        // 2. Handle "Table Already Exists" (InitialCreate mismatch)
                        else if (ex.Message.Contains("already exists"))
                        {
                            System.Diagnostics.Debug.WriteLine($"[App] Migration conflict logic...");
                            try
                            {
                                string insertSql = "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('20260121005426_InitialCreate', '9.0.0');";
                                context.Database.ExecuteSqlRaw(insertSql);
                                
                                // Retry migration
                                context.Database.Migrate();
                                System.Diagnostics.Debug.WriteLine($"[App] Database repaired.");
                            }
                            catch (System.Exception recoverEx) 
                            {
                                // If retry fails specifically due to LastViewed
                                if (recoverEx.Message.Contains("duplicate column name"))
                                {
                                     try
                                     {
                                         string fixHistory = "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('20260123075531_AddLastViewedToManga', '9.0.0');";
                                         context.Database.ExecuteSqlRaw(fixHistory);
                                         System.Diagnostics.Debug.WriteLine($"[App] Recovered from duplicate column error.");
                                     }
                                     catch {}
                                }
                                else
                                {
                                    System.Diagnostics.Debug.WriteLine($"[App] Failed to recover DB: {recoverEx.Message}");
                                }
                            }
                        }
                    }
                }

                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(sourceManager, libraryService, networkService, downloadService, settingsService, imageCacheService, secureImageService),
                };
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
