using System.Text.Json.Serialization;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Result of POST /api/cloud/backup: an overall flag plus per-application and per-service
/// outcomes. The same shape comes back from Cloud Gateway and standalone consoles; only the
/// installed component set differs.
/// </summary>
[VendorSpecific("UniFi", "console-level POST /api/cloud/backup response")]
public class UniFiConsoleBackupResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>Per installed application (e.g. "network", "protect").</summary>
    [JsonPropertyName("controllers")]
    public Dictionary<string, UniFiConsoleBackupComponent> Controllers { get; set; } = new();

    /// <summary>Per console service (e.g. "users").</summary>
    [JsonPropertyName("services")]
    public Dictionary<string, UniFiConsoleBackupComponent> Services { get; set; } = new();
}

/// <summary>One component's backup outcome.</summary>
public class UniFiConsoleBackupComponent
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }
}
