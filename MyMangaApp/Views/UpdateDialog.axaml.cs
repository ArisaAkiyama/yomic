using Avalonia.Controls;
using MyMangaApp.ViewModels;

namespace MyMangaApp.Views
{
    public partial class UpdateDialog : Window
    {
        public UpdateDialog()
        {
            InitializeComponent();
        }

        protected override void OnDataContextChanged(System.EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is UpdateDialogViewModel vm)
            {
                vm.CloseAction = () => Close();
            }
        }
    }
}
