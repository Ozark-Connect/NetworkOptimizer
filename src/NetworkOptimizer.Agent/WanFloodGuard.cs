using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Monitoring.WanFlood;

namespace NetworkOptimizer.Agent;

/// <summary>
/// Enforces WAN Flood Protection on the gateway the agent runs on. The server pushes the whole config; this
/// installs the always-on hashlimit chains and their jumps, holds the drops and limits the detector adds
/// (each with a time to live), and re-checks everything every 10 seconds. A Smart Queues re-provision
/// recreates the WAN ingress qdisc and removes our tc filters; the re-check puts them back and reports it.
/// </summary>
/// <remarks>
/// Every change to the gateway goes through <see cref="_mutate"/>, so the worker and the detector never
/// interleave commands. Our tc filters use priorities 100-163 (drops) and 200-263 (measurements) and
/// nothing else; UniFi's redirect at 49152 and any qdisc we did not create are never touched.
/// </remarks>
public sealed class WanFloodGuard
{
    private static readonly TimeSpan HookCheckEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StatusRefreshEvery = TimeSpan.FromMinutes(1);

    private readonly string _cachePath;
    private readonly IReadOnlyList<string> _guardHosts;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _gate = new();
    private readonly object _mutate = new();
    private WanFloodConfig? _pending;

    // Applied state, under _mutate.
    private WanFloodConfig? _config;
    private bool _perSource = true;
    private bool _attribution;
    private bool _forwardAttribution;
    private readonly List<ActiveDrop> _drops = new();
    private readonly List<ArmedLimit> _limits = new();
    private readonly HashSet<string> _ownedQdiscs = new();
    private readonly HashSet<string> _knownWans = new();
    private readonly Dictionary<string, DateTime> _suppressed = new();
    private readonly Dictionary<string, string?> _wanIps = new();
    private readonly Dictionary<string, WanFloodWanStatus> _wanStatus = new();
    private string _reapplied = "";
    private string? _error;
    private WanFloodStatus? _lastStatus;
    private DateTime _lastStatusAt;
    private string? _armedTeardown;

    /// <summary>Live path: the current tunnel's TrySend, set while a tunnel is up.</summary>
    public volatile Func<AgentMessage, bool>? LiveSend;

    /// <summary>The detector's state, reported in the status.</summary>
    public volatile string DetectorState = "idle";

    /// <summary>Counters the detector reads, reported in the status.</summary>
    public (int Count, int Max, long L1Dropped, long L3Dropped, bool WanLocalCounter) Counters;

    /// <param name="cachePath">Where the applied config and drops are kept, so a restart re-applies them.</param>
    /// <param name="guardHosts">Hosts (the server's) whose addresses are never limited or dropped.</param>
    public WanFloodGuard(string cachePath, IReadOnlyList<string> guardHosts)
    {
        _cachePath = cachePath;
        _guardHosts = guardHosts;
    }

    private const string ProbeChain = "NETOPT_PROBE";
    private const string ProbeSet = "netopt_probe";
    private const string ProbeDevice = "netopt_probe";

    /// <summary>
    /// Whether the gateway can run WAN Flood Protection, proved by using each piece once and removing it
    /// (Linux, root). Answered once at startup. A list of loaded matches or module files cannot answer this:
    /// iptables loads a module only on first use, so a shipped-but-unloaded match reads as absent, and newer
    /// kernels build these in, so no .ko exists. So probe by attempt instead.
    /// </summary>
    public static bool Available()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            if (Probe() is { } failed)
            {
                Console.Error.WriteLine($"WAN flood protection: capability probe failed at {failed}");
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    // Runs each kernel feature once; returns the step that failed, or null when all pass. Cleanup in finally
    // removes everything whether a step failed or not.
    private static string? Probe()
    {
        var ownsDevice = false;
        try
        {
            foreach (var tool in new[] { "iptables", "ip6tables" })
            {
                if (Run(tool, ["-w", "-t", "mangle", "-N", ProbeChain], null).Exit != 0) return $"{tool} -N";
                // Match-only: proves addrtype, conntrack, and hashlimit load together. No target.
                var rule = new[] { "-w", "-t", "mangle", "-A", ProbeChain,
                    "-m", "addrtype", "--dst-type", "LOCAL", "-m", "conntrack", "--ctstate", "NEW",
                    "-m", "hashlimit", "--hashlimit-above", "1/sec", "--hashlimit-mode", "srcip",
                    "--hashlimit-name", "nof_probe" };
                if (Run(tool, rule, null).Exit != 0) return $"{tool} hashlimit/addrtype";
            }
            if (Run("iptables-restore", ["--version"], null).Exit != 0) return "iptables-restore";
            if (Run("ip6tables-restore", ["--version"], null).Exit != 0) return "ip6tables-restore";
            if (Run("ipset", ["create", ProbeSet, "hash:net", "-exist"], null).Exit != 0) return "ipset";

            // A dummy device, so the tc probe never touches a WAN netdev; lo as a fallback where dummy
            // is unavailable (lo has no ingress qdisc of its own, so ours adds and removes cleanly).
            var device = ProbeDevice;
            if (Run("ip", ["link", "add", ProbeDevice, "type", "dummy"], null).Exit == 0) ownsDevice = true;
            else device = "lo";
            // On the lo fallback the qdisc may already exist; add only if it does not, and remove only ours.
            var hadQdisc = Run("tc", ["qdisc", "show", "dev", device, "ingress"], null).Stdout.Contains("ingress ffff:", StringComparison.Ordinal);
            if (!hadQdisc && Run("tc", ["qdisc", "add", "dev", device, "handle", "ffff:", "ingress"], null).Exit != 0) return "tc ingress qdisc";
            // TEST-NET-2 to itself, so even on lo the filter matches nothing. Proves cls_u32 + act_gact.
            var filter = WanFloodRules.TcFilterAdd(device, WanFloodRules.DropPref(1),
                "198.51.100.1", "198.51.100.1", "udp", 9, drop: true);
            var ok = Run("tc", filter, null).Exit == 0;
            if (hadQdisc) Run("tc", WanFloodRules.TcFilterDel(device, WanFloodRules.DropPref(1)), null);
            else Run("tc", ["qdisc", "del", "dev", device, "ingress"], null);
            if (!ok) return "tc u32/gact filter";
            return null;
        }
        finally
        {
            foreach (var tool in new[] { "iptables", "ip6tables" })
            {
                Run(tool, ["-w", "-t", "mangle", "-F", ProbeChain], null);
                Run(tool, ["-w", "-t", "mangle", "-X", ProbeChain], null);
            }
            Run("ipset", ["destroy", ProbeSet], null);
            if (ownsDevice) Run("ip", ["link", "del", ProbeDevice], null);
        }
    }

    /// <summary>Takes the server's config; the worker applies the newest one.</summary>
    public void Update(WanFloodConfig config)
    {
        lock (_gate) _pending = config;
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>Re-sends the last status, so a server that just connected learns the current state.</summary>
    public void Announce()
    {
        if (_lastStatus is { } status)
            LiveSend?.Invoke(new AgentMessage { WanFloodStatus = status });
    }

    // ---- What the detector reads ----

    /// <summary>Whether the feature is on.</summary>
    public bool Enabled => _config is { Enabled: true };

    /// <summary>Whether the detector may install drops (enforce mode, reactive drops on).</summary>
    public bool Enforcing => _config is { Enabled: true, Mode: "enforce", ReactiveDrop: true };

    /// <summary>Whether the detector runs at all (detect mode reports what enforce would do).</summary>
    public bool Detecting => _config is { Enabled: true, ReactiveDrop: true };

    public IReadOnlyList<string> Wans => _config?.WanIfnames.Where(WanFloodRules.IsValidInterface).Distinct().ToList() ?? [];

    public string? WanIp(string wan) { lock (_mutate) return _wanIps.GetValueOrDefault(wan); }

    public WanFloodRules.Limits Limits => LimitsOf(_config);

    public FloodThresholds Thresholds => ThresholdsOf(_config);

    public TimeSpan DropTtl => TimeSpan.FromSeconds(Math.Max(60, _config?.DropTtlSeconds ?? 600));

    public TimeSpan MaxDropTtl => TimeSpan.FromSeconds(Math.Max(60, _config?.MaxDropTtlSeconds ?? 3600));

    public bool PerSource { get { lock (_mutate) return _perSource; } }

    /// <summary>The guard addresses (server, never-drop list) a candidate must not be.</summary>
    public bool IsGuarded(string address)
    {
        if (!IPAddress.TryParse(address, out var ip)) return true;
        foreach (var entry in GuardEntries())
        {
            if (IPNetwork.TryParse(entry, out var net) ? net.Contains(ip) : IPAddress.TryParse(entry, out var single) && single.Equals(ip))
                return true;
        }
        return false;
    }

    // ---- The worker ----

    /// <summary>Applies the cached config, then applies pushes and re-checks for the life of the agent.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        DisarmCleanup();
        if (LoadCache() is { } cached)
        {
            Console.WriteLine(cached.Config.Enabled
                ? $"WAN flood protection: re-applying the cached config ({cached.Drops.Count} drop(s))"
                : "WAN flood protection: off (cached)");
            lock (_mutate)
            {
                _ownedQdiscs.UnionWith(cached.OwnedQdiscs);
                _knownWans.UnionWith(cached.Wans);
                var now = DateTime.UtcNow;
                _drops.AddRange(cached.Drops.Where(d => d.Kind == "tc-test" || d.ExpiresAt > now).Select(d => d with { Pref = 0 }));
                _limits.AddRange(cached.Limits.Where(l => l.ExpiresAt > now));
            }
            TryApply(cached.Config);
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(HookCheckEvery, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            WanFloodConfig? pending;
            lock (_gate)
            {
                pending = _pending;
                _pending = null;
            }
            if (pending != null) TryApply(pending);
            else
            {
                try { Recheck(); }
                catch (Exception ex) { Console.Error.WriteLine($"WAN flood protection: re-check failed: {ex.Message}"); }
            }
            if (Enabled) ArmCleanup(DateTime.UtcNow);
        }
    }

    private void TryApply(WanFloodConfig config)
    {
        try
        {
            Apply(config);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WAN flood protection: {ex.Message}");
            lock (_mutate) _error = ex.Message;
            Report(force: true);
        }
    }

    private void Apply(WanFloodConfig config)
    {
        lock (_mutate)
        {
            var wasEnabled = _config is { Enabled: true };
            var previousWans = _knownWans.ToList();
            if (!config.Enabled)
            {
                if (wasEnabled || _config == null)
                {
                    Teardown(previousWans.Concat(config.WanIfnames), _ownedQdiscs);
                    DisarmCleanup();
                    if (wasEnabled) Console.WriteLine("WAN flood protection: off, removed the agent's chains, limits, and drops");
                }
                _config = config;
                _drops.Clear();
                _limits.Clear();
                _ownedQdiscs.Clear();
                _knownWans.Clear();
                _attribution = _forwardAttribution = false;
                _perSource = true;
                _written.Clear();
                _error = null;
                SaveCache();
                Report(force: true);
                return;
            }

            var changed = _config?.Fingerprint != config.Fingerprint;
            _config = config;
            var wans = Wans;
            _knownWans.UnionWith(wans);
            _error = null;

            var now = DateTime.UtcNow;
            // Off or detect: the detector may no longer hold drops or limits (the test drop is separate).
            if (!Enforcing)
            {
                foreach (var d in _drops.Where(d => d.Kind != "tc-test").ToList()) RemoveDrop(d);
                _limits.Clear();
            }

            FillGuardSets();
            EnsureChains(force: false);
            EnsureHooks(wans, out _);
            RemoveStaleHooks(wans);
            foreach (var release in config.Releases) Release(release, now);
            foreach (var d in _drops.Where(d => !wans.Contains(d.Wan)).ToList()) RemoveDrop(d);

            RefreshWanIps(wans);
            EnsureTestDrops(config.TestDrop, wans);
            EnsureTc(wans, out _);
            SaveCache();
            // The same config is re-sent every few minutes; say so only when it changed.
            if (changed)
                Console.WriteLine($"WAN flood protection: {config.Mode} on {string.Join(", ", wans)}"
                    + (config.TestDrop ? ", test drop held" : "")
                    + (_drops.Count > 0 ? $", {_drops.Count} drop(s)" : ""));
            Report(force: true);
        }
    }

    // A provision can drop our jumps; a Smart Queues re-provision drops our tc filters. Rebuild what is
    // missing, say what it was, and expire drops and limits.
    private void Recheck()
    {
        lock (_mutate)
        {
            if (_config is not { Enabled: true }) return;
            var wans = Wans;
            var repaired = new List<string>();
            if (EnsureChains(force: false)) repaired.Add("chain");
            EnsureHooks(wans, out var inserted);
            if (inserted > 0) repaired.Add("jump");
            if (RefreshWanIps(wans))
            {
                // A new WAN address: a flood aimed at the old one no longer arrives, so its drops go; the
                // test drop is re-added on the new address.
                foreach (var d in _drops.Where(d => d.Destination != _wanIps.GetValueOrDefault(d.Wan)).ToList()) RemoveDrop(d);
                EnsureTestDrops(_config.TestDrop, wans);
            }
            EnsureTc(wans, out var tcRepair);
            if (tcRepair != null) repaired.Add(tcRepair);
            ReadDropPackets();
            ExpireDrops(DateTime.UtcNow);

            _reapplied = repaired.Count > 0 ? string.Join(",", repaired) : "";
            if (repaired.Count > 0)
                Console.WriteLine($"WAN flood protection: re-check repaired {_reapplied}");
            Report(force: repaired.Count > 0);
            _reapplied = "";
        }
    }

    // ---- Detector hooks ----

    /// <summary>Turns the attribution tables on or off (IPv4 source and port, port forwards).</summary>
    public void SetAttribution(bool on, bool forward)
    {
        lock (_mutate)
        {
            if (_attribution == on && _forwardAttribution == forward) return;
            _attribution = on;
            _forwardAttribution = forward;
            EnsureChains(force: false);
        }
    }

    /// <summary>Removes or restores the per-source rules (a full table drops new sources whatever the target).</summary>
    public void SetPerSource(bool on)
    {
        lock (_mutate)
        {
            if (_perSource == on) return;
            _perSource = on;
            EnsureChains(force: false);
            Report(force: true);
        }
    }

    /// <summary>
    /// Counts each candidate's packets at WAN ingress for <paramref name="window"/> with continue filters,
    /// which hand every packet on to UniFi's redirect untouched.
    /// </summary>
    public async Task<List<FloodCandidate>> MeasureAsync(IReadOnlyList<FloodCandidate> candidates, TimeSpan window, CancellationToken ct)
    {
        var placed = new List<(FloodCandidate Candidate, int Pref)>();
        lock (_mutate)
        {
            var slotByWan = new Dictionary<string, int>();
            foreach (var c in candidates)
            {
                var slot = slotByWan.GetValueOrDefault(c.Wan) + 1;
                if (slot > WanFloodRules.MaxSlots) continue;
                if (!EnsureIngressQdisc(c.Wan)) continue;
                slotByWan[c.Wan] = slot;
                var pref = WanFloodRules.MeasurePref(slot);
                Run("tc", WanFloodRules.TcFilterDel(c.Wan, pref), null);
                if (Run("tc", WanFloodRules.TcFilterAdd(c.Wan, pref, c.Source, c.Destination, c.Protocol, c.Port, drop: false), null).Exit == 0)
                    placed.Add((c, pref));
            }
        }
        try
        {
            await Task.Delay(window, ct);
        }
        finally
        {
            lock (_mutate)
            {
                for (var i = 0; i < placed.Count; i++)
                {
                    var (c, pref) = placed[i];
                    var packets = WanFloodParsers.ParseTcFilterPackets(
                        Run("tc", ["-s", "filter", "show", "dev", c.Wan, "parent", "ffff:", "pref", pref.ToString()], null).Stdout);
                    Run("tc", WanFloodRules.TcFilterDel(c.Wan, pref), null);
                    placed[i] = (c with { Packets = packets }, pref);
                }
                foreach (var wan in placed.Select(p => p.Candidate.Wan).Distinct()) ReleaseIngressQdisc(wan);
            }
        }
        return placed.Select(p => p.Candidate).ToList();
    }

    /// <summary>
    /// Adds a reactive tc drop (enforce mode only). Returns what was added, or what would have been in
    /// detect mode (<c>WouldDrop</c>), or null when it is refused: suppressed by a release, a duplicate,
    /// guarded, or out of slots.
    /// </summary>
    public WanFloodDrop? AddDrop(FloodCandidate c, double share, string kind)
    {
        lock (_mutate)
        {
            if (_config is not { Enabled: true, ReactiveDrop: true }) return null;
            var now = DateTime.UtcNow;
            var key = DropKey(c.Wan, c.Protocol, c.Source, c.Port);
            if (_suppressed.TryGetValue(key, out var until) && until > now) return null;
            if (c.Source != null && IsGuarded(c.Source)) return null;
            var existing = _drops.FirstOrDefault(d => DropKey(d.Wan, d.Protocol, d.Source, d.Port) == key);
            if (existing != null) return existing.ToProto();
            var drop = new ActiveDrop(c.Wan, kind, c.Protocol, c.Source, c.Destination, c.Port, 0, now, now + DropTtl, share);
            if (!Enforcing) return drop.ToProto(wouldDrop: true);
            if (_drops.Count(d => d.Wan == c.Wan) >= WanFloodRules.MaxSlots) return null;
            _drops.Add(drop);
            EnsureTc([c.Wan], out _);
            // EnsureTc replaces the record with its placed copy.
            var placed = _drops.First(d => DropKey(d.Wan, d.Protocol, d.Source, d.Port) == key);
            if (placed.Pref == 0) { _drops.Remove(placed); ReleaseIngressQdisc(c.Wan); return null; }
            drop = placed;
            Console.WriteLine($"WAN flood protection: dropping {c.Protocol} {c.Source ?? "any"} -> {c.Destination}:{c.Port} on {c.Wan} "
                + $"({share:P0} of unsolicited drops) for {DropTtl.TotalMinutes:0} min");
            SaveCache();
            Report(force: true);
            return drop.ToProto();
        }
    }

    /// <summary>Arms a stateful per-port limit on new connections to the gateway (enforce mode only).</summary>
    public WanFloodDrop? ArmPortLimit(string wan, string protocol, int port, double share) =>
        Arm(new ArmedLimit("port-limit", wan, protocol, null, port, default, default, share));

    /// <summary>Arms the tighter limit on one port forward while the conntrack table is under pressure.</summary>
    public WanFloodDrop? ArmForwardCap(string wan, string protocol, string internalIp, int port, double share) =>
        Arm(new ArmedLimit("forward-cap", wan, protocol, internalIp, port, default, default, share));

    private WanFloodDrop? Arm(ArmedLimit limit)
    {
        lock (_mutate)
        {
            if (_config is not { Enabled: true, ReactiveDrop: true }) return null;
            var now = DateTime.UtcNow;
            limit = limit with { AppliedAt = now, ExpiresAt = now + DropTtl };
            var key = $"{limit.Kind}|{limit.Protocol}|{limit.InternalIp}|{limit.Port}";
            if (_suppressed.TryGetValue(DropKey(limit.Wan, limit.Protocol, limit.InternalIp, limit.Port), out var until) && until > now) return null;
            if (_limits.FirstOrDefault(l => $"{l.Kind}|{l.Protocol}|{l.InternalIp}|{l.Port}" == key) is { } existing)
                return existing.ToProto(false);
            if (!Enforcing) return limit.ToProto(wouldDrop: true);
            if (_limits.Count(l => l.Kind == limit.Kind) >= (limit.Kind == "forward-cap" ? 4 : 8)) return null;
            _limits.Add(limit);
            EnsureChains(force: false);
            Console.WriteLine($"WAN flood protection: limiting new {limit.Protocol} connections to "
                + $"{limit.InternalIp ?? "the gateway"}:{limit.Port} for {DropTtl.TotalMinutes:0} min");
            SaveCache();
            Report(force: true);
            return limit.ToProto(false);
        }
    }

    /// <summary>Extends the armed limits' time to live while the detector still sees the flood.</summary>
    public void ExtendLimits(bool forward)
    {
        lock (_mutate)
        {
            var now = DateTime.UtcNow;
            for (var i = 0; i < _limits.Count; i++)
            {
                var l = _limits[i];
                if ((l.Kind == "forward-cap") != forward) continue;
                if (now - l.AppliedAt < MaxDropTtl && l.ExpiresAt - now < DropTtl / 2)
                    _limits[i] = l with { ExpiresAt = now + DropTtl };
            }
        }
    }

    /// <summary>Everything the detector holds right now (drops and limits), for incidents.</summary>
    public List<WanFloodDrop> Actions()
    {
        lock (_mutate)
            return _drops.Where(d => d.Kind != "tc-test").Select(d => d.ToProto()).Concat(_limits.Select(l => l.ToProto(false))).ToList();
    }

    // ---- Chains and hooks ----

    // What each chain held right after the agent last wrote it: the wanted rules' fingerprint and iptables'
    // own printing of the chain. Comparing printed to printed needs no parsing of iptables' output format.
    private readonly Dictionary<string, (string Fingerprint, string Printed)> _written = new();

    // True when a chain had to be (re)written. The first call after a start always writes (no snapshot yet).
    private bool EnsureChains(bool force)
    {
        if (_config == null) return false;
        var limits = LimitsOf(_config);
        var state = new WanFloodRules.ChainState(_config.Mode == "enforce", _perSource, _attribution, _forwardAttribution,
            _limits.Where(l => l.Kind == "port-limit").Select(l => new WanFloodRules.PortLimit(l.Protocol, l.Port, PortLimitRate(l.Protocol, limits))).Distinct().ToList(),
            _limits.Where(l => l.Kind == "forward-cap" && l.InternalIp != null).Select(l => new WanFloodRules.ForwardCap(l.Protocol, l.InternalIp!, l.Port)).Distinct().ToList());
        var rewrote = false;
        foreach (var (tool, restore, v6) in new[] { ("iptables", "iptables-restore", false), ("ip6tables", "ip6tables-restore", true) })
        {
            foreach (var (chain, rules) in new[]
            {
                (WanFloodRules.Chain, WanFloodRules.Rules(v6, limits, state)),
                (WanFloodRules.ForwardChain, WanFloodRules.ForwardRules(v6, limits, state)),
            })
            {
                var key = $"{tool}|{chain}";
                var fingerprint = WanFloodRules.Fingerprint(rules);
                var current = Run(tool, ["-w", "-t", "mangle", "-S", chain], null);
                if (!force && current.Exit == 0 && _written.TryGetValue(key, out var written)
                    && written.Fingerprint == fingerprint && written.Printed == current.Stdout)
                    continue;
                Check(Run(restore, ["-w", "--noflush"], WanFloodRules.Restore(chain, rules)), $"{restore} {chain}");
                _written[key] = (fingerprint, Run(tool, ["-w", "-t", "mangle", "-S", chain], null).Stdout);
                rewrote = true;
            }
        }
        return rewrote;
    }

    // A per-port limit allows a tenth of the per-source limit to the whole port: new sessions from real
    // clients still get in, and the flood spends its effort in the stateful table instead of the CPU.
    private static int PortLimitRate(string protocol, WanFloodRules.Limits l) =>
        Math.Max(10, (protocol == "tcp" ? l.SynPerSource : l.UdpPerSource) / 10);

    // Jumps at the top of mangle PREROUTING and FORWARD for each WAN, both families. Records per WAN
    // whether all four are in place.
    private void EnsureHooks(IReadOnlyList<string> wans, out int inserted)
    {
        inserted = 0;
        foreach (var wan in wans)
        {
            var all = true;
            foreach (var tool in new[] { "iptables", "ip6tables" })
            {
                foreach (var (builtin, jump) in new[] { ("PREROUTING", WanFloodRules.Jump(wan)), ("FORWARD", WanFloodRules.ForwardJump(wan)) })
                {
                    if (Run(tool, ["-w", "-t", "mangle", "-C", builtin, .. jump], null).Exit == 0) continue;
                    if (Run(tool, ["-w", "-t", "mangle", "-I", builtin, "1", .. jump], null).Exit == 0) inserted++;
                    else all = false;
                }
            }
            _hooked[wan] = all;
        }
    }

    private readonly Dictionary<string, bool> _hooked = new();

    // Jumps for WANs no longer in the config.
    private static void RemoveStaleHooks(IReadOnlyList<string> wans)
    {
        foreach (var tool in new[] { "iptables", "ip6tables" })
        {
            foreach (var (builtin, chain) in new[] { ("PREROUTING", WanFloodRules.Chain), ("FORWARD", WanFloodRules.ForwardChain) })
            {
                var lines = Run(tool, ["-w", "-t", "mangle", "-S", builtin], null).Stdout
                    .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Where(l => l.EndsWith($"-j {chain}", StringComparison.Ordinal));
                foreach (var line in lines)
                {
                    var tokens = line.Split(' ');
                    var i = Array.IndexOf(tokens, "-i");
                    if (i >= 0 && i + 1 < tokens.Length && wans.Contains(tokens[i + 1])) continue;
                    Run(tool, ["-w", "-t", "mangle", "-D", .. tokens.Skip(1)], null);
                }
            }
        }
    }

    private IEnumerable<string> GuardEntries()
    {
        foreach (var host in _guardHosts)
        {
            IPAddress[] addresses;
            try { addresses = IPAddress.TryParse(host, out var literal) ? [literal] : Dns.GetHostAddresses(host); }
            catch (SocketException) { continue; }
            foreach (var a in addresses) yield return (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString();
        }
        foreach (var entry in _config?.NeverDrop ?? [])
            if (IPNetwork.TryParse(entry.Trim(), out _) || IPAddress.TryParse(entry.Trim(), out _))
                yield return entry.Trim();
    }

    private void FillGuardSets()
    {
        var v4 = new List<string>();
        var v6 = new List<string>();
        foreach (var entry in GuardEntries().Distinct())
            (entry.Contains(':') ? v6 : v4).Add(entry);
        FillSet(WanFloodRules.GuardV4, "inet", v4);
        FillSet(WanFloodRules.GuardV6, "inet6", v6);
    }

    // Builds the new contents in a temporary set and swaps it in, so the live set is never half-filled.
    private static void FillSet(string name, string family, IReadOnlyList<string> entries)
    {
        var tmp = name + "_tmp";
        var type = $"hash:net family {family} hashsize 64 maxelem 4096";
        Run("ipset", ["destroy", tmp], null);
        var script = new StringBuilder();
        script.Append("create ").Append(name).Append(' ').Append(type).AppendLine(" -exist");
        script.Append("create ").Append(tmp).Append(' ').Append(type).AppendLine();
        foreach (var entry in entries) script.Append("add ").Append(tmp).Append(' ').Append(entry).AppendLine(" -exist");
        script.Append("swap ").Append(tmp).Append(' ').Append(name).AppendLine();
        script.Append("destroy ").Append(tmp).AppendLine();
        Check(Run("ipset", ["restore"], script.ToString()), $"ipset restore {name}");
    }

    // ---- tc ----

    // True when a WAN's address changed.
    private bool RefreshWanIps(IReadOnlyList<string> wans)
    {
        var changed = false;
        foreach (var wan in wans)
        {
            var ip = WanFloodParsers.ParseInterfaceIpv4(Run("ip", ["-4", "-o", "addr", "show", "dev", wan], null).Stdout);
            if (_wanIps.TryGetValue(wan, out var old) && old != ip && old != null) changed = true;
            _wanIps[wan] = ip;
        }
        return changed;
    }

    private void EnsureTestDrops(bool on, IReadOnlyList<string> wans)
    {
        foreach (var d in _drops.Where(d => d.Kind == "tc-test" && (!on || !wans.Contains(d.Wan) || d.Destination != _wanIps.GetValueOrDefault(d.Wan))).ToList())
            RemoveDrop(d);
        if (!on) return;
        foreach (var wan in wans)
        {
            if (_wanIps.GetValueOrDefault(wan) is not { } ip || _drops.Any(d => d.Kind == "tc-test" && d.Wan == wan)) continue;
            _drops.Add(new ActiveDrop(wan, "tc-test", "udp", WanFloodRules.TestDropSource, ip, WanFloodRules.TestDropPort, 0,
                DateTime.UtcNow, DateTime.MaxValue, 0));
        }
    }

    // Puts every held drop on its WAN's ingress. Returns what had to be repaired: "qdisc" when an ingress
    // qdisc we need was gone, "tc" when filters were missing (a Smart Queues re-provision), else null.
    private void EnsureTc(IReadOnlyList<string> wans, out string? repaired)
    {
        repaired = null;
        foreach (var wan in wans)
        {
            var held = _drops.Where(d => d.Wan == wan).ToList();
            var (qdisc, prefs) = ReadIngress(wan);
            if (_ownedQdiscs.Contains(wan) && prefs.Contains(WanFloodRules.UniFiRedirectPref))
                _ownedQdiscs.Remove(wan);
            _wanStatus[wan] = new WanFloodWanStatus
            {
                Wan = wan,
                SmartQueues = qdisc && !_ownedQdiscs.Contains(wan),
                SqRedirectPresent = prefs.Contains(WanFloodRules.UniFiRedirectPref),
            };
            if (held.Count == 0)
            {
                ReleaseIngressQdisc(wan);
                continue;
            }
            if (!qdisc)
            {
                if (!EnsureIngressQdisc(wan)) continue;
                if (held.Any(d => d.Pref != 0)) repaired = "qdisc";
                prefs = [];
            }
            var used = new HashSet<int>(held.Where(d => d.Pref != 0 && prefs.Contains(d.Pref)).Select(d => d.Pref));
            foreach (var d in held)
            {
                if (d.Pref != 0 && prefs.Contains(d.Pref)) continue;
                if (d.Pref != 0 && repaired == null) repaired = "tc";
                var pref = d.Pref != 0 && !used.Contains(d.Pref) ? d.Pref : FreeDropPref(used);
                if (pref == 0) continue;
                Run("tc", WanFloodRules.TcFilterDel(wan, pref), null);
                var result = Run("tc", WanFloodRules.TcFilterAdd(wan, pref, d.Source, d.Destination, d.Protocol, d.Port, drop: true), null);
                if (result.Exit != 0)
                {
                    Console.Error.WriteLine($"WAN flood protection: tc filter on {wan} failed: {result.Stderr.Trim()}");
                    continue;
                }
                used.Add(pref);
                var index = _drops.IndexOf(d);
                _drops[index] = d with { Pref = pref, PacketsSeen = 0, History = [] };
            }
        }
    }

    private static int FreeDropPref(HashSet<int> used)
    {
        for (var slot = 1; slot <= WanFloodRules.MaxSlots; slot++)
            if (!used.Contains(WanFloodRules.DropPref(slot))) return WanFloodRules.DropPref(slot);
        return 0;
    }

    private static (bool Qdisc, HashSet<int> Prefs) ReadIngress(string wan)
    {
        var qdisc = Run("tc", ["qdisc", "show", "dev", wan, "ingress"], null).Stdout.Contains("ingress ffff:", StringComparison.Ordinal);
        var prefs = qdisc ? WanFloodParsers.ParseTcPrefs(Run("tc", ["filter", "show", "dev", wan, "parent", "ffff:"], null).Stdout) : [];
        return (qdisc, prefs);
    }

    // A WAN without Smart Queues has no ingress qdisc. One is added only while a filter of ours needs it.
    private bool EnsureIngressQdisc(string wan)
    {
        if (Run("tc", ["qdisc", "show", "dev", wan, "ingress"], null).Stdout.Contains("ingress ffff:", StringComparison.Ordinal))
            return true;
        if (Run("tc", ["qdisc", "add", "dev", wan, "handle", "ffff:", "ingress"], null).Exit != 0) return false;
        _ownedQdiscs.Add(wan);
        return true;
    }

    // Deletes an ingress qdisc we created once nothing of ours is on it, and only if nothing else is.
    private void ReleaseIngressQdisc(string wan)
    {
        if (!_ownedQdiscs.Contains(wan) || _drops.Any(d => d.Wan == wan)) return;
        var (qdisc, prefs) = ReadIngress(wan);
        if (!qdisc) { _ownedQdiscs.Remove(wan); return; }
        if (prefs.Any(p => !WanFloodRules.IsOurPref(p))) { _ownedQdiscs.Remove(wan); return; }
        if (prefs.Count > 0) return;
        Run("tc", ["qdisc", "del", "dev", wan, "ingress"], null);
        _ownedQdiscs.Remove(wan);
    }

    private void RemoveDrop(ActiveDrop drop, bool keep = false)
    {
        if (drop.Pref != 0) Run("tc", WanFloodRules.TcFilterDel(drop.Wan, drop.Pref), null);
        var index = _drops.IndexOf(drop);
        if (keep && index >= 0) _drops[index] = drop with { Pref = 0 };
        else
        {
            _drops.Remove(drop);
            ReleaseIngressQdisc(drop.Wan);
        }
    }

    private void ReadDropPackets()
    {
        var now = DateTime.UtcNow;
        for (var i = 0; i < _drops.Count; i++)
        {
            var d = _drops[i];
            if (d.Pref == 0) continue;
            var packets = WanFloodParsers.ParseTcFilterPackets(
                Run("tc", ["-s", "filter", "show", "dev", d.Wan, "parent", "ffff:", "pref", d.Pref.ToString()], null).Stdout);
            var history = d.History.Where(h => now - h.At <= TimeSpan.FromSeconds(70)).Append((now, packets)).ToList();
            _drops[i] = d with { PacketsSeen = packets, History = history };
        }
    }

    private void ExpireDrops(DateTime now)
    {
        var t = ThresholdsOf(_config);
        foreach (var d in _drops.Where(d => d.Kind != "tc-test").ToList())
        {
            var lastMinute = d.History.Count > 1 ? d.History[^1].Packets - d.History.First(h => now - h.At <= TimeSpan.FromSeconds(60)).Packets : 0;
            switch (FloodTtl.Decide(now, d.AppliedAt, d.ExpiresAt, lastMinute, MaxDropTtl, t))
            {
                case TtlAction.Extend:
                    _drops[_drops.IndexOf(d)] = d with { ExpiresAt = now + DropTtl };
                    break;
                case TtlAction.Remove:
                    Console.WriteLine($"WAN flood protection: drop of {d.Protocol} {d.Source ?? "any"} -> :{d.Port} on {d.Wan} expired");
                    RemoveDrop(d);
                    SaveCache();
                    break;
            }
        }
        var expired = _limits.RemoveAll(l => l.ExpiresAt <= now);
        if (expired > 0)
        {
            Console.WriteLine($"WAN flood protection: {expired} limit(s) expired");
            EnsureChains(force: false);
            SaveCache();
        }
        foreach (var key in _suppressed.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()) _suppressed.Remove(key);
    }

    private void Release(WanFloodRelease release, DateTime now)
    {
        var source = string.IsNullOrEmpty(release.Source) ? null : release.Source;
        var key = DropKey(release.Wan, release.Protocol, source, release.Port);
        if (_suppressed.ContainsKey(key)) return;
        var removed = false;
        foreach (var d in _drops.Where(d => d.Kind != "tc-test" && DropKey(d.Wan, d.Protocol, d.Source, d.Port) == key).ToList())
        {
            RemoveDrop(d);
            removed = true;
        }
        if (_limits.RemoveAll(l => DropKey(l.Wan, l.Protocol, l.InternalIp, l.Port) == key) > 0)
        {
            EnsureChains(force: false);
            removed = true;
        }
        _suppressed[key] = now + DropTtl;
        if (removed) Console.WriteLine($"WAN flood protection: released {release.Protocol} {source ?? "any"} -> :{release.Port} on {release.Wan}");
    }

    private static string DropKey(string wan, string protocol, string? source, int port) => $"{wan}|{protocol}|{source}|{port}";

    // ---- Status ----

    private void Report(bool force)
    {
        var status = new WanFloodStatus
        {
            Fingerprint = _config?.Fingerprint ?? "",
            Ok = _error == null,
            Error = _error ?? "",
            Reapplied = _reapplied,
            State = DetectorState,
            ConntrackCount = Counters.Count,
            ConntrackMax = Counters.Max,
            TableFull = !_perSource,
        };
        if (_config is { Enabled: true })
        {
            foreach (var wan in Wans)
            {
                var ws = _wanStatus.TryGetValue(wan, out var known) ? known.Clone() : new WanFloodWanStatus { Wan = wan };
                ws.HooksPresent = _hooked.GetValueOrDefault(wan);
                ws.WanLocalCounter = Counters.WanLocalCounter;
                ws.L1DroppedPackets = Counters.L1Dropped;
                ws.L3DroppedPackets = Counters.L3Dropped;
                status.Wans.Add(ws);
            }
            status.Drops.AddRange(_drops.Select(d => d.ToProto()));
            status.Drops.AddRange(_limits.Select(l => l.ToProto(false)));
        }
        var now = DateTime.UtcNow;
        var changed = _lastStatus == null || !Structural(_lastStatus).Equals(Structural(status));
        _lastStatus = status;
        if (force || changed || now - _lastStatusAt >= StatusRefreshEvery)
        {
            _lastStatusAt = now;
            Announce();
        }
    }

    // The status without its counters, so counters alone do not resend it before the refresh.
    private static WanFloodStatus Structural(WanFloodStatus s)
    {
        var copy = s.Clone();
        copy.ConntrackCount = 0;
        copy.ConntrackMax = 0;
        foreach (var w in copy.Wans) { w.L1DroppedPackets = 0; w.L3DroppedPackets = 0; }
        foreach (var d in copy.Drops) d.Packets = 0;
        return copy;
    }

    /// <summary>Sends a fresh status (the detector calls this when its state changes).</summary>
    public void ReportNow()
    {
        lock (_mutate) Report(force: true);
    }

    // ---- Teardown and dead-man ----

    private void Teardown(IEnumerable<string> wans, IEnumerable<string> owned) =>
        Run("/bin/sh", ["-c", WanFloodRules.Teardown(wans.Where(WanFloodRules.IsValidInterface), owned)], null);

    // Dead-man's switch: a systemd timer, re-armed while the agent runs, removes everything an hour after
    // the agent stops re-arming it. Mirrors the gateway blocklist's.
    private static readonly TimeSpan CleanupAfter = TimeSpan.FromHours(1);
    private static readonly TimeSpan CleanupRearmEvery = TimeSpan.FromMinutes(5);
    private const string CleanupUnit = "netopt-flood-cleanup";
    private DateTime _cleanupArmedAt = DateTime.MinValue;
    private bool _cleanupFailureLogged;

    private static void DisarmCleanup()
    {
        Run("systemctl", ["stop", CleanupUnit + ".timer", CleanupUnit + ".service"], null);
        Run("systemctl", ["reset-failed", CleanupUnit + ".timer", CleanupUnit + ".service"], null);
    }

    private void ArmCleanup(DateTime now)
    {
        string script;
        lock (_mutate) script = WanFloodRules.Teardown(_knownWans.Where(WanFloodRules.IsValidInterface), _ownedQdiscs);
        if (now - _cleanupArmedAt < CleanupRearmEvery && script == _armedTeardown) return;
        _cleanupArmedAt = now;
        _armedTeardown = script;
        DisarmCleanup();
        var result = Run("systemd-run", [
            $"--unit={CleanupUnit}", $"--on-active={(int)CleanupAfter.TotalSeconds}", "--timer-property=AccuracySec=1min",
            "--description=Network Optimizer: remove the WAN flood protection of an agent that stopped",
            "/bin/sh", "-c", script + "; echo 'WAN flood protection: agent stopped an hour ago, removed its chains and drops'"], null);
        if (result.Exit == 0) _cleanupFailureLogged = false;
        else if (!_cleanupFailureLogged)
        {
            _cleanupFailureLogged = true;
            Console.Error.WriteLine($"WAN flood protection: could not schedule the cleanup timer: {result.Stderr.Trim()}");
        }
    }

    // ---- Cache ----

    private CachedState? LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            var cached = JsonSerializer.Deserialize<CachedFile>(File.ReadAllText(_cachePath), AgentJson.Options);
            if (cached == null) return null;
            var config = WanFloodConfig.Parser.ParseFrom(Convert.FromBase64String(cached.Config));
            return new CachedState(config, cached.Drops ?? [], cached.Limits ?? [], cached.OwnedQdiscs ?? [], cached.Wans ?? []);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or FormatException or InvalidProtocolBufferException)
        {
            Console.Error.WriteLine($"WAN flood protection: cache unreadable ({ex.Message}), waiting for the server");
            return null;
        }
    }

    private void SaveCache()
    {
        if (_config == null) return;
        try
        {
            var file = new CachedFile(Convert.ToBase64String(_config.ToByteArray()),
                _drops.Select(d => d with { Pref = 0, PacketsSeen = 0, History = [] }).ToList(), _limits.ToList(),
                _ownedQdiscs.ToList(), _knownWans.ToList());
            var tmp = _cachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, AgentJson.Options));
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"WAN flood protection: could not save the cache: {ex.Message}");
        }
    }

    // ---- Config mapping ----

    private static WanFloodRules.Limits LimitsOf(WanFloodConfig? c) => new(
        Positive(c?.UdpNewPerSource, 2000), Positive(c?.UdpNewBurst, 4000),
        Positive(c?.SynNewPerSource, 500), Positive(c?.SynNewBurst, 1000),
        Positive(c?.ForwardedPortNew, 1000), Positive(c?.ForwardedPortBurst, 2000),
        Positive(c?.ForwardedPortCapped, 100), Positive(c?.Thresholds?.AttributionPps, 500));

    private static FloodThresholds ThresholdsOf(WanFloodConfig? c)
    {
        var d = new FloodThresholds();
        var t = c?.Thresholds;
        return d with
        {
            SoftirqPercent = Positive(t?.SoftirqPercent, d.SoftirqPercent),
            UnsolicitedPps = Positive(t?.UnsolicitedPps, d.UnsolicitedPps),
            ConntrackFillPercent = Positive(t?.ConntrackFillPercent, d.ConntrackFillPercent),
            ConntrackClearPercent = Positive(t?.ConntrackClearPercent, d.ConntrackClearPercent),
        };
    }

    private static int Positive(int? value, int fallback) => value is > 0 ? value.Value : fallback;

    // ---- Process ----

    private static void Check((int Exit, string Stdout, string Stderr) result, string what)
    {
        if (result.Exit != 0)
            throw new InvalidOperationException($"{what}: {result.Stderr.Trim()}");
    }

    internal static (int Exit, string Stdout, string Stderr) Run(string file, IEnumerable<string> args, string? stdin)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{file} did not start");
        if (stdin != null)
        {
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(CommandTimeout))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            return (-1, "", $"{file} timed out");
        }
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    // ---- State records ----

    internal sealed record ActiveDrop(string Wan, string Kind, string Protocol, string? Source, string Destination, int Port,
        int Pref, DateTime AppliedAt, DateTime ExpiresAt, double Share)
    {
        public long PacketsSeen { get; init; }
        public List<(DateTime At, long Packets)> History { get; init; } = [];

        public WanFloodDrop ToProto(bool wouldDrop = false) => new()
        {
            Wan = Wan,
            Kind = Kind,
            Protocol = Protocol,
            Source = Source ?? "",
            Destination = Destination,
            Port = Port,
            AppliedUnixMs = new DateTimeOffset(AppliedAt).ToUnixTimeMilliseconds(),
            ExpiresUnixMs = ExpiresAt == DateTime.MaxValue ? 0 : new DateTimeOffset(ExpiresAt).ToUnixTimeMilliseconds(),
            Packets = PacketsSeen,
            Share = Share,
            WouldDrop = wouldDrop,
        };
    }

    internal sealed record ArmedLimit(string Kind, string Wan, string Protocol, string? InternalIp, int Port,
        DateTime AppliedAt, DateTime ExpiresAt, double Share)
    {
        public WanFloodDrop ToProto(bool wouldDrop) => new()
        {
            Wan = Wan,
            Kind = Kind,
            Protocol = Protocol,
            Destination = InternalIp ?? "",
            Port = Port,
            AppliedUnixMs = new DateTimeOffset(AppliedAt).ToUnixTimeMilliseconds(),
            ExpiresUnixMs = new DateTimeOffset(ExpiresAt).ToUnixTimeMilliseconds(),
            Share = Share,
            WouldDrop = wouldDrop,
        };
    }

    private sealed record CachedState(WanFloodConfig Config, List<ActiveDrop> Drops, List<ArmedLimit> Limits,
        List<string> OwnedQdiscs, List<string> Wans);

    internal sealed record CachedFile(string Config, List<ActiveDrop>? Drops, List<ArmedLimit>? Limits,
        List<string>? OwnedQdiscs, List<string>? Wans);
}
