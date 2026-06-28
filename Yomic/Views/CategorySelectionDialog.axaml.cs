using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using System.Collections.Generic;
using System.Linq;
using Yomic.Core.Models;

namespace Yomic.Views
{
    public partial class CategorySelectionDialog : Window
    {
        private readonly List<CategoryCheckBoxItem> _items = new();

        private class CategoryCheckBoxItem
        {
            public long Id { get; set; }
            public CheckBox CheckBox { get; set; } = null!;
        }

        public CategorySelectionDialog()
        {
            InitializeComponent();
        }

        public CategorySelectionDialog(List<Category> allCategories, List<long> checkedIds) : this()
        {
            var stack = this.FindControl<StackPanel>("CategoriesStack");
            if (stack == null) return;

            foreach (var category in allCategories)
            {
                var checkBox = new CheckBox
                {
                    IsChecked = checkedIds.Contains(category.Id),
                    Margin = new Avalonia.Thickness(0, 4),
                    Cursor = new Cursor(StandardCursorType.Hand)
                };

                // Use StackPanel as content to show color dot next to the category name
                var checkBoxContent = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
                
                // Color Dot
                checkBoxContent.Children.Add(new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = Brush.Parse(category.Color),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                });

                // Name Text
                checkBoxContent.Children.Add(new TextBlock
                {
                    Text = category.Name,
                    Foreground = GetThemeBrush("PrimaryText", Brushes.White),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    FontSize = 14
                });

                checkBox.Content = checkBoxContent;

                stack.Children.Add(checkBox);
                _items.Add(new CategoryCheckBoxItem { Id = category.Id, CheckBox = checkBox });
            }

            if (!allCategories.Any())
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "No custom categories found. Add categories from 'Manage Categories' first.",
                    Foreground = GetThemeBrush("SecondaryText", Brushes.Gray),
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle = FontStyle.Italic,
                    Margin = new Avalonia.Thickness(0, 10),
                    FontSize = 13
                });
            }
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            Close(null);
        }

        private void OnSaveClick(object? sender, RoutedEventArgs e)
        {
            var selectedIds = _items.Where(i => i.CheckBox.IsChecked == true).Select(i => i.Id).ToList();
            Close(selectedIds);
        }

        private IBrush GetThemeBrush(string key, IBrush defaultBrush)
        {
            if (Application.Current != null && Application.Current.TryFindResource(key, out var res) && res is IBrush brush)
            {
                return brush;
            }
            return defaultBrush;
        }
    }
}
