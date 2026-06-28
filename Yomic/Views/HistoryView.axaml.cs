using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using System;
using System.Reactive.Linq;
using Yomic.ViewModels;

namespace Yomic.Views
{
    public partial class HistoryView : UserControl
    {
        private IDisposable? _scrollSubscription;

        public HistoryView()
        {
            InitializeComponent();
            AttachedToVisualTree += (_, _) => AttachLazyLoading();
            DetachedFromVisualTree += (_, _) => DetachLazyLoading();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void AttachLazyLoading()
        {
            DetachLazyLoading();

            var scrollViewer = this.FindControl<ScrollViewer>("HistoryScrollViewer");
            if (scrollViewer == null) return;

            _scrollSubscription = Observable
                .FromEventPattern<ScrollChangedEventArgs>(
                    h => scrollViewer.ScrollChanged += h,
                    h => scrollViewer.ScrollChanged -= h)
                .Throttle(TimeSpan.FromMilliseconds(120))
                .Subscribe(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => TryLoadMore(scrollViewer)));
        }

        private void TryLoadMore(ScrollViewer scrollViewer)
        {
            if (DataContext is not HistoryViewModel vm || vm.IsLoadingMore || !vm.HasMoreItems) return;

            var remaining = scrollViewer.Extent.Height - scrollViewer.Viewport.Height - scrollViewer.Offset.Y;
            if (remaining <= 700)
            {
                vm.LoadMoreCommand.Execute().Subscribe();
            }
        }

        private void DetachLazyLoading()
        {
            _scrollSubscription?.Dispose();
            _scrollSubscription = null;
        }
    }
}
