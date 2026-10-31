using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>What one plan operation does.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlanAction
{
    Create,
    Update,
    Delete,
    /// <summary>Firewall policy order within one zone pair.</summary>
    Reorder,
}

/// <summary>Safety tiers (spec "Safety tiers"). The plan's highest tier decides what applying takes.</summary>
public static class PlanTiers
{
    public const int Direct = 1;
    public const int Acknowledge = 2;
    public const int SecurityRegression = 3;
    public const int Lockout = 4;

    public static string Label(int tier) => tier switch
    {
        Lockout => "Lockout",
        SecurityRegression => "Regression",
        Acknowledge => "Acknowledge",
        _ => "Direct",
    };
}

/// <summary>One write in a plan.</summary>
public sealed class PlanOperation
{
    /// <summary>Stable key within the plan ("networkconf:create:IoT"), used for acknowledgements and references.</summary>
    public string Key { get; set; } = "";
    public string Collection { get; set; } = "";
    public PlanAction Action { get; set; }

    /// <summary>Live UniFi ID for update and delete; null for create until it runs.</summary>
    public string? ObjectId { get; set; }
    public string ObjectName { get; set; } = "";

    /// <summary>Request body, with references already mapped to live IDs or <c>@ref:key</c> tokens for objects this plan creates. Can hold secrets.</summary>
    public JsonNode? Body { get; set; }

    /// <summary>Top-level fields the body sets (for verify-after-write).</summary>
    public List<string> WriteFields { get; set; } = [];

    /// <summary>Field changes for display, against live. Secrets carry no values.</summary>
    public List<StoredField> Fields { get; set; } = [];

    /// <summary>One-line change text ("Action: Allow All to Block").</summary>
    public string ChangeText { get; set; } = "";

    public int Tier { get; set; } = PlanTiers.Direct;

    /// <summary>Why the item needs acknowledging ("Service tag: WLAN security", "Delete").</summary>
    public List<string> Reasons { get; set; } = [];

    public List<string> Tags { get; set; } = [];

    /// <summary>Restore order group (diff-engine.md); deletes run in reverse after creates and updates.</summary>
    public int Order { get; set; }

    /// <summary>The write can cut a device's (or NO's) management path: ordered last and watched.</summary>
    public bool ManagementPath { get; set; }

    /// <summary>A refusal of this write fails the plan but does not stop the writes after it (a re-address's clients).</summary>
    public bool ContinueOnError { get; set; }

    /// <summary>The write touches the WAN path: watched for internet reachability.</summary>
    public bool WanPath { get; set; }

    /// <summary>Device writes run as each device is adopted (blank-console restore phase 2).</summary>
    public bool DevicePhase { get; set; }

    /// <summary>Objects that still reference a deleted object, shown with the delete.</summary>
    public List<string> Referrers { get; set; } = [];

    /// <summary>For Reorder: the zone pair and the ordered IDs before and after the predefined policies.</summary>
    public string? SourceZoneId { get; set; }
    public string? DestinationZoneId { get; set; }

    /// <summary>For Reorder: the policies it moves or adds, by name with their neighbors. Null on plans built before it.</summary>
    public List<OrderMove>? OrderMoves { get; set; }

    /// <summary>The plan writes this collection only after the gateway is adopted (blank-console restore).</summary>
    public bool NeedsGateway { get; set; }

    /// <summary>Keys of writes that must run first, beyond the creates the body references (a zone's new home for a network it gives up).</summary>
    public List<string> After { get; set; } = [];
}

/// <summary>A lockout pre-flight finding.</summary>
public sealed class LockoutFinding
{
    /// <summary>"certain" (refused by default) or "possible".</summary>
    public string Kind { get; set; } = "possible";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";

    /// <summary>Device names the typed confirmation must name.</summary>
    public List<string> Devices { get; set; } = [];
    public List<string> OperationKeys { get; set; } = [];
}

/// <summary>A displayed step in Write Order.</summary>
public sealed class WriteStep
{
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public List<string> OperationKeys { get; set; } = [];

    /// <summary>The step's objects by collection ("Networks": CC IoT, CC Cameras). Empty on plans built before it.</summary>
    public List<WriteStepGroup> Groups { get; set; } = [];
}

/// <summary>One collection's objects within a Write Order step.</summary>
public sealed class WriteStepGroup
{
    public string Collection { get; set; } = "";
    public List<string> Names { get; set; } = [];
}

/// <summary>The serialized plan (stored encrypted in <c>ConfigPlan.OperationsEncrypted</c>).</summary>
public sealed class PlanDocument
{
    public List<PlanOperation> Operations { get; set; } = [];
    public List<ImpactLine> Regressions { get; set; } = [];
    public List<ImpactLine> Fixes { get; set; } = [];
    public bool ImpactRan { get; set; }
    public List<LockoutFinding> Lockouts { get; set; } = [];

    /// <summary>Problems that stop the plan before any write (ambiguous names, references, refused deletes).</summary>
    public List<string> Errors { get; set; } = [];

    /// <summary>What the plan does not write and why (NO-owned, generated, read-only collections).</summary>
    public List<string> Notes { get; set; } = [];

    /// <summary>Per-site generated values a plan onto another site kept from the target, in plain words.</summary>
    public List<string> KeptValues { get; set; } = [];

    /// <summary>
    /// Fields kept at their live value because the source, from another Network version, references objects it
    /// never captured: collection display name to field labels.
    /// </summary>
    public Dictionary<string, List<string>> KeptReferences { get; set; } = [];

    /// <summary>
    /// Secrets kept at their live value because the source is from another Network version, whose stored form
    /// is unproven on this one: "Network AT&amp;T 5G: X uplink password".
    /// </summary>
    public List<string> KeptSecrets { get; set; } = [];
    public List<WriteStep> WriteOrder { get; set; } = [];

    /// <summary>Plan touches the management path: commit-confirmed watches devices.</summary>
    public bool WatchDevices { get; set; }

    /// <summary>Plan touches the WAN path: commit-confirmed also watches internet reachability.</summary>
    public bool WatchInternet { get; set; }

    /// <summary>Collections the plan covers (for the revert and the verify).</summary>
    public List<string> Collections { get; set; } = [];

    /// <summary>Old MAC to new MAC for Replace Device; device ops already target the new device.</summary>
    public Dictionary<string, string> DeviceMap { get; set; } = [];

    /// <summary>After the window, suggest forgetting these devices (Replace Device).</summary>
    public List<string> ForgetSuggestions { get; set; } = [];

    /// <summary>Re-cabling sheet rows (Replace Device).</summary>
    public List<RecablingRow> Recabling { get; set; } = [];

    /// <summary>Extra Review Plan sections for Replace Device and zone setup.</summary>
    public Dictionary<string, string> Context { get; set; } = [];

    /// <summary>Devices running older firmware than the config came from (Replace Device, Restore).</summary>
    public List<FirmwareAlignRow> FirmwareAlign { get; set; } = [];

    public int HighestTier => Math.Max(
        Operations.Count == 0 ? PlanTiers.Direct : Operations.Max(o => o.Tier),
        Math.Max(Regressions.Count > 0 ? PlanTiers.SecurityRegression : PlanTiers.Direct,
            Lockouts.Count > 0 ? PlanTiers.Lockout : PlanTiers.Direct));
}

/// <summary>One re-cabling sheet row.</summary>
/// <summary>A device to bring up to the firmware its config came from, through Firmware Rollout.</summary>
public sealed class FirmwareAlignRow
{
    public string Mac { get; set; } = "";
    public string Name { get; set; } = "";
    public string Current { get; set; } = "";
    public string Target { get; set; } = "";
}

public sealed class RecablingRow
{
    public string OldPort { get; set; } = "";
    public string NewPort { get; set; } = "";
    public string Connected { get; set; } = "";
    public bool Unresolved { get; set; }
}

/// <summary>Acknowledgements on a plan: items acknowledged, regressions accepted, and the typed confirmation.</summary>
public sealed class PlanAcknowledgements
{
    public HashSet<string> Items { get; set; } = [];
    public HashSet<string> Regressions { get; set; } = [];
    public string? TypedConfirmation { get; set; }

    /// <summary>Operation keys unticked on Review Plan: left out of the apply, with anything that needs them.</summary>
    public HashSet<string> Excluded { get; set; } = [];
}

/// <summary>One operation's result.</summary>
public sealed class OperationResult
{
    public string Key { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string? Message { get; set; }
    public List<string> NotApplied { get; set; } = [];
    public List<string> Normalized { get; set; } = [];

    /// <summary>The request never completed (no HTTP status), as opposed to the console refusing it.</summary>
    public bool NoReply { get; set; }
}

/// <summary>An apply's result.</summary>
public sealed class PlanResult
{
    public List<OperationResult> Operations { get; set; } = [];

    /// <summary>Differences left between the plan and the console after the verify snapshot.</summary>
    public List<string> VerifyDifferences { get; set; } = [];
    public string? WatchOutcome { get; set; }
}
