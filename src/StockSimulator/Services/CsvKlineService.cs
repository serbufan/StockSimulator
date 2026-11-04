using System.Globalization;
using System.IO;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using StockSimulator.Models;

namespace StockSimulator.Services;

/// <summary>CSV 导出选项。</summary>
public sealed class CsvExportOptions
{
    public bool IncludeAmount { get; set; } = true;
    public bool IncludeChangePct { get; set; } = true;
    public bool IncludeFlag { get; set; } = true;
    public bool IncludeAdjFactor { get; set; } = true;

    /// <summary>是否随K线一并导出滑点分段信息（SlippageBps / SlippageTicks）。</summary>
    public bool IncludeSlippage { get; set; } = true;

    public int PriceDecimals { get; set; } = 2;
    public string DateFormat { get; set; } = "yyyy-MM-dd";
    public string DateTimeFormat { get; set; } = "yyyy-MM-dd HH:mm";
    public bool WriteBom { get; set; } = true;
    public string Delimiter { get; set; } = ",";

    /// <summary>日线及以上周期只输出日期（不带时间）。</summary>
    public bool DateOnlyForDaily { get; set; } = true;

    public CsvExportOptions Clone() => (CsvExportOptions)MemberwiseClone();
}

/// <summary>CSV 导入结果。</summary>
public sealed class CsvImportResult
{
    public List<Kline> Bars { get; } = new();
    public List<string> Warnings { get; } = new();
    public string DetectedTimeFormat { get; set; } = string.Empty;

    public string Summary => $"导入 {Bars.Count} 根K线" +
                             (Warnings.Count > 0 ? $"，{Warnings.Count} 条提示" : "");
}

/// <summary>
/// CSV 读写服务。导出为标准 OHLCV（前 6 列固定：Date,Open,High,Low,Close,Volume），
/// 之后为可选扩展列（Amount/ChangePct/Flag/AdjFactor/SlippageBps/SlippageTicks），
/// 绝大多数网格回测软件可直接导入，也可只用前 6 列。
/// </summary>
public static class CsvKlineService
{
    private static readonly string[] TimeHeaders =
    {
        "date", "datetime", "time", "日期", "时间", "日期时间", "trade_date", "tradedate", "bar_time",
    };

    private static readonly string[] OpenHeaders = { "open", "开盘", "开盘价", "o" };
    private static readonly string[] HighHeaders = { "high", "最高", "最高价", "h" };
    private static readonly string[] LowHeaders = { "low", "最低", "最低价", "l" };
    private static readonly string[] CloseHeaders = { "close", "收盘", "收盘价", "c" };
    private static readonly string[] VolumeHeaders = { "volume", "vol", "成交量", "v", "成交量(手)" };
    private static readonly string[] AmountHeaders = { "amount", "成交额", "成交金额", "turnover" };
    private static readonly string[] FlagHeaders = { "flag", "标记", "状态" };

    /// <summary>导出标准 CSV。</summary>
    public static void Export(string filePath, IReadOnlyList<Kline> bars,
        IReadOnlyList<SlippageSegment>? slippages = null, CsvExportOptions? options = null,
        bool isDailyOrCoarser = true)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("导出路径为空。", nameof(filePath));
        options ??= new CsvExportOptions();
        slippages ??= Array.Empty<SlippageSegment>();

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var encoding = new UTF8Encoding(options.WriteBom);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = options.Delimiter,
            HasHeaderRecord = true,
        };

        using var writer = new StreamWriter(filePath, false, encoding);
        using var csv = new CsvWriter(writer, config);

        // 表头
        csv.WriteField("Date");
        csv.WriteField("Open");
        csv.WriteField("High");
        csv.WriteField("Low");
        csv.WriteField("Close");
        csv.WriteField("Volume");
        if (options.IncludeAmount) csv.WriteField("Amount");
        if (options.IncludeChangePct) csv.WriteField("ChangePct");
        if (options.IncludeFlag) csv.WriteField("Flag");
        if (options.IncludeAdjFactor) csv.WriteField("AdjFactor");
        if (options.IncludeSlippage)
        {
            csv.WriteField("SlippageBps");
            csv.WriteField("SlippageTicks");
        }

        csv.NextRecord();

        var useDateOnly = isDailyOrCoarser && options.DateOnlyForDaily;

        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            csv.WriteField(useDateOnly
                ? bar.Time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : bar.Time.ToString(options.DateTimeFormat, CultureInfo.InvariantCulture));
            csv.WriteField(Round(bar.Open, options.PriceDecimals));
            csv.WriteField(Round(bar.High, options.PriceDecimals));
            csv.WriteField(Round(bar.Low, options.PriceDecimals));
            csv.WriteField(Round(bar.Close, options.PriceDecimals));
            csv.WriteField(bar.Volume);

            if (options.IncludeAmount)
            {
                var amount = bar.Amount > 0
                    ? bar.Amount
                    : bar.Volume * (bar.High + bar.Low + bar.Close) / 3.0;
                csv.WriteField(Math.Round(amount, 2));
            }

            if (options.IncludeChangePct)
            {
                var prevClose = i > 0 ? bars[i - 1].Close : bar.Open;
                var pct = prevClose > 0 ? bar.Close / prevClose - 1 : 0;
                csv.WriteField(Math.Round(pct * 100, 4));   // 百分数，例如 2.35 表示 +2.35%
            }

            if (options.IncludeFlag) csv.WriteField(bar.Flag);

            if (options.IncludeAdjFactor) csv.WriteField(Math.Round(bar.AdjFactor, 6));

            if (options.IncludeSlippage)
            {
                var slip = slippages.FirstOrDefault(s => s.Contains(i));
                csv.WriteField(slip == null ? 0 : Math.Round(slip.SlippageBps, 2));
                csv.WriteField(slip == null ? 0 : Math.Round(slip.SlippageTicks, 2));
            }

            csv.NextRecord();
        }

        csv.Flush();
    }

    /// <summary>导出多份变体到指定目录（每份一个 CSV，另生成 index.csv 汇总统计）。</summary>
    public static List<string> ExportVariants(string directory, IEnumerable<Core.VariantSeries> variants,
        IReadOnlyList<SlippageSegment>? slippages = null, CsvExportOptions? options = null,
        bool isDailyOrCoarser = true)
    {
        Directory.CreateDirectory(directory);
        var files = new List<string>();
        var index = new List<(string File, Core.VariantSeries V)>();

        foreach (var v in variants)
        {
            var path = Path.Combine(directory, v.FileName + ".csv");
            Export(path, v.Bars, slippages, options, isDailyOrCoarser);
            files.Add(path);
            index.Add((Path.GetFileName(path), v));
        }

        var indexPath = Path.Combine(directory, "index.csv");
        using (var writer = new StreamWriter(indexPath, false, new UTF8Encoding(true)))
        using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            csv.WriteField("File");
            csv.WriteField("Name");
            csv.WriteField("Bars");
            csv.WriteField("ChangePct");
            csv.WriteField("MaxDrawdown");
            csv.WriteField("AnnualVolatility");
            csv.WriteField("AvgVolume");
            csv.WriteField("LastClose");
            csv.NextRecord();

            foreach (var (file, v) in index)
            {
                csv.WriteField(file);
                csv.WriteField(v.Name);
                csv.WriteField(v.BarCount);
                csv.WriteField(Math.Round(v.Stats.ChangePct * 100, 4));
                csv.WriteField(Math.Round(v.Stats.MaxDrawdown * 100, 4));
                csv.WriteField(Math.Round(v.Stats.AnnualVolatility * 100, 4));
                csv.WriteField(Math.Round(v.Stats.AvgVolume, 2));
                csv.WriteField(Math.Round(v.Stats.LastClose, 2));
                csv.NextRecord();
            }
        }

        files.Add(indexPath);
        return files;
    }

    /// <summary>
    /// 导入外部真实K线 CSV。支持中英文表头、逗号/分号/制表符分隔、常见日期格式。
    /// </summary>
    public static CsvImportResult Import(string filePath)
    {
        var result = new CsvImportResult();
        if (!File.Exists(filePath))
            throw new FileNotFoundException("找不到 CSV 文件。", filePath);

        var delimiter = DetectDelimiter(filePath);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter,
            HasHeaderRecord = true,
            BadDataFound = null,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.Trim,
            DetectColumnCountChanges = false,
        };

        using var reader = new StreamReader(filePath, Encoding.UTF8, true);
        using var csv = new CsvReader(reader, config);

        if (!csv.Read() || !csv.ReadHeader())
            throw new InvalidDataException("CSV 文件没有表头。");

        var headers = csv.HeaderRecord ?? Array.Empty<string>();
        var map = MapHeaders(headers);

        if (map.Time < 0 || map.Open < 0 || map.High < 0 || map.Low < 0 || map.Close < 0)
            throw new InvalidDataException(
                "无法识别列名。至少需要时间/开盘/最高/最低/收盘列（支持 Date/Open/High/Low/Close 或 日期/开盘价/最高价/最低价/收盘价）。");

        var row = 1;
        while (csv.Read())
        {
            row++;
            try
            {
                var timeText = csv.GetField(map.Time);
                if (string.IsNullOrWhiteSpace(timeText)) continue;

                var time = ParseTime(timeText!, out var fmt);
                if (fmt.Length > 0 && result.DetectedTimeFormat.Length == 0)
                    result.DetectedTimeFormat = fmt;

                var open = ParseDouble(csv.GetField(map.Open));
                var high = ParseDouble(csv.GetField(map.High));
                var low = ParseDouble(csv.GetField(map.Low));
                var close = ParseDouble(csv.GetField(map.Close));
                var volume = map.Volume >= 0 ? (long)Math.Round(ParseDouble(csv.GetField(map.Volume))) : 0;
                var amount = map.Amount >= 0 ? ParseDouble(csv.GetField(map.Amount)) : 0;
                var flag = map.Flag >= 0 ? csv.GetField(map.Flag) ?? string.Empty : string.Empty;

                var bar = new Kline(time, open, high, low, close, volume, amount, flag);
                bar.FixConsistency();
                result.Bars.Add(bar);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"第 {row} 行解析失败：{ex.Message}");
                if (result.Warnings.Count > 50) break;
            }
        }

        if (result.Bars.Count == 0)
            throw new InvalidDataException("CSV 中没有解析到有效K线。");

        // 排序 + 去重
        result.Bars.Sort((a, b) => a.Time.CompareTo(b.Time));
        var unique = new List<Kline>(result.Bars.Count);
        foreach (var bar in result.Bars)
        {
            if (unique.Count > 0 && unique[^1].Time == bar.Time)
            {
                result.Warnings.Add($"{bar.Time:yyyy-MM-dd HH:mm} 时间重复，已保留后一条。");
                unique[^1] = bar;
                continue;
            }

            unique.Add(bar);
        }

        result.Bars.Clear();
        result.Bars.AddRange(unique);
        result.Warnings.Add(result.Summary);
        return result;
    }

    private static string DetectDelimiter(string filePath)
    {
        using var reader = new StreamReader(filePath, Encoding.UTF8, true);
        var line = reader.ReadLine() ?? string.Empty;
        if (line.Contains('\t')) return "\t";
        if (line.Contains(';') && !line.Contains(',')) return ";";
        return ",";
    }

    private static (int Time, int Open, int High, int Low, int Close, int Volume, int Amount, int Flag) MapHeaders(
        string[] headers)
    {
        int Find(string[] candidates)
        {
            for (var i = 0; i < headers.Length; i++)
            {
                var h = Normalize(headers[i]);
                if (candidates.Any(c => h == c || h.StartsWith(c))) return i;
            }

            return -1;
        }

        return (Find(TimeHeaders), Find(OpenHeaders), Find(HighHeaders), Find(LowHeaders),
            Find(CloseHeaders), Find(VolumeHeaders), Find(AmountHeaders), Find(FlagHeaders));
    }

    private static string Normalize(string header)
    {
        var h = header.Trim().Trim('\uFEFF').ToLowerInvariant();
        h = h.Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
        return h;
    }

    private static double ParseDouble(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var cleaned = text.Trim().Replace(",", string.Empty).Replace("%", string.Empty);
        return double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static DateTime ParseTime(string text, out string detectedFormat)
    {
        detectedFormat = string.Empty;
        var s = text.Trim().Trim('"');

        var formats = new[]
        {
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd", "yyyy/MM/dd HH:mm:ss",
            "yyyy/MM/dd HH:mm", "yyyy/MM/dd", "yyyyMMdd", "yyyyMMddHHmmss", "yyyyMMddHHmm",
            "yyyy-M-d", "yyyy-M-d H:mm", "M/d/yyyy", "dd/MM/yyyy",
        };

        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
        {
            detectedFormat = s.Length <= 10 ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm";
            return exact;
        }

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            detectedFormat = "auto";
            return parsed;
        }

        // 秒/毫秒时间戳
        if (long.TryParse(s, out var epoch))
        {
            detectedFormat = "unix";
            return epoch > 1_000_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch).LocalDateTime
                : DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime;
        }

        throw new FormatException($"无法识别的时间：{text}");
    }

    private static double Round(double value, int decimals) =>
        Math.Round(value, Math.Clamp(decimals, 0, 8), MidpointRounding.AwayFromZero);
}
