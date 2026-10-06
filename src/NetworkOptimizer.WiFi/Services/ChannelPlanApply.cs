using NetworkOptimizer.Core;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Services;

/// <summary>One radio to move, carrying the before and after the user saw in the plan.</summary>
public sealed record ChannelApplyItem(
    string ApMac,
    string ApName,
    RadioBand Band,
    int CurrentChannel,
    int CurrentWidth,
    int Channel,
    int Width,
    bool IsDfs);

/// <summary>What happened to one radio.</summary>
public enum ChannelApplyStatus
{
    /// <summary>The AP reported the new channel.</summary>
    Applied,

    /// <summary>UniFi Network accepted the change, but the AP had not reported it by the deadline.</summary>
    Unconfirmed,

    /// <summary>Not attempted, for the reason given.</summary>
    Skipped,

    /// <summary>Attempted and refused, or the AP could not be read.</summary>
    Failed
}

/// <summary>The result for one radio.</summary>
public sealed record ChannelApplyOutcome(ChannelApplyItem Item, ChannelApplyStatus Status, string? Reason = null);

/// <summary>
/// The decisions behind applying a channel plan, kept free of I/O: which rows are applied, whether a
/// fresh device read still matches the plan, and whether the AP has arrived.
/// </summary>
public static class ChannelPlanApply
{
    /// <summary>Most radios moved at once, so the console never takes dozens of changes in one instant.</summary>
    public const int MaxWaveSize = 10;

    /// <summary>
    /// Groups the radios into waves that move together. Two APs that hear each other never share a
    /// wave, so a client on a moving AP still has an unmoved neighbor to roam to. An AP missing from
    /// the map moves alone, and with no map at all every AP does.
    /// </summary>
    public static List<List<ChannelApplyItem>> Waves(
        IReadOnlyList<ChannelApplyItem> items, IReadOnlyDictionary<string, HashSet<string>>? hearing)
    {
        if (hearing == null) return items.Select(i => new List<ChannelApplyItem> { i }).ToList();

        bool Known(ChannelApplyItem i) => hearing.ContainsKey(i.ApMac.ToLowerInvariant());
        bool Hears(ChannelApplyItem a, ChannelApplyItem b) =>
            (hearing.TryGetValue(a.ApMac.ToLowerInvariant(), out var ah) && ah.Contains(b.ApMac)) ||
            (hearing.TryGetValue(b.ApMac.ToLowerInvariant(), out var bh) && bh.Contains(a.ApMac));

        // Most-connected first: they are the hardest to place, so they take the early waves.
        var ordered = items
            .OrderByDescending(i => items.Count(o => !ReferenceEquals(o, i) && Hears(i, o)))
            .ThenBy(i => i.ApName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var waves = new List<List<ChannelApplyItem>>();
        foreach (var item in ordered)
        {
            var wave = Known(item)
                ? waves.FirstOrDefault(w => w.Count < MaxWaveSize && w.All(o => Known(o) && !Hears(item, o)))
                : null;
            if (wave == null) waves.Add(new List<ChannelApplyItem> { item });
            else wave.Add(item);
        }
        return waves;
    }

    /// <summary>
    /// Splits a band plan's changed rows into the radios to move and the ones left alone. Pinned rows
    /// never change, so they are never listed. A mesh child is left alone: its radio follows its
    /// parent on the uplink band, and the parent is in the list. A 320 MHz target is written like any
    /// other: the AP picks the block from the primary, exactly as when it is set in UniFi Network.
    /// </summary>
    public static (List<ChannelApplyItem> ToApply, List<ChannelApplyOutcome> NotApplied) FromPlan(ChannelPlan plan)
    {
        var toApply = new List<ChannelApplyItem>();
        var notApplied = new List<ChannelApplyOutcome>();
        foreach (var rec in plan.Recommendations.Where(r => r.IsChanged && !r.IsKept))
        {
            var item = new ChannelApplyItem(rec.ApMac, rec.ApName, plan.Band,
                rec.CurrentChannel, rec.CurrentWidth, rec.RecommendedChannel, rec.RecommendedWidth,
                rec.IsRecommendedDfsChannel);
            if (rec.IsMeshConstrained)
                notApplied.Add(new(item, ChannelApplyStatus.Skipped, "Follows its mesh parent"));
            else
                toApply.Add(item);
        }
        return (toApply, notApplied);
    }

    /// <summary>
    /// Checks a fresh read of the AP against the row. Returns the radio update to send, or the
    /// outcome that stops it: the AP is gone or offline, has no radio on the band, or already sits
    /// somewhere other than the plan's "current" (the plan is stale).
    /// </summary>
    [VendorSpecific("UniFi", "stat/device radio_table / radio_table_stats; state 1 = connected")]
    public static (RadioChannelUpdate? Update, ChannelApplyOutcome? Stop) Preflight(
        ChannelApplyItem item, UniFiDeviceResponse? device)
    {
        if (device == null || string.IsNullOrEmpty(device.Id))
            return (null, new(item, ChannelApplyStatus.Failed, "Not found in UniFi Network"));
        if (device.State != 1)
            return (null, new(item, ChannelApplyStatus.Skipped, "Offline"));

        var bandCode = item.Band.ToUniFiCode();
        var radio = device.RadioTable?.FirstOrDefault(r => string.Equals(r.Radio, bandCode, StringComparison.OrdinalIgnoreCase));
        if (radio == null || string.IsNullOrEmpty(radio.Name))
            return (null, new(item, ChannelApplyStatus.Failed, $"No {item.Band.ToDisplayString()} radio"));

        var live = LiveRadio(device, radio.Name, bandCode);
        if (live?.Channel is int channel && (channel != item.CurrentChannel || (live.Bw is int bw && bw != item.CurrentWidth)))
            return (null, new(item, ChannelApplyStatus.Skipped,
                $"Now on Ch {channel} / {live.Bw ?? item.CurrentWidth} MHz, not what the plan was built on"));

        return (new RadioChannelUpdate(radio.Name, bandCode, item.Channel, item.Width), null);
    }

    /// <summary>True once the AP is connected and its radio reports the target channel and width.</summary>
    [VendorSpecific("UniFi", "stat/device radio_table_stats live channel / bw")]
    public static bool HasArrived(ChannelApplyItem item, UniFiDeviceResponse? device)
    {
        if (device is not { State: 1 }) return false;
        var bandCode = item.Band.ToUniFiCode();
        var name = device.RadioTable?.FirstOrDefault(r => string.Equals(r.Radio, bandCode, StringComparison.OrdinalIgnoreCase))?.Name;
        var live = LiveRadio(device, name, bandCode);
        return live?.Channel == item.Channel && (live.Bw == null || live.Bw == item.Width);
    }

    private static RadioTableStats? LiveRadio(UniFiDeviceResponse device, string? name, string bandCode) =>
        device.RadioTableStats?.FirstOrDefault(s => name != null && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? device.RadioTableStats?.FirstOrDefault(s => string.Equals(s.Radio, bandCode, StringComparison.OrdinalIgnoreCase));
}
