using System.Collections.ObjectModel;
using ReactiveUI;
using System.Collections.Generic;
using System.Reactive;
using System.Linq;
using Avalonia.Media.Imaging;
using System.Net.Http;
using Avalonia.Platform;
using System.IO;
using System;
using System.Threading.Tasks;
using System.Net;

namespace Yomic.ViewModels
{
    public class SourceItem : ViewModelBase, IDisposable
    {
        public long Id { get; set; } // Source ID
        public string Name { get; set; } = string.Empty;
        public string Language { get; set; } = "EN";
        public string IconColor { get; set; } = "White"; // Used as Background
        public string IconForeground { get; set; } = "Black";
        private string _iconText = "S";
        public string IconText
        {
            get => _iconText;
            set => this.RaiseAndSetIfChanged(ref _iconText, value);
        }
        public bool IsPinned { get; set; }
        public bool IsInstalled { get; set; }
        public string BaseUrl { get; set; } = string.Empty;
        
        private Bitmap? _iconBitmap;
        public Bitmap? IconBitmap
        {
            get => _iconBitmap;
            set => this.RaiseAndSetIfChanged(ref _iconBitmap, value);
        }

        private bool _isLoadingIcon;
        public bool IsLoadingIcon
        {
            get => _isLoadingIcon;
            set => this.RaiseAndSetIfChanged(ref _isLoadingIcon, value);
        }

        // Multi-Language Support
        public ObservableCollection<Bitmap> LanguageFlags { get; } = new();

        public void Dispose()
        {
             if (_iconBitmap != null)
             {
                 _iconBitmap.Dispose();
                 _iconBitmap = null;
             }
             foreach (var flag in LanguageFlags)
             {
                 flag.Dispose();
             }
             LanguageFlags.Clear();
        }
    }

    public class BrowseViewModel : ViewModelBase, IDisposable
    {
        private readonly MainWindowViewModel _mainViewModel;
        private readonly Core.Services.SourceManager _sourceManager;
        private readonly Core.Services.NetworkService _networkService;
        private static readonly HttpClient _httpClient = new HttpClient();

        public ObservableCollection<SourceItem> Sources { get; set; } = new();

        public System.Windows.Input.ICommand OpenSourceCommand { get; }
        public ReactiveCommand<string, Unit> SetLanguageFilterCommand { get; }
        public System.Windows.Input.ICommand OpenWebViewCommand { get; }

        private bool _isOffline;
        public bool IsOffline
        {
            get => _isOffline;
            set => this.RaiseAndSetIfChanged(ref _isOffline, value);
        }

        private bool _isBypassing;
        public bool IsBypassing
        {
            get => _isBypassing;
            set => this.RaiseAndSetIfChanged(ref _isBypassing, value);
        }

        private string _bypassMessage = string.Empty;
        public string BypassMessage
        {
            get => _bypassMessage;
            set => this.RaiseAndSetIfChanged(ref _bypassMessage, value);
        }

        public BrowseViewModel(MainWindowViewModel mainViewModel, Core.Services.SourceManager sourceManager, Core.Services.NetworkService networkService, bool loadItems = true)
        {
            _mainViewModel = mainViewModel;
            _sourceManager = sourceManager;
            _networkService = networkService;
            
            // Initial State
            IsOffline = !_networkService.IsOnline;

            // Subscribe to Network Changes
            _networkService.StatusChanged += (s, isOnline) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                {
                    IsOffline = !isOnline;
                });
            };

            // Subscribe to Cloudflare bypass status updates
            Core.Services.CloudflareBypassService.Instance.OnStatusUpdate += (msg) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    BypassMessage = msg;
                });
            };
            
            OpenSourceCommand = ReactiveUI.ReactiveCommand.Create<SourceItem>(OpenSource);
            OpenWebViewCommand = ReactiveUI.ReactiveCommand.CreateFromTask<SourceItem>(OpenWebView);
            SetLanguageFilterCommand = ReactiveCommand.Create<string>(lang => { SelectedLanguage = lang; });
            
            if (loadItems)
            {
                LoadSources();
            }

            // Subscribe to dynamic updates
            _sourceManager.OnSourcesChanged += () =>
            {
                // Refresh list on UI thread
                Avalonia.Threading.Dispatcher.UIThread.Post(() => LoadSources());
            };
        }

        private void OpenSource(SourceItem item)
        {
            if (IsOffline) return; // Prevent action if offline

            var source = _sourceManager.GetSource(item.Id);
            if (source != null)
            {
                // Navigate to SourceFeedViewModel
                _mainViewModel.CurrentPage = new SourceFeedViewModel(source, _mainViewModel, _sourceManager, _mainViewModel.ImageCacheService, _mainViewModel.NetworkService);
            }
        }

        private async Task OpenWebView(SourceItem item)
        {
            if (IsOffline || string.IsNullOrEmpty(item.BaseUrl)) return;

            IsBypassing = true;
            BypassMessage = "Preparing...";

            try
            {
                Console.WriteLine($"[BrowseVM] Opening WebView for {item.Name} ({item.BaseUrl})...");
                var (ua, cookies) = await Core.Services.CloudflareBypassService.Instance.SolveInteractiveAsync(item.BaseUrl);

                if (!string.IsNullOrEmpty(ua) && cookies.Count > 0)
                {
                    // Inject cookies into the source's HttpClient
                    var source = _sourceManager.GetSource(item.Id);
                    if (source is Core.Sources.HttpSource httpSource)
                    {
                        var targetHost = new Uri(item.BaseUrl).Host;
                        foreach (var kv in cookies)
                        {
                            httpSource.CookieContainer.Add(new Cookie(kv.Key, kv.Value, "/", targetHost));
                        }
                        Console.WriteLine($"[BrowseVM] Injected {cookies.Count} cookies into {item.Name}");
                        
                        // Reload favicon if it hasn't loaded yet (still showing text fallback)
                        if (item.IconBitmap == null && !string.IsNullOrEmpty(source.IconUrl))
                        {
                            BypassMessage = "Updating Favicon...";
                            Console.WriteLine($"[BrowseVM] Re-loading favicon for {item.Name}...");
                            await LoadIconWithCookiesAsync(item, source.IconUrl, cookies, targetHost);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BrowseVM] WebView Error: {ex.Message}");
            }
            finally
            {
                // Delay slightly before hiding overlay to let user see "Success" message if it reached that far
                await Task.Delay(1000);
                IsBypassing = false;
            }
        }

        /// <summary>
        /// Loads a source icon using cookies (for Cloudflare-protected favicons).
        /// </summary>
        private async Task LoadIconWithCookiesAsync(SourceItem item, string url, Dictionary<string, string> cookies, string domain)
        {
            try
            {
                var handler = new System.Net.Http.HttpClientHandler
                {
                    UseCookies = true,
                    CookieContainer = new CookieContainer()
                };
                foreach (var kv in cookies)
                {
                    handler.CookieContainer.Add(new Cookie(kv.Key, kv.Value, "/", domain));
                }

                using var client = new HttpClient(handler);
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                
                var bytes = await client.GetByteArrayAsync(url);
                using var stream = new MemoryStream(bytes);
                var bitmap = new Bitmap(stream);
                
                // Update on UI thread
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    item.IconBitmap = bitmap;
                    item.IconText = ""; // Clear text fallback
                });
                Console.WriteLine($"[BrowseVM] Favicon loaded for {item.Name} ✓");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BrowseVM] Favicon load failed: {ex.Message}");
            }
        }

        public ObservableCollection<LanguageFilterItem> AvailableLanguages { get; } = new()
        {
            new LanguageFilterItem { Name = "All", Code = "ALL" },
            new LanguageFilterItem { Name = "Bahasa Indonesia", Code = "ID" },
            new LanguageFilterItem { Name = "English", Code = "EN" }
        };

        private LanguageFilterItem? _selectedLanguageItem;
        public LanguageFilterItem? SelectedLanguageItem
        {
            get => _selectedLanguageItem;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedLanguageItem, value);
                SelectedLanguage = value?.Code ?? "ALL";
            }
        }

        private string _selectedLanguage = "ALL";
        public string SelectedLanguage
        {
            get => _selectedLanguage;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedLanguage, value);
                var match = AvailableLanguages.FirstOrDefault(x => x.Code == value);
                if (match != null && SelectedLanguageItem != match)
                    SelectedLanguageItem = match;
                FilterSources();
            }
        }

        private System.Collections.Generic.List<SourceItem> _allSources = new();

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set
            {
                this.RaiseAndSetIfChanged(ref _searchText, value);
                FilterSources();
            }
        }
        
        public bool IsAll
        {
            get => SelectedLanguage == "ALL";
            set { if(value) SelectedLanguage = "ALL"; }
        }
        public bool IsId
        {
            get => SelectedLanguage == "ID";
            set { if(value) SelectedLanguage = "ID"; }
        }
        public bool IsEn
        {
            get => SelectedLanguage == "EN";
            set { if(value) SelectedLanguage = "EN"; }
        }

        private void LoadSources()
        {
            _allSources.Clear();
            var realSources = _sourceManager.GetSources();
            var showNsfw = App.SettingsService?.ShowNsfwSources ?? false;
            
            foreach (var source in realSources)
            {
                if (source.IsNsfw && !showNsfw) continue;
                
                // Branding Logic
                string iconBg = !string.IsNullOrEmpty(source.IconBackground) ? source.IconBackground : "White";
                string iconFg = !string.IsNullOrEmpty(source.IconForeground) ? source.IconForeground : "Black";
                string iconTxt = !string.IsNullOrEmpty(source.Name) ? source.Name.Substring(0, 1) : "?";

                var item = new SourceItem 
                { 
                    Id = source.Id,
                    Name = source.Name, 
                    BaseUrl = source.BaseUrl,
                    Language = source.Language, 
                    IconColor = iconBg,
                    IconForeground = iconFg,
                    IconText = iconTxt,
                    IsInstalled = true,
                    IsPinned = true
                };

                if (!string.IsNullOrEmpty(source.IconUrl))
                {
                     _ = LoadIconAsync(item, source);
                }
                
                LoadLanguageFlags(item);

                _allSources.Add(item);
            }
            // Sort A-Z
            _allSources.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            
            FilterSources();
        }

        private void FilterSources()
        {
            Sources.Clear();
            
            var query = _searchText?.Trim();
            foreach (var item in _allSources)
            {
                bool matchesSearch = string.IsNullOrEmpty(query) || 
                                     item.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
                                     
                bool matchesLang = SelectedLanguage == "ALL" || 
                                   (item.Language != null && item.Language.ToUpper() == SelectedLanguage);
                                   
                if (matchesSearch && matchesLang)
                {
                    Sources.Add(item);
                }
            }
            this.RaisePropertyChanged(nameof(HasSources));
        }

        public bool HasSources => Sources.Count > 0;

        private async System.Threading.Tasks.Task LoadIconAsync(SourceItem item, Core.Sources.IMangaSource source)
        {
            var url = source.IconUrl;
            if (string.IsNullOrEmpty(url)) return;

            item.IsLoadingIcon = true;
            try
            {
                byte[] bytes;
                
                // If the source is an HttpSource and has specific cookies, use them
                if (source is Core.Sources.HttpSource httpSource && httpSource.CookieContainer.Count > 0)
                {
                    using var handler = new HttpClientHandler 
                    { 
                        CookieContainer = httpSource.CookieContainer,
                        UseCookies = true
                    };
                    using var client = new HttpClient(handler);
                    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                    bytes = await client.GetByteArrayAsync(url);
                }
                else
                {
                    // Use Optimized Client (bypasses ISP blocks via DoH or Proxy)
                    using var optClient = _networkService.CreateOptimizedHttpClient();
                    bytes = await optClient.GetByteArrayAsync(url);
                }

                using var stream = new MemoryStream(bytes);
                var bitmap = new Bitmap(stream);
                
                // Update on UI thread
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    item.IconBitmap = bitmap;
                    item.IconText = ""; // Clear text
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BrowseVM] Icon load failed for {source.Name}: {ex.Message}");
            }
            finally
            {
                item.IsLoadingIcon = false;
            }
        }

        public void Dispose()
        {
            // Clear source icon bitmaps
            foreach (var item in _allSources)
            {
                if (item.IconBitmap != null)
                {
                    item.Dispose(); // Dispose entire item including flags
                }
            }
            _allSources.Clear();
            Sources.Clear();

            System.Diagnostics.Debug.WriteLine("[BrowseVM] Disposed and memory references cleared.");
        }

        private void LoadLanguageFlags(SourceItem item)
        {
             item.LanguageFlags.Clear();

             if (item.Name.Equals("MangaDex", StringComparison.OrdinalIgnoreCase))
             {
                 AddFlag(item, "id.png");
                 AddFlag(item, "gb.png");
                 return;
             }

             if (item.Language.Equals("id", StringComparison.OrdinalIgnoreCase))
             {
                 AddFlag(item, "id.png");
             }
             else if (item.Language.Equals("en", StringComparison.OrdinalIgnoreCase) || 
                      item.Language.Equals("gb", StringComparison.OrdinalIgnoreCase))
             {
                 AddFlag(item, "gb.png");
             }
        }

        private void AddFlag(SourceItem item, string fileName)
        {
            try
            {
                var uri = new Uri($"avares://Yomic/Assets/Flags/{fileName}");
                if (AssetLoader.Exists(uri))
                {
                    using var stream = AssetLoader.Open(uri);
                    item.LanguageFlags.Add(new Bitmap(stream));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load flag {fileName}: {ex.Message}");
            }
        }
    }
}
