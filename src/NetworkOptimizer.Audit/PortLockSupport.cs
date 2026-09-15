namespace NetworkOptimizer.Audit;

/// <summary>
/// Version gate for Lock Port to UniFi Device: UniFi Network 10.6.101+ and switch firmware 7.6.2+.
/// </summary>
public static class PortLockSupport
{
    /// <summary>
    /// Minimum UniFi Network application version that offers Lock Port to UniFi Device.
    /// </summary>
    public static readonly Version MinNetworkApplicationVersion = new(10, 6, 101);

    /// <summary>
    /// Minimum switch firmware version that supports Lock Port to UniFi Device.
    /// </summary>
    public static readonly Version MinSwitchFirmwareVersion = new(7, 6, 2);

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
    /// Whether both the application and the switch meet the minimum versions.
    /// Unknown or unparseable versions count as unsupported.
    /// </summary>
    public static bool IsAvailable(string? networkApplicationVersion, string? switchFirmwareVersion)
    {
        var app = ParseVersion(networkApplicationVersion);
        var fw = ParseVersion(switchFirmwareVersion);
        return app != null && fw != null
            && app >= MinNetworkApplicationVersion
            && fw >= MinSwitchFirmwareVersion;
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
