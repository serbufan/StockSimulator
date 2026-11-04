namespace StockSimulator.Models;

/// <summary>K线周期。日/周/月线的 Time 使用当日 00:00，分钟线使用交易时段的开始时间。</summary>
public enum BarPeriod
{
    Minute1 = 0,
    Minute5 = 1,
    Minute15 = 2,
    Minute30 = 3,
    Minute60 = 4,
    Day = 5,
    Week = 6,
    Month = 7,
}

public static class BarPeriodExtensions
{
    /// <summary>单根K线代表的分钟数（日线按 240 分钟交易时间折算，仅用于比较周期粗细）。</summary>
    public static int ToMinutes(this BarPeriod p) => p switch
    {
        BarPeriod.Minute1 => 1,
        BarPeriod.Minute5 => 5,
        BarPeriod.Minute15 => 15,
        BarPeriod.Minute30 => 30,
        BarPeriod.Minute60 => 60,
        BarPeriod.Day => 240,
        BarPeriod.Week => 240 * 5,
        BarPeriod.Month => 240 * 21,
        _ => 240,
    };

    public static bool IsIntraday(this BarPeriod p) => p.ToMinutes() < 240;

    public static string DisplayName(this BarPeriod p) => p switch
    {
        BarPeriod.Minute1 => "1分钟",
        BarPeriod.Minute5 => "5分钟",
        BarPeriod.Minute15 => "15分钟",
        BarPeriod.Minute30 => "30分钟",
        BarPeriod.Minute60 => "60分钟",
        BarPeriod.Day => "日线",
        BarPeriod.Week => "周线",
        BarPeriod.Month => "月线",
        _ => p.ToString(),
    };

    /// <summary>该周期一个交易日内的K线根数（日线及以上返回 1）。</summary>
    public static int BarsPerTradingDay(this BarPeriod p) => p switch
    {
        BarPeriod.Minute1 => 240,
        BarPeriod.Minute5 => 48,
        BarPeriod.Minute15 => 16,
        BarPeriod.Minute30 => 8,
        BarPeriod.Minute60 => 4,
        _ => 1,
    };
}
