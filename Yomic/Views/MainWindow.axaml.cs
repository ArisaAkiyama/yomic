using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI; // Added for WhenAnyValue
using ReactiveUI; // Added for WhenAnyValue
using Yomic.ViewModels;
using System;

namespace Yomic.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // Sync WindowState when ViewModel.IsFullscreen changes (e.g., from ReaderView)
                vm.WhenAnyValue(x => x.IsFullscreen)
                  .Subscribe(isFullscreen =>
                  {
                      SetFullscreen(vm, isFullscreen);
                  });
            }
        }

        protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (DataContext is MainWindowViewModel vm)
            {
                if (e.Key == Avalonia.Input.Key.F11 || e.Key == Avalonia.Input.Key.F2)
                {
                    ToggleFullscreen(vm);
                    e.Handled = true;
                }
                else if (e.Key == Avalonia.Input.Key.Escape && vm.IsFullscreen)
                {
                    // Only exit fullscreen if currently in fullscreen
                    SetFullscreen(vm, false);
                    e.Handled = true;
                }
                else if (e.KeyModifiers == Avalonia.Input.KeyModifiers.Control && e.Key == Avalonia.Input.Key.R)
                {
                    OnReloadClick(null, null);
                    e.Handled = true;
                }
            }
        }

        private void ToggleFullscreen(MainWindowViewModel vm)
        {
            bool newState = !vm.IsFullscreen;
            SetFullscreen(vm, newState);
        }

        private void SetFullscreen(MainWindowViewModel vm, bool isFullscreen)
        {
            vm.IsFullscreen = isFullscreen;
            
            if (isFullscreen)
            {
                // Strict Fullscreen Mode: Remove borders, padding, and transparency
                SystemDecorations = SystemDecorations.None;
                WindowState = WindowState.FullScreen;
                Padding = new Avalonia.Thickness(0);
                Background = Avalonia.Media.Brushes.Black;
                ExtendClientAreaToDecorationsHint = false; // Disable custom chrome to remove potential top bar reservation
            }
            else
            {
                // Restore Normal Mode
                SystemDecorations = SystemDecorations.Full;
                WindowState = WindowState.Normal;
                Padding = new Avalonia.Thickness(0); // WindowState=Maximized style handle this via XAML, but standard is 0 for Normal
                Background = Avalonia.Media.Brushes.Transparent;
                ExtendClientAreaToDecorationsHint = true;
            }
        }

        private void OnLibraryClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.GoToLibrary();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Navigation Error: {ex}");
            }
        }

        private void OnBrowseClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.GoToBrowse();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Navigation Error: {ex}");
            }
        }



        private void OnSettingsClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.GoToSettings();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Navigation Error: {ex}");
            }
        }

        private void OnHistoryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                vm.GoToHistory();
            }
        }

        private void OnDownloadsClick(object? sender, RoutedEventArgs e)
        {
             try
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.GoToDownloads();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Navigation Error: {ex}");
            }
        }

        private void OnUpdatesClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.GoToUpdates();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Navigation Error: {ex}");
            }
        }

        private void OnExtensionsClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.GoToExtensions();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Navigation Error: {ex}");
            }
        }

        private void OnReloadClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    // Check connectivity first
                    _ = vm.NetworkService.CheckConnectivityAsync();
                    
                    // If Library page, refresh it
                    if (vm.CurrentPage == vm.LibraryVM)
                    {
                        _ = vm.LibraryVM.RefreshLibrary();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Reload Error: {ex}");
            }
        }
    }
}
