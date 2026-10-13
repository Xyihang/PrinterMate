using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PrinterMate.App.Services;

namespace PrinterMate.App.Converters;

/// <summary>StatusLevel → 前景色(绿 / 橙 / 红 / 灰)。</summary>
public sealed class StatusLevelBrushConverter : IValueConverter
{
    private static readonly Brush Success = Freeze(Color.FromRgb(0x1B, 0x7F, 0x3B));
    private static readonly Brush Warning = Freeze(Color.FromRgb(0xB5, 0x6A, 0x00));
    private static readonly Brush Error = Freeze(Color.FromRgb(0xC0, 0x28, 0x28));
    private static readonly Brush Info = Freeze(Color.FromRgb(0x22, 0x33, 0x44));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is StatusLevel level
            ? level switch
            {
                StatusLevel.Success => Success,
                StatusLevel.Warning => Warning,
                StatusLevel.Error => Error,
                _ => Info,
            }
            : Info;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>布尔 → 颜色:true 绿 / false 红。</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    private static readonly Brush TrueBrush = Freeze(Color.FromRgb(0x1B, 0x7F, 0x3B));
    private static readonly Brush FalseBrush = Freeze(Color.FromRgb(0xC0, 0x28, 0x28));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? TrueBrush : FalseBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>布尔取反 → Visibility(true 折叠 / false 显示)。</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>布尔取反 → 颜色:true 红 / false 绿(用于"启用即为风险"的开关项)。</summary>
public sealed class InverseBoolToBrushConverter : IValueConverter
{
    private static readonly Brush TrueBrush = Freeze(Color.FromRgb(0xC0, 0x28, 0x28));
    private static readonly Brush FalseBrush = Freeze(Color.FromRgb(0x1B, 0x7F, 0x3B));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? TrueBrush : FalseBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
