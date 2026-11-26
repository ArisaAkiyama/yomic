using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Yomic.Core.Data;
using Yomic.Core.Services;
using Yomic.Core.Sources;
using YomicDesktop.Interop;

namespace YomicDesktop;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\Yomic_SingleInstance_Mutex";
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool isBackgroundUpdate = e.Args.Any(a => string.Equals(a, "--background-update", StringComparison.OrdinalIgnoreCase));
        bool isUiCapture = e.Args.Any(a =>
            string.Equals(a, "--capture-ui", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--capture-dark-ui", StringComparison.OrdinalIgnoreCase));

        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out _ownsMutex);
        }
        catch
        {
            _ownsMutex = false;
        }

        if (isBackgroundUpdate)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            if (!_ownsMutex)
            {
                // Another instance (e.g., interactive app) is already running; skip headless sync.
                Shutdown(0);
                return;
            }

            try
            {
                await RunHeadlessLibraryUpdateAsync();
            }
            catch (Exception ex)
            {
                LogService.Error("BackgroundUpdate", "Error running headless background update", ex);
            }
            finally
            {
                try { LogService.CloseAndFlush(); } catch { }
                ReleaseSingleInstanceMutex();
                Shutdown(0);
            }
            return;
        }

        if (!_ownsMutex && !isUiCapture)
        {
            ActivateExistingYomicInstance();
            Shutdown(0);
            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private static async Task RunHeadlessLibraryUpdateAsync()
    {
        LogService.Info("BackgroundUpdate", "Starting scheduled headless library sync...");
        using (var initDb = new MangaDbContext())
        {
            initDb.Database.EnsureCreated();
            MangaDbContext.EnsureMangaColumnsExist(initDb);
        }

        using var settingsService = new SettingsService();
        HttpSource.IsOfflineModeProvider = () => settingsService.IsOfflineMode;
        HttpSource.DnsOverHttpsProviderGetter = () => settingsService.DnsOverHttpsProvider;
        JsMangaSource.SelectedLanguage = string.Equals(settingsService.MangaDexLanguage, "id", StringComparison.OrdinalIgnoreCase) ? "id" : "en";

        if (settingsService.IsOfflineMode)
        {
            LogService.Info("BackgroundUpdate", "Skipped headless sync because Offline Mode is enabled.");
            return;
        }

        using var sourceManager = new SourceManager();

        var libraryService = new LibraryService();
        int updated = await libraryService.UpdateAllLibraryMangaAsync(sourceManager, forceRefresh: false);
        LogService.Info("BackgroundUpdate", $"Completed scheduled headless library sync ({updated} new chapters found).");
    }

    private static void ActivateExistingYomicInstance()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            foreach (var proc in Process.GetProcessesByName(current.ProcessName))
            {
                using (proc)
                {
                    if (proc.Id != current.Id && proc.MainWindowHandle != IntPtr.Zero)
                    {
                        NativeMethods.ActivateExistingWindow(proc.MainWindowHandle);
                        break;
                    }
                }
            }
        }
        catch
        {
            // Ignore process enumeration errors
        }
    }

    public static void ReleaseSingleInstanceMutexForRestart()
    {
        try
        {
            if (Current is App app)
            {
                if (app.Dispatcher.CheckAccess())
                {
                    app.ReleaseSingleInstanceMutex();
                }
                else
                {
                    app.Dispatcher.Invoke(app.ReleaseSingleInstanceMutex, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2));
                }
            }
        }
        catch { }
    }

    public static bool TryReacquireSingleInstanceMutex()
    {
        try
        {
            if (Current is App app)
            {
                bool Reacquire()
                {
                    if (app._singleInstanceMutex == null)
                    {
                        app._singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out app._ownsMutex);
                        return app._ownsMutex;
                    }
                    return app._ownsMutex;
                }

                return app.Dispatcher.CheckAccess()
                    ? Reacquire()
                    : app.Dispatcher.Invoke(Reacquire, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2));
            }
        }
        catch { }
        return false;
    }

    private void ReleaseSingleInstanceMutex()
    {
        if (_singleInstanceMutex != null)
        {
            try
            {
                if (_ownsMutex)
                {
                    _singleInstanceMutex.ReleaseMutex();
                    _ownsMutex = false;
                }
            }
            catch { }

            try
            {
                _singleInstanceMutex.Dispose();
            }
            catch { }
            _singleInstanceMutex = null;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (CloudflareBypassService.IsInstanceCreated)
        {
            try
            {
                Task.Run(() => CloudflareBypassService.Instance.DisposeAsync()).Wait(TimeSpan.FromSeconds(2));
            }
            catch { }
        }
        ReleaseSingleInstanceMutex();
        base.OnExit(e);
    }
}
