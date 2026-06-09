using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Yomic.Views
{
    public partial class ConfirmDialog : Window
    {
        public ConfirmDialog()
        {
            InitializeComponent();
        }

        public ConfirmDialog(string title, string message) : this()
        {
            var titleText = this.FindControl<TextBlock>("TitleText");
            if (titleText != null) titleText.Text = title;
            
            var messageText = this.FindControl<TextBlock>("MessageText");
            if (messageText != null) messageText.Text = message;
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            Close(false);
        }

        private void OnConfirmClick(object? sender, RoutedEventArgs e)
        {
            Close(true);
        }
    }
}
