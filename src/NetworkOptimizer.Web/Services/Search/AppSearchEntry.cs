using System.Security.Claims;

namespace NetworkOptimizer.Web.Services.Search;

/// <summary>
/// One place in the app a search can take you: a card, a panel, or a section that already carries an
/// element id for the standard scroll-and-highlight jump.
///
/// The shape is deliberately area-agnostic. Settings is the first area indexed, but nothing here
/// knows about tabs - a second area registers its own <see cref="IAppSearchProvider"/> and the same
/// search box, scorer and result list serve it unchanged.
///
/// A class rather than a record because <see cref="SearchText"/> caches into a field, and a record
/// would fold that field into its generated equality - two identical entries would compare unequal
/// on nothing more than which of them had been searched.
/// </summary>
public sealed class AppSearchEntry
{
    /// <summary>The on-screen name of the target, exactly as the user reads it on the card.</summary>
    public required string Title { get; init; }

    /// <summary>Top-level feature the entry lives under, e.g. "Settings". First crumb in the result.</summary>
    public required string Area { get; init; }

    /// <summary>Where inside the area, e.g. the tab name. Second crumb in the result.</summary>
    public string? Section { get; init; }

    /// <summary>Route the result navigates to, e.g. "/settings?tab=monitoring".</summary>
    public required string Route { get; init; }

    /// <summary>Element id to scroll to and ring once the route is showing.</summary>
    public string? Anchor { get; init; }

    /// <summary>Other names for the same thing: what it used to be called, or what a vendor calls it.</summary>
    public string[] Aliases { get; init; } = [];

    /// <summary>Words that live inside the card rather than in its title, e.g. the fields it holds.</summary>
    public string[] Keywords { get; init; } = [];

    /// <summary>
    /// Area-defined identifier for the target, for a host page that handles selection in place
    /// instead of navigating. Settings puts its tab id here so a hit switches tab without a round
    /// trip through the URL.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>
    /// The site this target lives on, when that is not the site being viewed. Set only for a result
    /// that needs a site switch to reach; null means "wherever you are". Reaching it changes which
    /// site the whole session is contexted to, so a result carrying this has to say so.
    /// </summary>
    public string? SiteSlug { get; init; }

    /// <summary>How to label <see cref="SiteSlug"/> to someone reading the result.</summary>
    public string? SiteName { get; init; }

    /// <summary>
    /// This entry as it would be presented from another site: same target, plus where it lives.
    /// Hand-copied because the class is not a record - see the note above on why.
    /// </summary>
    internal AppSearchEntry OnSite(string slug, string name) => new()
    {
        Title = Title,
        Area = Area,
        Section = Section,
        Route = Route,
        Anchor = Anchor,
        Aliases = Aliases,
        Keywords = Keywords,
        Key = Key,
        SiteSlug = slug,
        SiteName = name,
    };

    /// <summary>
    /// Every field as one string, so a query whose words are split across them still lands -
    /// "monitoring cable" is the section plus part of the title, and neither field holds both.
    /// Built once per entry rather than once per search: an index is static for the life of the
    /// process, and an app-wide one will not be small.
    ///
    /// That static index is shared by every circuit, so two users can race this. Deliberately
    /// unlocked: the value is derived from init-only fields, so both would compute the same string
    /// and publishing the reference is atomic. A lock here would serialize every search to protect
    /// nothing.
    /// </summary>
    internal string SearchText => _searchText ??= string.Join(' ',
        new[] { Title, Section, Area }
            .Concat(Aliases)
            .Concat(Keywords)
            .Where(s => !string.IsNullOrWhiteSpace(s)));

    private string? _searchText;
}

/// <summary>A matched entry and the score it earned, higher being better.</summary>
public sealed record AppSearchHit(AppSearchEntry Entry, int Score);

/// <summary>
/// Who is searching and where they are standing. Providers use it to leave out anything the caller
/// could not reach, so a search never offers a result that lands on a refusal or an absent tab.
/// </summary>
public sealed record AppSearchContext(ClaimsPrincipal? User);

/// <summary>
/// Supplies the searchable entries for one area of the app. Register an implementation to add that
/// area to the index; nothing else has to change.
/// </summary>
public interface IAppSearchProvider
{
    /// <summary>The area these entries belong to, matching <see cref="AppSearchEntry.Area"/>.</summary>
    string Area { get; }

    /// <summary>
    /// The entries this caller may reach, in no particular order. The token is superseded typing:
    /// a provider that does real work to answer should abandon it rather than finish for nobody.
    /// </summary>
    Task<IReadOnlyList<AppSearchEntry>> GetEntriesAsync(
        AppSearchContext context, CancellationToken cancellationToken = default);
}
