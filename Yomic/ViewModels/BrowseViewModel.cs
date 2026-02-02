using System.Collections.ObjectModel;
using ReactiveUI;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using System.Net.Http;
using System.IO;
using System;

namespace Yomic.ViewModels
{
    public class SourceItem : ViewModelBase
    {
        public long Id { get; set; } // Source ID
        public string Name { get; set; } = string.Empty;
        public string Language { get; set; } = "EN";
        public string IconColor { get; set; } = "White"; // Used as Background
        public string IconForeground { get; set; } = "Black";
        public string IconText { get; set; } = "S";
        public bool IsPinned { get; set; }
        public bool IsInstalled { get; set; }
        
        private Bitmap? _iconBitmap;
        public Bitmap? IconBitmap
        {
            get => _iconBitmap;
            set => this.RaiseAndSetIfChanged(ref _iconBitmap, value);
        }
    }

    public class BrowseViewModel : ViewModelBase
    {
        private readonly MainWindowViewModel _mainViewModel;
        private readonly Core.Services.SourceManager _sourceManager;
        private readonly Core.Services.NetworkService _networkService;
        private static readonly HttpClient _httpClient = new HttpClient();

        public ObservableCollection<SourceItem> Sources { get; set; } = new();

        public System.Windows.Input.ICommand OpenSourceCommand { get; }

        private bool _isOffline;
        public bool IsOffline
        {
            get => _isOffline;
            set => this.RaiseAndSetIfChanged(ref _isOffline, value);
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
            
            OpenSourceCommand = ReactiveUI.ReactiveCommand.Create<SourceItem>(OpenSource);
            
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

        private System.Collections.Generic.List<SourceItem> _allSources = new();
        
        private string _selectedLanguage = "ID"; // Default ID as per previous behavior
        public string SelectedLanguage
        {
            get => _selectedLanguage;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedLanguage, value);
                this.RaisePropertyChanged(nameof(IsAll));
                this.RaisePropertyChanged(nameof(IsId));
                this.RaisePropertyChanged(nameof(IsEn));
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
            
            foreach (var source in realSources)
            {
                // Branding Logic
                string iconBg = "White";
                string iconFg = "Black";
                string iconTxt = source.Name.Substring(0, 1);

                if (source.Name.Contains("Komiku", StringComparison.OrdinalIgnoreCase))
                {
                    iconBg = "#2596be"; // Komiku Blue
                    iconFg = "White";
                    iconTxt = "K";
                }

                var item = new SourceItem 
                { 
                    Id = source.Id,
                    Name = source.Name, 
                    Language = source.Language, 
                    IconColor = iconBg,
                    IconForeground = iconFg,
                    IconText = iconTxt,
                    IsInstalled = true,
                    IsPinned = true
                };

                if (source.Name.Contains("Komiku", StringComparison.OrdinalIgnoreCase))
                {
                     _ = LoadIconAsync(item, "https://www.google.com/s2/favicons?domain=komiku.org&sz=128");
                }
                
                _allSources.Add(item);
            }
            FilterSources();
        }

        private void FilterSources()
        {
            Sources.Clear();
            
            foreach (var item in _allSources)
            {
                if (SelectedLanguage == "ALL" || 
                    (item.Language != null && item.Language.ToUpper() == SelectedLanguage))
                {
                    Sources.Add(item);
                }
            }
            this.RaisePropertyChanged(nameof(HasSources));
        }

        public bool HasSources => Sources.Count > 0;

        private async System.Threading.Tasks.Task LoadIconAsync(SourceItem item, string url)
        {
            try
            {
                var bytes = await _httpClient.GetByteArrayAsync(url);
                using var stream = new MemoryStream(bytes);
                var bitmap = new Bitmap(stream);
                item.IconBitmap = bitmap;
                item.IconText = ""; // Clear text
            }
            catch
            {
                // Ignore
            }
        }
    }
}
