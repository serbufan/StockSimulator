using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StockSimulator.Models;

/// <summary>突发事件类型。</summary>
public enum MarketEventType
{
    /// <summary>跳空利好（向上一字跳空开盘）。</summary>
    GapUp = 0,

    /// <summary>跳空利空。</summary>
    GapDown = 1,

    /// <summary>停牌（若干根K线，OHLC 全部等于前收，成交量为 0）。</summary>
    Suspend = 2,

    /// <summary>除权除息（现金分红，价格向下调整 Magnitude 元）。</summary>
    Dividend = 3,

    /// <summary>送股/拆股（价格按 Magnitude 比例缩放，如 0.5 表示 10 送 10）。</summary>
    Split = 4,
}

/// <summary>
/// 插入到某个行情阶段中的突发事件。
/// AtBarIndex 为“阶段内第几根K线”（0 起），不是全局序号。
/// </summary>
public sealed partial class MarketEvent : ObservableObject
{
    [ObservableProperty] private MarketEventType _type = MarketEventType.GapUp;

    /// <summary>阶段内触发位置（第 N 根）。</summary>
    [ObservableProperty] private int _atBarIndex = 0;

    /// <summary>
    /// 幅度：跳空类为比例（0.03 = 跳空 3%），分红类为每股现金（元），送股类为缩股比例（0.5 = 10送10）。
    /// </summary>
    [ObservableProperty] private double _magnitude = 0.03;

    /// <summary>停牌持续根数（仅停牌事件使用）。</summary>
    [ObservableProperty] private int _durationBars = 3;

    [ObservableProperty] private string _note = string.Empty;

    public string TypeDisplay => Type switch
    {
        MarketEventType.GapUp => "跳空利好",
        MarketEventType.GapDown => "跳空利空",
        MarketEventType.Suspend => "停牌",
        MarketEventType.Dividend => "除权除息",
        MarketEventType.Split => "送股拆股",
        _ => Type.ToString(),
    };

    partial void OnTypeChanged(MarketEventType value) => OnPropertyChanged(nameof(TypeDisplay));

    public MarketEvent Clone() => new()
    {
        Type = Type,
        AtBarIndex = AtBarIndex,
        Magnitude = Magnitude,
        DurationBars = DurationBars,
        Note = Note,
    };
}

/// <summary>
/// 行情阶段参数：一个阶段内使用同一组统计特征生成若干根K线。
/// 多阶段首尾相接即构成完整时间轴。
/// </summary>
public sealed partial class SegmentConfig : ObservableObject
{
    [ObservableProperty] private string _name = "新阶段";

    /// <summary>本阶段生成的K线根数。</summary>
    [ObservableProperty] private int _barCount = 60;

    /// <summary>趋势漂移：每根K线的期望对数收益（0.001 ≈ 每根 +0.1%）。</summary>
    [ObservableProperty] private double _drift = 0.001;

    /// <summary>波动率：每根K线对数收益标准差（0.015 ≈ 每根 1.5%）。</summary>
    [ObservableProperty] private double _volatility = 0.015;

    /// <summary>单根K线最大涨跌幅（软约束，0.08 = 8%）。</summary>
    [ObservableProperty] private double _maxChangePct = 0.08;

    /// <summary>本阶段涨跌停比例（0 表示沿用全局A股规则；0.1/0.2/0.05 覆盖全局）。</summary>
    [ObservableProperty] private double _limitPct = 0;

    /// <summary>跳空概率（0~1）。</summary>
    [ObservableProperty] private double _gapProbability = 0.05;

    /// <summary>跳空幅度系数（相对波动率的倍数）。</summary>
    [ObservableProperty] private double _gapScale = 1.0;

    /// <summary>成交量系数（相对基准成交量）。</summary>
    [ObservableProperty] private double _volumeFactor = 1.0;

    /// <summary>阳线偏好：-1 全阴线倾向，0 中性，+1 全阳线倾向。</summary>
    [ObservableProperty] private double _bullBias = 0.0;

    /// <summary>均值回归强度（0 关闭；越大越容易被拉回均线，用于构造震荡/双顶形态）。</summary>
    [ObservableProperty] private double _meanReversion = 0.0;

    /// <summary>放量异动概率（0~1）。</summary>
    [ObservableProperty] private double _volumeSpikeProbability = 0.03;

    /// <summary>地量异动概率（0~1）。</summary>
    [ObservableProperty] private double _volumeDryProbability = 0.03;

    /// <summary>该阶段插入的突发事件。</summary>
    public ObservableCollection<MarketEvent> Events { get; set; } = new();

    public SegmentConfig Clone() => new()
    {
        Name = Name,
        BarCount = BarCount,
        Drift = Drift,
        Volatility = Volatility,
        MaxChangePct = MaxChangePct,
        LimitPct = LimitPct,
        GapProbability = GapProbability,
        GapScale = GapScale,
        VolumeFactor = VolumeFactor,
        BullBias = BullBias,
        MeanReversion = MeanReversion,
        VolumeSpikeProbability = VolumeSpikeProbability,
        VolumeDryProbability = VolumeDryProbability,
        Events = new ObservableCollection<MarketEvent>(Events.Select(e => e.Clone())),
    };

    public override string ToString() => $"{Name} ({BarCount}根)";
}

/// <summary>预置阶段模板（行情模板管理里可另存自定义模板）。</summary>
public static class SegmentPresets
{
    public sealed record Preset(string Name, Action<SegmentConfig> Apply);

    public static SegmentConfig Make(string name, double drift, double volatility, int bars,
        double bullBias = 0, double meanReversion = 0, double volumeFactor = 1.0, double gapProb = 0.05)
        => new()
        {
            Name = name,
            Drift = drift,
            Volatility = volatility,
            BarCount = bars,
            BullBias = bullBias,
            MeanReversion = meanReversion,
            VolumeFactor = volumeFactor,
            GapProbability = gapProb,
        };

    public static List<SegmentConfig> DefaultSegments() => new()
    {
        Make("1-缩量筑底", 0.0002, 0.008, 40, bullBias: -0.2, meanReversion: 0.35, volumeFactor: 0.6, gapProb: 0.02),
        Make("2-温和上升", 0.0035, 0.013, 60, bullBias: 0.45, meanReversion: 0.05, volumeFactor: 1.1),
        Make("3-加速主升", 0.0075, 0.024, 30, bullBias: 0.6, volumeFactor: 1.8, gapProb: 0.12),
        Make("4-高位震荡", 0.0, 0.02, 50, meanReversion: 0.5, volumeFactor: 1.2),
        Make("5-破位下跌", -0.006, 0.026, 40, bullBias: -0.55, volumeFactor: 1.4, gapProb: 0.1),
    };
}
