namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// One category of kernel messages extracted from dmesg output.
/// </summary>
public class DmesgCategory
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required DmesgSeverity Severity { get; init; }

    /// <summary>Summary line shown in the overview (e.g. "1,302 SFE connection removals - normal for multi-WAN").</summary>
    public required string Summary { get; init; }

    /// <summary>Individual matching lines, in dmesg order. Empty for noise categories that are only counted.</summary>
    public List<string> Lines { get; init; } = new();

    /// <summary>Total count of matching lines, which may exceed <see cref="Lines"/> when lines are suppressed.</summary>
    public int Count { get; init; }
}

public enum DmesgSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// Structured report from parsing a gateway's dmesg output.
/// </summary>
public class DmesgDiagnosticsReport
{
    public DateTime CollectedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Categories with findings, ordered by severity (errors first).</summary>
    public List<DmesgCategory> Categories { get; init; } = new();

    /// <summary>Total lines in the dmesg output.</summary>
    public int TotalLines { get; init; }

    /// <summary>Approximate uptime parsed from the last timestamp in dmesg, if available.</summary>
    public TimeSpan? ApproximateUptime { get; init; }

    /// <summary>Full raw dmesg output for the expandable raw section.</summary>
    public string RawOutput { get; init; } = string.Empty;

    /// <summary>Set when the SSH command itself failed.</summary>
    public string? RunError { get; init; }

    /// <summary>True when the ring buffer appears dominated by noise (SFE, bridge cycling), pushing out boot events.</summary>
    public bool RingBufferDominatedByNoise { get; init; }
}
