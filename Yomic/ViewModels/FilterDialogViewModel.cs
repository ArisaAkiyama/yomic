using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Reactive;

namespace Yomic.ViewModels
{
    public class FilterOption
    {
        public string Name { get; set; } = "";
        public int Value { get; set; }
        public bool IsSelected { get; set; }
    }

    public class FilterDialogViewModel : ViewModelBase
    {
        public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
        public ReactiveCommand<Unit, Unit> ResetCommand { get; }
        public ReactiveCommand<Unit, Unit> CloseCommand { get; }

        // Status filter options
        public ObservableCollection<FilterOption> StatusOptions { get; } = new()
        {
            new FilterOption { Name = "All Status", Value = 0, IsSelected = true },
            new FilterOption { Name = "Ongoing", Value = 1, IsSelected = false },
            new FilterOption { Name = "Completed", Value = 2, IsSelected = false },
            new FilterOption { Name = "Licensed", Value = 3, IsSelected = false },
            new FilterOption { Name = "Publishing Finished", Value = 4, IsSelected = false },
            new FilterOption { Name = "Cancelled", Value = 5, IsSelected = false },
            new FilterOption { Name = "Hiatus", Value = 6, IsSelected = false }
        };

        // Sort by options
        public ObservableCollection<FilterOption> SortOptions { get; } = new()
        {
            new FilterOption { Name = "Latest Update", Value = 0, IsSelected = true },
            new FilterOption { Name = "A-Z", Value = 1, IsSelected = false },
            new FilterOption { Name = "Z-A", Value = 2, IsSelected = false },
            new FilterOption { Name = "Newest Added", Value = 3, IsSelected = false },
            new FilterOption { Name = "Most Views", Value = 4, IsSelected = false }
        };

        // Type filter
        public ObservableCollection<FilterOption> TypeOptions { get; } = new()
        {
            new FilterOption { Name = "All Types", Value = 0, IsSelected = true },
            new FilterOption { Name = "Manga", Value = 1, IsSelected = false },
            new FilterOption { Name = "Manhwa", Value = 2, IsSelected = false },
            new FilterOption { Name = "Manhua", Value = 3, IsSelected = false }
        };

        private int _selectedStatusIndex = 0;
        public int SelectedStatusIndex
        {
            get => _selectedStatusIndex;
            set => this.RaiseAndSetIfChanged(ref _selectedStatusIndex, value);
        }

        private int _selectedSortIndex = 0;
        public int SelectedSortIndex
        {
            get => _selectedSortIndex;
            set => this.RaiseAndSetIfChanged(ref _selectedSortIndex, value);
        }

        private int _selectedTypeIndex = 0;
        public int SelectedTypeIndex
        {
            get => _selectedTypeIndex;
            set => this.RaiseAndSetIfChanged(ref _selectedTypeIndex, value);
        }

        private int _minChapters = 0;
        public int MinChapters
        {
            get => _minChapters;
            set => this.RaiseAndSetIfChanged(ref _minChapters, value);
        }

        private bool _isOpen;
        public bool IsOpen
        {
            get => _isOpen;
            set => this.RaiseAndSetIfChanged(ref _isOpen, value);
        }

        public event Action? OnApply;
        public event Action? OnClose;

        public FilterDialogViewModel() 
        {
            ApplyCommand = ReactiveCommand.Create(() => 
            {
                Console.WriteLine($"[Filter] Status: {SelectedStatusIndex}, Sort: {SelectedSortIndex}, Type: {SelectedTypeIndex}, MinCh: {MinChapters}");
                OnApply?.Invoke();
                IsOpen = false;
            });

            ResetCommand = ReactiveCommand.Create(() => 
            {
                SelectedStatusIndex = 0;
                SelectedSortIndex = 0;
                SelectedTypeIndex = 0;
                MinChapters = 0;
            });

            CloseCommand = ReactiveCommand.Create(() =>
            {
                OnClose?.Invoke();
                IsOpen = false;
            });
        }

        public void Open()
        {
            IsOpen = true;
        }

        // Get filter values for API
        public int GetStatusFilter() => SelectedStatusIndex;
        public int GetSortFilter() => SelectedSortIndex;
        public int GetTypeFilter() => SelectedTypeIndex;
        public int GetMinChapters() => MinChapters;
    }
}
