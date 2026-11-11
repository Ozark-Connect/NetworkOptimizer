using System.Collections.Concurrent;
using System.Text.Json;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Monitoring.Tailscale;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Tailscale;

/// <summary>What the last refresh of a site's tailnet found, for the Settings card.</summary>
/// <param name="DeviceError">Why the device list could not be read, or null.</param>
/// <param name="LocalError">Why the local status could not be read, or null (also null when it is off).</param>
/// <param name="SnatError">Why the conntrack table could not be read, or null.</param>
public sealed record TailnetStatus(
    bool Enabled, int Devices, DateTime? ReadAt, string? DeviceError,
    bool LocalRead, int DirectPeers, string? LocalError, int SnatEntries, string? SnatError);

/// <summary>
/// Each site's tailnet, held in memory and refreshed in the background: the device list every 60 s,
/// local status every 15 s, and routed-traffic attribution every 10 s while Live Traffic asks for it.
/// Nothing polls a site nobody is looking at: a read that finds its data due starts the refresh and
/// returns what it has. Internal readers use this directly; callers on a user's behalf go through
/// <see cref="ITailscaleDirectory"/>.
/// </summary>
public sealed class TailscaleDirectoryRegistry : ISiteScopedRegistry
{
    private static readonly TimeSpan DeviceEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LocalEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SnatEvery = TimeSpan.FromSeconds(10);
    /// <summary>Attribution keeps refreshing this long after the last read that wanted it.</summary>
    private static readonly TimeSpan SnatWantedFor = TimeSpan.FromSeconds(45);
    /// <summary>A path read at test time reuses a local status this fresh.</summary>
    private static readonly TimeSpan PathFresh = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FirstReadWait = TimeSpan.FromSeconds(3);

    private readonly ISystemSettingsService _settings;
    private readonly ICredentialProtectionService _credentials;
    private readonly ISiteHttpClientProvider _http;
    private readonly AgentTunnelRegistry _tunnels;
    private readonly AgentProbeService _agentProbes;
    private readonly ILogger<TailscaleDirectoryRegistry> _logger;
    private readonly ConcurrentDictionary<string, SiteState> _sites = new(StringComparer.OrdinalIgnoreCase);

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
        public TailscaleLocalStatus? Local;
        public IReadOnlyList<TailnetSnatEntry> Snat = [];
        public DateTime SettingsAt, DevicesAt, LocalAt, SnatAt, SnatWantedUntil;
        public string? DeviceError, LocalError, SnatError;
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

    /// <summary>Settings changed: the next read reloads them and refreshes everything at once.</summary>
    public void Invalidate(string slug)
    {
        var state = State(slug);
        state.Settings = null;
        state.Api = null;
        state.SettingsAt = state.DevicesAt = state.LocalAt = state.SnatAt = DateTime.MinValue;
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
    /// How the local tailscaled reaches a peer right now, for recording on a speed test result. Null
    /// when the site reads no local status, it cannot be read, or the address is no live peer.
    /// </summary>
    public async Task<TailnetPathInfo?> ReadPathAsync(string slug, string ip, CancellationToken ct)
    {
        var state = State(slug);
        var stored = state.Settings ?? await LoadSettingsAsync(slug, state);
        if (stored is not { Settings: { Enabled: true, LocalSource: not TailnetLocalSource.Off } settings }) return null;
        var now = DateTime.UtcNow;
        var local = state.Local;
        if (local == null || now - state.LocalAt > PathFresh)
        {
            try
            {
                local = await ReadLocalAsync(slug, settings, ct);
                state.Local = local;
                state.LocalAt = now;
                state.LocalError = null;
            }
            catch (TailscaleLocalException ex)
            {
                _logger.LogDebug("No Tailscale path for {Ip} on site {Site}: {Reason}", ip, slug, ex.Message);
                return null;
            }
        }
        var peer = TailnetSnapshot.Build(null, local, null, now).Peer(ip);
        return peer?.Path;
    }

    /// <summary>
    /// Reads everything the given settings point at, without storing any of it: the Settings card's
    /// Test Connection. Each part reports its own failure.
    /// </summary>
    internal async Task<TailnetTestResult> TestAsync(string slug, StoredSettings stored, CancellationToken ct)
    {
        var s = stored.Settings;
        IReadOnlyList<TailnetDevice>? devices = null;
        string? deviceError = null;
        if (s.DeviceSource != TailnetDeviceSource.LocalStatus)
        {
            try { devices = await ReadDevicesAsync(slug, stored, null, ct); }
            catch (TailnetDirectoryException ex) { deviceError = ex.Message; }
        }

        TailscaleLocalStatus? local = null;
        string? localError = null;
        if (s.LocalSource != TailnetLocalSource.Off)
        {
            try { local = await ReadLocalAsync(slug, s, ct); }
            catch (TailscaleLocalException ex) { localError = ex.Message; }
        }

        int? snat = null;
        string? snatError = null;
        if (s.AttributeRoutedTraffic && s.LocalSource != TailnetLocalSource.Off)
        {
            try { snat = (await ReadSnatAsync(slug, s, ct)).Count; }
            catch (TailscaleLocalException ex) { snatError = ex.Message; }
        }

        var snapshot = TailnetSnapshot.Build(devices, local, null, DateTime.UtcNow);
        return new TailnetTestResult(
            devices?.Count, deviceError,
            local == null ? null : local.Peers.Count, local == null ? null : snapshot.Peers.Count(p => p.Path?.Kind == TailnetPathKind.Direct),
            local?.Self == null ? null : TailnetNames.MachineName(local.Self.DnsName) ?? local.Self.HostName, localError,
            snat, snatError);
    }

    private static bool Due(SiteState state, DateTime now)
    {
        // Settings are re-read on the device-list clock even while off, so a restored backup takes effect.
        if (state.Settings == null || now - state.SettingsAt >= DeviceEvery) return true;
        var s = state.Settings.Settings;
        if (!s.Enabled) return false;
        if (s.DeviceSource != TailnetDeviceSource.LocalStatus && now - state.DevicesAt >= DeviceEvery) return true;
        if (s.LocalSource != TailnetLocalSource.Off && now - state.LocalAt >= LocalEvery) return true;
        return s.AttributeRoutedTraffic && s.LocalSource != TailnetLocalSource.Off
            && now < state.SnatWantedUntil && now - state.SnatAt >= SnatEvery;
    }

    private async Task RefreshAsync(string slug, SiteState state, bool force, CancellationToken ct)
    {
        await state.Refresh.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            // Settings are re-read with the device list, so a change made elsewhere lands within a minute.
            if (force || state.Settings == null || now - state.SettingsAt >= DeviceEvery)
                await LoadSettingsAsync(slug, state);
            var stored = state.Settings;
            if (stored == null || !stored.Settings.Enabled)
            {
                state.Snapshot = TailnetSnapshot.Empty;
                state.Devices = null;
                state.Local = null;
                state.Snat = [];
                state.Status = new TailnetStatus(false, 0, null, null, false, 0, null, 0, null);
                return;
            }
            var s = stored.Settings;

            if (s.DeviceSource == TailnetDeviceSource.LocalStatus)
            {
                state.Devices = null;
                state.DeviceError = null;
            }
            else if (force || now - state.DevicesAt >= DeviceEvery)
            {
                try
                {
                    state.Devices = await ReadDevicesAsync(slug, stored, state, ct);
                    state.DeviceError = null;
                }
                catch (TailnetDirectoryException ex)
                {
                    // The last good list stays: one failed read should not strip every name.
                    state.DeviceError = ex.Message;
                    _logger.LogWarning("Tailscale device list for site {Site} not read: {Reason}", slug, ex.Message);
                }
                state.DevicesAt = now;
            }

            if (s.LocalSource == TailnetLocalSource.Off)
            {
                state.Local = null;
                state.LocalError = null;
            }
            else if (force || now - state.LocalAt >= LocalEvery)
            {
                try
                {
                    state.Local = await ReadLocalAsync(slug, s, ct);
                    state.LocalError = null;
                }
                catch (TailscaleLocalException ex)
                {
                    state.Local = null;
                    state.LocalError = ex.Message;
                    _logger.LogDebug("Tailscale local status for site {Site} not read: {Reason}", slug, ex.Message);
                }
                state.LocalAt = now;
            }

            var snatOn = s.AttributeRoutedTraffic && s.LocalSource != TailnetLocalSource.Off;
            if (!snatOn)
            {
                state.Snat = [];
                state.SnatError = null;
            }
            else if (force || (now < state.SnatWantedUntil && now - state.SnatAt >= SnatEvery))
            {
                try
                {
                    state.Snat = await ReadSnatAsync(slug, s, ct);
                    state.SnatError = null;
                }
                catch (TailscaleLocalException ex)
                {
                    state.Snat = [];
                    state.SnatError = ex.Message;
                    _logger.LogDebug("Tailscale routed-traffic attribution for site {Site} not read: {Reason}", slug, ex.Message);
                }
                state.SnatAt = now;
            }

            var snapshot = TailnetSnapshot.Build(state.Devices, state.Local, state.Snat, now);
            state.Snapshot = snapshot;
            state.Status = new TailnetStatus(
                true, snapshot.Peers.Count + (snapshot.Self != null ? 1 : 0), now, state.DeviceError,
                state.Local != null, snapshot.Peers.Count(p => p.Path?.Kind == TailnetPathKind.Direct), state.LocalError,
                snapshot.SnatCount, state.SnatError);
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
            DeviceSource = Enum.TryParse<TailnetDeviceSource>(await Get(TailscaleSettingKeys.DeviceSource), out var ds) ? ds : TailnetDeviceSource.TailscaleApi,
            ClientId = await Get(TailscaleSettingKeys.ClientId) ?? "",
            Tailnet = await Get(TailscaleSettingKeys.Tailnet) ?? "",
            HeadscaleUrl = await Get(TailscaleSettingKeys.HeadscaleUrl) ?? "",
            LocalSource = Enum.TryParse<TailnetLocalSource>(await Get(TailscaleSettingKeys.LocalSource), out var ls) ? ls : TailnetLocalSource.Off,
            SocketPath = await Get(TailscaleSettingKeys.SocketPath) ?? "",
            AttributeRoutedTraffic = await Get(TailscaleSettingKeys.AttributeRouted) == "true",
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
        await Set(TailscaleSettingKeys.DeviceSource, s.DeviceSource.ToString());
        await Set(TailscaleSettingKeys.ClientId, s.ClientId.Trim());
        await Set(TailscaleSettingKeys.Tailnet, s.Tailnet.Trim());
        await Set(TailscaleSettingKeys.HeadscaleUrl, s.HeadscaleUrl.Trim());
        await Set(TailscaleSettingKeys.LocalSource, s.LocalSource.ToString());
        await Set(TailscaleSettingKeys.SocketPath, s.SocketPath.Trim());
        await Set(TailscaleSettingKeys.AttributeRouted, s.AttributeRoutedTraffic ? "true" : "false");
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
        if (s.DeviceSource == TailnetDeviceSource.HeadscaleApi)
        {
            if (string.IsNullOrWhiteSpace(stored.HeadscaleKey))
                throw new TailnetDirectoryException("Enter a Headscale API key.");
            return await new HeadscaleApiClient(http, s.HeadscaleUrl, stored.HeadscaleKey).GetDevicesAsync(ct);
        }
        if (string.IsNullOrWhiteSpace(s.ClientId) || string.IsNullOrWhiteSpace(stored.ClientSecret))
            throw new TailnetDirectoryException("Enter the OAuth client ID and secret.");
        // The client keeps its token for the hour it lives; a test builds its own and keeps nothing.
        var key = $"{s.ClientId}|{stored.ClientSecret}|{s.Tailnet}";
        TailscaleApiClient api;
        if (cacheIn != null && cacheIn.Api != null && cacheIn.ApiKey == key) api = cacheIn.Api;
        else
        {
            api = new TailscaleApiClient(http, s.ClientId, stored.ClientSecret, s.Tailnet);
            if (cacheIn != null) (cacheIn.Api, cacheIn.ApiKey) = (api, key);
        }
        return await api.GetDevicesAsync(ct);
    }

    private async Task<TailscaleLocalStatus> ReadLocalAsync(string slug, TailscaleSettings s, CancellationToken ct)
    {
        if (s.LocalSource == TailnetLocalSource.Server)
            return await TailscaleLocalApi.ReadStatusAsync(s.SocketPath, ct);
        var json = await AskAgentAsync(slug, TailscaleLocal.StatusKind, ct, s.SocketPath);
        return JsonSerializer.Deserialize<TailscaleLocalStatus>(json)
            ?? throw new TailscaleLocalException("The On-Site Agent returned an empty Tailscale status.");
    }

    private async Task<IReadOnlyList<TailnetSnatEntry>> ReadSnatAsync(string slug, TailscaleSettings s, CancellationToken ct)
    {
        if (s.LocalSource == TailnetLocalSource.Server)
            return await TailnetSnat.ReadAsync(ct);
        var json = await AskAgentAsync(slug, TailscaleLocal.SnatKind, ct);
        return JsonSerializer.Deserialize<List<TailnetSnatEntry>>(json) ?? [];
    }

    private async Task<string> AskAgentAsync(string slug, string kind, CancellationToken ct, string? socketPath = null)
    {
        var agent = _tunnels.GetForSite(slug).FirstOrDefault(c => c.HasCapability(TailscaleLocal.Capability));
        if (agent == null)
            throw new TailscaleLocalException(_tunnels.GetForSite(slug).Count > 0
                ? "This site's On-Site Agent is too old to read Tailscale. Update it in Settings - Multi-Site."
                : "This site has no On-Site Agent connected.");
        var response = await _agentProbes.RunAsync(slug, new ProbeRequest { Kind = kind, Address = socketPath?.Trim() ?? "" }, AgentTimeout, ct, agent.AgentId);
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

/// <summary>What Test Connection found, part by part; a null count with no error means the part is off.</summary>
public sealed record TailnetTestResult(
    int? Devices, string? DeviceError,
    int? LocalPeers, int? DirectPeers, string? LocalNode, string? LocalError,
    int? SnatEntries, string? SnatError);
