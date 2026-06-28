using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Yomic.ViewModels;

namespace Yomic.Views
{
    public partial class MangaDetailView : UserControl
    {
        private ScrollViewer? _scrollViewer;
        private ListBox? _listBox;
        private Border? _stickyHeader;

        public MangaDetailView()
        {
            InitializeComponent();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            InitializeStickyHeader();
            this.LayoutUpdated += OnLayoutUpdated;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            this.LayoutUpdated -= OnLayoutUpdated;
            if (_scrollViewer != null)
            {
                _scrollViewer.ScrollChanged -= OnScrollChanged;
                _scrollViewer = null;
            }
        }

        private void OnLayoutUpdated(object? sender, System.EventArgs e)
        {
            InitializeStickyHeader();
        }

        private void InitializeStickyHeader()
        {
            if (_listBox == null)
            {
                _listBox = this.FindControl<ListBox>("ChaptersListBox");
            }
            if (_listBox == null) return;

            if (_scrollViewer == null)
            {
                _scrollViewer = _listBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                if (_scrollViewer != null)
                {
                    _scrollViewer.ScrollChanged += OnScrollChanged;
                }
            }
            
            if (_stickyHeader == null)
            {
                _stickyHeader = this.FindControl<Border>("StickyHeader");
            }
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            UpdateStickyHeaderVisibility();
        }

        private void UpdateStickyHeaderVisibility()
        {
            if (_listBox == null || _scrollViewer == null) return;
            
            var container = _listBox.ContainerFromIndex(0) as Visual;
            if (container == null)
            {
                // Container is virtualized (recycled) — this only happens when 
                // the header item has been scrolled far above the viewport.
                // In this case, the sticky header should ALWAYS be visible.
                if (_stickyHeader != null) _stickyHeader.IsVisible = _scrollViewer.Offset.Y > 0;
                return;
            }

            var chaptersDivider = container.GetVisualDescendants()
                .OfType<Grid>()
                .FirstOrDefault(g => g.Name == "ChaptersDivider");

            if (chaptersDivider == null)
            {
                if (_stickyHeader != null) _stickyHeader.IsVisible = false;
                return;
            }

            var relativePoint = chaptersDivider.TranslatePoint(new Avalonia.Point(0, 0), _listBox);
            if (relativePoint != null)
            {
                bool shouldBeSticky = relativePoint.Value.Y <= 15;
                if (_stickyHeader != null)
                {
                    _stickyHeader.IsVisible = shouldBeSticky;
                }
            }
            else
            {
                if (_stickyHeader != null) _stickyHeader.IsVisible = false;
            }
        }

        protected override void OnDataContextChanged(System.EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (DataContext is MangaDetailViewModel vm)
            {
                vm.ShowDownloadAllDialogAsync = ShowDownloadAllDialogAsync;
            }
        }

        private async System.Threading.Tasks.Task<DownloadAllMode?> ShowDownloadAllDialogAsync(DownloadAllDialogInfo info)
        {
            if (this.VisualRoot is not Window owner)
            {
                return DownloadAllMode.NotDownloaded;
            }

            var dialog = new DownloadAllDialog(info);
            return await dialog.ShowDialog<DownloadAllMode?>(owner);
        }
        
        private void OnSynopsisToggleClick(object? sender, RoutedEventArgs e)
        {
            // Find the ListBox's ScrollViewer to preserve scroll position
            var listBox = this.FindControl<ListBox>("ChaptersListBox") ?? this.GetVisualDescendants()
                .OfType<ListBox>().FirstOrDefault();
            
            if (listBox == null) return;
            
            var scrollViewer = listBox.GetVisualDescendants()
                .OfType<ScrollViewer>().FirstOrDefault();
            
            if (scrollViewer == null) return;
            
            // Measure current header height before the command executes
            var currentOffset = scrollViewer.Offset;
            
            // After layout updates, restore the scroll offset
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                scrollViewer.Offset = currentOffset;
                UpdateStickyHeaderVisibility();
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
