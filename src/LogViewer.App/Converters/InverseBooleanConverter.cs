using System.Globalization;
using System.Windows.Data;

namespace LogViewer.App.Converters;

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => !(value is true);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => !(value is true);
}
