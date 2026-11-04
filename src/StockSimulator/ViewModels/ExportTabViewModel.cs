using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockSimulator.Core;
using StockSimulator.Models;
using StockSimulator.Services;

namespace StockSimulator.ViewModels;

/// <summary>快照对比表格的一行。</summary>
public sealed class CompareRow
{
    public required string Name { get; init; }
    public required string SnapshotValue { get; init; }
    public required string CurrentValue { get; init; }
    public required string Delta { get; init; }
}

/// <summary>
/// Tab4：导出、设置、行情模板管理（以及快照对比、多周期转换、联动发送）。
/// </summary>
public sealed partial class ExportTabViewModel : ObservableObject
{
    private readonly Workspace _ws;
    private readonly Action<string> _setStatus;
    private readonly Action<int> _navigateToTab;
    private readonly TemplateService _templates = new();

    /// <summary>工程载入完成（主窗口据此刷新其它 Tab）。</summary>
    public event EventHandler? ProjectLoaded;

    public ExportTabViewModel(Workspace workspace, Action<string> setStatus, Action<int> navigateToTab)
    {
        _ws = workspace;
        _setStatus = setStatus;
        _navigateToTab = navigateToTab;
        RefreshTemplateNames();

        // 数据一变就同步「待导出」提示
        _ws.BarsChanged += (_, _) => ExportSummary = $"{_ws.Bars.Count} 根K线待导出";
    }

    public Workspace Workspace => _ws;
    public MarketRuleSet Rules => _ws.Rules;
    public KlineGenerationSettings Settings => _ws.Settings;
    public ObservableCollection<SlippageSegment> Slippages => _ws.Slippages;

    public IReadOnlyList<PriceLimitMode> LimitModes { get; } = Enum.GetValues<PriceLimitMode>();
    public IReadOnlyList<BarPeriod> Periods { get; } = Enum.GetValues<BarPeriod>();

    // ---------------- CSV 导出选项 ----------------

    [ObservableProperty] private bool _includeAmount = true;
    [ObservableProperty] private bool _includeChangePct = true;
    [ObservableProperty] private bool _includeFlag = true;
    [ObservableProperty] private bool _includeAdjFactor = true;
    [ObservableProperty] private bool _includeSlippage = true;
    [ObservableProperty] private int _priceDecimals = 2;
    [ObservableProperty] private bool _dateOnlyForDaily = true;

    [ObservableProperty] private string _lastExportPath = "（尚未导出）";
    [ObservableProperty] private string _exportSummary = "0 根K线待导出";

    // ---------------- 多周期 ----------------

    [ObservableProperty] private BarPeriod _targetPeriod = BarPeriod.Minute30;
    [ObservableProperty] private string _periodHint = "把当前K线转换到目标周期（日线 → 分钟线为展开，分钟线 → 日线为聚合）。";

    // ---------------- 快照 ----------------

    public ObservableCollection<SnapshotRecord> Snapshots => _ws.Snapshots;
    public ObservableCollection<CompareRow> CompareRows { get; } = new();
    [ObservableProperty] private SnapshotRecord? _selectedSnapshot;
    [ObservableProperty] private string _snapshotName = "快照1";
    [ObservableProperty] private string _compareSummary = "选择一份快照后点击「与当前行情对比」。";

    // ---------------- 模板 ----------------

    public ObservableCollection<string> TemplateNames { get; } = new();
    [ObservableProperty] private string? _selectedTemplateName;
    [ObservableProperty] private string _templateName = "我的行情模板";

    // ---------------- 联动 ----------------

    [ObservableProperty] private string _handoffStatus = "尚未发送。";

    public string Disclaimer =>
        "本软件为仿真行情生成工具，所有K线为人工模拟数据，非真实行情，仅用于策略回测学习，不构成投资建议。";

    // ================= 导出 =================

    private CsvExportOptions BuildCsvOptions() => new()
    {
        IncludeAmount = IncludeAmount,
        IncludeChangePct = IncludeChangePct,
        IncludeFlag = IncludeFlag,
        IncludeAdjFactor = IncludeAdjFactor,
        IncludeSlippage = IncludeSlippage,
        PriceDecimals = Math.Clamp(PriceDecimals, 0, 8),
        DateOnlyForDaily = DateOnlyForDaily,
    };

    [RelayCommand]
    private void ExportCsv()
    {
        if (_ws.Bars.Count == 0)
        {
            _setStatus("没有K线可导出。");
            return;
        }

        var report = ConstraintValidator.Validate(_ws.Bars, _ws.Rules, 100);
        if (!report.IsValid &&
            !DialogService.Confirm($"校验发现 {report.ErrorCount} 个错误，仍要导出吗？\n\n{report.Summary}"))
            return;

        var defaultName = $"{_ws.Rules.Symbol}_{_ws.Bars[0].Time:yyyyMMdd}_{_ws.Bars[^1].Time:yyyyMMdd}_{_ws.Settings.Period.DisplayName()}.csv";
        var path = DialogService.SaveFile("K线 CSV (*.csv)|*.csv|所有文件 (*.*)|*.*", defaultName, "导出标准K线CSV");
        if (path == null) return;

        try
        {
            CsvKlineService.Export(path, _ws.Bars, _ws.Slippages, BuildCsvOptions(),
                _ws.Settings.Period.ToMinutes() >= 240);
            LastExportPath = path;
            ExportSummary = $"已导出 {_ws.Bars.Count} 根K线 · {_ws.Settings.Period.DisplayName()}";
            _setStatus("导出成功：" + path);
        }
        catch (Exception ex)
        {
            DialogService.Error("导出失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private void OpenExportFolder()
    {
        try
        {
            var dir = File.Exists(LastExportPath) ? Path.GetDirectoryName(LastExportPath) : null;
            if (dir != null)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch
        {
            // 忽略资源管理器启动失败
        }
    }

    // ================= 工程文件 =================

    [RelayCommand]
    private void SaveProject()
    {
        var defaultName = $"{_ws.Rules.Symbol}_{DateTime.Now:yyyyMMdd_HHmm}{ProjectService.FileExtension}";
        var path = DialogService.SaveFile(ProjectService.FileFilter, defaultName, "保存工程文件");
        if (path == null) return;

        try
        {
            ProjectService.Save(path, _ws.ToDocument());
            _setStatus("工程已保存：" + path);
        }
        catch (Exception ex)
        {
            DialogService.Error("保存失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private void LoadProject()
    {
        var path = DialogService.OpenFile(ProjectService.FileFilter, "打开工程文件");
        if (path == null) return;

        try
        {
            var doc = ProjectService.Load(path);
            _ws.LoadFrom(doc);
            ExportSummary = $"{_ws.Bars.Count} 根K线待导出";
            _setStatus($"工程已载入：{Path.GetFileName(path)}（{doc.Bars.Count} 根K线，{doc.Segments.Count} 个阶段）");
            ProjectLoaded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            DialogService.Error("载入失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private void NewProject()
    {
        if (!DialogService.Confirm("新建工程将清空当前K线与参数，是否继续？")) return;
        _ws.ReplaceBars(Array.Empty<Kline>(), source: "Generated");
        _ws.Annotations.Clear();
        _ws.Snapshots.Clear();
        _ws.Segments.Clear();
        foreach (var s in SegmentPresets.DefaultSegments()) _ws.Segments.Add(s);
        _ws.Slippages.Clear();
        _ws.Slippages.Add(new SlippageSegment());
        ProjectLoaded?.Invoke(this, EventArgs.Empty);
        _setStatus("已新建工程。");
    }

    // ================= 多周期转换 =================

    [RelayCommand]
    private void ConvertPeriod()
    {
        if (_ws.Bars.Count == 0)
        {
            _setStatus("没有K线可转换。");
            return;
        }

        var current = _ws.Settings.Period;
        var target = TargetPeriod;
        if (target == current)
        {
            _setStatus("目标周期与当前周期相同。");
            return;
        }

        try
        {
            if (target.ToMinutes() > current.ToMinutes())
            {
                // 聚合：分钟 → 日/周/月
                var aggregated = MultiPeriodConverter.Aggregate(_ws.Bars, target);
                _ws.ReplaceBars(aggregated, source: _ws.DataSource);
                _ws.Settings.Period = target;
                _setStatus($"已聚合为{target.DisplayName()}：{_ws.Bars.Count} 根。");
            }
            else if (target.IsIntraday() && !current.IsIntraday())
            {
                // 展开：日线 → 分钟线
                var expanded = MultiPeriodConverter.ExpandToIntraday(_ws.Bars, target, _ws.Settings.Seed);
                _ws.ReplaceBars(expanded, source: _ws.DataSource);
                _ws.Settings.Period = target;
                _setStatus($"已由{current.DisplayName()}展开为{target.DisplayName()}：{_ws.Bars.Count} 根。");
            }
            else
            {
                PeriodHint = $"{current.DisplayName()} 无法直接转换为 {target.DisplayName()}，请先聚合到日线或从日线展开。";
                _setStatus(PeriodHint);
                return;
            }

            PeriodHint = $"转换完成：{current.DisplayName()} → {target.DisplayName()}，共 {_ws.Bars.Count} 根。";
            ExportSummary = $"{_ws.Bars.Count} 根K线待导出";
        }
        catch (Exception ex)
        {
            DialogService.Error("周期转换失败：" + ex.Message);
        }
    }

    // ================= 快照 =================

    [RelayCommand]
    private void TakeSnapshot()
    {
        if (_ws.Bars.Count == 0)
        {
            _setStatus("没有K线可快照。");
            return;
        }

        var snapshot = new SnapshotRecord
        {
            Name = string.IsNullOrWhiteSpace(SnapshotName) ? $"快照{_ws.Snapshots.Count + 1}" : SnapshotName.Trim(),
            CreatedAt = DateTime.Now,
            Bars = _ws.Bars.Select(b => b.Clone()).ToList(),
            Stats = KlineStatistics.Compute(_ws.Bars),
        };
        _ws.Snapshots.Add(snapshot);
        SelectedSnapshot = snapshot;
        SnapshotName = $"快照{_ws.Snapshots.Count + 1}";
        _setStatus($"已保存快照「{snapshot.Name}」。");
    }

    [RelayCommand]
    private void RemoveSnapshot()
    {
        if (SelectedSnapshot == null) return;
        _ws.Snapshots.Remove(SelectedSnapshot);
        SelectedSnapshot = _ws.Snapshots.FirstOrDefault();
        CompareRows.Clear();
        _setStatus("已删除快照。");
    }

    [RelayCommand]
    private void CompareSnapshot()
    {
        if (SelectedSnapshot == null)
        {
            _setStatus("请先选择一份快照。");
            return;
        }

        if (_ws.Bars.Count == 0)
        {
            _setStatus("当前没有K线可对比。");
            return;
        }

        var current = KlineStatistics.Compute(_ws.Bars);
        var rows = KlineStatistics.Compare(SelectedSnapshot.Stats, current);
        CompareRows.Clear();
        foreach (var (name, a, b, delta) in rows)
            CompareRows.Add(new CompareRow { Name = name, SnapshotValue = a, CurrentValue = b, Delta = delta });

        CompareSummary = $"「{SelectedSnapshot.Name}」({SelectedSnapshot.Bars.Count}根) vs 当前({_ws.Bars.Count}根)";
        _setStatus("已完成快照对比。");
    }

    [RelayCommand]
    private void LoadSnapshot()
    {
        if (SelectedSnapshot == null) return;
        if (!DialogService.Confirm($"用快照「{SelectedSnapshot.Name}」覆盖当前K线？")) return;
        _ws.ReplaceBars(SelectedSnapshot.Bars.Select(b => b.Clone()), source: "Snapshot");
        ProjectLoaded?.Invoke(this, EventArgs.Empty);
        _setStatus($"已载入快照「{SelectedSnapshot.Name}」。");
    }

    [RelayCommand]
    private void ExportSnapshot()
    {
        var snap = SelectedSnapshot;
        if (snap == null || snap.Bars.Count == 0) return;
        var path = DialogService.SaveFile("K线 CSV (*.csv)|*.csv|所有文件 (*.*)|*.*",
            $"{snap.Name}_{_ws.Rules.Symbol}.csv", "导出快照");
        if (path == null) return;

        try
        {
            CsvKlineService.Export(path, snap.Bars, _ws.Slippages, BuildCsvOptions(),
                _ws.Settings.Period.ToMinutes() >= 240);
            _setStatus("快照已导出：" + path);
        }
        catch (Exception ex)
        {
            DialogService.Error("导出失败：" + ex.Message);
        }
    }

    // ================= 行情模板 =================

    private void RefreshTemplateNames()
    {
        TemplateNames.Clear();
        foreach (var n in _templates.ListNames()) TemplateNames.Add(n);
        SelectedTemplateName = TemplateNames.FirstOrDefault();
    }

    [RelayCommand]
    private void SaveTemplate()
    {
        if (string.IsNullOrWhiteSpace(TemplateName))
        {
            _setStatus("请填写模板名称。");
            return;
        }

        try
        {
            _templates.Save(TemplateName.Trim(), _ws.Segments);
            RefreshTemplateNames();
            SelectedTemplateName = TemplateNames.FirstOrDefault(n => n == TemplateName.Trim());
            _setStatus($"模板「{TemplateName}」已保存到 {_templates.Directory}");
        }
        catch (Exception ex)
        {
            DialogService.Error("保存模板失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private void LoadTemplate()
    {
        if (SelectedTemplateName == null) return;
        try
        {
            var segments = _templates.Load(SelectedTemplateName);
            _ws.Segments.Clear();
            foreach (var s in segments) _ws.Segments.Add(s);
            ProjectLoaded?.Invoke(this, EventArgs.Empty);
            _setStatus($"已套用模板「{SelectedTemplateName}」（{segments.Count} 个阶段）。");
        }
        catch (Exception ex)
        {
            DialogService.Error("载入模板失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private void DeleteTemplate()
    {
        if (SelectedTemplateName == null || SelectedTemplateName.StartsWith("内置")) return;
        _templates.Delete(SelectedTemplateName);
        RefreshTemplateNames();
        _setStatus("模板已删除。");
    }

    // ================= 联动 =================

    [RelayCommand]
    private void SendToGridBacktest()
    {
        if (_ws.Bars.Count == 0)
        {
            _setStatus("没有K线可发送。");
            return;
        }

        var payload = new KlineHandoffPayload
        {
            Symbol = _ws.Rules.Symbol,
            Bars = _ws.Bars.ToList(),                     // 内存引用，无序列化
            Rules = _ws.Rules.Clone(),
            Slippages = _ws.Slippages.Select(s => s.Clone()).ToList(),
            Source = _ws.DataSource,
        };

        HandoffStatus = KlineHandoff.Send(payload) + "  " + payload.Description;
        _setStatus("已发送到网格回测：" + HandoffStatus);
    }

    [RelayCommand]
    private void GoToDrawTab() => _navigateToTab(1);
}
