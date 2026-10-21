using System.Globalization;
using Avalonia.Data.Converters;

namespace VTC.Client;

/// <summary>布尔取反（用于"未登录才显示登录层"）。</summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}

/// <summary>"0"→空 隐藏，"1"→可见（未读徽标用）。</summary>
public sealed class UnreadVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int i) return i > 0;
        if (value is bool b) return b;
        return false;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value;
}
