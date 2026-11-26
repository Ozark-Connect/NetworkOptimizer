namespace NetworkOptimizer.Web.Services.Licensing;

/// <summary>Per-site licensing state, most permissive to most restrictive.</summary>
public enum SiteLicenseState
{
    /// <summary>Operating under the free tier (3 sites or fewer, or a free-tier floor slot).</summary>
    FreeTier,

    /// <summary>Covered by an active license key.</summary>
    Licensed,

    /// <summary>Covering key expired or site uncovered; operational until the grace deadline.</summary>
    Grace,

    /// <summary>Operations and stats collection blocked; historic data stays viewable.</summary>
    Restricted,
}

/// <summary>Why a site is in <see cref="SiteLicenseState.Grace"/> or <see cref="SiteLicenseState.Restricted"/>.</summary>
public enum LicenseRestrictionReason
{
    None,

    /// <summary>The covering term key's paid-through date has passed.</summary>
    KeyExpired,

    /// <summary>The covering key was revoked (perpetual: transaction fraud only).</summary>
    KeyRevoked,

    /// <summary>No key covers this site while licensing is in play.</summary>
    Unassigned,

    /// <summary>More than the free-tier site count with no active licensing.</summary>
    OverFreeLimit,
}

/// <summary>Computed licensing status for one managed site.</summary>
/// <param name="Slug">The site's immutable slug.</param>
/// <param name="State">Current licensing state.</param>
/// <param name="GraceDeadline">When grace ends (UTC); set only in <see cref="SiteLicenseState.Grace"/>.</param>
/// <param name="Reason">Why the site is in grace or restricted.</param>
/// <param name="CoveringKeyOrg">Org of the covering key, for display.</param>
public sealed record SiteLicenseStatus(
    string Slug,
    SiteLicenseState State,
    DateTime? GraceDeadline,
    LicenseRestrictionReason Reason,
    string? CoveringKeyOrg)
{
    /// <summary>True when operations and stats collection may run for this site.</summary>
    public bool IsOperational => State != SiteLicenseState.Restricted;
}

/// <summary>Instance-wide summary of the licensing snapshot.</summary>
/// <param name="States">Per-site statuses keyed by slug.</param>
/// <param name="AnyKeysActive">True when at least one key is active and current.</param>
/// <param name="TotalAllowance">Sum of active, current key allowances (grace keys excluded).</param>
/// <param name="ComputedAt">When this snapshot was computed (UTC).</param>
/// <param name="PersonalUse">True when personal use is declared, adding the free-tier slots beside the keys.</param>
public sealed record LicenseSnapshot(
    IReadOnlyDictionary<string, SiteLicenseStatus> States,
    bool AnyKeysActive,
    int TotalAllowance,
    DateTime ComputedAt,
    bool PersonalUse = false);
