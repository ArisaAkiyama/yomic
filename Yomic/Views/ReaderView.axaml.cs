using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia; // Added for VisualTreeAttachmentEventArgs
using Avalonia.Threading; // Added for DispatcherTimer
using Yomic.ViewModels;
using System;

namespace Yomic.Views
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
            this.Focusable = true; 
            this.AttachedToVisualTree += OnAttachedToVisualTree;
            


            // Fix for "Jumping to top":
            // We use AddHandler with handledEventsToo: true to ensure we capture events.
            this.AddHandler(PointerMovedEvent, OnRootPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
            
            // Custom Scroll Speed Handler
            // ScrollViewer consumes the event by default, so we need handledEventsToo: true
            this.AddHandler(PointerWheelChangedEvent, OnReaderPointerWheelChanged, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);

            // Pan / Drag Handlers
            // Pan / Drag Handlers
            var mainScroll = this.FindControl<ScrollViewer>("MainScroll");
            if (mainScroll != null)
            {
                mainScroll.AddHandler(PointerPressedEvent, OnPanPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
                mainScroll.AddHandler(PointerReleasedEvent, OnPanPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
                mainScroll.AddHandler(PointerMovedEvent, OnPanPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
                mainScroll.AddHandler(PointerMovedEvent, OnPanPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
            }
            
            // AutoScroll Timer
            _autoScrollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16) // ~60fps
            };
            _autoScrollTimer.Tick += OnAutoScrollTick;
        }

        private DateTime _lastNavTime = DateTime.MinValue;

        private void OnReaderPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (DataContext is ReaderViewModel vm && vm.IsWebtoon && MainScroll != null)
            {
                // Multiplier for faster scrolling (Adjust custom speed here)
                double speedMultiplier = 3.0;
                
                // e.Delta.Y is usually 1.0 or -1.0 per tick
                double offsetChange = -e.Delta.Y * 50 * speedMultiplier; 
                
                // Apply new offset
                MainScroll.Offset = new Avalonia.Vector(MainScroll.Offset.X, MainScroll.Offset.Y + offsetChange);
                
                // Mark event as handled to prevent default slow scrolling
                e.Handled = true;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            
            if (DataContext is ReaderViewModel vm)
            {
                // Debounce / Cool-down for navigation (300ms) to ensure "1x click" feel
                bool isNavKey = (e.Key == Key.Right || e.Key == Key.Left);
                if (isNavKey && (DateTime.Now - _lastNavTime).TotalMilliseconds < 300)
                {
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Right)
                {
                    _lastNavTime = DateTime.Now;
                    vm.NextPageCommand.Execute().Subscribe(_ => { });
                    e.Handled = true;
                }
                else if (e.Key == Key.Left)
                {
                    _lastNavTime = DateTime.Now;
                    vm.PrevPageCommand.Execute().Subscribe(_ => { });
                    e.Handled = true;
                }
                else if (e.Key == Key.Space)
                {
                    vm.ToggleMenuCommand.Execute().Subscribe(_ => { });
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape && vm.IsFullscreen)
                {
                    // ESC to exit fullscreen
                    vm.ToggleFullscreenCommand.Execute().Subscribe(_ => { });
                    e.Handled = true;
                }
                else if (e.Key == Key.F11)
                {
                    // F11 to toggle fullscreen
                    vm.ToggleFullscreenCommand.Execute().Subscribe(_ => { });
                    e.Handled = true;
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
                // Mapping: 0 to (Count-1)  ->  0 to MaxScroll
                
                int maxIndex = Math.Max(1, vm.Pages.Count - 1);
                var percent = e.NewValue / maxIndex;
                
                if (MainScroll != null)
                {
                    double maxScroll = Math.Max(0, MainScroll.Extent.Height - MainScroll.Viewport.Height);
                    var offset = percent * maxScroll;
                    MainScroll.Offset = new Avalonia.Vector(MainScroll.Offset.X, offset);
                }
            }
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (_isDraggingSlider) return; // Don't fight the user

            if (DataContext is ReaderViewModel vm && vm.IsWebtoon)
            {
                if (MainScroll == null || MainScroll.Extent.Height <= 0) return;

                // Sync Scroll -> Slider Index
                // Mapping: 0 to MaxScroll -> 0 to (Count-1)
                
                double maxScroll = Math.Max(1, MainScroll.Extent.Height - MainScroll.Viewport.Height);
                var percent = MainScroll.Offset.Y / maxScroll;
                
                // Clamp percent (can be slightly >1 or <0 due to rubber banding or rounding)
                percent = Math.Clamp(percent, 0.0, 1.0);
                
                var estimatedIndex = (int)(percent * (vm.Pages.Count - 1));
                
                // Clamp Index
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
                
                // --- Footer Logic ---
                // Strict trigger (50px from bottom) to show
                // Relaxed threshold (150px from bottom) to keep open
                double bottomThresh = vm.IsFooterVisible ? 150.0 : 50.0;
                bool showFooter = (point.Y > height - bottomThresh);
                
                if (vm.IsFooterVisible != showFooter)
                {
                    vm.IsFooterVisible = showFooter;
                }

                // --- Header Logic ---
                // Strict trigger (50px from top) to show
                // Relaxed threshold (80px from top) to keep open
                double topThresh = vm.IsHeaderVisible ? 80.0 : 50.0;
                bool showHeader = (point.Y < topThresh);

                if (vm.IsHeaderVisible != showHeader)
                {
                    vm.IsHeaderVisible = showHeader;
                }
            }
        }
        private void OnPageAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (sender is Control control && control.DataContext is PageViewModel page)
            {
                // Lazy Load when attached to visual tree (scrolled into view)
                page.Load();
            }
        }

        // --- Drag / Pan / AutoScroll Support ---
        private bool _isPanning = false;
        private bool _isAutoScrolling = false;
        private Point _lastPanPosition;
        
        // AutoScroll Vars
        private DispatcherTimer _autoScrollTimer;
        private Point _autoScrollAnchorPosition;
        private double _autoScrollSpeedY = 0;
        private Canvas? _autoScrollCanvas;
        private Border? _autoScrollAnchor;

        private void OnPanPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var props = e.GetCurrentPoint(this).Properties;
            if (!props.IsLeftButtonPressed || MainScroll == null) return;
            
            if (DataContext is ReaderViewModel vm && vm.ZoomScale <= 1.5)
            {
                // --- AUTO SCROLL MODE (Zoom <= 150%) ---
                _isAutoScrolling = true;
                _autoScrollAnchorPosition = e.GetPosition(this);
                
                // Show Anchor
                if (_autoScrollCanvas == null) _autoScrollCanvas = this.FindControl<Canvas>("AutoScrollCanvas");
                if (_autoScrollAnchor == null) _autoScrollAnchor = this.FindControl<Border>("AutoScrollAnchor");
                
                if (_autoScrollCanvas != null && _autoScrollAnchor != null)
                {
                    _autoScrollCanvas.IsVisible = true;
                    // Position the anchor centered on the click
                    Canvas.SetLeft(_autoScrollAnchor, _autoScrollAnchorPosition.X);
                    Canvas.SetTop(_autoScrollAnchor, _autoScrollAnchorPosition.Y);
                }
                
                // Capture pointer
                if (sender is Control control) e.Pointer.Capture(control);
                
                // Start Timer
                _autoScrollSpeedY = 0;
                _autoScrollTimer.Start();
                this.Cursor = new Cursor(StandardCursorType.SizeNorthSouth); // North-South Arrows
            }
            else
            {
                // --- PAN MODE (Zoom > 100%) ---
                _isPanning = true;
                _lastPanPosition = e.GetPosition(this);
                
                if (sender is Control control) e.Pointer.Capture(control);
                this.Cursor = new Cursor(StandardCursorType.SizeAll); // Hand/All Arrows
            }
        }

        private void OnPanPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_isAutoScrolling)
            {
                _isAutoScrolling = false;
                _autoScrollTimer.Stop();
                _autoScrollSpeedY = 0;
                
                if (_autoScrollCanvas != null) _autoScrollCanvas.IsVisible = false;
                
                e.Pointer.Capture(null);
                this.Cursor = Cursor.Default;
            }
            else if (_isPanning)
            {
                _isPanning = false;
                e.Pointer.Capture(null);
                this.Cursor = Cursor.Default;
            }
        }

        private void OnPanPointerMoved(object? sender, PointerEventArgs e)
        {
            if (MainScroll == null) return;

            if (_isAutoScrolling)
            {
                var currentPosition = e.GetPosition(this);
                
                // Calculate Speed based on distance from Anchor
                // Deadzone of 20px
                double dy = currentPosition.Y - _autoScrollAnchorPosition.Y;
                
                if (Math.Abs(dy) < 20)
                {
                    _autoScrollSpeedY = 0;
                }
                else
                {
                    // Linear speed scaling: (Distance - Deadzone) * Multiplier
                    // Adjust multiplier for sensitivity
                    double val = (dy > 0) ? (dy - 20) : (dy + 20);
                    _autoScrollSpeedY = val * 0.5; // Multiplier
                }
            }
            else if (_isPanning)
            {
                var currentPosition = e.GetPosition(this);
                var delta = _lastPanPosition - currentPosition; 

                // Apply new offset
                MainScroll.Offset = new Vector(MainScroll.Offset.X + delta.X, MainScroll.Offset.Y + delta.Y);
                
                _lastPanPosition = currentPosition;
            }
        }
        
        private void OnAutoScrollTick(object? sender, EventArgs e)
        {
            if (_isAutoScrolling && MainScroll != null && Math.Abs(_autoScrollSpeedY) > 0.1)
            {
                 MainScroll.Offset = new Vector(MainScroll.Offset.X, MainScroll.Offset.Y + _autoScrollSpeedY);
            }
        }
        
        private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            this.Focus();
            
        }
        

    }
}
