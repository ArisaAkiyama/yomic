using ReactiveUI;
using System.Reactive;
using System;
using System.Threading.Tasks;
using Avalonia; // Added for Application.Current

namespace Yomic.ViewModels
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
            set 
            {
                this.RaiseAndSetIfChanged(ref _isUpdateAvailable, value);
                this.RaisePropertyChanged(nameof(IsInstallButtonVisible));
            }
        }

        public bool IsInstallButtonVisible => IsUpdateAvailable && !IsDownloading;

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

        private bool _isDownloading;
        public bool IsDownloading
        {
            get => _isDownloading;
            set 
            {
                this.RaiseAndSetIfChanged(ref _isDownloading, value);
                this.RaisePropertyChanged(nameof(IsInstallButtonVisible));
            }
        }

        private double _downloadProgress;
        public double DownloadProgress
        {
            get => _downloadProgress;
            set => this.RaiseAndSetIfChanged(ref _downloadProgress, value);
        }

        private async void DownloadUpdate()
        {
            if (string.IsNullOrEmpty(_downloadUrl) || IsDownloading) return;

            // If it's just a web link (fallback), open browser
            if (!_downloadUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                 try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = _downloadUrl, UseShellExecute = true }); } catch { }
                 CloseAction?.Invoke();
                 return;
            }

            IsDownloading = true;
            StatusText = "Downloading Update...";
            StatusIcon = "\uE896"; // Download
            StatusColor = "#FF9900";

            try
            {
                var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Yomic_Update.exe");
                
                // Cleanup previous potential failed download
                if (System.IO.File.Exists(tempPath))
                {
                    try { System.IO.File.Delete(tempPath); } catch { }
                }
                
                using (var client = new System.Net.Http.HttpClient())
                {
                    using (var response = await client.GetAsync(_downloadUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();
                        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                        var canValidProgress = totalBytes != -1L;

                        using (var contentStream = await response.Content.ReadAsStreamAsync())
                        {
                            using (var fileStream = new System.IO.FileStream(tempPath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None, 8192, true))
                            {
                                var totalRead = 0L;
                                var buffer = new byte[8192];
                                var isMoreToRead = true;

                                do
                                {
                                    var read = await contentStream.ReadAsync(buffer, 0, buffer.Length);
                                    if (read == 0)
                                    {
                                        isMoreToRead = false;
                                    }
                                    else
                                    {
                                        await fileStream.WriteAsync(buffer, 0, read);
                                        totalRead += read;
                                        if (canValidProgress)
                                        {
                                            DownloadProgress = (double)totalRead / totalBytes * 100;
                                        }
                                    }
                                }
                                while (isMoreToRead);
                            }
                        }
                    }
                }

                // Launch Installer
                StatusText = "Launching Installer...";
                await Task.Delay(500); // Brief delay for UX

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tempPath,
                    UseShellExecute = true,
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"
                });

                // Close App
                if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
            }
            catch (Exception ex)
            {
                StatusText = "Download Failed";
                StatusColor = "#FF5555";
                ReleaseNotes = $"Error: {ex.Message}";
                IsDownloading = false;
                
                // Fallback: Show manual download button (re-enable IsUpdateAvailable state but with retry text if desired, 
                // or just leave it. For now, let's auto-open browser as fail-safe)
                 try 
                 { 
                     ReleaseNotes += "\n\nOpening download link in browser...";
                     System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = _downloadUrl, UseShellExecute = true }); 
                 } catch { }
            }
        }
    }
}
