using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockSimulator.Core;
using StockSimulator.Models;
using StockSimulator.Services;

namespace StockSimulator.ViewModels;

/// <summary>
/// Tab3：批量生成与变体管理。
/// 基于“基础行情”（当前工作K线）批量派生出多份带随机扰动的K线，用于网格策略压力测试。
/// </summary>
public sealed partial class BatchTabViewModel : ObservableObject
{
    private readonly Workspace _ws;
    private readonly Action<string> _setStatus;
    private List<Kline> _baseBars = new();

    /// <summary>请求把选中的变体应用到工作区（由主窗口转发给手绘 Tab 刷新）。</summary>
    public event EventHandler? ApplyToWorkspaceRequested;

    public BatchTabViewModel(Workspace workspace, Action<string> setStatus)
    {
        _ws = workspace;
        _setStatus = setStatus;
    }

    public ObservableCollection<VariantSeries> Variants { get; } = new();

    [ObservableProperty] private int _variantCount = 10;
    [ObservableProperty] private double _priceJitter = 0.012;
    [ObservableProperty] private double _volumeJitter = 0.2;
    [ObservableProperty] private double _trendShift = 0.0;
    [ObservableProperty] private bool _enableGapInjection = true;
    [ObservableProperty] private int _gapCount = 1;
    [ObservableProperty] private double _gapSize = 0.04;
    [ObservableProperty] private int _seedOffset = 1000;

    [ObservableProperty] private VariantSeries? _selectedVariant;
    [ObservableProperty] private string _baseInfo = "基础行情：0 根";
    [ObservableProperty] private string _summary = "尚未生成变体。";

    /// <summary>把当前工作K线设为批量生成的基础行情。</summary>
    [RelayCommand]
    private void CaptureBase()
    {
        _baseBars = _ws.Bars.Select(b => b.Clone()).ToList();
        BaseInfo = $"基础行情：{_baseBars.Count} 根（{_baseBars.FirstOrDefault()?.Time:yyyy-MM-dd} ~ " +
                   $"{_baseBars.LastOrDefault()?.Time:yyyy-MM-dd}，来源 {_ws.DataSource}）";
        _setStatus($"已捕获基础行情 {_baseBars.Count} 根。");
    }

    [RelayCommand]
    private void GenerateVariants()
    {
        if (_baseBars.Count == 0)
        {
            if (_ws.Bars.Count == 0)
            {
                _setStatus("没有基础行情，请先在「分段参数」页生成或「手绘编辑」页导入。");
                return;
            }

            CaptureBase();
        }

        var options = new VariantOptions
        {
            Count = Math.Clamp(VariantCount, 1, 200),
            PriceJitterPct = Math.Max(0, PriceJitter),
            VolumeJitterPct = Math.Max(0, VolumeJitter),
            TrendShiftPct = Math.Max(0, TrendShift),
            EnableGapInjection = EnableGapInjection,
            GapCount = Math.Max(0, GapCount),
            GapSizePct = Math.Max(0, GapSize),
            SeedOffset = Math.Max(1, SeedOffset),
        };

        var list = VariantGenerator.Generate(_baseBars, options, _ws.Settings.Seed, _ws.Rules);
        Variants.Clear();
        foreach (var v in list) Variants.Add(v);
        SelectedVariant = Variants.FirstOrDefault();

        var avgChange = list.Count == 0 ? 0 : list.Average(v => v.Stats.ChangePct);
        var avgDd = list.Count == 0 ? 0 : list.Average(v => v.Stats.MaxDrawdown);
        Summary = $"已生成 {list.Count} 份变体 | 平均区间涨跌 {avgChange:+0.00%;-0.00%;0.00%} | 平均最大回撤 {avgDd:0.00%}";
        _setStatus(Summary);
    }

    [RelayCommand]
    private void ApplySelected()
    {
        if (SelectedVariant == null) return;
        _ws.ReplaceBars(SelectedVariant.Bars.Select(b => b.Clone()), source: "Variant");
        _setStatus($"已将「{SelectedVariant.Name}」应用到工作区，可在「手绘编辑」页继续手绘。");
        ApplyToWorkspaceRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ExportAll()
    {
        if (Variants.Count == 0)
        {
            _setStatus("没有可导出的变体。");
            return;
        }

        var folder = DialogService.PickFolder("选择变体 CSV 输出目录");
        if (folder == null) return;

        try
        {
            var options = new CsvExportOptions();
            var files = CsvKlineService.ExportVariants(folder, Variants, _ws.Slippages, options,
                _ws.Settings.Period.ToMinutes() >= 240);
            _setStatus($"已导出 {files.Count} 个文件到 {folder}");
            DialogService.Info($"已导出 {Variants.Count} 份变体 CSV + index.csv 到：\n{folder}");
        }
        catch (Exception ex)
        {
            DialogService.Error("导出失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private void ExportSelected()
    {
        if (SelectedVariant == null) return;
        var path = DialogService.SaveFile("K线 CSV (*.csv)|*.csv|所有文件 (*.*)|*.*",
            SelectedVariant.FileName + ".csv", "导出选中变体");
        if (path == null) return;

        try
        {
            CsvKlineService.Export(path, SelectedVariant.Bars, _ws.Slippages, new CsvExportOptions(),
                _ws.Settings.Period.ToMinutes() >= 240);
            _setStatus($"已导出：{path}");
        }
        catch (Exception ex)
        {
            DialogService.Error("导出失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private void ClearVariants()
    {
        Variants.Clear();
        SelectedVariant = null;
        Summary = "尚未生成变体。";
    }
}
