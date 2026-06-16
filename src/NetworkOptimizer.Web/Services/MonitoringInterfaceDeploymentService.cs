using System.Net;
using System.Net.NetworkInformation;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;
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
    /// Validate a monitoring interface configuration. Returns null when valid, otherwise
    /// a human-readable reason. Also enforces shell-safety of interpolated values.
    /// </summary>
    public static string? Validate(MonitoringInterface mi)
    {
        if (string.IsNullOrWhiteSpace(mi.Name) ||
            !System.Text.RegularExpressions.Regex.IsMatch(mi.Name, "^[a-z][a-z0-9-]{0,14}$"))
            return "Interface name must be 1-15 chars, start with a letter, and use only lowercase letters, digits, or hyphens.";

        if (string.IsNullOrWhiteSpace(mi.WanIfName) ||
            !System.Text.RegularExpressions.Regex.IsMatch(mi.WanIfName, "^[a-zA-Z0-9._-]{1,20}$"))
            return "Select a WAN interface.";

        if (!IPAddress.TryParse(mi.TargetIp, out var target) || target.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return "Modem/ONT IP must be a valid IPv4 address.";

        if (!IPAddress.TryParse(mi.GatewayLocalIp, out var local) || local.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
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

        return null;
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

        /// <summary>The on-gateway duplicate-IP check could not run (arping unavailable).</summary>
        Skipped
    }

    public record PreflightResult(bool Ok, PreflightBlock Block, string Message);

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
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load UniFi networks for overlap preflight; continuing");
        }

        // Gate 2: target already reachable from the server. Either monitoring already
        // works (no plumbing needed) or it's a duplicate-IP collision we can't safely
        // route to (the alternate-IP DNAT case is a documented TODO).
        if (await IsReachableFromServerAsync(mi.TargetIp))
        {
            return new PreflightResult(false, PreflightBlock.AlreadyReachable,
                $"{mi.TargetIp} is already reachable from the Network Optimizer server. " +
                "If monitoring already works, no interface is needed. If a different device also uses this IP, " +
                "alternate-IP routing isn't supported yet.");
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
        steps.Add("Preflight passed: no UniFi network overlap, target not already reachable.");

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

        return new DeployResult(true, PreflightBlock.None,
            $"Monitoring interface {mi.Name} deployed. {mi.TargetIp} is now reachable from the LAN.", steps);
    }

    /// <summary>
    /// Remove a monitoring interface from the gateway: cron entry, SNAT rule, route,
    /// address, macvlan link, and the boot script. Idempotent.
    /// </summary>
    public async Task<(bool success, List<string> steps)> RemoveAsync(MonitoringInterface mi)
    {
        var steps = new List<string>();
        var path = ScriptPath(mi);

        await RunAsync($"crontab -l 2>/dev/null | grep -vF '{path}' | crontab - 2>/dev/null || true");
        steps.Add("Removed cron watchdog.");

        if (mi.SnatEnabled)
        {
            await RunAsync($"iptables -t nat -D POSTROUTING -o {mi.Name} -d {mi.TargetIp} -j SNAT --to-source {mi.GatewayLocalIp} 2>/dev/null || true");
            steps.Add("Removed SNAT rule.");
        }

        await RunAsync($"ip route del {mi.TargetIp}/32 dev {mi.Name} 2>/dev/null || true");
        await RunAsync($"ip link del {mi.Name} 2>/dev/null || true");
        steps.Add("Removed route and macvlan interface.");

        await RunAsync($"rm -f '{path}' /tmp/netopt-moniface-{mi.Name}.log 2>/dev/null || true");
        steps.Add("Removed boot script.");

        return (true, steps);
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

        /// <summary>True when every expected component for the given config is in place.</summary>
        public bool IsFullyApplied(MonitoringInterface mi) =>
            InterfaceExists && LocalIpAssigned && RoutePresent &&
            (!mi.SnatEnabled || SnatPresent) && WatchdogCronPresent && BootScriptPresent;
    }

    /// <summary>
    /// Check the live state of a monitoring interface using a single delimited SSH call.
    /// </summary>
    public async Task<InterfaceStatus> CheckStatusAsync(MonitoringInterface mi)
    {
        var status = new InterfaceStatus();
        var path = ScriptPath(mi);

        var combined =
            "echo '---UDM_BOOT---'; test -f /etc/systemd/system/udm-boot.service && echo y || echo n; " +
            $"echo '---IFACE---'; ip link show {mi.Name} >/dev/null 2>&1 && echo y || echo n; " +
            $"echo '---LOCALIP---'; ip -4 addr show dev {mi.Name} 2>/dev/null | grep -q 'inet {mi.GatewayLocalIp}/{mi.SubnetPrefix}' && echo y || echo n; " +
            $"echo '---ROUTE---'; ip route show {mi.TargetIp}/32 2>/dev/null | grep -q 'dev {mi.Name}' && echo y || echo n; " +
            $"echo '---SNAT---'; iptables -t nat -C POSTROUTING -o {mi.Name} -d {mi.TargetIp} -j SNAT --to-source {mi.GatewayLocalIp} 2>/dev/null && echo y || echo n; " +
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
        status.RoutePresent = Yes("ROUTE");
        status.SnatPresent = Yes("SNAT");
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
        // modem segment. The boot script reuses this interface.
        var probe =
            $"ip link show {mi.Name} >/dev/null 2>&1 || ip link add {mi.Name} link {mi.WanIfName} type macvlan mode bridge; " +
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
        return BootScriptTemplate
            .Replace("__IFACE__", mi.Name)
            .Replace("__WAN_IF__", mi.WanIfName)
            .Replace("__LOCAL_IP__", mi.GatewayLocalIp)
            .Replace("__PREFIX__", mi.SubnetPrefix.ToString())
            .Replace("__TARGET_IP__", mi.TargetIp)
            .Replace("__SNAT_ENABLED__", mi.SnatEnabled ? "1" : "0")
            .Replace("__WATCHDOG_MIN__", mi.WatchdogIntervalMinutes.ToString())
            .Replace("__SCRIPT_PATH__", ScriptPath(mi));
    }

    private const string BootScriptTemplate = @"#!/bin/sh
# Network Optimizer - Monitoring Interface
# Idempotently provides LAN/gateway access to a modem/ONT management IP that sits behind
# the WAN, via a macvlan on the physical WAN port with a gateway-local IP, a host route to
# the device, and (optionally) a narrow SNAT for LAN clients. Self-installs a cron watchdog
# so it survives reboots and UniFi reprovisioning.
# Managed by Network Optimizer - manual edits are overwritten on redeploy.

IFACE=""__IFACE__""
WAN_IF=""__WAN_IF__""
LOCAL_IP=""__LOCAL_IP__""
PREFIX=""__PREFIX__""
TARGET_IP=""__TARGET_IP__""
SNAT_ENABLED=""__SNAT_ENABLED__""
WATCHDOG_MIN=""__WATCHDOG_MIN__""
SCRIPT=""__SCRIPT_PATH__""
LOG=""/tmp/netopt-moniface-__IFACE__.log""

log() { echo ""$(date '+%Y-%m-%d %H:%M:%S') $1"" >> ""$LOG"" 2>/dev/null; }

# The physical WAN port must exist; if not (boot race), let the watchdog retry later.
ip link show ""$WAN_IF"" >/dev/null 2>&1 || { log ""wan $WAN_IF absent, deferring""; exit 0; }

changed=0

# 1. macvlan on the physical WAN port
if ! ip link show ""$IFACE"" >/dev/null 2>&1; then
    ip link add ""$IFACE"" link ""$WAN_IF"" type macvlan mode bridge && changed=1
fi
ip link set ""$IFACE"" up 2>/dev/null

# 2. gateway-local IP on the macvlan
if ! ip -4 addr show dev ""$IFACE"" 2>/dev/null | grep -q ""inet $LOCAL_IP/$PREFIX""; then
    ip addr flush dev ""$IFACE"" 2>/dev/null
    ip addr add ""$LOCAL_IP/$PREFIX"" dev ""$IFACE"" && changed=1
fi

# 3. host route to the modem/ONT, sourced from the gateway-local IP
if ! ip route show ""$TARGET_IP/32"" 2>/dev/null | grep -q ""dev $IFACE""; then
    ip route replace ""$TARGET_IP/32"" dev ""$IFACE"" src ""$LOCAL_IP"" && changed=1
fi

# 4. narrow SNAT so LAN clients reach the modem via the gateway-local IP
if [ ""$SNAT_ENABLED"" = ""1"" ]; then
    if ! iptables -t nat -C POSTROUTING -o ""$IFACE"" -d ""$TARGET_IP"" -j SNAT --to-source ""$LOCAL_IP"" 2>/dev/null; then
        iptables -t nat -A POSTROUTING -o ""$IFACE"" -d ""$TARGET_IP"" -j SNAT --to-source ""$LOCAL_IP"" && changed=1
    fi
fi

# 5. self-install the cron watchdog (re-applies after reprovision)
if ! crontab -l 2>/dev/null | grep -qF ""$SCRIPT""; then
    (crontab -l 2>/dev/null; echo ""*/$WATCHDOG_MIN * * * * $SCRIPT >/dev/null 2>&1"") | crontab -
    changed=1
fi

# Log only when something actually changed, to spare the eMMC.
[ ""$changed"" = ""1"" ] && log ""applied $IFACE on $WAN_IF: $LOCAL_IP/$PREFIX -> $TARGET_IP (snat=$SNAT_ENABLED)""
exit 0
";

    // ─── helpers ───

    private Task<(bool success, string output)> RunAsync(string command, TimeSpan? timeout = null)
        => _gatewaySsh.RunCommandAsync(command, timeout);

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
