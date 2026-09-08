using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkOptimizer.Web.Services.SqmLearning;

/// <summary>
/// The stored configuration of an Adaptive SQM learning schedule (the task's TargetConfig JSON).
/// The task's TargetId carries the data-path interface; everything else that identifies the WAN
/// and bounds the run lives here.
/// </summary>
public sealed class SqmLearningTaskConfig
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Default learning window.</summary>
    public const int DefaultDays = 7;

    /// <summary>Default seconds per direction handed to the binary for one sample.</summary>
    public const int DefaultDurationSeconds = 4;

    public int WanNumber { get; set; }
    public string? WanName { get; set; }

    /// <summary>UniFi WAN network group ("WAN", "WAN2"), the key the monitored counter interface resolves from.</summary>
    public string? WanGroup { get; set; }

    /// <summary>NetworkOptimizer.Sqm.Models.ConnectionType at start.</summary>
    public int ConnectionType { get; set; }

    /// <summary>When the run ends (UTC); the executor completes the profile and disables the task at this point.</summary>
    public DateTime EndsAt { get; set; }

    public int DurationSeconds { get; set; } = DefaultDurationSeconds;

    /// <summary>Nominal speeds at start, so the shaper lift can be sized even before the WAN is saved or deployed.</summary>
    public int NominalDownloadMbps { get; set; }
    public int NominalUploadMbps { get; set; }
    public int? LinkSpeedOverrideMbps { get; set; }
    public bool RateProportionalDownloadBurst { get; set; }

    /// <summary>The sizing facts as the WAN configuration shape the lift is built from.</summary>
    public NetworkOptimizer.Storage.Models.SqmWanConfiguration ToWanConfiguration(string? interfaceName) => new()
    {
        WanNumber = WanNumber,
        Interface = interfaceName ?? "",
        Name = WanName ?? "",
        ConnectionType = ConnectionType,
        NominalDownloadMbps = NominalDownloadMbps,
        NominalUploadMbps = NominalUploadMbps,
        LinkSpeedOverrideMbps = LinkSpeedOverrideMbps,
        RateProportionalDownloadBurst = RateProportionalDownloadBurst,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static SqmLearningTaskConfig? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var cfg = JsonSerializer.Deserialize<SqmLearningTaskConfig>(json, Options);
            if (cfg == null || cfg.WanNumber <= 0) return null;
            if (cfg.DurationSeconds <= 0) cfg.DurationSeconds = DefaultDurationSeconds;
            if (cfg.EndsAt.Kind == DateTimeKind.Unspecified)
                cfg.EndsAt = DateTime.SpecifyKind(cfg.EndsAt, DateTimeKind.Utc);
            return cfg;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
