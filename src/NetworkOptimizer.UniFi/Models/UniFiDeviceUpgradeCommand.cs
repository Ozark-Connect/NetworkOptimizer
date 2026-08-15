using NetworkOptimizer.Core;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Request bodies for the two cmd/devmgr firmware commands, and the MAC form the console expects.
/// </summary>
[VendorSpecific("UniFi", "cmd/devmgr upgrade / upgrade-external bodies")]
public static class UniFiDeviceUpgradeCommand
{
    /// <summary>Upgrade to the console's pending target for the device.</summary>
    public const string Upgrade = "upgrade";

    /// <summary>Upgrade to (or revert to) an arbitrary firmware image URL.</summary>
    public const string UpgradeExternal = "upgrade-external";

    /// <summary>
    /// POST cmd/devmgr body for the standard upgrade: the target comes from the console's catalog
    /// at its current channel, so no URL is sent.
    /// </summary>
    public static Dictionary<string, object?> BuildUpgradeBody(string mac)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mac);

        return new Dictionary<string, object?>
        {
            ["mac"] = NormalizeMac(mac),
            ["cmd"] = Upgrade
        };
    }

    /// <summary>
    /// POST cmd/devmgr body for an arbitrary-image upgrade or revert.
    /// </summary>
    public static Dictionary<string, object?> BuildExternalUpgradeBody(string mac, string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mac);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        return new Dictionary<string, object?>
        {
            ["mac"] = NormalizeMac(mac),
            ["url"] = url.Trim(),
            ["cmd"] = UpgradeExternal
        };
    }

    /// <summary>
    /// Whether the console ACCEPTED the command. Both firmware commands are asynchronous, so this
    /// says the console took the request - never that the device flashed. Success is decided later
    /// by the observed state plus a version comparison.
    /// </summary>
    public static bool IsAccepted(UniFiApiResponse<object>? response) => response?.Meta.Rc == "ok";

    /// <summary>
    /// Lowercase colon form, which is what the console expects. Any separator is accepted on the
    /// way in; anything that is not twelve hex digits is passed through lowercased rather than
    /// reshaped, so an unexpected identifier reaches the console as the caller wrote it.
    /// </summary>
    public static string NormalizeMac(string mac)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mac);

        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        if (hex.Length != 12)
            return mac.Trim().ToLowerInvariant().Replace('-', ':');

        return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }
}
