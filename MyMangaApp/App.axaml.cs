using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MyMangaApp.ViewModels;
using MyMangaApp.Views;
using Microsoft.EntityFrameworkCore;

namespace MyMangaApp
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var sourceManager = new Core.Services.SourceManager();
                // Load persisted extensions (DLLs)
                // Load persisted extensions (DLLs)
                sourceManager.LoadExtensions();
                
                var settingsService = new Core.Services.SettingsService();
                var libraryService = new Core.Services.LibraryService();
                var networkService = new Core.Services.NetworkService(settingsService);
                var downloadService = new Core.Services.DownloadService(sourceManager, libraryService, networkService);
                var imageCacheService = new Core.Services.ImageCacheService();
                
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
                        // Handle case where DB was created with EnsureCreated() (missing migration history)
                        // but tables exist.
                        if (ex.Message.Contains("already exists"))
                        {
                            System.Diagnostics.Debug.WriteLine($"[App] Migration conflict detected. Attempting to fix history...");
                            try
                            {
                                // Manually insert the InitialCreate record
                                // Requires 'Migrations' folder to contain '20260121005426_InitialCreate'
                                string insertSql = "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('20260121005426_InitialCreate', '9.0.0');";
                                context.Database.ExecuteSqlRaw(insertSql);
                                
                                // Retry migration for subsequent updates (like AddLastViewed)
                                context.Database.Migrate();
                                System.Diagnostics.Debug.WriteLine($"[App] Database repaired and migrated.");
                            }
                            catch (System.Exception recoverEx) 
                            { 
                                System.Diagnostics.Debug.WriteLine($"[App] Failed to recover DB: {recoverEx.Message}");
                                // If recovery fails, we might just have to swallow it if the app still runs?
                                // Or throw to let user know.
                            }
                        }
                        else
                        {
                            // Other errors: log or rethrow
                            System.Diagnostics.Debug.WriteLine($"[App] DB Error: {ex}");
                        }
                    }
                }

                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(sourceManager, libraryService, networkService, downloadService, settingsService, imageCacheService),
                };
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}