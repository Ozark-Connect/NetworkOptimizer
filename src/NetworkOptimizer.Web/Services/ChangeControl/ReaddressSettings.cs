using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>One of Network Optimizer's own settings that holds a moved address.</summary>
public sealed record SettingRewrite(string Setting, string Name, string Old, string New);

/// <summary>
/// Network Optimizer's own settings that hold LAN addresses: latency targets, device SSH hosts, the
/// Gateway SSH host, the UniFi Console URL, monitoring interfaces, modems, speed test servers, UPnP
/// notes. A re-address moves them with the site, and its revert moves them back.
/// </summary>
public static class ReaddressSettings
{
    /// <summary>The setting name of the UniFi Console URL, which needs a reconnect once moved.</summary>
    public const string ConsoleSetting = "UniFi Console connection";

    /// <summary>What a move would change (<paramref name="apply"/> false), or changes it and saves.</summary>
    public static async Task<List<SettingRewrite>> RunAsync(NetworkOptimizerDbContext db, IReadOnlyList<SubnetMove> moves, IReadOnlyList<string> remote, bool apply, CancellationToken ct)
    {
        var map = Readdressing.Map(moves);
        var far = remote.Select(Readdressing.ParseRange).OfType<Readdressing.Subnet>().ToList();
        var found = new List<SettingRewrite>();
        if (map.Count == 0) return found;

        string? Ip(string? value) => value == null ? null : Readdressing.Rewrite(value, map, far);
        string? Url(string? value)
        {
            if (value == null || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || Readdressing.Rewrite(uri.Host, map, far) is not { } host) return null;
            var at = value.IndexOf(uri.Host, StringComparison.Ordinal);
            return at < 0 ? null : value[..at] + host + value[(at + uri.Host.Length)..];
        }
        void Move<T>(IEnumerable<T> rows, string setting, Func<T, string> name, Func<T, string?> get, Action<T, string> set, Func<string?, string?> map1)
        {
            foreach (var row in rows)
                if (get(row) is { } old && map1(old) is { } moved)
                {
                    found.Add(new SettingRewrite(setting, name(row), old, moved));
                    if (apply) set(row, moved);
                }
        }

        Move(await db.MonitoringTargets.ToListAsync(ct), "Latency target", t => t.Name, t => t.Address, (t, v) => t.Address = v, Ip);
        Move(await db.DeviceSshConfigurations.ToListAsync(ct), "Device SSH", d => d.Name, d => d.Host, (d, v) => d.Host = v, Ip);
        Move(await db.GatewaySshSettings.ToListAsync(ct), "Gateway SSH", _ => "Gateway", g => g.Host, (g, v) => g.Host = v, Ip);
        Move(await db.UniFiConnectionSettings.ToListAsync(ct), ConsoleSetting, _ => "Console URL", c => c.ControllerUrl, (c, v) => c.ControllerUrl = v, Url);
        var interfaces = await db.MonitoringInterfaces.ToListAsync(ct);
        Move(interfaces, "Monitoring interface", i => i.Name, i => i.TargetIp, (i, v) => i.TargetIp = v, Ip);
        Move(interfaces, "Monitoring interface", i => i.Name, i => i.GatewayLocalIp, (i, v) => i.GatewayLocalIp = v, Ip);
        Move(interfaces, "Monitoring interface", i => i.Name, i => i.AliasIp, (i, v) => i.AliasIp = v, Ip);
        Move(await db.ModemConfigurations.ToListAsync(ct), "Cellular modem", m => m.Name, m => m.Host, (m, v) => m.Host = v, Ip);
        Move(await db.CmConfigurations.ToListAsync(ct), "Cable modem", m => m.Name, m => m.Host, (m, v) => m.Host = v, Ip);
        Move(await db.OntConfigurations.ToListAsync(ct), "ONT", m => m.Name, m => m.Host, (m, v) => m.Host = v, Ip);
        Move(await db.ExternalSpeedTestServers.ToListAsync(ct), "Speed test server", s => s.Name, s => s.Host, (s, v) => s.Host = v, Ip);
        Move(await db.UpnpNotes.ToListAsync(ct), "UPnP note", n => n.HostIp, n => n.HostIp, (n, v) => n.HostIp = v, Ip);

        if (apply && found.Count > 0) await db.SaveChangesAsync(ct);
        return found;
    }
}
