using Avalonia.Controls;

namespace Yomic.Views
{
    public partial class ExtensionsView : UserControl
    {
        public ExtensionsView()
        {
            InitializeComponent();
        }

        protected override void OnDataContextChanged(System.EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is ViewModels.ExtensionsViewModel vm)
            {
                vm.OpenFilePickerAsync = async () =>
                {
                    var topLevel = TopLevel.GetTopLevel(this);
                    if (topLevel == null) return null;

                    var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                    {
                        Title = "Select Extension DLL",
                        AllowMultiple = false,
                        FileTypeFilter = new[] 
                        { 
                            new Avalonia.Platform.Storage.FilePickerFileType("Mihon Extensions") { Patterns = new[] { "*.dll" } } 
                        }
                    });

                    return files.Count > 0 ? files[0] : null;
                };
            }
        }
    }
}
