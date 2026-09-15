namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Where the observers sit on the site's uplink tree, so a step's dark set can be worked out
/// from the tree rather than assumed.
/// </summary>
/// <param name="ConsoleAttachMac">
/// The device the UniFi Console hangs off, for a console that is a host on the LAN (a
/// self-hosted UniFi OS Server). Null when the console is the tree's root - a Cloud Gateway.
/// </param>
/// <param name="ConsoleUnlocated">
/// True when the console is a LAN host the client list could not place. Nothing about the
/// tree can then say what a switch reboot hides from it, so every device counts as dark.
/// </param>
/// <param name="VantageAttachMacs">
/// The device each probe vantage hangs off, keyed the way probe results name their vantage
/// ("server", "agent-{id}"). An agent on the gateway maps to the gateway itself; a null value is
/// a vantage that could not be placed and is treated as dark for any wired-infrastructure step.
/// </param>
public sealed record RolloutObserverPositions(
    string? ConsoleAttachMac,
    bool ConsoleUnlocated,
    IReadOnlyDictionary<string, string?> VantageAttachMacs)
{
    /// <summary>A Cloud Gateway console with no vantage placed: the pre-locator behavior.</summary>
    public static readonly RolloutObserverPositions ConsoleAtRoot =
        new(null, false, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Which devices and probe vantages a rebooting device takes dark, from the console's uplink tree
/// and where the observers sit on it.
///
/// A device is "offline" when it cannot reach the console, and a vantage's WAN is "down" when its
/// probes cannot reach the gateway. Both are a path on the tree, and a rebooting device darkens
/// whatever has it on that path. With a Cloud Gateway console that path is the device's chain to
/// the root, so the dark set is exactly the subtree - the old downstream walk. With a self-hosted
/// console the path turns at the lowest common ancestor and comes back down to the console's
/// switch, which is how a core switch takes the devices on the gateway's own ports dark: their
/// path to the console runs up through the gateway and back down through that switch.
///
/// The gateway is the root whatever its own uplink fields say: UniFi records where a gateway was
/// found on the LAN as last_uplink, which points at a child and would otherwise close a cycle.
/// </summary>
public static class RolloutDarkSet
{
    /// <summary>
    /// Parent of each device on the tree: the live uplink, or the last one the console recorded
    /// when the live one is missing (a device mid-reboot, or a gateway behind a self-hosted
    /// console, which never carries a live LAN uplink). The gateway has no parent.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> ParentMap(IEnumerable<RolloutDeviceObservation> devices)
    {
        var parents = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices)
            parents[d.Mac] = d.IsGateway ? null : d.UplinkMac ?? d.LastUplinkMac;
        return parents;
    }

    /// <summary>The device and every ancestor up to its root, in that order. Stops at a cycle.</summary>
    public static IReadOnlyList<string> ChainToRoot(string mac, IReadOnlyDictionary<string, string?> parents)
    {
        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = mac;
        while (!string.IsNullOrEmpty(current) && seen.Add(current))
        {
            chain.Add(current);
            parents.TryGetValue(current, out current);
        }
        return chain;
    }

    /// <summary>
    /// The devices between <paramref name="from"/> and <paramref name="to"/> on the tree, both
    /// ends included. Two ends that share no root have no known path between them, so the
    /// answer is the chain from <paramref name="from"/> to its own root - the direction anything
    /// leaving that subtree must travel. A null <paramref name="to"/> asks for that chain outright.
    /// </summary>
    public static IReadOnlyList<string> PathBetween(string from, string? to, IReadOnlyDictionary<string, string?> parents)
    {
        var up = ChainToRoot(from, parents);
        if (string.IsNullOrEmpty(to)) return up;

        var down = ChainToRoot(to, parents);
        var downIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < down.Count; i++)
            downIndex.TryAdd(down[i], i);

        for (var i = 0; i < up.Count; i++)
        {
            if (!downIndex.TryGetValue(up[i], out var meet)) continue;
            var path = new List<string>(up.Take(i + 1));
            for (var j = meet - 1; j >= 0; j--)
                path.Add(down[j]);
            return path;
        }

        return up;
    }

    /// <summary>
    /// Every device a step's reboot hides from the console. Wired-infrastructure steps (a switch
    /// or a gateway) also take every mesh child with them, and everything behind one: a wired
    /// reconvergence drops the wireless backhaul for a minute wherever the child sits.
    /// </summary>
    /// <param name="stepMac">The rebooting device.</param>
    /// <param name="stepIsWiredInfrastructure">Whether the step is a switch or a gateway.</param>
    /// <param name="devices">The console's current device list.</param>
    /// <param name="positions">Where the console sits.</param>
    public static HashSet<string> DevicesDarkenedBy(
        string stepMac,
        bool stepIsWiredInfrastructure,
        IReadOnlyCollection<RolloutDeviceObservation> devices,
        RolloutObserverPositions positions)
    {
        var dark = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var others = devices.Where(d => !string.Equals(d.Mac, stepMac, StringComparison.OrdinalIgnoreCase)).ToList();

        if (positions.ConsoleUnlocated && stepIsWiredInfrastructure)
        {
            foreach (var d in others) dark.Add(d.Mac);
            return dark;
        }

        var parents = ParentMap(devices);
        var consoleAt = positions.ConsoleAttachMac
            ?? devices.FirstOrDefault(d => d.IsGateway)?.Mac;

        foreach (var d in others)
        {
            // Index 0 is the device itself; a device is never dark for being on its own path.
            var path = PathBetween(d.Mac, consoleAt, parents);
            if (path.Skip(1).Contains(stepMac, StringComparer.OrdinalIgnoreCase))
                dark.Add(d.Mac);
        }

        if (!stepIsWiredInfrastructure) return dark;

        var meshChildren = others.Where(d => d.WirelessUplink).Select(d => d.Mac)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (meshChildren.Count == 0) return dark;

        foreach (var d in others)
        {
            if (ChainToRoot(d.Mac, parents).Any(meshChildren.Contains))
                dark.Add(d.Mac);
        }

        return dark;
    }

    /// <summary>
    /// Whether a step's reboot cuts a probe vantage off from the gateway, which is what its WAN
    /// probes then report as a WAN outage.
    /// </summary>
    /// <param name="stepMac">The rebooting device.</param>
    /// <param name="stepIsWiredInfrastructure">Whether the step is a switch or a gateway.</param>
    /// <param name="vantageAttachMac">
    /// The device the vantage hangs off; the gateway itself for an agent on the gateway. Null is
    /// a vantage nobody could place, taken as dark for any wired-infrastructure step.
    /// </param>
    /// <param name="gatewayMac">The site's gateway, or null when the device list has none.</param>
    /// <param name="parents">The tree, from <see cref="ParentMap"/>.</param>
    public static bool VantageDarkenedBy(
        string stepMac,
        bool stepIsWiredInfrastructure,
        string? vantageAttachMac,
        string? gatewayMac,
        IReadOnlyDictionary<string, string?> parents)
    {
        if (string.IsNullOrEmpty(vantageAttachMac))
            return stepIsWiredInfrastructure;

        return PathBetween(vantageAttachMac, gatewayMac, parents)
            .Contains(stepMac, StringComparer.OrdinalIgnoreCase);
    }
}
