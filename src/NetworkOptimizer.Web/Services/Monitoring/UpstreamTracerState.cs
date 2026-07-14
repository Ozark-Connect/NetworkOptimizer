using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Current state of the upstream tracer wizard. The service holds one instance in
/// memory; UI reads it via polling for live progress and the final review screen.
/// </summary>
public class UpstreamTracerState
{
    public TracerStep Step { get; set; } = TracerStep.Idle;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FailureMessage { get; set; }

    // Detection results, populated as the state machine progresses.
    public string? WanInterface { get; set; }
    public string? WanIpAddress { get; set; }
    public PublicAddressClass WanIpClass { get; set; }
    public bool IsCgnat { get; set; }
    public bool IsDoubleNat { get; set; }

    public string? WanNeighborMac { get; set; }
    public string? WanNeighborIp { get; set; }
    public string? WanNeighborOuiVendor { get; set; }
    public AccessTechnology AccessTechnology { get; set; } = AccessTechnology.Unknown;

    public List<AccessHopCandidate> AccessHops { get; set; } = new();
    public List<TransitAsnCandidate> TransitAsns { get; set; } = new();

    /// <summary>
    /// Transit ASNs the scheduled re-discovery confirmed are no longer on our path (absent from
    /// several consecutive runs). Surfaced in the review pre-unchecked; committing with an entry
    /// left unchecked pauses every enabled Transit target in that ASN. In-memory only (like the
    /// rest of the review state): a restart during the review window loses the list, but the
    /// persisted miss counters re-populate it on the next background recheck.
    /// </summary>
    public List<RemovedTransitAsn> RemovedTransitAsns { get; set; } = new();

    /// <summary>
    /// Transit ASNs currently absent from discovery but not yet confirmed removed (consecutive-miss
    /// count below the threshold). Informational only - surfaced read-only in the review so the user
    /// knows we noticed the ASN went off-path and are tracking it toward removal. Populated on every
    /// completed run (manual and scheduled) from the same evaluation, so the count stays accurate.
    /// </summary>
    public List<PendingRemovalTransitAsn> PendingRemovalTransitAsns { get; set; } = new();

    /// <summary>
    /// Identity keys this run discovered that the committed target set doesn't cover yet.
    /// Staged by the shared post-run evaluation for the re-discovery scheduler's review gate.
    /// </summary>
    public List<string> DiscoveryAddedAsns { get; set; } = new();

    /// <summary>
    /// Staged when this run resolved a valid access ISP ASN that differs from every access ASN
    /// committed for the WAN - a candidate provider change. Supersedes per-transit off-path
    /// staging for the run (a switched provider replaces the whole path at once, not N transit
    /// removals). Null when the access ASN is unchanged, unresolved this run, or the user
    /// already declined this same new ASN. In-memory only, like the rest of the review state.
    /// </summary>
    public IspChangeCandidate? IspChange { get; set; }

    // Per-CDN trace summaries for the live progress UI.
    public List<TraceSummary> Traces { get; set; } = new();

    public string? CurrentActivity { get; set; }
}

public enum TracerStep
{
    Idle,
    DetectingPublicIp,
    DiscoveringL2Neighbor,
    TracingAccessIsp,
    TracingTransitAsns,
    VerifyingReachability,
    ReviewingResults,
    Done,
    Failed
}

/// <summary>
/// An access-ISP hop candidate the tracer found. User can rename / disable in the
/// review step before committing. Becomes a MonitoringTarget row on commit.
/// </summary>
public class AccessHopCandidate
{
    public required string TargetId { get; set; }
    public required string Label { get; set; }        // auto-generated, user-overridable
    public required string Address { get; set; }
    public string? PtrHostname { get; set; }
    public int? AsnNumber { get; set; }
    public string? AsnName { get; set; }
    public UpstreamRole Role { get; set; }
    public int HopNumber { get; set; }
    public Core.Enums.ProbeMode RespondedTo { get; set; }
    public DiscoveryMethod Method { get; set; } = DiscoveryMethod.DirectRouter;
    public bool Enabled { get; set; } = true;
    public bool Unreachable { get; set; }
    public double? VerifiedRttMs { get; set; }
}

/// <summary>
/// A transit-ASN cloud candidate. One per distinct ASN seen across all CDN traces,
/// with the chosen target hop + tier already resolved by the fallback ladder.
/// </summary>
public class TransitAsnCandidate
{
    public int AsnNumber { get; set; }
    public required string AsnName { get; set; }
    public string? Label { get; set; }
    public DiscoveryMethod Method { get; set; }
    public string? TargetId { get; set; }              // null for Unresolved tier
    public string? HopAddress { get; set; }
    public string? HopHostname { get; set; }
    public Core.Enums.ProbeMode? RespondedTo { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Unreachable { get; set; }
    public double? VerifiedRttMs { get; set; }
    /// <summary>For PathProxy tier: the CDN endpoint we monitor as a proxy for this ASN.</summary>
    public string? PathProxyTarget { get; set; }

    /// <summary>
    /// Trace hop number of this candidate, used by post-verification auto-selection
    /// to order an ASN's hops and split them into RTT clumps (an ASN's run typically
    /// spans its ingress POP and, several ms later, a distant egress POP).
    /// </summary>
    public int HopNumber { get; set; }

    /// <summary>
    /// True when this candidate matched an existing monitoring target during
    /// reconcile and inherited its enabled state. Post-verification auto-selection
    /// skips ASNs with preserved members so rediscovery never overrides the user's
    /// stored choices.
    /// </summary>
    public bool PreservedFromExisting { get; set; }
}

/// <summary>
/// A transit ASN that auto-discovery confirmed is no longer on our path (absent from several
/// consecutive re-discovery runs). Surfaced in the review pre-unchecked; committing with it
/// left unchecked pauses every enabled Transit target in the ASN - auto-discovered and hand-
/// added alike - since if the ISP no longer routes through the ASN, all its targets are false.
/// </summary>
public class RemovedTransitAsn
{
    public int AsnNumber { get; set; }
    public required string AsnName { get; set; }
    /// <summary>Enabled Transit targets in this ASN that would be paused (auto + manual).</summary>
    public int TargetCount { get; set; }
    /// <summary>How many of those are hand-added (UserProvided), for the review note.</summary>
    public int ManualCount { get; set; }
    /// <summary>Checkbox state. Default false = pause on commit; user re-checks to keep monitoring.</summary>
    public bool Keep { get; set; }
}

/// <summary>
/// A transit ASN that has gone absent from discovery but hasn't yet hit the consecutive-miss
/// threshold for removal. Surfaced read-only in the review ("we're tracking this; N more runs
/// without it and we'll help you remove its targets"). No checkbox, no commit action - purely a
/// heads-up so the off-path detection isn't invisible until it suddenly confirms.
/// </summary>
public class PendingRemovalTransitAsn
{
    public int AsnNumber { get; set; }
    public required string AsnName { get; set; }
    /// <summary>Consecutive runs this ASN has been absent so far.</summary>
    public int MissCount { get; set; }
    /// <summary>Runs still needed without it present before it's confirmed for removal.</summary>
    public int RunsRemaining { get; set; }
    /// <summary>Enabled Transit targets that would be paused once confirmed (auto + manual).</summary>
    public int TargetCount { get; set; }
    /// <summary>How many of those are hand-added (UserProvided).</summary>
    public int ManualCount { get; set; }
}

/// <summary>
/// A detected access-ISP change awaiting the user's answer in the review. Confirmed stays null
/// until they pick; the commit button is gated on a decision. Confirming pauses every enabled
/// upstream target for the connection - access, transit, and path-proxy tiers, auto-discovered
/// and hand-added alike (manual targets pinned to a different WAN survive) - wipes the WAN's
/// off-path miss counters, and lets this run's candidates repopulate as the new baseline.
/// Declining leaves targets untouched and records the new ASN so the same change doesn't
/// re-prompt every run. Paused targets are never deleted.
/// </summary>
public class IspChangeCandidate
{
    public int OldAsnNumber { get; set; }
    public required string OldAsnName { get; set; }
    public int NewAsnNumber { get; set; }
    public required string NewAsnName { get; set; }
    /// <summary>Enabled upstream targets across all tiers a confirm would pause.</summary>
    public int TargetCount { get; set; }
    /// <summary>How many of those are hand-added (UserProvided).</summary>
    public int ManualCount { get; set; }
    /// <summary>Null until the user decides; true = start fresh, false = keep monitoring.</summary>
    public bool? Confirmed { get; set; }
}

/// <summary>One CDN traceroute summary for the live progress UI.</summary>
public class TraceSummary
{
    public required string CdnLabel { get; set; }    // "Cloudflare" / "Google" / ...
    public required string CdnEndpoint { get; set; }
    public required Core.Enums.ProbeMode Mode { get; set; }
    public int HopsRecorded { get; set; }
    public int HopsResponding { get; set; }
    public bool Reached { get; set; }
    public string? Error { get; set; }
}
