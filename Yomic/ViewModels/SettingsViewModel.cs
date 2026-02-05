using ReactiveUI;
using System.Reactive;
using System;
using Avalonia;
using System.Reactive.Linq;

namespace Yomic.ViewModels
{
    public class SettingsViewModel : ViewModelBase
    {
        public string AppVersion => $"v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";

        private readonly MainWindowViewModel _mainViewModel;

        // Event to request showing the update dialog
        public event Action? RequestUpdateDialog;

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

        // Security
        private bool _secureScreen;
        public bool SecureScreen
        {
            get => _secureScreen;
            set => this.RaiseAndSetIfChanged(ref _secureScreen, value);
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
        
        public ReactiveCommand<Unit, Unit> ClearAllDataCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearReadHistoryCommand { get; }
        public ReactiveCommand<Unit, Unit> CheckForUpdatesCommand { get; }
        public ReactiveCommand<Unit, Unit> VisitWebsiteCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenTwitterCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenGitHubCommand { get; }
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

        public SettingsViewModel(MainWindowViewModel mainViewModel, Core.Services.LibraryService libraryService, Core.Services.SettingsService settingsService, Core.Services.SourceManager sourceManager)
        {
            _mainViewModel = mainViewModel;
            _libraryService = libraryService;
            _settingsService = settingsService;
            _sourceManager = sourceManager;
            _updateService = new Core.Services.UpdateService();
            
            // Load settings
            _isDarkMode = _settingsService.IsDarkMode;
            _isOfflineMode = _settingsService.IsOfflineMode;
            _secureScreen = _settingsService.SecureScreen;
            _updateOnStart = _settingsService.UpdateOnStart;
            _checkAppUpdateOnStart = _settingsService.CheckAppUpdateOnStart;
            
            ClearAllDataCommand = ReactiveCommand.CreateFromTask(ClearAllDataAsync);
            ClearReadHistoryCommand = ReactiveCommand.CreateFromTask(ClearReadHistoryAsync);
            CheckForUpdatesCommand = ReactiveCommand.Create(CheckForUpdates);
            VisitWebsiteCommand = ReactiveCommand.Create(VisitWebsite);
            OpenTwitterCommand = ReactiveCommand.Create(OpenTwitter);
            OpenGitHubCommand = ReactiveCommand.Create(OpenGitHub);
            SyncLibraryCommand = ReactiveCommand.CreateFromTask(SyncLibraryAsync);
            
            // Auto-save on property change
            this.WhenAnyValue(x => x.IsDarkMode)
                .Subscribe(x => { 
                    _settingsService.IsDarkMode = x; 
                    _settingsService.Save(); 
                    if (Application.Current != null)
                    {
                        Application.Current.RequestedThemeVariant = x ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
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
            this.WhenAnyValue(x => x.SecureScreen)
                .Subscribe(x => { _settingsService.SecureScreen = x; _settingsService.Save(); });
            this.WhenAnyValue(x => x.UpdateOnStart)
                .Subscribe(x => { _settingsService.UpdateOnStart = x; _settingsService.Save(); });
            
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

        private async System.Threading.Tasks.Task ClearAllDataAsync()
        {
            try
            {
                // 1. Clear Library (DB + Downloads + Covers)
                await _libraryService.ClearDatabaseAsync();
                
                // 2. Clear Extensions (Plugins + JSON + Cache)
                _sourceManager.ClearAllExtensions();

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

        private async System.Threading.Tasks.Task ClearReadHistoryAsync()
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
    }
}
