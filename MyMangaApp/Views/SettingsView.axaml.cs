using Avalonia.Controls;

namespace MyMangaApp.Views
{
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
        }

        private void OnCheckUpdatesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var dialog = new UpdateDialog
            {
                DataContext = new MyMangaApp.ViewModels.UpdateDialogViewModel()
            };
            
            if (this.VisualRoot is Window parentWindow)
            {
                dialog.ShowDialog(parentWindow);
            }
        }
    }
}
