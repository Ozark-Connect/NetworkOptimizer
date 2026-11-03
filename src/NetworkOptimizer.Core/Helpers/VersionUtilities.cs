namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Comparison helpers for the MinVer/SemVer-style version strings the app and
/// its on-site agents report (e.g. "2.0.0-beta.2.14+3449fbae" or "1.4.2").
/// </summary>
public static class VersionUtilities
{
    /// <summary>
    /// True when <paramref name="candidate"/> is a lower version than
    /// <paramref name="required"/> under SemVer precedence: numeric core first,
    /// then prerelease identifiers (a release outranks any prerelease of the
    /// same core; identifiers compare numerically when both numeric, by their
    /// trailing number when both are the same letters plus a number ("preview9" is
    /// below "preview15"), otherwise ordinally; a longer identifier list outranks
    /// its prefix). Build metadata
    /// after '+' is ignored. Returns false when either side is missing or
    /// unparseable - callers flag only provable staleness.
    /// </summary>
    public static bool IsOlderThan(string? candidate, string? required)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(required))
            return false;

        var (candCore, candPre) = Split(candidate);
        var (reqCore, reqPre) = Split(required);
        if (candCore == null || reqCore == null)
            return false;

        var coreCompare = candCore.CompareTo(reqCore);
        if (coreCompare != 0)
            return coreCompare < 0;

        // Same core: a release (no prerelease) outranks any prerelease.
        if (candPre.Length == 0)
            return false;
        if (reqPre.Length == 0)
            return true;

        var count = Math.Min(candPre.Length, reqPre.Length);
        for (var i = 0; i < count; i++)
        {
            var cmp = CompareIdentifier(candPre[i], reqPre[i]);
            if (cmp != 0)
                return cmp < 0;
        }
        return candPre.Length < reqPre.Length;
    }

    /// <summary>
    /// Compares one prerelease identifier. Numeric when both are numbers; and when both are the
    /// same letters followed by a number, by that number, since our tags are "preview15" rather
    /// than "preview.15" and an ordinal compare puts "preview9" above it. Otherwise ordinal.
    /// </summary>
    private static int CompareIdentifier(string a, string b)
    {
        if (long.TryParse(a, out var aNum) && long.TryParse(b, out var bNum))
            return aNum.CompareTo(bNum);

        var (aPrefix, aSuffix) = SplitTrailingNumber(a);
        var (bPrefix, bSuffix) = SplitTrailingNumber(b);
        if (aSuffix is { } an && bSuffix is { } bn
            && string.Equals(aPrefix, bPrefix, StringComparison.OrdinalIgnoreCase))
            return an.CompareTo(bn);

        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static (string Prefix, long? Number) SplitTrailingNumber(string identifier)
    {
        var start = identifier.Length;
        while (start > 0 && char.IsAsciiDigit(identifier[start - 1]))
            start--;
        if (start == 0 || start == identifier.Length
            || !long.TryParse(identifier.AsSpan(start), out var number))
            return (identifier, null);
        return (identifier[..start], number);
    }

    /// <summary>
    /// The version as shown to users: build metadata after '+' stripped
    /// ("2.0.1+ed2714ca..." -> "2.0.1"), prerelease identifiers kept
    /// ("2.0.0-beta.2+3449fbae" -> "2.0.0-beta.2"), matching the footer's
    /// format. Null/blank and metadata-free inputs pass through unchanged.
    /// </summary>
    public static string? StripBuildMetadata(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return version;
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }

    /// <summary>
    /// Drops a leading "v" so a value rendered after the UI's own "v" prefix
    /// cannot double up: a from-source build stamped "v2.8.0-preview6-16-gc15f291a"
    /// would otherwise show as "vv...". Nothing else is changed. Null/blank passes
    /// through.
    /// </summary>
    public static string? TrimLeadingV(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return version;
        return version.TrimStart('v', 'V');
    }

    /// <summary>
    /// The bare X.Y.Z: prerelease identifiers and build metadata stripped
    /// ("2.8.0-preview5+abc" -> "2.8.0"). For user-facing copy where the gate
    /// version is a prerelease tag but the binaries a user fetches carry the
    /// release name. Null/blank passes through.
    /// </summary>
    public static string? CoreVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return version;
        var v = StripBuildMetadata(version)!;
        var dash = v.IndexOf('-');
        return dash >= 0 ? v[..dash] : v;
    }

    private static (Version? Core, string[] Prerelease) Split(string version)
    {
        var v = version.Trim().TrimStart('v', 'V');
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        var dash = v.IndexOf('-');
        var core = dash >= 0 ? v[..dash] : v;
        var pre = dash >= 0 ? v[(dash + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        if (!Version.TryParse(core, out var parsed))
            return (null, pre);
        // Normalize a 2-part core: Version treats a missing component as -1,
        // which would rank "2.0" below "2.0.0" even though they name the same
        // version.
        if (parsed.Build < 0)
            parsed = new Version(parsed.Major, parsed.Minor, 0);
        return (parsed, pre);
    }
}
