namespace StockSimulator.Models;

/// <summary>
/// 工程文件（*.ksproj.json）根对象。
/// 保存整套分段参数、手绘数据、标注、滑点分段、快照，可完整还原工作现场。
/// </summary>
public sealed class ProjectDocument
{
    public const string CurrentVersion = "1.0";

    public string FormatVersion { get; set; } = CurrentVersion;

    public string App { get; set; } = "StockSimulator";

    public DateTime SavedAt { get; set; } = DateTime.Now;

    /// <summary>A股规则开关。</summary>
    public MarketRuleSet Rules { get; set; } = new();

    /// <summary>生成全局参数。</summary>
    public KlineGenerationSettings Settings { get; set; } = new();

    /// <summary>分段参数（Tab1）。</summary>
    public List<SegmentConfig> Segments { get; set; } = new();

    /// <summary>当前工作K线（可能是生成结果，也可能是导入的真实行情 + 手绘修改）。</summary>
    public List<Kline> Bars { get; set; } = new();

    /// <summary>K线文字标注。</summary>
    public List<Annotation> Annotations { get; set; } = new();

    /// <summary>滑点分段。</summary>
    public List<SlippageSegment> Slippages { get; set; } = new();

    /// <summary>快照列表（可选保存，体积较大）。</summary>
    public List<SnapshotRecord> Snapshots { get; set; } = new();

    /// <summary>数据来源标记：Generated / Imported / Manual。</summary>
    public string DataSource { get; set; } = "Generated";
}
