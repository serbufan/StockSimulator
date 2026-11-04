using StockSimulator.Core;
using StockSimulator.Models;

namespace StockSimulator.Tests;

/// <summary>多周期转换、批量变体、平滑与噪声测试。</summary>
public class ConversionTests
{
    private static List<Kline> MakeDaily(int count = 5, double start = 10.0)
    {
        var bars = new List<Kline>();
        var price = start;
        var rnd = new DeterministicRandom(7);
        for (var i = 0; i < count; i++)
        {
            var open = price;
            var close = Math.Round(open * (1 + rnd.NextGaussian() * 0.02), 2);
            var high = Math.Round(Math.Max(open, close) * 1.015, 2);
            var low = Math.Round(Math.Min(open, close) * 0.985, 2);
            bars.Add(new Kline(new DateTime(2024, 3, 4).AddDays(i), open, high, low, close, 1_000_000));
            price = close;
        }

        return bars;
    }

    [Fact]
    public void ExpandToIntraday_ProducesBarsWithinDailyRange_AndKeepsVolume()
    {
        var daily = MakeDaily(3);
        var intraday = MultiPeriodConverter.ExpandToIntraday(daily, BarPeriod.Minute30, 42);

        Assert.Equal(3 * 8, intraday.Count);

        for (var d = 0; d < daily.Count; d++)
        {
            var slice = intraday.Skip(d * 8).Take(8).ToList();
            Assert.Equal(daily[d].Open, slice[0].Open, 2);
            Assert.Equal(daily[d].Close, slice[^1].Close, 2);
            Assert.All(slice, b =>
            {
                Assert.True(b.High <= daily[d].High + 0.011, $"分钟线最高价 {b.High} 超出日线 {daily[d].High}");
                Assert.True(b.Low >= daily[d].Low - 0.011, $"分钟线最低价 {b.Low} 低于日线 {daily[d].Low}");
                Assert.True(b.Open > 0 && b.High >= b.Low);
            });
            Assert.Equal(daily[d].Volume, slice.Sum(b => b.Volume));
        }

        // 时间递增
        for (var i = 1; i < intraday.Count; i++)
            Assert.True(intraday[i].Time > intraday[i - 1].Time);
    }

    [Fact]
    public void Aggregate_RecoversDailyBarFromIntraday()
    {
        var daily = MakeDaily(3);
        var intraday = MultiPeriodConverter.ExpandToIntraday(daily, BarPeriod.Minute15, 42);
        var back = MultiPeriodConverter.Aggregate(intraday, BarPeriod.Day);

        Assert.Equal(3, back.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(daily[i].Date(), back[i].Time.Date);
            Assert.Equal(daily[i].Open, back[i].Open, 2);
            Assert.Equal(daily[i].Close, back[i].Close, 2);
            Assert.Equal(intraday.Skip(i * 16).Take(16).Max(b => b.High), back[i].High, 2);
            Assert.Equal(intraday.Skip(i * 16).Take(16).Min(b => b.Low), back[i].Low, 2);
            Assert.Equal(daily[i].Volume, back[i].Volume);
        }
    }

    [Fact]
    public void VariantGenerator_ProducesRequestedCount()
    {
        var baseBars = MakeDaily(20);
        var options = new VariantOptions
        {
            Count = 5,
            PriceJitterPct = 0.01,
            VolumeJitterPct = 0.2,
            EnableGapInjection = true,
            GapCount = 1,
            SeedOffset = 100,
        };
        var rules = new MarketRuleSet();

        var variants = VariantGenerator.Generate(baseBars, options, 2024, rules);

        Assert.Equal(5, variants.Count);
        Assert.All(variants, v => Assert.Equal(20, v.Bars.Count));
        Assert.Equal(5, variants.Select(v => v.Stats.LastClose).Distinct().Count());
        Assert.All(variants, v => Assert.All(v.Bars, b => Assert.True(b.High >= b.Low)));
    }

    [Fact]
    public void Smooth_ReducesVolatility()
    {
        var bars = MakeDaily(60);
        var smoothed = NoiseSmoother.Smooth(bars, 7, 0.85);

        var before = KlineStatistics.Compute(bars).BarVolatility;
        var after = KlineStatistics.Compute(smoothed).BarVolatility;

        Assert.Equal(bars.Count, smoothed.Count);
        Assert.True(after < before, $"平滑后波动率 {after} 应小于 {before}");
    }

    [Fact]
    public void AddNoise_KeepsBarsValid()
    {
        var bars = MakeDaily(30);
        var rules = new MarketRuleSet { EnablePriceLimit = true, LimitMode = PriceLimitMode.Percent10 };
        var noisy = NoiseSmoother.AddNoise(bars, 0.015, 0.2, 999, rules);

        Assert.Equal(bars.Count, noisy.Count);
        var report = ConstraintValidator.Validate(noisy, rules);
        Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(i => i.Message)));
        Assert.NotEqual(bars[^1].Close, noisy[^1].Close);
    }

    [Fact]
    public void PatternLibrary_BuildsConsistentBars()
    {
        var rules = new MarketRuleSet();
        foreach (var pattern in PatternLibrary.All)
        {
            var bars = PatternLibrary.Build(pattern, 10, 40, new DateTime(2024, 1, 2), BarPeriod.Day, rules);
            Assert.Equal(40, bars.Count);
            Assert.All(bars, b => Assert.True(b.High >= Math.Max(b.Open, b.Close) && b.Low <= Math.Min(b.Open, b.Close)));
        }
    }

    [Fact]
    public void TrendLineBuilder_EndsNearTargetPrice()
    {
        var rules = new MarketRuleSet();
        var bars = TrendLineBuilder.Build(10, 12, 30, 0.01, new DateTime(2024, 1, 2), BarPeriod.Day, rules);

        Assert.Equal(30, bars.Count);
        Assert.InRange(bars[^1].Close, 11.7, 12.3);
        Assert.All(bars, b => Assert.True(b.High >= b.Low && b.Low > 0));
    }
}

internal static class TestExtensions
{
    public static DateTime Date(this Kline k) => k.Time.Date;
}
