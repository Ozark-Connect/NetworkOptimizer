using System.Globalization;
using System.Text.RegularExpressions;

namespace NetworkOptimizer.AgentProtocol;

/// <summary>Parses <c>swconfig</c> output on a UniFi gateway's switch chip, for the On-Site Agent's switch port follower.</summary>
public static class SwitchChipOutput
{
    private static readonly Regex PortsRe = new(@"ports:\s*(\d+)\s*\(cpu @ (\d+)\)", RegexOptions.Compiled);
    private static readonly Regex ModelRe = new(@"^\S+:\s*\S+\(([^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex LinkRe = new(@"link:(up|down|force-down)(?:\s+speed:(\d+)\S*)?(?:\s+(full|half)-duplex)?", RegexOptions.Compiled);

    /// <summary>The front-panel ports (every port but the CPU port), the CPU port, and the chip, from <c>swconfig dev switch0 help</c>.</summary>
    public static (List<int> Ports, int Cpu, string Model) Describe(string? help)
    {
        if (string.IsNullOrEmpty(help)) return (new(), -1, "");
        var first = help.Split('\n').FirstOrDefault() ?? "";
        var m = PortsRe.Match(first);
        if (!m.Success) return (new(), -1, "");
        var count = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var cpu = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var model = ModelRe.Match(first) is { Success: true } mm ? mm.Groups[1].Value : "";
        return (Enumerable.Range(0, count).Where(p => p != cpu).ToList(), cpu, model);
    }

    /// <summary>"port:2 link:up speed:2500baseT full-duplex ..." to its state; null when unreadable.</summary>
    public static (bool Up, int Speed, bool Full)? ParseLink(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        var m = LinkRe.Match(output);
        if (!m.Success) return null;
        var up = m.Groups[1].Value == "up";
        var speed = up && m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        return (up, speed, up && m.Groups[3].Value == "full");
    }

    /// <summary>"name : value" lines of <c>port N get mib</c> to a counter map.</summary>
    public static Dictionary<string, long> ParseMib(string? output)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(output)) return result;
        foreach (var line in output.Split('\n'))
        {
            var i = line.IndexOf(':');
            if (i <= 0) continue;
            if (long.TryParse(line[(i + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                result[line[..i].Trim()] = v;
        }
        return result;
    }
}