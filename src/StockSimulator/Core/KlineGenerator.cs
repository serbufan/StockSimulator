using StockSimulator.Models;

namespace StockSimulator.Core;

/// <summary>生成结果，含统计与告警信息。</summary>
public sealed class GenerationResult
{
    public List<Kline> Bars { get; } = new();
    public List<string> Warnings { get; } = new();
    public int SegmentCount { get; set; }
    public int SuspendedBars { get; set; }
    public int LimitUpBars { get; set; }
    public int LimitDownBars { get; set; }
    public int GapBars { get; set; }
    public int ExRightBars { get; set; }

    public double StartPrice => Bars.Count == 0 ? 0 : Bars[0].Open;
    public double FinalPrice => Bars.Count == 0 ? 0 : Bars[^1].Close;

    public string Summary =>
        $"共 {Bars.Count} 根K线 | {SegmentCount} 个阶段 | {StartPrice:F2} → {FinalPrice:F2} " +
        $"({(StartPrice == 0 ? 0 : FinalPrice / StartPrice - 1):+0.00%;-0.00%;0.00%})" +
        (SuspendedBars > 0 ? $" | 停牌 {SuspendedBars}" : "") +
        (LimitUpBars > 0 ? $" | 涨停 {LimitUpBars}" : "") +
        (LimitDownBars > 0 ? $" | 跌停 {LimitDownBars}" : "");
}

/// <summary>
/// 【核心】K线生成引擎。纯计算，无任何 UI / 绘图 / IO 依赖，可直接单元测试。
///
/// 生成模型（逐根K线）：
///   1. 跳空：以 gapProbability 概率产生 gap = volatility * gapScale * N(0,1)；
///   2. 开仓价 open = 前收 * (1 + gap)，并夹在涨跌停区间内；
///   3. 实体收益 ret = drift + volatility*z + bullBias*volatility*0.5 + 均值回归项；
///   4. 收盘 close = open * exp(ret)，先按阶段 maxChangePct 软约束，再按涨跌停硬约束；
///   5. 影线按 volatility * wickFactor 的正态绝对值扩展，同样受涨跌停约束；
///   6. 成交量 = 基准量 * 阶段系数 * 对数正态噪声 * 波动放大因子 * 放量/地量异动；
///   7. 全部价格按最小报价单位（默认 0.01）取整，并修正 OHLC 一致性。
///
/// 突发事件：跳空利好/利空（强制开盘跳空）、停牌（OHLC 拉平且成交量为 0）、
/// 除权/除息（前收按现金或送股比例下调，涨跌停以除权后价格为基准，符合A股实际规则）。
/// </summary>
public sealed class KlineGenerator
{
    private readonly KlineGenerationSettings _settings;
    private readonly MarketRuleSet _rules;

    public KlineGenerator(KlineGenerationSettings settings, MarketRuleSet rules)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    /// <summary>按分段参数生成完整K线序列。</summary>
    public GenerationResult Generate(IEnumerable<SegmentConfig> segments)
    {
        var result = new GenerationResult();
        var segs = segments?.ToList() ?? new List<SegmentConfig>();
        if (segs.Count == 0)
        {
            result.Warnings.Add("未配置任何行情阶段，无法生成。");
            return result;
        }

        var time = TradingCalendar.AlignToPeriod(_settings.StartDate, _settings.Period);
        var closes = new List<double>();
        var lastClose = _settings.StartPrice > 0 ? _settings.StartPrice : 10.0;
        var adjFactor = 1.0;
        var segIndex = 0;

        foreach (var seg in segs)
        {
            if (seg == null) continue;
            result.SegmentCount++;

            // 每个阶段使用独立随机流：修改前一段参数不会影响后续阶段的结果
            var rng = new DeterministicRandom(unchecked(_settings.Seed * 131 + segIndex * 7919 + 17));
            segIndex++;

            var events = (seg.Events ?? new System.Collections.ObjectModel.ObservableCollection<MarketEvent>())
                .OrderBy(e => e.AtBarIndex).ToList();
            var eventCursor = 0;
            var produced = 0;
            var target = Math.Max(0, seg.BarCount);
            var guard = 0;

            while (produced < target && guard++ < 500_000)
            {
                // ---------- 1. 取出本根要触发的突发事件 ----------
                MarketEvent? ev = null;
                if (eventCursor < events.Count && events[eventCursor].AtBarIndex <= produced)
                    ev = events[eventCursor++];

                if (ev?.Type == MarketEventType.Suspend)
                {
                    var n = Math.Max(1, ev.DurationBars);
                    for (var k = 0; k < n && produced < target; k++)
                    {
                        // 停牌：OHLC 全部等于前收，成交量为 0，标记 S
                        result.Bars.Add(new Kline(time, lastClose, lastClose, lastClose, lastClose, 0, 0, "S")
                        {
                            AdjFactor = adjFactor,
                        });
                        result.SuspendedBars++;
                        time = TradingCalendar.Next(time, _settings.Period);
                        produced++;
                    }

                    continue;
                }

                // ---------- 2. 除权除息 / 送股：先调整前收 ----------
                var flag = string.Empty;
                if (ev is { Type: MarketEventType.Dividend, Magnitude: > 0 })
                {
                    var exPrice = lastClose - ev.Magnitude;
                    if (exPrice < Math.Max(_rules.TickSize, 0.01))
                    {
                        result.Warnings.Add($"[{seg.Name}] 分红 {ev.Magnitude:F2} 元过大，已按最小价处理。");
                        exPrice = Math.Max(_rules.TickSize, 0.01) * 10;
                    }

                    adjFactor *= exPrice / lastClose;
                    lastClose = _rules.RoundToTick(exPrice);
                    flag += "X";
                    result.ExRightBars++;
                }
                else if (ev is { Type: MarketEventType.Split, Magnitude: > 0 and < 1 })
                {
                    adjFactor *= ev.Magnitude;
                    lastClose = _rules.RoundToTick(lastClose * ev.Magnitude);
                    flag += "X";
                    result.ExRightBars++;
                }

                // ---------- 3. 涨跌停基准（以除权后的前收计算，A股实际规则） ----------
                var limitPct = seg.LimitPct > 0 ? seg.LimitPct : _rules.EffectiveLimitPct;
                var limitUp = limitPct > 0 ? _rules.RoundToTick(lastClose * (1 + limitPct)) : double.MaxValue;
                var limitDown = limitPct > 0 ? _rules.RoundToTick(lastClose * (1 - limitPct)) : 0;

                // ---------- 4. 跳空 ----------
                double gap;
                if (ev?.Type == MarketEventType.GapUp)
                    gap = Math.Abs(ev.Magnitude);
                else if (ev?.Type == MarketEventType.GapDown)
                    gap = -Math.Abs(ev.Magnitude);
                else if (produced > 0 && seg.GapProbability > 0 && rng.Chance(seg.GapProbability))
                    gap = seg.Volatility * Math.Max(0.1, seg.GapScale) * rng.NextGaussian() * 0.9;
                else
                    gap = 0;

                if (gap != 0) flag += "G";

                var open = Clamp(lastClose * (1 + gap), limitDown, limitUp);

                // ---------- 5. 实体收益 ----------
                var meanReversion = 0.0;
                var window = Math.Max(2, _settings.MeanReversionWindow);
                if (seg.MeanReversion > 0 && closes.Count >= window)
                {
                    var ma = 0.0;
                    for (var i = closes.Count - window; i < closes.Count; i++) ma += closes[i];
                    ma /= window;
                    if (ma > 0 && lastClose > 0)
                        meanReversion = -Math.Clamp(seg.MeanReversion, 0, 3) * Math.Log(lastClose / ma);
                }

                var ret = seg.Drift
                          + seg.Volatility * rng.NextGaussian()
                          + Math.Clamp(seg.BullBias, -1, 1) * seg.Volatility * 0.5
                          + meanReversion;
                ret = Clamp(ret, -0.8, 0.8);

                var close = open * Math.Exp(ret);

                // 阶段级软约束：单根涨跌幅上限
                var maxPct = seg.MaxChangePct > 0 ? seg.MaxChangePct : 0.08;
                close = Clamp(close, lastClose * (1 - maxPct), lastClose * (1 + maxPct));
                // 涨跌停硬约束
                close = Clamp(close, limitDown, limitUp);

                // ---------- 6. 影线 ----------
                var wick = Math.Max(0, _settings.WickFactor) * seg.Volatility;
                var high = Math.Max(open, close) * (1 + Math.Abs(rng.NextGaussian()) * wick);
                var low = Math.Min(open, close) * (1 - Math.Abs(rng.NextGaussian()) * wick);
                high = Math.Min(high, limitUp);
                low = Math.Max(low, limitDown);

                // ---------- 7. 取整 + 一致性 ----------
                open = _rules.RoundToTick(open);
                close = _rules.RoundToTick(close);
                high = _rules.RoundToTick(high);
                low = _rules.RoundToTick(low);
                high = Math.Max(high, Math.Max(open, close));
                low = Math.Min(low, Math.Min(open, close));
                if (low <= 0) low = Math.Max(_rules.TickSize, Math.Round(high * 0.5, 2));

                // ---------- 8. 涨跌停标记 ----------
                if (limitPct > 0)
                {
                    if (close >= limitUp - 1e-9) flag += "U";
                    else if (close <= limitDown + 1e-9) flag += "D";
                }

                // ---------- 9. 成交量 ----------
                var volume = SimulateVolume(seg, rng, open, close, high, low, flag);
                var amount = volume * (open + high + low + close) / 4.0;

                var bar = new Kline(time, open, high, low, close, volume, Math.Round(amount, 2), flag)
                {
                    AdjFactor = Math.Round(adjFactor, 6),
                };
                bar.FixConsistency();
                result.Bars.Add(bar);

                if (flag.Contains('U')) result.LimitUpBars++;
                if (flag.Contains('D')) result.LimitDownBars++;
                if (flag.Contains('G')) result.GapBars++;

                lastClose = close;
                closes.Add(close);
                time = TradingCalendar.Next(time, _settings.Period);
                produced++;
            }

            if (produced < target)
                result.Warnings.Add($"[{seg.Name}] 期望 {target} 根，实际生成 {produced} 根。");
        }

        if (result.Bars.Count > 0 && result.Bars[^1].Close <= 0)
            result.Warnings.Add("最终价格非正，请检查起始价格与参数。");

        return result;
    }

    /// <summary>成交量仿真：基准量 × 阶段系数 × 波动放大 × 对数正态噪声 × 放量/地量异动。</summary>
    private long SimulateVolume(SegmentConfig seg, DeterministicRandom rng,
        double open, double close, double high, double low, string flag)
    {
        var baseVolume = Math.Max(1.0, _settings.BaseVolume * Math.Max(0, seg.VolumeFactor));

        // 波动越大成交越活跃
        var moveRatio = seg.Volatility > 1e-9 ? Math.Abs(close - open) / (open * seg.Volatility) : 0;
        var activity = 1.0 + 0.9 * Math.Min(2.5, moveRatio);

        // 振幅放大
        var amplitude = low > 1e-9 ? (high - low) / low : 0;
        var ampFactor = 1.0 + 6.0 * Math.Min(0.12, amplitude);

        var v = baseVolume * activity * ampFactor * Math.Exp(rng.NextGaussian() * 0.32);

        // 放量 / 地量随机异动
        if (_settings.EnableVolumeOutlier)
        {
            var u = rng.NextDouble();
            var spike = Math.Clamp(seg.VolumeSpikeProbability, 0, 1);
            var dry = Math.Clamp(seg.VolumeDryProbability, 0, 1);
            if (u < spike)
                v *= 3.0 + rng.NextDouble() * 4.0;             // 放量 3~7 倍
            else if (u < spike + dry)
                v *= 0.12 + rng.NextDouble() * 0.25;           // 地量 12%~37%
        }

        // 一字板缩量
        if (flag.Contains('U') || flag.Contains('D'))
            v *= 0.35;

        var lot = Math.Max(1, _settings.VolumeLotSize);
        var lots = Math.Round(v / lot, MidpointRounding.AwayFromZero);
        var volume = (long)Math.Max(lot, lots * lot);
        return volume;
    }

    private static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;
}
