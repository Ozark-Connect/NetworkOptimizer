using System.Text.RegularExpressions;

namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Device firmware versions as an operator reads them. This is the one place that shortens a
/// firmware string for display - reboot tooltips, chart annotations, device tables and the
/// Firmware Rollout screens all go through it, so the same device never reads two ways.
/// </summary>
/// <remarks>
/// Distinct from <see cref="VersionUtilities"/>, which handles the application's own SemVer.
/// </remarks>
public static class FirmwareVersionFormat
{
    /// <summary>
    /// Reduce a firmware string to the part an operator reads. The platform, git hash and build
    /// stamp only make a tooltip unreadable. Every shape in the fleet collapses to the version:
    /// consoles report <c>UXGA6AA.ipq9574.v5.1.26.0bc0fe4.260716.1128</c>, the switch upgrade
    /// marker reports <c>US3.rtl93xx_7.5.6+17090.260622.0846</c>, and the console catalog reports
    /// <c>7.5.10.17129</c>; all become <c>5.1.26</c> / <c>7.5.6</c> / <c>7.5.10</c>.
    /// </summary>
    public static string Short(string firmware)
    {
        if (string.IsNullOrWhiteSpace(firmware))
            return firmware;

        // Three components is the version everywhere in the fleet; anything after it is build
        // metadata, whatever separator it uses.
        var threePart = Regex.Match(firmware, @"(\d+\.\d+\.\d+)");
        if (threePart.Success)
            return threePart.Groups[1].Value;

        // Two-component versions exist on older builds. The lookahead keeps the match off a git
        // hash: in "v5.1.b3a286b" the trailing part is not a version component.
        var twoPart = Regex.Match(firmware, @"v?(\d+\.\d+)(?![0-9A-Za-z])", RegexOptions.IgnoreCase);
        return twoPart.Success ? twoPart.Groups[1].Value : firmware;
    }

    /// <summary>Short form, or null when there is no version to show.</summary>
    public static string? ShortOrNull(string? firmware) =>
        string.IsNullOrWhiteSpace(firmware) ? null : Short(firmware);

    /// <summary>
    /// Whether two firmware strings name the same build. The two sides of a comparison never carry
    /// the same amount of it: the catalog names <c>7.5.10.17129</c> while the device that just
    /// installed it reports <c>7.5.10</c>, so comparing them literally calls a good upgrade a
    /// failure. The version is therefore all that can be compared - a device never reports the
    /// build number, so two builds of one version are indistinguishable from here by construction.
    /// </summary>
    /// <param name="left">One version, in any of the fleet's shapes.</param>
    /// <param name="right">The other.</param>
    public static bool SameBuild(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        return string.Equals(Short(left), Short(right), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is a later version than <paramref name="installed"/>.
    /// Compared on the three-part version, since that is all a device reports. Equal is not newer.
    /// </summary>
    /// <param name="candidate">Version being offered.</param>
    /// <param name="installed">Version running now.</param>
    public static bool IsNewer(string? candidate, string? installed)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (string.IsNullOrWhiteSpace(installed)) return true;

        var a = Parts(candidate);
        var b = Parts(installed);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;

        static int[] Parts(string v) => Short(v).Split('.')
            .Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
    }
}
