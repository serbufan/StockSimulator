using CommunityToolkit.Mvvm.ComponentModel;

namespace StockSimulator.Models;

/// <summary>涨跌停档位。</summary>
public enum PriceLimitMode
{
    None = 0,
    Percent5 = 1,
    Percent10 = 2,
    Percent20 = 3,
    Custom = 4,
}

/// <summary>
/// A股交易规则开关集合：涨跌停、最小报价单位、T+1 等。
/// 生成、校验、导出三处共用同一份规则，保证一致性。
/// </summary>
public sealed partial class MarketRuleSet : ObservableObject
{
    [ObservableProperty] private string _symbol = "SIM001";

    [ObservableProperty] private string _displayName = "仿真标的";

    /// <summary>是否启用涨跌停限制。</summary>
    [ObservableProperty] private bool _enablePriceLimit = true;

    [ObservableProperty] private PriceLimitMode _limitMode = PriceLimitMode.Percent10;

    /// <summary>自定义涨跌幅（比例，0.1 表示 10%）。</summary>
    [ObservableProperty] private double _customLimitPct = 0.1;

    /// <summary>最小报价单位（A股默认 0.01 元）。</summary>
    [ObservableProperty] private double _tickSize = 0.01;

    /// <summary>成交量单位是否为“手”（1 手 = 100 股）。导出时统一换算为股。</summary>
    [ObservableProperty] private bool _volumeInLots = false;

    /// <summary>是否按 tick 取整价格。</summary>
    [ObservableProperty] private bool _roundPricesToTick = true;

    /// <summary>当前生效的涨跌幅限制比例；0 表示不限制。</summary>
    public double EffectiveLimitPct => !EnablePriceLimit ? 0 : LimitMode switch
    {
        PriceLimitMode.None => 0,
        PriceLimitMode.Percent5 => 0.05,
        PriceLimitMode.Percent10 => 0.10,
        PriceLimitMode.Percent20 => 0.20,
        PriceLimitMode.Custom => Math.Max(0, CustomLimitPct),
        _ => 0.10,
    };

    public string LimitDisplay => EffectiveLimitPct <= 0 ? "无限制" : $"±{EffectiveLimitPct:P0}";

    /// <summary>价格取整到最小报价单位。</summary>
    public double RoundToTick(double price)
    {
        if (!RoundPricesToTick || TickSize <= 0 || double.IsNaN(price) || double.IsInfinity(price))
            return price;

        var ticks = Math.Round(price / TickSize, MidpointRounding.AwayFromZero);
        var value = ticks * TickSize;
        // 消除浮点误差（例如 10.000000000000002）
        var digits = Math.Clamp((int)Math.Round(-Math.Log10(TickSize)), 0, 8);
        return Math.Round(value, digits, MidpointRounding.AwayFromZero);
    }

    public double LimitUp(double prevClose)
    {
        var pct = EffectiveLimitPct;
        return pct <= 0 ? double.PositiveInfinity : RoundToTick(prevClose * (1 + pct));
    }

    public double LimitDown(double prevClose)
    {
        var pct = EffectiveLimitPct;
        return pct <= 0 ? 0 : RoundToTick(prevClose * (1 - pct));
    }

    /// <summary>判断价格是否为最小报价单位的整数倍。</summary>
    public bool IsAlignedToTick(double price, double epsilon = 1e-6)
    {
        if (TickSize <= 0) return true;
        var ticks = price / TickSize;
        return Math.Abs(ticks - Math.Round(ticks)) <= epsilon;
    }

    public MarketRuleSet Clone() => new()
    {
        Symbol = Symbol,
        DisplayName = DisplayName,
        EnablePriceLimit = EnablePriceLimit,
        LimitMode = LimitMode,
        CustomLimitPct = CustomLimitPct,
        TickSize = TickSize,
        VolumeInLots = VolumeInLots,
        RoundPricesToTick = RoundPricesToTick,
    };
}
