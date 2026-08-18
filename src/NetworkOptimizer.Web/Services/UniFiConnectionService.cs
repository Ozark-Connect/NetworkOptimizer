using System.Text.Json;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Core.Interfaces;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Interface forms of the configured primary WAN, resolved by joining networkconf
/// (which WAN is primary) with the device JSON (which interfaces carry it).
/// </summary>
/// <param name="NetworkGroup">WAN networkgroup, e.g. "WAN".</param>
/// <param name="PhysicalIfName">Physical port ifname, e.g. "eth6".</param>
/// <param name="UplinkIfName">Data-path ifname, e.g. "eth6.100"/"ppp0" (where SQM deploys).</param>
/// <param name="CounterIfName">SNMP counter ifname, e.g. "eth6" (where InfluxDB rates are stored).</param>
public record PrimaryWanInterfaces(
    string NetworkGroup,
    string? PhysicalIfName,
    string? UplinkIfName,
    string? CounterIfName);

/// <summary>
/// Manages the UniFi controller connection and configuration persistence.
/// This is a singleton service that maintains the API client across the application.
/// Configuration is stored in the database with encrypted credentials.
/// </summary>
public class UniFiConnectionService : IUniFiClientProvider, IDisposable
{
    private readonly ILogger<UniFiConnectionService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ICredentialProtectionService _credentialProtection;

    private UniFiApiClient? _client;
    // Serializes every connection-lifecycle mutation of _client (connect, reconnect,
    // tunnel-drop, disconnect). Without it, concurrent connect attempts during the
    // startup/tunnel-up window null _client out from under an in-flight login, which
    // NPE'd when the failure branch read _client.LastLoginError. OnConnectionChanged
    // is always fired AFTER the gate is released so a subscriber can't re-enter and
    // deadlock.
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private UniFiConnectionSettings? _settings;
    private bool _isConnected;
    private string? _lastError;
    private DateTime? _lastConnectedAt;

    // Console connection alerting: armed after the first successful auth so setup-time
    // failures never alert; fires once per outage after two consecutive failed auth
    // probes spanning at least the minimum window (so second-scale cookie re-login
    // bursts and single blips during provisioning restarts stay silent).
    private IAlertEventBus? _alertBus;
    private bool _consoleAlertArmed;
    private bool _consoleAlertActive;
    private int _consecutiveAuthFailures;
    private DateTime _firstAuthFailureAt;
    private readonly object _consoleAlertLock = new();
    private static readonly TimeSpan ConsoleFailureMinWindow = TimeSpan.FromSeconds(50);

    // Cache to avoid repeated DB queries
    private DateTime _cacheTime = DateTime.MinValue;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

    // Device discovery cache (30 second TTL for dashboard responsiveness)
    private List<DiscoveredDevice>? _cachedDevices;
    private DateTime _deviceCacheTime = DateTime.MinValue;
    private static readonly TimeSpan DeviceCacheDuration = TimeSpan.FromSeconds(30);

    // Network cache (1 minute TTL - keeps Live View interface labels fresh)
    private List<NetworkInfo>? _cachedNetworks;
    private DateTime _networkCacheTime = DateTime.MinValue;
    private static readonly TimeSpan NetworkCacheDuration = TimeSpan.FromMinutes(1);

    // Lazy initialization for async config loading
    private Task? _initializationTask;
    private readonly object _initLock = new();

    /// <summary>
    /// Event fired when the connection state changes (connect, disconnect, or site change).
    /// Subscribers should refresh any cached data from the controller.
    /// </summary>
    public event Action? OnConnectionChanged;

    /// <summary>
    /// Slug of the site this connection instance is bound to. The DI-constructed
    /// singleton is the default site; SiteConnectionRegistry creates additional
    /// instances for other sites with their slug.
    /// </summary>
    public string SiteSlug { get; }

    public UniFiConnectionService(ILogger<UniFiConnectionService> logger, ILoggerFactory loggerFactory, IServiceProvider serviceProvider, ICredentialProtectionService credentialProtection,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
        _credentialProtection = credentialProtection;
        SiteSlug = siteSlug;

        // Start initialization in background (non-blocking)
        StartInitializationAsync();
    }

    /// <summary>
    /// Creates a DI scope pinned to this instance's site so scoped services
    /// (repositories, DbContext) hit this site's database. Scopes created by a
    /// singleton have no HTTP context and would otherwise resolve to the
    /// default site.
    /// </summary>
    private IServiceScope CreateSiteScope()
    {
        var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(SiteSlug);
        return scope;
    }

    /// <summary>
    /// Tracks auth probe outcomes from the API client (login attempts and the API-key
    /// revalidation probes) and raises console connection alerts. Suppressed until the
    /// first successful connection and while the site's agent tunnel is still coming up.
    /// </summary>
    private void HandleAuthProbe(bool success, string? error)
    {
        bool publishFailed = false, publishRestored = false;
        lock (_consoleAlertLock)
        {
            if (success)
            {
                if (_consoleAlertActive)
                {
                    _consoleAlertActive = false;
                    publishRestored = true;
                }
                _consoleAlertArmed = true;
                _consecutiveAuthFailures = 0;
            }
            else
            {
                if (!_consoleAlertArmed || IsAwaitingAgent)
                    return;

                if (_consecutiveAuthFailures == 0)
                    _firstAuthFailureAt = DateTime.UtcNow;
                _consecutiveAuthFailures++;

                if (!_consoleAlertActive
                    && _consecutiveAuthFailures >= 2
                    && DateTime.UtcNow - _firstAuthFailureAt >= ConsoleFailureMinWindow)
                {
                    _consoleAlertActive = true;
                    publishFailed = true;
                }
            }
        }

        if (publishFailed && !IsInRolloutConsoleCycle())
        {
            PublishConsoleAlert("console.connection_failed", AlertSeverity.Warning,
                "UniFi Console connection failed",
                $"Repeated attempts to authenticate with the UniFi Console have failed. Features that read the console API (Wi-Fi Optimizer, Config Optimizer, Security Audit, Threat Intelligence) are unavailable until it recovers. Last error: {error ?? "unknown"}");
        }
        if (publishRestored && !IsInRolloutConsoleCycle())
        {
            PublishConsoleAlert("console.connection_restored", AlertSeverity.Info,
                "UniFi Console connection restored",
                "The connection to the UniFi Console has recovered.");
        }
    }

    /// <summary>
    /// Clears the consecutive-failure count on intentional teardown (manual disconnect,
    /// agent tunnel drop) so failures from before the teardown can't pair with a failure
    /// from a later reconnect attempt and fire a spurious alert. Keeps the active-alert
    /// flag so a restore still pairs with an already-published failure.
    /// </summary>
    private void ResetConsoleFailureCount()
    {
        lock (_consoleAlertLock)
        {
            _consecutiveAuthFailures = 0;
        }
    }

    private bool IsInRolloutConsoleCycle()
    {
        try
        {
            var suppression = _serviceProvider.GetService<Firmware.RolloutSuppressionRegistry>();
            return suppression?.IsInRolloutWindow(SiteSlug, null, DateTime.UtcNow) == true;
        }
        catch { return false; }
    }

    private void PublishConsoleAlert(string eventType, AlertSeverity severity, string title, string message)
    {
        try
        {
            var bus = _alertBus ??= _serviceProvider.GetService<IAlertEventBus>();
            if (bus == null)
                return;

            var evt = new AlertEvent
            {
                EventType = eventType,
                Source = "console",
                Severity = severity,
                Title = title,
                Message = message,
                SiteSlug = SiteSlug == SiteManagementService.DefaultSiteSlug ? null : SiteSlug
            };
            _ = Task.Run(() => bus.PublishAsync(evt).AsTask());
            _logger.LogInformation("Published {EventType} alert event for site {Site}", eventType, SiteSlug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish console connection alert event");
        }
    }

    /// <summary>Per-site setting key: reach this site's console through its agent tunnel.</summary>
    public const string ConsoleViaAgentKey = "console.via_agent";

    // How the CURRENT client was built, not how the site is configured now. The teardown hooks
    // below used to re-read the setting, which answers a different question: whether the console is
    // meant to route through the agent from here on. Those diverge the moment coverage is switched
    // off with a tunnel-routed console still connected - the hooks then declined to tear anything
    // down, and the client sat "connected" against a loopback proxy whose tunnel had died, with no
    // path back (every automatic reconnect is gated on !IsConnected).
    private bool _clientViaAgent;

    /// <summary>Shown while a directly-connected console answers the handshake but never replies.</summary>
    private const string ConsoleUnresponsiveMessage =
        "Your UniFi Console accepted the connection but stopped responding. It may be restarting or upgrading. Network Optimizer keeps trying and will reconnect on its own.";

    /// <summary>Shown while a site's agent-tunneled console waits for the agent to come online.</summary>
    private const string AwaitingAgentMessage =
        "This site's console connects through its on-site agent, which isn't online yet. It'll connect automatically as soon as the agent comes online.";

    /// <summary>Per-site setting key: the UniFi Console's display name (system.name).</summary>
    public const string ConsoleNameKey = "console.name";

    /// <summary>
    /// Fetches the console's display name from the controller and caches it in the
    /// current site's database so the Sites listing and wizard can show it without
    /// a live call. Best-effort: failures leave the previous value untouched.
    /// </summary>
    private async Task RefreshConsoleNameAsync()
    {
        try
        {
            if (_client == null)
                return;
            var name = await _client.GetConsoleNameAsync();
            if (string.IsNullOrWhiteSpace(name))
                return;

            using var scope = CreateSiteScope();
            var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
            var setting = await db.SystemSettings.FindAsync(ConsoleNameKey);
            if (setting == null)
                db.SystemSettings.Add(new SystemSetting { Key = ConsoleNameKey, Value = name.Trim() });
            else
            {
                setting.Value = name.Trim();
                setting.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh console name");
        }
    }

    /// <summary>The cached UniFi Console display name for this site, if known.</summary>
    public async Task<string?> GetCachedConsoleNameAsync()
    {
        try
        {
            using var scope = CreateSiteScope();
            var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
            var setting = await db.SystemSettings.FindAsync(ConsoleNameKey);
            return string.IsNullOrWhiteSpace(setting?.Value) ? null : setting.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Whether this site's console is configured to be reached through its agent tunnel.</summary>
    public async Task<bool> IsConsoleViaAgentAsync()
    {
        try
        {
            // The default site answers no unless it has been handed to its agent, matching
            // SiteTunnelRouting.IsViaAgentAsync. The flag is deliberately kept rather than cleared
            // when coverage is switched off, so re-enabling coverage restores the operator's
            // choice - which is exactly why the flag on its own cannot be trusted here. Without
            // this, unchecking coverage left the console still dialing an agent that is no longer
            // meant to serve the site, and every console read failed.
            if (SiteSlug == SiteManagementService.DefaultSiteSlug
                && !_serviceProvider.GetRequiredService<SiteAgentCoverage>().Covers(SiteSlug))
            {
                return false;
            }

            using var scope = CreateSiteScope();
            var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
            var setting = await db.SystemSettings.FindAsync(ConsoleViaAgentKey);
            return bool.TryParse(setting?.Value, out var enabled) && enabled;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Whether an on-site agent tunnel is currently connected for this site.</summary>
    private bool IsAgentOnline()
    {
        var registry = _serviceProvider.GetService<AgentTunnelRegistry>();
        return registry != null && registry.GetForSite(SiteSlug).Count > 0;
    }

    /// <summary>
    /// Whether this site's agent tunnel is registered AND alive (not silent past
    /// the stale threshold). A black-holed tunnel stays registered until the 90s
    /// watchdog reaps it, so <see cref="IsAgentOnline"/> reads stale-true for
    /// that whole window - long enough for WaitForConnectionAsync to poll its
    /// full timeout on every page load of the site (twice per page with
    /// prerender), which is what made switching to an outaged site take ~10s+
    /// while a known-offline site was instant. Connect/wait decisions must use
    /// THIS; registration alone is only meaningful for teardown bookkeeping.
    /// </summary>
    private bool HasLiveAgentTunnel()
    {
        var registry = _serviceProvider.GetService<AgentTunnelRegistry>();
        return registry != null && registry.GetForSite(SiteSlug).Any(a => !a.IsStale);
    }

    /// <summary>
    /// Called when this site's agent tunnel drops. When the console is reached
    /// through that tunnel, flip straight to the awaiting-agent state: the client
    /// stays "connected" otherwise, and every console call dials the dead loopback
    /// proxy and burns through the transient-failure retry backoff (~14 s per
    /// call), which reads as a frozen UI on any page of this site while the agent
    /// is down. The agent-connected hook re-establishes the console when the
    /// tunnel returns.
    /// </summary>
    /// <summary>
    /// The site's agent is up but cannot open a connection to the console, so awaiting-agent would
    /// be the wrong answer. Marks it down instead; the next successful connect clears it.
    /// </summary>
    public Task NoteConsoleUnreachableAsync()
    {
        if (_consoleUnresponsive || _settings is not { IsConfigured: true, HasCredentials: true })
            return Task.CompletedTask;

        _isConnected = false;
        _consoleUnresponsive = true;
        _lastError = ConsoleUnresponsiveMessage;
        _logger.LogInformation("Site {Slug}'s agent cannot reach its console; marking it unresponsive", SiteSlug);
        OnConnectionChanged?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The client proved the console stopped answering mid-session. Marks it down so pages render
    /// the banner instead of paying a timeout per call; the background reconnect clears it.
    /// </summary>
    private void HandleConsoleWentSilent(UniFiApiClient client)
    {
        // Only the live client may report this. A disposed one's request can fault seconds after a
        // reconnect already succeeded, and acting on it would flip a good connection straight back
        // down - the subscription outlives the client it was made on.
        if (!ReferenceEquals(client, _client)) return;
        if (!_isConnected) return;
        _isConnected = false;
        _consoleUnresponsive = true;
        _lastError = ConsoleUnresponsiveMessage;
        _logger.LogWarning("Console for site {Slug} stopped answering; marking it unresponsive", SiteSlug);
        OnConnectionChanged?.Invoke();
    }

    /// <summary>
    /// Called when this site's agent tunnel drops. When the console is reached
    /// through that tunnel, flip straight to the awaiting-agent state: the client
    /// stays "connected" otherwise, and every console call dials the dead loopback
    /// proxy and burns through the transient-failure retry backoff (~14 s per
    /// call), which reads as a frozen UI on any page of this site while the agent
    /// is down. The agent-connected hook re-establishes the console when the
    /// tunnel returns.
    /// </summary>
    public async Task OnAgentTunnelDroppedAsync()
    {
        if (IsAgentOnline()) return; // another agent still carries the site

        await _connectGate.WaitAsync();
        var notify = false;
        try
        {
            if (!_isConnected && _client == null) return;
            if (!_clientViaAgent) return;

            // Re-check after the await: a fast agent bounce can reconnect (and the
            // connected hook re-establish the console) while the DB read above was in
            // flight - disposing the fresh client here would fabricate an up-to-60s
            // "awaiting agent" outage on a healthy tunnel.
            if (IsAgentOnline()) return;

            _logger.LogInformation(
                "Agent tunnel for site {Slug} dropped; marking its console as awaiting the agent", SiteSlug);
            _client?.Dispose();
            _client = null;
            _isConnected = false;
            _awaitingAgent = true;
            _lastError = AwaitingAgentMessage;
            ResetConsoleFailureCount();
            notify = true;
        }
        finally
        {
            _connectGate.Release();
            if (notify) OnConnectionChanged?.Invoke();
        }
    }

    /// <summary>
    /// Called the moment this site's agent tunnel is known black-holed - a proxy
    /// open timed out, an open was refused for staleness, or the tunnel watchdog
    /// saw it silent past the stale threshold - while the agent is still
    /// registered, so the 90s server watchdog (which drives
    /// <see cref="OnAgentTunnelDroppedAsync"/>) hasn't fired. Flips the console to
    /// awaiting-agent NOW so its calls short-circuit at the IsConnected guard
    /// instead of each dialing the dead loopback proxy and paying the retry
    /// backoff - which read as a multi-second site switch for up to 90s. Unlike
    /// OnAgentTunnelDroppedAsync it does NOT bail while the agent is still
    /// registered, because the tunnel is proven dead regardless. Idempotent; the
    /// agent-connected hook (on reconnect or the periodic refresh) re-establishes
    /// the console when the tunnel returns.
    /// </summary>
    public async Task NoteTunnelUnreachableAsync()
    {
        await _connectGate.WaitAsync();
        var notify = false;
        try
        {
            if (!_isConnected && _client == null) return; // already down / awaiting - idempotent
            if (!_clientViaAgent) return;                 // only agent-routed consoles ride the tunnel
            _logger.LogInformation(
                "Site {Slug}'s agent tunnel is unreachable; flipping its console to awaiting-agent ahead of the watchdog", SiteSlug);
            _client?.Dispose();
            _client = null;
            _isConnected = false;
            _awaitingAgent = true;
            _lastError = AwaitingAgentMessage;
            ResetConsoleFailureCount();
            notify = true;
        }
        finally
        {
            _connectGate.Release();
            if (notify) OnConnectionChanged?.Invoke();
        }
    }

    /// <summary>
    /// Called after a connect attempt fails. When the console is agent-routed and
    /// the tunnel itself is suspect (absent, stale, or open-breaker tripped), the
    /// failure is a dead-tunnel symptom: the loopback proxy dial collapses
    /// mid-TLS and parses as an "SSL certificate error" with advice the user
    /// can't act on - the console never answered at all. Land in
    /// awaiting-agent instead, so the banner tells the truth from the first
    /// moment rather than flashing a bogus SSL error until the flip corrects it.
    /// A genuine console-side failure over a HEALTHY tunnel keeps its real error.
    /// </summary>
    private async Task PreferAwaitingAgentOnDeadTunnelAsync()
    {
        try
        {
            if (!_clientViaAgent) return;
            var proxy = _serviceProvider.GetService<AgentTunnelProxyService>();
            if (proxy == null || !proxy.IsTunnelSuspect(SiteSlug)) return;
            _awaitingAgent = true;
            _lastError = AwaitingAgentMessage;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dead-tunnel error remap failed for site {Slug}", SiteSlug);
        }
    }

    /// <summary>
    /// The console's URL, and the loopback port to dial it on when the site is reached through its
    /// agent tunnel. The URL is deliberately left alone: it decides the Host header and the TLS SNI,
    /// and a console behind a name-routing reverse proxy answers 404 to anything that does not ask
    /// for it by name. Only the connection is redirected - see UniFiApiClient's ConnectCallback.
    /// Callers still force ignore-SSL for proxied connections.
    /// </summary>
    private (string Url, int? ProxyPort) ResolveControllerEndpoint(string controllerUrl, bool viaAgent)
    {
        if (!viaAgent) return (controllerUrl, null);
        var proxy = _serviceProvider.GetService<AgentTunnelProxyService>();
        if (proxy == null || !Uri.TryCreate(controllerUrl, UriKind.Absolute, out var uri))
            return (controllerUrl, null);
        var port = uri.IsDefaultPort ? (uri.Scheme == Uri.UriSchemeHttps ? 443 : 80) : uri.Port;
        var localPort = proxy.GetOrCreateEndpoint(SiteSlug, uri.Host, port, isConsole: true);
        _logger.LogInformation("Console for site {Slug} routed via agent tunnel (127.0.0.1:{LocalPort} -> {Host}:{Port})",
            SiteSlug, localPort, uri.Host, port);
        return (controllerUrl, localPort);
    }

    /// <summary>
    /// Starts the async initialization without blocking the constructor.
    /// Uses double-checked locking to ensure initialization runs only once.
    /// </summary>
    private void StartInitializationAsync()
    {
        lock (_initLock)
        {
            if (_initializationTask == null)
            {
                _initializationTask = Task.Run(async () =>
                {
                    try
                    {
                        await LoadConfigAndConnectAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error during UniFi connection service initialization");
                    }
                });
            }
        }
    }

    /// <summary>
    /// Loads configuration from database and optionally auto-connects.
    /// </summary>
    private async Task LoadConfigAndConnectAsync()
    {
        try
        {
            using var scope = CreateSiteScope();
            var repository = scope.ServiceProvider.GetRequiredService<IUniFiRepository>();

            var settings = await repository.GetUniFiConnectionSettingsAsync();

            if (settings != null && settings.IsConfigured && !string.IsNullOrEmpty(settings.ControllerUrl))
            {
                _settings = settings;
                _cacheTime = DateTime.UtcNow;

                _logger.LogInformation("Loaded saved UniFi configuration for {Url}", settings.ControllerUrl);

                // Auto-connect if we have credentials and RememberCredentials is true
                if (settings.RememberCredentials && settings.HasCredentials)
                {
                    // Only the default site connects at process startup and benefits from a brief
                    // settle delay. A secondary site's connection service is created on demand when
                    // the user switches to it (app already running), so the delay there just adds
                    // latency to the site switch.
                    if (SiteSlug == SiteManagementService.DefaultSiteSlug)
                        await Task.Delay(1000);

                    // If this site's console is reached through its agent tunnel and no
                    // live agent has connected yet, defer: dialing the loopback proxy with
                    // no agent behind it fails with a spurious SSL/EOF error on the dashboard.
                    // OnAgentConnectedAsync establishes the console connection as soon as
                    // the tunnel comes up (often 20-30s after startup).
                    if (await IsConsoleViaAgentAsync() && !HasLiveAgentTunnel())
                    {
                        _awaitingAgent = true;
                        _lastError = AwaitingAgentMessage;
                        _logger.LogInformation(
                            "Console for site {Slug} routes via its agent tunnel, which isn't connected yet; deferring connect until the agent comes online",
                            SiteSlug);
                    }
                    else
                    {
                        await ConnectWithSettingsAsync(settings);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading UniFi configuration from database");
        }
        finally
        {
            IsInitialized = true;

            // Notify subscribers so the dashboard can show the connection banner
            // (especially when auto-connect fails and WaitForConnectionAsync has already timed out)
            OnConnectionChanged?.Invoke();
        }
    }

    /// <summary>
    /// Ensures initialization has completed. Call this before accessing settings
    /// if you need to guarantee config is loaded.
    /// </summary>
    public async Task EnsureInitializedAsync()
    {
        var task = _initializationTask;
        if (task != null)
        {
            await task;
        }
    }

    public bool IsConnected => _isConnected && _client != null;
    public bool IsInitialized { get; private set; }
    public string? LastError => _lastError;

    private bool _awaitingAgent;

    /// <summary>
    /// True when this site's console is reached through its agent tunnel and that tunnel
    /// isn't up yet - a transient "waiting for the agent" state, not a misconfiguration.
    /// The UI should prompt to wait/refresh rather than steering to connection setup.
    /// Requires a configured, credentialed console: awaiting-agent is meaningless without a
    /// saved target, so a half-configured site falls through to the "set up in Settings" banner
    /// instead of showing the wait/refresh one (which would tell you to open Settings while only
    /// offering Refresh).
    /// </summary>
    public bool IsAwaitingAgent =>
        _awaitingAgent && !_isConnected && _settings is { IsConfigured: true, HasCredentials: true };

    private bool _consoleUnresponsive;

    /// <summary>
    /// True once a connect has timed out against a console that answers TCP but never replies.
    /// Lets reads fail fast. Never gate the background reconnect on it, and any successful connect
    /// clears it - this must not be able to latch a site off.
    /// </summary>
    public bool IsConsoleUnresponsive =>
        _consoleUnresponsive && !_isConnected && _settings is { IsConfigured: true, HasCredentials: true };
    public DateTime? LastConnectedAt => _lastConnectedAt;
    public bool IsUniFiOs => _client?.IsUniFiOs ?? false;

    /// <summary>
    /// Gets the current connection config (for UI display)
    /// </summary>
    public UniFiConnectionConfig? CurrentConfig
    {
        get
        {
            if (_settings == null) return null;
            return new UniFiConnectionConfig
            {
                ControllerUrl = _settings.ControllerUrl ?? "",
                Username = _settings.Username ?? "",
                Password = "", // Never expose password
                ApiKey = _settings.HasApiKey ? "saved" : null, // Signal that key exists without exposing it
                Site = _settings.Site,
                RememberCredentials = _settings.RememberCredentials,
                IgnoreControllerSSLErrors = _settings.IgnoreControllerSSLErrors
            };
        }
    }

    /// <summary>
    /// Gets the active UniFi API client, or null if not connected
    /// </summary>
    public UniFiApiClient? Client => _isConnected ? _client : null;

    /// <summary>
    /// Get the stored (decrypted) password for testing connection
    /// </summary>
    public async Task<string?> GetStoredPasswordAsync()
    {
        var settings = await GetSettingsAsync();
        if (!string.IsNullOrEmpty(settings.Password))
        {
            try
            {
                return _credentialProtection.Decrypt(settings.Password);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Get the stored (decrypted) API key for testing connection
    /// </summary>
    public async Task<string?> GetStoredApiKeyAsync()
    {
        var settings = await GetSettingsAsync();
        if (!string.IsNullOrEmpty(settings.ApiKey))
        {
            try
            {
                return _credentialProtection.Decrypt(settings.ApiKey);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Get the connection settings from database
    /// </summary>
    public async Task<UniFiConnectionSettings> GetSettingsAsync()
    {
        // Check cache first
        if (_settings != null && DateTime.UtcNow - _cacheTime < _cacheExpiry)
        {
            return _settings;
        }

        using var scope = CreateSiteScope();
        var repository = scope.ServiceProvider.GetRequiredService<IUniFiRepository>();

        var settings = await repository.GetUniFiConnectionSettingsAsync();

        if (settings == null)
        {
            // Create default settings
            settings = new UniFiConnectionSettings
            {
                Site = "default",
                RememberCredentials = true,
                IsConfigured = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await repository.SaveUniFiConnectionSettingsAsync(settings);
        }

        _settings = settings;
        _cacheTime = DateTime.UtcNow;

        return settings;
    }

    /// <summary>
    /// Configure and connect to a UniFi controller
    /// </summary>
    public async Task<bool> ConnectAsync(UniFiConnectionConfig config)
    {
        // Validate URL before attempting connection
        if (string.IsNullOrWhiteSpace(config.ControllerUrl))
        {
            _lastError = "Console URL is required. Enter the URL or hostname of your UniFi Console.";
            return false;
        }

        _logger.LogInformation("Connecting to UniFi controller at {Url}", config.ControllerUrl);

        await _connectGate.WaitAsync();
        var notify = false;
        try
        {
            // Dispose existing client
            _client?.Dispose();
            _client = null;
            _isConnected = false;
            // Cleared on success, not here: a connect takes up to the full timeout to resolve, and
            // wiping them on entry left the banner showing a generic "not connected" for that whole
            // window instead of the reason it already knew.
            _awaitingAgent = false;

            // Create new client
            var viaAgent = await IsConsoleViaAgentAsync();
            if (viaAgent && !HasLiveAgentTunnel())
            {
                // Reached through the agent tunnel, which isn't up - or is
                // dead-but-registered (black-holed). Dialing the loopback proxy now
                // fails with an SSL/EOF error that gets misreported as a certificate
                // problem, so surface the real reason.
                _awaitingAgent = true;
                _lastError = AwaitingAgentMessage;
                return false;
            }
            var consoleEndpoint = ResolveControllerEndpoint(config.ControllerUrl, viaAgent);
            var clientLogger = _loggerFactory.CreateLogger<UniFiApiClient>();
            _clientViaAgent = viaAgent;
            _client = new UniFiApiClient(
                clientLogger,
                consoleEndpoint.Url,
                config.Username,
                config.Password,
                config.Site,
                config.IgnoreControllerSSLErrors || viaAgent,
                config.ApiKey,
                consoleEndpoint.ProxyPort
            );
            _client.AuthProbeCompleted += HandleAuthProbe;
            _client.ConsoleWentSilent += HandleConsoleWentSilent;

            // Attempt to authenticate
            var success = await _client.LoginAsync();

            if (success)
            {
                // Validate the site ID by making a site-specific call
                var (siteValid, siteError) = await _client.ValidateSiteAsync();
                if (!siteValid)
                {
                    _lastError = siteError;
                    _logger.LogWarning("Site validation failed: {Error}", siteError);
                    _client.Dispose();
                    _client = null;
                    return false;
                }

                _isConnected = true;
                _lastError = null;
                _consoleUnresponsive = false;
                _lastConnectedAt = DateTime.UtcNow;

                // Save configuration to database
                await SaveSettingsAsync(config);

                // Cache the console's display name for the Sites listing / wizard.
                await RefreshConsoleNameAsync();

                // Clear cached data from previous connection/site
                ClearCaches();

                _logger.LogInformation("Successfully connected to UniFi controller (UniFi OS: {IsUniFiOs})", _client.IsUniFiOs);

                // Notify subscribers to refresh their data (fired after the gate releases)
                notify = true;

                return true;
            }
            else
            {
                // Use detailed error from API client if available
                var defaultError = config.UseApiKey
                    ? "Authentication failed. Check that the API key is valid and not expired."
                    : "Authentication failed. Check username and password.";
                _lastError = _client.LastLoginError ?? defaultError;
                _logger.LogWarning("Failed to authenticate with UniFi controller");
                _client.Dispose();
                _client = null;
                await PreferAwaitingAgentOnDeadTunnelAsync();
                return false;
            }
        }
        catch (Exception ex)
        {
            _lastError = ParseConnectionException(ex);
            _logger.LogError(ex, "Error connecting to UniFi controller");
            _client?.Dispose();
            _client = null;
            await PreferAwaitingAgentOnDeadTunnelAsync();
            return false;
        }
        finally
        {
            _connectGate.Release();
            if (notify) OnConnectionChanged?.Invoke();
        }
    }

    /// <summary>
    /// Connect using existing settings from database
    /// </summary>
    private async Task<bool> ConnectWithSettingsAsync(UniFiConnectionSettings settings)
    {
        if (!settings.HasCredentials) return false;

        await _connectGate.WaitAsync();
        var notify = false;

        // Use a shorter timeout for startup auto-connect so the dashboard
        // shows the "unreachable" banner quickly instead of waiting 60s+. Created
        // after the gate so its 8s budget covers the connect itself, not time spent
        // queued behind another in-flight connect.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        try
        {
            // Decrypt credentials
            string? decryptedPassword = null;
            string? decryptedApiKey = null;

            if (!string.IsNullOrEmpty(settings.ApiKey))
            {
                decryptedApiKey = _credentialProtection.Decrypt(settings.ApiKey);
            }

            if (!string.IsNullOrEmpty(settings.Password))
            {
                decryptedPassword = _credentialProtection.Decrypt(settings.Password);
            }

            var config = new UniFiConnectionConfig
            {
                ControllerUrl = settings.ControllerUrl!,
                Username = settings.Username ?? "",
                Password = decryptedPassword ?? "",
                ApiKey = decryptedApiKey,
                Site = settings.Site,
                RememberCredentials = settings.RememberCredentials,
                IgnoreControllerSSLErrors = settings.IgnoreControllerSSLErrors
            };

            // Dispose existing client
            _client?.Dispose();
            _client = null;
            _isConnected = false;
            // Cleared on success, not here: a connect takes up to the full timeout to resolve, and
            // wiping them on entry left the banner showing a generic "not connected" for that whole
            // window instead of the reason it already knew.
            _awaitingAgent = false;

            // Create new client
            var viaAgent = await IsConsoleViaAgentAsync();
            if (viaAgent && !HasLiveAgentTunnel())
            {
                // Reached through the agent tunnel, which isn't up - or is
                // dead-but-registered (black-holed). Dialing the loopback proxy now
                // fails with an SSL/EOF error that gets misreported as a certificate
                // problem, so surface the real reason.
                _awaitingAgent = true;
                _lastError = AwaitingAgentMessage;
                return false;
            }
            var consoleEndpoint = ResolveControllerEndpoint(config.ControllerUrl, viaAgent);
            var clientLogger = _loggerFactory.CreateLogger<UniFiApiClient>();
            _clientViaAgent = viaAgent;
            _client = new UniFiApiClient(
                clientLogger,
                consoleEndpoint.Url,
                config.Username,
                config.Password,
                config.Site,
                config.IgnoreControllerSSLErrors || viaAgent,
                config.ApiKey,
                consoleEndpoint.ProxyPort
            );
            _client.AuthProbeCompleted += HandleAuthProbe;
            _client.ConsoleWentSilent += HandleConsoleWentSilent;

            var success = await _client.LoginAsync(cts.Token);

            if (success)
            {
                // Validate the site ID by making a site-specific call
                var (siteValid, siteError) = await _client.ValidateSiteAsync(cts.Token);
                if (!siteValid)
                {
                    _lastError = siteError;
                    _logger.LogWarning("Site validation failed during reconnect: {Error}", siteError);
                    _client.Dispose();
                    _client = null;
                    return false;
                }

                _isConnected = true;
                _lastError = null;
                _consoleUnresponsive = false;
                _lastConnectedAt = DateTime.UtcNow;

                // Cache the console's display name on auto-reconnect too, so the
                // Sites listing shows it without a manual Connect/Test.
                await RefreshConsoleNameAsync();

                // Update last connected timestamp in DB
                using var scope = CreateSiteScope();
                var repository = scope.ServiceProvider.GetRequiredService<IUniFiRepository>();
                var dbSettings = await repository.GetUniFiConnectionSettingsAsync();
                if (dbSettings != null)
                {
                    dbSettings.LastConnectedAt = DateTime.UtcNow;
                    dbSettings.LastError = null;
                    dbSettings.UpdatedAt = DateTime.UtcNow;
                    await repository.SaveUniFiConnectionSettingsAsync(dbSettings);
                }

                // Clear cached data from the previous (failed/disconnected) state
                ClearCaches();

                _logger.LogInformation("Successfully connected to UniFi controller (UniFi OS: {IsUniFiOs}, API Key: {UseApiKey})", _client.IsUniFiOs, _client.UseApiKey);

                // Notify subscribers (e.g. the Dashboard) so they reload their data.
                // Critical for agent-tunneled consoles: when a site's console was
                // unreachable at initial load and the agent tunnel later comes up,
                // this reconnect fires the event that triggers the dashboard refresh.
                // Fired after the gate releases (see finally) so a subscriber can't
                // re-enter a gated connect and deadlock.
                notify = true;

                return true;
            }
            else
            {
                // Use detailed error from API client if available
                var defaultError = config.UseApiKey
                    ? "Authentication failed. Check that the API key is valid and not expired."
                    : "Authentication failed. Check username and password.";
                _lastError = _client.LastLoginError ?? defaultError;
                _client.Dispose();
                _client = null;
                await PreferAwaitingAgentOnDeadTunnelAsync();
                return false;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Nothing answered inside the budget, so every later call would pay the same wait.
            // Mark it so reads short-circuit; the background reconnect keeps dialing regardless.
            _consoleUnresponsive = true;
            _lastError = ConsoleUnresponsiveMessage;
            _logger.LogWarning("Console connect timed out for site {Slug}; treating it as unresponsive until a connect succeeds", SiteSlug);
            _client?.Dispose();
            _client = null;
            await PreferAwaitingAgentOnDeadTunnelAsync();
            return false;
        }
        catch (Exception ex)
        {
            _lastError = ParseConnectionException(ex);
            _logger.LogError(ex, "Error connecting to UniFi controller");
            _client?.Dispose();
            _client = null;
            await PreferAwaitingAgentOnDeadTunnelAsync();
            return false;
        }
        finally
        {
            _connectGate.Release();
            if (notify) OnConnectionChanged?.Invoke();
        }
    }

    /// <summary>
    /// Save connection settings to database
    /// </summary>
    private async Task SaveSettingsAsync(UniFiConnectionConfig config)
    {
        try
        {
            using var scope = CreateSiteScope();
            var repository = scope.ServiceProvider.GetRequiredService<IUniFiRepository>();

            var settings = await repository.GetUniFiConnectionSettingsAsync() ?? new UniFiConnectionSettings
            {
                CreatedAt = DateTime.UtcNow
            };

            settings.ControllerUrl = config.ControllerUrl;
            settings.Username = config.Username;
            settings.Site = config.Site;
            settings.RememberCredentials = config.RememberCredentials;
            settings.IgnoreControllerSSLErrors = config.IgnoreControllerSSLErrors;
            settings.IsConfigured = true;
            settings.LastConnectedAt = DateTime.UtcNow;
            settings.LastError = null;
            settings.UpdatedAt = DateTime.UtcNow;

            // Save credentials based on auth method - clear the other method
            if (config.UseApiKey)
            {
                // API key auth: save key, clear username/password
                if (!string.IsNullOrEmpty(config.ApiKey))
                {
                    settings.ApiKey = _credentialProtection.Encrypt(config.ApiKey);
                }
                settings.Username = null;
                settings.Password = null;
            }
            else
            {
                // Username/password auth: save credentials, clear API key
                if (!string.IsNullOrEmpty(config.Password))
                {
                    settings.Password = _credentialProtection.Encrypt(config.Password);
                }
                settings.ApiKey = null;
            }

            await repository.SaveUniFiConnectionSettingsAsync(settings);

            // Update cache
            _settings = settings;
            _cacheTime = DateTime.UtcNow;

            _logger.LogInformation("Saved UniFi configuration to database");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error saving UniFi configuration to database");
        }
    }

    /// <summary>
    /// Clears all cached data (devices, networks, etc.).
    /// Called automatically on connection changes.
    /// </summary>
    public void ClearCaches()
    {
        _cachedDevices = null;
        _deviceCacheTime = DateTime.MinValue;
        _cachedNetworks = null;
        _networkCacheTime = DateTime.MinValue;
        _logger.LogDebug("Cleared device and network caches");
    }

    /// <summary>
    /// Disconnect from the controller
    /// </summary>
    public async Task DisconnectAsync()
    {
        await _connectGate.WaitAsync();
        try
        {
            if (_client != null)
            {
                try
                {
                    await _client.LogoutAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during logout");
                }

                _client.Dispose();
                _client = null;
            }

            _isConnected = false;
            ResetConsoleFailureCount();
            ClearCaches();
            _logger.LogInformation("Disconnected from UniFi controller");
        }
        finally
        {
            _connectGate.Release();
            OnConnectionChanged?.Invoke();
        }
    }

    /// <summary>
    /// Test connection without saving
    /// </summary>
    public async Task<(bool Success, string? Error, string? ControllerInfo)> TestConnectionAsync(UniFiConnectionConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ControllerUrl))
            return (false, "Console URL is required. Enter the URL or hostname of your UniFi Console.", null);

        _logger.LogInformation("Testing connection to UniFi controller at {Url}", config.ControllerUrl);

        UniFiApiClient? testClient = null;
        try
        {
            var viaAgent = await IsConsoleViaAgentAsync();
            if (viaAgent && !HasLiveAgentTunnel())
            {
                // The console is reached through the agent tunnel, which isn't up (or is
                // dead-but-registered). Dialing the loopback proxy now fails with an
                // SSL/EOF error that gets misreported as a certificate problem, so
                // return the real reason instead.
                return (false,
                    "This site's console is reached through its on-site agent tunnel, which isn't connected yet. Start the site's agent (or wait for it to come online), then test again.",
                    null);
            }
            var consoleEndpoint = ResolveControllerEndpoint(config.ControllerUrl, viaAgent);
            var clientLogger = _loggerFactory.CreateLogger<UniFiApiClient>();
            testClient = new UniFiApiClient(
                clientLogger,
                consoleEndpoint.Url,
                config.Username,
                config.Password,
                config.Site,
                config.IgnoreControllerSSLErrors || viaAgent,
                config.ApiKey,
                consoleEndpoint.ProxyPort
            );

            var success = await testClient.LoginAsync();

            if (success)
            {
                // Validate the site ID by making a site-specific call
                var (siteValid, siteError) = await testClient.ValidateSiteAsync();
                if (!siteValid)
                {
                    return (false, siteError, null);
                }

                // Get system info for display
                var sysInfo = await testClient.GetSystemInfoAsync();
                var authMethod = testClient.UseApiKey ? "API Key" : (testClient.IsUniFiOs ? "UniFi OS" : "Standalone");
                var info = sysInfo != null
                    ? $"{sysInfo.Name} v{sysInfo.Version} ({authMethod})"
                    : "Connected successfully";

                return (true, null, info);
            }
            else
            {
                // Use detailed error from API client if available
                var defaultError = config.UseApiKey
                    ? "Authentication failed. Check that the API key is valid and not expired."
                    : "Authentication failed. Check username and password.";
                var error = testClient.LastLoginError ?? defaultError;
                return (false, error, null);
            }
        }
        catch (Exception ex)
        {
            // Parse common connection errors for user-friendly messages
            var error = ParseConnectionException(ex);
            return (false, error, null);
        }
        finally
        {
            testClient?.Dispose();
        }
    }

    /// <summary>
    /// Get list of available sites from the controller using provided credentials.
    /// Creates a temporary connection to fetch sites without affecting current connection state.
    /// </summary>
    public async Task<(bool Success, string? Error, List<UniFiSite> Sites)> GetSitesAsync(UniFiConnectionConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ControllerUrl))
            return (false, "Console URL is required. Enter the URL or hostname of your UniFi Console.", new List<UniFiSite>());

        _logger.LogInformation("Fetching sites from UniFi controller at {Url}", config.ControllerUrl);

        UniFiApiClient? testClient = null;
        try
        {
            var viaAgent = await IsConsoleViaAgentAsync();
            var consoleEndpoint = ResolveControllerEndpoint(config.ControllerUrl, viaAgent);
            var clientLogger = _loggerFactory.CreateLogger<UniFiApiClient>();
            testClient = new UniFiApiClient(
                clientLogger,
                consoleEndpoint.Url,
                config.Username,
                config.Password,
                config.Site,
                config.IgnoreControllerSSLErrors || viaAgent,
                config.ApiKey,
                consoleEndpoint.ProxyPort
            );

            var success = await testClient.LoginAsync();

            if (!success)
            {
                var defaultError = config.UseApiKey
                    ? "Authentication failed. Check that the API key is valid and not expired."
                    : "Authentication failed. Check username and password.";
                var error = testClient.LastLoginError ?? defaultError;
                return (false, error, new List<UniFiSite>());
            }

            var sitesDoc = await testClient.GetSitesAsync();
            if (sitesDoc == null)
            {
                return (false, "Failed to retrieve sites", new List<UniFiSite>());
            }

            var sites = new List<UniFiSite>();
            if (sitesDoc.RootElement.TryGetProperty("data", out var dataArray))
            {
                foreach (var siteElement in dataArray.EnumerateArray())
                {
                    var site = new UniFiSite
                    {
                        Name = siteElement.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                        Description = siteElement.TryGetProperty("desc", out var desc) ? desc.GetString() ?? "" : "",
                        Role = siteElement.TryGetProperty("role", out var role) ? role.GetString() ?? "" : "",
                        DeviceCount = siteElement.TryGetProperty("device_count", out var count) ? count.GetInt32() : 0
                    };
                    sites.Add(site);
                }
            }

            _logger.LogInformation("Found {Count} sites", sites.Count);
            return (true, null, sites);
        }
        catch (Exception ex)
        {
            var error = ParseConnectionException(ex);
            return (false, error, new List<UniFiSite>());
        }
        finally
        {
            testClient?.Dispose();
        }
    }

    /// <summary>
    /// Attempt to reconnect using saved configuration
    /// </summary>
    public async Task<bool> ReconnectAsync()
    {
        var settings = await GetSettingsAsync();

        if (!settings.IsConfigured || !settings.HasCredentials)
        {
            _lastError = SiteSlug == SiteManagementService.DefaultSiteSlug
                ? "The UniFi Console isn't connected yet. Set up the connection in Settings to view network data."
                : "This site's UniFi Console isn't connected yet. Set up its connection in Settings - a console that isn't directly reachable from this server connects through the site's on-site agent.";
            return false;
        }

        return await ConnectWithSettingsAsync(settings);
    }

    /// <summary>
    /// Whether the current connection uses API key authentication
    /// </summary>
    public bool IsApiKeyAuth => _client?.UseApiKey ?? false;

    /// <summary>
    /// Wait for the connection to be established (for use during app startup).
    /// Polls until connected or timeout is reached.
    /// </summary>
    /// <param name="timeout">Maximum time to wait</param>
    /// <param name="pollInterval">How often to check connection status</param>
    /// <returns>True if connected, false if timeout or no saved credentials</returns>
    public async Task<bool> WaitForConnectionAsync(TimeSpan? timeout = null, TimeSpan? pollInterval = null)
    {
        timeout ??= TimeSpan.FromSeconds(3);
        pollInterval ??= TimeSpan.FromMilliseconds(250);

        // If already connected, return immediately
        if (IsConnected) return true;

        // Already flipped to awaiting-agent (tunnel down or proven black-holed):
        // no poll can succeed until the agent reconnects, and pages reload via
        // OnConnectionChanged when it does. Waiting here would stall every page
        // render of the site for the full timeout.
        if (IsAwaitingAgent) return false;

        // Same reasoning for a console that answers TCP and then goes quiet: polling it here only
        // stalls the render, and the background reconnect brings it back via OnConnectionChanged.
        if (IsConsoleUnresponsive) return false;

        // Check if we have saved credentials to connect with
        var settings = await GetSettingsAsync();
        if (!settings.IsConfigured || !settings.HasCredentials || !settings.RememberCredentials)
        {
            // No auto-connect will happen, don't wait
            return false;
        }

        // If this site's console is reached through an agent tunnel that isn't up - or is
        // dead-but-registered (black-holed, silent past the stale threshold) - don't block:
        // the console connects asynchronously once the agent (re)connects
        // (OnAgentConnectedAsync), and pages reload via OnConnectionChanged. Polling the full
        // timeout here would stall the page render on every agent-site load or switch, which
        // is the single biggest cause of "the page takes forever to appear" on those sites.
        if (await IsConsoleViaAgentAsync() && !HasLiveAgentTunnel())
            return false;

        var startTime = DateTime.UtcNow;
        while (DateTime.UtcNow - startTime < timeout)
        {
            if (IsConnected) return true;
            await Task.Delay(pollInterval.Value);
        }

        _logger.LogWarning("Timed out waiting for UniFi controller connection");
        return false;
    }

    /// <summary>
    /// Clear saved credentials from database
    /// </summary>
    public async Task ClearCredentialsAsync()
    {
        using var scope = CreateSiteScope();
        var repository = scope.ServiceProvider.GetRequiredService<IUniFiRepository>();

        var settings = await repository.GetUniFiConnectionSettingsAsync();
        if (settings != null)
        {
            settings.Username = null;
            settings.Password = null;
            settings.ApiKey = null;
            settings.IsConfigured = false;
            settings.UpdatedAt = DateTime.UtcNow;
            await repository.SaveUniFiConnectionSettingsAsync(settings);
        }

        // Invalidate cache
        _settings = null;
        _cacheTime = DateTime.MinValue;
    }

    /// <summary>
    /// Get all discovered devices with proper DeviceType enum values.
    /// This is the preferred way to get devices - use this instead of Client.GetDevicesAsync().
    /// </summary>
    public async Task<List<DiscoveredDevice>> GetDiscoveredDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (_client == null || !_isConnected)
        {
            _logger.LogWarning("Cannot get devices - not connected to controller");
            return new List<DiscoveredDevice>();
        }

        // Return cached devices if still fresh
        if (_cachedDevices != null && DateTime.UtcNow - _deviceCacheTime < DeviceCacheDuration)
        {
            _logger.LogDebug("Returning cached device list ({Count} devices)", _cachedDevices.Count);
            return _cachedDevices;
        }

        var discoveryLogger = _loggerFactory.CreateLogger<UniFiDiscovery>();
        var discovery = new UniFiDiscovery(_client, discoveryLogger);
        var devices = await discovery.DiscoverDevicesAsync(cancellationToken);

        // Cache a real answer only. DiscoverDevicesAsync returns an empty list rather than throwing
        // when the fetch fails, and UniFiApiClient deliberately doesn't cache that failure - caching
        // it here would undo that and blank the device list for every consumer until it expires.
        // The one genuinely device-less site is a standalone UniFi OS Server, which can afford the
        // re-query.
        if (devices.Count > 0)
        {
            _cachedDevices = devices;
            _deviceCacheTime = DateTime.UtcNow;
        }

        return devices;
    }

    /// <summary>
    /// Invalidates the device cache, forcing a fresh fetch on next request.
    /// </summary>
    public void InvalidateDeviceCache()
    {
        _cachedDevices = null;
        _deviceCacheTime = DateTime.MinValue;
    }

    /// <summary>
    /// Gets the list of configured networks from the UniFi controller.
    /// Successful results are cached for <see cref="NetworkCacheDuration"/>.
    /// </summary>
    public async Task<List<NetworkInfo>> GetNetworksAsync(CancellationToken cancellationToken = default)
    {
        if (_client == null || !_isConnected)
        {
            _logger.LogWarning("Cannot get networks - not connected to controller");
            return new List<NetworkInfo>();
        }

        // Return cached networks if still fresh
        if (_cachedNetworks != null && DateTime.UtcNow - _networkCacheTime < NetworkCacheDuration)
        {
            return _cachedNetworks;
        }

        var networks = await _client.GetNetworkConfigsAsync(cancellationToken);

        // Same rule as the device list: a failed fetch is not an answer. GetNetworkConfigsAsync
        // returns null rather than throwing, and the old "?? new List<NetworkInfo>()" cached that
        // as "this console has no networks" - which is what the primary-WAN lookup and expected WAN
        // speeds read, so one transient failure hid both until the cache expired.
        if (networks == null || networks.Count == 0)
        {
            _logger.LogWarning("No networks returned from the console; not caching, so the next request retries");
            return new List<NetworkInfo>();
        }

        _cachedNetworks = networks.Select(n => new NetworkInfo
        {
            Id = n.Id,
            Name = n.Name,
            Purpose = n.Purpose,
            Enabled = n.Enabled,
            VlanId = n.Vlan,
            IpSubnet = n.IpSubnet,
            VpnType = n.VpnType,
            WireguardId = n.WireguardId,
            IsDhcpEnabled = n.DhcpdEnabled,
            DhcpRange = n.DhcpdEnabled ? $"{n.DhcpdStart} - {n.DhcpdStop}" : null,
            Gateway = n.DhcpdGateway,
            IsNat = n.IsNat,
            WanUploadMbps = n.WanProviderCapabilities?.UploadMbps,
            WanDownloadMbps = n.WanProviderCapabilities?.DownloadMbps,
            WanNetworkgroup = n.WanNetworkgroup,
            WanSmartqEnabled = n.WanSmartqEnabled,
            WanLoadBalanceType = n.WanLoadBalanceType,
            WanLoadBalanceWeight = n.WanLoadBalanceWeight,
            WanFailoverPriority = n.WanFailoverPriority,
            WanIfname = n.WanIfname
        }).ToList();
        _networkCacheTime = DateTime.UtcNow;

        return _cachedNetworks;
    }

    /// <summary>
    /// Resolves the primary WAN network from networkconf using load-balance
    /// configuration. Among enabled WANs with purpose "wan": weighted WANs
    /// beat failover-only, highest weight wins, lowest failover priority breaks
    /// ties, and networkgroup "WAN" is the final fallback. Returns null when no
    /// WAN networks are configured.
    /// </summary>
    public static NetworkInfo? ResolvePrimaryWanNetwork(IReadOnlyList<NetworkInfo> networks, ILogger? logger = null)
    {
        var wanNets = networks
            .Where(n => n.IsWan && n.Enabled)
            .ToList();
        if (wanNets.Count == 0) return null;
        if (wanNets.Count == 1)
        {
            logger?.LogDebug("Primary WAN is {Name} (networkgroup={NG}, single WAN)",
                wanNets[0].Name, wanNets[0].WanNetworkgroup);
            return wanNets[0];
        }

        var primary = wanNets
            .OrderBy(n => string.Equals(n.WanLoadBalanceType, "failover-only", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(n => n.WanLoadBalanceWeight ?? 0)
            .ThenBy(n => n.WanFailoverPriority ?? int.MaxValue)
            .ThenBy(n => string.Equals(n.WanNetworkgroup, "WAN", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .First();

        logger?.LogDebug(
            "Primary WAN is {Name} (networkgroup={NG}, type={LBType}, weight={Weight}, priority={Priority}) out of {Count} WANs",
            primary.Name, primary.WanNetworkgroup, primary.WanLoadBalanceType ?? "weighted",
            primary.WanLoadBalanceWeight, primary.WanFailoverPriority, wanNets.Count);
        return primary;
    }

    /// <summary>
    /// Whether the site spreads traffic across WANs rather than running one primary with the rest
    /// on failover. True when two or more enabled WANs are NOT marked failover-only, which is
    /// UniFi's way of saying they share the load.
    /// <para>
    /// It decides what an unpinned probe measures. Under failover-only, everything on the LAN
    /// leaves by the primary, so an ordinary agent measures the primary honestly and needs no
    /// policy route (during an actual failover it follows the backup - collateral we accept and
    /// state). Under load balancing the same probe is spread across WANs and attributable to
    /// none, so every probe source has to be pinned, the primary's included.
    /// </para>
    /// </summary>
    public static bool ResolveSiteLoadBalances(IReadOnlyList<NetworkInfo> networks) =>
        networks.Count(n => n.IsWan && n.Enabled
            && !string.Equals(n.WanLoadBalanceType, "failover-only", StringComparison.OrdinalIgnoreCase)) > 1;

    /// <summary>
    /// Convenience: fetches networks and resolves the primary WAN in one call.
    /// </summary>
    public async Task<NetworkInfo?> GetPrimaryWanNetworkAsync(CancellationToken ct = default)
    {
        var networks = await GetNetworksAsync(ct);
        return ResolvePrimaryWanNetwork(networks, _logger);
    }

    /// <summary>
    /// Resolves the interface forms of the CONFIGURED primary WAN by combining
    /// networkconf (which WAN is primary) with the cached device call (which
    /// interfaces carry that WAN's traffic). Returns both the SNMP counter
    /// interface (e.g. "eth6" - where InfluxDB rates are stored) and the data-path
    /// interface (e.g. "eth6.100"/"ppp0" - where SQM deploys). These differ on
    /// VLAN-tagged WANs. Returns null when no primary WAN can be resolved.
    /// </summary>
    public async Task<PrimaryWanInterfaces?> GetPrimaryWanInterfacesAsync(CancellationToken ct = default)
    {
        var primary = await GetPrimaryWanNetworkAsync(ct);
        if (primary?.WanNetworkgroup == null) return null;
        return await GetWanInterfacesForGroupAsync(primary.WanNetworkgroup, ct);
    }

    /// <summary>
    /// Resolves the interface forms of ANY WAN by its network group ("WAN", "WAN2") from the
    /// cached device call - the same walk <see cref="GetPrimaryWanInterfacesAsync"/> performs for
    /// the configured primary, generalized so per-WAN consumers (multi-WAN ISP Health, the WAN
    /// throughput selectors) pair a WAN's counters and data path with that same WAN's plan
    /// speeds instead of falling back to another WAN's. Returns null when the group's wan
    /// object cannot be found.
    /// </summary>
    public async Task<PrimaryWanInterfaces?> GetWanInterfacesForGroupAsync(string networkGroup, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(networkGroup) || _client == null) return null;
        var rawDevices = await _client.GetDevicesAsync(ct);
        var gw = rawDevices.FirstOrDefault(d => d.Type is "ugw" or "udm" or "uxg");
        if (gw == null) return null;

        var wanInterfaces = gw.GetWanInterfaces();
        if (wanInterfaces.Count == 0) return null;

        // Build ifname → networkgroup from ethernet_overrides
        var ifnameToNg = GatewayWanHelper.BuildNetworkGroupByIfname(
            gw.AdditionalData != null && gw.AdditionalData.TryGetValue("ethernet_overrides", out var eoElem)
                ? eoElem : default);

        // Find the wan object whose physical interface maps to the requested networkgroup
        foreach (var wan in wanInterfaces)
        {
            string? ng = null;
            if (!string.IsNullOrEmpty(wan.IfName))
                ifnameToNg.TryGetValue(wan.IfName, out ng);
            ng ??= GatewayWanHelper.WanNetworkGroupFromKey(wan.Key);

            if (string.Equals(ng, networkGroup, StringComparison.OrdinalIgnoreCase))
            {
                var counter = NetworkUtilities.PreferredWanCounterInterface(wan.IfName, wan.UplinkIfName);
                _logger.LogDebug("WAN {NG} interfaces: counter={Counter}, data-path={Uplink} (physical={Physical})",
                    ng, counter, wan.UplinkIfName ?? wan.IfName, wan.IfName);
                return new PrimaryWanInterfaces(ng, wan.IfName, wan.UplinkIfName, counter);
            }
        }

        return null;
    }

    /// <summary>
    /// Every WAN's interface forms from the cached device call, one entry per wan1..wan6 object
    /// with an uplink. The all-WAN usage fingerprint sums these counter interfaces; per-WAN load
    /// callers must NOT use this list (see MonitoringInfluxClient.QueryGatewayWanRatesAsync's
    /// summing contract) - they resolve their one WAN via
    /// <see cref="GetWanInterfacesForGroupAsync"/>.
    /// </summary>
    public async Task<List<PrimaryWanInterfaces>> GetAllWanInterfacesAsync(CancellationToken ct = default)
    {
        var results = new List<PrimaryWanInterfaces>();
        if (_client == null) return results;
        var rawDevices = await _client.GetDevicesAsync(ct);
        var gw = rawDevices.FirstOrDefault(d => d.Type is "ugw" or "udm" or "uxg");
        if (gw == null) return results;

        var wanInterfaces = gw.GetWanInterfaces();
        var ifnameToNg = GatewayWanHelper.BuildNetworkGroupByIfname(
            gw.AdditionalData != null && gw.AdditionalData.TryGetValue("ethernet_overrides", out var eoElem)
                ? eoElem : default);
        foreach (var wan in wanInterfaces)
        {
            if (string.IsNullOrEmpty(wan.UplinkIfName) && string.IsNullOrEmpty(wan.IfName)) continue;
            string? ng = null;
            if (!string.IsNullOrEmpty(wan.IfName))
                ifnameToNg.TryGetValue(wan.IfName, out ng);
            ng ??= GatewayWanHelper.WanNetworkGroupFromKey(wan.Key);
            var counter = NetworkUtilities.PreferredWanCounterInterface(wan.IfName, wan.UplinkIfName);
            results.Add(new PrimaryWanInterfaces(ng, wan.IfName, wan.UplinkIfName, counter));
        }
        return results;
    }

    /// <summary>
    /// Resolves the data-path interface name (e.g. "eth6.100", "ppp0") for the
    /// primary WAN - the Linux ifname SQM deploys on. Thin accessor over
    /// <see cref="GetPrimaryWanInterfacesAsync"/>.
    /// </summary>
    public async Task<string?> GetPrimaryWanDataPathInterfaceAsync(CancellationToken ct = default)
    {
        var ifaces = await GetPrimaryWanInterfacesAsync(ct);
        if (ifaces == null) return null;
        return ifaces.UplinkIfName ?? ifaces.PhysicalIfName;
    }

    /// <summary>
    /// Enrich a speed test result with client info from UniFi (MAC, name, Wi-Fi signal).
    /// </summary>
    /// <param name="result">The speed test result to enrich</param>
    /// <param name="setDeviceName">Whether to set DeviceName from UniFi (false for SSH tests that already have a name)</param>
    /// <param name="overwriteMac">Whether to overwrite existing MAC (false for SSH tests that may have MAC from config)</param>
    public async Task EnrichSpeedTestWithClientInfoAsync(Iperf3Result result, bool setDeviceName = true, bool overwriteMac = true)
    {
        if (!IsConnected || _client == null)
            return;

        try
        {
            var clients = await _client.GetClientsAsync();
            var client = clients?.FirstOrDefault(c => c.Ip == result.DeviceHost);

            // If IP match failed, try matching by MAC (for hostname-based tests where MAC was set by path analysis)
            if (client == null && !string.IsNullOrEmpty(result.ClientMac))
            {
                client = clients?.FirstOrDefault(c =>
                    c.Mac.Equals(result.ClientMac, StringComparison.OrdinalIgnoreCase));
            }

            if (client == null)
                return;

            // Set MAC address
            if (overwriteMac || string.IsNullOrEmpty(result.ClientMac))
                result.ClientMac = client.Mac;

            // Set device name from UniFi
            if (setDeviceName)
                result.DeviceName = !string.IsNullOrEmpty(client.Name) ? client.Name : client.Hostname;

            // Capture Wi-Fi signal for wireless clients
            if (!client.IsWired)
            {
                result.WifiSignalDbm = client.Signal;
                result.WifiNoiseDbm = client.Noise;
                result.WifiChannel = client.Channel;
                result.WifiRadioProto = client.RadioProto;
                result.WifiRadio = client.Radio;
                result.WifiTxRateKbps = client.TxRate;
                result.WifiRxRateKbps = client.RxRate;

                // Capture MLO (Multi-Link Operation) data for Wi-Fi 7 clients
                result.WifiIsMlo = client.IsMlo ?? false;
                if (client.IsMlo == true && client.MloDetails?.Count > 0)
                {
                    var mloLinks = client.MloDetails.Select(m => new
                    {
                        radio = m.Radio,
                        channel = m.Channel,
                        channelWidth = m.ChannelWidth,
                        signal = m.Signal,
                        noise = m.Noise,
                        txRate = m.TxRate,
                        rxRate = m.RxRate
                    }).ToList();
                    result.WifiMloLinksJson = JsonSerializer.Serialize(mloLinks);
                    _logger.LogDebug("Captured MLO data for {Ip}: {LinkCount} links",
                        result.DeviceHost, client.MloDetails.Count);
                }

                _logger.LogDebug("Enriched Wi-Fi info for {Ip}: Signal={Signal}dBm, Channel={Channel}, Radio={Radio}, Proto={Proto}, MLO={IsMlo}",
                    result.DeviceHost, result.WifiSignalDbm, result.WifiChannel, result.WifiRadio, result.WifiRadioProto, result.WifiIsMlo);
            }

            _logger.LogDebug("Enriched client info for {Ip}: MAC={Mac}, Name={Name}",
                result.DeviceHost, result.ClientMac, result.DeviceName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enrich client info for {Ip}", result.DeviceHost);
        }
    }

    /// <summary>
    /// Parses connection exceptions for user-friendly error messages
    /// </summary>
    private string ParseConnectionException(Exception ex)
    {
        var message = ex.Message;
        var innerMessage = ex.InnerException?.Message ?? "";

        // SSL certificate errors
        if (message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
            innerMessage.Contains("certificate", StringComparison.OrdinalIgnoreCase) ||
            innerMessage.Contains("RemoteCertificate", StringComparison.OrdinalIgnoreCase))
        {
            if (innerMessage.Contains("RemoteCertificateNameMismatch"))
            {
                return "SSL certificate error: The certificate doesn't match the hostname. Enable 'Ignore SSL Errors' in settings, or use the correct hostname.";
            }
            if (innerMessage.Contains("RemoteCertificateChainErrors"))
            {
                return "SSL certificate error: Self-signed or untrusted certificate. Enable 'Ignore SSL Errors' in settings.";
            }
            return "SSL certificate error: Unable to establish secure connection. Enable 'Ignore SSL Errors' in settings.";
        }

        // Connection refused
        if (message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
        {
            return "Connection refused. Check if the controller is running and the URL is correct.";
        }

        // Host not found
        if (message.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("host is known", StringComparison.OrdinalIgnoreCase))
        {
            return "Host not found. Check the controller URL.";
        }

        // Timeout (includes HttpClient.Timeout and TaskCanceledException)
        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("HttpClient.Timeout", StringComparison.OrdinalIgnoreCase) ||
            ex is TaskCanceledException)
        {
            return "Connection timed out. Check the console URL and firewall/VPN settings.";
        }

        return message;
    }

    public void Dispose()
    {
        // Instances are owned by SiteConnectionRegistry but handed out through a
        // scoped forwarding registration, so the container calls Dispose whenever a
        // request/circuit scope ends. Only the registry may tear down the shared
        // connection, via DisposeOwned.
    }

    internal void DisposeOwned()
    {
        _client?.Dispose();
        _connectGate.Dispose();
    }
}

public class UniFiConnectionConfig
{
    public string ControllerUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string? ApiKey { get; set; }
    public string Site { get; set; } = "default";
    public bool RememberCredentials { get; set; } = true;
    /// <summary>
    /// Whether to ignore SSL certificate errors when connecting to the controller.
    /// Default is true because UniFi controllers use self-signed certificates.
    /// </summary>
    public bool IgnoreControllerSSLErrors { get; set; } = true;

    /// <summary>Whether this config uses API key authentication</summary>
    public bool UseApiKey => !string.IsNullOrEmpty(ApiKey);
}
