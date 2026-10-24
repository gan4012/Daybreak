using System.Globalization;
using System.Windows.Data;

namespace Daybreak.Launcher;

/// <summary>布尔取反(常用于 IsBusy 反控按钮可用性)。</summary>
public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : value;
}
