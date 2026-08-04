using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Which WAN the live throughput tiles are showing, for the surfaces that carry those tiles (the
/// Monitoring Live View tab and the dashboard's Live View panel). Both ask the same question and
/// answered it identically in their own code until this became one implementation.
/// <para>
/// The selection is deliberately separate from the analysis selectors: it has its own per-site
/// storage key, so watching one WAN's live rate never moves the Network Performance or ISP Health
/// focus. It IS shared between the two live surfaces, which is the point of the shared key - the
/// dashboard and the Monitoring tab show the same WAN.
/// </para>
/// <para>
/// Transient: each component keeps its own instance and its own <see cref="OnChanged"/>, so one
/// surface re-rendering never reaches into another's lifecycle.
/// </para>
/// </summary>
public sealed class LiveWanScope
{
    private readonly MonitoringPathView _pathView;
    private readonly SiteDbContextFactory _siteDb;
    private readonly SiteContextService _siteContext;
    private readonly IJSRuntime _js;

    private bool _loaded;
    private bool _restored;

    public LiveWanScope(
        MonitoringPathView pathView,
        SiteDbContextFactory siteDb,
        SiteContextService siteContext,
        IJSRuntime js)
    {
        _pathView = pathView;
        _siteDb = siteDb;
        _siteContext = siteContext;
        _js = js;
    }

    /// <summary>
    /// A WAN the live tiles can show. <paramref name="CounterIfName"/> is the interface whose
    /// counters carry that WAN's throughput; null when nothing has ever recorded one, which the
    /// tiles read as "no answer" rather than substituting another WAN's.
    /// </summary>
    /// <param name="HasContext">Whether a WAN context names this WAN. A secondary WAN without one
    /// is not probed at all, so anything offering to fix its monitoring has to send the user to
    /// make the context first - discovery cannot help until there is one.</param>
    public sealed record Option(string Key, string Label, bool IsPrimary, string? CounterIfName, bool HasContext);

    /// <summary>
    /// Raised after the selection changes. The surface owning this instance sets it: the scope
    /// holds the selection but cannot re-render or reach JS interop, so re-rendering the tiles and
    /// pointing the chart at the new WAN both happen here. Async because both of those are.
    /// </summary>
    public Func<Task>? OnChanged { get; set; }

    public IReadOnlyList<Option> Options { get; private set; } = Array.Empty<Option>();

    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every WAN currently shown. One is the ordinary case; several is comparison mode.</summary>
    public IReadOnlyCollection<string> SelectedKeys => _selected;

    /// <summary>
    /// The WAN in focus: the only selected one, or - while comparing - the primary of the
    /// selection. Everything that can only answer for ONE WAN reads this: the ISP Health score,
    /// the deep link, the throughput reference. Never null once options have loaded.
    /// </summary>
    public string SelectedKey =>
        _selected.Count == 1 ? _selected.First()
        : SelectedOptions.FirstOrDefault(o => o.IsPrimary)?.Key
            ?? SelectedOptions.FirstOrDefault()?.Key
            ?? "";

    public Option? Selected =>
        Options.FirstOrDefault(w => string.Equals(w.Key, SelectedKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>The selected WANs as options, in the order the site enumerates them.</summary>
    public IReadOnlyList<Option> SelectedOptions =>
        Options.Where(o => _selected.Contains(o.Key)).ToList();

    /// <summary>True while more than one WAN is shown - the tiles aggregate and the chart splits.</summary>
    public bool IsComparing => _selected.Count > 1;

    public bool IsSelected(string key) => _selected.Contains(key);

    /// <summary>
    /// True once there is more than one WAN to choose between - the gate for rendering the
    /// selector at all. A single-WAN site never shows it.
    /// </summary>
    public bool HasChoice => Options.Count > 1;

    private string StorageKey => _siteContext.ScopeStorageKey("liveWanScope");

    /// <summary>
    /// Loads the live WANs plus any WAN a context is bound to that the console currently omits
    /// (a down or failed WAN still has history worth reading).
    /// <para>
    /// Loads once per instance. The console poll calls this on every refresh, but a site's WAN
    /// list and its contexts do not change on that cadence, and on a single-WAN site the answer
    /// is one option and no selector - so re-asking bought a console call and a query per tick
    /// and nothing else.
    /// </para>
    /// </summary>
    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;

        var options = new List<Option>();

        // Read first: a live WAN needs to know whether a context names it, and the answer decides
        // where a "fix my monitoring" link can usefully send someone.
        List<Storage.Models.WanContext> contexts = new();
        try
        {
            await using var ctxDb = _siteDb.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
            contexts = await ctxDb.WanContexts.AsNoTracking().ToListAsync();
        }
        catch { /* site DB unavailable - treat every WAN as context-less, the conservative read */ }
        var keysWithContext = contexts
            .Where(c => !string.IsNullOrEmpty(c.WanInterface))
            .Select(c => c.WanInterface!.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var wan in await _pathView.GetWansAsync())
            {
                var key = wan.WanInterface.ToLowerInvariant();
                options.Add(new Option(
                    key,
                    GatewayWanHelper.FormatWanLabel(
                        wan.FriendlyName, GatewayWanHelper.WanIndexFromKey(wan.WanInterface), null, null),
                    wan.IsPrimary,
                    NetworkUtilities.PreferredWanCounterInterface(wan.PhysicalIfName, wan.UplinkIfName),
                    keysWithContext.Contains(key)));
            }
        }
        catch { /* console unreachable - contexts below still describe the WANs we know of */ }

        try
        {
            await using var db = _siteDb.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
            foreach (var ctx in contexts)
            {
                if (string.IsNullOrEmpty(ctx.WanInterface)) continue;
                var key = ctx.WanInterface!.ToLowerInvariant();
                if (options.Any(o => o.Key == key)) continue;

                // The console is not reporting this WAN, so its counter interface comes from what
                // the last connected run remembered for it.
                var group = GatewayWanHelper.WanNetworkGroupFromKey(key);
                var counter = (await db.WanProfiles.FirstOrDefaultAsync(w => w.WanNetworkgroup == group))?.CounterInterface;
                options.Add(new Option(
                    key,
                    GatewayWanHelper.FormatWanLabel(ctx.Name, GatewayWanHelper.WanIndexFromKey(key), null, null),
                    IsPrimary: false,
                    counter,
                    HasContext: true));
            }
        }
        catch { /* site DB unavailable - the live WANs above stand on their own */ }

        Options = options;
        if (_selected.Count == 0 && options.Count > 0)
            _selected.Add(ResolveDefaultKey(options));
    }

    /// <summary>
    /// The WAN the tiles open on: the one the console says holds the primary role, else the first
    /// WAN enumerated. That fallback is a guess - WAN group names carry no role information in
    /// UniFi Network - and it decides only which tile is shown first.
    /// </summary>
    internal static string ResolveDefaultKey(IReadOnlyList<Option> options)
        => options.FirstOrDefault(o => o.IsPrimary)?.Key ?? options.FirstOrDefault()?.Key ?? "";

    /// <summary>
    /// Shows exactly this WAN, or - with <paramref name="toggle"/> - adds and removes it from the
    /// comparison set. The same grammar the Network Performance filter uses, so the two rows do
    /// not behave differently for the same gesture: a plain click solos, a modifier click builds
    /// a set, and the set never empties.
    /// </summary>
    public async Task SelectAsync(string key, bool persist = true, bool toggle = false)
    {
        var option = Options.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));
        if (option == null) return;

        if (toggle)
        {
            if (!_selected.Add(option.Key) && _selected.Count > 1) _selected.Remove(option.Key);
        }
        else
        {
            _selected.Clear();
            _selected.Add(option.Key);
        }
        await PersistAndNotifyAsync(persist);
    }

    private async Task PersistAndNotifyAsync(bool persist)
    {
        if (persist)
        {
            try { await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, string.Join(",", _selected)); }
            catch { /* circuit going away - the selection still holds for this render */ }
        }
        if (OnChanged != null) await OnChanged.Invoke();
    }

    /// <summary>
    /// Restores the stored selection once interop is reachable. A stored WAN that no longer
    /// exists is ignored, leaving the default - so removing a WAN cannot strand the tiles on it.
    /// </summary>
    public async Task RestoreAsync()
    {
        if (_restored || !HasChoice) return;
        _restored = true;
        try
        {
            // Stored as a comma list since the selection became a set. A single key is still a
            // valid list of one, so a value written before comparison mode existed restores fine.
            var stored = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            var keys = (stored ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(k => Options.Any(o => string.Equals(o.Key, k, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            // Every stored WAN gone (renamed, removed) leaves the default rather than nothing.
            if (keys.Count > 0)
            {
                _selected.Clear();
                foreach (var k in keys) _selected.Add(k);
                await PersistAndNotifyAsync(persist: false);
            }
        }
        catch { /* no interop yet - the default selection stands */ }
    }
}
