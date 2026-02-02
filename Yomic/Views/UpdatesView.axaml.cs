using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using Yomic.ViewModels;

namespace Yomic.Views
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
