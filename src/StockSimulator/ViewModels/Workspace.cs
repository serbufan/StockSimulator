using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StockSimulator.Core;
using StockSimulator.Models;

namespace StockSimulator.ViewModels;

/// <summary>
/// 工作区：四个 Tab 共享的“单一数据源”。
/// Bars 是当前正在编辑/导出的K线序列（可能来自生成、导入 CSV 或手绘）。
/// </summary>
public sealed partial class Workspace : ObservableObject
{
    private const int MaxHistory = 60;

    private readonly List<List<Kline>> _undo = new();
    private readonly List<List<Kline>> _redo = new();

    public MarketRuleSet Rules { get; } = new();

    public KlineGenerationSettings Settings { get; } = new();

    /// <summary>分段参数（Tab1）。</summary>
    public ObservableCollection<SegmentConfig> Segments { get; } = new();

    /// <summary>滑点分段（随K线一起导出）。</summary>
    public ObservableCollection<SlippageSegment> Slippages { get; } = new();

    /// <summary>K线文字标注。</summary>
    public ObservableCollection<Annotation> Annotations { get; } = new();

    /// <summary>当前工作K线。</summary>
    public ObservableCollection<Kline> Bars { get; } = new();

    /// <summary>快照（Tab4 对比）。</summary>
    public ObservableCollection<SnapshotRecord> Snapshots { get; } = new();

    /// <summary>数据来源：Generated / Imported / Manual / Variant。</summary>
    [ObservableProperty] private string _dataSource = "Generated";

    /// <summary>K线数据发生结构性变化（新增/删除/替换/撤销），画布需要重绘。</summary>
    public event EventHandler? BarsChanged;

    /// <summary>撤销/重做可用状态变化。</summary>
    public event EventHandler? HistoryChanged;

    public Workspace()
    {
        foreach (var s in SegmentPresets.DefaultSegments()) Segments.Add(s);
        Slippages.Add(new SlippageSegment());
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>在修改前调用：把当前序列压入撤销栈。</summary>
    public void BeginEdit()
    {
        _undo.Add(Bars.Select(b => b.Clone()).ToList());
        if (_undo.Count > MaxHistory) _undo.RemoveAt(0);
        _redo.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>放弃最后一次 BeginEdit 记录（未产生实际修改时调用）。</summary>
    public void CancelEdit()
    {
        if (_undo.Count > 0) _undo.RemoveAt(_undo.Count - 1);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var snapshot = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(Bars.Select(b => b.Clone()).ToList());
        ApplySnapshot(snapshot);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var snapshot = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(Bars.Select(b => b.Clone()).ToList());
        ApplySnapshot(snapshot);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplySnapshot(List<Kline> snapshot)
    {
        Bars.Clear();
        foreach (var b in snapshot) Bars.Add(b);
        BarsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>整体替换K线序列。</summary>
    public void ReplaceBars(IEnumerable<Kline> bars, bool recordUndo = true, string? source = null)
    {
        if (recordUndo) BeginEdit();
        Bars.Clear();
        foreach (var b in bars) Bars.Add(b);
        if (source != null) DataSource = source;
        BarsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddBar(Kline bar)
    {
        Bars.Add(bar);
        BarsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NotifyBarsChanged() => BarsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>载入工程文档内容。</summary>
    public void LoadFrom(ProjectDocument doc)
    {
        CopyRules(doc.Rules);
        CopySettings(doc.Settings);

        Segments.Clear();
        foreach (var s in doc.Segments) Segments.Add(s);

        Slippages.Clear();
        foreach (var s in doc.Slippages) Slippages.Add(s);
        if (Slippages.Count == 0) Slippages.Add(new SlippageSegment());

        Annotations.Clear();
        foreach (var a in doc.Annotations) Annotations.Add(a);

        Snapshots.Clear();
        foreach (var s in doc.Snapshots) Snapshots.Add(s);

        _undo.Clear();
        _redo.Clear();
        ReplaceBars(doc.Bars, recordUndo: false, source: doc.DataSource);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>导出为工程文档。</summary>
    public ProjectDocument ToDocument() => new()
    {
        Rules = Rules.Clone(),
        Settings = Settings.Clone(),
        Segments = Segments.Select(s => s.Clone()).ToList(),
        Bars = Bars.Select(b => b.Clone()).ToList(),
        Annotations = Annotations.Select(a => a.Clone()).ToList(),
        Slippages = Slippages.Select(s => s.Clone()).ToList(),
        Snapshots = Snapshots.Select(s => new SnapshotRecord
        {
            Name = s.Name,
            CreatedAt = s.CreatedAt,
            Remark = s.Remark,
            Bars = s.Bars.Select(b => b.Clone()).ToList(),
            Stats = s.Stats,
        }).ToList(),
        DataSource = DataSource,
    };

    public void CopyRules(MarketRuleSet src)
    {
        Rules.Symbol = src.Symbol;
        Rules.DisplayName = src.DisplayName;
        Rules.EnablePriceLimit = src.EnablePriceLimit;
        Rules.LimitMode = src.LimitMode;
        Rules.CustomLimitPct = src.CustomLimitPct;
        Rules.TickSize = src.TickSize;
        Rules.VolumeInLots = src.VolumeInLots;
        Rules.RoundPricesToTick = src.RoundPricesToTick;
    }

    public void CopySettings(KlineGenerationSettings src)
    {
        Settings.StartPrice = src.StartPrice;
        Settings.StartDate = src.StartDate;
        Settings.Period = src.Period;
        Settings.BaseVolume = src.BaseVolume;
        Settings.Seed = src.Seed;
        Settings.WickFactor = src.WickFactor;
        Settings.MeanReversionWindow = src.MeanReversionWindow;
        Settings.EnableVolumeOutlier = src.EnableVolumeOutlier;
        Settings.VolumeLotSize = src.VolumeLotSize;
    }

    /// <summary>当前序列的统计指标。</summary>
    public KlineStats CurrentStats => KlineStatistics.Compute(Bars);
}
