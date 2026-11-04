using System.IO;
using StockSimulator.Core;
using StockSimulator.Models;
using StockSimulator.Services;

var outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../samples"));
Directory.CreateDirectory(outDir);

var settings = new KlineGenerationSettings
{
    StartPrice = 12.80,
    StartDate = new DateTime(2024, 1, 2),
    Period = BarPeriod.Day,
    BaseVolume = 3_000_000,
    Seed = 20240102,
};

var rules = new MarketRuleSet
{
    Symbol = "SIM001",
    DisplayName = "仿真标的",
    EnablePriceLimit = true,
    LimitMode = PriceLimitMode.Percent10,
    TickSize = 0.01,
};

var segments = new List<SegmentConfig>
{
    SegmentPresets.Make("1-缩量筑底", 0.0005, 0.008, 20, bullBias: -0.1, meanReversion: 0.35, volumeFactor: 0.7, gapProb: 0.02),
    SegmentPresets.Make("2-温和上升", 0.004, 0.014, 30, bullBias: 0.5, volumeFactor: 1.1),
    SegmentPresets.Make("3-高位震荡", 0.0, 0.02, 20, meanReversion: 0.5, volumeFactor: 1.2),
};
segments[1].Events.Add(new MarketEvent { Type = MarketEventType.GapUp, AtBarIndex = 10, Magnitude = 0.04 });
segments[2].Events.Add(new MarketEvent { Type = MarketEventType.Suspend, AtBarIndex = 8, DurationBars = 2 });
segments[2].Events.Add(new MarketEvent { Type = MarketEventType.Dividend, AtBarIndex = 15, Magnitude = 0.20 });

var result = new KlineGenerator(settings, rules).Generate(segments);
var slippages = new List<SlippageSegment>
{
    new() { Name = "前段低滑点", StartIndex = 0, EndIndex = 49, SlippageBps = 5, SlippageTicks = 1 },
    new() { Name = "后段高滑点", StartIndex = 50, EndIndex = int.MaxValue, SlippageBps = 12, SlippageTicks = 2 },
};

var dailyPath = Path.Combine(outDir, "sample_daily_ohlcv.csv");
CsvKlineService.Export(dailyPath, result.Bars, slippages, new CsvExportOptions(), true);

var minute = MultiPeriodConverter.ExpandToIntraday(result.Bars.Take(3).ToList(), BarPeriod.Minute30, settings.Seed);
var minutePath = Path.Combine(outDir, "sample_30min_ohlcv.csv");
CsvKlineService.Export(minutePath, minute, slippages, new CsvExportOptions(), false);

Console.WriteLine(result.Summary);
Console.WriteLine($"日线样例: {dailyPath}");
Console.WriteLine($"30分钟样例: {minutePath} ({minute.Count} 根)");
Console.WriteLine(File.ReadAllLines(dailyPath).Take(4).Aggregate((a, b) => a + Environment.NewLine + b));
