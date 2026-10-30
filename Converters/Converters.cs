using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace LinBench.Wpf.Converters
{
    /// <summary>
    /// Binds a RadioButton.IsChecked to a string property.
    /// IsChecked = (value == ConverterParameter). When checked, writes ConverterParameter back.
    /// Usage: IsChecked="{Binding Mode, Converter={StaticResource StringEquals}, ConverterParameter=open}"
    /// </summary>
    public class StringEqualsConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? parameter?.ToString() ?? string.Empty : Binding.DoNothing;
    }

    /// <summary>Non-empty string -> Visible, empty/null -> Collapsed.</summary>
    public class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => string.IsNullOrWhiteSpace(value?.ToString()) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>true -> green, false -> red. Used for the backend-connected dot in the status bar.</summary>
    public class BoolToStatusBrushConverter : IValueConverter
    {
        private static readonly Brush Good = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x5B)));
        private static readonly Brush Bad = Freeze(new SolidColorBrush(Color.FromRgb(0xD1, 0x43, 0x43)));

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? Good : Bad;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>Stability-summary badge background: stable -> light green, unstable -> light red.</summary>
    public class BoolToStableBrushConverter : IValueConverter
    {
        private static readonly Brush Good = Freeze(new SolidColorBrush(Color.FromRgb(0xDC, 0xF5, 0xE6)));
        private static readonly Brush Bad = Freeze(new SolidColorBrush(Color.FromRgb(0xFD, 0xE2, 0xE2)));

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? Good : Bad;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
