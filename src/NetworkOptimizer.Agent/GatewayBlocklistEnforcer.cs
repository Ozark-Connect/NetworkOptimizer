using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NetworkOptimizer.AgentProtocol;

namespace NetworkOptimizer.Agent;

/// <summary>
/// Enforces Automatic Blocking on the gateway the agent runs on. The server pushes the full set;
/// it goes into hash:net ipsets (swapped in whole, each entry with its own timeout) that one chain
/// matches on new connections, jumped to from the top of FORWARD and INPUT. UniFi's provisioning
/// leaves foreign sets, chains, and jumps alone, so nothing here reprovisions the gateway; the jumps
/// are still re-checked, since a reboot or firmware upgrade clears everything.
/// </summary>
public sealed class GatewayBlocklistEnforcer
{
    /// <summary>The capability string, shared by agent and server.</summary>
    public const string Capability = "gateway-blocklist";

    private const string Chain = "NETOPT_BLOCK";
    private const string BlockV4 = "netopt_block_v4";
    private const string BlockV6 = "netopt_block_v6";
    private const string GuardV4 = "netopt_guard_v4";
    private const string GuardV6 = "netopt_guard_v6";
    private const string OutV4 = "netopt_out_v4";
    private const string OutV6 = "netopt_out_v6";
    private const int MaxElements = 262144;
    // ipset's largest timeout; anything longer is clamped to it.
    private const int MaxTtlSeconds = 2147483;
    private static readonly TimeSpan HookCheckEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly string _cachePath;
    private readonly IReadOnlyList<string> _guardHosts;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _gate = new();
    private GatewayBlocklistSet? _pending;
    // What is applied now; null until the first apply (from the cache or the server).
    private CachedSet? _applied;
    private (string Fingerprint, DateTime At)? _saved;
    private GatewayBlocklistStatus? _lastStatus;

    /// <summary>Live path: the current tunnel's TrySend, set while a tunnel is up.</summary>
    public volatile Func<AgentMessage, bool>? LiveSend;

    /// <param name="cachePath">Where the last set is kept, so a restart re-applies it before the tunnel is up.</param>
    /// <param name="guardHosts">Hosts (the server's) whose addresses are never blocked.</param>
    public GatewayBlocklistEnforcer(string cachePath, IReadOnlyList<string> guardHosts)
    {
        _cachePath = cachePath;
        _guardHosts = guardHosts;
    }

    /// <summary>Whether ipset and iptables are present and usable (Linux, root). Answered once at startup.</summary>
    public static bool Available()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            return Run("ipset", ["list", "-n"], null).Exit == 0
                && Run("iptables", ["-S", "INPUT"], null).Exit == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Takes the server's set; the worker applies the newest one.</summary>
    public void Update(GatewayBlocklistSet set)
    {
        lock (_gate) _pending = set;
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>Re-sends the last status, so a server that just connected learns the current state.</summary>
    public void Announce()
    {
        if (_lastStatus is { } status)
            LiveSend?.Invoke(new AgentMessage { GatewayBlocklistStatus = status });
    }

    /// <summary>Applies the cached set, then applies pushes and re-checks the jumps for the life of the agent.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (LoadCache() is { } cached)
        {
            Console.WriteLine(cached.Enabled
                ? $"Gateway blocklist: re-applying {cached.Entries.Count} cached entr(ies)"
                : "Gateway blocklist: off (cached)");
            Apply(cached);
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

            GatewayBlocklistSet? pending;
            lock (_gate)
            {
                pending = _pending;
                _pending = null;
            }
            try
            {
                if (pending != null)
                    Apply(CachedSet.From(pending, DateTime.UtcNow));
                else if (_applied is { Enabled: true } applied)
                    Recheck(applied);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Gateway blocklist: {ex.Message}");
            }
        }
    }

    private void Apply(CachedSet set)
    {
        if (!set.Enabled)
        {
            // Sites that never use gateway blocking are sent "off" on every resend: tear down once.
            if (_applied is not { Enabled: false })
            {
                Teardown();
                if (_applied != null) Console.WriteLine("Gateway blocklist: off, removed the agent's sets and rules");
            }
            _applied = set;
            SaveCache(set);
            Report(set.Fingerprint, true, false, 0, 0, null);
            return;
        }

        var now = DateTime.UtcNow;
        var (v4, v6, refused) = Split(set.Entries, now);
        var (out4, out6, refusedOut) = Split(set.Outbound ?? [], now);
        refused += refusedOut;
        var errors = new List<string>();
        try
        {
            FillSet(BlockV4, "inet", v4, withTimeout: true);
            FillSet(BlockV6, "inet6", v6, withTimeout: true);
            FillSet(OutV4, "inet", out4, withTimeout: true);
            FillSet(OutV6, "inet6", out6, withTimeout: true);
            var (guard4, guard6) = GuardAddresses();
            FillSet(GuardV4, "inet", guard4.Select(a => (a, 0)).ToList(), withTimeout: false);
            FillSet(GuardV6, "inet6", guard6.Select(a => (a, 0)).ToList(), withTimeout: false);
            EnsureChain("iptables", GuardV4, BlockV4, OutV4);
            EnsureChain("ip6tables", GuardV6, BlockV6, OutV6);
        }
        catch (CommandException ex)
        {
            errors.Add(ex.Message);
        }
        var hooked = EnsureHooks(out var inserted);

        var changed = _applied?.Fingerprint != set.Fingerprint;
        _applied = set;
        SaveCache(set);
        if (changed || errors.Count > 0)
            Console.WriteLine($"Gateway blocklist: {v4.Count} IPv4 and {v6.Count} IPv6 entr(ies) applied"
                + (out4.Count + out6.Count > 0 ? $", {out4.Count + out6.Count} blocked outbound" : "")
                + (refused > 0 ? $", {refused} refused (not public)" : "")
                + (inserted > 0 ? $", {inserted} jump(s) inserted" : "")
                + (errors.Count > 0 ? $"; failed: {string.Join("; ", errors)}" : ""));
        Report(set.Fingerprint, errors.Count == 0, hooked, v4.Count, v6.Count, errors.Count > 0 ? string.Join("; ", errors) : null);
    }

    // The sets and chain outlive a provision; a reboot or someone else's flush does not. Rebuild
    // what is missing and say so only when something was.
    private void Recheck(CachedSet applied)
    {
        var names = Run("ipset", ["list", "-n"], null).Stdout.Split('\n', StringSplitOptions.TrimEntries);
        if (!names.Contains(BlockV4) || !names.Contains(BlockV6)
            || Run("iptables", ["-S", Chain], null).Exit != 0 || Run("ip6tables", ["-S", Chain], null).Exit != 0)
        {
            Console.WriteLine("Gateway blocklist: sets or chain missing, re-applying");
            _applied = null;
            Apply(applied);
            return;
        }
        var hooked = EnsureHooks(out var inserted);
        if (inserted > 0)
            Console.WriteLine($"Gateway blocklist: re-inserted {inserted} jump(s) to {Chain}");
        if (inserted > 0 || _lastStatus?.HookPresent != hooked)
            Report(applied.Fingerprint, _lastStatus?.Ok ?? true, hooked, _lastStatus?.V4Entries ?? 0, _lastStatus?.V6Entries ?? 0, _lastStatus?.Error);
    }

    private void Report(string fingerprint, bool ok, bool hooked, int v4, int v6, string? error)
    {
        _lastStatus = new GatewayBlocklistStatus
        {
            Fingerprint = fingerprint,
            Ok = ok,
            HookPresent = hooked,
            V4Entries = v4,
            V6Entries = v6,
            Error = error ?? "",
        };
        Announce();
    }

    /// <summary>
    /// Splits entries by family, dropping anything that is not a public range: the server never sends
    /// those, and the agent refuses them on its own so a bad push cannot cut the site off.
    /// </summary>
    internal static (List<(string Address, int Ttl)> V4, List<(string Address, int Ttl)> V6, int Refused) Split(
        IEnumerable<CachedEntry> entries, DateTime now)
    {
        var v4 = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var v6 = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var refused = 0;
        foreach (var entry in entries)
        {
            var ttl = (int)Math.Min(MaxTtlSeconds, Math.Ceiling((entry.ExpiresAt - now).TotalSeconds));
            if (ttl <= 0) continue;
            if (NetworkOptimizer.Core.Helpers.BlocklistEntry.Canonical(entry.Address) is not { } canonical)
            {
                refused++;
                continue;
            }
            var target = canonical.IsV6 ? v6 : v4;
            target[canonical.Text] = Math.Max(ttl, target.GetValueOrDefault(canonical.Text));
        }
        return (v4.Select(kv => (kv.Key, kv.Value)).ToList(), v6.Select(kv => (kv.Key, kv.Value)).ToList(), refused);
    }

    // The server's own addresses: the agent never blocks the path it is managed over.
    private (List<string> V4, List<string> V6) GuardAddresses()
    {
        var v4 = new HashSet<string>();
        var v6 = new HashSet<string>();
        foreach (var host in _guardHosts)
        {
            IPAddress[] addresses;
            try
            {
                addresses = IPAddress.TryParse(host, out var literal) ? [literal] : Dns.GetHostAddresses(host);
            }
            catch (SocketException)
            {
                continue;
            }
            foreach (var a in addresses)
            {
                var ip = a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
                (ip.AddressFamily == AddressFamily.InterNetworkV6 ? v6 : v4).Add(ip.ToString());
            }
        }
        return (v4.ToList(), v6.ToList());
    }

    // Builds the new contents in a temporary set and swaps it in, so the live set is never half-filled.
    private static void FillSet(string name, string family, IReadOnlyList<(string Address, int Ttl)> entries, bool withTimeout)
    {
        var tmp = name + "_tmp";
        var type = $"hash:net family {family} hashsize 1024 maxelem {MaxElements}" + (withTimeout ? " timeout 0" : "");
        Run("ipset", ["destroy", tmp], null);
        var script = new StringBuilder();
        script.Append("create ").Append(name).Append(' ').Append(type).AppendLine(" -exist");
        script.Append("create ").Append(tmp).Append(' ').Append(type).AppendLine();
        foreach (var (address, ttl) in entries)
        {
            script.Append("add ").Append(tmp).Append(' ').Append(address);
            if (withTimeout) script.Append(" timeout ").Append(ttl);
            script.AppendLine(" -exist");
        }
        script.Append("swap ").Append(tmp).Append(' ').Append(name).AppendLine();
        script.Append("destroy ").Append(tmp).AppendLine();
        Check(Run("ipset", ["restore"], script.ToString()), $"ipset restore {name}");
    }

    // The chain: never the server, then new connections from a blocked source are dropped, then
    // anything to an outbound-blocked destination. Matching new connections only for sources leaves
    // replies to connections a LAN device opened alone, as the console policies do. Rewritten only
    // when it differs, so a steady set never touches the rules.
    private static void EnsureChain(string tool, string guardSet, string blockSet, string outSet)
    {
        string[] wanted =
        [
            $"-A {Chain} -m set --match-set {guardSet} src -j RETURN",
            $"-A {Chain} -m set --match-set {blockSet} src -m state --state NEW -j DROP",
            $"-A {Chain} -m set --match-set {outSet} dst -j DROP",
        ];
        var current = Run(tool, ["-S", Chain], null);
        if (current.Exit == 0)
        {
            var rules = current.Stdout.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.StartsWith("-A ", StringComparison.Ordinal)).ToList();
            if (rules.SequenceEqual(wanted)) return;
            Check(Run(tool, ["-F", Chain], null), $"{tool} -F {Chain}");
        }
        else
        {
            Check(Run(tool, ["-N", Chain], null), $"{tool} -N {Chain}");
        }
        foreach (var rule in wanted)
            Check(Run(tool, rule.Split(' '), null), $"{tool} {rule}");
    }

    // Jumps at the top of FORWARD and INPUT for both families. True when all four are in place.
    private static bool EnsureHooks(out int inserted)
    {
        inserted = 0;
        var all = true;
        foreach (var tool in new[] { "iptables", "ip6tables" })
        {
            foreach (var builtin in new[] { "FORWARD", "INPUT" })
            {
                if (Run(tool, ["-C", builtin, "-j", Chain], null).Exit == 0) continue;
                if (Run(tool, ["-I", builtin, "1", "-j", Chain], null).Exit == 0)
                    inserted++;
                else
                    all = false;
            }
        }
        return all;
    }

    private static void Teardown()
    {
        foreach (var tool in new[] { "iptables", "ip6tables" })
        {
            foreach (var builtin in new[] { "FORWARD", "INPUT" })
            {
                // A jump added twice by hand is removed in full.
                for (var i = 0; i < 8 && Run(tool, ["-D", builtin, "-j", Chain], null).Exit == 0; i++) { }
            }
            Run(tool, ["-F", Chain], null);
            Run(tool, ["-X", Chain], null);
        }
        foreach (var set in new[] { BlockV4, BlockV6, GuardV4, GuardV6, OutV4, OutV6 })
        {
            Run("ipset", ["destroy", set + "_tmp"], null);
            Run("ipset", ["destroy", set], null);
        }
    }

    private CachedSet? LoadCache()
    {
        try
        {
            return File.Exists(_cachePath) ? JsonSerializer.Deserialize<CachedSet>(File.ReadAllText(_cachePath), AgentJson.Options) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Gateway blocklist: cache unreadable ({ex.Message}), waiting for the server");
            return null;
        }
    }

    // The same set is re-sent every few minutes; rewriting the file each time only wears the flash.
    // Hourly is enough to keep the public list entries' renewed expiries on disk.
    private void SaveCache(CachedSet set)
    {
        if (_saved is { } saved && saved.Fingerprint == set.Fingerprint && DateTime.UtcNow - saved.At < TimeSpan.FromHours(1))
            return;
        _saved = (set.Fingerprint, DateTime.UtcNow);
        try
        {
            var tmp = _cachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(set, AgentJson.Options));
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Gateway blocklist: could not save the cache: {ex.Message}");
        }
    }

    private static void Check((int Exit, string Stdout, string Stderr) result, string what)
    {
        if (result.Exit != 0)
            throw new CommandException($"{what}: {result.Stderr.Trim()}");
    }

    private static (int Exit, string Stdout, string Stderr) Run(string file, IEnumerable<string> args, string? stdin)
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

    private sealed class CommandException(string message) : Exception(message);

    /// <summary>The applied set as kept on disk: expiries are absolute so a restart honors them.</summary>
    internal sealed record CachedSet(bool Enabled, string Fingerprint, List<CachedEntry> Entries, List<CachedEntry>? Outbound = null)
    {
        public static CachedSet From(GatewayBlocklistSet set, DateTime now) => new(
            set.Enabled,
            set.Fingerprint,
            set.Entries.Select(e => new CachedEntry(e.Address, now.AddSeconds(e.TtlSeconds))).ToList(),
            set.Outbound.Select(e => new CachedEntry(e.Address, now.AddSeconds(e.TtlSeconds))).ToList());
    }

    internal sealed record CachedEntry(string Address, DateTime ExpiresAt);
}
