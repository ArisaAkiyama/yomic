using Avalonia.Controls;
using Avalonia.Interactivity;
using Yomic.ViewModels;
using System;

namespace Yomic.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
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
