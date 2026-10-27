using System.Text.Json;
using System.Text.Json.Nodes;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>A device on either side of Replace Device, or pending adoption.</summary>
public sealed class MigrateDeviceView
{
    public string Mac { get; init; } = "";
    public string Name { get; init; } = "";
    public string Model { get; init; } = "";
    public string Type { get; init; } = "";
    public string? Version { get; init; }
    public bool Online { get; init; }
    public bool Pending { get; init; }
    public string? Ip { get; init; }
    public bool InSnapshot { get; init; }
}

/// <summary>Replace Device's two pickers: devices in the latest snapshot, and devices live now.</summary>
public sealed class ReplaceDevicesView
{
    public List<MigrateDeviceView> Old { get; init; } = [];
    public List<MigrateDeviceView> New { get; init; } = [];
    public DateTime? SnapshotAt { get; init; }
}

/// <summary>A snapshot offered as a restore source.</summary>
public sealed class RestoreSnapshotView
{
    public int Id { get; init; }
    public DateTime TakenAt { get; init; }
    public string Label { get; init; } = "";
    public string? NetworkAppVersion { get; init; }
}

public sealed partial class ChangeControlService
{
    private static bool IsGatewayType(string? type) => type is "ugw" or "udm" or "uxg";

    private static string? MetaStr(JsonObject device, string key) =>
        (device[ConfigCollections.DeviceMetaField] as JsonObject)?[key] is JsonValue v ? v.ToString() : null;

    private static MigrateDeviceView DeviceView(JsonObject d, bool inSnapshot)
    {
        var state = MetaStr(d, "state");
        return new MigrateDeviceView
        {
            Mac = d["mac"]?.ToString().ToLowerInvariant() ?? "",
            Name = ConfigState.DisplayName(ConfigCollections.Get("device"), d),
            Model = UniFiProductDatabase.GetBestProductName(d["model"]?.ToString(), MetaStr(d, "shortname")),
            Type = d["type"]?.ToString() ?? MetaStr(d, "type") ?? "",
            Version = d["version"]?.ToString(),
            Online = state == "1",
            Pending = state == "2" || d["adopted"]?.GetValueKind() == JsonValueKind.False,
            Ip = MetaStr(d, "ip"),
            InSnapshot = inSnapshot,
        };
    }

    private async Task<(ConfigState? State, ConfigSnapshot? Snapshot)> LatestSnapshotStateAsync(CancellationToken ct)
    {
        var latest = await Repo.UseAsync((r, c) => r.GetLatestSnapshotAsync(c), ct);
        return latest == null ? (null, null) : (await Engine.LoadSnapshotStateAsync(latest.Id, ct), latest);
    }

    public async Task<ReplaceDevicesView> GetReplaceDevicesAsync(CancellationToken cancellationToken = default)
    {
        var (snap, meta) = await LatestSnapshotStateAsync(cancellationToken);
        var live = await Engine.ReadLiveAsync(cancellationToken);
        var snapMacs = snap?.Get("device").Select(d => d["mac"]?.ToString().ToLowerInvariant()).OfType<string>().ToHashSet() ?? [];
        return new ReplaceDevicesView
        {
            Old = snap?.Get("device").Select(d => DeviceView(d, true)).OrderBy(d => d.Name).ToList() ?? [],
            New = live.Get("device").Select(d => DeviceView(d, snapMacs.Contains(d["mac"]?.ToString().ToLowerInvariant() ?? "")))
                .Where(d => !d.Pending).OrderBy(d => d.InSnapshot).ThenBy(d => d.Name).ToList(),
            SnapshotAt = meta?.TakenAt,
        };
    }

    private async Task<(ReplacementProposal Proposal, JsonObject Old, JsonObject New, ConfigState Live)> ProposeAsync(
        string oldMac, string newMac, IReadOnlyDictionary<string, string>? choices, IReadOnlyCollection<string>? acks, CancellationToken ct)
    {
        oldMac = oldMac.ToLowerInvariant();
        newMac = newMac.ToLowerInvariant();
        if (oldMac == newMac) throw new InvalidOperationException("Choose two different devices.");
        var (snap, _) = await LatestSnapshotStateAsync(ct);
        var live = await Engine.ReadLiveAsync(ct);
        var old = snap?.Get("device").FirstOrDefault(d => d["mac"]?.ToString().ToLowerInvariant() == oldMac)
            ?? live.Get("device").FirstOrDefault(d => d["mac"]?.ToString().ToLowerInvariant() == oldMac)
            ?? throw new InvalidOperationException("The old device is not in the latest snapshot.");
        var @new = live.Get("device").FirstOrDefault(d => d["mac"]?.ToString().ToLowerInvariant() == newMac)
            ?? throw new InvalidOperationException("The new device is not adopted on this site.");
        if (IsGatewayType(old["type"]?.ToString()) != IsGatewayType(@new["type"]?.ToString())
            || (old["type"]?.ToString() == "uap") != (@new["type"]?.ToString() == "uap"))
            throw new InvalidOperationException("Replace a device with one of the same kind: switch for switch, access point for access point, gateway for gateway.");
        var proposal = DeviceReplacement.Propose(old, @new, live, choices, acks?.ToHashSet());
        return (proposal, old, @new, live);
    }

    public async Task<ReplacementProposal> GetReplacementAsync(string oldMac, string newMac, IReadOnlyDictionary<string, string>? choices,
        IReadOnlyCollection<string>? acks, CancellationToken cancellationToken = default) =>
        (await ProposeAsync(oldMac, newMac, choices, acks, cancellationToken)).Proposal;

    public async Task<int> BuildReplacePlanAsync(string oldMac, string newMac, IReadOnlyDictionary<string, string>? choices,
        IReadOnlyCollection<string>? acks, CancellationToken cancellationToken = default)
    {
        var (proposal, old, @new, live) = await ProposeAsync(oldMac, newMac, choices, acks, cancellationToken);
        if (!proposal.Ready)
            throw new InvalidOperationException("Every configured port needs a target or an explicit drop, and every drop needs acknowledging.");

        var adapted = DeviceReplacement.Adapt(proposal, old, @new);
        var desired = live.Clone();
        var devices = desired.Collections["device"] = desired.Get("device")
            .Where(d => d["mac"]?.ToString().ToLowerInvariant() is { } m && m != proposal.NewMac && m != proposal.OldMac)
            .Select(d => (JsonObject)d.DeepClone()).ToList();
        devices.Add(adapted);

        // Everything else that names the old device (mesh uplinks, a client's fixed AP) moves to the new one.
        foreach (var key in new[] { "device", "user" })
            foreach (var o in desired.Get(key).Where(o => !ReferenceEquals(o, adapted)))
                RewriteMac(o, proposal.OldMac, proposal.NewMac);

        var options = new PlanOptions
        {
            Collections = new HashSet<string> { "device", "user" },
            DeleteMissing = false,
            DeviceMap = new Dictionary<string, string> { [proposal.OldMac] = proposal.NewMac },
        };
        var plan = await Plans.CreateAsync(ConfigPlanKind.ReplaceDevice, $"Replace {proposal.OldName} with {proposal.NewModel}",
            $"{proposal.OldName} ({proposal.OldModel})", null, null, desired, options, Actor, cancellationToken,
            doc =>
            {
                doc.Context["old_mac"] = proposal.OldMac;
                doc.Context["new_mac"] = proposal.NewMac;
                doc.Context["old_name"] = proposal.OldName;
                doc.Recabling = proposal.Recabling;
                doc.ForgetSuggestions = [proposal.OldMac];
                foreach (var w in proposal.Warnings) doc.Notes.Add(w);
                foreach (var r in proposal.Ports.Where(p => p.Target is { Count: 0 }))
                    doc.Notes.Add($"{r.OldLabel} ({r.Config}): config dropped.");
                if (proposal is { OldVersion: { } o, NewVersion: { } n } && FirmwareVersionFormat.IsNewer(o, n))
                    doc.FirmwareAlign.Add(new FirmwareAlignRow { Mac = proposal.NewMac, Name = proposal.NewModel, Current = n, Target = o });
            });
        return plan.Id;
    }

    /// <summary>Adopted devices running older firmware than the snapshot recorded for them.</summary>
    internal static List<FirmwareAlignRow> FirmwareBehind(ConfigState snapshot, ConfigState live) =>
        snapshot.Get("device")
            .Select(s => (Snap: s, Live: live.Get("device").FirstOrDefault(l => string.Equals(l["mac"]?.ToString(), s["mac"]?.ToString(), StringComparison.OrdinalIgnoreCase))))
            .Where(p => p.Live != null && p.Snap["version"]?.ToString() is { Length: > 0 } want && p.Live["version"]?.ToString() is { Length: > 0 } have
                && FirmwareVersionFormat.IsNewer(want, have))
            .Select(p => new FirmwareAlignRow
            {
                Mac = p.Live!["mac"]!.ToString().ToLowerInvariant(),
                Name = p.Live["name"]?.ToString() is { Length: > 0 } nm ? nm
                    : UniFiProductDatabase.GetBestProductName(p.Live["model"]?.ToString(), MetaStr(p.Live, "shortname")),
                Current = p.Live["version"]!.ToString(),
                Target = p.Snap["version"]!.ToString(),
            })
            .ToList();

    private static void RewriteMac(JsonNode? node, string oldMac, string newMac)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    if (key is "mac" or "_id" or ConfigCollections.DeviceMetaField) continue;
                    if (o[key] is JsonValue v && v.ToString().Equals(oldMac, StringComparison.OrdinalIgnoreCase)) o[key] = newMac;
                    else RewriteMac(o[key], oldMac, newMac);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.ToString().Equals(oldMac, StringComparison.OrdinalIgnoreCase)) a[i] = newMac;
                    else RewriteMac(a[i], oldMac, newMac);
                }
                break;
        }
    }

    public async Task<byte[]> GetRecablingPdfAsync(string oldMac, string newMac, IReadOnlyDictionary<string, string>? choices,
        IReadOnlyCollection<string>? acks, CancellationToken cancellationToken = default)
    {
        var (p, _, _, _) = await ProposeAsync(oldMac, newMac, choices, acks, cancellationToken);
        return new RecablingPdfGenerator().GenerateBytes(p, await SiteNameAsync(cancellationToken));
    }

    public async Task<List<MigrateDeviceView>> GetPendingAdoptionsAsync(string? sourceSiteSlug, int? snapshotId, CancellationToken cancellationToken = default)
    {
        var live = await Engine.ReadLiveAsync(cancellationToken);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (latest, _) = await LatestSnapshotStateAsync(cancellationToken);
        foreach (var d in latest?.Get("device") ?? []) if (d["mac"]?.ToString() is { } m) known.Add(m);
        if (sourceSiteSlug != null && snapshotId != null && await LoadVisibleSnapshotAsync(sourceSiteSlug, snapshotId.Value, cancellationToken) is { } source)
            foreach (var d in source.State.Get("device")) if (d["mac"]?.ToString() is { } m) known.Add(m);
        return live.Get("device").Select(d => DeviceView(d, known.Contains(d["mac"]?.ToString() ?? "")))
            .Where(d => d.Pending).OrderByDescending(d => d.InSnapshot).ThenBy(d => d.Mac).ToList();
    }

    public async Task<string?> AdoptAsync(IReadOnlyList<string> macs, CancellationToken cancellationToken = default)
    {
        if (macs.Count == 0) return null;
        var body = JsonSerializer.Serialize(new { cmd = "adopt", macs = macs.Select(m => m.ToLowerInvariant()).ToArray(), client = "web" });
        var response = await Engine.Api.SendAsync(UniFiApiScope.SiteV1, HttpMethod.Post, "cmd/devmgr", body, cancellationToken);
        Engine.PollSoon();
        return response.IsSuccess ? null : $"UniFi refused the adopt: {response.ErrorCode ?? response.Status.ToString()}";
    }

    public async Task<string?> ForgetDeviceAsync(string mac, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(new { cmd = "delete-device", macs = new[] { mac.ToLowerInvariant() } });
        var response = await Engine.Api.SendAsync(UniFiApiScope.SiteV1, HttpMethod.Post, "cmd/sitemgr", body, cancellationToken);
        Engine.PollSoon();
        return response.IsSuccess ? null : $"UniFi refused the forget: {response.ErrorCode ?? response.Status.ToString()}";
    }

    // ---------- Restore ----------

    private async Task<bool> CanSeeSiteAsync(string slug)
    {
        var allowed = await _access.AuthorizedSlugsAsync();
        return allowed == null || allowed.Contains(slug);
    }

    private async Task<(ConfigState State, ConfigSnapshot Snapshot)?> LoadVisibleSnapshotAsync(string slug, int snapshotId, CancellationToken ct)
    {
        if (!await CanSeeSiteAsync(slug)) return null;
        var engine = _registry.GetFor(slug);
        var snap = await engine.Repositories.UseAsync((r, c) => r.GetSnapshotAsync(snapshotId, c), ct);
        if (snap == null) return null;
        var state = await engine.LoadSnapshotStateAsync(snapshotId, ct);
        return state == null ? null : (state, snap);
    }

    public async Task<List<RestoreSnapshotView>> GetRestoreSnapshotsAsync(string sourceSiteSlug, CancellationToken cancellationToken = default)
    {
        if (!await CanSeeSiteAsync(sourceSiteSlug)) return [];
        var snaps = await _registry.GetFor(sourceSiteSlug).Repositories.UseAsync((r, c) => r.GetSnapshotsAsync(limit: 200, cancellationToken: c), cancellationToken);
        return snaps.Select(s => new RestoreSnapshotView
        {
            Id = s.Id, TakenAt = s.TakenAt, NetworkAppVersion = s.NetworkAppVersion,
            Label = $"{Local(s.TakenAt):MMM d, h:mm tt}, {(s.IsBaseline ? "Baseline" : s.Pinned ? s.PinReason ?? "Pinned" : TriggerLabel(s).Text)}",
        }).ToList();
    }

    public async Task<int> BuildRestoreAsync(string sourceSiteSlug, int snapshotId, bool deleteMissing, bool devicesOnly, CancellationToken cancellationToken = default)
    {
        var source = await LoadVisibleSnapshotAsync(sourceSiteSlug, snapshotId, cancellationToken)
            ?? throw new InvalidOperationException("That snapshot is not available to you.");
        var (state, snap) = source;
        var live = await Engine.ReadLiveAsync(cancellationToken);
        var sameSite = string.Equals(sourceSiteSlug, _site.Slug, StringComparison.OrdinalIgnoreCase);
        var hasGateway = live.Get("device").Any(d => IsGatewayType(d["type"]?.ToString()) && d["adopted"]?.GetValueKind() != JsonValueKind.False);
        // Phase 2 of a blank-console restore: only device config, for devices adopted since phase 1.
        var options = new PlanOptions
        {
            SameSite = sameSite, DeleteMissing = deleteMissing && !devicesOnly, TargetHasGateway = hasGateway,
            Collections = devicesOnly ? new HashSet<string> { "device" } : null,
        };
        // A legacy-firewall snapshot onto a zone-based console: translate the rules, and audit against the legacy config.
        var translation = !devicesOnly && LegacyFirewallTranslator.Applies(state, live)
            ? LegacyFirewallTranslator.Translate(state, live, options.DeleteMissing)
            : null;
        var label = Local(snap.TakenAt).ToString("MMM d, h:mm tt");
        var sourceName = sameSite ? await SiteNameAsync(cancellationToken) : sourceSiteSlug;
        var plan = await Plans.CreateAsync(ConfigPlanKind.Restore, $"{(devicesOnly ? "Device config from" : "Restore")} {sourceName} snapshot ({label}) onto {await SiteNameAsync(cancellationToken)}",
            $"Snapshot, {label}{(sameSite ? "" : $", site {sourceSiteSlug}")}", sameSite ? snapshotId : null, sameSite ? null : sourceSiteSlug,
            translation?.Desired ?? state, options, Actor, cancellationToken,
            doc =>
            {
                if (translation != null) AddTranslation(doc, translation);
                doc.Context["source_taken_at"] = snap.TakenAt.ToString("o");
                if (snap.NetworkAppVersion is { } from && live.NetworkAppVersion is { } to && from != to)
                    doc.Notes.Add(FirmwareVersionFormat.IsNewer(from, to)
                        ? $"The snapshot is from Network {from}; this console runs the older {to}. Settings {from} added may be refused: update the Network app in Firmware Rollout first."
                        : $"The snapshot is from Network {from}; this console runs {to}. Fields the newer version added keep their defaults.");
                doc.FirmwareAlign = FirmwareBehind(state, live);
                if (!hasGateway)
                    doc.Notes.Add("No gateway is adopted here yet: gateway-bound items (routes, port forwards, traffic routes, QoS, NAT) wait until it is.");
                var missing = state.Get("device").Count(d => !live.Get("device").Any(l => string.Equals(l["mac"]?.ToString(), d["mac"]?.ToString(), StringComparison.OrdinalIgnoreCase)));
                if (missing > 0)
                    doc.Notes.Add($"{missing} {(missing == 1 ? "device in the snapshot is" : "devices in the snapshot are")} not adopted here. Adopt them from Adopt Pending Devices, then Write Device Config builds the plan for their config.");
            },
            impactBefore: translation != null ? state : null);
        return plan.Id;
    }

    /// <summary>
    /// Records a legacy-to-zone translation on the plan. An enabled rule it could not prove stops at
    /// Review Plan as a regression to accept by name (spec "Legacy firewall to ZBF migration").
    /// </summary>
    internal static void AddTranslation(PlanDocument doc, LegacyTranslation translation)
    {
        var translated = translation.Rules.Count(r => r.Status == LegacyRuleStatus.Translated);
        var covered = translation.Rules.Count(r => r.Status == LegacyRuleStatus.Covered);
        doc.Notes.Add($"Legacy firewall rules translated to zone policies: {translated} translated ({translation.Rules.Sum(r => r.Policies)} policies), "
            + $"{covered} covered by the zone-based firewall's built-in rules, {translation.Unproven.Count()} not translated. "
            + "The Security Audit compares the result with the legacy config.");
        foreach (var r in translation.Unproven)
        {
            if (!r.Enabled)
            {
                doc.Notes.Add($"Legacy rule {r.Name} ({r.Ruleset}, disabled) not translated: {r.Detail}");
                continue;
            }
            doc.Regressions.Add(new ImpactLine
            {
                Kind = "Regression", Title = $"Legacy rule not translated: {r.Name}",
                Detail = $"{r.Ruleset}: {r.Detail} Recreate it as a zone policy after the restore, or accept that it is gone.",
                Severity = "Recommended", Key = $"legacy:{r.Id}",
            });
        }
    }
}
