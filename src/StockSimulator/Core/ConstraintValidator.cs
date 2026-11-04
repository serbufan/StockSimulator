using StockSimulator.Models;

namespace StockSimulator.Core;

/// <summary>
/// 约束校验器：检查K线合法性、报价单位（最小价位）、涨跌停、价格非负、时间单调等。
/// 手绘/导入/生成的数据都应过一遍校验，导出前再校验一次，避免回测软件读入脏数据。
/// </summary>
public static class ConstraintValidator
{
    private const double Eps = 1e-9;

    public static ValidationReport Validate(IReadOnlyList<Kline> bars, MarketRuleSet rules, int maxIssues = 500)
    {
        var report = new ValidationReport();
        if (bars == null || bars.Count == 0)
        {
            report.Add(IssueSeverity.Error, -1, default, "EMPTY", "K线序列为空。");
            return report;
        }

        var tick = rules?.TickSize ?? 0.01;
        var limitPct = rules?.EffectiveLimitPct ?? 0;

        for (var i = 0; i < bars.Count; i++)
        {
            if (report.Issues.Count >= maxIssues)
            {
                report.Add(IssueSeverity.Info, i, bars[i].Time, "TRUNCATED", "问题过多，已截断显示。");
                break;
            }

            var b = bars[i];
            var prev = i > 0 ? bars[i - 1] : null;

            // 1. 价格非负 / 非零
            if (b.Open <= 0 || b.High <= 0 || b.Low <= 0 || b.Close <= 0)
                report.Add(IssueSeverity.Error, i, b.Time, "PRICE_NON_POSITIVE",
                    $"价格必须为正：O={b.Open} H={b.High} L={b.Low} C={b.Close}");

            // 2. OHLC 一致性
            if (b.High + Eps < Math.Max(b.Open, b.Close) || b.Low - Eps > Math.Min(b.Open, b.Close))
                report.Add(IssueSeverity.Error, i, b.Time, "OHLC_INCONSISTENT",
                    $"最高/最低价未包住实体：O={b.Open:F2} H={b.High:F2} L={b.Low:F2} C={b.Close:F2}");

            if (b.High + Eps < b.Low)
                report.Add(IssueSeverity.Error, i, b.Time, "HIGH_BELOW_LOW",
                    $"最高价低于最低价：H={b.High:F2} L={b.Low:F2}");

            // 3. 报价单位
            if (rules != null)
            {
                if (!rules.IsAlignedToTick(b.Open, 1e-6) || !rules.IsAlignedToTick(b.High, 1e-6) ||
                    !rules.IsAlignedToTick(b.Low, 1e-6) || !rules.IsAlignedToTick(b.Close, 1e-6))
                    report.Add(IssueSeverity.Warning, i, b.Time, "TICK_MISALIGNED",
                        $"价格不是最小报价单位 {tick} 的整数倍。");
            }

            // 4. 涨跌停
            if (limitPct > 0 && prev != null && prev.Close > 0)
            {
                var up = rules!.LimitUp(prev.Close);
                var down = rules.LimitDown(prev.Close);
                if (b.High > up + 1e-6 || b.Close > up + 1e-6)
                    report.Add(IssueSeverity.Error, i, b.Time, "LIMIT_UP_BREACH",
                        $"最高价 {b.High:F2} / 收盘价 {b.Close:F2} 超过涨停价 {up:F2}（前收 {prev.Close:F2}）。");
                if (b.Low < down - 1e-6 || b.Close < down - 1e-6)
                    report.Add(IssueSeverity.Error, i, b.Time, "LIMIT_DOWN_BREACH",
                        $"最低价 {b.Low:F2} / 收盘价 {b.Close:F2} 低于跌停价 {down:F2}（前收 {prev.Close:F2}）。");
            }

            // 5. 时间单调 / 重复
            if (prev != null)
            {
                if (b.Time <= prev.Time)
                    report.Add(IssueSeverity.Error, i, b.Time, "TIME_ORDER",
                        $"时间未递增：{prev.Time:yyyy-MM-dd HH:mm} → {b.Time:yyyy-MM-dd HH:mm}");
            }

            // 6. 成交量
            if (b.Volume < 0)
                report.Add(IssueSeverity.Error, i, b.Time, "VOLUME_NEGATIVE", $"成交量为负：{b.Volume}");
            if (b.Volume == 0 && !b.IsSuspended && !b.IsUpLimit && !b.IsDownLimit)
                report.Add(IssueSeverity.Info, i, b.Time, "VOLUME_ZERO", "成交量为 0（非停牌/非一字板）。");

            // 7. 停牌K线必须一字
            if (b.IsSuspended && (Math.Abs(b.High - b.Low) > Eps))
                report.Add(IssueSeverity.Warning, i, b.Time, "SUSPEND_NOT_FLAT",
                    "停牌K线的最高价与最低价应相等。");
        }

        // 8. 首根K线的涨跌停无法校验（无前收），给出提示
        if (bars.Count > 0 && limitPct > 0)
            report.Add(IssueSeverity.Info, 0, bars[0].Time, "FIRST_BAR", "首根K线无前收盘价，跳过涨跌停校验。");

        return report;
    }

    /// <summary>
    /// 一键修复：价格取整到最小报价单位、修正 OHLC 关系、夹到涨跌停区间、剔除负成交量。
    /// 返回被修改的K线根数。
    /// </summary>
    public static int Repair(IReadOnlyList<Kline> bars, MarketRuleSet rules)
    {
        if (bars == null || bars.Count == 0 || rules == null) return 0;

        var changed = 0;
        Kline? prev = null;

        foreach (var b in bars)
        {
            var before = (b.Open, b.High, b.Low, b.Close, b.Volume);
            if (double.IsNaN(b.Open) || double.IsInfinity(b.Open) || b.Open <= 0) b.Open = prev?.Close ?? rules.TickSize * 1000;
            if (double.IsNaN(b.Close) || double.IsInfinity(b.Close) || b.Close <= 0) b.Close = b.Open;
            if (double.IsNaN(b.High) || b.High <= 0) b.High = Math.Max(b.Open, b.Close);
            if (double.IsNaN(b.Low) || b.Low <= 0) b.Low = Math.Min(b.Open, b.Close);

            b.Open = rules.RoundToTick(b.Open);
            b.High = rules.RoundToTick(b.High);
            b.Low = rules.RoundToTick(b.Low);
            b.Close = rules.RoundToTick(b.Close);
            b.High = Math.Max(b.High, Math.Max(b.Open, b.Close));
            b.Low = Math.Min(b.Low, Math.Min(b.Open, b.Close));
            if (b.Low <= 0) b.Low = Math.Max(rules.TickSize, rules.RoundToTick(b.High * 0.5));

            if (prev != null)
            {
                var up = rules.LimitUp(prev.Close);
                var down = rules.LimitDown(prev.Close);
                if (!double.IsInfinity(up))
                {
                    if (b.High > up) b.High = up;
                    if (b.Close > up) b.Close = up;
                    if (b.Open > up) b.Open = up;
                }

                if (b.Low < down) b.Low = down;
                if (b.Close < down) b.Close = down;
                if (b.Open < down) b.Open = down;
            }

            if (b.Volume < 0) b.Volume = 0;
            if (b.Amount <= 0) b.Amount = Math.Round(b.Volume * (b.High + b.Low + b.Close) / 3.0, 2);
            b.FixConsistency();

            if (before != (b.Open, b.High, b.Low, b.Close, b.Volume)) changed++;
            prev = b;
        }

        return changed;
    }
}
