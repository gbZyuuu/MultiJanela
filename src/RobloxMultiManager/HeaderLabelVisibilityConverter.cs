using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RobloxMultiManager;

public sealed class HeaderLabelVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length == 2 && values[0] is double width && values[1] is double font && width >= 550 * font / 14 + 50
            ? Visibility.Visible : Visibility.Collapsed;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
