using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using NetworkOptimizer.AgentProtocol;

namespace NetworkOptimizer.Agent;

/// <summary>
/// Reads a UniFi gateway's built-in switch chip through swconfig: each port's link state every few
/// seconds, reported as a change with its time, and the chip's own error counters once a minute.
/// The gateway's port netdevs are VLAN views of one 10G conduit, so this is the only place a drop on
/// a gateway LAN port is visible. Which ethN a chip port is comes from the board's own UniFi OS board
/// file (switches[].edge-ports), never from the port number: the order differs by board (reversed on
/// the UCG-Fiber, UXG-Fiber, and UDR7; in order on the UDR5G and UDM Pro). A board with no file, or
/// whose file disagrees with swconfig on the CPU port, reports nothing.
/// </summary>
public sealed class SwitchPortFollower
{
    /// <summary>The hello capability an agent announces when the chip is readable.</summary>
    public const string Capability = "switch-port-links";

    private static readonly TimeSpan LinkEvery = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CountersEvery = TimeSpan.FromSeconds(60);

    private readonly Action<AgentMessage> _enqueue;
    private readonly Func<string, TimeSpan, string?> _run;
    private readonly Dictionary<int, (bool Up, int Speed, bool Full)> _last = new();
    private Dictionary<int, string> _ifNames = new();
    private string _model = "";

    private const string BoardInfoPath = "/etc/board.info";
    private const string BoardConfigDir = "/usr/share/ubios-udapi-server/config-board";

    /// <summary>Tests: a follower with a known chip port map.</summary>
    internal SwitchPortFollower(Action<AgentMessage> enqueue, Func<string, TimeSpan, string?> run, Dictionary<int, string> ifNames)
        : this(enqueue, run) => _ifNames = ifNames;

    /// <summary>Creates the follower; <paramref name="run"/> runs swconfig with the given arguments (tests substitute it).</summary>
    public SwitchPortFollower(Action<AgentMessage> enqueue, Func<string, TimeSpan, string?>? run = null)
    {
        _enqueue = enqueue;
        _run = run ?? RunSwconfig;
    }

    /// <summary>True when swconfig answers for switch0 and the board's own file maps its ports.</summary>
    public static bool Available()
    {
        try { return File.Exists("/usr/sbin/swconfig") && LoadPortMap(RunSwconfig) is { Count: > 0 }; }
        catch (Exception) { return false; }
    }

    /// <summary>Chip port to ethN for this box, or null when the board file is missing or disagrees with swconfig.</summary>
    private static Dictionary<int, string>? LoadPortMap(Func<string, TimeSpan, string?> run)
    {
        var description = SwitchChipOutput.Describe(run("dev switch0 help", TimeSpan.FromSeconds(5)));
        if (description.Ports.Count == 0 || !File.Exists(BoardInfoPath) || !Directory.Exists(BoardConfigDir)) return null;
        var file = SwitchChipBoard.BoardFile(File.ReadAllText(BoardInfoPath), Directory.GetFiles(BoardConfigDir, "*.json"));
        if (file == null) return null;
        return SwitchChipBoard.PortMap(File.ReadAllText(file), description.Cpu, description.Ports);
    }

    public Task RunAsync(CancellationToken ct) =>
        Task.Factory.StartNew(() => Loop(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Loop(CancellationToken ct)
    {
        _model = SwitchChipOutput.Describe(_run("dev switch0 help", TimeSpan.FromSeconds(5))).Model;
        if (LoadPortMap(_run) is not { } map)
        {
            Console.Error.WriteLine("Switch ports: no board file maps this switch chip, so no chip data is sent");
            return;
        }
        _ifNames = map;
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
        foreach (var (port, ifName) in _ifNames)
        {
            var output = _run($"dev switch0 port {port} get link", TimeSpan.FromSeconds(3));
            if (SwitchChipOutput.ParseLink(output) is not { } state) continue;
            var first = !_last.TryGetValue(port, out var prev);
            _last[port] = state;
            // The first pass is a baseline, not a change: a restart must not read as every port relinking.
            if (first || prev == state) continue;
            batch.Events.Add(new SwitchPortLinkEvent
            {
                IfName = ifName,
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
        foreach (var (port, ifName) in _ifNames)
        {
            var mib = SwitchChipOutput.ParseMib(_run($"dev switch0 port {port} get mib", TimeSpan.FromSeconds(5)));
            if (mib.Count == 0) continue;
            var link = _last.TryGetValue(port, out var l) ? l : default;
            batch.Counters.Add(new SwitchPortCounters
            {
                IfName = ifName,
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
