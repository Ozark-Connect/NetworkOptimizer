using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using NetworkOptimizer.AgentProtocol;

namespace NetworkOptimizer.Agent;

/// <summary>
/// Reads a UniFi gateway's built-in switch chip through swconfig: each port's link state every few
/// seconds, reported as a change with its time, and the chip's own error counters once a minute.
/// The gateway's port netdevs are VLAN views of one 10G conduit, so this is the only place a drop on
/// a gateway LAN port is visible. swconfig port N is the kernel's ethN (verified by negotiated speed on
/// the UCG-Fiber and UXG-Fiber, both realtek rtl8372); the server only trusts a port whose speed
/// agrees with UniFi's port table for that ifname.
/// </summary>
public sealed class SwitchPortFollower
{
    /// <summary>The hello capability an agent announces when the chip is readable.</summary>
    public const string Capability = "switch-port-links";

    private static readonly TimeSpan LinkEvery = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CountersEvery = TimeSpan.FromSeconds(60);
    private static readonly Regex PortsRe = new(@"ports:\s*(\d+)\s*\(cpu @ (\d+)\)", RegexOptions.Compiled);
    private static readonly Regex ModelRe = new(@"^\S+:\s*\S+\(([^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex LinkRe = new(@"link:(up|down|force-down)(?:\s+speed:(\d+)\S*)?(?:\s+(full|half)-duplex)?", RegexOptions.Compiled);

    private readonly Action<AgentMessage> _enqueue;
    private readonly Func<string, TimeSpan, string?> _run;
    private readonly Dictionary<int, (bool Up, int Speed, bool Full)> _last = new();
    private List<int> _ports = new();
    private string _model = "";

    /// <summary>Creates the follower; <paramref name="run"/> runs swconfig with the given arguments (tests substitute it).</summary>
    public SwitchPortFollower(Action<AgentMessage> enqueue, Func<string, TimeSpan, string?>? run = null)
    {
        _enqueue = enqueue;
        _run = run ?? RunSwconfig;
    }

    /// <summary>True when swconfig answers for switch0 with a port count.</summary>
    public static bool Available()
    {
        try { return File.Exists("/usr/sbin/swconfig") && Describe(RunSwconfig("dev switch0 help", TimeSpan.FromSeconds(5))).Ports.Count > 0; }
        catch (Exception) { return false; }
    }

    public Task RunAsync(CancellationToken ct) =>
        Task.Factory.StartNew(() => Loop(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Loop(CancellationToken ct)
    {
        var description = Describe(_run("dev switch0 help", TimeSpan.FromSeconds(5)));
        _ports = description.Ports;
        _model = description.Model;
        if (_ports.Count == 0) return;
        var nextCounters = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var batch = new SwitchPortLinkBatch { SwitchModel = _model };
                PollLinks(batch, DateTime.UtcNow);
                if (DateTime.UtcNow >= nextCounters)
                {
                    PollCounters(batch, DateTime.UtcNow);
                    nextCounters = DateTime.UtcNow + CountersEvery;
                }
                if (batch.Events.Count > 0 || batch.Counters.Count > 0)
                    _enqueue(new AgentMessage { SwitchPortLinks = batch });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Switch ports: poll failed ({ex.Message})");
            }
            ct.WaitHandle.WaitOne(LinkEvery);
        }
    }

    /// <summary>One link pass: an event for every port whose state changed since the last pass.</summary>
    internal void PollLinks(SwitchPortLinkBatch batch, DateTime now)
    {
        foreach (var port in _ports)
        {
            var output = _run($"dev switch0 port {port} get link", TimeSpan.FromSeconds(3));
            if (ParseLink(output) is not { } state) continue;
            var first = !_last.TryGetValue(port, out var prev);
            _last[port] = state;
            // The first pass is a baseline, not a change: a restart must not read as every port relinking.
            if (first || prev == state) continue;
            batch.Events.Add(new SwitchPortLinkEvent
            {
                IfName = $"eth{port}",
                Up = state.Up,
                SpeedMbps = state.Speed,
                FullDuplex = state.Full,
                TimestampUnixMs = new DateTimeOffset(now).ToUnixTimeMilliseconds(),
            });
        }
    }

    /// <summary>One counter pass over every port's MIB.</summary>
    internal void PollCounters(SwitchPortLinkBatch batch, DateTime now)
    {
        foreach (var port in _ports)
        {
            var mib = ParseMib(_run($"dev switch0 port {port} get mib", TimeSpan.FromSeconds(5)));
            if (mib.Count == 0) continue;
            var link = _last.TryGetValue(port, out var l) ? l : default;
            batch.Counters.Add(new SwitchPortCounters
            {
                IfName = $"eth{port}",
                RxCrcErrors = mib.GetValueOrDefault("rx_etherStatsCRCAlignErrors"),
                TxCrcErrors = mib.GetValueOrDefault("tx_etherStatsCRCAlignErrors"),
                InDiscards = mib.GetValueOrDefault("dot1dTpPortInDiscards"),
                OutDiscards = mib.GetValueOrDefault("ifOutDiscards"),
                LateCollisions = mib.GetValueOrDefault("dot3StatsLateCollisions"),
                SymbolErrors = mib.GetValueOrDefault("dot3StatsSymbolErrors"),
                RxPauseFrames = mib.GetValueOrDefault("dot3InPauseFrames"),
                TxPauseFrames = mib.GetValueOrDefault("dot3OutPauseFrames"),
                Up = link.Up,
                SpeedMbps = link.Speed,
                TimestampUnixMs = new DateTimeOffset(now).ToUnixTimeMilliseconds(),
            });
        }
    }

    /// <summary>The front-panel ports (every port but the CPU port) and the chip's description.</summary>
    internal static (List<int> Ports, string Model) Describe(string? help)
    {
        if (string.IsNullOrEmpty(help)) return (new(), "");
        var first = help.Split('\n').FirstOrDefault() ?? "";
        var m = PortsRe.Match(first);
        if (!m.Success) return (new(), "");
        var count = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var cpu = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var model = ModelRe.Match(first) is { Success: true } mm ? mm.Groups[1].Value : "";
        return (Enumerable.Range(0, count).Where(p => p != cpu).ToList(), model);
    }

    /// <summary>"port:2 link:up speed:2500baseT full-duplex ..." to its state; null when unreadable.</summary>
    internal static (bool Up, int Speed, bool Full)? ParseLink(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        var m = LinkRe.Match(output);
        if (!m.Success) return null;
        var up = m.Groups[1].Value == "up";
        var speed = up && m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        return (up, speed, up && m.Groups[3].Value == "full");
    }

    /// <summary>"name : value" lines to a counter map.</summary>
    internal static Dictionary<string, long> ParseMib(string? output)
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

    private static string? RunSwconfig(string args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo("swconfig", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi);
        if (process == null) return null;
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            return null;
        }
        return output.Result;
    }
}
