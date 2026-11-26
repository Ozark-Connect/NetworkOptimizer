using NetworkOptimizer.Core;

namespace NetworkOptimizer.UniFi;

/// <summary>
/// Picks the name to show for a client, the same way at every place that names one.
/// </summary>
[VendorSpecific("UniFi", "display_name, name (alias), unifi_device_info_from_ucore.name, hostname")]
public static class ClientNames
{
    /// <summary>
    /// A UniFi device (a Protect camera, an AI Key, a CloudKey) is named in its own app, and UniFi
    /// Network's display name follows that name. UniFi OS hands some of those names over in hostname
    /// form ("SecurityAIKey" for "[Security] AI Key"): a name equal to the hostname, or to the alias with
    /// only its letters and digits kept, is that form, and the alias set in UniFi Network is then the real
    /// one. Any other client: display name, alias, device name, hostname.
    /// </summary>
    /// <param name="displayName">UniFi's display name (v2 active clients), or null.</param>
    /// <param name="alias">The alias set in UniFi Network (<c>name</c>), or null.</param>
    /// <param name="ucoreName">The owning app's name for a UniFi device, or null.</param>
    /// <param name="hostname">The client's hostname, or null.</param>
    /// <param name="isUniFiDevice">The client is a UniFi device another UniFi app owns.</param>
    /// <param name="hostnameFallback">Return the hostname when nothing else names the client.</param>
    public static string? Choose(string? displayName, string? alias, string? ucoreName, string? hostname,
        bool isUniFiDevice, bool hostnameFallback = true)
    {
        var display = Clean(displayName);
        var given = Clean(alias);
        var device = Clean(ucoreName);
        var host = Clean(hostname);

        if (isUniFiDevice && (device ?? display) is { } appName)
        {
            // The hostname is not always that form ("ai-key" while the app name reads "SecurityAIKey"), so the
            // alias reduced to its letters and digits counts too.
            var hostnameForm = (host != null && string.Equals(appName, host, StringComparison.OrdinalIgnoreCase))
                || (given != null && string.Equals(appName, LettersAndDigits(given), StringComparison.OrdinalIgnoreCase));
            if (!hostnameForm || given == null) return appName;
            return given;
        }
        return display ?? given ?? device ?? (hostnameFallback ? host : null);
    }

    /// <summary><see cref="Choose"/> for a stat/sta or rest/user client, with UniFi's display name when the caller has it.</summary>
    public static string? For(Models.UniFiClientResponse client, string? displayName = null, bool hostnameFallback = true) =>
        Choose(displayName ?? client.DisplayName, client.Name, client.UnifiDeviceInfoFromUcore?.Name, client.Hostname, client.IsUniFiDevice, hostnameFallback);

    /// <summary><see cref="Choose"/> for a v2 active or history client, which carries its own display name.</summary>
    public static string? For(Models.UniFiClientDetailResponse client, bool hostnameFallback = true) =>
        Choose(client.DisplayName, client.Name, null, client.Hostname, client.UnifiDevice, hostnameFallback);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string LettersAndDigits(string value) => string.Concat(value.Where(char.IsLetterOrDigit));
}
