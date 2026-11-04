using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockSimulator.Core;
using StockSimulator.Models;
using StockSimulator.Services;

namespace StockSimulator.ViewModels;

/// <summary>工具栏按钮项。</summary>
public sealed partial class ToolItem : ObservableObject
{
    public required DrawTool Tool { get; init; }
    public required string Name { get; init; }
    public required string Glyph { get; init; }
    public required string Description { get; init; }

    public override string ToString() => Name;
}

/// <summary>
/// Tab2：手绘K线编辑器。
/// 画布交互委托给 <see cref="KlineCanvasViewModel"/>，这里负责工具栏、参数面板、校验与数据操作。
/// </summary>
public sealed partial class ManualDrawTabViewModel : ObservableObject
{
    private readonly Workspace _ws;
    private readonly Action<string> _setStatus;
    private Kline? _hookedBar;

    public ManualDrawTabViewModel(Workspace workspace, Action<string> setStatus)
    {
        _ws = workspace;
        _setStatus = setStatus;
        Canvas = new KlineCanvasViewModel(workspace, setStatus);

        Tools = new ObservableCollection<ToolItem>
        {
            new() { Tool = DrawTool.Select, Name = "选择/拖拽", Glyph = "↖", Description = "点选K线，拖拽实体改收盘、拖拽上下影线改最高/最低" },
            new() { Tool = DrawTool.DrawBar, Name = "单K绘制", Glyph = "▮", Description = "在画布上按住拖拽，松开生成/覆盖一根K线" },
            new() { Tool = DrawTool.TrendLine, Name = "趋势线生成", Glyph = "╱", Description = "拉一条直线，自动生成一串K线" },
            new() { Tool = DrawTool.Erase, Name = "橡皮擦", Glyph = "⌫", Description = "点击K线将其删除" },
            new() { Tool = DrawTool.DrawVolume, Name = "成交量笔", Glyph = "▁▃", Description = "在K线上上下拖拽设置成交量柱高度" },
            new() { Tool = DrawTool.Pattern, Name = "形态模板", Glyph = "◇", Description = "从当前位置插入双顶/双底/头肩顶/三角/一字横盘" },
        };

        SelectedTool = Tools[0];
        SelectedPattern = Patterns.FirstOrDefault(p => p.Name == "DoubleTop") ?? Patterns.FirstOrDefault();

        _ws.HistoryChanged += (_, _) =>
        {
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        };
        _ws.BarsChanged += (_, _) => Canvas.RequestRender();
        Canvas.PropertyChanged += CanvasOnPropertyChanged;
    }

    public Workspace Workspace => _ws;
    public KlineCanvasViewModel Canvas { get; }

    public ObservableCollection<ToolItem> Tools { get; }

    public IReadOnlyList<PatternDefinition> Patterns => PatternLibrary.All;

    public ObservableCollection<ValidationIssue> Issues { get; } = new();

    public ObservableCollection<Annotation> Annotations => _ws.Annotations;

    [ObservableProperty] private ToolItem? _selectedTool;
    [ObservableProperty] private PatternDefinition? _selectedPattern;

    [ObservableProperty] private string _validationSummary = "尚未校验。";
    [ObservableProperty] private int _smoothWindow = 5;
    [ObservableProperty] private double _smoothStrength = 0.7;
    [ObservableProperty] private double _noisePrice = 0.008;
    [ObservableProperty] private double _noiseVolume = 0.15;
    [ObservableProperty] private string _annotationText = "关注此位";
    [ObservableProperty] private string _annotationColor = "#FFB300";

    public string BarInfoText
    {
        get
        {
            var bar = Canvas.SelectedBar;
            if (bar == null) return "未选中K线";
            return $"{bar.Time:yyyy-MM-dd HH:mm}　O {bar.Open:F2}　H {bar.High:F2}　L {bar.Low:F2}　C {bar.Close:F2}　" +
                   $"V {bar.Volume:N0}　涨跌 {bar.ChangePct:P2}";
        }
    }

    partial void OnSelectedToolChanged(ToolItem? value)
    {
        if (value != null) Canvas.Tool = value.Tool;
    }

    partial void OnSelectedPatternChanged(PatternDefinition? value)
    {
        if (value != null) Canvas.PatternName = value.Name;
    }

    private void CanvasOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KlineCanvasViewModel.SelectedIndex))
        {
            OnPropertyChanged(nameof(BarInfoText));
            HookSelectedBar();
        }
        else if (e.PropertyName == nameof(KlineCanvasViewModel.SelectedBar))
        {
            OnPropertyChanged(nameof(BarInfoText));
        }
    }

    private void HookSelectedBar()
    {
        if (_hookedBar != null) _hookedBar.PropertyChanged -= OnSelectedBarPropertyChanged;
        _hookedBar = Canvas.SelectedBar;
        if (_hookedBar != null) _hookedBar.PropertyChanged += OnSelectedBarPropertyChanged;
    }

    private void OnSelectedBarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Kline.Open) or nameof(Kline.High) or nameof(Kline.Low) or nameof(Kline.Close)
            or nameof(Kline.Volume))
        {
            OnPropertyChanged(nameof(BarInfoText));
            Canvas.RequestRender();
        }
    }

    /// <summary>外部（如批量变体应用/导入）改动数据后调用。</summary>
    public void RefreshAfterExternalChange()
    {
        Canvas.SelectedIndex = -1;
        Canvas.RequestRender();
        OnPropertyChanged(nameof(BarInfoText));
    }

    // ---------------- 撤销 / 重做 ----------------

    private bool CanUndo() => _ws.CanUndo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        _ws.Undo();
        _setStatus("已撤销。");
    }

    private bool CanRedo() => _ws.CanRedo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        _ws.Redo();
        _setStatus("已重做。");
    }

    // ---------------- 数据加工 ----------------

    [RelayCommand]
    private void Smooth()
    {
        if (_ws.Bars.Count == 0) return;
        _ws.ReplaceBars(NoiseSmoother.Smooth(_ws.Bars, SmoothWindow, SmoothStrength), source: "Manual");
        _setStatus($"已平滑行情（窗口 {SmoothWindow}，强度 {SmoothStrength:P0}）。");
    }

    [RelayCommand]
    private void AddNoise()
    {
        if (_ws.Bars.Count == 0) return;
        var seed = Environment.TickCount;
        _ws.ReplaceBars(NoiseSmoother.AddNoise(_ws.Bars, NoisePrice, NoiseVolume, seed, _ws.Rules), source: "Manual");
        _setStatus($"已叠加噪声（价格 σ={NoisePrice:P2}，量 σ={NoiseVolume:P0}）。");
    }

    [RelayCommand]
    private void Validate()
    {
        var report = ConstraintValidator.Validate(_ws.Bars, _ws.Rules);
        Issues.Clear();
        foreach (var i in report.Issues.Take(500)) Issues.Add(i);
        ValidationSummary = report.Summary;
        _setStatus("校验完成：" + report.Summary);
    }

    [RelayCommand]
    private void Repair()
    {
        if (_ws.Bars.Count == 0) return;
        _ws.BeginEdit();
        var n = ConstraintValidator.Repair(_ws.Bars, _ws.Rules);
        _ws.NotifyBarsChanged();
        _setStatus($"已修复 {n} 根K线。");
        Validate();
    }

    [RelayCommand]
    private void ClearAll()
    {
        if (_ws.Bars.Count == 0) return;
        if (!DialogService.Confirm("确定清空当前所有K线？此操作可撤销。")) return;
        _ws.ReplaceBars(Array.Empty<Kline>(), source: "Manual");
        Canvas.SelectedIndex = -1;
        Issues.Clear();
        _setStatus("已清空K线。");
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (Canvas.SelectedIndex < 0 || Canvas.SelectedIndex >= _ws.Bars.Count)
        {
            _setStatus("请先在画布上选中一根K线。");
            return;
        }

        _ws.BeginEdit();
        _ws.Bars.RemoveAt(Canvas.SelectedIndex);
        _ws.DataSource = "Manual";
        _ws.NotifyBarsChanged();
        Canvas.SelectedIndex = Math.Min(Canvas.SelectedIndex, _ws.Bars.Count - 1);
        _setStatus("已删除选中K线。");
    }

    [RelayCommand]
    private void InsertPattern()
    {
        if (SelectedPattern == null) return;
        Canvas.PatternName = SelectedPattern.Name;
        var index = Canvas.SelectedIndex >= 0 ? Canvas.SelectedIndex : _ws.Bars.Count;
        Canvas.InsertPattern(index);
    }

    [RelayCommand]
    private void ImportCsv()
    {
        var path = DialogService.OpenFile(
            "K线 CSV (*.csv)|*.csv|所有文件 (*.*)|*.*", "导入真实行情 CSV");
        if (path == null) return;

        try
        {
            var result = CsvKlineService.Import(path);
            _ws.ReplaceBars(result.Bars, source: "Imported");
            _ws.Settings.StartPrice = result.Bars[0].Open;
            _ws.Settings.StartDate = result.Bars[0].Time;
            Canvas.SelectedIndex = -1;
            _setStatus($"已导入 {result.Bars.Count} 根K线：{Path.GetFileName(path)}");
            Validate();
            if (result.Warnings.Count > 1)
                DialogService.Info(string.Join(Environment.NewLine, result.Warnings.Take(15)), "导入提示");
        }
        catch (Exception ex)
        {
            DialogService.Error("导入失败：" + ex.Message);
        }
    }

    // ---------------- 标注 ----------------

    [RelayCommand]
    private void AddAnnotation()
    {
        if (_ws.Bars.Count == 0) return;
        var index = Canvas.SelectedIndex >= 0 ? Canvas.SelectedIndex : _ws.Bars.Count - 1;
        var annotation = new Annotation
        {
            BarIndex = index,
            Text = string.IsNullOrWhiteSpace(AnnotationText) ? "标注" : AnnotationText.Trim(),
            ColorHex = AnnotationColor,
        };
        _ws.Annotations.Add(annotation);
        Canvas.RequestRender();
        _setStatus($"已在第 {index} 根K线上添加标注。");
    }

    [RelayCommand]
    private void RemoveAnnotation(Annotation? annotation)
    {
        if (annotation == null) return;
        _ws.Annotations.Remove(annotation);
        Canvas.RequestRender();
    }

    [RelayCommand]
    private void JumpToAnnotation(Annotation? annotation)
    {
        if (annotation == null) return;
        Canvas.SelectedIndex = annotation.BarIndex;
        _setStatus($"已定位到第 {annotation.BarIndex} 根K线。");
    }
}
