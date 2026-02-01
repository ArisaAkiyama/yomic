using Avalonia.Controls;
using System.Reactive.Linq;
using System.Reactive;

namespace MyMangaApp.Views
{
    public partial class SourceFeedView : UserControl
    {
        public SourceFeedView()
        {
            InitializeComponent();
            
            // Infinite Scroll Logic
            MainScrollViewer.ScrollChanged += (s, e) => 
            {
                if (s is ScrollViewer sv)
                {
                    // Debug Scroll
                    // System.Diagnostics.Debug.WriteLine($"[Scroll] Off: {sv.Offset.Y}, Ext: {sv.Extent.Height}, View: {sv.Viewport.Height}");

                     // Trigger when close to bottom (e.g. 500px buffer)
                    if (sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 500)
                    {
                        if (DataContext is ViewModels.SourceFeedViewModel vm)
                        {
                            // Trigger Next Page (Infinite Scroll)
                            // Use HasNextPage property and avoid firing if loading
                            // ALSO: Only trigger if Pagination is HIDDEN (Latest Mode). Do not trigger for Directory mode.
                            if (vm.HasNextPage && !vm.IsLoading && !vm.IsPaginationVisible)
                            {
                                System.Diagnostics.Debug.WriteLine("[Scroll] Triggering Next Page!");
                                vm.NextPageCommand.Execute(System.Reactive.Unit.Default).Subscribe(System.Reactive.Observer.Create<System.Reactive.Unit>(_ => { }));
                            }
                        }
                    }
                }
            };
        }
    }
}
