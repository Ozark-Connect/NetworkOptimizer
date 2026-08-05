using System.Text;
using System.Text.RegularExpressions;
using NetworkOptimizer.Diagnostics.Models;

namespace NetworkOptimizer.Web.Services.Ssh;

/// <summary>
/// One WAN to read traffic control for: the names of its two shaper devices plus the rates UniFi
/// says it should be shaping at, which is what tells a missing shaper apart from a direction
/// UniFi was never asked to shape.
/// </summary>
/// <param name="WanName">The WAN's display name in UniFi Network.</param>
/// <param name="Interface">Data-path interface - "eth6", "eth6.100", "ppp0".</param>
/// <param name="IfbInterface">The ingress companion - "ifb" plus the data-path name.</param>
/// <param name="DownRateMbps">Configured Smart Queue download rate, if any.</param>
/// <param name="UpRateMbps">Configured Smart Queue upload rate, if any.</param>
public record ShaperProbeTarget(
    string WanName,
    string Interface,
    string IfbInterface,
    int? DownRateMbps,
    int? UpRateMbps);

/// <summary>
/// Builds and reads the gateway's traffic control readout for WANs with Smart Queues enabled.
/// Pure string work with no I/O, so the parsing is unit-testable against real gateway output.
///
/// Every interface is asked in a single marker-separated command, the same shape
/// <see cref="Monitoring.GatewayDiagnosticsService"/> uses: SSH session setup dominates the cost,
/// so one round trip covers every WAN whatever the count.
/// </summary>
public static partial class GatewayShaperProbe
{
    /// <summary>Prefix of the line that introduces one interface's section.</summary>
    public const string Marker = "###TC";

    /// <summary>
    /// The command reading every interface in one trip. Each section is introduced by
    /// "###TC &lt;interface&gt;", stderr is folded into stdout so "Cannot find device" arrives as
    /// section text rather than vanishing, and the chain ends on `true` so a non-zero exit from
    /// the last tc call isn't reported as a failed SSH run.
    /// </summary>
    public static string BuildCommand(IEnumerable<string> interfaces)
    {
        var command = new StringBuilder();
        foreach (var name in interfaces)
        {
            command.Append($"echo '{Marker} {name}'; tc class show dev {name} 2>&1; ");
        }
        command.Append("true");
        return command.ToString();
    }

    /// <summary>
    /// Reads the command output into one state per target. A target whose sections did not both
    /// come back is dropped rather than guessed at - a truncated readout must not read as a
    /// missing shaper.
    /// </summary>
    public static List<WanShaperState> Parse(string output, IEnumerable<ShaperProbeTarget> targets)
    {
        var sections = SplitSections(output);
        var states = new List<WanShaperState>();

        foreach (var target in targets)
        {
            if (!sections.TryGetValue(target.Interface, out var egress) ||
                !sections.TryGetValue(target.IfbInterface, out var ingress))
            {
                continue;
            }

            states.Add(new WanShaperState
            {
                WanName = target.WanName,
                Interface = target.Interface,
                IfbInterface = target.IfbInterface,
                DownRateMbps = target.DownRateMbps,
                UpRateMbps = target.UpRateMbps,
                Egress = ReadDevice(egress),
                Ingress = ReadDevice(ingress)
            });
        }

        return states;
    }

    /// <summary>
    /// Guards an interface name before it reaches the command line. Interface names are the only
    /// caller-supplied part of the command and they come from the controller, so they are checked
    /// rather than escaped.
    /// </summary>
    public static bool IsValidInterfaceName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && InterfaceNamePattern().IsMatch(name);

    /// <summary>
    /// What one section says about its device. An empty section is a real answer: an interface
    /// with no shaper and no classful qdisc lists nothing at all.
    /// </summary>
    private static TcDeviceState ReadDevice(string section)
    {
        if (DeviceMissingPattern().IsMatch(section))
            return new TcDeviceState { DeviceFound = false, HasRootHtb = false };

        return new TcDeviceState
        {
            DeviceFound = true,
            HasRootHtb = RootHtbPattern().IsMatch(section)
        };
    }

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
            if (trimmed.StartsWith(Marker + " ", StringComparison.Ordinal))
            {
                Flush();
                current = trimmed[(Marker.Length + 1)..].Trim();
                continue;
            }
            if (current != null) buffer.Add(line);
        }
        Flush();
        return sections;
    }

    /// <summary>
    /// The shaper actually running: "class htb 1:1 root rate 550Mbit ...". An interface left to
    /// the kernel's own multiqueue shows "class mq :1 root" and matches nothing here.
    /// </summary>
    [GeneratedRegex(@"^\s*class\s+htb\s+\S+\s+root\b", RegexOptions.Multiline)]
    private static partial Regex RootHtbPattern();

    /// <summary>iproute2's wording when the device does not exist on the box.</summary>
    [GeneratedRegex(@"Cannot find device|does not exist", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceMissingPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,30}$")]
    private static partial Regex InterfaceNamePattern();
}
