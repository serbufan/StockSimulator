using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockSimulator.Core;
using StockSimulator.Models;

namespace StockSimulator.ViewModels;

/// <summary>画布工具。</summary>
public enum DrawTool
{
    Select = 0,
    DrawBar = 1,
    TrendLine = 2,
    Erase = 3,
    DrawVolume = 4,
    Pattern = 5,
}

/// <summary>画布缩放请求。</summary>
public enum CanvasZoomAction
{
    In,
    Out,
    Fit,
}

/// <summary>拖拽过程中的实时预览（由画布控件负责绘制）。</summary>
public sealed class CanvasPreview
{
    public bool IsTrend { get; init; }
    public int StartIndex { get; init; }
    public int EndIndex { get; init; }
    public double StartPrice { get; init; }
    public double EndPrice { get; init; }
    public double High { get; init; }
    public double Low { get; init; }
}

/// <summary>
/// 画布交互状态机（与具体绘图库无关，便于测试）。
/// 控件只负责：像素 ↔ 坐标换算、把鼠标位置翻译成 (barIndex, price, volumeFraction) 后调用这里。
///
/// 支持：选择/拖拽修改K线、单K绘制、趋势线生成K线、橡皮擦、手绘成交量、形态模板插入、网格/最小价位吸附。
/// 所有会改动数据的操作都会先写入 Workspace 的撤销栈。
/// </summary>
public sealed partial class KlineCanvasViewModel : ObservableObject
{
    private readonly Workspace _ws;
    private readonly Action<string> _setStatus;

    private bool _dragging;
    private int _dragStartIndex;
    private int _dragCurrentIndex;
    private double _dragStartPrice;
    private double _dragCurrentPrice;
    private int _editZone;          // 0=实体(收盘) 1=上影线(最高) 2=下影线(最低)
    private Kline? _editOriginal;
    private bool _changed;

    public KlineCanvasViewModel(Workspace workspace, Action<string> setStatus)
    {
        _ws = workspace;
        _setStatus = setStatus;
    }

    public Workspace Workspace => _ws;
    public ObservableCollection<Kline> Bars => _ws.Bars;
    public ObservableCollection<Annotation> Annotations => _ws.Annotations;
    public MarketRuleSet Rules => _ws.Rules;

    /// <summary>成交量区域占绘图区高度的比例。</summary>
    public const double VolumeBandHeightFraction = 0.20;

    [ObservableProperty] private DrawTool _tool = DrawTool.Select;
    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private bool _snapToTick = true;
    [ObservableProperty] private bool _snapToGrid;
    [ObservableProperty] private double _gridStep = 0.05;
    [ObservableProperty] private double _wickRatio = 0.3;
    [ObservableProperty] private string _patternName = "双顶";
    [ObservableProperty] private int _patternBars = 40;
    [ObservableProperty] private double _trendVolatility = 0.012;
    [ObservableProperty] private bool _showVolume = true;
    [ObservableProperty] private bool _showAnnotations = true;
    [ObservableProperty] private string _hint = "选择工具后在画布上拖拽即可编辑。";

    /// <summary>当前预览（null 表示无）。</summary>
    public CanvasPreview? Preview { get; private set; }

    public Kline? SelectedBar =>
        SelectedIndex >= 0 && SelectedIndex < Bars.Count ? Bars[SelectedIndex] : null;

    public bool HasSelection => SelectedBar != null;

    public double MaxVolumeReference =>
        Bars.Count == 0 ? 1_000_000 : Math.Max(1_000_000, Bars.Max(b => (double)b.Volume) * 1.2);

    /// <summary>请求画布重绘。</summary>
    public event EventHandler? RenderRequested;

    public event EventHandler<CanvasZoomAction>? ZoomRequested;

    [RelayCommand]
    private void Zoom(string? action)
    {
        var act = action?.ToLowerInvariant() switch
        {
            "in" => CanvasZoomAction.In,
            "out" => CanvasZoomAction.Out,
            _ => CanvasZoomAction.Fit,
        };
        ZoomRequested?.Invoke(this, act);
    }

    partial void OnSelectedIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedBar));
        OnPropertyChanged(nameof(HasSelection));
        RenderRequested?.Invoke(this, EventArgs.Empty);
    }

    public void RequestRender() => RenderRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>价格吸附：先网格步长，再最小报价单位。</summary>
    public double SnapPrice(double price)
    {
        if (double.IsNaN(price)) return 0;
        if (SnapToGrid && GridStep > 0)
            price = Math.Round(price / GridStep, MidpointRounding.AwayFromZero) * GridStep;
        if (SnapToTick)
            price = Rules.RoundToTick(price);
        return Math.Max(Rules.TickSize, price);
    }

    // ------------------------------------------------------------------
    // 鼠标输入
    // ------------------------------------------------------------------

    public void PointerDown(int barIndex, double price, double volumeFraction, bool isRightButton = false)
    {
        if (isRightButton)
        {
            CancelDrag();
            SelectedIndex = -1;
            return;
        }

        var index = Math.Clamp(barIndex, 0, Bars.Count);
        price = SnapPrice(price);

        switch (Tool)
        {
            case DrawTool.Select:
                if (index >= Bars.Count)
                {
                    SelectedIndex = -1;
                    return;
                }

                SelectedIndex = index;
                _editOriginal = Bars[index].Clone();
                _editZone = DetectZone(Bars[index], price);
                _dragStartIndex = _dragCurrentIndex = index;
                _dragStartPrice = _dragCurrentPrice = price;
                _ws.BeginEdit();
                _dragging = true;
                _changed = false;
                Hint = _editZone switch
                {
                    1 => "拖拽调整最高价",
                    2 => "拖拽调整最低价",
                    _ => "拖拽调整收盘价",
                };
                break;

            case DrawTool.DrawBar:
                _dragStartIndex = _dragCurrentIndex = Math.Min(index, Bars.Count);
                _dragStartPrice = _dragCurrentPrice = price;
                _dragging = true;
                _changed = false;
                UpdateCandlePreview();
                Hint = "拖拽确定实体范围，松开生成/修改该K线";
                break;

            case DrawTool.TrendLine:
                _dragStartIndex = _dragCurrentIndex = Math.Min(index, Math.Max(0, Bars.Count - 1));
                _dragStartPrice = _dragCurrentPrice = price;
                _dragging = true;
                _changed = false;
                UpdateTrendPreview();
                Hint = "拖拽到结束位置，松开按趋势线生成K线";
                break;

            case DrawTool.Erase:
                EraseAt(index);
                break;

            case DrawTool.DrawVolume:
                if (index >= Bars.Count) return;
                SelectedIndex = index;
                _ws.BeginEdit();
                _dragging = true;
                _changed = true;
                _dragStartIndex = index;
                ApplyVolume(index, volumeFraction);
                Hint = "上下拖拽设置成交量柱高度";
                break;

            case DrawTool.Pattern:
                InsertPattern(index);
                break;
        }

        RenderRequested?.Invoke(this, EventArgs.Empty);
    }

    public void PointerMove(int barIndex, double price, double volumeFraction)
    {
        if (!_dragging) return;

        var index = Math.Clamp(barIndex, 0, Bars.Count);
        price = SnapPrice(price);

        switch (Tool)
        {
            case DrawTool.Select:
            {
                if (_editOriginal == null || _dragStartIndex >= Bars.Count) return;
                var bar = Bars[_dragStartIndex];
                var prevClose = _dragStartIndex > 0 ? Bars[_dragStartIndex - 1].Close : _editOriginal.Open;
                var up = Rules.LimitUp(prevClose);
                var down = Rules.LimitDown(prevClose);
                var p = Math.Clamp(price, down, up);

                // 原子更新：一次写入 + 一次通知，不会出现「收盘已改、最高没跟上」的中间态
                switch (_editZone)
                {
                    case 1:
                        bar.SetOhlc(bar.Open, Math.Max(p, Math.Max(bar.Open, bar.Close)), bar.Low, bar.Close);
                        break;
                    case 2:
                        bar.SetOhlc(bar.Open, bar.High, Math.Min(p, Math.Min(bar.Open, bar.Close)), bar.Close);
                        break;
                    default:
                        bar.SetOhlc(bar.Open, bar.High, bar.Low, p);
                        break;
                }

                _changed = true;
                _dragCurrentPrice = p;
                break;
            }

            case DrawTool.DrawBar:
                _dragCurrentIndex = index;
                _dragCurrentPrice = price;
                UpdateCandlePreview();
                _changed = true;
                break;

            case DrawTool.TrendLine:
                _dragCurrentIndex = index;
                _dragCurrentPrice = price;
                UpdateTrendPreview();
                _changed = true;
                break;

            case DrawTool.DrawVolume:
                ApplyVolume(_dragStartIndex, volumeFraction);
                _changed = true;
                break;
        }

        RenderRequested?.Invoke(this, EventArgs.Empty);
    }

    public void PointerUp(int barIndex, double price, double volumeFraction)
    {
        if (!_dragging)
        {
            if (Tool == DrawTool.Select)
            {
                var idx = Math.Clamp(barIndex, 0, Bars.Count - 1);
                if (idx >= 0 && idx < Bars.Count) SelectedIndex = idx;
            }

            return;
        }

        price = SnapPrice(price);
        _dragging = false;

        switch (Tool)
        {
            case DrawTool.Select:
                if (_changed)
                {
                    _setStatus($"已修改第 {_dragStartIndex} 根K线：O={Bars[_dragStartIndex].Open:F2} H={Bars[_dragStartIndex].High:F2} " +
                               $"L={Bars[_dragStartIndex].Low:F2} C={Bars[_dragStartIndex].Close:F2}");
                    _ws.NotifyBarsChanged();
                }
                else
                {
                    _ws.CancelEdit();
                }

                break;

            case DrawTool.DrawBar:
                CommitCandle(Math.Clamp(barIndex, 0, Bars.Count), price);
                break;

            case DrawTool.TrendLine:
                CommitTrend(Math.Clamp(barIndex, 0, Bars.Count), price);
                break;

            case DrawTool.DrawVolume:
                _ws.NotifyBarsChanged();
                _setStatus($"已设置第 {_dragStartIndex} 根成交量：{Bars[_dragStartIndex].Volume:N0}");
                break;
        }

        Preview = null;
        _editOriginal = null;
        _changed = false;
        Hint = "完成。";
        RenderRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CancelDrag()
    {
        if (_dragging)
        {
            if (!_changed)
            {
                // 没有实际改动：丢弃撤销点
                _ws.CancelEdit();
            }
            else if (Tool == DrawTool.Select && _editOriginal != null && _dragStartIndex < Bars.Count)
            {
                // 已改动但用户取消：还原原始K线，撤销点仍保留给用户选择
                Bars[_dragStartIndex].CopyFrom(_editOriginal);
            }
        }

        _dragging = false;
        _changed = false;
        Preview = null;
        _editOriginal = null;
        RenderRequested?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------
    // 各工具的提交逻辑
    // ------------------------------------------------------------------

    private void UpdateCandlePreview()
    {
        var price = _dragCurrentPrice;
        var open = _dragStartPrice;
        var high = Math.Max(open, price) * (1 + Math.Max(0, WickRatio) * 0.02);
        var low = Math.Min(open, price) * (1 - Math.Max(0, WickRatio) * 0.02);
        Preview = new CanvasPreview
        {
            IsTrend = false,
            StartIndex = _dragStartIndex,
            EndIndex = _dragStartIndex,
            StartPrice = open,
            EndPrice = price,
            High = high,
            Low = low,
        };
    }

    private void UpdateTrendPreview()
    {
        Preview = new CanvasPreview
        {
            IsTrend = true,
            StartIndex = Math.Min(_dragStartIndex, _dragCurrentIndex),
            EndIndex = Math.Max(_dragStartIndex, _dragCurrentIndex),
            StartPrice = _dragStartPrice,
            EndPrice = _dragCurrentPrice,
            High = Math.Max(_dragStartPrice, _dragCurrentPrice),
            Low = Math.Min(_dragStartPrice, _dragCurrentPrice),
        };
    }

    private void CommitCandle(int index, double closePrice)
    {
        var open = _dragStartPrice;
        closePrice = SnapPrice(closePrice);
        open = SnapPrice(open);
        var span = Math.Abs(closePrice - open);
        var wick = Math.Max(span * Math.Max(0, WickRatio), open * 0.002);
        var high = Math.Max(open, closePrice) + wick;
        var low = Math.Max(Rules.TickSize, Math.Min(open, closePrice) - wick);

        if (index < Bars.Count)
        {
            _ws.BeginEdit();
            var bar = Bars[index];
            bar.Open = open;
            bar.High = Rules.RoundToTick(high);
            bar.Low = Rules.RoundToTick(low);
            bar.Close = closePrice;
            bar.FixConsistency();
            _ws.NotifyBarsChanged();
            _setStatus($"已绘制并覆盖第 {index} 根K线。");
        }
        else
        {
            _ws.BeginEdit();
            var time = NextBarTime();
            var bar = new Kline(time, open, Rules.RoundToTick(high), Rules.RoundToTick(low), closePrice,
                Math.Max(1L, _ws.Settings.BaseVolume / 2), 0);
            bar.Amount = Math.Round(bar.Volume * (bar.High + bar.Low + bar.Close) / 3.0, 2);
            bar.FixConsistency();
            _ws.Bars.Add(bar);
            SelectedIndex = _ws.Bars.Count - 1;
            _ws.NotifyBarsChanged();
            _setStatus($"已在末尾新增一根K线（{time:yyyy-MM-dd HH:mm}）。");
        }

        _ws.DataSource = "Manual";
    }

    private void CommitTrend(int endIndex, double endPrice)
    {
        var startIndex = Math.Min(_dragStartIndex, Math.Max(0, Bars.Count - 1));
        var end = Math.Max(startIndex, endIndex);
        var count = end - startIndex + 1;
        if (count <= 0) return;

        var startPrice = _dragStartPrice;
        endPrice = SnapPrice(endPrice);
        if (Math.Abs(endPrice - startPrice) < 1e-6)
        {
            _setStatus("趋势线首尾价格相同，已忽略。");
            return;
        }

        var startTime = startIndex < Bars.Count
            ? Bars[startIndex].Time
            : NextBarTime();

        var generated = TrendLineBuilder.Build(
            startPrice, endPrice, count, TrendVolatility, startTime, _ws.Settings.Period,
            _ws.Rules, _ws.Settings.BaseVolume, _ws.Settings.Seed + startIndex * 31, 0.4);

        _ws.BeginEdit();
        for (var i = 0; i < count; i++)
        {
            var target = startIndex + i;
            if (target < Bars.Count)
            {
                var src = generated[i];
                var bar = Bars[target];
                bar.Open = src.Open;
                bar.High = src.High;
                bar.Low = src.Low;
                bar.Close = src.Close;
                bar.Volume = src.Volume;
                bar.Amount = src.Amount;
                bar.FixConsistency();
            }
            else
            {
                _ws.Bars.Add(generated[i]);
            }
        }

        _ws.DataSource = "Manual";
        SelectedIndex = Math.Min(startIndex + count - 1, Bars.Count - 1);
        _ws.NotifyBarsChanged();
        _setStatus($"趋势线已生成 {count} 根K线（{startPrice:F2} → {endPrice:F2}）。");
    }

    private void ApplyVolume(int index, double fraction)
    {
        if (index < 0 || index >= Bars.Count) return;
        var vol = (long)Math.Max(0, Math.Clamp(fraction, 0, 1.0) * MaxVolumeReference);
        Bars[index].Volume = vol;
        Bars[index].Amount = Math.Round(vol * (Bars[index].High + Bars[index].Low + Bars[index].Close) / 3.0, 2);
    }

    private void EraseAt(int index)
    {
        if (index < 0 || index >= Bars.Count) return;
        _ws.BeginEdit();
        Bars.RemoveAt(index);

        // 标注索引同步平移
        foreach (var a in Annotations)
        {
            if (a.BarIndex > index) a.BarIndex--;
            else if (a.BarIndex == index) a.BarIndex = Math.Max(0, index - 1);
        }

        SelectedIndex = Math.Min(index, Bars.Count - 1);
        _ws.DataSource = "Manual";
        _ws.NotifyBarsChanged();
        _setStatus($"已删除第 {index} 根K线。");
    }

    /// <summary>插入形态模板：从 index 开始覆盖/追加模板K线。</summary>
    public void InsertPattern(int index)
    {
        var def = PatternLibrary.Find(PatternName);
        if (def == null)
        {
            _setStatus("未找到形态模板。");
            return;
        }

        index = Math.Clamp(index, 0, Bars.Count);
        var startTime = index < Bars.Count ? Bars[index].Time : NextBarTime();
        var anchor = index < Bars.Count ? Bars[index].Open : (Bars.Count > 0 ? Bars[^1].Close : _ws.Settings.StartPrice);

        var generated = PatternLibrary.Build(def, anchor, Math.Max(3, PatternBars), startTime,
            _ws.Settings.Period, _ws.Rules, _ws.Settings.BaseVolume, 0.004, _ws.Settings.Seed + 977, 0.35);

        _ws.BeginEdit();
        for (var i = 0; i < generated.Count; i++)
        {
            var target = index + i;
            if (target < Bars.Count)
            {
                var src = generated[i];
                var bar = Bars[target];
                bar.Open = src.Open;
                bar.High = src.High;
                bar.Low = src.Low;
                bar.Close = src.Close;
                bar.Volume = src.Volume;
                bar.Amount = src.Amount;
                bar.FixConsistency();
            }
            else
            {
                _ws.Bars.Add(generated[i]);
            }
        }

        _ws.DataSource = "Manual";
        SelectedIndex = Math.Min(index + generated.Count - 1, Bars.Count - 1);
        _ws.NotifyBarsChanged();
        _setStatus($"已插入形态「{def.DisplayName}」，共 {generated.Count} 根。");
    }

    // ------------------------------------------------------------------

    private int DetectZone(Kline bar, double price)
    {
        var range = Math.Max(1e-6, bar.High - bar.Low);
        var tol = range * 0.18;
        if (price >= bar.High - tol) return 1;
        if (price <= bar.Low + tol) return 2;
        return 0;
    }

    public DateTime NextBarTime()
    {
        if (Bars.Count == 0)
            return TradingCalendar.AlignToPeriod(_ws.Settings.StartDate, _ws.Settings.Period);
        return TradingCalendar.Next(Bars[^1].Time, _ws.Settings.Period);
    }

    /// <summary>直接设置某根K线的 OHLC（供属性面板使用）。</summary>
    public void ApplyOhlc(int index, double open, double high, double low, double close)
    {
        if (index < 0 || index >= Bars.Count) return;
        _ws.BeginEdit();
        Bars[index].SetOhlc(SnapPrice(open), SnapPrice(high), SnapPrice(low), SnapPrice(close));
        _ws.DataSource = "Manual";
        _ws.NotifyBarsChanged();
        _setStatus($"已应用第 {index} 根K线的 OHLC。");
    }
}
