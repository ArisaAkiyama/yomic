using ReactiveUI;
using System;

namespace Yomic.ViewModels
{
    public class JsDebugViewModel : ViewModelBase
    {
        private readonly MainWindowViewModel _mainViewModel;

        public JsDebugViewModel(MainWindowViewModel mainViewModel)
        {
            _mainViewModel = mainViewModel;
        }
    }
}
