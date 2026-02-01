using ReactiveUI;
using System;
using System.Reactive;

namespace MyMangaApp.ViewModels
{
    public class WelcomeViewModel : ViewModelBase
    {
        private readonly MainWindowViewModel _mainViewModel;

        private int _currentSlideIndex = 0;
        public int CurrentSlideIndex
        {
            get => _currentSlideIndex;
            set => this.RaiseAndSetIfChanged(ref _currentSlideIndex, value);
        }

        private bool _isFirstSlide = true;
        public bool IsFirstSlide
        {
            get => _isFirstSlide;
            set => this.RaiseAndSetIfChanged(ref _isFirstSlide, value);
        }

        private bool _isLastSlide = false;
        public bool IsLastSlide
        {
            get => _isLastSlide;
            set => this.RaiseAndSetIfChanged(ref _isLastSlide, value);
        }

        // Dot Indicator Properties


        public ReactiveCommand<Unit, Unit> FinishCommand { get; }

        public WelcomeViewModel(MainWindowViewModel mainViewModel)
        {
            _mainViewModel = mainViewModel;



            FinishCommand = ReactiveCommand.Create(() => 
            {
                try
                {
                    _mainViewModel.CompleteOnboarding();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in FinishCommand: {ex.Message}");
                }
            });
        }

        private void UpdateSlideStatus()
        {
            IsFirstSlide = CurrentSlideIndex == 0;
            IsLastSlide = CurrentSlideIndex == 3;
        }
    }
}
