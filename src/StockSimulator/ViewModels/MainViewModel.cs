using CommunityToolkit.Mvvm.ComponentModel;
using StockSimulator.Services;
using Wpf.Ui.Controls;

namespace StockSimulator.ViewModels;

/// <summary>左侧导航项（对应一个功能页）。</summary>
public sealed class NavigationItem
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required SymbolRegular Symbol { get; init; }
}

/// <summary>主窗口 ViewModel：聚合四个 Tab 的子 ViewModel，并共享同一个 <see cref="Workspace"/>。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public Workspace Workspace { get; } = new();

    /// <summary>导航栏数据源，SelectedIndex 与 <see cref="SelectedTabIndex"/> 双向绑定。</summary>
    public IReadOnlyList<NavigationItem> NavigationItems { get; } = new List<NavigationItem>
    {
        new() { Title = "分段参数", Subtitle = "自动生成 K 线", Symbol = SymbolRegular.DataHistogram24 },
        new() { Title = "手绘编辑", Subtitle = "画布改 K 线", Symbol = SymbolRegular.Pen24 },
        new() { Title = "批量变体", Subtitle = "压力测试数据", Symbol = SymbolRegular.Layer24 },
        new() { Title = "导出设置", Subtitle = "CSV / 工程 / 模板", Symbol = SymbolRegular.DocumentSave24 },
    };

    public SegmentTabViewModel SegmentTab { get; }
    public ManualDrawTabViewModel DrawTab { get; }
    public BatchTabViewModel BatchTab { get; }
    public ExportTabViewModel ExportTab { get; }

    [ObservableProperty] private string _status = "绪绪：请在 Tab1 配置分段参数后点击「生成K线」。";

    [ObservableProperty] private int _selectedTabIndex;

    [ObservableProperty] private string _barCountText = "0 根";

    public string Title => "股票仿真K线生成器 · Stock Simulator";

    /// <summary>常驻免责声明（界面底部固定显示）。</summary>
    public string Disclaimer =>
        "本软件为仿真行情生成工具，所有K线为人工模拟数据，非真实行情，仅用于策略回测学习，不构成投资建议。";

    public MainViewModel()
    {
        SegmentTab = new SegmentTabViewModel(Workspace, SetStatus);
        DrawTab = new ManualDrawTabViewModel(Workspace, SetStatus);
        BatchTab = new BatchTabViewModel(Workspace, SetStatus);
        ExportTab = new ExportTabViewModel(Workspace, SetStatus, name => SelectedTabIndex = name);

        BatchTab.ApplyToWorkspaceRequested += (_, _) => DrawTab.RefreshAfterExternalChange();

        // 工程/模板/快照载入后，各 Tab 同步刷新
        ExportTab.ProjectLoaded += (_, _) =>
        {
            SegmentTab.RefreshFromWorkspace();
            DrawTab.RefreshAfterExternalChange();
            BatchTab.ClearVariantsCommand.Execute(null);
        };

        Workspace.BarsChanged += (_, _) => BarCountText = $"{Workspace.Bars.Count} 根";

        // 演示用：注册内置网格联动接收器，验证“一键发送”通道
        KlineHandoff.Receiver ??= new DemoGridBacktestReceiver();
    }

    public void SetStatus(string message) => Status = $"[{DateTime.Now:HH:mm:ss}] {message}";
}
