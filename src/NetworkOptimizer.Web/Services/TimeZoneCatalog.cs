using System.Diagnostics.CodeAnalysis;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Web.Services;

/// <summary>One time zone offered in a picker.</summary>
/// <param name="Id">IANA id, as stored (<c>America/Chicago</c>).</param>
/// <param name="Region">Picker group: the id's first segment, or "UTC".</param>
/// <param name="Name">The id without its region, underscores as spaces (<c>Chicago</c>).</param>
public sealed record TimeZoneOption(string Id, string Region, string Name);

/// <summary>
/// The time zones offered for display, and resolution of a stored id. Ids are always IANA: the
/// container's TZ, the UniFi Console, and every browser speak it, and .NET resolves IANA ids on Windows.
/// </summary>
public static class TimeZoneCatalog
{
    /// <summary>The UTC entry's id.</summary>
    public const string UtcId = "Etc/UTC";

    private static readonly string[] Regions =
        ["Africa", "America", "Antarctica", "Asia", "Atlantic", "Australia", "Europe", "Indian", "Pacific"];

    private static readonly Lazy<IReadOnlyList<TimeZoneOption>> _options = new(Build);

    /// <summary>Every offered zone, grouped by region and sorted by name within it, UTC first.</summary>
    public static IReadOnlyList<TimeZoneOption> Options => _options.Value;

    /// <summary>The server's own zone as an IANA id, where the platform can name one.</summary>
    public static string ServerZoneId => ToIana(TimeZoneInfo.Local);

    /// <summary>Resolves a stored id. False for null, blank, or an id this platform does not know.</summary>
    public static bool TryResolve(string? id, [NotNullWhen(true)] out TimeZoneInfo? zone)
    {
        zone = null;
        // On Linux an id is a path under the zoneinfo directory; stored ids come from clients.
        return !string.IsNullOrWhiteSpace(id) && IsWellFormedId(id = id.Trim())
            && TimeZoneInfo.TryFindSystemTimeZoneById(id, out zone);
    }

    private static bool IsWellFormedId(string id) =>
        id.Length <= 64 && !id.Contains("..") && id[0] != '/'
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '+');

    /// <summary>The IANA id of a zone, converting a Windows id where needed.</summary>
    public static string ToIana(TimeZoneInfo zone) =>
        zone.HasIanaId ? zone.Id
        : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana
        : zone.Id;

    /// <summary>
    /// An instant on <paramref name="zone"/>'s clock. Unspecified kind is read as UTC, which is what EF
    /// returns from SQLite and what <see cref="DateTime.ToLocalTime"/> assumed.
    /// </summary>
    public static DateTime ToZone(DateTime utc, TimeZoneInfo zone) => DateTimeUtilities.ToZone(utc, zone);

    /// <summary>"America/Chicago (UTC-05:00)", with the offset in force at <paramref name="nowUtc"/>.</summary>
    public static string Describe(string id, DateTime nowUtc) =>
        TryResolve(id, out var zone) ? $"{id} ({OffsetLabel(zone, nowUtc)})" : id;

    /// <summary>"UTC-05:00" for the offset in force at <paramref name="nowUtc"/>.</summary>
    public static string OffsetLabel(TimeZoneInfo zone, DateTime nowUtc)
    {
        var offset = zone.GetUtcOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));
        return $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}";
    }

    /// <summary>
    /// Short name of a zone at an instant: "CDT" where the platform has one (Linux), else the
    /// initials of the long name ("Central Daylight Time" on Windows).
    /// </summary>
    public static string Abbreviation(TimeZoneInfo zone, DateTime instantUtc) =>
        DateTimeUtilities.ZoneAbbreviation(zone, instantUtc);

    private static IReadOnlyList<TimeZoneOption> Build()
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            var id = ToIana(zone);
            var slash = id.IndexOf('/');
            if (slash > 0 && Regions.Contains(id[..slash]))
                ids.Add(id);
        }

        var options = new List<TimeZoneOption> { new(UtcId, "UTC", "UTC") };
        options.AddRange(ids.Select(id =>
        {
            var slash = id.IndexOf('/');
            return new TimeZoneOption(id, id[..slash], id[(slash + 1)..].Replace('_', ' '));
        }));
        return options;
    }
}
