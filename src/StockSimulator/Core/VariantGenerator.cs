using StockSimulator.Models;

namespace StockSimulator.Core;

/// <summary>批量变体生成参数。</summary>
public sealed class VariantOptions
{
    /// <summary>生成份数。</summary>
    public int Count { get; set; } = 10;

    /// <summary>单根价格扰动标准差（0.012 = 1.2%）。</summary>
    public double PriceJitterPct { get; set; } = 0.012;

    /// <summary>成交量扰动标准差。</summary>
    public double VolumeJitterPct { get; set; } = 0.2;

    /// <summary>每份变体的整体趋势偏移幅度（区间后段累计漂移，0 = 不改变趋势）。</summary>
    public double TrendShiftPct { get; set; } = 0.0;

    /// <summary>是否注入随机跳空。</summary>
    public bool EnableGapInjection { get; set; } = true;

    /// <summary>每份变体注入的跳空次数（随机位置）。</summary>
    public int GapCount { get; set; } = 1;

    /// <summary>单次跳空幅度（比例）。</summary>
    public double GapSizePct { get; set; } = 0.04;

    /// <summary>种子步长，保证每份变体互不相同且可复现。</summary>
    public int SeedOffset { get; set; } = 1000;

    public VariantOptions Clone() => (VariantOptions)MemberwiseClone();
}

/// <summary>一份变体行情。</summary>
public sealed class VariantSeries
{
    public VariantSeries(int index, string name, List<Kline> bars, KlineStats stats)
    {
        Index = index;
        Name = name;
        Bars = bars;
        Stats = stats;
    }

    public int Index { get; }
    public string Name { get; }

    /// <summary>变体名称（用于文件名）。</summary>
    public string FileName => $"variant_{Index:D3}";

    public List<Kline> Bars { get; }
    public KlineStats Stats { get; }

    public int BarCount => Bars.Count;
    public string RangeDisplay => Stats.RangeDisplay;
    public string ChangePctDisplay => Stats.ChangePctDisplay;
    public string MaxDrawdownDisplay => Stats.MaxDrawdownDisplay;
    public string AnnualVolatilityDisplay => Stats.AnnualVolatilityDisplay;
    public string AvgVolumeDisplay => Stats.AvgVolumeDisplay;
    public double LastClose => Stats.LastClose;
    public string Summary => $"{Name} | {BarCount}根 | 涨跌 {ChangePctDisplay} | 回撤 {MaxDrawdownDisplay}";
}

/// <summary>
/// 批量变体生成：基于一套基础行情，生成多份带随机扰动的K线，用于网格策略的压力测试。
/// 每份变体使用独立种子，可完全复现。
/// </summary>
public static class VariantGenerator
{
    public static List<VariantSeries> Generate(IReadOnlyList<Kline> baseBars, VariantOptions options,
        int baseSeed = 20240101, MarketRuleSet? rules = null)
    {
        var result = new List<VariantSeries>();
        if (baseBars == null || baseBars.Count == 0 || options == null) return result;

        var count = Math.Clamp(options.Count, 1, 500);

        for (var i = 0; i < count; i++)
        {
            var seed = unchecked(baseSeed + options.SeedOffset * (i + 1));
            var bars = NoiseSmoother.AddNoise(baseBars, options.PriceJitterPct, options.VolumeJitterPct, seed, rules);

            if (options.TrendShiftPct > 1e-9)
                bars = ApplyTrendShift(bars, options.TrendShiftPct, seed + 17, rules);

            if (options.EnableGapInjection && options.GapCount > 0)
                bars = InjectGaps(bars, options.GapCount, options.GapSizePct, seed + 29, rules);

            var stats = KlineStatistics.Compute(bars);
            result.Add(new VariantSeries(i + 1, $"变体 {i + 1:D2}", bars, stats));
        }

        return result;
    }

    /// <summary>对整段行情叠加累计趋势偏移（后段偏移更大），模拟不同趋势强度的压力场景。</summary>
    private static List<Kline> ApplyTrendShift(IReadOnlyList<Kline> bars, double shiftPct, int seed, MarketRuleSet? rules)
    {
        var rng = new DeterministicRandom(seed);
        var slope = shiftPct * rng.NextSigned() / Math.Max(1, bars.Count);
        var result = new List<Kline>(bars.Count);
        var acc = 0.0;

        foreach (var b in bars)
        {
            acc += slope;
            var f = Math.Exp(acc);
            var bar = new Kline(b.Time, b.Open * f, b.High * f, b.Low * f, b.Close * f, b.Volume, 0, b.Flag)
            {
                AdjFactor = b.AdjFactor,
            };
            Normalize(bar, rules);
            bar.Amount = Math.Round(bar.Volume * (bar.High + bar.Low + bar.Close) / 3.0, 2);
            result.Add(bar);
        }

        return result;
    }

    /// <summary>在随机位置注入跳空（价格整体平移，保持 K 线形态）。</summary>
    private static List<Kline> InjectGaps(IReadOnlyList<Kline> bars, int gapCount, double gapPct, int seed, MarketRuleSet? rules)
    {
        if (bars.Count < 5) return bars.ToList();

        var rng = new DeterministicRandom(seed);
        var positions = new SortedSet<int>();
        var guard = 0;
        while (positions.Count < Math.Min(gapCount, bars.Count - 2) && guard++ < 100)
            positions.Add(rng.NextInt(bars.Count - 2) + 1);

        var result = new List<Kline>(bars.Count);
        var acc = 0.0;
        var gapPctAbs = Math.Abs(gapPct);
        var signs = positions.Select(_ => rng.NextDouble() < 0.5 ? -1 : 1).ToList();
        var signMap = positions.Select((p, idx) => (p, sign: signs[idx])).ToDictionary(x => x.p, x => x.sign);

        for (var i = 0; i < bars.Count; i++)
        {
            if (signMap.TryGetValue(i, out var sign))
                acc += sign * gapPctAbs;

            var b = bars[i];
            var f = Math.Exp(acc);
            var bar = new Kline(b.Time, b.Open * f, b.High * f, b.Low * f, b.Close * f, b.Volume, 0,
                string.IsNullOrEmpty(b.Flag) ? "G" : b.Flag + "G")
            {
                AdjFactor = b.AdjFactor,
            };
            Normalize(bar, rules);
            bar.Amount = Math.Round(bar.Volume * (bar.High + bar.Low + bar.Close) / 3.0, 2);
            result.Add(bar);
        }

        return result;
    }

    private static void Normalize(Kline bar, MarketRuleSet? rules)
    {
        if (rules != null)
        {
            bar.Open = rules.RoundToTick(bar.Open);
            bar.High = rules.RoundToTick(bar.High);
            bar.Low = rules.RoundToTick(bar.Low);
            bar.Close = rules.RoundToTick(bar.Close);
        }

        if (bar.Low <= 0) bar.Low = 0.01;
        bar.FixConsistency();
    }
}
