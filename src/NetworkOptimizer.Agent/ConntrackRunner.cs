using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Monitoring.Conntrack;

namespace NetworkOptimizer.Agent;

/// <summary>
/// The gateway's conntrack sampler: reads <c>/proc/net/nf_conntrack</c> on the server-pushed
/// cadence (2s), differences per flow at the source (see <see cref="ConntrackAccountant"/>),
/// and ships per-client WAN window deltas two ways. Every window goes out live on the tunnel
/// (superseded by the next, so a lost one costs nothing), and ~6s aggregates ride the
/// store-and-forward <see cref="ResultBuffer"/> into the time series - acked, spooled,
/// replayed across outages - so the series matches the SNMP fast tier's grain while the live
/// split stays 2s-fresh. No per-flow records, destinations, or ports ever leave this process.
/// Runs for the life of the agent like the SNMP runner: config survives tunnel drops, and
/// <c>enabled=false</c> from the server is the fleet-wide kill switch.
/// </summary>
public sealed class ConntrackRunner
{
    private const string ProcPath = "/proc/net/nf_conntrack";
    private const string AcctSysctlPath = "/proc/sys/net/netfilter/nf_conntrack_acct";

    /// <summary>A sample pass over this budget stretches the cadence one step (self-protection
    /// on huge tables); a pass back under a third of it relaxes one step.</summary>
    private static readonly TimeSpan PassBudget = TimeSpan.FromMilliseconds(250);
    private const int MaxIntervalSeconds = 30;

    /// <summary>How long windows accumulate before one batch is enqueued for the time series.</summary>
    private static readonly TimeSpan AggregateEvery = TimeSpan.FromSeconds(6);

    private static readonly TimeSpan NeighborRefreshEvery = TimeSpan.FromSeconds(60);

    private readonly Action<AgentMessage> _enqueue;
    private readonly ConntrackAccountant _accountant = new();
    private volatile ConntrackConfig? _config;
    private int _stretchedInterval;

    private ConntrackHostView? _view;
    private DateTime _viewBuiltAt;

    // (ip, mac, ifname) -> summed window bytes/flows since the last aggregate flush.
    private readonly Dictionary<(string, string, string), (long Down, long Up, int Flows)> _pending = new();
    private DateTime _pendingSince = DateTime.UtcNow;
    private int _pendingWindowSeconds;

    /// <summary>Live path: the current tunnel's TrySend, set while a tunnel is up. Null drops
    /// the live batch - the buffer-borne aggregate still carries the bytes.</summary>
    public volatile Func<AgentMessage, bool>? LiveSend;

    public ConntrackRunner(Action<AgentMessage> enqueue)
    {
        _enqueue = enqueue;
    }

    /// <summary>
    /// Whether this host can account conntrack at all: byte counters enabled and the table
    /// readable. Answered once at startup for the hello's capability list - a permissions or
    /// module surprise downgrades to "not capable", never to a lie.
    /// </summary>
    public static bool SourceReadable()
    {
        try
        {
            if (!File.Exists(ProcPath)) return false;
            if (File.Exists(AcctSysctlPath) && File.ReadAllText(AcctSysctlPath).Trim() != "1") return false;
            using var reader = new StreamReader(ProcPath);
            reader.ReadLine();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void UpdateConfig(ConntrackConfig config)
    {
        var previous = _config;
        _config = config;
        if (previous == null || previous.Enabled != config.Enabled)
            Console.WriteLine(config.Enabled
                ? $"Received conntrack config: sampling every {config.IntervalSeconds}s"
                : "Conntrack accounting disabled by server config");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var config = _config;
            var interval = Math.Max(2, config?.IntervalSeconds is > 0 ? config!.IntervalSeconds : 5);
            if (config is { Enabled: true })
            {
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    SamplePass();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Conntrack sample pass failed: {ex.Message}");
                }
                stopwatch.Stop();

                // Self-protection: a pass over budget stretches the cadence, and says so via
                // window_seconds on the batches - the server never has to know why.
                if (stopwatch.Elapsed > PassBudget)
                    _stretchedInterval = Math.Min(MaxIntervalSeconds, Math.Max(interval, _stretchedInterval) * 2);
                else if (stopwatch.Elapsed < PassBudget / 3)
                    _stretchedInterval = 0;
            }
            var delay = _stretchedInterval > interval ? _stretchedInterval : interval;
            await Task.Delay(TimeSpan.FromSeconds(delay), ct);
        }
    }

    private DateTime _lastPassAt;

    private void SamplePass()
    {
        var view = RefreshHostView();
        List<ConntrackFlow> flows;
        using (var reader = new StreamReader(ProcPath))
            flows = ConntrackParser.Parse(reader);

        var wasSeeded = _accountant.Seeded;
        var deltas = _accountant.Account(flows, view);
        var now = DateTime.UtcNow;
        // The window is the ACTUAL gap since the previous pass, not the nominal interval: a
        // stretched cadence or a slow pass otherwise labels a long window short, and every rate
        // computed from it inflates by the ratio.
        var windowSeconds = _lastPassAt == default
            ? 0
            : (int)Math.Clamp(Math.Round((now - _lastPassAt).TotalSeconds), 1, 120);
        _lastPassAt = now;
        if (!wasSeeded || windowSeconds == 0)
        {
            _pendingSince = now;
            return;
        }

        // Live batch every window, straight onto the tunnel. Sent even with no clients: an empty
        // window says the feed is alive and every client's WAN is zero right now, which is what
        // lets the server treat "no entry" as measured-idle rather than as lost coverage.
        var live = new ConntrackSampleBatch
        {
            TimestampUnixMs = new DateTimeOffset(now).ToUnixTimeMilliseconds(),
            WindowSeconds = windowSeconds,
        };
        foreach (var d in deltas)
        {
            live.Clients.Add(new ConntrackClientSample
            {
                Ip = d.Ip,
                Mac = d.Mac,
                WanIfname = d.WanIfName,
                WanDownBytes = d.DownBytes,
                WanUpBytes = d.UpBytes,
                Flows = d.Flows,
            });
            var key = (d.Ip, d.Mac, d.WanIfName);
            var sum = _pending.TryGetValue(key, out var p) ? p : (0L, 0L, 0);
            _pending[key] = (sum.Item1 + d.DownBytes, sum.Item2 + d.UpBytes, Math.Max(sum.Item3, d.Flows));
        }
        LiveSend?.Invoke(new AgentMessage { ConntrackSamples = live });
        _pendingWindowSeconds += windowSeconds;

        if (now - _pendingSince >= AggregateEvery)
            FlushAggregate(now);
    }

    private void FlushAggregate(DateTime now)
    {
        // Enqueued even with no clients: the empty batch is the persisted statement that the
        // feed covered this window with nothing moving, which is what lets a totals reader
        // pick conntrack over DPI for an idle hour instead of reading it as a coverage gap.
        var batch = new ConntrackSampleBatch
        {
            TimestampUnixMs = new DateTimeOffset(now).ToUnixTimeMilliseconds(),
            WindowSeconds = _pendingWindowSeconds,
            Aggregated = true,
        };
        foreach (var ((ip, mac, ifName), sum) in _pending)
        {
            batch.Clients.Add(new ConntrackClientSample
            {
                Ip = ip,
                Mac = mac,
                WanIfname = ifName,
                WanDownBytes = sum.Down,
                WanUpBytes = sum.Up,
                Flows = sum.Flows,
            });
        }
        _enqueue(new AgentMessage { ConntrackSamples = batch });
        _pending.Clear();
        _pendingSince = now;
        _pendingWindowSeconds = 0;
    }

    /// <summary>
    /// The host's addresses, connected subnets, and neighbor table, rebuilt on a slow cadence:
    /// interfaces and neighbors move at human speed while flows move at packet speed. The
    /// neighbor map is read at sample time from the LIVE tables (v4 ARP from /proc, v6 NDP via
    /// `ip -j -6 neigh`) so IPv6 privacy addresses aggregate to their MAC while current.
    /// </summary>
    private ConntrackHostView RefreshHostView()
    {
        if (_view != null && DateTime.UtcNow - _viewBuiltAt < NeighborRefreshEvery)
            return _view;

        var view = new ConntrackHostView();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in ni.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (IPAddress.IsLoopback(address)) continue;
                    view.AddHostAddress(address, ni.Name);
                    if (unicast.PrefixLength > 0 && !address.IsIPv6LinkLocal)
                        view.AddConnectedSubnet(NetworkOf(address, unicast.PrefixLength), unicast.PrefixLength);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Conntrack host view build failed: {ex.Message}");
        }

        LoadArpTable(view);
        LoadIpv6Neighbors(view);

        _view = view;
        _viewBuiltAt = DateTime.UtcNow;
        return view;
    }

    private static IPAddress NetworkOf(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        var fullBytes = prefixLength / 8;
        var remaining = prefixLength % 8;
        for (var i = fullBytes + (remaining > 0 ? 1 : 0); i < bytes.Length; i++) bytes[i] = 0;
        if (remaining > 0 && fullBytes < bytes.Length)
            bytes[fullBytes] &= (byte)(0xFF << (8 - remaining));
        return new IPAddress(bytes);
    }

    private static void LoadArpTable(ConntrackHostView view)
    {
        try
        {
            if (!File.Exists("/proc/net/arp")) return;
            foreach (var line in File.ReadLines("/proc/net/arp").Skip(1))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // IP address, HW type, Flags, HW address, Mask, Device. Flags 0x2 = complete.
                if (parts.Length < 4 || parts[2] != "0x2") continue;
                if (IPAddress.TryParse(parts[0], out var ip))
                    view.AddNeighbor(ip, parts[3]);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ARP table read failed: {ex.Message}");
        }
    }

    private static void LoadIpv6Neighbors(ConntrackHostView view)
    {
        try
        {
            var psi = new ProcessStartInfo("ip", "-j -6 neigh show")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi);
            if (process == null) return;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000)) { try { process.Kill(); } catch { } return; }
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return;

            using var doc = JsonDocument.Parse(output);
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("dst", out var dst) || !entry.TryGetProperty("lladdr", out var lladdr)) continue;
                if (entry.TryGetProperty("state", out var state)
                    && state.ValueKind == JsonValueKind.Array
                    && state.EnumerateArray().Any(s => s.GetString() is "FAILED" or "INCOMPLETE")) continue;
                if (IPAddress.TryParse(dst.GetString(), out var ip) && lladdr.GetString() is { } mac)
                    view.AddNeighbor(ip, mac);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"IPv6 neighbor read failed: {ex.Message}");
        }
    }
}
