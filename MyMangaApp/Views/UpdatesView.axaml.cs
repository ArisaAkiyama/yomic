using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using MyMangaApp.ViewModels;

namespace MyMangaApp.Views
{
    public partial class UpdatesView : ReactiveUserControl<UpdatesViewModel>
    {
        public UpdatesView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
