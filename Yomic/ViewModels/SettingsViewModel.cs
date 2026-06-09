using ReactiveUI;
using System.Reactive;
using System;
using System.IO;
using System.Linq;
using Avalonia;
using System.Reactive.Linq;
using System.Threading.Tasks;
namespace Yomic.ViewModels
{
    public class SettingsViewModel : ViewModelBase
    {
        public string AppVersion => $"v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";

        private readonly MainWindowViewModel _mainViewModel;

        // Event to request showing the update dialog
        public event Action? RequestUpdateDialog;

        // Events for Backup/Restore File Dialogs
        public event Action? RequestBackupDialog;
        public event Action? RequestRestoreDialog;
        public event Action? RequestClearDataDialog;
        public event Action? RequestClearHistoryDialog;

        // General
        private bool _isDarkMode = true;
        public bool IsDarkMode
        {
            get => _isDarkMode;
            set => this.RaiseAndSetIfChanged(ref _isDarkMode, value);
        }

        private bool _isOfflineMode;
        public bool IsOfflineMode
        {
            get => _isOfflineMode;
            set => this.RaiseAndSetIfChanged(ref _isOfflineMode, value);
        }

        private bool _checkAppUpdateOnStart;
        public bool CheckAppUpdateOnStart
        {
            get => _checkAppUpdateOnStart;
            set => this.RaiseAndSetIfChanged(ref _checkAppUpdateOnStart, value);
        }

        private bool _showNsfwSources;
        public bool ShowNsfwSources
        {
            get => _showNsfwSources;
            set => this.RaiseAndSetIfChanged(ref _showNsfwSources, value);
        }

        private int _dnsOverHttpsProvider;
        public int DnsOverHttpsProvider
        {
            get => _dnsOverHttpsProvider;
            set => this.RaiseAndSetIfChanged(ref _dnsOverHttpsProvider, value);
        }

        // Security
        private bool _secureScreen;
        public bool SecureScreen
        {
            get => _secureScreen;
            set => this.RaiseAndSetIfChanged(ref _secureScreen, value);
        }

        // QoL Settings
        private bool _preloadNextChapter;
        public bool PreloadNextChapter
        {
            get => _preloadNextChapter;
            set => this.RaiseAndSetIfChanged(ref _preloadNextChapter, value);
        }

        private int _maxCacheSizeIndex;
        public int MaxCacheSizeIndex
        {
            get => _maxCacheSizeIndex;
            set => this.RaiseAndSetIfChanged(ref _maxCacheSizeIndex, value);
        }

        // Library
        private bool _updateOnStart;
        public bool UpdateOnStart
        {
            get => _updateOnStart;
            set => this.RaiseAndSetIfChanged(ref _updateOnStart, value);
        }

        // VPN Bypass
        private bool _isVpnEnabled;
        public bool IsVpnEnabled
        {
            get => _isVpnEnabled;
            set => this.RaiseAndSetIfChanged(ref _isVpnEnabled, value);
        }

        private bool _isVpnConnected;
        public bool IsVpnConnected
        {
            get => _isVpnConnected;
            set => this.RaiseAndSetIfChanged(ref _isVpnConnected, value);
        }

        private string _vpnStatus = "Disconnected";
        public string VpnStatus
        {
            get => _vpnStatus;
            set => this.RaiseAndSetIfChanged(ref _vpnStatus, value);
        }

        private double _vpnDownloadProgress;
        public double VpnDownloadProgress
        {
            get => _vpnDownloadProgress;
            set => this.RaiseAndSetIfChanged(ref _vpnDownloadProgress, value);
        }

        private bool _isVpnDownloading;
        public bool IsVpnDownloading
        {
            get => _isVpnDownloading;
            set => this.RaiseAndSetIfChanged(ref _isVpnDownloading, value);
        }

        public string VpnButtonText => IsVpnConnected ? "Disconnect" : "Connect";

        private readonly Core.Services.LibraryService _libraryService;
        private readonly Core.Services.SettingsService _settingsService;
        private readonly Core.Services.SourceManager _sourceManager;
        private readonly Core.Services.NetworkService _networkService;
        private readonly Core.Services.BackupService _backupService;
        
        public ReactiveCommand<Unit, Unit> ClearAllDataCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearReadHistoryCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearCacheCookiesCommand { get; }
        public ReactiveCommand<Unit, Unit> BackupDataCommand { get; }
        public ReactiveCommand<Unit, Unit> RestoreDataCommand { get; }
        public ReactiveCommand<Unit, Unit> CheckForUpdatesCommand { get; }
        public ReactiveCommand<Unit, Unit> VisitWebsiteCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenTwitterCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenGitHubCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenFacebookCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenInstagramCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenTikTokCommand { get; }
        public ReactiveCommand<Unit, Unit> ToggleVpnCommand { get; }
        public ReactiveCommand<Unit, Unit> SyncLibraryCommand { get; }

        private readonly Core.Services.UpdateService _updateService;

        private async void CheckForUpdates()
        {
             _mainViewModel.ShowNotification("Checking for updates...", NotificationType.Info);
             try
             {
                 var updateInfo = await _updateService.CheckForUpdatesAsync();
                 if (updateInfo.IsUpdateAvailable)
                 {
                     _mainViewModel.ShowNotification($"Update Available: {updateInfo.LatestVersion}", NotificationType.Success);
                     
                     // Trigger Dialog
                     Avalonia.Threading.Dispatcher.UIThread.Post(() => RequestUpdateDialog?.Invoke());
                 }
                 else
                 {
                     _mainViewModel.ShowNotification("You are using the latest version.", NotificationType.Success);
                 }
             }
             catch (Exception ex)
             {
                 _mainViewModel.ShowNotification($"Update check failed: {ex.Message}", NotificationType.Error);
             }
        }

        private void VisitWebsite()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://github.com/ArisaAkiyama/yomic",
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                 _mainViewModel.ShowNotification($"Could not open website: {ex.Message}");
            }
        }

        private void OpenTwitter()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "https://x.com/ArisaAkiyama", UseShellExecute = true });
            }
            catch (Exception ex)
            {
                 _mainViewModel.ShowNotification($"Could not open Twitter: {ex.Message}");
            }
        }

        private void OpenGitHub()
        {
            try
            {
                 System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "https://github.com/ArisaAkiyama", UseShellExecute = true });
            }
            catch (Exception ex)
            {
                 _mainViewModel.ShowNotification($"Could not open GitHub: {ex.Message}");
            }
        }

        private void OpenFacebook()
        {
            try
            {
                 System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "https://web.facebook.com/febianrizaarzewiniga", UseShellExecute = true });
            }
            catch (Exception ex)
            {
                 _mainViewModel.ShowNotification($"Could not open Facebook: {ex.Message}");
            }
        }

        private void OpenInstagram()
        {
            try
            {
                 System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "https://www.instagram.com/febianriza.a/", UseShellExecute = true });
            }
            catch (Exception ex)
            {
                 _mainViewModel.ShowNotification($"Could not open Instagram: {ex.Message}");
            }
        }

        private void OpenTikTok()
        {
            try
            {
                 System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "https://www.tiktok.com/@arisaakiyama", UseShellExecute = true });
            }
            catch (Exception ex)
            {
                 _mainViewModel.ShowNotification($"Could not open TikTok: {ex.Message}");
            }
        }

        public SettingsViewModel(MainWindowViewModel mainViewModel, Core.Services.LibraryService libraryService, Core.Services.SettingsService settingsService, Core.Services.SourceManager sourceManager, Core.Services.NetworkService networkService)
        {
            _mainViewModel = mainViewModel;
            _libraryService = libraryService;
            _settingsService = settingsService;
            _sourceManager = sourceManager;
            _networkService = networkService;
            _updateService = new Core.Services.UpdateService();
            _backupService = new Core.Services.BackupService();
            
            // Load settings
            _isDarkMode = _settingsService.IsDarkMode;
            _isOfflineMode = _settingsService.IsOfflineMode;
            _secureScreen = _settingsService.SecureScreen;
            _updateOnStart = _settingsService.UpdateOnStart;
            _checkAppUpdateOnStart = _settingsService.CheckAppUpdateOnStart;
            _showNsfwSources = _settingsService.ShowNsfwSources;
            _dnsOverHttpsProvider = _settingsService.DnsOverHttpsProvider;
            _preloadNextChapter = _settingsService.PreloadNextChapter;
            _maxCacheSizeIndex = _settingsService.MaxCacheSizeMb switch
            {
                250 => 1,
                500 => 2,
                1000 => 3,
                2000 => 4,
                _ => 0
            };
            
            ClearAllDataCommand = ReactiveCommand.Create(() => RequestClearDataDialog?.Invoke());
            ClearReadHistoryCommand = ReactiveCommand.Create(() => RequestClearHistoryDialog?.Invoke());
            ClearCacheCookiesCommand = ReactiveCommand.CreateFromTask(ClearCacheCookiesAsync);
            CheckForUpdatesCommand = ReactiveCommand.Create(CheckForUpdates);
            BackupDataCommand = ReactiveCommand.Create(RequestBackup);
            RestoreDataCommand = ReactiveCommand.Create(RequestRestore);
            VisitWebsiteCommand = ReactiveCommand.Create(VisitWebsite);
            OpenTwitterCommand = ReactiveCommand.Create(OpenTwitter);
            OpenGitHubCommand = ReactiveCommand.Create(OpenGitHub);
            OpenFacebookCommand = ReactiveCommand.Create(OpenFacebook);
            OpenInstagramCommand = ReactiveCommand.Create(OpenInstagram);
            OpenTikTokCommand = ReactiveCommand.Create(OpenTikTok);
            SyncLibraryCommand = ReactiveCommand.CreateFromTask(SyncLibraryAsync);
            
            this.WhenAnyValue(x => x.IsDarkMode)
                .Subscribe(x => { 
                    _settingsService.IsDarkMode = x; 
                    _settingsService.Save(); 
                    if (Application.Current != null)
                    {
                        if (_mainViewModel.RequestThemeChange != null)
                        {
                            _mainViewModel.RequestThemeChange.Invoke(x);
                        }
                        else
                        {
                            Application.Current.RequestedThemeVariant = x ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
                        }
                    }
                });
            this.WhenAnyValue(x => x.IsOfflineMode)
                .Skip(1)
                .Subscribe(x => { 
                    _settingsService.IsOfflineMode = x; 
                    _settingsService.Save();
                    
                    if (x) _mainViewModel.ShowNotification("Offline Mode Enabled", NotificationType.Error);
                    else _mainViewModel.ShowNotification("Offline Mode Disabled", NotificationType.Success);
                });
            this.WhenAnyValue(x => x.CheckAppUpdateOnStart)
                .Subscribe(x => { _settingsService.CheckAppUpdateOnStart = x; _settingsService.Save(); });
            this.WhenAnyValue(x => x.ShowNsfwSources)
                .Subscribe(x => { _settingsService.ShowNsfwSources = x; _settingsService.Save(); });
            this.WhenAnyValue(x => x.DnsOverHttpsProvider)
                .Subscribe(x => { _settingsService.DnsOverHttpsProvider = x; _settingsService.Save(); });
            this.WhenAnyValue(x => x.SecureScreen)
                .Subscribe(x => { _settingsService.SecureScreen = x; _settingsService.Save(); });
            this.WhenAnyValue(x => x.UpdateOnStart)
                .Subscribe(x => { _settingsService.UpdateOnStart = x; _settingsService.Save(); });
            this.WhenAnyValue(x => x.PreloadNextChapter)
                .Subscribe(x => { _settingsService.PreloadNextChapter = x; _settingsService.Save(); });
            this.WhenAnyValue(x => x.MaxCacheSizeIndex)
                .Subscribe(x => {
                    int mbValue = x switch
                    {
                        1 => 250,
                        2 => 500,
                        3 => 1000,
                        4 => 2000,
                        _ => 0
                    };
                    _settingsService.MaxCacheSizeMb = mbValue;
                    _settingsService.Save();
                    System.Threading.Tasks.Task.Run(() => CleanupReaderCache(mbValue));
                });
            
            // VPN Toggle Command
            ToggleVpnCommand = ReactiveCommand.CreateFromTask(ToggleVpnAsync);
            
            // Subscribe to SingboxService status changes
            Core.Services.SingboxService.Instance.StatusChanged += (isConnected) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    IsVpnConnected = isConnected;
                    VpnStatus = isConnected ? "Connected" : "Disconnected";
                });
            };
            
            // Load initial VPN state
            IsVpnConnected = Core.Services.SingboxService.Instance.IsRunning;
            VpnStatus = IsVpnConnected ? "Connected" : "Disconnected";
        }

        private async System.Threading.Tasks.Task ToggleVpnAsync()
        {
            var singbox = Core.Services.SingboxService.Instance;
            
            if (singbox.IsRunning)
            {
                VpnStatus = "Disconnecting...";
                singbox.Stop();
                IsVpnEnabled = false;
                _mainViewModel.ShowNotification("VPN Bypass Disabled", NotificationType.Info);
            }
            else
            {
                VpnStatus = "Connecting...";
                IsVpnDownloading = true;
                
                var progress = new Progress<double>(p =>
                {
                    VpnDownloadProgress = p;
                    VpnStatus = $"Downloading... {(int)(p * 100)}%";
                });
                
                var success = await singbox.StartAsync();
                IsVpnDownloading = false;
                VpnDownloadProgress = 0;
                
                if (success)
                {
                    IsVpnEnabled = true;
                    _mainViewModel.ShowNotification("VPN Bypass Enabled! Restart sources to apply.", NotificationType.Success);
                }
                else
                {
                    VpnStatus = "Connection Failed";
                    _mainViewModel.ShowNotification("Failed to start VPN. Check logs.", NotificationType.Error);
                }
            }
        }

        private void RequestBackup()
        {
            RequestBackupDialog?.Invoke();
        }

        private void RequestRestore()
        {
            RequestRestoreDialog?.Invoke();
        }

        private async Task ClearCacheCookiesAsync()
        {
            _mainViewModel.ShowNotification("Clearing cache and connections...", NotificationType.Info);
            
            // Clear image cache
            _mainViewModel.ImageCacheService.Clear();
            
            // Reset network connections and DNS cache
            await _networkService.ResetConnectionsAsync();
            
            // Call GC to aggressively free memory held by bitmaps
            GC.Collect();
            
            _mainViewModel.ShowNotification("Cache & Cookies cleared successfully!", NotificationType.Success);
        }

        public async System.Threading.Tasks.Task ProcessBackupAsync(string path)
        {
            _mainViewModel.ShowNotification("Creating backup...", NotificationType.Info);
            bool success = await _backupService.CreateBackupAsync(path);
            if (success)
            {
                _mainViewModel.ShowNotification("Backup created successfully!", NotificationType.Success);
            }
            else
            {
                _mainViewModel.ShowNotification("Failed to create backup.", NotificationType.Error);
            }
        }

        public async System.Threading.Tasks.Task ProcessRestoreAsync(string path)
        {
            _mainViewModel.ShowNotification("Restoring backup...", NotificationType.Info);
            bool success = await _backupService.RestoreBackupAsync(path);
            if (success)
            {
                _mainViewModel.ShowNotification("Restore completed! Please restart the app manually.", NotificationType.Success);
            }
            else
            {
                _mainViewModel.ShowNotification("Failed to restore backup.", NotificationType.Error);
            }
        }

        public async System.Threading.Tasks.Task ProcessClearDataAsync()
        {
            try
            {
                // 1. Clear Library (DB + Downloads + Covers)
                await _libraryService.ClearDatabaseAsync();
                
                // 2. Clear Extensions (Plugins + JSON + Cache)
                _sourceManager.ClearAllCache();

                // 3. Clear Settings
                _settingsService.Reset();

                // Show notification via MainViewModel
                _mainViewModel.ShowNotification("All data cleared successfully. Application will close in 3 seconds.");
                
                // Wait and Exit
                await System.Threading.Tasks.Task.Delay(3000);
                System.Environment.Exit(0);
            }
            catch (System.Exception ex)
            {
                 _mainViewModel.ShowNotification($"Error clearing data: {ex.Message}");
            }
        }

        public async System.Threading.Tasks.Task ProcessClearHistoryAsync()
        {
            try
            {
                // Clear all chapter read status from database
                await _libraryService.ClearAllReadHistoryAsync();
                _mainViewModel.ShowNotification("Read history cleared successfully!", NotificationType.Success);
            }
            catch (System.Exception ex)
            {
                _mainViewModel.ShowNotification($"Error clearing read history: {ex.Message}");
            }
        }
        private bool _isSyncingLibrary;
        public bool IsSyncingLibrary
        {
            get => _isSyncingLibrary;
            set => this.RaiseAndSetIfChanged(ref _isSyncingLibrary, value);
        }

        private async System.Threading.Tasks.Task SyncLibraryAsync()
        {
            if (IsSyncingLibrary) return;
            IsSyncingLibrary = true;
            _mainViewModel.ShowNotification("Syncing library chapters...", NotificationType.Info);

            try
            {
                int newCount = await _libraryService.UpdateAllLibraryMangaAsync(_sourceManager);
                if (newCount > 0)
                    _mainViewModel.ShowNotification($"Library Sync Complete: Found {newCount} new chapters!", NotificationType.Success);
                else
                    _mainViewModel.ShowNotification("Library Sync Complete: No new chapters found.", NotificationType.Info);
            }
            catch (Exception ex)
            {
                _mainViewModel.ShowNotification($"Sync failed: {ex.Message}", NotificationType.Error);
            }
            finally
            {
                 IsSyncingLibrary = false;
            }
        }

        public static void CleanupReaderCache(int maxCacheSizeMb)
        {
            if (maxCacheSizeMb <= 0) return; // Disabled

            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var cacheDir = Path.Combine(appData, "Yomic", "Cache", "Reader");
                if (!Directory.Exists(cacheDir)) return;

                var di = new DirectoryInfo(cacheDir);
                var files = di.GetFiles("*.cache");
                if (files.Length == 0) return;

                // Sort by last write time ascending (oldest first)
                var sortedFiles = files.OrderBy(f => f.LastWriteTime).ToList();

                long currentSize = sortedFiles.Sum(f => f.Length);
                long maxSize = (long)maxCacheSizeMb * 1024 * 1024;

                if (currentSize > maxSize)
                {
                    long deletedSize = 0;
                    int deletedCount = 0;
                    foreach (var file in sortedFiles)
                    {
                        if (currentSize - deletedSize <= maxSize)
                            break;

                        try
                        {
                            long len = file.Length;
                            file.Delete();
                            deletedSize += len;
                            deletedCount++;
                        }
                        catch
                        {
                            // File might be locked/in use, skip it
                        }
                    }
                    System.Diagnostics.Debug.WriteLine($"[CacheCleanup] Removed {deletedCount} files ({(deletedSize / (1024.0 * 1024.0)):F2} MB). Current size: {((currentSize - deletedSize) / (1024.0 * 1024.0)):F2} MB.");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CacheCleanup] Error cleaning cache: {ex.Message}");
            }
        }
    }
}
