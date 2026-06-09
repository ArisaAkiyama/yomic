using Avalonia.Controls;
using System.Reactive.Linq;
using System.Reactive;

namespace Yomic.Views
{
    public partial class SourceFeedView : UserControl
    {
        public SourceFeedView()
        {
            InitializeComponent();
            
            // Infinite Scroll Logic
            // Infinite Scroll Logic (Attached to ListBox's internal ScrollViewer)
            // Infinite Scroll Logic
            // Attached to the main ScrollViewer
            // Infinite Scroll Logic (Attached to ListBox's internal ScrollViewer via routed event)
            MangaListBox.AddHandler(ScrollViewer.ScrollChangedEvent, (s, e) => 
            {
                if (e is ScrollChangedEventArgs args && args.Source is ScrollViewer sv)
                {
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
            });
        }
    }
}
