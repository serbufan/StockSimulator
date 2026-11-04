using StockSimulator.Models;

namespace StockSimulator.Core;

/// <summary>行情序列统计计算（快照对比、变体评分用）。</summary>
public static class KlineStatistics
{
    /// <summary>计算统计指标。annualFactor 为一年交易日数，用于年化波动率。</summary>
    public static KlineStats Compute(IReadOnlyList<Kline> bars, int annualFactor = 240)
    {
        var stats = new KlineStats();
        if (bars == null || bars.Count == 0) return stats;

        stats.BarCount = bars.Count;
        stats.FirstTime = bars[0].Time;
        stats.LastTime = bars[^1].Time;
        stats.FirstClose = bars[0].Close;
        stats.LastClose = bars[^1].Close;
        stats.MaxHigh = bars.Max(b => b.High);
        stats.MinLow = bars.Min(b => b.Low);
        stats.TotalVolume = bars.Sum(b => (double)b.Volume);
        stats.AvgVolume = stats.TotalVolume / bars.Count;
        stats.ChangePct = stats.FirstClose == 0 ? 0 : stats.LastClose / stats.FirstClose - 1;

        // 最大回撤
        var peak = double.MinValue;
        var maxDd = 0.0;
        foreach (var b in bars)
        {
            if (b.Close > peak) peak = b.Close;
            if (peak > 0)
            {
                var dd = 1 - b.Close / peak;
                if (dd > maxDd) maxDd = dd;
            }
        }

        stats.MaxDrawdown = maxDd;

        // 单根收益标准差
        var returns = new List<double>();
        for (var i = 1; i < bars.Count; i++)
        {
            if (bars[i - 1].Close > 0 && bars[i].Close > 0)
                returns.Add(Math.Log(bars[i].Close / bars[i - 1].Close));
        }

        if (returns.Count > 1)
        {
            var mean = returns.Average();
            var variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
            stats.BarVolatility = Math.Sqrt(Math.Max(0, variance));
            stats.AnnualVolatility = stats.BarVolatility * Math.Sqrt(annualFactor);
        }

        return stats;
    }

    /// <summary>两组序列的关键指标差异（用于快照对比）。</summary>
    public static List<(string Name, string A, string B, string Delta)> Compare(KlineStats a, KlineStats b)
    {
        return new List<(string, string, string, string)>
        {
            ("K线根数", a.BarCount.ToString(), b.BarCount.ToString(), (b.BarCount - a.BarCount).ToString("+0;-0;0")),
            ("起始日期", a.FirstTime.ToString("yyyy-MM-dd"), b.FirstTime.ToString("yyyy-MM-dd"), "-"),
            ("结束日期", a.LastTime.ToString("yyyy-MM-dd"), b.LastTime.ToString("yyyy-MM-dd"), "-"),
            ("期末价格", $"{a.LastClose:F2}", $"{b.LastClose:F2}", $"{b.LastClose - a.LastClose:+0.00;-0.00;0.00}"),
            ("区间涨跌幅", a.ChangePctDisplay, b.ChangePctDisplay, $"{b.ChangePct - a.ChangePct:+0.00%;-0.00%;0.00%}"),
            ("最大回撤", a.MaxDrawdownDisplay, b.MaxDrawdownDisplay, $"{b.MaxDrawdown - a.MaxDrawdown:+0.00%;-0.00%;0.00%}"),
            ("年化波动率", a.AnnualVolatilityDisplay, b.AnnualVolatilityDisplay, $"{b.AnnualVolatility - a.AnnualVolatility:+0.00%;-0.00%;0.00%}"),
            ("最高/最低", a.PriceRangeDisplay, b.PriceRangeDisplay, "-"),
            ("平均成交量", a.AvgVolumeDisplay, b.AvgVolumeDisplay, $"{(b.AvgVolume - a.AvgVolume) / 10000:+0.##;-0.##;0}万"),
        };
    }
}
