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
