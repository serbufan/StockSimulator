using System.IO;
using StockSimulator.Models;
using StockSimulator.Services;

namespace StockSimulator.Tests;

/// <summary>CSV 导入导出、工程文件、联动通道测试。</summary>
public class IoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ks_tests_" + Guid.NewGuid().ToString("N"));

    public IoTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // 忽略
        }
    }

    private static List<Kline> SampleBars(int count = 20)
    {
        var bars = new List<Kline>();
        var price = 10.0;
        for (var i = 0; i < count; i++)
        {
            var open = Math.Round(price, 2);
            var close = Math.Round(price * (1 + (i % 3 == 0 ? 0.01 : -0.008)), 2);
            bars.Add(new Kline(new DateTime(2024, 1, 2).AddDays(i), open,
                Math.Max(open, close) + 0.05, Math.Min(open, close) - 0.05, close, 100_000 + i * 100));
            price = close;
        }

        return bars;
    }

    [Fact]
    public void Csv_ExportImport_RoundTrip()
    {
        var path = Path.Combine(_dir, "bars.csv");
        var bars = SampleBars();
        var slippages = new List<SlippageSegment>
        {
            new() { Name = "前段", StartIndex = 0, EndIndex = 9, SlippageBps = 5, SlippageTicks = 1 },
            new() { Name = "后段", StartIndex = 10, EndIndex = int.MaxValue, SlippageBps = 12, SlippageTicks = 2 },
        };

        CsvKlineService.Export(path, bars, slippages, new CsvExportOptions(), true);

        var text = File.ReadAllText(path);
        Assert.StartsWith("Date,Open,High,Low,Close,Volume", text.TrimStart('\uFEFF'));

        var imported = CsvKlineService.Import(path);
        Assert.Equal(bars.Count, imported.Bars.Count);
        for (var i = 0; i < bars.Count; i++)
        {
            Assert.Equal(bars[i].Time.Date, imported.Bars[i].Time.Date);
            Assert.Equal(bars[i].Open, imported.Bars[i].Open, 2);
            Assert.Equal(bars[i].High, imported.Bars[i].High, 2);
            Assert.Equal(bars[i].Low, imported.Bars[i].Low, 2);
            Assert.Equal(bars[i].Close, imported.Bars[i].Close, 2);
            Assert.Equal(bars[i].Volume, imported.Bars[i].Volume);
        }
    }

    [Fact]
    public void Csv_ImportsChineseHeaders()
    {
        var path = Path.Combine(_dir, "cn.csv");
        File.WriteAllText(path,
            "日期,开盘价,最高价,最低价,收盘价,成交量\r\n" +
            "2024-01-02,10.00,10.50,9.90,10.30,120000\r\n" +
            "2024-01-03,10.30,10.80,10.20,10.60,150000\r\n");

        var result = CsvKlineService.Import(path);

        Assert.Equal(2, result.Bars.Count);
        Assert.Equal(10.30, result.Bars[0].Close, 2);
        Assert.Equal(150000, result.Bars[1].Volume);
    }

    [Fact]
    public void Csv_Throws_WhenHeadersUnrecognized()
    {
        var path = Path.Combine(_dir, "bad.csv");
        File.WriteAllText(path, "a,b,c\r\n1,2,3\r\n");

        Assert.Throws<InvalidDataException>(() => CsvKlineService.Import(path));
    }

    [Fact]
    public void Project_SaveLoad_PreservesEverything()
    {
        var path = Path.Combine(_dir, "demo.ksproj.json");
        var doc = new ProjectDocument
        {
            Rules = new MarketRuleSet { Symbol = "TEST01", LimitMode = PriceLimitMode.Percent20 },
            Settings = new KlineGenerationSettings { StartPrice = 25.5, Seed = 77, Period = BarPeriod.Minute15 },
            Segments = new List<SegmentConfig>
            {
                new()
                {
                    Name = "阶段A",
                    BarCount = 33,
                    Drift = 0.003,
                    Events = new System.Collections.ObjectModel.ObservableCollection<MarketEvent>
                    {
                        new() { Type = MarketEventType.Suspend, AtBarIndex = 4, DurationBars = 2 },
                    },
                },
            },
            Bars = SampleBars(8),
            Annotations = new List<Annotation> { new() { BarIndex = 3, Text = "突破位", ColorHex = "#FF0000" } },
            Slippages = new List<SlippageSegment> { new() { SlippageBps = 8, SlippageTicks = 1.5 } },
            DataSource = "Manual",
        };

        ProjectService.Save(path, doc);
        var loaded = ProjectService.Load(path);

        Assert.Equal("TEST01", loaded.Rules.Symbol);
        Assert.Equal(PriceLimitMode.Percent20, loaded.Rules.LimitMode);
        Assert.Equal(25.5, loaded.Settings.StartPrice);
        Assert.Equal(BarPeriod.Minute15, loaded.Settings.Period);
        Assert.Single(loaded.Segments);
        Assert.Equal(33, loaded.Segments[0].BarCount);
        Assert.Single(loaded.Segments[0].Events);
        Assert.Equal(MarketEventType.Suspend, loaded.Segments[0].Events[0].Type);
        Assert.Equal(8, loaded.Bars.Count);
        Assert.Equal("突破位", loaded.Annotations[0].Text);
        Assert.Equal(8, loaded.Slippages[0].SlippageBps);
        Assert.Equal("Manual", loaded.DataSource);
    }

    [Fact]
    public void KlineHandoff_DeliversInMemory()
    {
        var received = 0;
        var receiver = new CountingReceiver(() => received++);
        var original = KlineHandoff.Receiver;
        try
        {
            KlineHandoff.Receiver = receiver;
            var payload = new KlineHandoffPayload
            {
                Symbol = "SIM001",
                Bars = SampleBars(15),
                Rules = new MarketRuleSet(),
            };

            var result = KlineHandoff.Send(payload);

            Assert.NotNull(result);
            Assert.Equal(1, received);
            Assert.Same(payload, KlineHandoff.LastPayload);
        }
        finally
        {
            KlineHandoff.Receiver = original;
        }
    }

    private sealed class CountingReceiver : IGridBacktestReceiver
    {
        private readonly Action _onReceive;
        public CountingReceiver(Action onReceive) => _onReceive = onReceive;
        public string Name => "测试接收器";
        public string Receive(KlineHandoffPayload payload)
        {
            _onReceive();
            return "ok";
        }
    }
}
