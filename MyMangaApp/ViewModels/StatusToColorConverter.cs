using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MyMangaApp.ViewModels
{
    public class StatusToColorConverter : IValueConverter
    {
        public static readonly StatusToColorConverter Instance = new();
        
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                return status switch
                {
                    "Ongoing" => new SolidColorBrush(Color.Parse("#22C55E")),    // Green
                    "Completed" => new SolidColorBrush(Color.Parse("#0078D7")),  // Blue
                    "Hiatus" => new SolidColorBrush(Color.Parse("#F59E0B")),     // Orange
                    "Cancelled" => new SolidColorBrush(Color.Parse("#EF4444")),  // Red
                    _ => new SolidColorBrush(Color.Parse("#6B7280"))              // Gray for Unknown
                };
            }
            return new SolidColorBrush(Color.Parse("#6B7280"));
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
