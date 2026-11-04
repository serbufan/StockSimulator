using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using StockSimulator.Models;

namespace StockSimulator.Services;

/// <summary>
/// 工程文件（*.ksproj.json）读写。
/// 保存：A股规则、生成参数、分段参数（含事件）、当前K线、标注、滑点分段、快照。
/// </summary>
public static class ProjectService
{
    public const string FileExtension = ".ksproj.json";
    public const string FileFilter = "仿真行情工程 (*.ksproj.json)|*.ksproj.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*";

    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 中文不转义，便于人工查看
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    public static void Save(string filePath, ProjectDocument document)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        document.SavedAt = DateTime.Now;
        document.FormatVersion = ProjectDocument.CurrentVersion;

        var json = JsonSerializer.Serialize(document, Options);
        File.WriteAllText(filePath, json, new System.Text.UTF8Encoding(true));
    }

    public static ProjectDocument Load(string filePath)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("工程文件不存在。", filePath);
        var json = File.ReadAllText(filePath);
        var doc = JsonSerializer.Deserialize<ProjectDocument>(json, Options);
        if (doc == null) throw new InvalidDataException("工程文件格式错误，无法解析。");

        doc.Rules ??= new MarketRuleSet();
        doc.Settings ??= new KlineGenerationSettings();
        doc.Segments ??= new List<SegmentConfig>();
        doc.Bars ??= new List<Kline>();
        doc.Annotations ??= new List<Annotation>();
        doc.Slippages ??= new List<SlippageSegment>();
        doc.Snapshots ??= new List<SnapshotRecord>();

        foreach (var s in doc.Segments)
            s.Events ??= new System.Collections.ObjectModel.ObservableCollection<MarketEvent>();

        return doc;
    }

    /// <summary>把工程文档转成字符串（用于单元测试/剪贴板）。</summary>
    public static string ToJson(ProjectDocument document) => JsonSerializer.Serialize(document, Options);

    public static ProjectDocument FromJson(string json) =>
        JsonSerializer.Deserialize<ProjectDocument>(json, Options) ?? new ProjectDocument();
}
