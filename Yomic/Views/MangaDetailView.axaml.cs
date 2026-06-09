using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Yomic.ViewModels;

namespace Yomic.Views
{
    public partial class MangaDetailView : UserControl
    {
        public MangaDetailView()
        {
            InitializeComponent();
        }
        
        private void OnSynopsisToggleClick(object? sender, RoutedEventArgs e)
        {
            // Find the ListBox's ScrollViewer to preserve scroll position
            var listBox = this.FindControl<ListBox>("") ?? this.GetVisualDescendants()
                .OfType<ListBox>().FirstOrDefault();
            
            if (listBox == null) return;
            
            var scrollViewer = listBox.GetVisualDescendants()
                .OfType<ScrollViewer>().FirstOrDefault();
            
            if (scrollViewer == null) return;
            
            // Measure current header height before the command executes
            // (Command fires before this Click handler via binding order,
            //  so we need to adjust on next layout pass)
            var currentOffset = scrollViewer.Offset;
            
            // After layout updates, restore the scroll offset
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                scrollViewer.Offset = currentOffset;
            }, Avalonia.Threading.DispatcherPriority.Render);
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


    }
}
