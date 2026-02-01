using ReactiveUI;
using System.Reactive;

namespace MyMangaApp.ViewModels
{
    public class ErrorViewModel : ViewModelBase
    {
        private string _title = "Connection Failed";
        public string Title
        {
            get => _title;
            set => this.RaiseAndSetIfChanged(ref _title, value);
        }

        private string _message = "Unable to connect. Please check your internet.";
        public string Message
        {
            get => _message;
            set => this.RaiseAndSetIfChanged(ref _message, value);
        }

        public ReactiveCommand<Unit, Unit> RetryCommand { get; }

        public ErrorViewModel(ReactiveCommand<Unit, Unit> retryCommand)
        {
            RetryCommand = retryCommand;
        }

        // Default constructor for design-time usage
        public ErrorViewModel()
        {
            RetryCommand = ReactiveCommand.Create(() => { });
        }
    }
}
