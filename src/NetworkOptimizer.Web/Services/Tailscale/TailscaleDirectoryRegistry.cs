using System.Collections.Concurrent;
using System.Text.Json;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Monitoring.Tailscale;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Tailscale;

/// <summary>What the last refresh of a site's tailnet found, for the Settings card.</summary>
/// <param name="Error">Why the device list could not be read, or null.</param>
/// <param name="AgentName">The On-Site Agent whose host's Tailscale is also read, or null when none is.</param>
/// <param name="AgentRoutes">Whether that host is the tailnet's subnet router or exit node.</param>
public sealed record TailnetStatus(bool Enabled, int Devices, DateTime? ReadAt, string? Error, string? AgentName, bool AgentRoutes);

/// <summary>What Test Connection found: the device count, or why the list could not be read.</summary>
public sealed record TailnetTestResult(int? Devices, string? Error);

/// <summary>
/// Each site's tailnet, held in memory and refreshed in the background: the device list every 60 s.
/// When an On-Site Agent's host runs Tailscale, its status (how each peer connects) every 15 s, and its
/// routed-traffic attribution every 10 s while Live Traffic is viewed. Agents with no Tailscale are
/// asked again only every 10 minutes. Nothing polls a site nobody is looking at: a read that finds its
/// data due starts the refresh and returns what it has. Internal readers use this directly; callers on
/// a user's behalf go through <see cref="ITailscaleDirectory"/>.
/// </summary>
public sealed class TailscaleDirectoryRegistry : ISiteScopedRegistry
{
    private static readonly TimeSpan DeviceEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LocalEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SnatEvery = TimeSpan.FromSeconds(10);
    /// <summary>A site whose agents run no Tailscale is asked again after this long.</summary>
    private static readonly TimeSpan NoAgentRecheck = TimeSpan.FromMinutes(10);
    /// <summary>Attribution keeps refreshing this long after the last read that wanted it.</summary>
    private static readonly TimeSpan SnatWantedFor = TimeSpan.FromSeconds(45);
    /// <summary>A path read at test time reuses an agent status this fresh.</summary>
    private static readonly TimeSpan PathFresh = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FirstReadWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DerpMapEvery = TimeSpan.FromHours(24);

    private readonly ISystemSettingsService _settings;
    private readonly ICredentialProtectionService _credentials;
    private readonly ISiteHttpClientProvider _http;
    private readonly AgentTunnelRegistry _tunnels;
    private readonly AgentProbeService _agentProbes;
    private readonly ILogger<TailscaleDirectoryRegistry> _logger;
    private readonly ConcurrentDictionary<string, SiteState> _sites = new(StringComparer.OrdinalIgnoreCase);
    private DerpMapClient.DerpMap? _derpMap;
    private DateTime _derpMapAt;

    public TailscaleDirectoryRegistry(
        ISystemSettingsService settings,
        ICredentialProtectionService credentials,
        ISiteHttpClientProvider http,
        AgentTunnelRegistry tunnels,
        AgentProbeService agentProbes,
        ILogger<TailscaleDirectoryRegistry> logger)
    {
        _settings = settings;
        _credentials = credentials;
        _http = http;
        _tunnels = tunnels;
        _agentProbes = agentProbes;
        _logger = logger;
    }

    private sealed class SiteState
    {
        public readonly SemaphoreSlim Refresh = new(1, 1);
        public volatile TailnetSnapshot Snapshot = TailnetSnapshot.Empty;
        public volatile TailnetStatus? Status;
        public StoredSettings? Settings;
        public TailscaleApiClient? Api;
        public string? ApiKey;
        public IReadOnlyList<TailnetDevice>? Devices;
        public string? Error;
        /// <summary>The agent whose host's Tailscale is read; null when none has one.</summary>
        public AgentTunnelConnection? Agent;
        public TailscaleLocalStatus? Local;
        public IReadOnlyList<TailnetSnatEntry> Snat = [];
        public DateTime SettingsAt, DevicesAt, LocalAt, SnatAt, SnatWantedUntil, AgentSearchedAt;
        public int Refreshing;
    }

    /// <summary>The site's settings with its secrets decrypted, for the refresh only.</summary>
    internal sealed record StoredSettings(TailscaleSettings Settings, string? ClientSecret, string? HeadscaleKey);

    private SiteState State(string slug) => _sites.GetOrAdd(slug, _ => new SiteState());

    /// <summary>The site's current tailnet, starting a background refresh when part of it is due.</summary>
    /// <param name="wantSnat">True from Live Traffic: keeps routed-traffic attribution refreshing.</param>
    public TailnetSnapshot Get(string slug, bool wantSnat = false)
    {
        var state = State(slug);
        var now = DateTime.UtcNow;
        if (wantSnat) state.SnatWantedUntil = now + SnatWantedFor;
        if (Due(state, now) && Interlocked.Exchange(ref state.Refreshing, 1) == 0)
            _ = Task.Run(async () =>
            {
                try { await RefreshAsync(slug, state, force: false, CancellationToken.None); }
                finally { Interlocked.Exchange(ref state.Refreshing, 0); }
            });
        return state.Snapshot;
    }

    /// <summary>
    /// The site's tailnet, waiting for the first refresh when there has been none, so a page's first
    /// load already has names. Later reads return at once, like <see cref="Get"/>.
    /// </summary>
    public async Task<TailnetSnapshot> GetReadyAsync(string slug, CancellationToken ct = default)
    {
        var state = State(slug);
        // At most a few seconds: a slow control server finishes in the background instead of holding the page.
        if (state.Status == null)
            await Task.WhenAny(RefreshAsync(slug, state, force: false, CancellationToken.None), Task.Delay(FirstReadWait, ct));
        return Get(slug);
    }

    /// <summary>Whether the site's tailnet holds an address (for classifying a custom IP pool).</summary>
    public bool IsPeer(string slug, string ip) => Get(slug).IsPeer(ip);

    /// <summary>What the last refresh found; null before the first one.</summary>
    public TailnetStatus? StatusFor(string slug) => State(slug).Status;

    /// <summary>Settings changed: the next read reloads them, looks for an agent again, and refreshes everything.</summary>
    public void Invalidate(string slug)
    {
        var state = State(slug);
        state.Settings = null;
        state.Api = null;
        state.Agent = null;
        state.SettingsAt = state.DevicesAt = state.LocalAt = state.SnatAt = state.AgentSearchedAt = DateTime.MinValue;
        Get(slug);
    }

    /// <summary>Refreshes the site now and returns the result; used after a save.</summary>
    public async Task<TailnetSnapshot> RefreshNowAsync(string slug, CancellationToken ct)
    {
        var state = State(slug);
        await RefreshAsync(slug, state, force: true, ct);
        return state.Snapshot;
    }

    /// <summary>
    /// How the agent host's tailscaled reaches a peer right now, exactly: for a speed test result. Null
    /// when no agent here runs Tailscale or the address is no live peer of it; the flows then answer.
    /// </summary>
    public async Task<TailnetPathInfo?> ReadPathAsync(string slug, string ip, CancellationToken ct)
    {
        var state = State(slug);
        if (state.Agent is not { } agent) return null;
        var local = state.Local;
        if (local == null || DateTime.UtcNow - state.LocalAt > PathFresh)
        {
            try
            {
                local = await AskStatusAsync(slug, agent.AgentId, ct);
                state.Local = local;
                state.LocalAt = DateTime.UtcNow;
            }
            catch (TailscaleLocalException ex)
            {
                _logger.LogDebug("No Tailscale path for {Ip} on site {Site}: {Reason}", ip, slug, ex.Message);
                return null;
            }
        }
        var path = TailnetSnapshot.Build(null, local, null, DateTime.UtcNow, _derpMap).Peer(ip)?.Path;
        return path;
    }

    /// <summary>Reads the device list the given settings point at, storing nothing: the card's Test Connection.</summary>
    internal async Task<TailnetTestResult> TestAsync(string slug, StoredSettings stored, CancellationToken ct)
    {
        try
        {
            return new TailnetTestResult((await ReadDevicesAsync(slug, stored, null, ct)).Count, null);
        }
        catch (TailnetDirectoryException ex)
        {
            return new TailnetTestResult(null, ex.Message);
        }
    }

    private static bool Due(SiteState state, DateTime now)
    {
        // Settings are re-read on the device-list clock even while off, so a restored backup takes effect.
        if (state.Settings == null || now - state.SettingsAt >= DeviceEvery) return true;
        if (!state.Settings.Settings.Enabled) return false;
        if (now - state.DevicesAt >= DeviceEvery) return true;
        if (state.Agent == null) return now - state.AgentSearchedAt >= NoAgentRecheck;
        if (now - state.LocalAt >= LocalEvery) return true;
        return state.Snapshot.Self?.RouterBadge != null && now < state.SnatWantedUntil && now - state.SnatAt >= SnatEvery;
    }

    private async Task RefreshAsync(string slug, SiteState state, bool force, CancellationToken ct)
    {
        await state.Refresh.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            if (force || state.Settings == null || now - state.SettingsAt >= DeviceEvery)
                await LoadSettingsAsync(slug, state);
            var stored = state.Settings;
            if (stored == null || !stored.Settings.Enabled)
            {
                state.Snapshot = TailnetSnapshot.Empty;
                state.Devices = null;
                state.Local = null;
                state.Agent = null;
                state.Snat = [];
                state.Status = new TailnetStatus(false, 0, null, null, null, false);
                return;
            }

            if (force || now - state.DevicesAt >= DeviceEvery)
            {
                try
                {
                    state.Devices = await ReadDevicesAsync(slug, stored, state, ct);
                    state.Error = null;
                }
                catch (TailnetDirectoryException ex)
                {
                    // The last good list stays: one failed read should not strip every name.
                    state.Error = ex.Message;
                    _logger.LogWarning("Tailscale device list for site {Site} not read: {Reason}", slug, ex.Message);
                }
                state.DevicesAt = now;
                await RefreshDerpMapAsync(ct);
            }

            await RefreshAgentAsync(slug, state, force, now, ct);

            var snapshot = TailnetSnapshot.Build(state.Devices, state.Local, state.Snat, now, _derpMap);
            state.Snapshot = snapshot;
            state.Status = new TailnetStatus(true, snapshot.Peers.Count + (snapshot.Self != null ? 1 : 0), now, state.Error,
                state.Agent?.AgentName, snapshot.Self?.RouterBadge != null && state.Agent != null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Tailscale refresh for site {Site} failed", slug);
        }
        finally
        {
            state.Refresh.Release();
        }
    }

    /// <summary>
    /// Finds and reads the On-Site Agent whose host runs Tailscale: the one whose node routes for the
    /// tailnet when there is one, since only its conntrack holds routed peers. A site without one is
    /// asked again every 10 minutes; one that stops answering is looked for again at once.
    /// </summary>
    private async Task RefreshAgentAsync(string slug, SiteState state, bool force, DateTime now, CancellationToken ct)
    {
        if (state.Agent is { } current && (force || now - state.LocalAt >= LocalEvery))
        {
            try
            {
                state.Local = await AskStatusAsync(slug, current.AgentId, ct);
                state.LocalAt = now;
            }
            catch (TailscaleLocalException ex)
            {
                _logger.LogDebug("Tailscale on agent {Agent} for site {Site} stopped answering: {Reason}", current.AgentName, slug, ex.Message);
                state.Agent = null;
                state.Local = null;
                state.Snat = [];
                state.AgentSearchedAt = DateTime.MinValue;
            }
        }

        if (state.Agent == null && (force || now - state.AgentSearchedAt >= NoAgentRecheck))
        {
            state.AgentSearchedAt = now;
            var answered = new List<(AgentTunnelConnection Agent, TailscaleLocalStatus Status)>();
            foreach (var agent in _tunnels.GetForSite(slug).Where(c => c.HasCapability(TailscaleLocal.Capability)))
            {
                try { answered.Add((agent, await AskStatusAsync(slug, agent.AgentId, ct))); }
                catch (TailscaleLocalException) { /* No Tailscale on this agent's host. */ }
            }
            if (answered.Count > 0)
            {
                var pick = answered.FirstOrDefault(a => TailnetSnapshot.Build(state.Devices, a.Status, null, now).Self?.RouterBadge != null);
                (state.Agent, state.Local) = pick.Agent != null ? (pick.Agent, pick.Status) : (answered[0].Agent, answered[0].Status);
                state.LocalAt = now;
                _logger.LogInformation("Reading Tailscale on On-Site Agent {Agent} for site {Site}", state.Agent.AgentName, slug);
            }
        }

        var routes = state.Agent != null && TailnetSnapshot.Build(state.Devices, state.Local, null, now).Self?.RouterBadge != null;
        if (!routes)
        {
            state.Snat = [];
        }
        else if (force || (now < state.SnatWantedUntil && now - state.SnatAt >= SnatEvery))
        {
            try
            {
                var json = await AskAgentAsync(slug, state.Agent!.AgentId, TailscaleLocal.SnatKind, ct);
                state.Snat = JsonSerializer.Deserialize<List<TailnetSnatEntry>>(json) ?? [];
            }
            catch (TailscaleLocalException ex)
            {
                state.Snat = [];
                _logger.LogDebug("Routed-traffic attribution on agent {Agent} for site {Site} not read: {Reason}", state.Agent!.AgentName, slug, ex.Message);
            }
            state.SnatAt = now;
        }
    }

    private async Task RefreshDerpMapAsync(CancellationToken ct)
    {
        if (_derpMap != null && DateTime.UtcNow - _derpMapAt < DerpMapEvery) return;
        try
        {
            _derpMap = await DerpMapClient.GetAsync(await _http.GetClientAsync(null, ct), ct);
        }
        catch (Exception ex) when (ex is TailnetDirectoryException or JsonException)
        {
            _logger.LogDebug("Tailscale's DERP map not read: {Reason}", ex.Message);
        }
        // A failed read waits the full interval too: relays still match by hostname meanwhile.
        _derpMapAt = DateTime.UtcNow;
    }

    private async Task<StoredSettings?> LoadSettingsAsync(string slug, SiteState state)
    {
        try
        {
            var stored = await ReadSettingsAsync(slug);
            state.Settings = stored;
            state.SettingsAt = DateTime.UtcNow;
            return stored;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tailscale settings for site {Site} not read", slug);
            return null;
        }
    }

    /// <summary>The site's stored settings, secrets decrypted.</summary>
    internal async Task<StoredSettings> ReadSettingsAsync(string slug)
    {
        async Task<string?> Get(string key) => await _settings.GetAsync(slug, key);
        string? Decrypt(string? value) =>
            string.IsNullOrEmpty(value) ? null : _credentials.IsEncrypted(value) ? _credentials.Decrypt(value) : value;

        var clientSecret = Decrypt(await Get(TailscaleSettingKeys.ClientSecret));
        var headscaleKey = Decrypt(await Get(TailscaleSettingKeys.HeadscaleKey));
        var settings = new TailscaleSettings
        {
            Enabled = await Get(TailscaleSettingKeys.Enabled) == "true",
            ControlServer = Enum.TryParse<TailnetControlServer>(await Get(TailscaleSettingKeys.ControlServer), out var cs) ? cs : TailnetControlServer.Tailscale,
            ClientId = await Get(TailscaleSettingKeys.ClientId) ?? "",
            HeadscaleUrl = await Get(TailscaleSettingKeys.HeadscaleUrl) ?? "",
            HasClientSecret = !string.IsNullOrEmpty(clientSecret),
            HasHeadscaleKey = !string.IsNullOrEmpty(headscaleKey),
        };
        return new StoredSettings(settings, clientSecret, headscaleKey);
    }

    /// <summary>Stores the site's settings; an empty secret keeps the stored one.</summary>
    internal async Task SaveSettingsAsync(string slug, TailscaleSettingsUpdate update)
    {
        var s = update.Settings;
        Task Set(string key, string? value) => _settings.SetAsync(slug, key, value);
        await Set(TailscaleSettingKeys.Enabled, s.Enabled ? "true" : "false");
        await Set(TailscaleSettingKeys.ControlServer, s.ControlServer.ToString());
        await Set(TailscaleSettingKeys.ClientId, s.ClientId.Trim());
        await Set(TailscaleSettingKeys.HeadscaleUrl, s.HeadscaleUrl.Trim());
        if (!string.IsNullOrWhiteSpace(update.ClientSecret))
            await Set(TailscaleSettingKeys.ClientSecret, _credentials.EncryptForSite(update.ClientSecret.Trim(), slug));
        if (!string.IsNullOrWhiteSpace(update.HeadscaleKey))
            await Set(TailscaleSettingKeys.HeadscaleKey, _credentials.EncryptForSite(update.HeadscaleKey.Trim(), slug));
        Invalidate(slug);
    }

    private string? SiteArg(string slug) => slug == SiteManagementService.DefaultSiteSlug ? null : slug;

    private async Task<IReadOnlyList<TailnetDevice>> ReadDevicesAsync(string slug, StoredSettings stored, SiteState? cacheIn, CancellationToken ct)
    {
        var s = stored.Settings;
        var http = await _http.GetClientAsync(SiteArg(slug), ct);
        if (s.ControlServer == TailnetControlServer.Headscale)
        {
            if (string.IsNullOrWhiteSpace(stored.HeadscaleKey))
                throw new TailnetDirectoryException("Enter a Headscale API key.");
            return await new HeadscaleApiClient(http, s.HeadscaleUrl, stored.HeadscaleKey).GetDevicesAsync(ct);
        }
        if (string.IsNullOrWhiteSpace(s.ClientId) || string.IsNullOrWhiteSpace(stored.ClientSecret))
            throw new TailnetDirectoryException("Enter the OAuth client ID and secret.");
        // The client keeps its token for the hour it lives; a test builds its own and keeps nothing.
        var key = $"{s.ClientId}|{stored.ClientSecret}";
        TailscaleApiClient api;
        if (cacheIn != null && cacheIn.Api != null && cacheIn.ApiKey == key) api = cacheIn.Api;
        else
        {
            api = new TailscaleApiClient(http, s.ClientId, stored.ClientSecret);
            if (cacheIn != null) (cacheIn.Api, cacheIn.ApiKey) = (api, key);
        }
        return await api.GetDevicesAsync(ct);
    }

    private async Task<TailscaleLocalStatus> AskStatusAsync(string slug, int agentId, CancellationToken ct)
    {
        var json = await AskAgentAsync(slug, agentId, TailscaleLocal.StatusKind, ct);
        return JsonSerializer.Deserialize<TailscaleLocalStatus>(json)
            ?? throw new TailscaleLocalException("The On-Site Agent returned an empty Tailscale status.");
    }

    private async Task<string> AskAgentAsync(string slug, int agentId, string kind, CancellationToken ct)
    {
        var response = await _agentProbes.RunAsync(slug, new ProbeRequest { Kind = kind }, AgentTimeout, ct, agentId);
        if (response == null || !response.Success)
            throw new TailscaleLocalException(response?.Error is { Length: > 0 } error
                ? $"On-Site Agent: {error}" : "The On-Site Agent did not answer.");
        return response.ResultJson;
    }

    /// <inheritdoc />
    public Func<ValueTask>? EvictSite(string slug)
    {
        _sites.TryRemove(slug, out _);
        return null;
    }
}
