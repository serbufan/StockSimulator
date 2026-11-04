using StockSimulator.Models;

namespace StockSimulator.Core;

/// <summary>
/// 噪声扰动与一键平滑。
/// 平滑：滑动平均作用于收盘价（可调强度），开盘价取上一根平滑收盘，保持走势连贯；
/// 扰动：整根K线按同一比例缩放（不破坏形态），成交量独立对数正态扰动。
/// </summary>
public static class NoiseSmoother
{
    /// <summary>
    /// 平滑行情。window 为滑动窗口（3~15 比较自然），strength 为平滑力度 0~1。
    /// </summary>
    public static List<Kline> Smooth(IReadOnlyList<Kline> bars, int window = 5, double strength = 0.7, bool smoothVolume = true)
    {
        var result = new List<Kline>();
        if (bars == null || bars.Count == 0) return result;

        window = Math.Clamp(window, 1, Math.Max(1, bars.Count));
        strength = Math.Clamp(strength, 0, 1);

        var closes = bars.Select(b => b.Close).ToArray();
        var smoothed = new double[closes.Length];
        for (var i = 0; i < closes.Length; i++)
        {
            var from = Math.Max(0, i - window / 2);
            var to = Math.Min(closes.Length - 1, i + window / 2);
            var sum = 0.0;
            for (var k = from; k <= to; k++) sum += closes[k];
            var ma = sum / (to - from + 1);
            smoothed[i] = closes[i] * (1 - strength) + ma * strength;
        }

        var volumes = bars.Select(b => (double)b.Volume).ToArray();
        var prevClose = bars[0].Open;

        for (var i = 0; i < bars.Count; i++)
        {
            var src = bars[i];
            var close = smoothed[i];
            var open = i == 0 ? src.Open : prevClose;

            // 用原K线的影线比例保留形态特征
            var upWick = Math.Max(1e-6, src.High - Math.Max(src.Open, src.Close));
            var dnWick = Math.Max(1e-6, Math.Min(src.Open, src.Close) - src.Low);
            var body = Math.Max(1e-6, Math.Abs(src.Close - src.Open));
            var upRatio = Math.Min(1.0, upWick / body);
            var dnRatio = Math.Min(1.0, dnWick / body);

            var newBody = Math.Abs(close - open);
            var high = Math.Max(open, close) + newBody * upRatio;
            var low = Math.Min(open, close) - newBody * dnRatio;
            if (low <= 0) low = Math.Max(0.01, Math.Min(open, close) * 0.99);

            long volume = src.Volume;
            if (smoothVolume)
            {
                var from = Math.Max(0, i - window / 2);
                var to = Math.Min(bars.Count - 1, i + window / 2);
                var sum = 0.0;
                for (var k = from; k <= to; k++) sum += volumes[k];
                var ma = sum / (to - from + 1);
                volume = (long)Math.Round(volumes[i] * (1 - strength) + ma * strength);
            }

            var bar = new Kline(src.Time, open, high, low, close, Math.Max(0, volume),
                Math.Max(0, volume) * (high + low + close) / 3.0, src.Flag)
            {
                AdjFactor = src.AdjFactor,
            };
            bar.FixConsistency();
            result.Add(bar);
            prevClose = close;
        }

        return result;
    }

    /// <summary>
    /// 叠加噪声扰动。priceJitter 为单根价格扰动标准差（0.01 = 1%），volumeJitter 同理。
    /// </summary>
    public static List<Kline> AddNoise(IReadOnlyList<Kline> bars, double priceJitter = 0.01,
        double volumeJitter = 0.15, int seed = 20240101, MarketRuleSet? rules = null)
    {
        var result = new List<Kline>();
        if (bars == null || bars.Count == 0) return result;

        var rng = new DeterministicRandom(seed);
        var prevClose = bars[0].Open;

        foreach (var src in bars)
        {
            // 同一根K线使用同一缩放因子，保持形态
            var factor = Math.Exp(rng.NextGaussian() * Math.Max(0, priceJitter));
            var open = src.Open * factor;
            var close = src.Close * factor;
            var high = src.High * factor;
            var low = src.Low * factor;

            if (rules is { EnablePriceLimit: true })
            {
                var limitUp = rules.LimitUp(prevClose);
                var limitDown = rules.LimitDown(prevClose);
                close = Math.Clamp(close, limitDown, limitUp);
                open = Math.Clamp(open, limitDown, limitUp);
                high = Math.Min(high, limitUp);
                low = Math.Max(low, limitDown);
            }

            if (rules != null)
            {
                open = rules.RoundToTick(open);
                close = rules.RoundToTick(close);
                high = rules.RoundToTick(high);
                low = rules.RoundToTick(low);
            }

            if (low <= 0) low = 0.01;

            var vol = (long)Math.Max(0, src.Volume * Math.Exp(rng.NextGaussian() * Math.Max(0, volumeJitter)));
            var bar = new Kline(src.Time, open, high, low, close, vol, vol * (high + low) / 2, src.Flag)
            {
                AdjFactor = src.AdjFactor,
            };
            bar.FixConsistency();
            result.Add(bar);
            prevClose = close;
        }

        return result;
    }
}
