using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScottPlot;
using StockSimulator.Models;
using StockSimulator.ViewModels;
using Color = ScottPlot.Color;

namespace StockSimulator.Controls;

/// <summary>
/// ScottPlot 画布封装。
/// 职责边界：只做「像素 ↔ 坐标换算」与「把数据画出来」；
/// 所有编辑逻辑都在 <see cref="KlineCanvasViewModel"/> 中，便于脱离 WPF 测试。
/// </summary>
public partial class KlineCanvasControl : UserControl
{
    private static readonly Color UpColor = Color.FromHex("#E04B4B");
    private static readonly Color DownColor = Color.FromHex("#2AA37A");
    private static readonly Color Ma5Color = Color.FromHex("#E8A33D");
    private static readonly Color Ma20Color = Color.FromHex("#5B8FF9");
    private static readonly Color AccentColor = Color.FromHex("#F0B429");
    private static readonly Color SelectionColor = Color.FromHex("#1E88E5");

    private KlineCanvasViewModel? _vm;

    private bool _needFit = true;
    private bool _dragging;
    private int _lastBarCount = -1;
    private double _xMin, _xMax = 1, _yMin, _yMax = 1;

    public KlineCanvasControl()
    {
        InitializeComponent();

        // 关闭 ScottPlot 自带交互（平移/缩放/右键菜单），避免与手绘操作冲突
        PlotControl.UserInputProcessor.IsEnabled = false;
        PlotControl.Plot.Axes.DateTimeTicksBottom();

        PlotControl.MouseLeftButtonDown += OnMouseLeftDown;
        PlotControl.MouseMove += OnMouseMove;
        PlotControl.MouseLeftButtonUp += OnMouseUp;
        PlotControl.MouseRightButtonDown += OnMouseRightDown;
        PlotControl.MouseWheel += OnMouseWheel;
        DataContextChanged += OnDataContextChanged;
        SizeChanged += (_, _) => PlotControl.Refresh();
    }

    /// <summary>K线实体宽度：按数据实际时间间距的 70% 计算，日线/分钟线都适用。</summary>
    private TimeSpan CandleSpan()
    {
        if (_vm == null || _vm.Bars.Count < 2) return TimeSpan.FromDays(0.7);
        var spacingDays = Math.Min(1.0, EstimateSpacing());
        return TimeSpan.FromDays(Math.Max(1e-4, spacingDays * 0.7));
    }

    // ------------------------------------------------------------------
    // 绑定
    // ------------------------------------------------------------------

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.RenderRequested -= VmOnRenderRequested;
            _vm.ZoomRequested -= VmOnZoomRequested;
            _vm.PropertyChanged -= VmOnPropertyChanged;
        }

        _vm = e.NewValue as KlineCanvasViewModel;

        if (_vm != null)
        {
            _vm.RenderRequested += VmOnRenderRequested;
            _vm.ZoomRequested += VmOnZoomRequested;
            _vm.PropertyChanged += VmOnPropertyChanged;
        }

        _needFit = true;
        Render();
    }

    private void VmOnRenderRequested(object? sender, EventArgs e) => Render();

    private void VmOnZoomRequested(object? sender, CanvasZoomAction action) => Zoom(action);

    private void VmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KlineCanvasViewModel.Tool))
            UpdateCursor();
        else if (e.PropertyName is nameof(KlineCanvasViewModel.ShowVolume)
                 or nameof(KlineCanvasViewModel.ShowAnnotations))
            Render();
    }

    private void UpdateCursor()
    {
        PlotControl.Cursor = _vm?.Tool switch
        {
            DrawTool.Select => Cursors.Arrow,
            DrawTool.Erase => Cursors.No,
            _ => Cursors.Cross,
        };
    }

    // ------------------------------------------------------------------
    // 鼠标交互
    // ------------------------------------------------------------------

    private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null || _vm.Bars.Count == 0) return;
        var (index, price, volFrac) = Resolve(e);
        _dragging = true;
        PlotControl.CaptureMouse();
        _vm.PointerDown(index, price, volFrac);
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_vm == null || !_dragging) return;
        var (index, price, volFrac) = Resolve(e);
        _vm.PointerMove(index, price, volFrac);
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        PlotControl.ReleaseMouseCapture();
        if (!_dragging) return;
        _dragging = false;
        var (index, price, volFrac) = Resolve(e);
        _vm.PointerUp(index, price, volFrac);
        e.Handled = true;
    }

    private void OnMouseRightDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        var (index, price, volFrac) = Resolve(e);
        _vm.PointerDown(index, price, volFrac, isRightButton: true);
        e.Handled = true;   // 屏蔽 ScottPlot 右键菜单
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Zoom(e.Delta > 0 ? CanvasZoomAction.In : CanvasZoomAction.Out);
        e.Handled = true;
    }

    /// <summary>把鼠标位置翻译成 (K线下标, 价格, 成交量比例)。</summary>
    private (int Index, double Price, double VolumeFraction) Resolve(MouseEventArgs e)
    {
        if (_vm == null) return (0, 0, 0);

        var pixel = PlotControl.GetPlotPixelPosition(e);
        var coord = PlotControl.Plot.GetCoordinates(pixel);

        var bars = _vm.Bars;
        var index = FindNearestIndex(coord.X);
        var volFrac = (_yMax - _yMin) <= 1e-9
            ? 0
            : Math.Clamp((coord.Y - _yMin) / ((_yMax - _yMin) * KlineCanvasViewModel.VolumeBandHeightFraction), 0, 1);

        return (index, coord.Y, volFrac);
    }

    private int FindNearestIndex(double x)
    {
        var bars = _vm!.Bars;
        if (bars.Count == 0) return 0;

        var spacing = EstimateSpacing();
        if (x >= bars[^1].X + spacing * 0.5) return bars.Count;   // 末尾追加
        if (x <= bars[0].X) return 0;

        var lo = 0;
        var hi = bars.Count - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (bars[mid].X <= x) lo = mid;
            else hi = mid;
        }

        return x - bars[lo].X <= bars[hi].X - x ? lo : hi;
    }

    private double EstimateSpacing()
    {
        var bars = _vm!.Bars;
        if (bars.Count < 2) return 1.0;
        return Math.Max(1e-6, (bars[^1].X - bars[0].X) / (bars.Count - 1));
    }

    private double XOfIndex(int index)
    {
        var bars = _vm!.Bars;
        if (bars.Count == 0) return 0;
        if (index < bars.Count) return bars[index].X;
        return bars[^1].X + EstimateSpacing() * (index - bars.Count + 1);
    }

    // ------------------------------------------------------------------
    // 渲染
    // ------------------------------------------------------------------

    /// <summary>全量重绘。</summary>
    public void Render()
    {
        var plot = PlotControl.Plot;
        plot.Clear();

        if (_vm == null)
        {
            EmptyHint.Visibility = Visibility.Visible;
            PlotControl.Refresh();
            return;
        }

        var bars = _vm.Bars;
        EmptyHint.Visibility = bars.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (bars.Count == 0)
        {
            PlotControl.Refresh();
            return;
        }

        if (bars.Count != _lastBarCount)
        {
            _lastBarCount = bars.Count;
            _needFit = true;
        }

        // --- K线 ---
        // 防御式清洗：即使模型在拖拽/绑定编辑中出现瞬时非法值，也不让绘图库抛异常
        var span = CandleSpan();
        var ohlcs = new List<OHLC>(bars.Count);
        foreach (var b in bars)
        {
            var o = b.Open > 0 ? b.Open : 0.01;
            var c = b.Close > 0 ? b.Close : o;
            var h = Math.Max(b.High, Math.Max(o, c));
            var l = Math.Min(b.Low, Math.Min(o, c));
            if (l <= 0) l = Math.Max(0.01, Math.Min(o, c) * 0.99);
            ohlcs.Add(new OHLC(o, h, l, c, b.Time, span));
        }

        var candles = plot.Add.Candlestick(ohlcs);
        candles.RisingColor = UpColor;
        candles.FallingColor = DownColor;

        // --- 均线 ---
        AddMovingAverage(plot, bars, 5, Ma5Color);
        AddMovingAverage(plot, bars, 20, Ma20Color);

        if (_needFit || _lastBarCount <= 0)
        {
            ComputeFitLimits(bars);
            _needFit = false;
        }

        // --- 成交量（画在绘图区下部预留区域） ---
        if (_vm.ShowVolume) DrawVolume(plot, bars);

        // --- 选中高亮 ---
        DrawSelection(plot, bars);

        // --- 拖拽预览 ---
        DrawPreview(plot);

        // --- 标注 ---
        if (_vm.ShowAnnotations) DrawAnnotations(plot, bars);

        ApplyDateAxisFormat(bars);

        // 标题只用 ASCII，避免 SkiaSharp 缺少中文字体时出现方块
        plot.Title(_vm.Rules.EnablePriceLimit
            ? $"{_vm.Rules.Symbol}    Bars: {bars.Count}    Limit: +/-{_vm.Rules.EffectiveLimitPct:P0}"
            : $"{_vm.Rules.Symbol}    Bars: {bars.Count}    Limit: none");
        ApplyLimits();
        PlotControl.Refresh();
    }

    /// <summary>
    /// 横轴日期刻度：按周期自适应格式（分钟线显示时刻、日线显示月-日、跨年显示年-月），
    /// 并限制刻度数量，避免标签相互重叠。
    /// </summary>
    private void ApplyDateAxisFormat(IReadOnlyList<Kline> bars)
    {
        var plot = PlotControl.Plot;
        var isIntraday = _vm!.Workspace.Settings.Period.IsIntraday();
        var days = Math.Max(1.0, (bars[^1].Time - bars[0].Time).TotalDays);

        var generator = new ScottPlot.TickGenerators.DateTimeAutomatic { MaxTickCount = 8 };
        generator.LabelFormatter = t =>
        {
            if (isIntraday)
                return days > 5 ? t.ToString("MM-dd HH:mm") : t.ToString("HH:mm");
            return days > 400 ? t.ToString("yyyy") : days > 120 ? t.ToString("yy-MM") : t.ToString("MM-dd");
        };

        plot.Axes.Bottom.TickGenerator = generator;
        plot.Axes.Bottom.TickLabelStyle.FontSize = 11;
    }

    private void AddMovingAverage(Plot plot, IReadOnlyList<Kline> bars, int window, Color color)
    {
        if (bars.Count < window) return;

        var n = bars.Count - window + 1;
        var xs = new double[n];
        var ys = new double[n];
        var sum = 0.0;

        for (var i = 0; i < bars.Count; i++)
        {
            sum += bars[i].Close;
            if (i >= window) sum -= bars[i - window].Close;
            if (i >= window - 1)
            {
                xs[i - window + 1] = bars[i].X;
                ys[i - window + 1] = sum / window;
            }
        }

        var line = plot.Add.ScatterLine(xs, ys);
        line.LineColor = color;
        line.LineWidth = 1.2f;
        line.MarkerSize = 0;
    }

    private void ComputeFitLimits(IReadOnlyList<Kline> bars)
    {
        var low = Math.Max(0.01, bars.Min(b => b.Low > 0 ? b.Low : b.Close));
        var high = bars.Max(b => b.High > 0 ? b.High : b.Close);
        if (high <= low) high = low + Math.Max(0.01, low * 0.02);

        var range = high - low;
        var padding = range * 0.06;

        _xMin = bars[0].X - EstimateSpacing();
        _xMax = bars[^1].X + EstimateSpacing();
        _yMin = low - padding - range * 0.28;    // 底部留出成交量带
        _yMax = high + padding;
    }

    private void ApplyLimits() => PlotControl.Plot.Axes.SetLimits(_xMin, _xMax, _yMin, _yMax);

    private void DrawVolume(Plot plot, IReadOnlyList<Kline> bars)
    {
        if (bars.Count > 4000) return;   // 太多时不画量柱，保证流畅

        var maxVol = Math.Max(1.0, bars.Max(b => (double)b.Volume));
        var band = (_yMax - _yMin) * KlineCanvasViewModel.VolumeBandHeightFraction;
        var baseY = _yMin;
        var width = Math.Max(1.5f, bars.Count > 800 ? 2f : 3.5f);

        foreach (var b in bars)
        {
            if (b.Volume <= 0) continue;
            var h = b.Volume / maxVol * band * 0.85;
            var line = plot.Add.Line(b.X, baseY, b.X, baseY + h);
            line.LineColor = (b.IsBullish ? UpColor : DownColor).WithAlpha(0.5);
            line.LineWidth = width;
        }
    }

    private void DrawSelection(Plot plot, IReadOnlyList<Kline> bars)
    {
        var index = _vm!.SelectedIndex;
        if (index < 0 || index >= bars.Count) return;

        var bar = bars[index];
        var half = EstimateSpacing() * 0.55;
        var rect = plot.Add.Rectangle(bar.X - half, bar.X + half, bar.Low * 0.998, bar.High * 1.002);
        rect.FillColor = SelectionColor.WithAlpha(0.08);
        rect.LineColor = SelectionColor;
        rect.LineWidth = 1.2f;
    }

    private void DrawPreview(Plot plot)
    {
        var preview = _vm!.Preview;
        if (preview == null) return;

        var spacing = EstimateSpacing();

        if (preview.IsTrend)
        {
            var line = plot.Add.Line(XOfIndex(preview.StartIndex), preview.StartPrice,
                XOfIndex(preview.EndIndex), preview.EndPrice);
            line.LineColor = AccentColor;
            line.LineWidth = 2.5f;
            return;
        }

        var x = XOfIndex(preview.StartIndex);
        var half = spacing * 0.5;
        var top = Math.Max(preview.StartPrice, preview.EndPrice);
        var bottom = Math.Min(preview.StartPrice, preview.EndPrice);
        if (top - bottom < 0.01) top = bottom + 0.01;

        var body = plot.Add.Rectangle(x - half, x + half, bottom, top);
        body.FillColor = AccentColor.WithAlpha(0.35);
        body.LineColor = AccentColor;
        body.LineWidth = 1.5f;

        var wick = plot.Add.Line(x, preview.Low, x, preview.High);
        wick.LineColor = AccentColor;
        wick.LineWidth = 1.5f;
    }

    private void DrawAnnotations(Plot plot, IReadOnlyList<Kline> bars)
    {
        foreach (var a in _vm!.Annotations)
        {
            if (a.BarIndex < 0 || a.BarIndex >= bars.Count) continue;
            var bar = bars[a.BarIndex];
            var y = bar.High * (1 + Math.Max(0.002, a.OffsetPct));
            var text = plot.Add.Text(a.Text, bar.X, y);
            text.LabelFontSize = 12;
            text.LabelFontName = "Microsoft YaHei";     // 保证中文标注可正常显示
            text.LabelFontColor = Color.FromHex("#222222");
            text.LabelBackgroundColor = Color.FromHex(a.ColorHex).WithAlpha(0.85);
            text.Alignment = Alignment.LowerCenter;
            text.OffsetY = -4;
        }
    }

    // ------------------------------------------------------------------
    // 缩放
    // ------------------------------------------------------------------

    private void Zoom(CanvasZoomAction action)
    {
        if (_vm == null || _vm.Bars.Count == 0) return;

        if (action == CanvasZoomAction.Fit)
        {
            _needFit = true;
            Render();
            return;
        }

        if (_needFit)
        {
            ComputeFitLimits(_vm.Bars);
            _needFit = false;
        }

        var factor = action == CanvasZoomAction.In ? 0.8 : 1.25;
        var cx = (_xMin + _xMax) / 2;
        var cy = (_yMin + _yMax) / 2;
        var halfX = Math.Max(EstimateSpacing(), (_xMax - _xMin) / 2 * factor);
        var halfY = Math.Max(0.01, (_yMax - _yMin) / 2 * factor);

        _xMin = cx - halfX;
        _xMax = cx + halfX;
        _yMin = cy - halfY;
        _yMax = cy + halfY;

        ApplyLimits();
        PlotControl.Refresh();
    }
}
