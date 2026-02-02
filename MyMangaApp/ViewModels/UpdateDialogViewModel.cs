using ReactiveUI;
using System.Reactive;
using System;
using System.Threading.Tasks;

namespace MyMangaApp.ViewModels
{
    public class UpdateDialogViewModel : ViewModelBase
    {
        private readonly Core.Services.UpdateService _updateService;

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => this.RaiseAndSetIfChanged(ref _isLoading, value);
        }

        private bool _isUpdateAvailable;
        public bool IsUpdateAvailable
        {
            get => _isUpdateAvailable;
            set => this.RaiseAndSetIfChanged(ref _isUpdateAvailable, value);
        }

        private string _statusText = "Checking for updates...";
        public string StatusText
        {
            get => _statusText;
            set => this.RaiseAndSetIfChanged(ref _statusText, value);
        }

        private string _latestVersion = "";
        public string LatestVersion
        {
            get => _latestVersion;
            set => this.RaiseAndSetIfChanged(ref _latestVersion, value);
        }

        private string _releaseNotes = "";
        public string ReleaseNotes
        {
            get => _releaseNotes;
            set => this.RaiseAndSetIfChanged(ref _releaseNotes, value);
        }

        private string _downloadUrl = "";

        // UI Helpers
        private string _statusIcon = "\uE777"; // Sync icon
        public string StatusIcon
        {
            get => _statusIcon;
            set => this.RaiseAndSetIfChanged(ref _statusIcon, value);
        }

        private string _statusColor = "#FF9900"; // Orange
        public string StatusColor
        {
            get => _statusColor;
            set => this.RaiseAndSetIfChanged(ref _statusColor, value);
        }

        public ReactiveCommand<Unit, Unit> DownloadCommand { get; }
        public ReactiveCommand<Unit, Unit> CloseCommand { get; }
        
        // Action to close the window
        public Action? CloseAction { get; set; }

        public UpdateDialogViewModel(Core.Services.UpdateService? updateService = null)
        {
            _updateService = updateService ?? new Core.Services.UpdateService();

            DownloadCommand = ReactiveCommand.Create(DownloadUpdate);
            CloseCommand = ReactiveCommand.Create(() => CloseAction?.Invoke());

            // Start check automatically
            _ = CheckForUpdatesAsync();
        }

        public async Task CheckForUpdatesAsync()
        {
            IsLoading = true;
            StatusText = "Checking for updates...";
            StatusIcon = "\uE895"; // Sync
            StatusColor = "#FF9900"; // Orange
            IsUpdateAvailable = false;

            try
            {
                // Artificial delay for better UX (so it doesn't flash too fast)
                await Task.Delay(1500);

                var info = await _updateService.CheckForUpdatesAsync();
                
                if (info.IsUpdateAvailable)
                {
                    IsUpdateAvailable = true;
                    LatestVersion = info.LatestVersion;
                    ReleaseNotes = info.ReleaseNotes;
                    _downloadUrl = info.DownloadUrl;
                    StatusText = "New Update Available!";
                    StatusIcon = "\uE74E"; // Download Global
                    StatusColor = "#00C853"; // Green (Success)
                }
                else
                {
                    IsUpdateAvailable = false;
                    StatusText = "You're up to date";
                    StatusIcon = "\uE930"; // Checkmark
                    StatusColor = "#A6ADC8"; // Subtle
                    ReleaseNotes = $"Version {info.LatestVersion} is currently installed.";
                }
            }
            catch (Exception ex)
            {
                StatusText = "Update Failed";
                StatusIcon = "\uE783"; // Warning
                StatusColor = "#FF5555"; // Red
                ReleaseNotes = $"Error: {ex.Message}";
                IsUpdateAvailable = false;
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void DownloadUpdate()
        {
            if (!string.IsNullOrEmpty(_downloadUrl))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = _downloadUrl,
                        UseShellExecute = true
                    });
                    CloseAction?.Invoke();
                }
                catch { }
            }
        }
    }
}
