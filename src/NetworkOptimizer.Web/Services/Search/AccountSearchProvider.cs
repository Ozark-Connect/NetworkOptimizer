namespace NetworkOptimizer.Web.Services.Search;

/// <summary>
/// Indexes the signed-in user's own account page into the Settings search, where people look for
/// it. Every signed-in user reaches the page whatever their role, so this is how a Site Admin finds
/// their own time zone: the Settings - Application card is a Global Admin's alone.
/// </summary>
public sealed class AccountSearchProvider : IAppSearchProvider
{
    /// <summary>The Security page's Time Zone card.</summary>
    internal static readonly AppSearchEntry TimeZone = new()
    {
        Title = "Time Zone",
        Area = "Account",
        Section = "Security",
        Route = "/account/security",
        Anchor = "time-zone",
        Aliases = ["timezone", "time zone", "tz"],
        Keywords = ["your time zone", "local time", "clock", "utc", "daylight saving", "dst", "timestamps",
            "dates and times"],
    };

    /// <summary>The search box this answers in: the one on the Settings page.</summary>
    public string Area => "Settings";

    /// <inheritdoc />
    public Task<IReadOnlyList<AppSearchEntry>> GetEntriesAsync(
        AppSearchContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AppSearchEntry>>(
            context.User?.Identity?.IsAuthenticated == true ? [TimeZone] : []);
}
