using System.Reflection;
using System.Text.Json;
using NetworkOptimizer.Audit.Rules;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>Names the Change Control collection an audit config input is built from.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public sealed class AuditInputCollectionAttribute(string collection) : Attribute
{
    public string Collection { get; } = collection;
}

/// <summary>
/// The config half of an <see cref="Audit.Models.AuditRequest"/>: everything a config change can
/// alter. Telemetry (clients, history, fingerprints, Protect cameras) is not here; the comparison
/// reads it once so both runs see the same network. Each property names its source collection,
/// and that mapping is the impact-diff trigger list.
/// </summary>
public sealed class AuditConfigInputs
{
    /// <summary>stat/device JSON (<c>{"data":[...]}</c>) with this config's device fields overlaid on live telemetry.</summary>
    [AuditInputCollection("device")]
    public required string DeviceDataJson { get; init; }

    /// <summary>rest/setting in the v1 envelope.</summary>
    [AuditInputCollection("setting")]
    public JsonElement? SettingsData { get; init; }

    /// <summary>v2 firewall-policies array.</summary>
    [AuditInputCollection("firewall_policy")]
    public JsonElement? FirewallPoliciesData { get; init; }

    /// <summary>Legacy rest/firewallrule list, for sites not on zone-based firewall.</summary>
    [AuditInputCollection("firewallrule")]
    public JsonElement? LegacyFirewallRulesData { get; init; }

    [AuditInputCollection("firewallgroup")]
    public List<UniFiFirewallGroup>? FirewallGroups { get; init; }

    /// <summary>v2 nat array.</summary>
    [AuditInputCollection("nat")]
    public JsonElement? NatRulesData { get; init; }

    [AuditInputCollection("portconf")]
    public List<UniFiPortProfile>? PortProfiles { get; init; }

    [AuditInputCollection("networkconf")]
    public List<UniFiNetworkConfig>? NetworkConfigs { get; init; }

    [AuditInputCollection("firewall_zone")]
    public List<UniFiFirewallZone>? FirewallZones { get; init; }

    /// <summary>Configured port forwards; live UPnP mappings are added by the comparison.</summary>
    [AuditInputCollection("portforward")]
    public List<UniFiPortForwardRule>? PortForwardRules { get; init; }

    /// <summary>UPnP from the usg setting section.</summary>
    [AuditInputCollection("setting")]
    public bool? UpnpEnabled { get; init; }

    /// <summary>
    /// A client address as this config would have it (null: unchanged). Set when the config moves subnets:
    /// clients renew into the new one, so live addresses would read as mismatched.
    /// </summary>
    public Func<string?, string?>? MapClientIp { get; init; }

    /// <summary>Collections whose change can move an audit result.</summary>
    public static IReadOnlySet<string> TriggerCollections { get; } = typeof(AuditConfigInputs)
        .GetProperties()
        .SelectMany(p => p.GetCustomAttributes<AuditInputCollectionAttribute>())
        .Select(a => a.Collection)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public partial class AuditService
{
    /// <summary>
    /// Runs the audit engine on two configs against one read of live telemetry, so the difference
    /// between the results is the config's doing alone (Change Control's impact diff). Persists
    /// nothing and publishes no alerts. Threat context is left out for the same reason.
    /// </summary>
    public async Task<(AuditResult Before, AuditResult After)?> RunConfigComparisonAsync(
        AuditConfigInputs before, AuditConfigInputs after, string? networkAppVersion)
    {
        if (!_connectionService.IsConnected || _connectionService.Client == null)
            return null;

        var options = new AuditOptions();
        await LoadAuditSettingsAsync(options);
        UnusedPortRule.SetThresholds(options.UnusedPortInactivityDays, options.NamedPortInactivityDays);
        var client = _connectionService.Client;

        List<UniFiClientResponse>? clients = null;
        List<UniFiClientDetailResponse>? history = null;
        ProtectCameraCollection? cameras = null;
        List<string>? unifiDeviceMacs = null;
        List<UniFiPortForwardRule> upnpMappings = [];
        try { clients = await client.GetClientsAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "Comparison: clients unavailable"); }
        try { history = await client.GetClientHistoryAsync(withinHours: 720); } catch (Exception ex) { _logger.LogDebug(ex, "Comparison: client history unavailable"); }
        try { cameras = await client.GetProtectCamerasAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "Comparison: Protect cameras unavailable"); }
        try
        {
            var active = await client.GetActiveClientsAsync(includeUnifiDevices: true);
            unifiDeviceMacs = active.Where(c => c.UnifiDevice).Select(c => c.Mac).ToList();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Comparison: UniFi device clients unavailable"); }
        try { upnpMappings = (await client.GetPortForwardRulesAsync()).Where(r => r.IsUpnp == 1).ToList(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Comparison: UPnP mappings unavailable"); }
        var fingerprints = await _fingerprintService.GetDatabaseAsync(_connectionService);

        var allowance = new Audit.Models.DeviceAllowanceSettings
        {
            AllowAppleStreamingOnMainNetwork = options.AllowAppleStreamingOnMainNetwork,
            AllowAllStreamingOnMainNetwork = options.AllowAllStreamingOnMainNetwork,
            AllowNameBrandTVsOnMainNetwork = options.AllowNameBrandTVsOnMainNetwork,
            AllowAllTVsOnMainNetwork = options.AllowAllTVsOnMainNetwork,
            AllowMediaPlayersOnMainNetwork = options.AllowMediaPlayersOnMainNetwork,
            AllowPrintersOnMainNetwork = options.AllowPrintersOnMainNetwork,
        };

        async Task<AuditResult> Run(AuditConfigInputs config)
        {
            _firewallParser.SetFirewallGroups(config.FirewallGroups);
            List<Audit.Models.FirewallRule>? rules = null;
            if (config.FirewallPoliciesData is { } policies && HasFirewallData(policies))
                rules = _firewallParser.ExtractFirewallPolicies(policies);
            else if (config.LegacyFirewallRulesData is { ValueKind: JsonValueKind.Array } legacy)
                rules = legacy.EnumerateArray().Select(_firewallParser.ParseFirewallRule).OfType<Audit.Models.FirewallRule>().ToList();

            var result = await _auditEngine.RunAuditAsync(new Audit.Models.AuditRequest
            {
                DeviceDataJson = config.DeviceDataJson,
                Clients = config.MapClientIp is { } map ? clients?.Select(c => MovedClient(c, map)).ToList() : clients,
                ClientHistory = config.MapClientIp is { } historyMap ? history?.Select(c => MovedClient(c, historyMap)).ToList() : history,
                FingerprintDb = fingerprints,
                SettingsData = config.SettingsData,
                FirewallRules = rules,
                FirewallGroups = config.FirewallGroups,
                NatRulesData = config.NatRulesData,
                AllowanceSettings = allowance,
                ProtectCameras = cameras,
                PortProfiles = config.PortProfiles,
                NetworkApplicationVersion = networkAppVersion,
                UniFiDeviceClientMacs = unifiDeviceMacs,
                ClientName = "Change Control",
                DnatExcludedVlanIds = options.DnatExcludedVlanIds,
                PiholeManagementPort = options.PiholeManagementPort,
                PiholeManagementUrl = options.PiholeManagementUrl,
                TrustedDnsRedirectTargets = options.TrustedDnsRedirectTargets,
                UpnpEnabled = config.UpnpEnabled,
                PortForwardRules = [.. config.PortForwardRules ?? [], .. upnpMappings],
                NetworkConfigs = config.NetworkConfigs,
                FirewallZones = config.FirewallZones,
                NetworkPurposeOverrides = options.NetworkPurposeOverrides,
            });
            return ConvertAuditResult(result, options);
        }

        // Both runs probe the same live network, so the second reuses the first's DNS probes.
        using var probes = Audit.Dns.ThirdPartyDnsDetector.ShareProbeResults();
        return (await Run(before), await Run(after));
    }

    // A copy, as both runs share one read of the clients.
    private static T MovedClient<T>(T client, Func<string?, string?> map) where T : class
    {
        var copy = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(client))!;
        switch (copy)
        {
            case UniFiClientResponse c:
                c.Ip = map(c.Ip) ?? c.Ip;
                c.LastIp = map(c.LastIp) ?? c.LastIp;
                c.FixedIp = map(c.FixedIp) ?? c.FixedIp;
                break;
            case UniFiClientDetailResponse d:
                d.Ip = map(d.Ip) ?? d.Ip;
                d.LastIp = map(d.LastIp) ?? d.LastIp;
                d.FixedIp = map(d.FixedIp) ?? d.FixedIp;
                break;
        }
        return copy;
    }
}
