using Microsoft.Extensions.Logging;
using NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;

namespace NetworkOptimizer.Web.Components.Shared.Monitoring.LiveTraffic;

/// <summary>
/// One client's Live Traffic, for the cards on Client Performance's Data / Apps tab. It holds the
/// agent's viewer lease and refreshes every 2 s while it lives; the page creates it when the tab
/// opens and disposes it when the tab closes, which releases the lease. The cards subscribe to
/// <see cref="Changed"/> and re-render themselves, so the page does not re-render on every tick.
/// One query serves every card: the filters and the Active or Ended choice apply to all of them.
/// </summary>
public sealed class ClientLiveFeed : IDisposable
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(2);

    private readonly ILiveTrafficService _service;
    private readonly Func<Func<Task>, Task> _invoke;
    private readonly ILogger _logger;
    private readonly string _viewerId = Guid.NewGuid().ToString("N");
    private System.Threading.Timer? _timer;
    private int _refreshing;
    private bool _again;
    private bool _disposed;

    /// <param name="invoke">The page's InvokeAsync: every refresh runs on the circuit, as the page's own would.</param>
    public ClientLiveFeed(ILiveTrafficService service, Func<Func<Task>, Task> invoke,
        string clientMac, string clientName, IReadOnlyCollection<string> clientIps, ILogger logger)
    {
        _service = service;
        _invoke = invoke;
        _logger = logger;
        ClientMac = clientMac;
        ClientName = clientName;
        Scope = new LiveClientScope(clientMac, clientIps);
    }

    public string ClientMac { get; }
    public string ClientName { get; }
    public LiveClientScope Scope { get; }

    /// <summary>The latest answer, in the held order; null until the first one lands.</summary>
    public LiveTrafficView? View { get; private set; }

    /// <summary>
    /// UniFi Network's flow records for the client, asked for only while no agent can serve Live;
    /// null until then.
    /// </summary>
    public LiveHistoryView? History { get; private set; }
    private DateTime _historyAt = DateTime.MinValue;
    // A filter changed since History was last asked: ask again now rather than in 15 s.
    private bool _historyChanged;

    /// <summary>Every active flow of the client, unfiltered: its totals and busiest apps, for the Speed tab.</summary>
    public LiveTrafficView? Overall { get; private set; }

    /// <summary>The client's live apps, active flows only, whatever <see cref="Ended"/> says.</summary>
    public IReadOnlyList<LiveGroupRow> Apps { get; private set; } = Array.Empty<LiveGroupRow>();

    public LiveOrderHold Hold { get; } = new();
    public List<LiveTrafficFilter> Filters { get; } = new();
    public bool Ended { get; private set; }
    public LiveFlowSort Sort { get; private set; } = LiveFlowSort.Download;
    public bool Descending { get; private set; } = true;
    public LiveDnsMode DnsMode { get; private set; } = LiveDnsMode.All;

    /// <summary>Raised after every refresh, off the render thread: handlers marshal with InvokeAsync.</summary>
    public event Func<Task>? Changed;

    /// <summary>
    /// The page's tab is hidden: refreshes still run, which keeps the agent's lease, but the cards are
    /// not told, so a tab left in the background has no backlog of redraws to catch up on.
    /// </summary>
    public bool Hidden { get; private set; }

    /// <summary>Shown again: asks now, so the cards draw the latest at once.</summary>
    public Task SetHiddenAsync(bool hidden)
    {
        if (Hidden == hidden) return Task.CompletedTask;
        Hidden = hidden;
        return hidden ? Task.CompletedTask : RefreshAsync();
    }

    public void Start()
    {
        if (_disposed || _timer != null) return;
        _timer = new System.Threading.Timer(_ =>
        {
            if (_disposed) return;
            try { _ = _invoke(RefreshAsync); }
            catch { /* circuit going away */ }
        }, null, TimeSpan.Zero, RefreshEvery);
    }

    /// <summary>
    /// Asks again now. One asked while another is in flight runs right after it, so a change the
    /// user just made is never left for the next tick.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            _again = true;
            return;
        }
        try
        {
            var query = new LiveTrafficQuery
            {
                ViewerId = _viewerId,
                Client = Scope,
                Filters = Filters.ToList(),
                GroupBy = LiveTrafficGroupBy.App,
                Ended = Ended,
                DnsMode = DnsMode,
                IncludeSankey = false,
                Sort = Sort,
                Descending = Descending,
                MaxRows = 50,
            };
            var view = await _service.GetViewAsync(query);
            // The apps are what is moving now; with Ended shown they are asked for apart.
            var endedApps = Ended
                ? (await _service.GetViewAsync(query with { Ended = false, IncludeDns = false, MaxRows = 1 })).Groups
                : null;
            // The Speed tab's note counts everything the client is moving, whatever the cards filter.
            var overall = Ended || Filters.Count > 0
                ? await _service.GetViewAsync(query with { Ended = false, Filters = [], IncludeDns = false, MaxRows = 1 })
                : null;
            // No agent can serve Live: the console's own flow records for the client stand in,
            // read no more often than History refreshes them.
            var history = History;
            if (view.State is LiveTrafficState.NoGatewayAgent or LiveTrafficState.AgentNeedsUpdate
                    or LiveTrafficState.NoConntrack or LiveTrafficState.MonitoringOff
                && (history is null or { State: LiveHistoryState.Loading } || _historyChanged
                    || DateTime.UtcNow - _historyAt >= FlowHistoryCache.RefreshEvery))
            {
                _historyAt = DateTime.UtcNow;
                _historyChanged = false;
                history = await _service.GetHistoryAsync(new LiveHistoryQuery
                {
                    ViewerId = _viewerId,
                    Client = Scope,
                    Filters = Filters.ToList(),
                    Sort = LiveHistorySort.Time,
                    MaxRows = 50,
                });
            }
            if (_disposed) return;
            View = Hold.Apply(view, $"{Ended}|{Sort}|{Descending}|{string.Join(",", Filters)}");
            Apps = endedApps ?? View.Groups;
            Overall = overall ?? View;
            History = history;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Client Live Traffic refresh failed");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
        await NotifyAsync();
        if (_again)
        {
            _again = false;
            await RefreshAsync();
        }
    }

    private async Task NotifyAsync()
    {
        if (_disposed || Hidden || Changed is not { } changed) return;
        foreach (var handler in changed.GetInvocationList().Cast<Func<Task>>())
        {
            try { await handler(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Client Live Traffic: a card failed to update"); }
        }
    }

    // ---------------------------------------------------------------- the user's choices

    public LiveTrafficFilter? Kept(LiveTrafficField field, string value) =>
        Filters.FirstOrDefault(f => f.Field == field && !f.Exclude && string.Equals(f.Value, value, StringComparison.OrdinalIgnoreCase));

    public Task AddFilterAsync(LiveTrafficFilter filter)
    {
        Filters.RemoveAll(f => f.Field == filter.Field && string.Equals(f.Value, filter.Value, StringComparison.OrdinalIgnoreCase));
        Filters.Add(filter);
        _historyChanged = true;
        return RefreshAsync();
    }

    public Task RemoveFilterAsync(LiveTrafficFilter filter)
    {
        Filters.Remove(filter);
        _historyChanged = true;
        return RefreshAsync();
    }

    public Task ClearFiltersAsync()
    {
        Filters.Clear();
        _historyChanged = true;
        return RefreshAsync();
    }

    /// <summary>A click keeps only that value, a second click lets it go again; Alt-click hides it.</summary>
    public Task ToggleAsync(bool alt, LiveTrafficField field, string value)
    {
        if (alt) return AddFilterAsync(new LiveTrafficFilter(field, value, Exclude: true));
        return Kept(field, value) is { } kept ? RemoveFilterAsync(kept) : AddFilterAsync(new LiveTrafficFilter(field, value));
    }

    public Task SetEndedAsync(bool ended)
    {
        if (Ended == ended) return Task.CompletedTask;
        Ended = ended;
        return RefreshAsync();
    }

    /// <summary>A new column starts biggest or newest first, names A to Z; the same column again flips it.</summary>
    public Task SetSortAsync(LiveFlowSort sort)
    {
        if (Sort == sort) Descending = !Descending;
        else
        {
            Sort = sort;
            Descending = sort is not (LiveFlowSort.Client or LiveFlowSort.Destination or LiveFlowSort.App or LiveFlowSort.Route);
        }
        return RefreshAsync();
    }

    public Task SetDnsModeAsync(LiveDnsMode mode)
    {
        if (DnsMode == mode) return Task.CompletedTask;
        DnsMode = mode;
        return RefreshAsync();
    }

    /// <summary>What a value click does in this client's cards.</summary>
    public LiveTrafficRowContext RowContext => _rowContext ??= new LiveTrafficRowContext
    {
        IsKept = (kind, value) => Kept(kind, value) != null,
        Toggle = (e, kind, value) => ToggleAsync(e.AltKey, kind, value),
        Exclude = AddFilterAsync,
    };
    private LiveTrafficRowContext? _rowContext;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        try { _ = _service.ReleaseAsync(_viewerId); }
        catch { /* circuit going away */ }
    }
}
