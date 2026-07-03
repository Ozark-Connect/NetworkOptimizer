using System.Text.RegularExpressions;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Triggers a backhaul re-scan on a mesh-child AP over SSH so it self-roams to the strongest
/// available parent. The parent set is decided by UniFi Network (Auto, or a constrained/pinned
/// parent); this service never chooses the parent - it only prompts the scan and reports the
/// before/after link. On-demand, re-runnable, idempotent.
/// </summary>
public class MeshOptimizationService
{
    private readonly UniFiSshService _ssh;
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

    public MeshOptimizationService(UniFiSshRegistry uniFiSshRegistry, ILogger<MeshOptimizationService> logger)
    {
        _ssh = uniFiSshRegistry.GetDefault();
        _logger = logger;
    }

    /// <summary>
    /// Run a backhaul re-scan on the given mesh-child AP and report whether it moved to a
    /// stronger parent.
    /// </summary>
    /// <param name="host">The AP's IP/host for SSH.</param>
    /// <param name="iface">The STA backhaul interface (e.g. "vwiresta7").</param>
    /// <param name="apName">AP display name, used only for log/message context.</param>
    public async Task<MeshOptimizationResult> OptimizeAsync(
        string? host, string? iface, string? apName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            return MeshOptimizationResult.NoOp(iface, "This AP has no reachable address.");

        if (string.IsNullOrWhiteSpace(iface) || !ValidStaIface.IsMatch(iface))
            return MeshOptimizationResult.NoOp(iface, "This AP isn't a wireless mesh child.");

        // The action runs over the shared UniFi device SSH credentials. Without them every
        // wpa_cli call just fails with a generic error, so check up front and point the user at
        // where to set it up.
        var sshSettings = await _ssh.GetSettingsAsync();
        var sshConfigured = !string.IsNullOrEmpty(sshSettings.Username) &&
            (!string.IsNullOrEmpty(sshSettings.Password) || !string.IsNullOrEmpty(sshSettings.PrivateKeyPath));
        if (!sshConfigured)
            return MeshOptimizationResult.NoOp(iface, "Set up UniFi Device SSH in Settings to re-pair the uplink.");

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

        var before = await ReadLinkAsync(host, wc, cancellationToken);
        if (before.Bssid == null)
        {
            _logger.LogDebug("[MeshOptimize] {Ap}: could not read backhaul status on {Iface}", apName, iface);
            return MeshOptimizationResult.Failed(iface, "Couldn't read the mesh backhaul status.");
        }

        var (scanOk, _) = await _ssh.RunCommandAsync(host, $"{wc} scan", cancellationToken: cancellationToken);
        if (!scanOk)
            return MeshOptimizationResult.Failed(iface, "Couldn't start the backhaul scan.");

        // The scan blips the backhaul, then the AP evaluates results and roams on its own, which
        // can land several seconds after the scan returns. A single early read sees the old parent
        // and wrongly reports "already on best", so poll the link and stop as soon as the BSSID
        // changes (or the window closes). Reads during the blip can come back empty; keep the last
        // good one.
        (string? Bssid, int? Rssi, int? LinkSpeedMbps) after = (null, null, null);
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

            var read = await ReadLinkAsync(host, wc, cancellationToken);
            if (read.Bssid == null) continue;

            after = read;
            if (!string.Equals(read.Bssid, before.Bssid, StringComparison.OrdinalIgnoreCase))
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
                ? $"Moved to a stronger parent ({Describe(after.Rssi)})."
                : $"Already on the best parent ({Describe(after.Rssi)})."
        };

        _logger.LogInformation(
            "[MeshOptimize] {Ap} ({Iface}): {Action} {BeforeBssid}({BeforeRssi}) -> {AfterBssid}({AfterRssi})",
            apName, iface, result.Action, before.Bssid, before.Rssi, after.Bssid, after.Rssi);

        return result;
    }

    /// <summary>Read the current backhaul BSSID and RSSI in a single SSH round trip.</summary>
    private async Task<(string? Bssid, int? Rssi, int? LinkSpeedMbps)> ReadLinkAsync(
        string host, string wc, CancellationToken cancellationToken)
    {
        var (ok, output) = await _ssh.RunCommandAsync(
            host, $"{wc} status; {wc} signal_poll", cancellationToken: cancellationToken);
        if (!ok || string.IsNullOrWhiteSpace(output))
            return (null, null, null);

        var bssid = MatchValue(output, @"(?im)^bssid=([0-9a-f:]{17})");
        var rssi = MatchInt(output, @"(?im)^RSSI=(-?\d+)");
        var linkSpeed = MatchInt(output, @"(?im)^LINKSPEED=(\d+)");
        return (bssid, rssi, linkSpeed);
    }

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
