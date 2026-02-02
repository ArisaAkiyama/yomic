using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Yomic.Views
{
    public partial class TrackingDialog : Window
    {
        public TrackingDialog()
        {
            InitializeComponent();
        }

        private void OnCloseClick(object? sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
