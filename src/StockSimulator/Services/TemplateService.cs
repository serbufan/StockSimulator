using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using StockSimulator.Models;

namespace StockSimulator.Services;

/// <summary>行情模板：一组可复用的分段参数（可含事件）。</summary>
public sealed class MarketTemplate
{
    public string Name { get; set; } = "模板";
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<SegmentConfig> Segments { get; set; } = new();
}

/// <summary>
/// 行情模板管理：把常用分段组合另存为模板（存于 %AppData%/StockSimulator/Templates），
/// 下次一键套用。内置模板来自 <see cref="SegmentPresets"/>。
/// </summary>
public sealed class TemplateService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Directory { get; }

    public TemplateService(string? directory = null)
    {
        Directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StockSimulator", "Templates");
        System.IO.Directory.CreateDirectory(Directory);
    }

    public List<string> ListNames()
    {
        var names = new List<string> { "内置：默认五段行情" };
        names.AddRange(System.IO.Directory
            .EnumerateFiles(Directory, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f)!)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n));
        return names;
    }

    public void Save(string name, IEnumerable<SegmentConfig> segments, string description = "")
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("模板名称不能为空。", nameof(name));
        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        var template = new MarketTemplate
        {
            Name = name,
            Description = description,
            Segments = segments.Select(s => s.Clone()).ToList(),
        };
        var json = JsonSerializer.Serialize(template, Options);
        File.WriteAllText(Path.Combine(Directory, safe + ".json"), json, new System.Text.UTF8Encoding(true));
    }

    public List<SegmentConfig> Load(string name)
    {
        if (name.StartsWith("内置"))
            return SegmentPresets.DefaultSegments();

        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(Directory, safe + ".json");
        if (!File.Exists(path)) throw new FileNotFoundException("模板不存在。", path);

        var template = JsonSerializer.Deserialize<MarketTemplate>(File.ReadAllText(path), Options);
        var segments = template?.Segments ?? new List<SegmentConfig>();
        foreach (var s in segments) s.Events ??= new System.Collections.ObjectModel.ObservableCollection<MarketEvent>();
        return segments;
    }

    public void Delete(string name)
    {
        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(Directory, safe + ".json");
        if (File.Exists(path)) File.Delete(path);
    }
}
