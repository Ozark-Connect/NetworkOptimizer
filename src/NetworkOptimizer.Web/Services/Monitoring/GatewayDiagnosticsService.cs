using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Collects read-only interface diagnostics from the site's UniFi gateway over SSH: the
/// interface's addresses and DHCP lease lifetimes, the SFP module's transceiver/DDM readout,
/// and the neighbor (ARP/NDP) table. Nothing here changes gateway state - every command is a
/// read, so a run can never disturb a live WAN.
///
/// All four commands go out in a single SSH round trip separated by marker lines, the same
/// shape the reboot-reason probe uses: SSH session setup dominates the cost, and one trip
/// keeps a diagnostics run to about the latency of a single command.
/// </summary>
public class GatewayDiagnosticsService
{
    private const string AddressMarker = "###ADDR";
    private const string RouteMarker = "###ROUTE";
    private const string SfpMarker = "###SFP";
    private const string NeighborMarker = "###NEIGH";

    private readonly IGatewaySshService _gatewaySsh;
    private readonly IeeeOuiDatabase _ouiDatabase;
    private readonly ILogger<GatewayDiagnosticsService> _logger;

    public GatewayDiagnosticsService(
        IGatewaySshService gatewaySsh,
        IeeeOuiDatabase ouiDatabase,
        ILogger<GatewayDiagnosticsService> logger)
    {
        _gatewaySsh = gatewaySsh;
        _ouiDatabase = ouiDatabase;
        _logger = logger;
    }

    /// <summary>
    /// Runs the diagnostics for one interface. Never throws for a failed command - each
    /// section reports its own error so an interface with no SFP still returns its lease.
    /// </summary>
    public async Task<GatewayDiagnosticsResult> RunAsync(string interfaceName, CancellationToken ct = default)
    {
        var result = new GatewayDiagnosticsResult { Interface = interfaceName };

        if (!GatewayDiagnosticsParser.IsValidInterfaceName(interfaceName))
        {
            result.RunError = "That isn't a valid interface name.";
            return result;
        }

        // The transceiver belongs to the physical port, so a tagged WAN ("ethN.100") has to
        // ask its parent - the sub-interface itself only ever answers "Operation not
        // supported". Addresses and neighbors stay on the requested interface: that is where
        // the lease and the ARP entries actually live.
        var sfpInterface = GatewayDiagnosticsParser.PhysicalInterfaceName(interfaceName);
        result.SfpInterface = sfpInterface;

        // Every command is redirected to stdout so a failure message (no such device, no SFP
        // in the port, ethtool missing) comes back as section text instead of vanishing, and
        // the chain ends on `true` so a non-zero exit from the last command isn't reported as
        // a failed SSH run.
        var command =
            $"echo '{AddressMarker}'; ip -d addr show dev {interfaceName} 2>&1; " +
            $"echo '{RouteMarker}'; ip route show table all 2>/dev/null | grep '^default' | head -20; " +
            $"echo '{SfpMarker}'; ethtool -m {sfpInterface} 2>&1; " +
            $"echo '{NeighborMarker}'; ip neigh show dev {interfaceName} 2>&1; true";

        string output;
        try
        {
            var (success, commandOutput) = await _gatewaySsh.RunCommandAsync(
                command, TimeSpan.FromSeconds(20), ct);
            if (!success)
            {
                result.RunError = string.IsNullOrWhiteSpace(commandOutput)
                    ? "Couldn't reach the gateway over SSH. Check the gateway SSH credentials in Settings."
                    : commandOutput.Trim();
                return result;
            }
            output = commandOutput;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Gateway diagnostics SSH run failed for {Interface}", interfaceName);
            result.RunError = $"Gateway command failed: {ex.Message}";
            return result;
        }

        var sections = SplitSections(output);
        PopulateInterface(result, sections, interfaceName);
        PopulateSfp(result, sections);
        PopulateNeighbors(result, sections);
        return result;
    }

    private void PopulateInterface(
        GatewayDiagnosticsResult result, IReadOnlyDictionary<string, string> sections, string interfaceName)
    {
        var addressOutput = sections.GetValueOrDefault(AddressMarker, string.Empty);
        result.RawOutput[$"ip -d addr show dev {interfaceName}"] = addressOutput;

        var info = GatewayDiagnosticsParser.ParseAddressOutput(addressOutput, interfaceName);
        if (info == null)
        {
            result.InterfaceError = string.IsNullOrWhiteSpace(addressOutput)
                ? "The gateway reported nothing for this interface."
                : addressOutput.Trim();
            return;
        }

        info.DefaultGateway = UpstreamTracerService.SelectWanDefaultGateway(
            sections.GetValueOrDefault(RouteMarker), interfaceName);
        result.InterfaceInfo = info;
    }

    private void PopulateSfp(GatewayDiagnosticsResult result, IReadOnlyDictionary<string, string> sections)
    {
        var sfpOutput = sections.GetValueOrDefault(SfpMarker, string.Empty);
        result.RawOutput[$"ethtool -m {result.SfpInterface ?? result.Interface}"] = sfpOutput;

        var module = GatewayDiagnosticsParser.ParseEthtoolModuleOutput(sfpOutput);
        if (module != null)
        {
            result.SfpModule = module;
            return;
        }

        // A copper port, a port with no module seated, or a gateway without ethtool all land
        // here. ethtool's own message is the most useful thing to show.
        result.SfpError = string.IsNullOrWhiteSpace(sfpOutput)
            ? "No transceiver data for this interface."
            : sfpOutput.Trim();
    }

    private void PopulateNeighbors(GatewayDiagnosticsResult result, IReadOnlyDictionary<string, string> sections)
    {
        var neighborOutput = sections.GetValueOrDefault(NeighborMarker, string.Empty);
        result.RawOutput[$"ip neigh show dev {result.Interface}"] = neighborOutput;

        var neighbors = GatewayDiagnosticsParser.ParseNeighborOutput(neighborOutput);
        if (neighbors.Count == 0)
        {
            result.NeighborError = string.IsNullOrWhiteSpace(neighborOutput)
                ? "No neighbors in the table for this interface."
                : neighborOutput.Trim();
            return;
        }

        foreach (var neighbor in neighbors)
        {
            if (!string.IsNullOrEmpty(neighbor.MacAddress))
                neighbor.Vendor = _ouiDatabase.GetVendor(neighbor.MacAddress);
        }
        result.Neighbors = neighbors;
    }

    /// <summary>
    /// Splits the combined output on the marker lines. Anything before the first marker is
    /// dropped: a login banner or MOTD lands there on some gateways.
    /// </summary>
    private static Dictionary<string, string> SplitSections(string output)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var buffer = new List<string>();

        void Flush()
        {
            if (current != null)
                sections[current] = string.Join("\n", buffer);
            buffer.Clear();
        }

        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed is AddressMarker or RouteMarker or SfpMarker or NeighborMarker)
            {
                Flush();
                current = trimmed;
                continue;
            }
            if (current != null) buffer.Add(line);
        }
        Flush();
        return sections;
    }
}
