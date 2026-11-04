using StockSimulator.Models;

namespace StockSimulator.Core;

/// <summary>形态模板定义：用若干关键点（时间比例, 价格倍数）描述形态骨架。</summary>
public sealed class PatternDefinition
{
    public PatternDefinition(string name, string displayName, params (double T, double P)[] points)
    {
        Name = name;
        DisplayName = displayName;
        Points = points;
    }

    public string Name { get; }
    public string DisplayName { get; }
    public (double T, double P)[] Points { get; }

    public override string ToString() => DisplayName;
}

/// <summary>
/// 内置经典形态模板：双顶、双底、头肩顶、三角整理、一字横盘。
/// 既供手绘编辑器的“形态模板”画笔使用，也可作为分段生成的补充手段。
/// </summary>
public static class PatternLibrary
{
    public static IReadOnlyList<PatternDefinition> All { get; } = new List<PatternDefinition>
    {
        new("DoubleTop", "双顶", (0, 1.00), (0.18, 1.12), (0.30, 1.05), (0.45, 1.14), (0.60, 1.03), (0.78, 0.95), (1.0, 0.92)),
        new("DoubleBottom", "双底", (0, 1.00), (0.18, 0.90), (0.32, 0.97), (0.48, 0.89), (0.62, 0.98), (0.80, 1.04), (1.0, 1.06)),
        new("HeadShouldersTop", "头肩顶", (0, 1.00), (0.15, 1.08), (0.25, 1.03), (0.40, 1.17), (0.52, 1.04), (0.65, 1.10), (0.78, 1.02), (1.0, 0.92)),
        new("HeadShouldersBottom", "头肩底", (0, 1.00), (0.15, 0.93), (0.25, 0.98), (0.40, 0.86), (0.52, 0.97), (0.65, 0.91), (0.78, 0.99), (1.0, 1.08)),
        new("Triangle", "三角整理", (0, 1.00), (0.12, 1.08), (0.25, 0.95), (0.38, 1.05), (0.50, 0.975), (0.62, 1.02), (0.75, 0.99), (0.88, 1.005), (1.0, 1.00)),
        new("Flat", "一字横盘", (0, 1.00), (0.35, 1.002), (0.70, 0.999), (1.0, 1.00)),
    };

    public static PatternDefinition? Find(string name) =>
        All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? All.FirstOrDefault(p => p.DisplayName == name);

    /// <summary>
    /// 根据形态模板生成K线。价格按 startPrice 缩放，加入轻微噪声避免过于机械。
    /// </summary>
    public static List<Kline> Build(PatternDefinition def, double startPrice, int barCount,
        DateTime startTime, BarPeriod period, MarketRuleSet rules, long baseVolume = 1_000_000,
        double noise = 0.004, int seed = 1, double wickFactor = 0.35)
    {
        var bars = new List<Kline>();
        if (def == null || barCount <= 0 || startPrice <= 0) return bars;

        var rng = new DeterministicRandom(seed);
        var time = TradingCalendar.AlignToPeriod(startTime, period);
        var prevClose = startPrice;

        for (var i = 0; i < barCount; i++)
        {
            var t = barCount == 1 ? 0 : (double)i / (barCount - 1);
            var target = Interpolate(def.Points, t) * startPrice;
            var open = prevClose;
            var close = target * (1 + rng.NextGaussian() * noise);

            var wick = Math.Abs(close - open) * wickFactor + startPrice * 0.002;
            var high = Math.Max(open, close) + Math.Abs(rng.NextGaussian()) * wick;
            var low = Math.Min(open, close) - Math.Abs(rng.NextGaussian()) * wick;
            if (low <= 0) low = 0.01;

            open = rules.RoundToTick(open);
            high = rules.RoundToTick(high);
            low = rules.RoundToTick(low);
            close = rules.RoundToTick(close);

            var volume = (long)Math.Max(100, baseVolume * (0.8 + rng.NextDouble() * 0.4));
            var bar = new Kline(time, open, high, low, close, volume, volume * (high + low) / 2);
            bar.FixConsistency();
            bars.Add(bar);

            prevClose = close;
            time = TradingCalendar.Next(time, period);
        }

        return bars;
    }

    /// <summary>在关键点之间线性插值。</summary>
    public static double Interpolate((double T, double P)[] points, double t)
    {
        if (points.Length == 0) return 1.0;
        if (t <= points[0].T) return points[0].P;
        if (t >= points[^1].T) return points[^1].P;

        for (var i = 0; i < points.Length - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            if (t >= a.T && t <= b.T)
            {
                var span = b.T - a.T;
                var k = span <= 1e-12 ? 0 : (t - a.T) / span;
                return a.P + (b.P - a.P) * k;
            }
        }

        return points[^1].P;
    }
}
