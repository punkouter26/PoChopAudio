using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace PoChopAudio.WinUI.Common;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        var isVisible = value is Visibility v && v == Visibility.Visible;
        return Invert ? !isVisible : isVisible;
    }
}

public sealed class InvertedBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is bool b ? !b : false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return value is bool b ? !b : false;
    }
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var hasValue = value is not null;
        if (value is string s) hasValue = !string.IsNullOrWhiteSpace(s);
        if (Invert) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}




