using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MyMangaApp.ViewModels;
using System;

namespace MyMangaApp.Views
{
    public partial class ReaderView : UserControl
    {
        public ReaderView()
        {
            InitializeComponent();
            
            // Register Slider pointer events with handledEventsToo: true to ensure we catch them
            var slider = this.FindControl<Avalonia.Controls.Slider>("ProgressSlider");
            if (slider != null)
            {
                slider.AddHandler(PointerPressedEvent, OnSliderPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
                slider.AddHandler(PointerReleasedEvent, OnSliderPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
            }

            // Focusable needs to be true for KeyDown to work
            this.AttachedToVisualTree += (s, e) => this.Focus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            
            if (DataContext is ReaderViewModel vm)
            {
                if (e.Key == Key.Right)
                {
                    vm.NextPageCommand.Execute().Subscribe(_ => { });
                    e.Handled = true;
                }
                else if (e.Key == Key.Left)
                {
                    vm.PrevPageCommand.Execute().Subscribe(_ => { });
                    e.Handled = true;
                }
                else if (e.Key == Key.Space)
                {
                    vm.ToggleMenuCommand.Execute().Subscribe(_ => { });
                    e.Handled = true;
                }
            }
        }

        private Avalonia.Point _startPoint;

        private void OnReaderPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            _startPoint = e.GetPosition(this);
        }

        private void OnReaderPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            var endPoint = e.GetPosition(this);
            var deltaX = Math.Abs(endPoint.X - _startPoint.X);
            var deltaY = Math.Abs(endPoint.Y - _startPoint.Y);

            // If moved significantly, treat as Drag/Scroll and ignore click
            if (deltaX > 10 || deltaY > 10) return;

            if (DataContext is ReaderViewModel vm)
            {
                var width = this.Bounds.Width;
                var x = endPoint.X;

                if (x < width * 0.3)
                {
                    // Left 30% -> Prev Page
                    vm.PrevPageCommand.Execute().Subscribe(_ => { });
                }
                else if (x > width * 0.7)
                {
                    // Right 30% -> Next Page
                    vm.NextPageCommand.Execute().Subscribe(_ => { });
                }
                else
                {
                    // Center 40% -> Toggle Menu
                    vm.ToggleMenuCommand.Execute().Subscribe(_ => { });
                }
            }
        }

        private void OnBackClick(object? sender, RoutedEventArgs e)
        {
            if (this.VisualRoot is MainWindow mainWindow && 
                mainWindow.DataContext is MainWindowViewModel vm)
            {
                vm.GoToLibrary();
            }
            else if (this.VisualRoot is ReaderWindow readerWindow)
            {
                readerWindow.Close();
            }
        }

        // Check if user is interacting with slider to prevent loop
        private bool _isDraggingSlider = false;

        private void OnSliderPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            _isDraggingSlider = true;
        }

        private void OnSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            _isDraggingSlider = false;
        }

        private void OnSliderValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (DataContext is ReaderViewModel vm && vm.IsWebtoon && _isDraggingSlider)
            {
                // User dragged slider -> Scroll to position
                // Value is Page Index
                var percent = e.NewValue / Math.Max(1, vm.Pages.Count);
                var offset = percent * MainScroll.Extent.Height;
                MainScroll.Offset = new Avalonia.Vector(MainScroll.Offset.X, offset);
            }
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (_isDraggingSlider) return; // Don't fight the user

            if (DataContext is ReaderViewModel vm && vm.IsWebtoon)
            {
                if (MainScroll.Extent.Height <= 0) return;

                // Simple Approximation: Viewport Center determines page?
                // Or Top? Let's use Top.
                var percent = MainScroll.Offset.Y / MainScroll.Extent.Height;
                var estimatedIndex = (int)(percent * vm.Pages.Count);
                
                // Clamp
                if (vm.Pages.Count > 0)
                {
                    estimatedIndex = Math.Clamp(estimatedIndex, 0, vm.Pages.Count - 1);
                    
                    if (vm.CurrentPageIndex != estimatedIndex)
                    {
                        vm.CurrentPageIndex = estimatedIndex;
                    }
                }
            }
        }



        private void OnRootPointerMoved(object? sender, PointerEventArgs e)
        {
            if (DataContext is ReaderViewModel vm)
            {
                var point = e.GetPosition(this);
                var height = this.Bounds.Height;
                // Footer is taller (~120px+), so we need a larger threshold at bottom
                var topThreshold = 100.0;
                var bottomThreshold = 200.0; 

                bool shouldShow = (point.Y < topThreshold) || (point.Y > height - bottomThreshold);

                if (vm.IsMenuVisible != shouldShow)
                {
                    vm.IsMenuVisible = shouldShow;
                }
            }
        }
    }
}
