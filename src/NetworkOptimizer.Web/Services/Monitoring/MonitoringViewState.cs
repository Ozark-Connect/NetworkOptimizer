using NetworkOptimizer.UniFi;
using static NetworkOptimizer.Web.Services.Monitoring.MonitoringLinks;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>Which of Network Performance's scopes a view is in.</summary>
public enum AnalysisScopeKind
{
    /// <summary>This network's own devices and targets, reached over no WAN.</summary>
    Lan,

    /// <summary>The whole site: every WAN and the LAN.</summary>
    All,

    /// <summary>One or more named WANs.</summary>
    Wans,
}

/// <summary>A WAN as the Monitoring filters offer it.</summary>
/// <param name="Key">The WAN's key ("wan", "wan2", ...).</param>
/// <param name="IsPrimary">The console names it the primary.</param>
public sealed record SiteWan(string Key, bool IsPrimary);

/// <summary>Network Performance's scope filter: the scope, and the WANs selected in it.</summary>
/// <param name="WanKeys">The WAN set. Kept, unlit, while the scope is LAN, so going back to WANs restores it.</param>
public sealed record AnalysisScope(AnalysisScopeKind Kind, IReadOnlyList<string> WanKeys);

/// <summary>A deep link into a Monitoring tab, as its query names it. Any part may be absent.</summary>
public sealed record MonitoringLink(string? Category, string? Target, string? At, string? Span, string? Investigate, string? Wan);

/// <summary>
/// The rules that decide what Monitoring's analysis views show when a link arrives or a tab jumps to another: one place,
/// tested, instead of each link and each handler carrying its own copy. Pure: inputs in, the view out.
/// <para>
/// What the user sees (research: monitoring-view-state plan): each tab keeps its own filters per site; changing one
/// saves it; a link or a jump changes the view for that visit only; a jump carries only what the destination can show;
/// a site switch opens the same tab on that site's own filters.
/// </para>
/// </summary>
public static class MonitoringViewState
{
    /// <summary>The <c>?wan=</c> token that names the LAN scope.</summary>
    public const string LanToken = LanScopeToken;

    /// <summary>The categories that are reached over no WAN: shown on the LAN scope.</summary>
    public static bool IsLanCategory(string? category) =>
        string.Equals(category, FabricCategory, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The chart categories a scope shows, in button order: LAN shows LAN and Custom; WANs show ISP, Transit, Internet,
    /// and Custom; All shows every one. Mirrors <c>CATEGORIES_BY_SCOPE</c> in latency-charts.js.
    /// </summary>
    public static IReadOnlyList<string> CategoriesFor(AnalysisScopeKind kind) => kind switch
    {
        AnalysisScopeKind.Lan => [FabricCategory, CustomCategory],
        AnalysisScopeKind.Wans => [AccessIspCategory, TransitCategory, InternetServiceCategory, CustomCategory],
        _ => [FabricCategory, AccessIspCategory, TransitCategory, InternetServiceCategory, CustomCategory],
    };

    /// <summary>
    /// The scope a deep link lands Network Performance on, for the visit only; null when the link does not narrow the
    /// filter (the saved scope stands) or names WANs this site does not have.
    /// <para>
    /// The category decides the scope: a LAN category lands on the LAN scope, whatever WAN token the link carries
    /// (alert and episode links ask for every WAN; only the LAN scope is LAN on every site shape). A WAN category
    /// lands on the WANs the link names, All for <c>all</c> (even on a single-WAN site, as before), and the saved
    /// WANs when it names none. Custom is shown in every scope, so it lands where the link's WAN token says, else
    /// the saved scope. A link with no category narrows only for an investigation or <c>wan=lan</c>, as before.
    /// </para>
    /// </summary>
    /// <param name="wans">The site's WANs. A LAN landing needs none, so it resolves before they load.</param>
    public static AnalysisScope? LinkScope(MonitoringLink link, IReadOnlyList<SiteWan> wans, AnalysisScope saved)
    {
        var wan = link.Wan?.Trim();
        var hasWan = !string.IsNullOrEmpty(wan);
        var isLanToken = string.Equals(wan, LanToken, StringComparison.OrdinalIgnoreCase);
        var investigate = link.Investigate is "packet-loss" or "loaded-loss";

        if (IsLanCategory(link.Category) || (isLanToken && string.IsNullOrEmpty(link.Category) && !investigate))
            return saved with { Kind = AnalysisScopeKind.Lan };

        // Without a category the link narrows only for an investigation; a discover link's WAN never re-scopes.
        if (string.IsNullOrEmpty(link.Category) && !investigate) return null;
        if (!hasWan || isLanToken)
        {
            // A WAN category with no WAN named lands on the saved WANs; off the LAN scope, which cannot show it.
            if (string.Equals(link.Category, CustomCategory, StringComparison.OrdinalIgnoreCase)) return null;
            return saved.Kind == AnalysisScopeKind.Lan ? WansOrPrimary(saved.WanKeys, wans) : null;
        }

        if (string.Equals(wan, LiveWanScope.AllWansToken, StringComparison.OrdinalIgnoreCase))
            return wans.Count == 0 ? null : new AnalysisScope(AnalysisScopeKind.All, wans.Select(w => w.Key).ToList());

        var keys = ResolveWanKeys(wan!, wans);
        if (keys.Count == 0) return null;
        // Naming every WAN of a multi-WAN site is the whole site.
        var all = wans.Count > 1 && wans.All(w => keys.Contains(w.Key, StringComparer.OrdinalIgnoreCase));
        return new AnalysisScope(all ? AnalysisScopeKind.All : AnalysisScopeKind.Wans, keys);
    }

    /// <summary>Whether <see cref="LinkScope"/> needs the site's WANs to answer: false for a LAN landing.</summary>
    public static bool LinkNeedsWans(MonitoringLink link) =>
        !IsLanCategory(link.Category)
        && !(string.IsNullOrEmpty(link.Category) && string.Equals(link.Wan?.Trim(), LanToken, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The WAN keys a <c>?wan=</c> value names: <c>all</c>, <c>primary</c>, or a comma list matched by WAN index
    /// ("wan", "wan1" and "WAN" are all WAN1), against the site's WANs. Unknown names drop out.
    /// </summary>
    public static List<string> ResolveWanKeys(string wan, IReadOnlyList<SiteWan> wans)
    {
        var trimmed = wan.Trim();
        if (string.Equals(trimmed, LiveWanScope.AllWansToken, StringComparison.OrdinalIgnoreCase))
            return wans.Select(w => w.Key).ToList();
        if (string.Equals(trimmed, LiveWanScope.PrimaryWanToken, StringComparison.OrdinalIgnoreCase))
            return wans.Where(w => w.IsPrimary).Select(w => w.Key).ToList();
        return trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(GatewayWanHelper.WanIndexFromKey)
            .Where(i => i > 0)
            .Select(i => wans.FirstOrDefault(w => GatewayWanHelper.WanIndexFromKey(w.Key) == i)?.Key)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The category the chart lands on: the asked one when the scope shows it, else the scope's first. LAN opens on
    /// ISP instead when the scope shows WANs and none of the targets on screen are on the LAN, so a view opens on a
    /// populated chart rather than an empty one.
    /// </summary>
    /// <param name="asked">The link's category, or null to open on the default.</param>
    /// <param name="lanTargetsOnScreen">The scope on screen has LAN targets.</param>
    public static string LandingCategory(string? asked, AnalysisScopeKind kind, bool lanTargetsOnScreen)
    {
        var shown = CategoriesFor(kind);
        var wanted = asked ?? FabricCategory;
        if (IsLanCategory(wanted) && kind != AnalysisScopeKind.Lan && !lanTargetsOnScreen) wanted = AccessIspCategory;
        return shown.FirstOrDefault(c => string.Equals(c, wanted, StringComparison.OrdinalIgnoreCase)) ?? shown[0];
    }

    /// <summary>
    /// The <c>?wan=</c> Watch in Live View carries from Network Performance: the WANs on screen, All for All, and
    /// nothing from the LAN scope, which Live View has no view of, so Live View keeps its own filter. Null on a site
    /// without the WAN choice.
    /// </summary>
    public static string? LiveJumpWan(AnalysisScope np, bool multiWanUi) => !multiWanUi ? null : np.Kind switch
    {
        AnalysisScopeKind.All => LiveWanScope.AllWansToken,
        AnalysisScopeKind.Wans when np.WanKeys.Count > 0 => string.Join(",", np.WanKeys),
        _ => null,
    };

    private static AnalysisScope? WansOrPrimary(IReadOnlyList<string> saved, IReadOnlyList<SiteWan> wans)
    {
        var keys = saved.Where(k => wans.Any(w => string.Equals(w.Key, k, StringComparison.OrdinalIgnoreCase))).ToList();
        if (keys.Count == 0) keys = wans.Where(w => w.IsPrimary).Select(w => w.Key).Take(1).ToList();
        if (keys.Count == 0) keys = wans.Select(w => w.Key).Take(1).ToList();
        return keys.Count == 0 ? null : new AnalysisScope(AnalysisScopeKind.Wans, keys);
    }
}
