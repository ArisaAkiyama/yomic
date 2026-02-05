using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using ReactiveUI;
using Avalonia.Media.Imaging;
using System.Net.Http;
using System.IO;
using Yomic.Core.Services;
using Yomic.Core.Sources;

namespace Yomic.ViewModels
{
    public class ExtensionItem : ViewModelBase
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Version { get; set; } = "1.0";
        public string Language { get; set; } = "EN";
        public string IconText { get; set; } = "E";
        public string IconColor { get; set; } = "#FF9900";
        public string IconBackground { get; set; } = "#313244";
        public string Description { get; set; } = "";
        public string? FilePath { get; set; } // Path for uninstalled extensions
        
        private Bitmap? _iconBitmap;
        public Bitmap? IconBitmap
        {
            get => _iconBitmap;
            set => this.RaiseAndSetIfChanged(ref _iconBitmap, value);
        }

        private bool _isInstalled;
        public bool IsInstalled
        {
            get => _isInstalled;
            set => this.RaiseAndSetIfChanged(ref _isInstalled, value);
        }

        private bool _isInstalling;
        public bool IsInstalling
        {
            get => _isInstalling;
            set => this.RaiseAndSetIfChanged(ref _isInstalling, value);
        }

        // Feature Flags
        public bool CanVerify { get; set; }

        public IMangaSource? SourceInstance { get; set; }
    }

    public class ExtensionsViewModel : ViewModelBase
    {
        private readonly SourceManager _sourceManager;
        private static readonly HttpClient _httpClient = new HttpClient();
        
        private List<ExtensionItem> _allExtensionsCache = new();
        public ObservableCollection<ExtensionItem> FilteredExtensions { get; } = new();

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set 
            {
                this.RaiseAndSetIfChanged(ref _searchText, value);
                FilterExtensions();
            }
        }

        public ReactiveCommand<ExtensionItem, Unit> ToggleInstallCommand { get; }
        public ReactiveCommand<ExtensionItem, Unit> VerifyExtensionCommand { get; }
        public ReactiveCommand<Unit, Unit> AddExtensionCommand { get; }

        private bool _hasNoInstalledExtensions;
        public bool HasNoInstalledExtensions
        {
            get => _hasNoInstalledExtensions;
            set => this.RaiseAndSetIfChanged(ref _hasNoInstalledExtensions, value);
        }

        public ExtensionsViewModel(SourceManager sourceManager)
        {
            _sourceManager = sourceManager;
            
            ToggleInstallCommand = ReactiveCommand.Create<ExtensionItem>(ToggleInstall);
            VerifyExtensionCommand = ReactiveCommand.Create<ExtensionItem>(VerifyExtension);
            AddExtensionCommand = ReactiveCommand.Create(AddExtension);
            OpenRepoCommand = ReactiveCommand.Create(OpenRepo);
            
            LoadExtensions();
        }

        public ReactiveCommand<Unit, Unit> OpenRepoCommand { get; }

        private void OpenRepo()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo 
                { 
                    FileName = "https://github.com/ArisaAkiyama/extension-yomic", 
                    UseShellExecute = true 
                });
            }
            catch { /* Ignore */ }
        }

        private async void VerifyExtension(ExtensionItem item)
        {
            if (item.SourceInstance == null) return;
            var method = item.SourceInstance.GetType().GetMethod("InitializeBrowserAsync");
            if (method != null)
            {
                 StatusMessage = $"Verifying {item.Name}... Please solve the CAPTCHA in the browser window.";
                 await System.Threading.Tasks.Task.Run(() => 
                 {
                     try { method.Invoke(item.SourceInstance, null); }
                     catch (Exception)
                     {
                         // Ignore error
                     }
                 });
                 StatusMessage = $"{item.Name} Verified!";
                 
                 // Clear message after delay
                 await System.Threading.Tasks.Task.Delay(3000);
                 StatusMessage = "";
            }
        }
        
        public bool HasExtensions => FilteredExtensions.Count > 0;

        private void UpdateEmptyState()
        {
            // Empty state if NO extensions are installed (checking cache mostly, or filtered list?)
            // Usually empty state in UI means "No results found" or "No installed extensions overall"
            // Let's base it on Filtered List count for "No Results" 
            // OR base it on Installed Count for "No installed".
            // The UI logic seemed to check "HasExtensions" (Count > 0 of list).
            this.RaisePropertyChanged(nameof(HasExtensions));
        }

        private void LoadExtensions()
        {
            _allExtensionsCache.Clear();
            var activeSources = _sourceManager.GetSources();
            foreach (var source in activeSources)
            {
                bool canVerify = source.GetType().GetMethod("InitializeBrowserAsync") != null;
                
                // Branding Logic
                string iconBg = "#313244";
                string iconFg = "#FF9900";
                string iconTxt = source.Name.Substring(0, 1);

                if (source.Name.Contains("Komiku", StringComparison.OrdinalIgnoreCase))
                {
                    iconBg = "#2596be"; // Komiku Blue
                    iconFg = "White";
                    iconTxt = "K";
                }

                var extItem = new ExtensionItem
                {
                    Id = source.Id,
                    Name = source.Name,
                    Version = "1.0.2",
                    Language = source.Language,
                    IconText = iconTxt,
                    IconColor = iconFg,
                    IconBackground = iconBg,
                    Description = $"{source.Name} Source (api.komiku.org)",
                    IsInstalled = true,
                    SourceInstance = source,
                    CanVerify = canVerify
                };
                
                if (source.Name.Contains("Komiku", StringComparison.OrdinalIgnoreCase))
                {
                     _ = LoadIconAsync(extItem, "https://www.google.com/s2/favicons?domain=komiku.org&sz=128");
                }
                
                _allExtensionsCache.Add(extItem);
            }
            
            FilterExtensions();
        }

        private void FilterExtensions()
        {
            FilteredExtensions.Clear();
            
            var query = _searchText?.Trim();
            var list = string.IsNullOrEmpty(query) 
                ? _allExtensionsCache 
                : _allExtensionsCache.Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

            // Sort: Installed First, then Name
            var sorted = list.OrderByDescending(x => x.IsInstalled).ThenBy(x => x.Name);
            
            foreach(var item in sorted)
            {
                FilteredExtensions.Add(item);
            }
            
            UpdateEmptyState();
        }

        // Delegate for View to hook into
        public System.Func<System.Threading.Tasks.Task<Avalonia.Platform.Storage.IStorageFile?>>? OpenFilePickerAsync { get; set; }

        private string? _statusMessage; // Helper: make nullable if needed to fix warning
        public string? StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set => this.RaiseAndSetIfChanged(ref _isBusy, value);
        }

        private async void AddExtension()
        {
            if (OpenFilePickerAsync == null) return;
            StatusMessage = "";

            try
            {
                var file = await OpenFilePickerAsync();
                if (file == null) return;

                IsBusy = true;
                
                // Simulate a small delay so the user SEES the loading if it's too fast
                await System.Threading.Tasks.Task.Delay(500);

                var path = file.Path.LocalPath;
                StatusMessage = $"Loading {System.IO.Path.GetFileName(path)}...";

                // Use SourceManager to PEEK only (do not install yet)
                var source = _sourceManager.PeekExtension(path);

                if (source != null)
                {
                    // Check if already in UI list (duplicate check)
                    // We check ID and Name to be sure
                    if (_allExtensionsCache.Any(x => x.Id == source.Id || x.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase))) 
                    {
                        StatusMessage = $"Extension '{source.Name}' is already in the list.";
                        IsBusy = false;
                        return;
                    }

                    bool canVerify = source.GetType().GetMethod("InitializeBrowserAsync") != null;

                    // Branding Logic
                    string iconBg = "#313244";
                    string iconFg = "#FF9900";
                    string iconTxt = source.Name.Substring(0, 1);

                    if (source.Name.Contains("Komiku", StringComparison.OrdinalIgnoreCase))
                    {
                        iconBg = "#2596be";
                        iconFg = "White";
                        iconTxt = "K";
                    }

                    var newExt = new ExtensionItem
                    {
                            Id = source.Id,
                            Name = source.Name,
                            Version = source.GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0",
                            Language = source.Language,
                            IconText = iconTxt,
                            IconColor = iconFg,
                            IconBackground = iconBg,
                            Description = $"Loaded from {System.IO.Path.GetFileName(path)}",
                            IsInstalled = false, // Not installed yet
                            SourceInstance = source,
                            CanVerify = canVerify,
                            FilePath = path // Save path for installation
                    };
                    
                    if (source.Name.Contains("Komiku", StringComparison.OrdinalIgnoreCase))
                    {
                            _ = LoadIconAsync(newExt, "https://www.google.com/s2/favicons?domain=komiku.org&sz=128");
                    }

                    _allExtensionsCache.Add(newExt);
                    FilterExtensions();
                    StatusMessage = ""; 
                }
                else
                {
                    StatusMessage = "Error: Failed to load extension from DLL.";
                }
            }
            catch (System.Exception ex)
            {
                StatusMessage = $"Load Error: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async System.Threading.Tasks.Task LoadIconAsync(ExtensionItem item, string url)
        {
            try
            {
                var bytes = await _httpClient.GetByteArrayAsync(url);
                using var stream = new MemoryStream(bytes);
                var bitmap = new Bitmap(stream);
                // Switch to UI thread if needed, but ReactiveUI properties handle notification.
                // However, Bitmap creation often needs to happen? No, Bitmap matches UI thread usually? 
                // Avalonia Bitmaps are thread-safeish but best created on UI or passed carefully.
                // We'll just set it.
                item.IconBitmap = bitmap;
                item.IconText = ""; // Clear IconText if bitmap is loaded
            }
            catch
            {
                // Fallback to text
            }
        }

        private async void ToggleInstall(ExtensionItem item)
        {
            if (item.IsInstalled)
            {
                // Uninstall
                item.IsInstalling = true;
                await System.Threading.Tasks.Task.Delay(1000); // Simulate uninstall time

                item.IsInstalled = false;
                item.IsInstalling = false;
                
                _sourceManager.RemoveSource(item.Id);
                _allExtensionsCache.Remove(item); 
            }
            else
            {
                // Install - with loading simulation
                item.IsInstalling = true;
                await System.Threading.Tasks.Task.Delay(1000); // Simulate install time
                
                if (!string.IsNullOrEmpty(item.FilePath))
                {
                    // INSTALL: Copy to Plugins and Load (Background Thread)
                    var loadedSource = await System.Threading.Tasks.Task.Run(() => _sourceManager.InstallPlugin(item.FilePath));
                    
                    if (loadedSource != null)
                    {
                        item.SourceInstance = loadedSource;
                        item.IsInstalled = true;
                        
                        var fileName = System.IO.Path.GetFileName(item.FilePath);
                        item.Description = $"Installed in Plugins ({fileName})";
                    }
                    else
                    {
                        StatusMessage = "Failed to install extension.";
                    }
                }
                else
                {
                     // Fallback for pre-installed items logic
                     if (item.SourceInstance != null)
                     {
                         _sourceManager.AddSource(item.SourceInstance);
                         item.IsInstalled = true;
                     }
                }
                
                item.IsInstalling = false;
            }
            
            FilterExtensions();
        }
    }
}
