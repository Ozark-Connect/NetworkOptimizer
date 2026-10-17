using System.Text.RegularExpressions;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Triggers a backhaul re-scan on a mesh-child AP over SSH so it self-roams to the strongest
/// available parent. The parent set is decided by UniFi Network (Auto, or a constrained/pinned
/// parent); this service never chooses the parent - it only prompts the scan and reports the
/// before/after link. On-demand, re-runnable, idempotent.
/// Scoped per site: the injected <see cref="UniFiSshService"/> forwards to the current site's
/// device SSH credentials, so the scan runs against the right site's AP.
/// </summary>
public class MeshOptimizationService : IMeshOptimizationService
{
    private readonly UniFiSshService _ssh;
    private readonly Ssh.DeviceSshRouterRegistry _sshRouters;
    private readonly SiteContextService _siteContext;
    private readonly Licensing.LicenseStateService _licenseState;
    private readonly ILogger<MeshOptimizationService> _logger;

    /// <summary>
    /// After the scan, the AP evaluates results and roams on its own - which can take well over
    /// ten seconds. Poll the link this many times, waiting <see cref="RoamPollInterval"/> between
    /// reads, stopping early once it lands on a new parent.
    /// </summary>
    private const int RoamPollAttempts = 7;

    /// <summary>Delay between post-scan link reads (also the initial settle after the scan).</summary>
    private static readonly TimeSpan RoamPollInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The STA backhaul iface is interpolated into a shell command, so its format is locked down
    /// to the known UniFi pattern (e.g. "vwiresta7") to prevent command injection.
    /// </summary>
    private static readonly Regex ValidStaIface = new(@"^vwiresta\d+$", RegexOptions.Compiled);

    public MeshOptimizationService(
        UniFiSshService ssh,
        Ssh.DeviceSshRouterRegistry sshRouters,
        SiteContextService siteContext,
        Licensing.LicenseStateService licenseState,
        ILogger<MeshOptimizationService> logger)
    {
        _ssh = ssh;
        _sshRouters = sshRouters;
        _siteContext = siteContext;
        _licenseState = licenseState;
        _logger = logger;
    }

    /// <summary>
    /// Run a backhaul re-scan on the given mesh-child AP and report whether it moved to a
    /// stronger parent.
    /// </summary>
    /// <param name="host">The AP's IP/host for SSH.</param>
    /// <param name="iface">The STA backhaul interface (e.g. "vwiresta7").</param>
    /// <param name="apName">AP display name, used only for log/message context.</param>
    /// <param name="apMac">The AP's MAC, which the SSH router keys its credential route on.</param>
    public async Task<MeshOptimizationResult> OptimizeAsync(
        string? host, string? iface, string? apName, string? apMac, CancellationToken cancellationToken = default)
    {
        if (!_licenseState.IsSiteOperational(_siteContext.Slug))
        {
            _logger.LogWarning("Mesh optimization refused: site {Site} is license-restricted", _siteContext.Slug);
            return MeshOptimizationResult.NoOp(iface, Licensing.LicenseGuard.RestrictedMessage);
        }

        if (string.IsNullOrWhiteSpace(host))
            return MeshOptimizationResult.NoOp(iface, "This AP has no reachable address.");

        // The router picks the credentials, so an Express (UX, UX7) adopted as an AP that refuses Device SSH
        // falls back to Gateway SSH. A mesh child is always an access point.
        var target = new Ssh.DeviceSshTarget(apMac ?? "", host, NetworkOptimizer.Core.Enums.DeviceType.AccessPoint);

        // Without Device SSH every wpa_cli call fails with a generic error, so point the user at
        // where to set it up. Checked after settling the route: an Express (UX, UX7) adopted as an AP is still
        // a console, so it takes the console's root login (Gateway SSH) and needs no Device SSH.
        var route = await _sshRouters.GetFor(_siteContext.Slug).SettleAsync(target, cancellationToken);
        var sshSettings = await _ssh.GetSettingsAsync();
        var sshConfigured = !string.IsNullOrEmpty(sshSettings.Username) &&
            (!string.IsNullOrEmpty(sshSettings.Password) || !string.IsNullOrEmpty(sshSettings.PrivateKeyPath));
        if (!sshConfigured && route == Ssh.SshCredentialRoute.DeviceCredentials)
            return MeshOptimizationResult.NoOp(iface, "Set up UniFi Device SSH in Settings to re-pair the uplink.");

        // UniFi does not always report a mesh child's own uplink - after a parent reboots it can
        // describe the pair only from the parent's side - and the interface name lives on exactly
        // the half that goes missing. The device knows it either way, so ask it rather than let a
        // reporting gap disable the action.
        if (string.IsNullOrWhiteSpace(iface) || !ValidStaIface.IsMatch(iface))
        {
            iface = await DiscoverStaInterfaceAsync(target, cancellationToken);
            if (iface == null)
                return MeshOptimizationResult.NoOp(null, "This AP isn't a wireless mesh child.");

            _logger.LogDebug("[MeshOptimize] {Ap}: UniFi reported no backhaul iface; found {Iface} on the device", apName, iface);
        }

        // The per-interface wpa_supplicant control socket lives in different dirs across AP
        // platforms. U7-class APs run one global wpa_supplicant and nest the socket under
        // /var/run/wpa_supplicant/wpa_supplicant-<iface>/<iface>; U6-class APs run a per-interface
        // wpa_supplicant whose socket is flat at /var/run/wpa_supplicant/<iface>. Both name the
        // socket file <iface>, so probe both parents at call time and use whichever holds the live
        // socket. iface is validated above (^vwiresta\d+$), so it's safe to interpolate into the
        // shell. (BusyBox / POSIX sh, runtime only.)
        var ctrlDir = $"\"$(for d in /var/run/wpa_supplicant/wpa_supplicant-{iface} /var/run/wpa_supplicant; " +
            $"do [ -S \"$d/{iface}\" ] && {{ printf %s \"$d\"; break; }}; done)\"";
        var wc = $"wpa_cli -p {ctrlDir} -i {iface}";

        var before = await ReadLinkAsync(target, wc, cancellationToken);
        if (before.Bssid == null)
        {
            _logger.LogDebug("[MeshOptimize] {Ap}: could not read backhaul status on {Iface}", apName, iface);
            return MeshOptimizationResult.Failed(iface, "Couldn't read the mesh backhaul status.");
        }

        var beforeMloLinks = await ReadMloLinksAsync(target, before, cancellationToken);
        if (beforeMloLinks.Count > 0)
            before = before with { Rssi = beforeMloLinks.Max(l => l.Rssi) };

        var (scanOk, _) = await Run(target, $"{wc} scan", cancellationToken);
        if (!scanOk)
            return MeshOptimizationResult.Failed(iface, "Couldn't start the backhaul scan.");

        // The scan blips the backhaul, then the AP evaluates results and roams on its own, which
        // can land several seconds after the scan returns. A single early read sees the old parent
        // and wrongly reports "already on best", so poll the link and stop as soon as the BSSID
        // changes (or the window closes). Reads during the blip can come back empty; keep the last
        // good one.
        var after = new LinkRead(null, null, null, null);
        var moved = false;
        for (var attempt = 0; attempt < RoamPollAttempts; attempt++)
        {
            try
            {
                await Task.Delay(RoamPollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return MeshOptimizationResult.Failed(iface, "Mesh optimization was cancelled.");
            }

            var read = await ReadLinkAsync(target, wc, cancellationToken);
            if (read.Bssid == null) continue;

            after = read;
            if (IsNewParent(before, read))
            {
                moved = true;
                break;
            }
        }

        // Every read came back empty (the scan can drop the SSH session). Don't assert "already on
        // best" - the card refresh that follows is the source of truth for the current parent.
        if (after.Bssid == null)
        {
            return new MeshOptimizationResult
            {
                Iface = iface,
                BeforeBssid = before.Bssid,
                BeforeRssi = before.Rssi,
                Action = "noop",
                Ok = true,
                Message = "Backhaul re-scan done. Refreshing to show the current parent."
            };
        }

        var afterMloLinks = await ReadMloLinksAsync(target, after, cancellationToken);
        if (afterMloLinks.Count > 0)
            after = after with { Rssi = afterMloLinks.Max(l => l.Rssi) };
        // An MLO link with no fresh scan entry has no signal to report; non-MLO keeps "signal unknown".
        var signal = afterMloLinks.Count > 0 ? DescribeLinks(afterMloLinks) : after.ApMld != null ? null : Describe(after.Rssi);
        var suffix = signal != null ? $" ({signal})" : "";

        var result = new MeshOptimizationResult
        {
            Iface = iface,
            BeforeBssid = before.Bssid,
            BeforeRssi = before.Rssi,
            AfterBssid = after.Bssid,
            AfterRssi = after.Rssi,
            AfterLinkSpeedMbps = after.LinkSpeedMbps,
            Action = moved ? "moved" : "noop",
            Ok = true,
            Message = moved
                ? $"Moved to a stronger parent{suffix}."
                : $"Already on the best parent{suffix}."
        };

        _logger.LogInformation(
            "[MeshOptimize] {Ap} ({Iface}): {Action} {BeforeBssid}({BeforeRssi}) -> {AfterBssid}({AfterRssi}) mld {BeforeMld} -> {AfterMld}{Links}",
            apName, iface, result.Action, before.Bssid, before.Rssi, after.Bssid, after.Rssi,
            before.ApMld ?? "-", after.ApMld ?? "-", afterMloLinks.Count > 0 ? $" links {signal}" : "");

        return result;
    }

    /// <summary>
    /// An MLO backhaul names its parent by the AP MLD address; the link BSSID in status is one
    /// link of that parent and can change without a roam. Non-MLO links compare BSSIDs.
    /// </summary>
    private static bool IsNewParent(LinkRead before, LinkRead after) =>
        before.ApMld != null && after.ApMld != null
            ? !string.Equals(before.ApMld, after.ApMld, StringComparison.OrdinalIgnoreCase)
            : !string.Equals(before.Bssid, after.Bssid, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Per-link signal for an MLO backhaul, where signal_poll returns FAIL. Each link is its own
    /// vwiresta interface with its own supplicant, so each is asked for the scan-cache entry
    /// (<c>bss</c>) of its own BSSID. Empty for a non-MLO link, or one whose signal_poll already
    /// reported RSSI, so that path is unchanged.
    /// </summary>
    private async Task<List<(RadioBand Band, int Rssi)>> ReadMloLinksAsync(
        Ssh.DeviceSshTarget target, LinkRead link, CancellationToken cancellationToken)
    {
        if (link.ApMld == null || link.Rssi != null)
            return [];

        const string command =
            "for i in $(ls /sys/class/net 2>/dev/null | grep -x 'vwiresta[0-9]*'); do " +
            "d=\"$(for d in /var/run/wpa_supplicant/wpa_supplicant-$i /var/run/wpa_supplicant; " +
            "do [ -S \"$d/$i\" ] && { printf %s \"$d\"; break; }; done)\"; [ -n \"$d\" ] || continue; " +
            "b=$(wpa_cli -p \"$d\" -i $i status | sed -n 's/^bssid=//p'); echo '#bss'; " +
            "[ -n \"$b\" ] && wpa_cli -p \"$d\" -i $i bss \"$b\"; done";

        var (ok, output) = await Run(target, command, cancellationToken);
        if (!ok || string.IsNullOrWhiteSpace(output))
            return [];

        var links = new List<(RadioBand Band, int Rssi)>();
        foreach (var block in output.Split("#bss", StringSplitOptions.RemoveEmptyEntries))
        {
            // The level is from the last scan, not the live link: only the link just scanned is current.
            var mld = MatchValue(block, @"(?im)^ap_mld_addr=([0-9a-f:]{17})");
            var freq = MatchInt(block, @"(?im)^freq=(\d+)");
            var level = MatchInt(block, @"(?im)^level=(-?\d+)");
            var age = MatchInt(block, @"(?im)^age=(\d+)");
            if (string.Equals(mld, link.ApMld, StringComparison.OrdinalIgnoreCase)
                && freq is > 0 && level is < 0 && age is <= MaxBssAgeSeconds)
                links.Add((RadioBandExtensions.FromFrequencyMhz(freq.Value), level.Value));
        }
        return links;
    }

    /// <summary>Oldest scan-cache entry still reported as a link's signal.</summary>
    private const int MaxBssAgeSeconds = 60;

    /// <summary>
    /// The AP's own STA backhaul interface, read off the device. A wired AP has none, which is how
    /// this also answers "is it actually a mesh child" when UniFi will not say. Both wpa_supplicant
    /// layouts are searched, the same pair the control socket is probed in below.
    /// </summary>
    /// <param name="target">The AP.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<string?> DiscoverStaInterfaceAsync(Ssh.DeviceSshTarget target, CancellationToken cancellationToken)
    {
        var (ok, output) = await Run(
            target, "ls /sys/class/net 2>/dev/null | grep -x 'vwiresta[0-9]*' | head -1", cancellationToken);

        if (!ok) return null;
        var name = output?.Trim().Split('\n', '\r').FirstOrDefault()?.Trim();
        return !string.IsNullOrEmpty(name) && ValidStaIface.IsMatch(name) ? name : null;
    }

    /// <summary>One read of the backhaul link. <c>ApMld</c> is set only on an MLO backhaul.</summary>
    private sealed record LinkRead(string? Bssid, int? Rssi, int? LinkSpeedMbps, string? ApMld);

    /// <summary>Read the current backhaul BSSID and RSSI in a single SSH round trip.</summary>
    private async Task<LinkRead> ReadLinkAsync(
        Ssh.DeviceSshTarget target, string wc, CancellationToken cancellationToken)
    {
        var (ok, output) = await Run(target, $"{wc} status; {wc} signal_poll", cancellationToken);
        if (!ok || string.IsNullOrWhiteSpace(output))
            return new LinkRead(null, null, null, null);

        var bssid = MatchValue(output, @"(?im)^bssid=([0-9a-f:]{17})");
        var rssi = MatchInt(output, @"(?im)^RSSI=(-?\d+)");
        var linkSpeed = MatchInt(output, @"(?im)^LINKSPEED=(\d+)");
        var apMld = MatchValue(output, @"(?im)^mlo_ap_mld=([0-9a-f:]{17})");
        return new LinkRead(bssid, rssi, linkSpeed, apMld);
    }

    private Task<(bool success, string output)> Run(Ssh.DeviceSshTarget target, string command, CancellationToken ct) =>
        _sshRouters.GetFor(_siteContext.Slug).RunAsync(target, command, null, ct);

    private static string? MatchValue(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static int? MatchInt(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    private static string Describe(int? rssi) => rssi.HasValue ? $"{rssi} dBm" : "signal unknown";

    private static string DescribeLinks(List<(RadioBand Band, int Rssi)> links) =>
        string.Join(", ", links.OrderByDescending(l => l.Band).Select(l => $"{l.Band.ToDisplayString()} {l.Rssi} dBm"));
}

/// <summary>Outcome of a mesh backhaul re-scan. Returned to the page; not shown raw to the user.</summary>
public class MeshOptimizationResult
{
    public string? Iface { get; set; }
    public string? BeforeBssid { get; set; }
    public int? BeforeRssi { get; set; }
    public string? AfterBssid { get; set; }
    public int? AfterRssi { get; set; }
    public int? AfterLinkSpeedMbps { get; set; }

    /// <summary>"moved" if the backhaul roamed to a different parent, otherwise "noop".</summary>
    public string Action { get; set; } = "noop";

    /// <summary>True when the action ran; false when it couldn't (no iface, SSH failure, etc.).</summary>
    public bool Ok { get; set; }

    /// <summary>User-facing summary for the toast.</summary>
    public string Message { get; set; } = string.Empty;

    public static MeshOptimizationResult NoOp(string? iface, string message) =>
        new() { Iface = iface, Action = "noop", Ok = false, Message = message };

    public static MeshOptimizationResult Failed(string? iface, string message) =>
        new() { Iface = iface, Action = "noop", Ok = false, Message = message };
}
