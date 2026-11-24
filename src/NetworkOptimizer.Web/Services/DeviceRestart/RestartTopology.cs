using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Web.Services.Firmware;

namespace NetworkOptimizer.Web.Services.DeviceRestart;

/// <summary>One device as the restart logic sees it: where it hangs in the uplink tree and what state it is in.</summary>
/// <param name="Mac">Normalized MAC.</param>
/// <param name="Name">Display name.</param>
/// <param name="Type">Effective device type.</param>
/// <param name="ParentMac">Normalized MAC of its uplink device, or null at the root.</param>
/// <param name="UplinkPortIdx">The port on the parent it plugs into, for a wired uplink.</param>
/// <param name="State">The console's raw device state (1 connected, 4 upgrading, 5 provisioning).</param>
/// <param name="UptimeSeconds">Uptime as the console last reported it.</param>
/// <param name="PoeWatts">For a switch: watts delivered per <c>port_idx</c> on its last reading.</param>
public sealed record RestartNode(
    string Mac,
    string Name,
    DeviceType Type,
    string? ParentMac,
    int? UplinkPortIdx,
    int State,
    long UptimeSeconds,
    IReadOnlyDictionary<int, double>? PoeWatts = null)
{
    /// <summary>The console's Connected state.</summary>
    public const int ConnectedState = 1;

    /// <summary>The console's Upgrading state.</summary>
    public const int UpgradingState = 4;

    /// <summary>The console's Provisioning state.</summary>
    public const int ProvisioningState = 5;

    public bool IsOnline => State == ConnectedState;

    /// <summary>Upgrading or provisioning: a restart must not land on it.</summary>
    public bool IsBusy => State is UpgradingState or ProvisioningState;
}

/// <summary>
/// A site's uplink tree at one instant, built from the console's device list. Depth 0 is a root
/// (the gateway); a device whose parent the console does not list is a root too.
/// </summary>
public sealed class RestartTopology
{
    private readonly Dictionary<string, RestartNode> _nodes;
    private readonly Dictionary<string, List<string>> _children = new(StringComparer.OrdinalIgnoreCase);

    public RestartTopology(IEnumerable<RestartNode> nodes)
    {
        _nodes = new Dictionary<string, RestartNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
            _nodes[MacNormalizer.Normalize(node.Mac)] = node;

        foreach (var node in _nodes.Values)
        {
            // An uplink loop would put each device in the other's subtree: the edge closing it is dropped.
            if (Parent(node) is not { } parent || IsAbove(node.Mac, parent.Mac)) continue;
            var key = MacNormalizer.Normalize(parent.Mac);
            if (!_children.TryGetValue(key, out var list))
                _children[key] = list = [];
            list.Add(MacNormalizer.Normalize(node.Mac));
        }
    }

    /// <summary>Every device in the tree.</summary>
    public IEnumerable<RestartNode> All => _nodes.Values;

    /// <summary>The device, or null when the console does not list it.</summary>
    public RestartNode? Find(string? mac) =>
        string.IsNullOrEmpty(mac) ? null : _nodes.GetValueOrDefault(MacNormalizer.Normalize(mac));

    /// <summary>The device it uplinks to, or null at a root. A gateway is always a root: the console can report
    /// a LAN switch as its uplink, which would hang the whole site below that switch.</summary>
    public RestartNode? Parent(RestartNode node)
    {
        if (node.Type == DeviceType.Gateway || string.IsNullOrEmpty(node.ParentMac)) return null;
        var parent = Find(node.ParentMac);
        return parent == null || string.Equals(parent.Mac, node.Mac, StringComparison.OrdinalIgnoreCase) ? null : parent;
    }

    /// <summary>The devices above it, nearest first. Stops on a loop rather than spinning.</summary>
    public IReadOnlyList<RestartNode> PathToRoot(string mac)
    {
        var path = new List<RestartNode>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var node = Find(mac);
        if (node == null) return path;
        seen.Add(node.Mac);
        while (Parent(node) is { } parent && seen.Add(parent.Mac))
        {
            path.Add(parent);
            node = parent;
        }
        return path;
    }

    /// <summary>Hops from the root; 0 for a root or an unknown device.</summary>
    public int Depth(string mac) => PathToRoot(mac).Count;

    /// <summary>The devices that uplink straight to it.</summary>
    public IReadOnlyList<RestartNode> Children(string mac) =>
        _children.TryGetValue(MacNormalizer.Normalize(mac), out var kids) ? kids.Select(k => _nodes[k]).ToList() : [];

    /// <summary>Every device below it, not including itself.</summary>
    public IReadOnlyList<RestartNode> Subtree(string mac)
    {
        var result = new List<RestartNode>();
        var root = Find(mac);
        if (root == null) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MacNormalizer.Normalize(root.Mac) };
        var queue = new Queue<string>();
        queue.Enqueue(MacNormalizer.Normalize(root.Mac));
        while (queue.Count > 0)
        {
            if (!_children.TryGetValue(queue.Dequeue(), out var kids)) continue;
            foreach (var kid in kids)
            {
                if (!seen.Add(kid)) continue;
                result.Add(_nodes[kid]);
                queue.Enqueue(kid);
            }
        }
        return result;
    }

    /// <summary>True when <paramref name="ancestorMac"/> is above <paramref name="mac"/> in the tree.</summary>
    public bool IsAbove(string ancestorMac, string mac) =>
        PathToRoot(mac).Any(n => string.Equals(MacNormalizer.Normalize(n.Mac), MacNormalizer.Normalize(ancestorMac), StringComparison.OrdinalIgnoreCase));
}
