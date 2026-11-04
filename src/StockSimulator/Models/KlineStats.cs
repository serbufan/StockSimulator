using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StockSimulator.Models;

/// <summary>行情序列统计指标（快照对比、变体对比共用）。</summary>
public sealed class KlineStats
{
    public int BarCount { get; set; }
    public DateTime FirstTime { get; set; }
    public DateTime LastTime { get; set; }
    public double FirstClose { get; set; }
    public double LastClose { get; set; }
    public double MaxHigh { get; set; }
    public double MinLow { get; set; }
    public double AvgVolume { get; set; }
    public double TotalVolume { get; set; }

    /// <summary>区间涨跌幅（首收 → 末收）。</summary>
    public double ChangePct { get; set; }

    /// <summary>最大回撤（正数）。</summary>
    public double MaxDrawdown { get; set; }

    /// <summary>单根K线收益标准差（未年化）。</summary>
    public double BarVolatility { get; set; }

    /// <summary>年化波动率（按 240 交易日/年 折算）。</summary>
    public double AnnualVolatility { get; set; }

    [JsonIgnore] public string ChangePctDisplay => $"{ChangePct:+0.00%;-0.00%;0.00%}";
    [JsonIgnore] public string MaxDrawdownDisplay => $"{MaxDrawdown:0.00%}";
    [JsonIgnore] public string AnnualVolatilityDisplay => $"{AnnualVolatility:0.00%}";
    [JsonIgnore] public string RangeDisplay => $"{FirstTime:yyyy-MM-dd} ~ {LastTime:yyyy-MM-dd}";
    [JsonIgnore] public string PriceRangeDisplay => $"{MinLow:0.00} ~ {MaxHigh:0.00}";
    [JsonIgnore] public string AvgVolumeDisplay => $"{AvgVolume / 10000:0.##}万";
}

/// <summary>快照：保存某一时刻的完整K线序列，用于前后对比。</summary>
public sealed partial class SnapshotRecord : ObservableObject
{
    [ObservableProperty] private string _name = "快照";

    [ObservableProperty] private DateTime _createdAt = DateTime.Now;

    [ObservableProperty] private string _remark = string.Empty;

    public List<Kline> Bars { get; set; } = new();

    /// <summary>生成时的统计指标（载入后由服务重算，反序列化时可直接使用）。</summary>
    public KlineStats Stats { get; set; } = new();

    public string Summary => $"{Name} | {Bars.Count}根 | {Stats.RangeDisplay}";
}
