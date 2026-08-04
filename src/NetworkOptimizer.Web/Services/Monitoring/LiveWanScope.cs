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
    public sealed record Option(string Key, string Label, bool IsPrimary, string? CounterIfName);

    /// <summary>Raised after the selection changes so the component can re-render.</summary>
    public Action? OnChanged { get; set; }

    public IReadOnlyList<Option> Options { get; private set; } = Array.Empty<Option>();

    public string SelectedKey { get; private set; } = "";

    public Option? Selected =>
        Options.FirstOrDefault(w => string.Equals(w.Key, SelectedKey, StringComparison.OrdinalIgnoreCase));

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
        try
        {
            foreach (var wan in await _pathView.GetWansAsync())
            {
                options.Add(new Option(
                    wan.WanInterface.ToLowerInvariant(),
                    GatewayWanHelper.FormatWanLabel(
                        wan.FriendlyName, GatewayWanHelper.WanIndexFromKey(wan.WanInterface), null, null),
                    wan.IsPrimary,
                    NetworkUtilities.PreferredWanCounterInterface(wan.PhysicalIfName, wan.UplinkIfName)));
            }
        }
        catch { /* console unreachable - contexts below still describe the WANs we know of */ }

        try
        {
            await using var db = _siteDb.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
            var contexts = await db.WanContexts.AsNoTracking().ToListAsync();
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
                    counter));
            }
        }
        catch { /* site DB unavailable - the live WANs above stand on their own */ }

        Options = options;
        if (string.IsNullOrEmpty(SelectedKey))
            SelectedKey = ResolveDefaultKey(options);
    }

    /// <summary>
    /// The WAN the tiles open on: the one the console says holds the primary role, else the first
    /// WAN enumerated. That fallback is a guess - WAN group names carry no role information in
    /// UniFi Network - and it decides only which tile is shown first.
    /// </summary>
    internal static string ResolveDefaultKey(IReadOnlyList<Option> options)
        => options.FirstOrDefault(o => o.IsPrimary)?.Key ?? options.FirstOrDefault()?.Key ?? "";

    /// <summary>Selects a WAN, persisting it unless this is the restore of a stored value.</summary>
    public async Task SelectAsync(string key, bool persist = true)
    {
        if (!Options.Any(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase))) return;
        SelectedKey = key;
        if (persist)
        {
            try { await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, key); }
            catch { /* circuit going away - the selection still holds for this render */ }
        }
        OnChanged?.Invoke();
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
            var stored = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrEmpty(stored)
                && Options.Any(o => string.Equals(o.Key, stored, StringComparison.OrdinalIgnoreCase)))
            {
                await SelectAsync(stored, persist: false);
            }
        }
        catch { /* no interop yet - the default selection stands */ }
    }
}
