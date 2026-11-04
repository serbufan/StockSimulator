using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using StockSimulator.Models;
using StockSimulator.ViewModels;

namespace StockSimulator.Converters;

/// <summary>#RRGGBB 字符串 → 画刷（时间轴色块、标注颜色）。</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            var hex = value as string;
            if (string.IsNullOrWhiteSpace(hex)) return Brushes.Gray;
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        }
        catch
        {
            return Brushes.Gray;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>枚举 → 中文显示名。</summary>
public sealed class EnumDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        PriceLimitMode m => m switch
        {
            PriceLimitMode.None => "不限制",
            PriceLimitMode.Percent5 => "±5%（ST股）",
            PriceLimitMode.Percent10 => "±10%（主板）",
            PriceLimitMode.Percent20 => "±20%（创业板/科创板）",
            PriceLimitMode.Custom => "自定义",
            _ => m.ToString(),
        },
        MarketEventType t => t switch
        {
            MarketEventType.GapUp => "跳空利好",
            MarketEventType.GapDown => "跳空利空",
            MarketEventType.Suspend => "停牌",
            MarketEventType.Dividend => "除权除息",
            MarketEventType.Split => "送股拆股",
            _ => t.ToString(),
        },
        BarPeriod p => p.DisplayName(),
        DrawTool t => t switch
        {
            DrawTool.Select => "选择/拖拽",
            DrawTool.DrawBar => "单K绘制",
            DrawTool.TrendLine => "趋势线生成",
            DrawTool.Erase => "橡皮擦",
            DrawTool.DrawVolume => "成交量笔",
            DrawTool.Pattern => "形态模板",
            _ => t.ToString(),
        },
        _ => value?.ToString() ?? string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 索引 → Visibility：与 ConverterParameter 相等时显示，否则 Hidden（保留控件实例与 ScottPlot 画布状态）。
/// </summary>
public sealed class IndexToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int index) return System.Windows.Visibility.Hidden;
        if (parameter is null) return System.Windows.Visibility.Hidden;
        return int.TryParse(parameter.ToString(), out var target) && index == target
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Hidden;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>bool → Visibility（true 显示）。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        if (Invert) b = !b;
        return b ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
