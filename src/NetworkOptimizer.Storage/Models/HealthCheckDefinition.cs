using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// A user-defined health check on one UniFi device: a command run over SSH on a cadence, a rule
/// for reading one value out of its output, a condition on that value, and what to do when the
/// condition holds. Templates (JSON files shipped with the app) pre-fill one of these; the row
/// is the user's copy and is what the runner executes.
/// </summary>
public class HealthCheckDefinition
{
    [Key]
    public int Id { get; set; }

    /// <summary>Device MAC address this check runs on.</summary>
    [Required, MaxLength(50)]
    public string DeviceMac { get; set; } = string.Empty;

    /// <summary>Display name, also the chart title on Device Stats.</summary>
    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Influx field the value is stored under on <c>device_health</c>, prefixed <c>hc_</c> by the
    /// writer. Letters, digits and underscores only; unique per device.
    /// </summary>
    [Required, MaxLength(60)]
    public string FieldName { get; set; } = string.Empty;

    /// <summary>Template id this check was created from, or null for a custom check.</summary>
    [MaxLength(80)]
    public string? TemplateId { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between runs. Floor 30.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>Shell command run on the device as the SSH user.</summary>
    [Required, MaxLength(4000)]
    public string Command { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>How the value is read out of the command's output.</summary>
    public HealthCheckParser Parser { get; set; } = HealthCheckParser.FirstNumber;

    /// <summary>Parser argument: the regex for <see cref="HealthCheckParser.RegexGroup"/> and <see cref="HealthCheckParser.MatchingLineCount"/>.</summary>
    [MaxLength(500)]
    public string? ParserArg { get; set; }

    public HealthCheckOperator Operator { get; set; } = HealthCheckOperator.GreaterOrEqual;

    public double Threshold { get; set; }

    /// <summary>Consecutive failing samples before the check trips. Floor 1.</summary>
    public int ConsecutiveSamples { get; set; } = 2;

    /// <summary>
    /// Output that means the check does not apply on this device (an empty output when null).
    /// A not-applicable sample is silent: nothing written, nothing counted.
    /// </summary>
    [MaxLength(200)]
    public string? NotApplicablePattern { get; set; }

    /// <summary>Whether tripping raises an alert.</summary>
    public bool AlertEnabled { get; set; } = true;

    /// <summary>Alert severity as <c>AlertSeverity</c>; stored as its integer value.</summary>
    public int AlertSeverity { get; set; } = 2;

    /// <summary>What to do on the device when the check trips.</summary>
    public HealthCheckRemedy Remedy { get; set; } = HealthCheckRemedy.None;

    /// <summary>Service name for <see cref="HealthCheckRemedy.RestartService"/>, process name for <see cref="HealthCheckRemedy.KillProcess"/>.</summary>
    [MaxLength(64)]
    public string? RemedyArg { get; set; }

    /// <summary>Seconds after a remedy before the same check may run it again.</summary>
    public int RemedyCooldownSeconds { get; set; } = 1800;

    /// <summary>Most remedies in one UTC day. 0 means no cap.</summary>
    public int RemedyMaxPerDay { get; set; } = 4;

    [MaxLength(500)]
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>How a health check reads its value out of the command output.</summary>
public enum HealthCheckParser
{
    /// <summary>The first number on the last non-empty line of output.</summary>
    FirstNumber = 0,

    /// <summary>The first capture group of the regex in ParserArg, on the last line that matches.</summary>
    RegexGroup = 1,

    /// <summary>The command's exit code.</summary>
    ExitCode = 2,

    /// <summary>How many output lines match the regex in ParserArg.</summary>
    MatchingLineCount = 3,
}

/// <summary>Comparison between the parsed value and the threshold.</summary>
public enum HealthCheckOperator
{
    GreaterOrEqual = 0,
    LessOrEqual = 1,
    Equal = 2,
    NotEqual = 3,
}

/// <summary>Remedial action a health check runs on the device when it trips.</summary>
public enum HealthCheckRemedy
{
    None = 0,

    /// <summary><c>systemctl restart RemedyArg</c>. Gateways only: access points have no systemd.</summary>
    RestartService = 1,

    /// <summary><c>pkill -x RemedyArg</c>.</summary>
    KillProcess = 2,

    /// <summary><c>reboot</c>.</summary>
    RebootDevice = 3,
}
