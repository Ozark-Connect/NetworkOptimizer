namespace NetworkOptimizer.Threats.Models;

/// <summary>
/// A source Network Optimizer decided to block. In Dry Run it is recorded but never sent to a
/// target (<see cref="Enforced"/> false), so the list shows what Enforce would have done.
/// </summary>
public class ThreatBlock
{
    public int Id { get; set; }

    /// <summary>The blocked source address.</summary>
    public string SourceIp { get; set; } = string.Empty;

    public BlockTrigger Trigger { get; set; }

    /// <summary>Why, in one line, e.g. "7 WAF events in 10 minutes".</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Earlier blocks of this source within the strike window; sets the ban length.</summary>
    public int Strikes { get; set; }

    /// <summary>True when it was sent to the targets (Enforce); false for a Dry Run record.</summary>
    public bool Enforced { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>When it was released early by a user, or null.</summary>
    public DateTime? ReleasedAt { get; set; }

    /// <summary>Who released it early (user name), or null.</summary>
    public string? ReleasedBy { get; set; }

    /// <summary>Active at <paramref name="now"/>: not released and not yet expired.</summary>
    public bool IsActive(DateTime now) => ReleasedAt == null && ExpiresAt > now;

    /// <summary>The reason a manual block shows outside its own site, where the user who made it stays private.</summary>
    public const string ManualReasonElsewhere = "Blocked manually";

    /// <summary>
    /// An unsaved copy for showing outside its own site: a manual block's reason names the user who
    /// made it, so it reads <see cref="ManualReasonElsewhere"/>. Automatic reasons name no one.
    /// </summary>
    public ThreatBlock ForOtherSites() => Trigger != BlockTrigger.Manual ? this : new ThreatBlock
    {
        Id = Id,
        SourceIp = SourceIp,
        Trigger = Trigger,
        Reason = ManualReasonElsewhere,
        Strikes = Strikes,
        Enforced = Enforced,
        CreatedAt = CreatedAt,
        ExpiresAt = ExpiresAt,
        ReleasedAt = ReleasedAt,
        ReleasedBy = ReleasedBy,
    };
}

/// <summary>What made Network Optimizer block a source.</summary>
public enum BlockTrigger
{
    /// <summary>Repeated WAF events from one source.</summary>
    WafRepeat = 0,

    /// <summary>A scan sweep, brute force, or exploit campaign pattern.</summary>
    Pattern = 1,

    /// <summary>Blocked by hand.</summary>
    Manual = 2
}
