namespace NetworkOptimizer.Core.Models;

/// <summary>
/// Represents a UniFi Protect camera/device with MAC and name
/// </summary>
public sealed record ProtectCamera
{
    /// <summary>
    /// MAC address of the Protect device (lowercase, colon-separated)
    /// </summary>
    public required string Mac { get; init; }

    /// <summary>
    /// Display name of the Protect device (from Protect API or model name)
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Network ID from the Protect API (connection_network_id).
    /// This is the authoritative network assignment for Protect devices,
    /// which may differ from the Network API's network_id when Virtual Network Override is used.
    /// </summary>
    public string? ConnectionNetworkId { get; init; }

    /// <summary>
    /// Whether this device is an NVR (UNVR, UNVR-Pro, Cloud Key).
    /// NVRs are infrastructure devices that can legitimately be on Management or Security VLANs.
    /// </summary>
    public bool IsNvr { get; init; }

    /// <summary>
    /// MAC address of the switch/AP the camera is connected through (from uplink_mac in Protect API).
    /// Used for fallback port matching when the camera doesn't appear in stat/sta client data.
    /// </summary>
    public string? UplinkMac { get; init; }

    /// <summary>
    /// Create a ProtectCamera from MAC and name
    /// </summary>
    public static ProtectCamera Create(string mac, string name, string? connectionNetworkId = null, bool isNvr = false, string? uplinkMac = null)
        => new() { Mac = mac.ToLowerInvariant(), Name = name, ConnectionNetworkId = connectionNetworkId, IsNvr = isNvr, UplinkMac = uplinkMac?.ToLowerInvariant() };
}

/// <summary>
/// Collection of UniFi Protect cameras indexed by MAC address
/// </summary>
public sealed class ProtectCameraCollection
{
    private readonly Dictionary<string, ProtectCamera> _cameras = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Number of cameras in the collection
    /// </summary>
    public int Count => _cameras.Count;

    /// <summary>
    /// Add a camera to the collection
    /// </summary>
    public void Add(ProtectCamera camera)
    {
        _cameras[camera.Mac] = camera;
    }

    /// <summary>
    /// Add a camera by MAC and name
    /// </summary>
    public void Add(string mac, string name)
    {
        Add(ProtectCamera.Create(mac, name));
    }

    /// <summary>
    /// Add a camera by MAC, name, and connection network ID
    /// </summary>
    public void Add(string mac, string name, string? connectionNetworkId, bool isNvr = false, string? uplinkMac = null)
    {
        Add(ProtectCamera.Create(mac, name, connectionNetworkId, isNvr, uplinkMac));
    }

    /// <summary>
    /// Try to get the full ProtectCamera by MAC address
    /// </summary>
    public bool TryGet(string? mac, out ProtectCamera? camera)
    {
        camera = null;
        if (string.IsNullOrEmpty(mac))
            return false;
        return _cameras.TryGetValue(mac, out camera);
    }

    /// <summary>
    /// Check if a MAC address belongs to a Protect camera
    /// </summary>
    public bool ContainsMac(string? mac)
    {
        if (string.IsNullOrEmpty(mac))
            return false;
        return _cameras.ContainsKey(mac);
    }

    /// <summary>
    /// Try to get the camera name for a MAC address
    /// </summary>
    public bool TryGetName(string? mac, out string? name)
    {
        name = null;
        if (string.IsNullOrEmpty(mac))
            return false;

        if (_cameras.TryGetValue(mac, out var camera))
        {
            name = camera.Name;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Get the camera name for a MAC address, or null if not found
    /// </summary>
    public string? GetName(string? mac)
    {
        TryGetName(mac, out var name);
        return name;
    }

    /// <summary>
    /// Try to get the connection network ID for a MAC address.
    /// This is the authoritative network from the Protect API.
    /// </summary>
    public bool TryGetNetworkId(string? mac, out string? networkId)
    {
        networkId = null;
        if (string.IsNullOrEmpty(mac))
            return false;

        if (_cameras.TryGetValue(mac, out var camera))
        {
            networkId = camera.ConnectionNetworkId;
            return !string.IsNullOrEmpty(networkId);
        }
        return false;
    }

    /// <summary>
    /// Check if a MAC address belongs to an NVR device
    /// </summary>
    public bool IsNvr(string? mac)
    {
        if (string.IsNullOrEmpty(mac))
            return false;
        return _cameras.TryGetValue(mac, out var camera) && camera.IsNvr;
    }

    /// <summary>
    /// Get all cameras in the collection
    /// </summary>
    public IEnumerable<ProtectCamera> GetAll() => _cameras.Values;

    /// <summary>
    /// Create an empty collection
    /// </summary>
    public static ProtectCameraCollection Empty => new();
}
