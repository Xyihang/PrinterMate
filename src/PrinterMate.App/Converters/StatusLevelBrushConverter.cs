using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PrinterMate.App.Services;

namespace PrinterMate.App.Converters;

/// <summary>
/// 状态级别 → 画刷。
///
/// 颜色【不再硬编码在 C# 里】:统一从 App 资源字典按令牌键解析,
/// 让 Resources/Tokens.xaml 成为唯一的颜色真相来源(此前这里与令牌各存一份、值还不一致)。
/// 若解析不到(例如单元测试里没有 Application 实例),回落到冻结的兜底画刷,行为不变。
/// </summary>
internal static class TokenBrushes
{
    public static Brush Resolve(string tokenKey, byte r, byte g, byte b)
        => Application.Current?.TryFindResource(tokenKey) as Brush ?? Freeze(Color.FromRgb(r, g, b));

    public static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>状态级别 → 画刷(成功 / 警示 / 错误 / 信息)。</summary>
public sealed class StatusLevelBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is StatusLevel level
            ? level switch
            {
                StatusLevel.Success => TokenBrushes.Resolve("Brush.Success", 0x10, 0x7C, 0x10),
                StatusLevel.Warning => TokenBrushes.Resolve("Brush.Warning", 0xB4, 0x53, 0x09),
                StatusLevel.Error => TokenBrushes.Resolve("Brush.Danger", 0xC4, 0x2B, 0x1C),
                _ => TokenBrushes.Resolve("Brush.TextPrimary", 0x1F, 0x2A, 0x37),
            }
            : TokenBrushes.Resolve("Brush.TextPrimary", 0x1F, 0x2A, 0x37);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>布尔 → 画刷:true 绿 / false 红。</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true
            ? TokenBrushes.Resolve("Brush.Success", 0x10, 0x7C, 0x10)
            : TokenBrushes.Resolve("Brush.Danger", 0xC4, 0x2B, 0x1C);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>布尔取反 → Visibility(true 折叠 / false 显示)。</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>布尔取反 → 画刷:true 红 / false 绿(用于"启用即为风险"的开关项)。</summary>
public sealed class InverseBoolToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true
            ? TokenBrushes.Resolve("Brush.Danger", 0xC4, 0x2B, 0x1C)
            : TokenBrushes.Resolve("Brush.Success", 0x10, 0x7C, 0x10);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
