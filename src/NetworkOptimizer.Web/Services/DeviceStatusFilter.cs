using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The Dashboard Device Status card's type tabs and search: which types the site has, and whether
/// a device matches what was typed.
/// </summary>
public static class DeviceStatusFilter
{
    /// <summary>The tab order: the network's spine first, then the rest by name.</summary>
    private static readonly DeviceType[] LeadingTypes = [DeviceType.Gateway, DeviceType.Switch, DeviceType.AccessPoint];

    /// <summary>
    /// Words a user may type for a type, beyond its display name. A search token matches a type when
    /// it starts one of these words, so "AP", "wifi", and "router" all find what people call them.
    /// </summary>
    private static readonly Dictionary<DeviceType, string[]> TypeWords = new()
    {
        [DeviceType.Gateway] = ["router", "firewall", "gw", "udm", "ucg", "uxg", "usg", "efg"],
        [DeviceType.Switch] = ["sw", "usw"],
        [DeviceType.AccessPoint] = ["ap", "aps", "wap", "uap", "wifi", "wi-fi", "wireless", "wlan"],
        [DeviceType.CellularModem] = ["lte", "5g", "4g", "modem", "mobile", "broadband", "umbb"],
        [DeviceType.CableModem] = ["modem", "docsis", "uci"],
        [DeviceType.BuildingBridge] = ["ubb", "ptp", "b2b"],
        [DeviceType.DeviceBridge] = ["udb"],
        [DeviceType.CloudKey] = ["cloud", "key", "uck", "console", "controller"],
        [DeviceType.SmartPower] = ["power", "pdu", "plug", "strip", "usp"],
        [DeviceType.NAS] = ["storage", "unas"],
        [DeviceType.ProtectDevice] = ["nvr", "camera", "unvr"],
        [DeviceType.TalkDevice] = ["phone", "voip"],
        [DeviceType.AccessDevice] = ["door"],
        [DeviceType.Accessory] = ["sfp"],
        [DeviceType.TravelRouter] = ["router", "utr"],
    };

    /// <summary>
    /// True when the device wants a look: offline or in error, its last restart from here failed, or it
    /// rebooted unexpectedly in the last day. In-progress states (provisioning, updating) do not count.
    /// </summary>
    public static bool NeedsAttention(DeviceInfo device, bool restartFailed) =>
        device.StatusInfo.Kind is DeviceStatusKind.Offline or DeviceStatusKind.Error
        || restartFailed
        || (device.RebootWasUnexpected && device.RebootedRecently);

    /// <summary>The types present in <paramref name="devices"/>, in tab order, each with its count.</summary>
    public static IReadOnlyList<(DeviceType Type, int Count)> PresentTypes(IEnumerable<DeviceInfo> devices) =>
        devices.GroupBy(d => d.Type)
            .Select(g => (Type: g.Key, Count: g.Count()))
            .OrderBy(t => Array.IndexOf(LeadingTypes, t.Type) is var i && i >= 0 ? i : LeadingTypes.Length)
            .ThenBy(t => t.Type.ToDisplayName(), StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// True when the device is of <paramref name="type"/> (null is every type) and matches every
    /// whitespace-separated word of <paramref name="query"/>.
    /// </summary>
    public static bool Matches(DeviceInfo device, DeviceType? type, string? query)
    {
        if (type is { } t && device.Type != t)
            return false;
        if (string.IsNullOrWhiteSpace(query))
            return true;

        foreach (var token in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TokenMatches(device, token))
                return false;
        }
        return true;
    }

    private static bool TokenMatches(DeviceInfo device, string token)
    {
        if (Contains(device.Name, token)
            || Contains(device.IpAddress, token)
            || Contains(device.Model, token)
            || Contains(device.Firmware, token)
            || Contains(device.Mac, token)
            || Contains(device.StatusInfo.Label, token))
            return true;

        foreach (var word in TypeWordsFor(device.Type))
        {
            if (word.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static IEnumerable<string> TypeWordsFor(DeviceType type)
    {
        var name = type.ToDisplayName();
        // "Access Point" answers to "access", "point", and "accesspoint".
        foreach (var word in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            yield return word;
        if (name.Contains(' '))
            yield return name.Replace(" ", "");
        if (TypeWords.TryGetValue(type, out var extra))
        {
            foreach (var word in extra)
                yield return word;
        }
    }

    private static bool Contains(string? value, string token) =>
        !string.IsNullOrEmpty(value) && value.Contains(token, StringComparison.OrdinalIgnoreCase);
}
