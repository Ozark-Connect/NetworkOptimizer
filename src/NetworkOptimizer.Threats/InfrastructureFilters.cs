using System.Net;
using System.Net.Sockets;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Threats;

/// <summary>
/// The system Infrastructure filters for a site's management hosts (this host on the default site,
/// each separate-box On-Site Agent on its site), one per device-management port. Never source-wide:
/// that would hide every alert a compromised host raised.
/// </summary>
public static class InfrastructureFilters
{
    /// <summary>What Network Optimizer and its agents dial on devices, by destination port.</summary>
    public static readonly IReadOnlyDictionary<int, string> ManagementPorts = new Dictionary<int, string>
    {
        [22] = "SSH",
        [8899] = "AP Agent",
        [161] = "SNMP",
        [5201] = "iperf3",
    };

    /// <summary>Ends every label this class writes, so a demoted filter is recognized when its host returns.</summary>
    public const string LabelSuffix = "(device management)";

    /// <summary>What to change so the site's system Infrastructure filters match its management hosts.</summary>
    /// <param name="Add">New filters to save.</param>
    /// <param name="Retire">System filters for a host, port, or shape no longer wanted: demote and disable.</param>
    /// <param name="Promote">Filters retired earlier whose host and port are wanted again.</param>
    public sealed record Changes(List<ThreatNoiseFilter> Add, List<ThreatNoiseFilter> Retire, List<ThreatNoiseFilter> Promote);

    public static Changes Plan(IReadOnlyList<ThreatNoiseFilter> existing, IEnumerable<InfrastructureSource> sources)
    {
        var desired = new Dictionary<(string Ip, int Port), InfrastructureSource>();
        foreach (var source in sources)
            foreach (var port in ManagementPorts.Keys)
                desired.TryAdd((source.Ip, port), source);

        bool Wanted(ThreatNoiseFilter f) =>
            f.DestIp == null && f.DestPort is int port && f.SourceIp != null && desired.ContainsKey((f.SourceIp, port));

        var system = existing.Where(f => f.IsSystem && f.Category == ThreatFilterCategory.Infrastructure).ToList();
        var retire = system.Where(f => !Wanted(f)).ToList();
        var covered = system.Where(Wanted).Select(f => (f.SourceIp!, f.DestPort!.Value)).ToHashSet();

        var promote = new List<ThreatNoiseFilter>();
        foreach (var f in existing.Where(f => !f.IsSystem && f.Category == ThreatFilterCategory.Infrastructure &&
                                              (f.Label ?? "").EndsWith(LabelSuffix, StringComparison.Ordinal) && Wanted(f)))
        {
            if (covered.Add((f.SourceIp!, f.DestPort!.Value)))
                promote.Add(f);
        }

        var add = desired.Where(d => !covered.Contains(d.Key)).Select(d => new ThreatNoiseFilter
        {
            SourceIp = d.Key.Ip,
            DestPort = d.Key.Port,
            Category = ThreatFilterCategory.Infrastructure,
            Label = $"{d.Value.Name} {LabelSuffix}",
            Description = "Auto-created: SSH, AP Agent, SNMP, iperf3",
            IsSystem = true,
            Enabled = true,
        }).ToList();

        return new Changes(add, retire, promote);
    }

    /// <summary>
    /// This host's LAN IPv4, or null when it cannot be known. HOST_IP wins when set. A container
    /// that sees a single interface is on bridge networking, where that address is the container's.
    /// </summary>
    public static string? DetectSelfIpv4(
        Func<string, string?>? getEnv = null,
        Func<IReadOnlyList<string>>? localAddresses = null,
        Func<string?>? detectLocalIp = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        localAddresses ??= NetworkUtilities.LocalUnicastAddresses;
        detectLocalIp ??= NetworkUtilities.DetectLocalIp;

        var inContainer = string.Equals(getEnv("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);
        var hostIpSet = !string.IsNullOrWhiteSpace(getEnv("HOST_IP"));
        if (inContainer && !hostIpSet && localAddresses().Count <= 1)
            return null;

        var ip = detectLocalIp();
        return IPAddress.TryParse(ip, out var address) && address.AddressFamily == AddressFamily.InterNetwork
            ? address.ToString()
            : null;
    }
}
