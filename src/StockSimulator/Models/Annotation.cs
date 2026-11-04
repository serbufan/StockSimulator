using CommunityToolkit.Mvvm.ComponentModel;

namespace StockSimulator.Models;

/// <summary>K线文字标注：挂在某一根K线上方（或下方）。</summary>
public sealed partial class Annotation : ObservableObject
{
    /// <summary>目标K线在序列中的下标。</summary>
    [ObservableProperty] private int _barIndex;

    [ObservableProperty] private string _text = "标注";

    [ObservableProperty] private string _colorHex = "#FFB300";

    /// <summary>相对该K线最高价的偏移比例（正数在上方，负数在下方）。</summary>
    [ObservableProperty] private double _offsetPct = 0.03;

    public Annotation Clone() => new()
    {
        BarIndex = BarIndex,
        Text = Text,
        ColorHex = ColorHex,
        OffsetPct = OffsetPct,
    };
}

/// <summary>滑点分段：按K线序号区间设置滑点，导出 CSV 时随 K 线一并写出。</summary>
public sealed partial class SlippageSegment : ObservableObject
{
    [ObservableProperty] private string _name = "默认滑点";

    /// <summary>起始K线序号（含）。</summary>
    [ObservableProperty] private int _startIndex = 0;

    /// <summary>结束K线序号（含），使用 2147483647 表示到最后。</summary>
    [ObservableProperty] private int _endIndex = int.MaxValue;

    /// <summary>滑点（基点，1bp = 0.01%）。</summary>
    [ObservableProperty] private double _slippageBps = 5;

    /// <summary>滑点（最小报价单位跳数），与 bps 同时导出，回测软件可任选其一。</summary>
    [ObservableProperty] private double _slippageTicks = 1;

    public bool Contains(int index) => index >= StartIndex && index <= EndIndex;

    public SlippageSegment Clone() => new()
    {
        Name = Name,
        StartIndex = StartIndex,
        EndIndex = EndIndex,
        SlippageBps = SlippageBps,
        SlippageTicks = SlippageTicks,
    };
}
