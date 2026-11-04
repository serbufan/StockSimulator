using StockSimulator.Models;

namespace StockSimulator.Core;

/// <summary>
/// 趋势线 → K线 转换器。
/// 手绘模式下用户在画布上拉一条直线（起点价 → 终点价），本类按线性/对数路径
/// 插值出每根K线的收盘价，并叠加噪声与影线，生成自然的走势带。
/// </summary>
public static class TrendLineBuilder
{
    public static List<Kline> Build(
        double startPrice,
        double endPrice,
        int barCount,
        double volatility,
        DateTime startTime,
        BarPeriod period,
        MarketRuleSet rules,
        long baseVolume = 1_000_000,
        int seed = 1,
        double wickFactor = 0.4,
        bool logarithmic = true)
    {
        var bars = new List<Kline>();
        if (barCount <= 0 || startPrice <= 0 || endPrice <= 0) return bars;

        var rng = new DeterministicRandom(seed);
        var time = TradingCalendar.AlignToPeriod(startTime, period);
        var vol = Math.Max(0.0005, volatility);

        var logStart = Math.Log(startPrice);
        var logEnd = Math.Log(endPrice);
        var prevClose = startPrice;

        for (var i = 0; i < barCount; i++)
        {
            var t = barCount == 1 ? 1.0 : (double)(i + 1) / barCount;

            // 目标价位：对数路径插值（等比涨跌更贴近真实行情）
            var anchor = logarithmic
                ? Math.Exp(logStart + (logEnd - logStart) * t)
                : startPrice + (endPrice - startPrice) * t;

            // 噪声随 t 轻微放大，靠近端点时收敛，保证首尾价格接近用户拉线
            var envelope = Math.Sin(Math.PI * Math.Min(1.0, Math.Max(0.0, t))) * 0.8 + 0.2;
            var close = anchor * Math.Exp(rng.NextGaussian() * vol * envelope);
            var open = prevClose;

            var wick = vol * envelope * wickFactor;
            var high = Math.Max(open, close) * (1 + Math.Abs(rng.NextGaussian()) * wick);
            var low = Math.Min(open, close) * (1 - Math.Abs(rng.NextGaussian()) * wick);

            var limitUp = rules.LimitUp(prevClose);
            var limitDown = rules.LimitDown(prevClose);
            close = Math.Clamp(close, limitDown, limitUp);
            high = Math.Min(high, limitUp);
            low = Math.Max(low, limitDown);

            open = rules.RoundToTick(open);
            high = rules.RoundToTick(high);
            low = rules.RoundToTick(low);
            close = rules.RoundToTick(close);
            if (low <= 0) low = Math.Max(0.01, rules.RoundToTick(high * 0.9));

            var volume = (long)Math.Max(100, baseVolume * (0.75 + rng.NextDouble() * 0.5)
                                                  * (1 + 4 * Math.Abs(close - open) / open));
            var bar = new Kline(time, open, high, low, close, volume, volume * (high + low) / 2);
            bar.FixConsistency();
            bars.Add(bar);

            prevClose = close;
            time = TradingCalendar.Next(time, period);
        }

        return bars;
    }
}
