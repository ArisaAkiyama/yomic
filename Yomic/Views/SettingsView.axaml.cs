using Avalonia.Controls;

namespace Yomic.Views
{
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
            this.DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object? sender, System.EventArgs e)
        {
            if (DataContext is Yomic.ViewModels.SettingsViewModel vm)
            {
                vm.RequestUpdateDialog -= OpenUpdateDialog;
                vm.RequestUpdateDialog += OpenUpdateDialog;
            }
        }

        private void OpenUpdateDialog()
        {
             var dialog = new UpdateDialog
            {
                DataContext = new Yomic.ViewModels.UpdateDialogViewModel()
            };
            
            if (this.VisualRoot is Window parentWindow)
            {
                dialog.ShowDialog(parentWindow);
            }
        }

        private void OnCheckUpdatesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            OpenUpdateDialog();
        }
    }
}
