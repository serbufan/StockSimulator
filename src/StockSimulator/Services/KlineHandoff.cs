using StockSimulator.Models;

namespace StockSimulator.Services;

/// <summary>联动载荷：把当前K线连同规则、滑点一起打包，纯内存传递，不落盘。</summary>
public sealed class KlineHandoffPayload
{
    public required string Symbol { get; init; }
    public required IReadOnlyList<Kline> Bars { get; init; }
    public MarketRuleSet? Rules { get; init; }
    public IReadOnlyList<SlippageSegment>? Slippages { get; init; }
    public string Source { get; init; } = "Manual";
    public DateTime CreatedAt { get; } = DateTime.Now;

    public string Description =>
        $"{Symbol} | {Bars.Count} 根 | {Bars.FirstOrDefault()?.Time:yyyy-MM-dd} ~ {Bars.LastOrDefault()?.Time:yyyy-MM-dd}";
}

/// <summary>
/// 网格回测接收端需要实现的接口。
/// 同一个进程内的网格回测模块（或联动的另一个 Tab / 另一个窗口）注册本接口即可接收数据。
/// </summary>
public interface IGridBacktestReceiver
{
    string Name { get; }
    string Receive(KlineHandoffPayload payload);
}

/// <summary>
/// “一键发送到网格回测”的内存通道。
/// 使用方式（在网格回测模块初始化时）：
/// <code>
/// KlineHandoff.Receiver = new MyGridBacktestReceiver();
/// // 或者订阅事件做更细粒度的处理
/// KlineHandoff.HandoffRequested += payload => { ... };
/// </code>
/// 数据全程在内存中以对象引用传递，不经过文件，也不做序列化。
/// </summary>
public static class KlineHandoff
{
    public static event Action<KlineHandoffPayload>? HandoffRequested;

    public static IGridBacktestReceiver? Receiver { get; set; }

    public static KlineHandoffPayload? LastPayload { get; private set; }

    public static string LastResult { get; private set; } = "尚未发送。";

    /// <summary>发送当前K线到网格回测端。</summary>
    public static string Send(KlineHandoffPayload payload)
    {
        LastPayload = payload;

        var results = new List<string>();
        try
        {
            Receiver?.Receive(payload);
            if (Receiver != null) results.Add($"已送达「{Receiver.Name}」");
            HandoffRequested?.Invoke(payload);
            if (HandoffRequested != null) results.Add("已广播给订阅方");
        }
        catch (Exception ex)
        {
            LastResult = $"发送失败：{ex.Message}";
            return LastResult;
        }

        LastResult = results.Count == 0
            ? "当前没有网格回测模块注册接收（仅缓存到内存通道）。"
            : string.Join("；", results) + $"（{payload.Bars.Count} 根K线，内存传递）";

        return LastResult;
    }
}

/// <summary>
/// 演示用网格回测接收端：仅做统计，用于验证联动通道是否打通。
/// 实际项目中替换为不对称网格回测引擎即可。
/// </summary>
public sealed class DemoGridBacktestReceiver : IGridBacktestReceiver
{
    public string Name => "内置网格联动演示接收器";

    public double GridStepPct { get; set; } = 0.02;

    public string Receive(KlineHandoffPayload payload)
    {
        var bars = payload.Bars;
        if (bars.Count == 0) return "收到空数据。";

        var low = bars.Min(b => b.Low);
        var high = bars.Max(b => b.High);
        var step = Math.Max(0.001, GridStepPct);
        var levels = new List<double>();
        for (var p = low; p <= high * (1 + 1e-9); p *= 1 + step)
            levels.Add(Math.Round(p, 2));

        var slip = payload.Slippages?.FirstOrDefault();
        return $"已接收 {bars.Count} 根K线（{low:F2}~{high:F2}），按 {step:P1} 等比网格将生成 {levels.Count} 条网格线" +
               (slip != null ? $"，滑点 {slip.SlippageBps:F0}bp。" : "。");
    }
}
