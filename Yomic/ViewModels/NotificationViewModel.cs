using ReactiveUI;
using System.Reactive;
using System.Threading.Tasks;

namespace Yomic.ViewModels
{
    public enum NotificationType
    {
        Info,
        Success,
        Error
    }

    public class NotificationViewModel : ViewModelBase
    {
        private string _message = string.Empty;
        public string Message
        {
            get => _message;
            set => this.RaiseAndSetIfChanged(ref _message, value);
        }
        
        private bool _isVisible;
        public bool IsVisible
        {
            get => _isVisible;
            set => this.RaiseAndSetIfChanged(ref _isVisible, value);
        }

        private NotificationType _type;
        public NotificationType Type 
        {
            get => _type;
            set => this.RaiseAndSetIfChanged(ref _type, value);
        }

        // Helpers for View Binding
        public bool IsSuccess => Type == NotificationType.Success;
        public bool IsError => Type == NotificationType.Error;
        public bool IsInfo => Type == NotificationType.Info;

        public async void Show(string message, NotificationType type = NotificationType.Info)
        {
            Message = message;
            Type = type;
            // Trigger PropertyChanged for helper properties
            this.RaisePropertyChanged(nameof(IsSuccess));
            this.RaisePropertyChanged(nameof(IsError));
            this.RaisePropertyChanged(nameof(IsInfo));

            IsVisible = true;
            await Task.Delay(3000);
            IsVisible = false;
        }
    }
}
