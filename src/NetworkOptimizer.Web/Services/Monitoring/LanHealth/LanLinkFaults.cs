using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>
/// The devices whose uplink has an open LAN Health fault alert (Link Flapping, Link Errors), per site. The fabric
/// target's sustained-loss alert checks it so one fault raises one alert: the link alert names the cause.
/// </summary>
public sealed class LanLinkFaults
{
    private readonly ConcurrentDictionary<(string Site, string Mac), byte> _open = new();

    /// <summary>Marks or clears an open fault on the link into <paramref name="deviceMac"/>.</summary>
    public void Set(string site, string deviceMac, bool open)
    {
        var key = (site, LanLinkBuilder.Mac(deviceMac));
        if (open) _open[key] = 0;
        else _open.TryRemove(key, out _);
    }

    /// <summary>Whether the link into <paramref name="deviceMac"/> has an open fault alert.</summary>
    public bool Has(string site, string? deviceMac) =>
        !string.IsNullOrEmpty(deviceMac) && _open.ContainsKey((site, LanLinkBuilder.Mac(deviceMac)));
}