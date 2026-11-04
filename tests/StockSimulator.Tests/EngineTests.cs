using StockSimulator.Core;
using StockSimulator.Models;

namespace StockSimulator.Tests;

/// <summary>核心引擎测试：确定性、涨跌停、事件、成交量、校验。</summary>
public class EngineTests
{
    private static (KlineGenerationSettings S, MarketRuleSet R) Make(int seed = 12345)
    {
        var s = new KlineGenerationSettings
        {
            StartPrice = 10.00,
            StartDate = new DateTime(2024, 1, 2),
            Period = BarPeriod.Day,
            BaseVolume = 1_000_000,
            Seed = seed,
            WickFactor = 0.45,
        };
        var r = new MarketRuleSet
        {
            EnablePriceLimit = true,
            LimitMode = PriceLimitMode.Percent10,
            TickSize = 0.01,
            RoundPricesToTick = true,
        };
        return (s, r);
    }

    private static List<SegmentConfig> Segments() => new()
    {
        SegmentPresets.Make("上升", 0.004, 0.02, 50, bullBias: 0.5, volumeFactor: 1.2),
        SegmentPresets.Make("下跌", -0.006, 0.03, 50, bullBias: -0.6, gapProb: 0.15),
    };

    [Fact]
    public void Generate_IsDeterministic_ForSameSeed()
    {
        var (s, r) = Make();
        var a = new KlineGenerator(s, r).Generate(Segments()).Bars;
        var b = new KlineGenerator(s, r).Generate(Segments()).Bars;

        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Time, b[i].Time);
            Assert.Equal(a[i].Open, b[i].Open, 6);
            Assert.Equal(a[i].High, b[i].High, 6);
            Assert.Equal(a[i].Low, b[i].Low, 6);
            Assert.Equal(a[i].Close, b[i].Close, 6);
            Assert.Equal(a[i].Volume, b[i].Volume);
        }
    }

    [Fact]
    public void Generate_DifferentSeed_ProducesDifferentSeries()
    {
        var (s1, r) = Make(1);
        var (s2, _) = Make(2);
        var a = new KlineGenerator(s1, r).Generate(Segments()).Bars;
        var b = new KlineGenerator(s2, r).Generate(Segments()).Bars;

        Assert.NotEqual(a[^1].Close, b[^1].Close);
    }

    [Fact]
    public void Generate_RespectsPriceLimit_AndTickAlignment()
    {
        var (s, r) = Make();
        var report = ConstraintValidator.Validate(new KlineGenerator(s, r).Generate(Segments()).Bars, r);

        Assert.DoesNotContain(report.Issues, i => i.Code is "LIMIT_UP_BREACH" or "LIMIT_DOWN_BREACH");
        Assert.DoesNotContain(report.Issues, i => i.Code is "OHLC_INCONSISTENT" or "PRICE_NON_POSITIVE");
        Assert.DoesNotContain(report.Issues, i => i.Code is "TICK_MISALIGNED");
        Assert.True(report.IsValid);
    }

    [Fact]
    public void Generate_ProducesPositiveLotAlignedVolume()
    {
        var (s, r) = Make();
        var bars = new KlineGenerator(s, r).Generate(Segments()).Bars;

        Assert.All(bars, b =>
        {
            Assert.True(b.Volume > 0);
            Assert.Equal(0, b.Volume % 100);
            Assert.True(b.Amount > 0);
            Assert.True(b.High >= Math.Max(b.Open, b.Close));
            Assert.True(b.Low <= Math.Min(b.Open, b.Close));
        });
    }

    [Fact]
    public void SuspendEvent_EmitsFlatZeroVolumeBars()
    {
        var (s, r) = Make();
        var seg = SegmentPresets.Make("含停牌", 0.001, 0.015, 20);
        seg.Events.Add(new MarketEvent { Type = MarketEventType.Suspend, AtBarIndex = 5, DurationBars = 3 });

        var bars = new KlineGenerator(s, r).Generate(new[] { seg }).Bars;

        Assert.Equal(20, bars.Count);
        for (var i = 5; i <= 7; i++)
        {
            Assert.True(bars[i].IsSuspended);
            Assert.Equal(0, bars[i].Volume);
            Assert.Equal(bars[i].Open, bars[i].High);
            Assert.Equal(bars[i].Open, bars[i].Low);
            Assert.Equal(bars[i].Open, bars[i].Close);
        }
    }

    [Fact]
    public void DividendEvent_AdjustsPriceAndFlag()
    {
        var (s, r) = Make();
        var seg = SegmentPresets.Make("除息", 0.0, 0.005, 20);
        seg.Events.Add(new MarketEvent { Type = MarketEventType.Dividend, AtBarIndex = 10, Magnitude = 0.5 });

        var bars = new KlineGenerator(s, r).Generate(new[] { seg }).Bars;

        Assert.True(bars[10].IsExRight);
        Assert.Equal("X", bars[10].Flag);
        Assert.True(bars[10].AdjFactor < 1.0);
        Assert.True(bars[10].Open < bars[9].Close, "除息日后开盘价应低于除息前收盘价");
    }

    [Fact]
    public void GapUpEvent_ForcesOpeningGap()
    {
        var (s, r) = Make();
        var seg = SegmentPresets.Make("跳空", 0.0, 0.003, 10, gapProb: 0);
        seg.Events.Add(new MarketEvent { Type = MarketEventType.GapUp, AtBarIndex = 4, Magnitude = 0.05 });

        var bars = new KlineGenerator(s, r).Generate(new[] { seg }).Bars;

        Assert.Contains('G', bars[4].Flag);
        var gapPct = bars[4].Open / bars[3].Close - 1;
        Assert.InRange(gapPct, 0.03, 0.07);
    }

    [Fact]
    public void Validator_DetectsBrokenBars()
    {
        var bars = new List<Kline>
        {
            new(new DateTime(2024, 1, 2), 10, 11, 9, 10.5, 1000),
            new(new DateTime(2024, 1, 3), 10.4, 10.5, 10.0, 12.0, -5),   // high < close，且量为负
        };
        var rules = new MarketRuleSet { EnablePriceLimit = true, LimitMode = PriceLimitMode.Percent10 };

        var report = ConstraintValidator.Validate(bars, rules);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, i => i.Code == "OHLC_INCONSISTENT");
        Assert.Contains(report.Issues, i => i.Code == "VOLUME_NEGATIVE");
        Assert.Contains(report.Issues, i => i.Code == "LIMIT_UP_BREACH");
    }

    [Fact]
    public void Repair_FixesBrokenBars()
    {
        var rules = new MarketRuleSet { EnablePriceLimit = true, LimitMode = PriceLimitMode.Percent10 };
        var bars = new List<Kline>
        {
            new(new DateTime(2024, 1, 2), 10, 11, 9, 10.5, 1000),
            new(new DateTime(2024, 1, 3), 10.4, 9.0, 12.0, 13.0, -5),
        };

        var changed = ConstraintValidator.Repair(bars, rules);

        Assert.True(changed > 0);
        Assert.True(bars[1].High >= Math.Max(bars[1].Open, bars[1].Close));
        Assert.True(bars[1].Low <= Math.Min(bars[1].Open, bars[1].Close));
        Assert.True(bars[1].Volume >= 0);
        Assert.True(bars[1].High <= 11.55);   // 10.5 * 1.1 涨停价
    }

    [Fact]
    public void Statistics_ComputesDrawdownAndVolatility()
    {
        var bars = new List<Kline>();
        var price = 10.0;
        for (var i = 0; i < 100; i++)
        {
            var next = price * (i < 50 ? 1.01 : 0.99);
            bars.Add(new Kline(new DateTime(2024, 1, 1).AddDays(i), price, Math.Max(price, next), Math.Min(price, next), next, 1000));
            price = next;
        }

        var stats = KlineStatistics.Compute(bars);

        Assert.Equal(100, stats.BarCount);
        Assert.True(stats.MaxDrawdown > 0);
        Assert.True(stats.AnnualVolatility > 0);
    }
}
