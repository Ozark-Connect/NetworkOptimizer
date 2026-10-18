namespace NetworkOptimizer.Audit;

/// <summary>
/// Version gate for Lock Port to UniFi Device: UniFi Network 10.6.101+ and switch firmware 7.6.2+,
/// with per-model exceptions from the UniFi Switch release notes.
/// </summary>
public static class PortLockSupport
{
    /// <summary>
    /// Switches UniFi never offers the lock on, by product name (UniFi Switch 7.6.7 release notes).
    /// "US-8 models" covers the whole US-8 family.
    /// </summary>
    private static readonly HashSet<string> UnsupportedSwitchModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "US-16-XG",
        "USW-Mission-Critical",
        "US-8",
        "US-8-60W",
        "US-8-150W",
    };

    /// <summary>
    /// Switches that gained the lock later than the general minimums, by product name. USW-Flex: switch
    /// firmware 7.6.7, which requires UniFi Network 11.
    /// </summary>
    private static readonly Dictionary<string, (Version App, Version Firmware)> ModelMinimums = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USW-Flex"] = (new Version(11, 0, 0, 0), new Version(7, 6, 7, 0)),
    };

    /// <summary>
    /// Minimum UniFi Network application version that offers Lock Port to UniFi Device.
    /// </summary>
    public static readonly Version MinNetworkApplicationVersion = new(10, 6, 101);

    /// <summary>
    /// Minimum switch firmware version that supports Lock Port to UniFi Device.
    /// </summary>
    public static readonly Version MinSwitchFirmwareVersion = new(7, 6, 2);

    /// <summary>
    /// UniFi Network version that renamed the switch port settings: Ethernet Port Profiles became Port
    /// Profiles, and Restricted with allowed MACs became Port Security with a MAC Address Filter.
    /// </summary>
    public static readonly Version MinPortSecurityNamesVersion = new(10, 6, 0);

    /// <summary>
    /// Whether issue text should use the 10.6+ setting names. An unknown version gets them too: it only
    /// happens when the version read fails, and the new names match the current UI.
    /// </summary>
    public static bool UsesPortSecurityNames(string? networkApplicationVersion)
    {
        var version = ParseVersion(networkApplicationVersion);
        return version == null || version >= MinPortSecurityNamesVersion;
    }

    /// <summary>
    /// Whether the device type can take Lock Port to UniFi Device at all. UniFi switches (usw) only:
    /// gateways and in-wall APs have switch ports but run other firmware lines, and an AP's 8.x
    /// version would otherwise pass the 7.6.2 check.
    /// </summary>
    public static bool SupportsDeviceType(string? deviceType) =>
        string.Equals(deviceType, "usw", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether UniFi Network lets Lock Port to UniFi Device sit on a port that has an Ethernet Port
    /// Profile assigned. It does not as of 10.6; the lock is a per-port setting only. Every rule that
    /// weighs a profile against the lock reads this flag, so flipping it is the whole change when
    /// UniFi adds profile support.
    /// </summary>
    /// <remarks>
    /// Two ways UniFi could add it, both speculative. (1) The lock stays per-port and is allowed
    /// alongside a profile: flip this flag and nothing else changes. (2) The profile itself carries a
    /// "lock to whatever UniFi device connects" setting: that needs a UniFiPortProfile field, the
    /// profile suggestion analyzer treating lockable ports as profile candidates again, and a
    /// MacRestriction/PortLock read of the profile's setting. (2) is the less likely one; a profile
    /// that locks on first contact is a foot-gun.
    /// </remarks>
    public const bool LockCoexistsWithPortProfile = false;

    /// <summary>
    /// Whether a port with the given profile assignment can take the lock at all.
    /// </summary>
    public static bool CanLockWithProfile(string? portProfileId) =>
        LockCoexistsWithPortProfile || string.IsNullOrEmpty(portProfileId);

    /// <summary>
    /// Whether the switch model can take the lock on any version. An unknown model counts as supported.
    /// </summary>
    public static bool SupportsModel(string? switchProductName) =>
        string.IsNullOrEmpty(switchProductName) || !UnsupportedSwitchModels.Contains(switchProductName);

    /// <summary>
    /// Whether both the application and the switch meet the minimum versions, including any higher
    /// minimums for the switch model. Unknown or unparseable versions count as unsupported.
    /// </summary>
    public static bool IsAvailable(string? networkApplicationVersion, string? switchFirmwareVersion, string? switchProductName = null)
    {
        var app = ParseVersion(networkApplicationVersion);
        var fw = ParseVersion(switchFirmwareVersion);
        if (app == null || fw == null || !SupportsModel(switchProductName))
            return false;

        var (minApp, minFw) = switchProductName != null && ModelMinimums.TryGetValue(switchProductName, out var model)
            ? model
            : (MinNetworkApplicationVersion, MinSwitchFirmwareVersion);
        return app >= minApp && fw >= minFw;
    }

    /// <summary>
    /// Parse the leading numeric dotted part of a version string ("7.6.2.17186", "10.6.106", "v10.6.101-beta").
    /// </summary>
    public static Version? ParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var s = raw.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..];

        var end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.'))
            end++;
        var numeric = s[..end].Trim('.');

        var parts = numeric.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        // Version needs 2-4 components; extra build segments are dropped
        var trimmed = string.Join('.', parts.Take(4));
        return Version.TryParse(trimmed, out var v) ? Normalize(v) : null;
    }

    // Version compares -1 (unset) below 0, so "10.6" must not sort under "10.6.0"
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
