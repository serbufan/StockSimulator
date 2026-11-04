using CommunityToolkit.Mvvm.ComponentModel;

namespace StockSimulator.Models;

/// <summary>K线生成引擎的全局参数（与分段参数一起决定最终输出）。</summary>
public sealed partial class KlineGenerationSettings : ObservableObject
{
    /// <summary>起始价格。</summary>
    [ObservableProperty] private double _startPrice = 10.00;

    /// <summary>起始日期（分钟线会自动对齐到 09:30）。</summary>
    [ObservableProperty] private DateTime _startDate = new(2024, 1, 2);

    /// <summary>生成周期。</summary>
    [ObservableProperty] private BarPeriod _period = BarPeriod.Day;

    /// <summary>基准成交量（股）。</summary>
    [ObservableProperty] private long _baseVolume = 2_000_000;

    /// <summary>随机种子。相同种子 + 相同参数 = 完全可复现的结果。</summary>
    [ObservableProperty] private int _seed = 20240101;

    /// <summary>影线强度（相对波动率）。</summary>
    [ObservableProperty] private double _wickFactor = 0.45;

    /// <summary>均值回归回看窗口。</summary>
    [ObservableProperty] private int _meanReversionWindow = 20;

    /// <summary>是否启用放量/地量随机异动。</summary>
    [ObservableProperty] private bool _enableVolumeOutlier = true;

    /// <summary>成交量最小跳动（股），默认 100 股（1 手）。</summary>
    [ObservableProperty] private int _volumeLotSize = 100;

    public KlineGenerationSettings Clone() => new()
    {
        StartPrice = StartPrice,
        StartDate = StartDate,
        Period = Period,
        BaseVolume = BaseVolume,
        Seed = Seed,
        WickFactor = WickFactor,
        MeanReversionWindow = MeanReversionWindow,
        EnableVolumeOutlier = EnableVolumeOutlier,
        VolumeLotSize = VolumeLotSize,
    };
}
