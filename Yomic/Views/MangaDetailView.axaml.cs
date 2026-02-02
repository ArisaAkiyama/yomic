using Avalonia.Controls;
using Avalonia.Interactivity;
using Yomic.ViewModels;

namespace Yomic.Views
{
    public partial class MangaDetailView : UserControl
    {
        public MangaDetailView()
        {
            InitializeComponent();
        }
        
        private void OnBackClick(object? sender, RoutedEventArgs e)
        {
            // Find the MainWindow and get its ViewModel
            if (this.VisualRoot is MainWindow mainWindow && 
                mainWindow.DataContext is MainWindowViewModel vm)
            {
                vm.GoBack();
            }
        }

        private void OnReadClick(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ChapterItem chapter &&
                this.VisualRoot is MainWindow mainWindow && 
                mainWindow.DataContext is MainWindowViewModel vm)
            {
                // Get chapter list from MangaDetailViewModel
                System.Collections.Generic.List<ChapterItem>? chapters = null;
                long sourceId = 3;
                string title = "";
                
                if (this.DataContext is MangaDetailViewModel detailVm)
                {
                    chapters = detailVm.Chapters;
                    sourceId = detailVm.SourceId;
                    title = detailVm.Title;
                    // Pass URL to enable online persistence
                    vm.GoToReader(chapter, chapters, sourceId, title, detailVm.Url, detailVm.IsExplicitContent);
                } else {
                     vm.GoToReader(chapter, chapters, sourceId, title);
                }
            }
        }

        private void OnTrackingClick(object? sender, RoutedEventArgs e)
        {
             var dialog = new TrackingDialog();
             if (this.VisualRoot is Window parentWindow)
             {
                 dialog.ShowDialog(parentWindow);
             }
        }

        private void OnOpenInNewWindowClick(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ChapterItem chapter &&
                this.VisualRoot is Window parentWindow && 
                parentWindow.DataContext is MainWindowViewModel vm)
            {
                // Get context
                System.Collections.Generic.List<ChapterItem>? chapters = null;
                long sourceId = 3;
                string title = "";
                bool isNsfw = false;
                if (this.DataContext is MangaDetailViewModel detailVm)
                {
                    chapters = detailVm.Chapters;
                    sourceId = detailVm.SourceId;
                    title = detailVm.Title;
                    isNsfw = detailVm.IsExplicitContent;
                }

                var readerVM = new ReaderViewModel(vm, vm.SourceManager, chapter, chapters, vm.NetworkService, vm.LibraryService, sourceId, title, "", isNsfw);
                var readerWindow = new ReaderWindow
                {
                    DataContext = readerVM
                };
                
                // Override Back to close window
                readerVM.CustomBackAction = () => readerWindow.Close();

                readerWindow.Show();
            }
        }
    }
}
