using StockSimulator.Models;

namespace StockSimulator.Core;

/// <summary>
/// 多周期转换：
///   1. Aggregate —— 由细周期聚合到粗周期（分钟 → 日/周/月，或 1 分钟 → 5/15/30/60 分钟）；
///   2. ExpandToIntraday —— 由日线“展开”出分钟级K线（布朗桥插值 + 日内 U 型量能分布），
///      展开后的分钟线首尾价、最高最低价均落在原日线区间内，保证与日线一致。
/// </summary>
public static class MultiPeriodConverter
{
    /// <summary>聚合成粗周期。时间戳取该组第一根K线的时间。</summary>
    public static List<Kline> Aggregate(IReadOnlyList<Kline> source, BarPeriod target)
    {
        var result = new List<Kline>();
        if (source == null || source.Count == 0) return result;

        string? currentKey = null;
        var bucket = new List<Kline>();

        void Flush()
        {
            if (bucket.Count == 0) return;
            var first = bucket[0];
            var last = bucket[^1];
            var high = bucket.Max(b => b.High);
            var low = bucket.Min(b => b.Low);
            var volume = bucket.Sum(b => b.Volume);
            var amount = bucket.Sum(b => b.Amount > 0 ? b.Amount : b.Volume * (b.High + b.Low + b.Close) / 3.0);
            var flag = bucket.All(b => b.IsSuspended) ? "S" : last.Flag;

            var bar = new Kline(first.Time, first.Open, high, low, last.Close, volume, Math.Round(amount, 2), flag)
            {
                AdjFactor = last.AdjFactor,
            };
            bar.FixConsistency();
            result.Add(bar);
            bucket = new List<Kline>();
        }

        foreach (var bar in source)
        {
            var key = BucketKey(bar.Time, target);
            if (key != currentKey)
            {
                Flush();
                currentKey = key;
            }

            bucket.Add(bar);
        }

        Flush();
        return result;
    }

    /// <summary>
    /// 日线 → 分钟线。
    /// 用布朗桥（Brownian bridge）在开盘价与收盘价之间插值，并按日内 U 型量能分布拆分成交量。
    /// </summary>
    public static List<Kline> ExpandToIntraday(IReadOnlyList<Kline> dailyBars, BarPeriod target,
        int seed = 20240101, double volatilityFactor = 0.55, bool keepSuspendedDays = true)
    {
        var result = new List<Kline>();
        if (dailyBars == null || dailyBars.Count == 0) return result;
        if (!target.IsIntraday())
            throw new ArgumentException("目标周期必须是分钟级周期。", nameof(target));

        var perDay = target.BarsPerTradingDay();
        var step = target.ToMinutes();
        var dayIndex = 0;

        foreach (var daily in dailyBars)
        {
            var rng = new DeterministicRandom(unchecked(seed + dayIndex * 7919 + 13));
            dayIndex++;

            var day = daily.Time.Date;
            var open = daily.Open;
            var close = daily.Close;
            var high = Math.Max(daily.High, Math.Max(open, close));
            var low = Math.Min(daily.Low, Math.Min(open, close));
            if (low <= 0) low = Math.Max(0.01, open * 0.9);
            if (high <= low) high = Math.Max(high, low * 1.001);

            // 停牌日：全部拉平
            if (daily.IsSuspended || perDay <= 0)
            {
                if (!keepSuspendedDays) continue;
                for (var k = 0; k < perDay; k++)
                {
                    var t = TradingCalendar.FromSessionMinute(day, k * step);
                    result.Add(new Kline(t, daily.Close, daily.Close, daily.Close, daily.Close, 0, 0, "S")
                    {
                        AdjFactor = daily.AdjFactor,
                    });
                }

                continue;
            }

            // --- 1. 构造对数价格布朗桥 ---
            var n = perDay;
            var increments = new double[n + 1];
            for (var k = 1; k <= n; k++)
                increments[k] = increments[k - 1] + rng.NextGaussian();

            var logOpen = Math.Log(open);
            var logClose = Math.Log(close);
            var devMax = double.NegativeInfinity;
            var devMin = double.PositiveInfinity;
            var devMaxIndex = 0;
            var devMinIndex = 0;
            var linear = new double[n + 1];
            var dev = new double[n + 1];

            for (var k = 0; k <= n; k++)
            {
                linear[k] = logOpen + (logClose - logOpen) * k / n;
                dev[k] = increments[k] - increments[n] * k / n;
                if (dev[k] > devMax) { devMax = dev[k]; devMaxIndex = k; }
                if (dev[k] < devMin) { devMin = dev[k]; devMinIndex = k; }
            }

            // 缩放：让桥的上下偏离刚好填满当日振幅的 90%
            var positiveScale = 1.0;
            var negativeScale = 1.0;
            if (devMax > 1e-9)
            {
                var room = Math.Log(high) - linear[devMaxIndex];
                positiveScale = room > 1e-9 ? Math.Min(1.0, 0.9 * room / devMax) : 0.05;
            }

            if (devMin < -1e-9)
            {
                var room = linear[devMinIndex] - Math.Log(low);
                negativeScale = room > 1e-9 ? Math.Min(1.0, 0.9 * room / -devMin) : 0.05;
            }

            var path = new double[n + 1];
            for (var k = 0; k <= n; k++)
            {
                var d = dev[k] >= 0 ? dev[k] * positiveScale : dev[k] * negativeScale;
                path[k] = Math.Exp(linear[k] + d);
            }

            // 端点严格对齐日线开收盘
            path[0] = open;
            path[n] = close;

            // --- 2. 日内 U 型量能权重 ---
            var weights = new double[n];
            var weightSum = 0.0;
            for (var k = 0; k < n; k++)
            {
                var x = (k + 0.5) / n;                       // 0~1
                var uShape = 0.55 + 2.2 * Math.Pow(2 * Math.Abs(x - 0.5), 2);   // 两头大中间小
                var lunchDip = (x > 0.42 && x < 0.58) ? 0.65 : 1.0;             // 午间缩量
                var w = Math.Max(0.05, uShape * lunchDip * (0.75 + rng.NextDouble() * 0.5));
                weights[k] = w;
                weightSum += w;
            }

            var remainingVolume = daily.Volume;
            var volFactor = Math.Max(0.001, volatilityFactor);

            // --- 3. 逐根生成分钟K线 ---
            for (var k = 0; k < n; k++)
            {
                var t = TradingCalendar.FromSessionMinute(day, k * step);
                var o = path[k];
                var c = path[k + 1];
                var wig = Math.Abs(rng.NextGaussian()) * volFactor * 0.4 * Math.Abs(c - o) + (high - low) * 0.004;
                var h = Math.Max(o, c) + wig;
                var l = Math.Min(o, c) - wig;
                h = Math.Min(h, high);
                l = Math.Max(l, low);
                if (l <= 0) l = Math.Max(0.01, Math.Min(o, c) * 0.995);

                long volume;
                if (k == n - 1)
                    volume = Math.Max(0, remainingVolume);
                else
                    volume = (long)Math.Round(daily.Volume * weights[k] / weightSum);
                remainingVolume -= volume;

                var bar = new Kline(t, Round2(o), Round2(h), Round2(l), Round2(c), Math.Max(0, volume),
                    Math.Round(Math.Max(0, volume) * (o + c) / 2, 2), daily.Flag);
                bar.FixConsistency();
                result.Add(bar);
            }
        }

        return result;
    }

    /// <summary>按周期计算分桶键，用于聚合。</summary>
    public static string BucketKey(DateTime time, BarPeriod period)
    {
        switch (period)
        {
            case BarPeriod.Month:
                return $"{time.Year:D4}-{time.Month:D2}";
            case BarPeriod.Week:
            {
                var offset = ((int)time.DayOfWeek + 6) % 7;   // 周一 = 0
                var monday = time.Date.AddDays(-offset);
                return $"W{monday:yyyyMMdd}";
            }
            case BarPeriod.Day:
                return $"D{time:yyyyMMdd}";
            default:
            {
                var step = period.ToMinutes();
                var idx = TradingCalendar.SessionMinuteIndex(time) / Math.Max(1, step);
                return $"M{time:yyyyMMdd}_{idx:D3}";
            }
        }
    }

    private static double Round2(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
