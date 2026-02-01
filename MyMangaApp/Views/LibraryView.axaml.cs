using Avalonia.Controls;
using Avalonia.Input;
using MyMangaApp.ViewModels;
using System;

namespace MyMangaApp.Views
{
    public partial class LibraryView : UserControl
    {
        public LibraryView()
        {
            InitializeComponent();
        }
        
        private void OnMangaCardPressed(object? sender, PointerPressedEventArgs e)
        {
            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsLeftButtonPressed)
            {
                if (sender is Border border && border.Tag is MangaItem item)
                {
                    if (DataContext is LibraryViewModel vm)
                    {
                        vm.OpenMangaCommand.Execute(item).Subscribe(_ => { });
                    }
                }
            }
        }

        private void OnOverlayDismiss(object? sender, PointerPressedEventArgs e)
        {
            if (DataContext is LibraryViewModel vm)
            {
                vm.IsFilterVisible = false;
            }
        }
    }
}
