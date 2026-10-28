namespace NetworkOptimizer.Web.Services.CellularModemProviders;

/// <summary>
/// Cellular provider keys, and which transport each uses. Settings decides the form's fields, the
/// port, and what to keep on save from these checks, so a new SSH provider is added here once
/// instead of at every comparison.
/// </summary>
public static class ModemProviderKeys
{
    /// <summary>UniFi modems, polled with the site's shared device SSH credentials.</summary>
    public const string Qmicli = "qmicli";

    /// <summary>GL.iNet routers, polled through their own <c>gl_modem</c> and ubus service.</summary>
    public const string QuectelAt = "quectel-at";

    /// <summary>Any router with a Quectel module, polled straight at the module's AT port.</summary>
    public const string QuectelSsh = "quectel-ssh";

    /// <summary>MikroTik RouterOS devices, polled through the RouterOS console.</summary>
    public const string MikroTikRouterOs = "mikrotik-routeros";

    /// <summary>
    /// Whether the provider polls with the site's shared device SSH credentials. Empty counts as
    /// qmicli: it is the default for rows that predate the Provider column.
    /// </summary>
    public static bool UsesSharedSsh(string? provider) =>
        string.IsNullOrWhiteSpace(provider) || provider.Equals(Qmicli, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the provider polls over SSH with credentials stored on the modem row.</summary>
    public static bool UsesPerModemSsh(string? provider) =>
        provider != null &&
        (provider.Equals(QuectelAt, StringComparison.OrdinalIgnoreCase) ||
         provider.Equals(QuectelSsh, StringComparison.OrdinalIgnoreCase) ||
         provider.Equals(MikroTikRouterOs, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether the provider is reached over SSH at all. Everything else is HTTP(S), whose save path
    /// forces port 80 or 443 and clears the SSH-only fields.
    /// </summary>
    public static bool IsSsh(string? provider) => UsesSharedSsh(provider) || UsesPerModemSsh(provider);
}
