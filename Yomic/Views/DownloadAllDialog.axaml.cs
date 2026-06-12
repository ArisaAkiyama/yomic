using Avalonia.Controls;
using Avalonia.Interactivity;
using Yomic.ViewModels;

namespace Yomic.Views
{
    public partial class DownloadAllDialog : Window
    {
        public DownloadAllDialog()
        {
            InitializeComponent();
        }

        public DownloadAllDialog(DownloadAllDialogInfo info) : this()
        {
            MangaTitleText.Text = info.MangaTitle;
            TotalCountText.Text = info.TotalChapters.ToString();
            MissingCountText.Text = info.NotDownloadedCount.ToString();
            UnreadCountText.Text = info.UnreadNotDownloadedCount.ToString();
            UnreadOption.IsEnabled = info.UnreadNotDownloadedCount > 0;
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            Close(null);
        }

        private void OnStartClick(object? sender, RoutedEventArgs e)
        {
            var mode = UnreadOption.IsChecked == true
                ? DownloadAllMode.UnreadNotDownloaded
                : DownloadAllMode.NotDownloaded;

            Close(mode);
        }
    }
}
