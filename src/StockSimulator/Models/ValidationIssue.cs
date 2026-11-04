namespace StockSimulator.Models;

public enum IssueSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>约束校验发现的一条问题。</summary>
public sealed class ValidationIssue
{
    public ValidationIssue(IssueSeverity severity, int barIndex, DateTime time, string code, string message)
    {
        Severity = severity;
        BarIndex = barIndex;
        Time = time;
        Code = code;
        Message = message;
    }

    public IssueSeverity Severity { get; }
    public int BarIndex { get; }
    public DateTime Time { get; }
    public string Code { get; }
    public string Message { get; }

    public string SeverityDisplay => Severity switch
    {
        IssueSeverity.Error => "错误",
        IssueSeverity.Warning => "警告",
        _ => "提示",
    };

    public string TimeDisplay => Time == default ? "-" : Time.ToString("yyyy-MM-dd HH:mm");

    public override string ToString() => $"[{SeverityDisplay}] #{BarIndex} {TimeDisplay} {Message}";
}

/// <summary>校验报告。</summary>
public sealed class ValidationReport
{
    public List<ValidationIssue> Issues { get; } = new();

    public int ErrorCount => Issues.Count(i => i.Severity == IssueSeverity.Error);

    public int WarningCount => Issues.Count(i => i.Severity == IssueSeverity.Warning);

    public bool IsValid => ErrorCount == 0;

    public string Summary => Issues.Count == 0
        ? "校验通过，未发现问题。"
        : $"共 {Issues.Count} 条：错误 {ErrorCount}，警告 {WarningCount}。";

    public void Add(IssueSeverity s, int index, DateTime time, string code, string message)
        => Issues.Add(new ValidationIssue(s, index, time, code, message));
}
