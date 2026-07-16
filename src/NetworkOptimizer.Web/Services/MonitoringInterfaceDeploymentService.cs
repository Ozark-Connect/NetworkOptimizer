using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Deploys "monitoring interfaces" to the UniFi gateway: a macvlan on the physical WAN
/// port plus a host route and optional SNAT so the Network Optimizer server (a LAN
/// client) and browsers can reach an ONT/modem management IP that sits behind the WAN.
/// The boot script is self-contained and idempotent and installs a cron watchdog so it
/// survives reboots and UniFi reprovisioning - the same model as Adaptive SQM and
/// Performance Tweaks. All gateway access goes through <see cref="IGatewaySshService"/>.
/// </summary>
public class MonitoringInterfaceDeploymentService
{
    private readonly ILogger<MonitoringInterfaceDeploymentService> _logger;
    private readonly IGatewaySshService _gatewaySsh;
    private readonly IUdmBootService _udmBoot;
    private readonly UniFiConnectionService _connection;

    private const string OnBootDir = "/data/on_boot.d";

    public MonitoringInterfaceDeploymentService(
        ILogger<MonitoringInterfaceDeploymentService> logger,
        IGatewaySshService gatewaySsh,
        IUdmBootService udmBoot,
        UniFiConnectionService connection)
    {
        _logger = logger;
        _gatewaySsh = gatewaySsh;
        _udmBoot = udmBoot;
        _connection = connection;
    }

    private static string ScriptName(MonitoringInterface mi) => $"30-monitoring-iface-{mi.Name}.sh";
    private static string ScriptPath(MonitoringInterface mi) => $"{OnBootDir}/{ScriptName(mi)}";

    /// <summary>
    /// Mask for Network Optimizer's own reserved fwmark/table namespace - the top byte.
    /// Chosen to be clearly disjoint from UniFi's own internal multi-WAN policy routing,
    /// which (confirmed live against real UniFi gateway hardware) occupies roughly bits
    /// 16-22 (e.g. "fwmark 0x1a0000/0x7e0000 lookup 201.ppp0"). Matches the masked-xmark
    /// convention this project's own wansteer feature already uses for the same reason.
    /// </summary>
    public const string AliasMarkMask = "0xff000000";

    /// <summary>
    /// Highest Id this scheme can encode collision-free (the id must fit inside the single
    /// byte the mask covers) - see <see cref="AliasMark"/> for why. 254 concurrent aliased
    /// interfaces is far beyond anything this feature will ever see in practice.
    /// </summary>
    public const int MaxAliasableId = 254;

    /// <summary>
    /// Whether an id can be encoded by <see cref="AliasMark"/>/<see cref="AliasTable"/>. An id
    /// outside this range can NEVER have valid alias artifacts on the gateway - deploy is
    /// rejected for it (see <see cref="Validate"/>) and the encoding cannot represent it - so
    /// callers that only need to know whether gateway alias cleanup/probing applies (Remove,
    /// CheckStatus) should check this instead of calling the throwing derivations directly.
    /// </summary>
    private static bool IsAliasableId(int id) => id >= 1 && id <= MaxAliasableId;

    /// <summary>
    /// Fwmark for an aliased interface's traffic, derived from its Id (stable across edits).
    /// The id must live INSIDE the masked byte (shifted into bits 24-31), not added below it -
    /// an earlier version computed `0x01000000 + id`, which put the id in the UNMASKED lower 24
    /// bits. That failed two ways, found via live verification against real UniFi hardware:
    /// (1) `iptables -t mangle -j MARK --set-xmark value/mask` only writes the bits of `value`
    /// that fall inside `mask` - the id bits outside 0xff000000 were silently discarded, so
    /// every aliased interface ended up with the identical mark; (2) even where a match
    /// happened to have the right bits, the kernel's `-m mark --mark value/mask` match compares
    /// the packet's MASKED mark against the RAW, UNMASKED `value` (net/netfilter/xt_mark.c:
    /// `(skb->mark &amp; info->mask) == info->mark`) - so a `value` with any bit outside `mask` set
    /// can never match, permanently. Keeping the id entirely inside the masked byte (as done
    /// here) makes both problems moot: the value's bits outside the mask are already zero, so
    /// masking has no effect on it either way.
    /// </summary>
    public static string AliasMark(int id)
    {
        if (id < 1 || id > MaxAliasableId)
            throw new ArgumentOutOfRangeException(nameof(id), id,
                $"Alias id must be between 1 and {MaxAliasableId} (see {nameof(MaxAliasableId)}) to stay inside the masked byte.");
        return $"0x{(uint)id << 24:x}";
    }

    /// <summary>Private routing table id for an aliased interface, numerically equal to its mark.</summary>
    public static string AliasTable(int id)
    {
        if (id < 1 || id > MaxAliasableId)
            throw new ArgumentOutOfRangeException(nameof(id), id,
                $"Alias id must be between 1 and {MaxAliasableId} (see {nameof(MaxAliasableId)}) to stay inside the masked byte.");
        return ((uint)id << 24).ToString();
    }

    /// <summary>
    /// Stable, locally-administered unicast MAC for this interface's macvlan, derived from its
    /// Id (the same stable key as <see cref="AliasMark"/>). The boot script pins it as the
    /// macvlan's hardware address so the L2 identity survives gateway reboots and redeploys.
    /// Without a pinned address, <c>ip link add ... type macvlan</c> gets a fresh RANDOM MAC on
    /// every create - so each gateway reboot presents a NEW CPE MAC to the modem/ONT, forcing it
    /// to relearn its single downstream CPE and, on carrier gear that pins to one CPE (DOCSIS
    /// cable modems, a Starlink in Bypass Mode), transiently wedging its management stack. Applies
    /// to aliased and plain
    /// interfaces alike, so (unlike <see cref="AliasMark"/>) it has no id-range limit; any id,
    /// including 0 for a not-yet-persisted row, yields a valid deterministic address.
    /// First octet 0x02: bit 1 set marks it locally administered (never collides with a real
    /// vendor OUI), bit 0 clear keeps it unicast; the low 5 octets are hash bytes. Lowercase to
    /// match <c>/sys/class/net/*/address</c> for the boot script's migrate-if-different check.
    /// </summary>
    public static string StableMac(int id)
    {
        var h = SHA256.HashData(Encoding.ASCII.GetBytes($"netopt-moniface-mac-{id}"));
        return $"02:{h[1]:x2}:{h[2]:x2}:{h[3]:x2}:{h[4]:x2}:{h[5]:x2}";
    }

    /// <summary>
    /// Shell command for the on-gateway mark/table live preflight (Gate 4). Emits exactly one
    /// of <c>FREE</c>, <c>OURS</c>, or <c>FOREIGN</c> on stdout. It does not merely check whether
    /// our mark exists somewhere; it verifies that <em>every</em> <c>ip rule</c> line referencing
    /// our routing table also carries our exact fwmark/mask, AND that every mangle/nat rule
    /// carrying our mark/mask signature is one of the two exact rules the boot script itself
    /// would create (the mangle MARK rule on <paramref name="aliasIp"/>, the nat DNAT rule to
    /// <paramref name="targetIp"/>) - the boot script's own <c>cleanup_marked_rules()</c> deletes
    /// ANY mangle/nat rule matching the mark/mask signature regardless of ip-rule/table usage or
    /// which alias/target it references, so an extra foreign rule sharing our mark/mask (with a
    /// different alias or target IP) must not be missed just because a table reference also
    /// happens to look fine:
    /// <list type="bullet">
    /// <item><c>FREE</c> - no rule references our table id, and no mangle/nat rule carries our
    /// mark/mask (safe to create it).</item>
    /// <item><c>OURS</c> - every rule referencing our table id carries our mark, and every
    /// mangle/nat rule carrying our mark/mask is exactly our own expected rule (a re-apply,
    /// safe - a genuine previous deploy's mangle/nat rules are trusted along with it).</item>
    /// <item><c>FOREIGN</c> - either at least one rule uses our table id without our mark, or at
    /// least one mangle/nat rule carries our mark/mask that isn't our own expected rule (a
    /// foreign rule, or one referencing a different alias/target IP than this deploy).</item>
    /// </list>
    /// The "ours" counts match the EXACT canonical whole line <c>iptables-save</c> emits for the
    /// two rules the boot script creates (<c>grep -xF</c>: fixed-string, full-line - confirmed
    /// against real UniFi hardware, including the <c>/32</c> iptables-save always appends to a
    /// host destination). A substring match on just the destination would misclassify a foreign
    /// rule sharing our mark and destination but sitting in a different chain or carrying a
    /// different action - or one whose destination merely extends ours textually
    /// (<c>...100.19</c> contains <c>...100.1</c>) - as OURS, and the boot script's sweep would
    /// then delete it.
    /// </summary>
    public static string MarkRangePreflightCommand(int id, string aliasIp, string targetIp)
    {
        var mark = AliasMark(id);
        var table = AliasTable(id);
        return
            $"total=$(ip rule show | grep -c 'lookup {table}\\b'); " +
            $"ours=$(ip rule show | grep 'lookup {table}\\b' | grep -c '{mark}/{AliasMarkMask}'); " +
            $"mangle=$(iptables-save -t mangle 2>/dev/null | grep -c -- '{mark}/{AliasMarkMask}'); " +
            $"nat=$(iptables-save -t nat 2>/dev/null | grep -c -- '{mark}/{AliasMarkMask}'); " +
            $"ipt_total=$((mangle + nat)); " +
            $"mangle_ours=$(iptables-save -t mangle 2>/dev/null | grep -cxF -- '-A PREROUTING -d {aliasIp}/32 -j MARK --set-xmark {mark}/{AliasMarkMask}'); " +
            $"nat_ours=$(iptables-save -t nat 2>/dev/null | grep -cxF -- '-A PREROUTING -m mark --mark {mark}/{AliasMarkMask} -j DNAT --to-destination {targetIp}'); " +
            $"ipt_ours=$((mangle_ours + nat_ours)); " +
            $"if [ \"$total\" -eq 0 ] && [ \"$ipt_total\" -eq 0 ]; then echo FREE; " +
            $"elif [ \"$total\" -gt 0 ] && [ \"$total\" -ne \"$ours\" ]; then echo FOREIGN; " +
            $"elif [ \"$ipt_total\" -ne \"$ipt_ours\" ]; then echo FOREIGN; " +
            $"else echo OURS; fi";
    }

    /// <summary>
    /// Validate a monitoring interface configuration. Returns null when valid, otherwise
    /// a human-readable reason. Also enforces shell-safety of interpolated values.
    /// </summary>
    public static string? Validate(MonitoringInterface mi)
    {
        // \A...\z anchors (not ^...$): in .NET, $ also matches just before a trailing
        // newline, so "eth6\n" would pass and split the interpolated SSH command line.
        if (string.IsNullOrWhiteSpace(mi.Name) ||
            !System.Text.RegularExpressions.Regex.IsMatch(mi.Name, @"\A[a-z][a-z0-9-]{0,14}\z"))
            return "Interface name must be 1-15 chars, start with a letter, and use only lowercase letters, digits, or hyphens.";

        if (string.IsNullOrWhiteSpace(mi.WanIfName) ||
            !System.Text.RegularExpressions.Regex.IsMatch(mi.WanIfName, @"\A[a-zA-Z0-9._-]{1,20}\z"))
            return "Select a WAN interface.";

        if (mi.WanVlanId is int vlan && (vlan < 1 || vlan > 4094))
            return "VLAN ID must be between 1 and 4094.";

        // Require dotted-quad: IPAddress.TryParse accepts shorthand ("192.168.100" ->
        // 192.168.0.100), which would deploy a route/macvlan to the wrong address.
        if ((mi.TargetIp ?? "").Split('.').Length != 4 ||
            !IPAddress.TryParse(mi.TargetIp, out var target) || target.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return "Modem/ONT IP must be a valid IPv4 address.";

        if ((mi.GatewayLocalIp ?? "").Split('.').Length != 4 ||
            !IPAddress.TryParse(mi.GatewayLocalIp, out var local) || local.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return "Gateway-local IP must be a valid IPv4 address.";

        if (mi.SubnetPrefix < 8 || mi.SubnetPrefix > 30)
            return "Subnet prefix must be between /8 and /30.";

        if (mi.TargetIp == mi.GatewayLocalIp)
            return "Gateway-local IP must differ from the modem/ONT IP.";

        var subnet = $"{mi.TargetIp}/{mi.SubnetPrefix}";
        if (!NetworkUtilities.IsIpInSubnet(mi.GatewayLocalIp, NetworkAddress(subnet)))
            return "Gateway-local IP must be on the same subnet as the modem/ONT IP.";

        if (mi.WatchdogIntervalMinutes < 1 || mi.WatchdogIntervalMinutes > 60)
            return "Watchdog interval must be between 1 and 60 minutes.";

        if (mi.AliasIp != null)
        {
            if ((mi.AliasIp ?? "").Split('.').Length != 4 ||
                !IPAddress.TryParse(mi.AliasIp, out var alias) || alias.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return "Alias IP must be a valid IPv4 address.";

            if (mi.AliasIp == mi.TargetIp)
                return "Alias IP must differ from the modem/ONT IP.";

            if (mi.AliasIp == mi.GatewayLocalIp)
                return "Alias IP must differ from the gateway-local IP.";

            if (NetworkUtilities.IsIpInSubnet(mi.AliasIp, NetworkAddress($"{mi.TargetIp}/{mi.SubnetPrefix}")))
                return "Alias IP must be outside the modem/ONT's own subnet.";

            if (!mi.SnatEnabled)
                return "Alias IP requires SNAT to be enabled (LAN clients' replies need it to find their way back).";

            // Id == 0 means "not saved yet" (EF hasn't backfilled the real autoincrement id) -
            // this call runs both before the initial save (id unknown) and again from every
            // PreflightAsync/DeployAsync call once the id is real, so only enforce once it's
            // known. AliasMark/AliasTable enforce the same bound defensively at the point of
            // use; this is the friendly, pre-deploy version of that same constraint.
            if (mi.Id != 0 && mi.Id > MaxAliasableId)
                return $"This interface's id ({mi.Id}) exceeds {MaxAliasableId}, the highest this alias scheme can encode " +
                    "collision-free. Remove old, unused aliased interfaces to free up the range before deploying this one.";
        }

        return null;
    }

    /// <summary>
    /// Defense-in-depth guard for the methods that interpolate row values into root SSH
    /// commands without running the full <see cref="Validate"/> (remove/status). Shape-only -
    /// none of Validate's business rules (id range, SNAT requirement, subnet relations) - so
    /// rows that can no longer pass full validation (e.g. an aliased row whose id outgrew the
    /// mark range) can still be cleaned up and inspected, while a row whose strings wouldn't
    /// survive shell interpolation (a hand-edited database) is rejected before reaching SSH.
    /// The only current write path (the card's ValidateAndSaveAsync) always runs Validate
    /// first, so this should never fire in normal operation.
    /// </summary>
    private static void EnsureShellSafeStrings(MonitoringInterface mi)
    {
        static bool IsIpv4(string? s) => (s ?? "").Split('.').Length == 4 &&
            IPAddress.TryParse(s, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

        if (!System.Text.RegularExpressions.Regex.IsMatch(mi.Name ?? "", @"\A[a-z][a-z0-9-]{0,14}\z") ||
            !System.Text.RegularExpressions.Regex.IsMatch(mi.WanIfName ?? "", @"\A[a-zA-Z0-9._-]{1,20}\z") ||
            !IsIpv4(mi.TargetIp) || !IsIpv4(mi.GatewayLocalIp) ||
            (mi.AliasIp != null && !IsIpv4(mi.AliasIp)))
        {
            throw new InvalidOperationException(
                $"Monitoring interface {mi.Id} contains values that fail shell-safety validation; refusing to build SSH commands from it.");
        }
    }

    /// <summary>Reason a deploy preflight blocked, or <see cref="None"/> when clear to deploy.</summary>
    public enum PreflightBlock
    {
        None,
        InvalidInput,
        UniFiOverlap,
        AlreadyReachable,
        LocalIpInUse,
        GatewayUnreachable,

        /// <summary>Our reserved fwmark/routing-table id (derived from this interface's own
        /// database id) is already occupied by a foreign rule on the gateway. Distinct from
        /// <see cref="UniFiOverlap"/>, which is about the target subnet overlapping a UniFi network.</summary>
        MarkRangeConflict,

        /// <summary>The on-gateway duplicate-IP check could not run (arping unavailable).</summary>
        Skipped
    }

    public record PreflightResult(bool Ok, PreflightBlock Block, string Message);

    /// <summary>
    /// Gate 1: does TargetIp, GatewayLocalIp, or (when set) AliasIp fall inside a UniFi-managed
    /// network/VLAN subnet? A pure function over an explicit network list - rather than a live
    /// controller call - so it's unit-testable, the same split used by
    /// <see cref="UniFiConnectionService.ResolvePrimaryWanNetwork"/>. Returns null when clear.
    /// </summary>
    public static PreflightResult? CheckUniFiNetworkOverlap(MonitoringInterface mi, IReadOnlyList<NetworkInfo> networks)
    {
        foreach (var net in networks)
        {
            if (net.IsWan || !net.Enabled || string.IsNullOrEmpty(net.IpSubnet))
                continue;

            if (NetworkUtilities.IsIpInSubnet(mi.TargetIp, net.IpSubnet) ||
                NetworkUtilities.IsIpInSubnet(mi.GatewayLocalIp, net.IpSubnet))
            {
                return new PreflightResult(false, PreflightBlock.UniFiOverlap,
                    $"{mi.TargetIp} overlaps your \"{net.Name}\" network ({net.IpSubnet}). " +
                    "Monitoring this device via a dedicated interface isn't possible - renumber the modem or that network.");
            }

            // Checked separately from TargetIp/GatewayLocalIp above: an alias landing inside a
            // real UniFi LAN/VLAN subnet installs a mangle mark + DNAT rule that hijacks that
            // network's traffic, not just a routing conflict - so the fix is a different alias,
            // not renumbering the device. Gate 2's reachability ping alone won't reliably catch
            // this (an offline device or unleased DHCP address in that subnet sails through it).
            if (mi.AliasIp != null && NetworkUtilities.IsIpInSubnet(mi.AliasIp, net.IpSubnet))
            {
                return new PreflightResult(false, PreflightBlock.UniFiOverlap,
                    $"Alias IP {mi.AliasIp} overlaps your \"{net.Name}\" network ({net.IpSubnet}). " +
                    "Pick a different, unused alias IP outside your UniFi-managed networks.");
            }
        }

        return null;
    }

    /// <summary>
    /// Server-side preflight (no gateway changes): validates input, checks that the
    /// target subnet does not overlap a UniFi-managed network/VLAN, and checks that the
    /// target is not already reachable from the Network Optimizer server. The local-IP
    /// availability gate runs on the gateway during <see cref="DeployAsync"/>.
    /// </summary>
    public async Task<PreflightResult> PreflightAsync(MonitoringInterface mi, CancellationToken ct = default)
    {
        var invalid = Validate(mi);
        if (invalid != null)
            return new PreflightResult(false, PreflightBlock.InvalidInput, invalid);

        // Gate 1: overlap with a UniFi-managed network/VLAN. If the gateway already owns
        // this address space, a dedicated monitoring route isn't possible.
        try
        {
            var networks = await _connection.GetNetworksAsync(ct);
            var overlap = CheckUniFiNetworkOverlap(mi, networks);
            if (overlap != null)
                return overlap;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load UniFi networks for overlap preflight; continuing");
        }

        // Gate 2: reachability collision check. Non-alias mode: TargetIp must not already
        // be reachable, UNLESS it's this exact interface's own already-deployed rules (a
        // re-apply/edit of a working interface would otherwise always false-positive here,
        // since by then the target genuinely IS reachable - that's the point). Alias mode
        // inverts: TargetIp being reachable is expected; AliasIp must not be, with the same
        // carve-out for re-applying an already-aliased interface. The carve-out is based on
        // CheckStatusAsync confirming THIS interface's own signature is present - not just
        // "some route exists" - so a stale or foreign route can't falsely satisfy it.
        var checkIp = mi.AliasIp ?? mi.TargetIp;
        if (await IsReachableFromServerAsync(checkIp))
        {
            var status = await CheckStatusAsync(mi);
            if (!status.GatewayReachable)
            {
                return new PreflightResult(false, PreflightBlock.GatewayUnreachable,
                    $"{checkIp} is reachable, but the gateway itself couldn't be reached over SSH to confirm " +
                    "whether this is already your own deployment. Try again once the gateway is reachable.");
            }
            if (!status.IsFullyApplied(mi))
            {
                var message = mi.AliasIp != null
                    ? $"Alias IP {mi.AliasIp} is already reachable from the Network Optimizer server - pick a different, unused alias."
                    : $"{mi.TargetIp} is already reachable from the Network Optimizer server. " +
                      "If monitoring already works, no interface is needed. If a different device also uses this IP, " +
                      "set an Alias IP in Advanced so both can be monitored.";
                return new PreflightResult(false, PreflightBlock.AlreadyReachable, message);
            }
            // Reachable, and confirmed to be this interface's own already-deployed rules -
            // fall through to Ok (a re-apply/edit is allowed to proceed).
        }

        return new PreflightResult(true, PreflightBlock.None, "Ready to deploy.");
    }

    public record DeployResult(bool Success, PreflightBlock Block, string Message, List<string> Steps);

    /// <summary>
    /// Deploy (or re-apply) a monitoring interface: runs the server-side preflight,
    /// ensures udm-boot, performs the on-gateway local-IP availability gate, then writes
    /// and runs the idempotent boot script (which installs the cron watchdog).
    /// </summary>
    public async Task<DeployResult> DeployAsync(MonitoringInterface mi, CancellationToken ct = default)
    {
        var steps = new List<string>();

        var preflight = await PreflightAsync(mi, ct);
        if (!preflight.Ok)
            return new DeployResult(false, preflight.Block, preflight.Message, steps);
        // Alias mode inverts what "not already reachable" means (see Gate 2 above): TargetIp
        // being reachable via the OTHER WAN's device is expected there, so say what was
        // actually checked instead of a claim that reads as wrong in that mode.
        steps.Add(mi.AliasIp != null
            ? "Preflight passed: no UniFi network overlap, alias IP not already reachable."
            : "Preflight passed: no UniFi network overlap, target not already reachable.");

        // Ensure udm-boot so the interface survives reboots and firmware updates.
        if (!await _udmBoot.IsInstalledAsync())
        {
            steps.Add("Installing udm-boot (gateway boot persistence)...");
            var (udmOk, udmMsg) = await _udmBoot.InstallAsync();
            if (!udmOk)
                return new DeployResult(false, PreflightBlock.GatewayUnreachable, $"udm-boot install failed: {udmMsg}", steps);
            steps.Add("udm-boot installed.");
        }

        // Gate 3 (on-gateway): ensure the chosen gateway-local IP isn't already in use on
        // the modem subnet. Bring up a bare macvlan and run duplicate-address detection.
        var dupCheck = await CheckLocalIpAvailableAsync(mi);
        if (dupCheck == PreflightBlock.LocalIpInUse)
        {
            await RunAsync($"ip link del {mi.Name} 2>/dev/null || true");
            return new DeployResult(false, PreflightBlock.LocalIpInUse,
                $"{mi.GatewayLocalIp} is already in use on the modem subnet. Pick a different gateway-local IP.", steps);
        }
        steps.Add(dupCheck == PreflightBlock.None
            ? $"Gateway-local IP {mi.GatewayLocalIp} is free."
            : "Skipped duplicate-IP check (arping unavailable).");

        // Gate 4 (on-gateway, alias only): confirm our reserved mark/table range isn't
        // already occupied by something foreign, rather than assuming it's free. Only ever
        // checked for aliased interfaces - non-alias deploys never touch this range.
        if (mi.AliasIp != null)
        {
            var table = AliasTable(mi.Id);
            var rangeCheck = await RunAsync(MarkRangePreflightCommand(mi.Id, mi.AliasIp!, mi.TargetIp));
            if (rangeCheck.success && rangeCheck.output.Contains("FOREIGN"))
            {
                return new DeployResult(false, PreflightBlock.MarkRangeConflict,
                    $"Table {table} (derived from this interface's own id) is already used by something else on the gateway. " +
                    "If you recently edited this interface's alias or target IP, these are likely its own stale rules - " +
                    "click Remove, then Deploy, to clear and recreate them. Otherwise report it rather than deploying.", steps);
            }
            // Honest about the residual risk: unlike the boot script's other idempotent
            // checks, its cleanup_marked_rules() sweep for THIS range would happily flush or
            // overwrite a foreign rule it can't distinguish from a stale one of its own - this
            // check existing is what prevents that, so skipping it removes that protection
            // rather than the boot script somehow substituting for it.
            steps.Add(rangeCheck.success
                ? "Reserved mark/table range is free (or already ours)."
                : "Skipped mark/table range check (gateway unreachable) - deploying without it risks the " +
                  "boot script sweeping or overwriting a foreign rule in this range undetected.");
        }

        // Write and run the idempotent boot script.
        var script = GenerateBootScript(mi);
        var unix = script.Replace("\r\n", "\n").Replace("\r", "\n");
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(unix));
        var path = ScriptPath(mi);

        var write = await RunAsync($"mkdir -p {OnBootDir} && echo '{b64}' | base64 -d > '{path}' && chmod +x '{path}'");
        if (!write.success)
            return new DeployResult(false, PreflightBlock.GatewayUnreachable, $"Failed to write boot script: {write.output}", steps);
        steps.Add($"Wrote boot script {ScriptName(mi)}.");

        var run = await RunAsync($"'{path}'", TimeSpan.FromSeconds(30));
        if (!run.success)
            return new DeployResult(false, PreflightBlock.GatewayUnreachable, $"Boot script run failed: {run.output}", steps);
        steps.Add("Applied macvlan, route" + (mi.SnatEnabled ? ", SNAT" : "") + ", and cron watchdog.");

        var message = mi.AliasIp != null
            ? $"Monitoring interface {mi.Name} deployed. Point your ONT/CM/cellular monitor at {mi.AliasIp} " +
              $"(not {mi.TargetIp} directly - that address is shared with another WAN)."
            : $"Monitoring interface {mi.Name} deployed. {mi.TargetIp} is now reachable from the LAN.";
        return new DeployResult(true, PreflightBlock.None, message, steps);
    }

    /// <summary>
    /// Remove a monitoring interface from the gateway: cron entry, alias mark/DNAT/policy
    /// route (if aliased), SNAT rule, route, address, macvlan link, and the boot script.
    /// Idempotent. Failures are surfaced (not swallowed) for the alias artifacts - stale
    /// DNAT/mark/policy-route state is a worse failure mode than a stale SNAT rule.
    /// </summary>
    public async Task<(bool success, List<string> steps)> RemoveAsync(MonitoringInterface mi)
    {
        EnsureShellSafeStrings(mi);
        var steps = new List<string>();
        var path = ScriptPath(mi);
        var success = true;

        await RunAsync($"crontab -l 2>/dev/null | grep -vF '{path}' | crontab - 2>/dev/null || true");
        steps.Add("Removed cron watchdog.");

        if (mi.AliasIp != null && IsAliasableId(mi.Id))
        {
            var mark = AliasMark(mi.Id);
            var table = AliasTable(mi.Id);

            var cleanupMangle = await RunAsync(CleanupMarkedRulesCommand("mangle", mark, mi.Name));
            var cleanupNat = await RunAsync(CleanupMarkedRulesCommand("nat", mark, mi.Name));
            // ip rule del / ip route flush return nonzero both when they remove something
            // AND when there is nothing there (empty/absent rule or table) - the exit code
            // alone cannot tell "nothing to remove" (a normal, expected outcome on a never-
            // fully-deployed or already-removed interface) from a genuine failure. Guard each
            // with an existence check so "nothing there" is a clean success while a real
            // removal failure still surfaces (mirrors the boot script's own guarded del).
            var ruleDel = await RunAsync(
                $"if ip rule show 2>/dev/null | grep -qF 'fwmark {mark}/{AliasMarkMask} lookup {table}'; " +
                $"then ip rule del fwmark {mark}/{AliasMarkMask} lookup {table} 2>/dev/null; fi");
            var routeFlush = await RunAsync(
                $"if ip route show table {table} 2>/dev/null | grep -q .; " +
                $"then ip route flush table {table} 2>/dev/null; fi");

            if (!cleanupMangle.success || !cleanupNat.success || !ruleDel.success || !routeFlush.success)
            {
                success = false;
                steps.Add("WARNING: failed to fully remove alias mark/DNAT rules - check the gateway manually.");
            }
            else
            {
                steps.Add("Removed alias mark/DNAT rules and policy route table.");
            }
        }
        else if (mi.AliasIp != null)
        {
            // An id outside the aliasable range could never have been deployed (Validate
            // rejects it, and AliasMark/AliasTable can't encode it) - there is nothing on the
            // gateway to clean up for it, so skip straight to the base teardown below rather
            // than calling the throwing derivations just to compute a mark/table we'd never use.
            steps.Add("Alias id is outside the aliasable range - no alias mark/DNAT/policy-route artifacts existed to remove.");
        }

        if (mi.SnatEnabled)
        {
            await RunAsync($"iptables -w 5 -t nat -D POSTROUTING -o {mi.Name} -d {mi.TargetIp} -j SNAT --to-source {mi.GatewayLocalIp} 2>/dev/null || true");
            steps.Add("Removed SNAT rule.");
        }

        await RunAsync($"ip route del {mi.TargetIp}/32 dev {mi.Name} 2>/dev/null || true");
        await RunAsync($"ip link del {mi.Name} 2>/dev/null || true");
        steps.Add("Removed route and macvlan interface.");

        await RunAsync($"rm -f '{path}' /tmp/netopt-moniface-{mi.Name}.log 2>/dev/null || true");
        steps.Add("Removed boot script.");

        return (success, steps);
    }

    /// <summary>
    /// Shell command to delete every rule in the given iptables table carrying our alias
    /// mark/mask signature. A pipeline's exit status is its LAST command's - piping
    /// iptables-save | grep straight into "while ... | iptables -D" would report whether the
    /// while LOOP ran, not whether any individual deletion inside it failed, and a genuine
    /// deletion failure would be reported as success. Two fixes needed together: capture the
    /// candidate rules to a file first so the while loop reads via "&lt; file" redirection
    /// instead of a pipe (a piped-into while runs its body in a subshell, so a variable set
    /// inside it - our fail flag - would vanish the moment the pipe finishes; redirection
    /// keeps the loop in the current shell, so the flag survives), then exit 1 only if that
    /// flag was set. An empty match (nothing to delete) is a clean, expected success.
    /// </summary>
    internal static string CleanupMarkedRulesCommand(string iptablesTable, string mark, string ifaceName)
    {
        var tmp = $"/tmp/netopt-rm-{ifaceName}-{iptablesTable}.$$";
        return
            $"tmp='{tmp}'; iptables-save -t {iptablesTable} 2>/dev/null | grep -- '{mark}/{AliasMarkMask}' > \"$tmp\"; " +
            "fail=0; while IFS= read -r rule; do del=$(echo \"$rule\" | sed 's/^-A //'); " +
            $"iptables -w 5 -t {iptablesTable} -D $del 2>/dev/null || fail=1; done < \"$tmp\"; " +
            "rm -f \"$tmp\"; [ \"$fail\" = \"1\" ] && exit 1; exit 0";
    }

    /// <summary>Live status of a deployed monitoring interface on the gateway.</summary>
    public class InterfaceStatus
    {
        public bool GatewayReachable { get; set; }
        public bool UdmBootInstalled { get; set; }
        public bool InterfaceExists { get; set; }
        public bool LocalIpAssigned { get; set; }
        public bool RoutePresent { get; set; }
        public bool SnatPresent { get; set; }
        public bool WatchdogCronPresent { get; set; }
        public bool BootScriptPresent { get; set; }

        /// <summary>Alias-only: the per-interface policy route (private table) is present.</summary>
        public bool PolicyRoutePresent { get; set; }

        /// <summary>Alias-only: the mangle PREROUTING mark rule on the alias IP is present.</summary>
        public bool MarkRulePresent { get; set; }

        /// <summary>Alias-only: the nat PREROUTING DNAT rule (matched on our mark) is present.</summary>
        public bool DnatRulePresent { get; set; }

        /// <summary>
        /// True when every expected component for the given config is in place. For an
        /// aliased interface this additionally requires the fwmark/policy-route/DNAT
        /// signature - a stale or foreign route must not be able to satisfy this check,
        /// since callers (the Gate 2 "is this already ours" carve-out) trust it completely.
        /// </summary>
        public bool IsFullyApplied(MonitoringInterface mi)
        {
            var routeOk = mi.AliasIp == null ? RoutePresent : PolicyRoutePresent;
            var baseline = InterfaceExists && LocalIpAssigned && routeOk &&
                (!mi.SnatEnabled || SnatPresent) && WatchdogCronPresent && BootScriptPresent;

            if (mi.AliasIp == null)
                return baseline;

            return baseline && MarkRulePresent && DnatRulePresent;
        }
    }

    /// <summary>
    /// Check the live state of a monitoring interface using a single delimited SSH call.
    /// </summary>
    public async Task<InterfaceStatus> CheckStatusAsync(MonitoringInterface mi)
    {
        EnsureShellSafeStrings(mi);
        var status = new InterfaceStatus();
        var path = ScriptPath(mi);

        var aliasChecks = "";
        if (mi.AliasIp != null && IsAliasableId(mi.Id))
        {
            var mark = AliasMark(mi.Id);
            var table = AliasTable(mi.Id);
            aliasChecks =
                $"echo '---POLICYROUTE---'; ip route show table {table} {mi.TargetIp}/32 2>/dev/null | grep -q 'dev {mi.Name}' && " +
                $"ip route show table {table} {mi.TargetIp}/32 2>/dev/null | grep -q 'src {mi.GatewayLocalIp}' && echo y || echo n; " +
                $"echo '---MARKRULE---'; iptables -w 5 -t mangle -C PREROUTING -d {mi.AliasIp} -j MARK --set-xmark {mark}/{AliasMarkMask} 2>/dev/null && echo y || echo n; " +
                $"echo '---DNATRULE---'; iptables -w 5 -t nat -C PREROUTING -m mark --mark {mark}/{AliasMarkMask} -j DNAT --to-destination {mi.TargetIp} 2>/dev/null && echo y || echo n; ";
        }
        // An out-of-range id can never have valid alias artifacts on the gateway (see
        // IsAliasableId) - aliasChecks is left empty above so the section keys below simply
        // never appear in the output, and Yes(...) already reports absent keys as false.

        var combined =
            "echo '---UDM_BOOT---'; test -f /etc/systemd/system/udm-boot.service && echo y || echo n; " +
            $"echo '---IFACE---'; ip link show {mi.Name} >/dev/null 2>&1 && echo y || echo n; " +
            $"echo '---LOCALIP---'; ip -4 addr show dev {mi.Name} 2>/dev/null | grep -q 'inet {mi.GatewayLocalIp}/{mi.SubnetPrefix}' && echo y || echo n; " +
            $"echo '---ROUTE---'; ip route show {mi.TargetIp}/32 2>/dev/null | grep -q 'dev {mi.Name}' && echo y || echo n; " +
            $"echo '---SNAT---'; iptables -w 5 -t nat -C POSTROUTING -o {mi.Name} -d {mi.TargetIp} -j SNAT --to-source {mi.GatewayLocalIp} 2>/dev/null && echo y || echo n; " +
            aliasChecks +
            $"echo '---CRON---'; crontab -l 2>/dev/null | grep -qF '{path}' && echo y || echo n; " +
            $"echo '---SCRIPT---'; test -f '{path}' && echo y || echo n";

        var result = await RunAsync(combined);
        status.GatewayReachable = result.success;
        if (!result.success)
            return status;

        var s = ParseDelimited(result.output);
        bool Yes(string k) => s.TryGetValue(k, out var v) && v.Trim() == "y";
        status.UdmBootInstalled = Yes("UDM_BOOT");
        status.InterfaceExists = Yes("IFACE");
        status.LocalIpAssigned = Yes("LOCALIP");
        status.RoutePresent = mi.AliasIp == null && Yes("ROUTE");
        status.SnatPresent = Yes("SNAT");
        status.PolicyRoutePresent = mi.AliasIp != null && Yes("POLICYROUTE");
        status.MarkRulePresent = mi.AliasIp != null && Yes("MARKRULE");
        status.DnatRulePresent = mi.AliasIp != null && Yes("DNATRULE");
        status.WatchdogCronPresent = Yes("CRON");
        status.BootScriptPresent = Yes("SCRIPT");
        return status;
    }

    /// <summary>
    /// On-gateway check that the chosen gateway-local IP isn't already answering on the
    /// modem's L2 segment. Brings up a bare macvlan and sends a plain ARP probe (sender
    /// 0.0.0.0) for the candidate: a reply means the address is taken. We deliberately do
    /// NOT use <c>arping -D</c> - on UniFi OS that mode errors on a bare interface and
    /// returns a non-zero code even for free addresses (it would false-positive every
    /// deploy). A reply line ("reply from") is the only reliable signal here.
    /// Returns <see cref="PreflightBlock.LocalIpInUse"/> when taken,
    /// <see cref="PreflightBlock.Skipped"/> when arping is unavailable, otherwise
    /// <see cref="PreflightBlock.None"/> (free, or we couldn't create the probe interface -
    /// in which case the boot script will surface any real failure).
    /// </summary>
    private async Task<PreflightBlock> CheckLocalIpAvailableAsync(MonitoringInterface mi)
    {
        // Create the macvlan (idempotent) and bring it up so arping can transmit on the
        // modem segment. The boot script reuses this interface. On a VLAN WAN, the parent
        // is the VLAN subinterface, which we ensure exists first (same as the boot script).
        var parent = ParentInterface(mi);
        var ensureVlan = mi.WanVlanId is int vlan
            ? $"ip link show {parent} >/dev/null 2>&1 || ip link add link {mi.WanIfName} name {parent} type vlan id {vlan}; " +
              $"ip link set {parent} up 2>/dev/null; "
            : "";
        var probe =
            ensureVlan +
            $"ip link show {mi.Name} >/dev/null 2>&1 || ip link add {mi.Name} link {parent} address {StableMac(mi.Id)} type macvlan mode bridge; " +
            $"ip link set {mi.Name} up 2>/dev/null; " +
            $"if ! ip link show {mi.Name} >/dev/null 2>&1; then echo RESULT:NOIFACE; " +
            $"elif ! command -v arping >/dev/null 2>&1; then echo RESULT:SKIP; " +
            $"else out=$(arping -c 2 -w 2 -I {mi.Name} -S 0.0.0.0 {mi.GatewayLocalIp} 2>&1); " +
            $"if echo \"$out\" | grep -qi 'reply from'; then echo RESULT:INUSE; else echo RESULT:FREE; fi; fi";

        var result = await RunAsync(probe, TimeSpan.FromSeconds(15));
        if (!result.success)
            return PreflightBlock.None; // can't verify; let the apply proceed

        if (result.output.Contains("RESULT:INUSE"))
            return PreflightBlock.LocalIpInUse;
        if (result.output.Contains("RESULT:SKIP"))
            return PreflightBlock.Skipped;

        // RESULT:FREE, RESULT:NOIFACE, or anything unexpected -> don't block; let the
        // boot-script apply proceed and surface any genuine error.
        return PreflightBlock.None;
    }

    private async Task<bool> IsReachableFromServerAsync(string ip)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, 2000);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false; // can't ping (e.g. sandboxed) -> treat as not reachable, allow deploy
        }
    }

    /// <summary>
    /// Builds the self-contained, idempotent boot script. Uses token replacement rather
    /// than string interpolation so the shell's own <c>${...}</c> syntax stays readable.
    /// </summary>
    public static string GenerateBootScript(MonitoringInterface mi)
    {
        var aliased = mi.AliasIp != null;
        // The verbatim template inherits this source file's checked-out line endings
        // (CRLF on Windows with autocrlf) - normalize here so the generated script is
        // LF-only everywhere it's consumed, not just after DeployAsync's upload scrub.
        return BootScriptTemplate
            .Replace("\r\n", "\n")
            .Replace("__IFACE__", mi.Name)
            .Replace("__MAC__", StableMac(mi.Id))
            .Replace("__WAN_IF__", mi.WanIfName)
            .Replace("__VLAN_ID__", mi.WanVlanId?.ToString() ?? "")
            .Replace("__LOCAL_IP__", mi.GatewayLocalIp)
            .Replace("__PREFIX__", mi.SubnetPrefix.ToString())
            .Replace("__TARGET_IP__", mi.TargetIp)
            .Replace("__SNAT_ENABLED__", mi.SnatEnabled ? "1" : "0")
            .Replace("__ALIAS_ENABLED__", aliased ? "1" : "0")
            .Replace("__ALIAS_IP__", mi.AliasIp ?? "")
            .Replace("__MARK__", aliased ? AliasMark(mi.Id) : "")
            .Replace("__MASK__", AliasMarkMask)
            .Replace("__TABLE__", aliased ? AliasTable(mi.Id) : "")
            .Replace("__WATCHDOG_MIN__", mi.WatchdogIntervalMinutes.ToString())
            .Replace("__SCRIPT_PATH__", ScriptPath(mi));
    }

    private const string BootScriptTemplate = @"#!/bin/sh
# Network Optimizer - Monitoring Interface
# Idempotently provides LAN/gateway access to a modem/ONT management IP that sits behind
# the WAN, via a macvlan on the physical WAN port with a gateway-local IP, a host route to
# the device, and (optionally) a narrow SNAT for LAN clients. When ALIAS_ENABLED, also
# provides DNAT + fwmark policy routing so a second device sharing the same TARGET_IP on a
# different WAN can be reached via ALIAS_IP instead - see the ""Duplicate Reachable IP""
# design doc. Self-installs a cron watchdog so it survives reboots and UniFi reprovisioning.
# Managed by Network Optimizer - manual edits are overwritten on redeploy.

IFACE=""__IFACE__""
MAC=""__MAC__""
WAN_IF=""__WAN_IF__""
VLAN_ID=""__VLAN_ID__""
LOCAL_IP=""__LOCAL_IP__""
PREFIX=""__PREFIX__""
TARGET_IP=""__TARGET_IP__""
SNAT_ENABLED=""__SNAT_ENABLED__""
ALIAS_ENABLED=""__ALIAS_ENABLED__""
ALIAS_IP=""__ALIAS_IP__""
MARK=""__MARK__""
MASK=""__MASK__""
TABLE=""__TABLE__""
WATCHDOG_MIN=""__WATCHDOG_MIN__""
SCRIPT=""__SCRIPT_PATH__""
LOG=""/tmp/netopt-moniface-__IFACE__.log""

log() { echo ""$(date '+%Y-%m-%d %H:%M:%S') $1"" >> ""$LOG"" 2>/dev/null; }

# Remove any previous PREROUTING rules (given table) carrying this interface's mark
# signature before re-adding, keyed on the mark/mask (not on remembered IP values) so an
# edited alias/target IP never leaves an orphaned rule behind. Candidates are captured to a
# temp file first so the loop reads via ""< file"" redirection instead of a pipe: piping into
# a while loop runs its body in a subshell (busybox ash), so a failure recorded in $fail there
# would vanish the moment the pipe finished and never reach the caller's final exit check.
cleanup_marked_rules() {
    tmp=""/tmp/netopt-boot-rm-$1.$$""
    iptables-save -t ""$1"" 2>/dev/null | grep -- ""$MARK/$MASK"" > ""$tmp""
    while IFS= read -r rule; do
        del=$(echo ""$rule"" | sed 's/^-A //')
        iptables -w 5 -t ""$1"" -D $del 2>/dev/null || fail=1
    done < ""$tmp""
    rm -f ""$tmp""
}

# The physical WAN port must exist; if not (boot race), let the watchdog retry later.
ip link show ""$WAN_IF"" >/dev/null 2>&1 || { log ""wan $WAN_IF absent, deferring""; exit 0; }

changed=0
fail=0

# 0. resolve the macvlan parent. With a VLAN, the parent is the subinterface (e.g.
# eth6.100) so frames are tagged; UniFi creates it for a VLAN WAN, but if it's absent
# we add it ourselves. We never tear it down on removal - reprovision reaps it.
PARENT=""$WAN_IF""
if [ -n ""$VLAN_ID"" ]; then
    PARENT=""$WAN_IF.$VLAN_ID""
    if ! ip link show ""$PARENT"" >/dev/null 2>&1; then
        ip link add link ""$WAN_IF"" name ""$PARENT"" type vlan id ""$VLAN_ID"" && changed=1 || fail=1
    fi
    ip link set ""$PARENT"" up 2>/dev/null
fi

# 1. macvlan on the WAN parent (physical port, or VLAN subinterface), pinned to a stable,
# locally-administered MAC. Without a pinned address the kernel assigns a fresh RANDOM MAC on
# every create - so each gateway reboot presents a NEW CPE MAC to the modem/ONT, forcing it to
# relearn its single downstream CPE and, on carrier gear that pins to one CPE (DOCSIS cable
# modems, a Starlink in Bypass Mode), transiently wedging its management stack. A stable per-row
# MAC keeps the same L2 identity across reboots and redeploys.
if ! ip link show ""$IFACE"" >/dev/null 2>&1; then
    ip link add ""$IFACE"" link ""$PARENT"" address ""$MAC"" type macvlan mode bridge && changed=1 || fail=1
fi
# Migrate an interface created before MAC pinning (or with a stray random MAC) onto the stable
# MAC. /sys reports lowercase and $MAC is generated lowercase, so the compare is exact. A MAC
# can only be changed with the link down, so flap it - but only when it actually differs, to
# keep the ""log only when changed"" eMMC guard honest and avoid a needless CPE relearn each tick.
cur_mac=$(cat /sys/class/net/""$IFACE""/address 2>/dev/null)
if [ -n ""$MAC"" ] && [ ""$cur_mac"" != ""$MAC"" ]; then
    ip link set ""$IFACE"" down 2>/dev/null
    ip link set ""$IFACE"" address ""$MAC"" && changed=1 || fail=1
    ip link set ""$IFACE"" up 2>/dev/null
fi
ip link set ""$IFACE"" up 2>/dev/null

# 2. gateway-local IP on the macvlan. Checks specifically for the noprefixroute flag (not
# just the address) so an interface deployed before this flag was added gets migrated -
# otherwise it would keep looking ""already applied"" and never lose its phantom /24 route.
if ! ip -4 addr show dev ""$IFACE"" 2>/dev/null | grep ""inet $LOCAL_IP/$PREFIX "" | grep -q noprefixroute; then
    ip addr flush dev ""$IFACE"" 2>/dev/null
    # noprefixroute: without it, the kernel auto-installs a connected route for the WHOLE
    # subnet (e.g. 192.168.100.0/24) in the main table just from assigning this address -
    # silently hijacking every other address in that subnet (including a second WAN's device
    # sharing it) even though we only ever asked for a route to TARGET_IP specifically.
    ip addr add ""$LOCAL_IP/$PREFIX"" dev ""$IFACE"" noprefixroute && changed=1 || fail=1
fi

# 3. host route to the modem/ONT. Aliased interfaces skip the main-table route entirely
# (ambiguous when two WANs share TARGET_IP) and instead route via a private per-interface
# table, selected by an fwmark set on ALIAS_IP before DNAT rewrites it to TARGET_IP. All
# four alias artifacts - plus the absence of a stale main-table route left by a pre-alias
# deployment of this row - are checked together before touching any of them: tearing down
# and re-adding on every tick (rather than only when something's actually missing or wrong)
# would defeat the ""only log when changed"" eMMC guard below AND open a window, every
# single tick, where the mark rule is briefly absent - during which an in-flight flow to
# the alias can leak via the main table toward the OTHER WAN's device, the exact hijack
# this feature exists to prevent.
if [ ""$ALIAS_ENABLED"" = ""1"" ]; then
    if ip route show table ""$TABLE"" ""$TARGET_IP/32"" 2>/dev/null | grep -q ""dev $IFACE"" &&
       ip route show table ""$TABLE"" ""$TARGET_IP/32"" 2>/dev/null | grep -q ""src $LOCAL_IP"" &&
       ! ip route show ""$TARGET_IP/32"" 2>/dev/null | grep -q ""dev $IFACE"" &&
       ip rule show | grep -qF ""fwmark $MARK/$MASK lookup $TABLE"" &&
       iptables -w 5 -t mangle -C PREROUTING -d ""$ALIAS_IP"" -j MARK --set-xmark ""$MARK/$MASK"" 2>/dev/null &&
       iptables -w 5 -t nat -C PREROUTING -m mark --mark ""$MARK/$MASK"" -j DNAT --to-destination ""$TARGET_IP"" 2>/dev/null; then
        : # all four alias artifacts present and no stale main-table route - nothing to do this tick
    else
        cleanup_marked_rules mangle
        cleanup_marked_rules nat
        ip rule show | grep -q ""lookup $TABLE\b"" && ip rule del fwmark ""$MARK/$MASK"" lookup ""$TABLE"" 2>/dev/null
        ip route flush table ""$TABLE"" 2>/dev/null

        # A pre-alias plain deployment of this row left its host route in the MAIN table,
        # where it keeps capturing the shared TARGET_IP ahead of the other WAN's device -
        # the exact hijack alias mode exists to prevent. dev-scoped delete: another row may
        # legitimately route the same TARGET_IP via ITS OWN interface.
        if ip route show ""$TARGET_IP/32"" 2>/dev/null | grep -q ""dev $IFACE""; then
            ip route del ""$TARGET_IP/32"" dev ""$IFACE"" && changed=1 || fail=1
        fi

        ip route replace ""$TARGET_IP/32"" dev ""$IFACE"" src ""$LOCAL_IP"" table ""$TABLE"" && changed=1 || fail=1
        ip rule add fwmark ""$MARK/$MASK"" lookup ""$TABLE"" && changed=1 || fail=1
        iptables -w 5 -t mangle -A PREROUTING -d ""$ALIAS_IP"" -j MARK --set-xmark ""$MARK/$MASK"" && changed=1 || fail=1
        iptables -w 5 -t nat -A PREROUTING -m mark --mark ""$MARK/$MASK"" -j DNAT --to-destination ""$TARGET_IP"" && changed=1 || fail=1
    fi
elif ! ip route show ""$TARGET_IP/32"" 2>/dev/null | grep -q ""dev $IFACE""; then
    ip route replace ""$TARGET_IP/32"" dev ""$IFACE"" src ""$LOCAL_IP"" && changed=1 || fail=1
fi

# 4. narrow SNAT so LAN clients reach the modem via the gateway-local IP
if [ ""$SNAT_ENABLED"" = ""1"" ]; then
    if ! iptables -w 5 -t nat -C POSTROUTING -o ""$IFACE"" -d ""$TARGET_IP"" -j SNAT --to-source ""$LOCAL_IP"" 2>/dev/null; then
        iptables -w 5 -t nat -A POSTROUTING -o ""$IFACE"" -d ""$TARGET_IP"" -j SNAT --to-source ""$LOCAL_IP"" && changed=1 || fail=1
    fi
fi

# 5. self-install the cron watchdog (re-applies after reprovision)
if ! crontab -l 2>/dev/null | grep -qF ""$SCRIPT""; then
    (crontab -l 2>/dev/null; echo ""*/$WATCHDOG_MIN * * * * $SCRIPT >/dev/null 2>&1"") | crontab -
    changed=1
fi

# Log only when something actually changed, to spare the eMMC.
[ ""$changed"" = ""1"" ] && log ""applied $IFACE on $PARENT: $LOCAL_IP/$PREFIX -> $TARGET_IP (snat=$SNAT_ENABLED alias=$ALIAS_ENABLED)""

# A critical step failed above. Every step already ran to completion (this script never
# enables shell strict-exit-on-error and nothing above exits early on failure) so there is
# nothing left half-torn-down to finish - this exit is purely a status report to the caller
# (SSH exit code / cron), not an abort.
[ ""$fail"" = ""1"" ] && exit 1
exit 0
";

    // ─── helpers ───

    private Task<(bool success, string output)> RunAsync(string command, TimeSpan? timeout = null)
        => _gatewaySsh.RunCommandAsync(command, timeout);

    /// <summary>
    /// The macvlan's parent interface: the VLAN subinterface (e.g. "eth6.100") when a
    /// VLAN is configured, otherwise the bare physical WAN port.
    /// </summary>
    private static string ParentInterface(MonitoringInterface mi)
        => mi.WanVlanId is int vlan ? $"{mi.WanIfName}.{vlan}" : mi.WanIfName;

    /// <summary>Network (zero-host) address for a CIDR, e.g. "192.168.100.1/24" -> "192.168.100.0/24".</summary>
    private static string NetworkAddress(string cidr)
    {
        var (addr, prefix) = NetworkUtilities.ParseCidr(cidr);
        if (addr == null) return cidr;
        var bytes = addr.GetAddressBytes();
        var bits = prefix;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bits >= 8) { bits -= 8; continue; }
            var mask = bits > 0 ? (byte)(0xFF << (8 - bits)) : (byte)0;
            bytes[i] &= mask;
            bits = 0;
        }
        return $"{new IPAddress(bytes)}/{prefix}";
    }

    private static Dictionary<string, string> ParseDelimited(string output)
    {
        var sections = new Dictionary<string, string>();
        string? key = null;
        var value = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("---") && t.EndsWith("---") && t.Length > 6)
            {
                if (key != null) sections[key] = string.Join("\n", value);
                key = t.Trim('-');
                value.Clear();
            }
            else if (key != null)
            {
                value.Add(line);
            }
        }
        if (key != null) sections[key] = string.Join("\n", value);
        return sections;
    }
}
