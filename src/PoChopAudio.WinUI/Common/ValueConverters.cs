using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

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

public sealed class SecondsToTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double sec)
        {
            var ts = TimeSpan.FromSeconds(sec);
            return ts.TotalMinutes >= 1
                ? $"{ts.Minutes}:{ts.Seconds:D2}.{ts.Milliseconds / 100:D1}"
                : $"{ts.Seconds}.{ts.Milliseconds / 100:D1}s";
        }
        return "0.0s";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class DbFormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double db)
        {
            return $"{db:F1} dB";
        }
        return "- dB";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

