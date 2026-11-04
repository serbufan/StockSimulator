using StockSimulator.Models;

namespace StockSimulator.Core;

/// <summary>
/// 简化的A股交易日历：日线跳过周末；分钟线按 09:30-11:30 / 13:00-15:00 两个交易时段推进。
/// 不包含法定节假日（如需精确可自行补充 holiday 列表，见 <see cref="Holidays"/>）。
/// </summary>
public static class TradingCalendar
{
    public static readonly TimeSpan MorningOpen = new(9, 30, 0);
    public static readonly TimeSpan MorningClose = new(11, 30, 0);
    public static readonly TimeSpan AfternoonOpen = new(13, 0, 0);
    public static readonly TimeSpan AfternoonClose = new(15, 0, 0);

    /// <summary>可选的休市日（法定节假日）。默认为空。</summary>
    public static HashSet<DateTime> Holidays { get; } = new();

    public static bool IsTradingDay(DateTime date) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !Holidays.Contains(date.Date);

    public static DateTime NextTradingDay(DateTime date)
    {
        var d = date.Date;
        do
        {
            d = d.AddDays(1);
        } while (!IsTradingDay(d));

        return d;
    }

    /// <summary>
    /// 交易时段内的“分钟序号”：上午 0~119（09:30~11:29），下午 120~239（13:00~14:59）。
    /// 用于分钟线的推进、聚合分桶以及日内成交量 U 型分布。
    /// </summary>
    public static int SessionMinuteIndex(DateTime t)
    {
        var m = t.TimeOfDay.TotalMinutes;
        if (m < 690) return Math.Max(0, (int)(m - 570));      // 09:30 之前归零；上午
        if (m < 780) return 120;                              // 11:30 ~ 13:00 视为下午首根
        if (m < 900) return 120 + (int)(m - 780);             // 下午
        return 240;                                           // 15:00 之后
    }

    public static DateTime FromSessionMinute(DateTime date, int index)
    {
        if (index < 0) index = 0;
        return index < 120
            ? date.Date.AddMinutes(570 + index)
            : date.Date.AddMinutes(780 + (index - 120));
    }

    /// <summary>把任意时间对齐到周期的第一根K线时间。</summary>
    public static DateTime AlignToPeriod(DateTime time, BarPeriod period)
    {
        switch (period)
        {
            case BarPeriod.Day:
            case BarPeriod.Week:
            case BarPeriod.Month:
            {
                var d = time.Date;
                while (!IsTradingDay(d)) d = d.AddDays(1);
                return d;
            }
            default:
            {
                var step = period.ToMinutes();
                var idx = SessionMinuteIndex(time);
                idx = idx / step * step;
                var day = time.Date;
                while (!IsTradingDay(day)) day = day.AddDays(1);
                return FromSessionMinute(day, Math.Min(idx, 240 - step));
            }
        }
    }

    /// <summary>返回该周期下的下一根K线时间。</summary>
    public static DateTime Next(DateTime time, BarPeriod period)
    {
        switch (period)
        {
            case BarPeriod.Day:
            {
                var next = time.Date.AddDays(1);
                return IsTradingDay(next) ? next : NextTradingDay(time.Date);
            }
            case BarPeriod.Week:
            {
                var d = time.Date.AddDays(7);
                while (!IsTradingDay(d)) d = d.AddDays(1);
                return d;
            }
            case BarPeriod.Month:
                return new DateTime(time.Year, time.Month, 1).AddMonths(1);
            default:
            {
                var step = period.ToMinutes();
                var idx = SessionMinuteIndex(time) + step;
                if (idx >= 240)
                    return FromSessionMinute(NextTradingDay(time.Date), 0);
                return FromSessionMinute(time.Date, idx);
            }
        }
    }

    /// <summary>判断时间戳是否落在交易时段内。</summary>
    public static bool IsInSession(DateTime t, BarPeriod period)
    {
        if (period == BarPeriod.Day || period == BarPeriod.Week || period == BarPeriod.Month) return true;
        var tod = t.TimeOfDay;
        return (tod >= MorningOpen && tod < MorningClose) || (tod >= AfternoonOpen && tod < AfternoonClose);
    }
}
