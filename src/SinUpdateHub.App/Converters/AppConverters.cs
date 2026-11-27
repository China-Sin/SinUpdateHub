using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SinUpdateHub.App.Services;
using SinUpdateHub.Core.Models;

namespace SinUpdateHub.App.Converters;

/// <summary>bool → Visibility。ConverterParameter="invert" 取反。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>bool 取反。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}

/// <summary>null 或空字符串 → Collapsed；ConverterParameter="invert" 反向。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var empty = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            empty = !empty;
        }

        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>UpdateState → 状态圆点/文字颜色。</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    private static readonly Brush Updatable = Freeze(new SolidColorBrush(Color.FromRgb(0xBE, 0x96, 0xFF)));   // 紫
    private static readonly Brush Latest = Freeze(new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)));      // 绿
    private static readonly Brush Failed = Freeze(new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49)));      // 红
    private static readonly Brush Checking = Freeze(new SolidColorBrush(Color.FromRgb(0x58, 0xA6, 0xFF)));    // 蓝
    private static readonly Brush Unknown = Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)));     // 灰

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is UpdateState state
            ? state switch
            {
                UpdateState.UpdateAvailable => Updatable,
                UpdateState.UpToDate => Latest,
                UpdateState.Failed => Failed,
                UpdateState.Checking => Checking,
                _ => Unknown,
            }
            : Unknown;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>SourceKind → 来源标签颜色（GitHub 紫 / WinGet 青 / 官网 琥珀 / 网盘 蓝）。</summary>
public sealed class SourceToBrushConverter : IValueConverter
{
    private static readonly Brush GitHub = Freeze(new SolidColorBrush(Color.FromRgb(0xA3, 0x71, 0xF7)));
    private static readonly Brush WinGet = Freeze(new SolidColorBrush(Color.FromRgb(0x2F, 0xD9, 0xC4)));
    private static readonly Brush Custom = Freeze(new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22)));
    private static readonly Brush BaiduPan = Freeze(new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            SourceKind.WinGet => WinGet,
            SourceKind.Custom => Custom,
            SourceKind.BaiduPan => BaiduPan,
            _ => GitHub,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>枚举值 → bool（RadioButton 的 IsChecked 双向绑定）。ConverterParameter 为枚举名。</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || parameter is not string name)
        {
            return false;
        }

        return string.Equals(value.ToString(), name, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is string name && targetType.IsEnum)
        {
            return Enum.Parse(targetType, name, ignoreCase: true);
        }

        return Binding.DoNothing;
    }
}

/// <summary>0-1 的进度 → 百分比文本。</summary>
public sealed class ProgressToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d ? d.ToString("P0") : "0%";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>复选框勾选状态 → 对应图标字符（用 Segoe Fluent Icons / Segoe MDL2 Assets）。ConverterParameter 传未勾选字符。</summary>
public sealed class BoolToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var on = value is bool b && b;
        var off = parameter as string ?? "\uE73E";
        return on ? "\uE73E" : off;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>十六进制颜色串 → SolidColorBrush。ConverterParameter 可传默认回退色（默认 #2F75B5）。</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var fallback = parameter as string ?? "#2F75B5";
        return NoteImageHelper.ParseBrushOrFallback(value as string, fallback);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
