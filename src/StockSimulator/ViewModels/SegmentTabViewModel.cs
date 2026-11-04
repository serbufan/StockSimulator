using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockSimulator.Core;
using StockSimulator.Models;

namespace StockSimulator.ViewModels;

/// <summary>时间轴上的一个阶段块。</summary>
public sealed class TimelineItem
{
    public required string Name { get; init; }
    public required int Bars { get; init; }
    public required double Width { get; init; }
    public required string ColorHex { get; init; }
    public required int StartIndex { get; init; }
    public required int EndIndex { get; init; }
    public required string Tooltip { get; init; }
    public required string RangeText { get; init; }
    public bool HasEvents { get; init; }
}

/// <summary>
/// Tab1：参数分段自动生成。
/// 负责分段列表/事件编辑、时间轴可视化、生成与校验。
/// </summary>
public sealed partial class SegmentTabViewModel : ObservableObject
{
    private static readonly string[] Palette =
    {
        "#4E79A7", "#59A14F", "#EDC948", "#E15759", "#B07AA1", "#76B7B2", "#FF9DA7", "#9C755F",
    };

    private readonly Workspace _ws;
    private readonly Action<string> _setStatus;

    public SegmentTabViewModel(Workspace workspace, Action<string> setStatus)
    {
        _ws = workspace;
        _setStatus = setStatus;

        _ws.Segments.CollectionChanged += OnSegmentsChanged;
        foreach (var s in _ws.Segments) HookSegment(s);
        RebuildTimeline();
        SelectedSegment = _ws.Segments.FirstOrDefault();
    }

    public Workspace Workspace => _ws;
    public MarketRuleSet Rules => _ws.Rules;
    public KlineGenerationSettings Settings => _ws.Settings;
    public ObservableCollection<SegmentConfig> Segments => _ws.Segments;
    public ObservableCollection<SlippageSegment> Slippages => _ws.Slippages;

    public IReadOnlyList<PriceLimitMode> LimitModes { get; } = Enum.GetValues<PriceLimitMode>();
    public IReadOnlyList<MarketEventType> EventTypes { get; } = Enum.GetValues<MarketEventType>();
    public IReadOnlyList<BarPeriod> Periods { get; } = Enum.GetValues<BarPeriod>();

    [ObservableProperty] private SegmentConfig? _selectedSegment;
    [ObservableProperty] private MarketEvent? _selectedEvent;
    [ObservableProperty] private SlippageSegment? _selectedSlippage;
    [ObservableProperty] private string _generationSummary = "尚未生成。";
    [ObservableProperty] private string _validationSummary = "尚未校验。";
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<TimelineItem> Timeline { get; } = new();
    public ObservableCollection<ValidationIssue> Issues { get; } = new();

    public int TotalBars => Segments.Sum(s => Math.Max(0, s.BarCount));

    public string TotalBarsText => $"共 {Segments.Count} 个阶段 / {TotalBars} 根K线";

    public bool HasSelectedSegment => SelectedSegment != null;

    partial void OnSelectedSegmentChanged(SegmentConfig? value)
    {
        SelectedEvent = value?.Events.FirstOrDefault();
        OnPropertyChanged(nameof(HasSelectedSegment));
    }

    // ---------------- 阶段操作 ----------------

    [RelayCommand]
    private void AddSegment()
    {
        var seg = SegmentPresets.Make($"阶段{Segments.Count + 1}", 0.001, 0.015, 60);
        _ws.Segments.Add(seg);
        SelectedSegment = seg;
        _setStatus("已新增阶段。");
    }

    [RelayCommand]
    private void CloneSegment()
    {
        if (SelectedSegment == null) return;
        var clone = SelectedSegment.Clone();
        clone.Name = SelectedSegment.Name + "-副本";
        var index = _ws.Segments.IndexOf(SelectedSegment);
        _ws.Segments.Insert(index + 1, clone);
        SelectedSegment = clone;
        _setStatus("已复制阶段。");
    }

    [RelayCommand]
    private void RemoveSegment()
    {
        if (SelectedSegment == null) return;
        var index = _ws.Segments.IndexOf(SelectedSegment);
        _ws.Segments.Remove(SelectedSegment);
        SelectedSegment = _ws.Segments.Count > 0
            ? _ws.Segments[Math.Clamp(index, 0, _ws.Segments.Count - 1)]
            : null;
        _setStatus("已删除阶段。");
    }

    [RelayCommand]
    private void MoveSegmentUp()
    {
        if (SelectedSegment == null) return;
        var i = _ws.Segments.IndexOf(SelectedSegment);
        if (i <= 0) return;
        _ws.Segments.Move(i, i - 1);
        RebuildTimeline();
    }

    [RelayCommand]
    private void MoveSegmentDown()
    {
        if (SelectedSegment == null) return;
        var i = _ws.Segments.IndexOf(SelectedSegment);
        if (i < 0 || i >= _ws.Segments.Count - 1) return;
        _ws.Segments.Move(i, i + 1);
        RebuildTimeline();
    }

    [RelayCommand]
    private void LoadDefaultPreset()
    {
        _ws.Segments.Clear();
        foreach (var s in SegmentPresets.DefaultSegments()) _ws.Segments.Add(s);
        SelectedSegment = _ws.Segments.FirstOrDefault();
        _setStatus("已载入内置五段行情模板。");
    }

    [RelayCommand]
    private void ClearSegments()
    {
        _ws.Segments.Clear();
        SelectedSegment = null;
        _setStatus("已清空阶段。");
    }

    [RelayCommand]
    private void ApplyQuickPreset(string? key)
    {
        if (SelectedSegment == null) return;
        switch (key)
        {
            case "up":
                SelectedSegment.Drift = 0.004;
                SelectedSegment.Volatility = 0.013;
                SelectedSegment.BullBias = 0.5;
                SelectedSegment.MeanReversion = 0.05;
                SelectedSegment.Name = SelectedSegment.Name.Contains("升") ? SelectedSegment.Name : SelectedSegment.Name + "-上升";
                break;
            case "down":
                SelectedSegment.Drift = -0.005;
                SelectedSegment.Volatility = 0.022;
                SelectedSegment.BullBias = -0.5;
                SelectedSegment.MeanReversion = 0.05;
                break;
            case "range":
                SelectedSegment.Drift = 0.0;
                SelectedSegment.Volatility = 0.016;
                SelectedSegment.BullBias = 0;
                SelectedSegment.MeanReversion = 0.55;
                break;
            case "squeeze":
                SelectedSegment.Drift = 0.0;
                SelectedSegment.Volatility = 0.006;
                SelectedSegment.VolumeFactor = 0.6;
                SelectedSegment.MeanReversion = 0.3;
                break;
            case "crash":
                SelectedSegment.Drift = -0.015;
                SelectedSegment.Volatility = 0.035;
                SelectedSegment.BullBias = -0.8;
                SelectedSegment.GapProbability = 0.2;
                SelectedSegment.VolumeFactor = 1.8;
                break;
        }

        RebuildTimeline();
        _setStatus($"已套用快速预设：{key}");
    }

    // ---------------- 事件 ----------------

    [RelayCommand]
    private void AddEvent()
    {
        if (SelectedSegment == null) return;
        var ev = new MarketEvent
        {
            AtBarIndex = Math.Max(0, SelectedSegment.BarCount / 2),
            Type = MarketEventType.GapUp,
            Magnitude = 0.03,
        };
        SelectedSegment.Events.Add(ev);
        SelectedEvent = ev;
        RebuildTimeline();
        _setStatus("已插入突发事件。");
    }

    [RelayCommand]
    private void RemoveEvent()
    {
        if (SelectedSegment == null || SelectedEvent == null) return;
        SelectedSegment.Events.Remove(SelectedEvent);
        SelectedEvent = SelectedSegment.Events.FirstOrDefault();
        RebuildTimeline();
    }

    // ---------------- 滑点 ----------------

    [RelayCommand]
    private void AddSlippage()
    {
        var last = _ws.Slippages.LastOrDefault();
        var start = last == null ? 0 : Math.Min(int.MaxValue - 1, last.EndIndex + 1);
        var seg = new SlippageSegment { Name = $"滑点分段{Slippages.Count + 1}", StartIndex = start, EndIndex = int.MaxValue };
        _ws.Slippages.Add(seg);
        SelectedSlippage = seg;
    }

    [RelayCommand]
    private void RemoveSlippage()
    {
        if (SelectedSlippage == null) return;
        _ws.Slippages.Remove(SelectedSlippage);
        SelectedSlippage = _ws.Slippages.FirstOrDefault();
    }

    // ---------------- 生成 / 校验 ----------------

    [RelayCommand]
    private void Generate()
    {
        if (Segments.Count == 0)
        {
            _setStatus("没有可用的阶段，请先添加阶段。");
            return;
        }

        IsBusy = true;
        try
        {
            var generator = new KlineGenerator(_ws.Settings, _ws.Rules);
            var result = generator.Generate(Segments);

            if (result.Bars.Count == 0)
            {
                GenerationSummary = "生成失败：" + string.Join("；", result.Warnings);
                _setStatus(GenerationSummary);
                return;
            }

            _ws.ReplaceBars(result.Bars, recordUndo: true, source: "Generated");
            GenerationSummary = result.Summary;
            _setStatus("生成完成：" + result.Summary +
                       (result.Warnings.Count > 0 ? $"（{result.Warnings.Count} 条告警）" : ""));

            if (result.Warnings.Count > 0)
                foreach (var w in result.Warnings.Take(20)) _setStatus("告警：" + w);

            Validate();
            RebuildTimeline();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Validate()
    {
        var report = ConstraintValidator.Validate(_ws.Bars, _ws.Rules);
        Issues.Clear();
        foreach (var issue in report.Issues.Take(500)) Issues.Add(issue);
        ValidationSummary = report.Summary;
        _setStatus("校验完成：" + report.Summary);
    }

    [RelayCommand]
    private void Repair()
    {
        if (_ws.Bars.Count == 0) return;
        _ws.BeginEdit();
        var changed = ConstraintValidator.Repair(_ws.Bars, _ws.Rules);
        _ws.NotifyBarsChanged();
        _setStatus($"一键修复完成，{changed} 根K线被修正。");
        Validate();
    }

    // ---------------- 时间轴 ----------------

    private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (SegmentConfig s in e.NewItems) HookSegment(s);
        if (e.OldItems != null)
        {
            foreach (SegmentConfig s in e.OldItems)
            {
                s.PropertyChanged -= SegmentOnPropertyChanged;
                s.Events.CollectionChanged -= OnEventsChanged;
            }
        }

        OnPropertyChanged(nameof(TotalBars));
        OnPropertyChanged(nameof(TotalBarsText));
        RebuildTimeline();
    }

    private void HookSegment(SegmentConfig s)
    {
        s.PropertyChanged -= SegmentOnPropertyChanged;
        s.PropertyChanged += SegmentOnPropertyChanged;
        s.Events.CollectionChanged -= OnEventsChanged;
        s.Events.CollectionChanged += OnEventsChanged;
    }

    private void SegmentOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SegmentConfig.BarCount) or nameof(SegmentConfig.Name))
        {
            OnPropertyChanged(nameof(TotalBars));
            OnPropertyChanged(nameof(TotalBarsText));
            RebuildTimeline();
        }
    }

    private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildTimeline();

    /// <summary>工程/模板载入后刷新（重新挂钩事件并重建时间轴）。</summary>
    public void RefreshFromWorkspace()
    {
        foreach (var s in _ws.Segments) HookSegment(s);
        SelectedSegment = _ws.Segments.FirstOrDefault();
        OnPropertyChanged(nameof(TotalBars));
        OnPropertyChanged(nameof(TotalBarsText));
        RebuildTimeline();
    }

    private void RebuildTimeline()
    {
        Timeline.Clear();
        var total = Math.Max(1, Segments.Sum(s => Math.Max(0, s.BarCount)));
        const double totalWidth = 660.0;
        var index = 0;

        for (var i = 0; i < Segments.Count; i++)
        {
            var s = Segments[i];
            var bars = Math.Max(0, s.BarCount);
            var width = Math.Max(14.0, bars / (double)total * totalWidth);
            Timeline.Add(new TimelineItem
            {
                Name = s.Name,
                Bars = bars,
                Width = width,
                ColorHex = Palette[i % Palette.Length],
                StartIndex = index,
                EndIndex = index + Math.Max(0, bars - 1),
                HasEvents = s.Events.Count > 0,
                Tooltip = $"{s.Name}\n{bars} 根 | 漂移 {s.Drift:P2}/根 | 波动 {s.Volatility:P2}\n" +
                          $"涨跌停 {(s.LimitPct > 0 ? s.LimitPct.ToString("P0") : "跟随全局")} | 跳空 {s.GapProbability:P0}\n" +
                          $"量能系数 {s.VolumeFactor:F2} | 阳线偏好 {s.BullBias:F2}" +
                          (s.Events.Count > 0 ? $"\n事件 {s.Events.Count} 个" : ""),
                RangeText = bars > 0 ? $"#{index}~#{index + bars - 1}" : "-",
            });
            index += bars;
        }
    }
}
