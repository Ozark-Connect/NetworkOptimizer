using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetworkOptimizer.UniFi;

/// <summary>
/// A WAN's Smart Queues settings before a <see cref="UniFiApiClient.SetWanSmartQueuesAsync"/> change,
/// so a caller can put them back.
/// </summary>
/// <param name="Enabled"><c>wan_smartq_enabled</c>.</param>
/// <param name="DownloadKbps"><c>wan_smartq_down_rate</c>, null when unset.</param>
/// <param name="UploadKbps"><c>wan_smartq_up_rate</c>, null when unset.</param>
public sealed record WanSmartQueues(bool Enabled, int? DownloadKbps, int? UploadKbps);

/// <summary>Outcome of <see cref="UniFiApiClient.SetWanSmartQueuesAsync"/>.</summary>
/// <param name="Success">True when the console accepted the write.</param>
/// <param name="Error">What went wrong, as a sentence fragment, when it did not.</param>
/// <param name="Previous">The WAN's settings before the change, when the WAN was found.</param>
public sealed record WanSmartQueuesResult(bool Success, string? Error, WanSmartQueues? Previous);

public partial class UniFiApiClient
{
    /// <summary>
    /// Sets a WAN's Smart Queues in UniFi Network: on or off, and the download and upload rates in
    /// kbps. A null argument leaves that field as it is. The WAN is the one network with
    /// <c>purpose</c> "wan" and the given <c>wan_networkgroup</c> ("WAN", "WAN2").
    /// </summary>
    /// <remarks>
    /// Reads the whole <c>rest/networkconf</c> object and writes it back with only those fields
    /// changed, so nothing the typed <see cref="Models.UniFiNetworkConfig"/> lacks is lost. A change
    /// makes the console provision the gateway, which rebuilds that WAN's queues; on the gateways
    /// tested every apply also restarts a DHCP server and the DNS forwarder. The caller declares
    /// its <see cref="UniFiWriteSource"/>.
    /// </remarks>
    [NetworkOptimizer.Core.VendorSpecific("UniFi", "rest/networkconf wan_smartq_* read-modify-write")]
    public async Task<WanSmartQueuesResult> SetWanSmartQueuesAsync(
        string networkGroup, bool? enabled = null, int? downloadKbps = null, int? uploadKbps = null,
        CancellationToken cancellationToken = default)
    {
        var read = await SendRawAsync(UniFiApiScope.SiteV1, HttpMethod.Get, "rest/networkconf", null, cancellationToken);
        if (!read.IsSuccess)
            return new WanSmartQueuesResult(false, "the WAN settings could not be read from the UniFi Console", null);

        var update = BuildSmartQueuesUpdate(read.Body, networkGroup, enabled, downloadKbps, uploadKbps);
        if (update.Error != null)
            return new WanSmartQueuesResult(false, update.Error, update.Previous);

        var write = await SendRawAsync(UniFiApiScope.SiteV1, HttpMethod.Put, $"rest/networkconf/{update.Id}",
            update.Body, cancellationToken);
        if (!write.IsSuccess || write.ErrorCode is not (null or "api.err.Ok"))
        {
            _logger.LogWarning("Smart Queues update for {Group} refused: {Status} {Code}", networkGroup, write.Status, write.ErrorCode);
            return new WanSmartQueuesResult(false, "the UniFi Console refused the Smart Queues change", update.Previous);
        }
        return new WanSmartQueuesResult(true, null, update.Previous);
    }

    /// <summary>
    /// Finds the WAN for <paramref name="networkGroup"/> in a <c>rest/networkconf</c> response and
    /// returns it whole with the requested Smart Queues fields set. Refuses a missing or ambiguous
    /// WAN, a rate that is not positive, and Smart Queues on without both rates.
    /// </summary>
    public static (string? Id, string? Body, WanSmartQueues? Previous, string? Error) BuildSmartQueuesUpdate(
        string networkconfJson, string networkGroup, bool? enabled, int? downloadKbps, int? uploadKbps)
    {
        if (downloadKbps is <= 0 || uploadKbps is <= 0)
            return (null, null, null, "a Smart Queues rate must be above zero");

        JsonNode? root;
        try { root = JsonNode.Parse(networkconfJson); }
        catch (JsonException) { return (null, null, null, "the UniFi Console's WAN settings could not be read"); }

        var matches = (root?["data"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(n => (string?)n["purpose"] == "wan" && (string?)n["wan_networkgroup"] == networkGroup)
            .ToList();
        if (matches.Count != 1)
            return (null, null, null, matches.Count == 0
                ? $"no WAN network in UniFi Network uses {networkGroup}"
                : $"more than one WAN network in UniFi Network uses {networkGroup}");

        var wan = matches[0];
        if ((string?)wan["_id"] is not { Length: > 0 } id)
            return (null, null, null, "the WAN network has no ID");

        var previous = new WanSmartQueues(
            ReadBool(wan["wan_smartq_enabled"]), ReadKbps(wan["wan_smartq_down_rate"]), ReadKbps(wan["wan_smartq_up_rate"]));
        var nextEnabled = enabled ?? previous.Enabled;
        var nextDown = downloadKbps ?? previous.DownloadKbps;
        var nextUp = uploadKbps ?? previous.UploadKbps;
        if (nextEnabled && (nextDown is not > 0 || nextUp is not > 0))
            return (null, null, previous, "Smart Queues needs both a download and an upload rate");

        if (enabled.HasValue) wan["wan_smartq_enabled"] = enabled.Value;
        if (downloadKbps.HasValue) wan["wan_smartq_down_rate"] = downloadKbps.Value;
        if (uploadKbps.HasValue) wan["wan_smartq_up_rate"] = uploadKbps.Value;
        return (id, wan.ToJsonString(), previous, null);
    }

    private static bool ReadBool(JsonNode? node) =>
        node is JsonValue value && (value.TryGetValue(out bool b) ? b : value.TryGetValue(out string? s) && bool.TryParse(s, out var p) && p);

    // UniFi sends some numeric fields as strings.
    private static int? ReadKbps(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue(out int i)) return i;
        if (value.TryGetValue(out long l) && l is > 0 and < int.MaxValue) return (int)l;
        return value.TryGetValue(out string? s) && int.TryParse(s, out var parsed) ? parsed : null;
    }
}
