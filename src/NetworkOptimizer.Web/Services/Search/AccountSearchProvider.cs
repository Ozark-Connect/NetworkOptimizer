namespace NetworkOptimizer.Web.Services.Search;

/// <summary>
/// Indexes the signed-in user's own account page into the Settings search, where people look for
/// it. Every signed-in user reaches the page whatever their role, so this is how a Site Admin finds
/// their own time zone and guided tours: the Settings - Application cards are a Global Admin's alone.
/// </summary>
public sealed class AccountSearchProvider : IAppSearchProvider
{
    /// <summary>The Security page's Time Zone card.</summary>
    internal static readonly AppSearchEntry TimeZone = new()
    {
        Title = "Date & Time",
        Area = "Account",
        Section = "Security",
        Route = "/account/security",
        Anchor = "time-zone",
        Aliases = ["Time Zone", "timezone", "tz", "Date and Time"],
        Keywords = ["your time zone", "time format", "date format", "12-hour", "24-hour", "am pm", "day first",
            "month first", "local time", "clock", "utc", "daylight saving", "dst", "timestamps", "dates and times"],
    };

    /// <summary>The Security page's Guided Tours card: the caller's own tour state and opt-out.</summary>
    internal static readonly AppSearchEntry GuidedTours = new()
    {
        Title = "Guided Tours",
        Area = "Account",
        Section = "Security",
        Route = "/account/security",
        Anchor = "guided-tours",
        Aliases = ["What's new", "tour"],
        Keywords = ["walkthrough", "replay", "start tour", "don't offer tours automatically", "release walkthrough"],
    };

    /// <summary>The search box this answers in: the one on the Settings page.</summary>
    public string Area => "Settings";

    /// <inheritdoc />
    public Task<IReadOnlyList<AppSearchEntry>> GetEntriesAsync(
        AppSearchContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AppSearchEntry>>(
            context.User?.Identity?.IsAuthenticated == true ? [TimeZone, GuidedTours] : []);
}
