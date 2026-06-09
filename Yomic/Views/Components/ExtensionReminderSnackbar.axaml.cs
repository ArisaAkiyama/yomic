using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.Diagnostics;

namespace Yomic.Views.Components
{
    public partial class ExtensionReminderSnackbar : UserControl
    {
        // URLs for action buttons
        private const string DownloadUrl = "https://github.com/ArisaAkiyama/extension-yomic";
        private const string ContactDevUrl = "https://mail.google.com/mail/?view=cm&to=arisaakiyama12@gmail.com&su=Yomic%20Extension%20Issue";
        
        public ExtensionReminderSnackbar()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Shows the snackbar with slide-in animation
        /// </summary>
        public void Show()
        {
            IsVisible = true;
            Opacity = 1;
        }

        /// <summary>
        /// Hides the snackbar
        /// </summary>
        public void Hide()
        {
            IsVisible = false;
        }

        private void OnDownloadClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                // Open download URL in default browser
                Process.Start(new ProcessStartInfo
                {
                    FileName = DownloadUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Core.Services.LogService.Error("ExtensionReminder", "Failed to open download URL", ex);
            }
            
            Hide();
        }

        private void OnContactDevClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                // Open contact/issues URL in default browser
                Process.Start(new ProcessStartInfo
                {
                    FileName = ContactDevUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Core.Services.LogService.Error("ExtensionReminder", "Failed to open contact URL", ex);
            }
            
            Hide();
        }

        private void OnDismissClick(object? sender, RoutedEventArgs e)
        {
            Hide();
        }
    }
}
