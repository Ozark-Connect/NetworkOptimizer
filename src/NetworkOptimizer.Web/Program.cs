using ApexCharts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Audit;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web;
using NetworkOptimizer.Web.Endpoints;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;
using NetworkOptimizer.Web.Services.CableModemProviders;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Identity;
using NetworkOptimizer.Web.Services.Licensing;
using NetworkOptimizer.Web.Services.OntProviders;
using NetworkOptimizer.Web.Services.Ssh;
using Serilog;
using Serilog.Events;

// TODO(i18n): Add internationalization/localization support. Community volunteers available for translations.
// See: https://learn.microsoft.com/en-us/aspnet/core/blazor/globalization-localization

var builder = WebApplication.CreateBuilder(args);

// Windows Service support (no-op when running as console or on non-Windows)
if (OperatingSystem.IsWindows())
{
    // Load configuration from Windows Registry (set by MSI installer)
    // This runs before env vars so env vars can override registry values
    builder.Configuration.AddInMemoryCollection(LoadWindowsRegistrySettings());

    builder.Host.UseWindowsService(options =>
    {
        options.ServiceName = "NetworkOptimizer";
    });

    // Configure Kestrel to listen on port 8042 for Windows service mode
    // Only set if ASPNETCORE_URLS or ASPNETCORE_HTTP_PORTS is not already configured
    var urlsConfigured = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
                      || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"));
    if (!urlsConfigured)
    {
        builder.WebHost.UseUrls("http://*:8042");
    }
}

// Configure Data Protection to persist keys to the data volume
var isDocker = string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);
var keysPath = isDocker
    ? "/app/data/keys"
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetworkOptimizer", "keys");
Directory.CreateDirectory(keysPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("NetworkOptimizer");

// Add services to the container
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Configure logging with Serilog
// Read log levels from configuration (supports env vars like Logging__LogLevel__NetworkOptimizer=Debug)
var defaultLogLevel = builder.Configuration.GetValue("Logging:LogLevel:Default", "Information");
var appLogLevel = builder.Configuration.GetValue("Logging:LogLevel:NetworkOptimizer", "Information");

var loggerConfig = new LoggerConfiguration()
    .MinimumLevel.Is(Enum.Parse<LogEventLevel>(defaultLogLevel, ignoreCase: true))
    .MinimumLevel.Override("NetworkOptimizer", Enum.Parse<LogEventLevel>(appLogLevel, ignoreCase: true))
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Extensions.Http", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");

// Add file logging for Windows (in the logs folder under install directory)
if (OperatingSystem.IsWindows())
{
    var logFolder = Path.Combine(AppContext.BaseDirectory, "logs");
    Directory.CreateDirectory(logFolder);
    var logPath = Path.Combine(logFolder, "networkoptimizer-.log");

    loggerConfig.WriteTo.File(
        logPath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
}

Log.Logger = loggerConfig.CreateLogger();
builder.Host.UseSerilog();

// Add memory cache for path analysis caching
builder.Services.AddMemoryCache();

// Register file version provider for cache-busting static assets (CSS, JS)
builder.Services.AddSingleton<IFileVersionProvider, NetworkOptimizer.Web.Services.FileVersionProvider>();

// Register credential protection service (singleton - shared encryption key)
builder.Services.AddSingleton<NetworkOptimizer.Storage.Services.ICredentialProtectionService, NetworkOptimizer.Storage.Services.CredentialProtectionService>();

// All UniFi console connections live in SiteConnectionRegistry, one long-lived
// instance per site. Scoped resolution (components, request handlers, per-site
// scopes) forwards to the current site's instance; singletons and background
// code inject the registry directly.
builder.Services.AddSiteScopedRegistry<SiteConnectionRegistry>();
builder.Services.AddScoped(sp => sp.GetRequiredService<SiteConnectionRegistry>()
    .GetFor(sp.GetRequiredService<SiteContextService>().Slug));
builder.Services.AddSingleton<IUniFiClientProvider>(sp => sp.GetRequiredService<SiteConnectionRegistry>().GetDefault());

// Register Network Path Analyzer (singleton - uses caching)
// Default site's path analyzer, owned by the speed test registry so the whole
// enrichment family (analyzer, snapshots, client speed test) shares one topology
// cache per site. Singleton consumers keep injecting the interface and get the
// default site's instance; per-site consumers resolve through the registry.
builder.Services.AddSingleton<INetworkPathAnalyzer>(sp =>
    sp.GetRequiredService<SpeedTestServiceRegistry>().GetDefault().PathAnalyzer);

// Register audit engine and analyzers
builder.Services.AddTransient<VlanAnalyzer>();
builder.Services.AddTransient<PortSecurityAnalyzer>();
builder.Services.AddTransient<FirewallRuleParser>();
builder.Services.AddTransient<FirewallRuleAnalyzer>();
builder.Services.AddTransient<AuditScorer>();
builder.Services.AddTransient<ConfigAuditEngine>();

// Register TC Monitor client (singleton - shared HTTP client)
builder.Services.AddSingleton<TcMonitorClient>();

// Register SQLite database context
// Docker: /app/data, Windows: install dir, macOS/Linux: LocalApplicationData
string dbPath;
if (isDocker)
{
    dbPath = "/app/data/network_optimizer.db";
}
else if (OperatingSystem.IsWindows())
{
    // Windows: store in data folder under install directory (survives updates, removed on uninstall)
    var dataFolder = Path.Combine(AppContext.BaseDirectory, "data");
    Directory.CreateDirectory(dataFolder);
    dbPath = Path.Combine(dataFolder, "network_optimizer.db");
}
else
{
    // macOS/Linux: use LocalApplicationData
    dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetworkOptimizer", "network_optimizer.db");
}
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
// Scoped DbContext routes to the current site's database via SiteContextService
// (cookie-driven; scopes without an HTTP context resolve to the default site's main DB).
builder.Services.AddDbContext<NetworkOptimizerDbContext>((sp, options) =>
    options.UseSqlite($"Data Source={sp.GetRequiredService<SiteContextService>().DbPath}")
           .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

// Register DbContextFactory for singleton services (ClientSpeedTestService, Iperf3ServerService)
// that need database access but can't inject scoped DbContext.
//
// Why custom factory? AddDbContext registers DbContextOptions as Scoped, but AddDbContextFactory
// registers it as Singleton. Using both causes DI validation errors in Development mode:
// "Cannot consume scoped service from singleton". Our custom factory owns its own options instance,
// avoiding the conflict entirely.
var factoryOptions = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
    .UseSqlite($"Data Source={dbPath}")
    .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
    .Options;
builder.Services.AddSingleton<IDbContextFactory<NetworkOptimizerDbContext>>(
    new NetworkOptimizer.Storage.Models.NetworkOptimizerDbContextFactory(factoryOptions));

// Per-site database path resolution (main db doubles as the site registry and default site data)
builder.Services.AddSingleton(new NetworkOptimizer.Storage.Services.SiteDatabasePaths(dbPath));
builder.Services.AddSingleton<NetworkOptimizer.Storage.Services.SiteDbContextFactory>();

// ---- Multi-site agent tunnel listener ----
// The agent tunnel is gRPC, which needs HTTP/2. The main port stays HTTP/1.1
// (reverse proxies, browsers), so the tunnel gets its own HTTP/2 listener,
// served over TLS with an ephemeral self-signed cert (see
// CreateSelfSignedTunnelCert) so the reverse-proxy-to-app hop is encrypted even
// across a box boundary. Explicit Kestrel Listen calls override URL-based configuration,
// so when the tunnel is active the main HTTP port(s) are re-bound explicitly
// alongside it. Single-site installs never take this branch: no new port, no
// binding changes. The flag is read with raw SQLite because Kestrel must be
// configured before the service provider (and EF) exist.
var agentTunnelPort = builder.Configuration.GetValue("AgentTunnel:Port", 8043);
var agentTunnelEnabled = false;
try
{
    if (File.Exists(dbPath))
    {
        using var flagConnection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        flagConnection.Open();
        using var flagCommand = flagConnection.CreateCommand();
        flagCommand.CommandText = "SELECT Value FROM SystemSettings WHERE Key = @key";
        flagCommand.Parameters.AddWithValue("@key", NetworkOptimizer.Storage.Models.SystemSettingKeys.MultiSiteEnabled);
        agentTunnelEnabled = bool.TryParse(flagCommand.ExecuteScalar() as string, out var multiSiteFlag) && multiSiteFlag;
    }
}
catch
{
    // Fresh install or pre-multi-site schema: tunnel stays off until enabled + restart.
}

if (agentTunnelEnabled)
{
    var mainBindings = StartupHelpers.ResolveHttpBindings();
    if (mainBindings == null)
    {
        // Custom HTTPS or non-port URL configuration we can't safely re-bind.
        Console.WriteLine("Agent tunnel disabled: ASPNETCORE_URLS contains bindings the tunnel listener cannot co-exist with (HTTPS or non-port URLs)");
        agentTunnelEnabled = false;
    }
    else
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            foreach (var (host, port) in mainBindings)
            {
                if (host == "localhost")
                    options.ListenLocalhost(port);
                else if (System.Net.IPAddress.TryParse(host, out var ip))
                    options.Listen(ip, port);
                else
                    options.ListenAnyIP(port);
            }
            options.ListenAnyIP(agentTunnelPort, listen =>
            {
                listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
                // TLS on the tunnel port so the reverse-proxy-to-app hop is
                // encrypted even when the proxy runs on a separate box (the
                // agent key and pushed SNMP credentials would otherwise cross
                // that LAN segment in cleartext h2c). The proxy is configured to
                // skip verification, so an ephemeral self-signed cert suffices.
                listen.UseHttps(StartupHelpers.CreateSelfSignedTunnelCert());
            });
        });
    }
}
builder.Services.AddSingleton(new AgentTunnelOptions(agentTunnelEnabled, agentTunnelPort));
builder.Services.AddGrpc();
builder.Services.AddSingleton<AgentTunnelRegistry>();
builder.Services.AddSingleton<AgentProbeResultSink>();
builder.Services.AddSingleton<AgentTunnelProxyService>();
builder.Services.AddSingleton<AgentIperf3Service>();
builder.Services.AddSingleton<AgentUwnService>();
builder.Services.AddSingleton<AgentProbeService>();
builder.Services.AddSingleton<AgentSnmpQueryService>();
builder.Services.AddSingleton<CanonicalBaseUrlProvider>();
builder.Services.AddSingleton<AgentServerUrlProvider>();

// Register repository pattern (scoped - same lifetime as DbContext)
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.IAuditRepository, NetworkOptimizer.Storage.Repositories.AuditRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.ISettingsRepository, NetworkOptimizer.Storage.Repositories.SettingsRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.IUniFiRepository, NetworkOptimizer.Storage.Repositories.UniFiRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.IModemRepository, NetworkOptimizer.Storage.Repositories.ModemRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.ICmRepository, NetworkOptimizer.Storage.Repositories.CmRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.IOntRepository, NetworkOptimizer.Storage.Repositories.OntRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.IStarlinkRepository, NetworkOptimizer.Storage.Repositories.StarlinkRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.IFirmwareRolloutRepository, NetworkOptimizer.Storage.Repositories.FirmwareRolloutRepository>();
// Singleton on the MAIN database whichever site is planning: the shared firmware catalog pools
// what every site's console has been offered, so per-site executors and scoped services share it.
builder.Services.AddSingleton<NetworkOptimizer.Storage.Interfaces.ISharedFirmwareCatalogRepository, NetworkOptimizer.Storage.Repositories.SharedFirmwareCatalogRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.IMonitoringInterfaceRepository, NetworkOptimizer.Storage.Repositories.MonitoringInterfaceRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.ISpeedTestRepository, NetworkOptimizer.Storage.Repositories.SpeedTestRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.ISqmRepository, NetworkOptimizer.Storage.Repositories.SqmRepository>();
builder.Services.AddScoped<NetworkOptimizer.Alerts.Interfaces.IAlertRepository, NetworkOptimizer.Storage.Repositories.AlertRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.ISiteRepository, NetworkOptimizer.Storage.Repositories.SiteRepository>();
builder.Services.AddSingleton<SiteRegistryChangeNotifier>();
builder.Services.AddScoped<SiteManagementService>();
builder.Services.AddMutatingService<ISiteManagementService>(sp => sp.GetRequiredService<SiteManagementService>());
builder.Services.AddScoped<SiteContextService>();
builder.Services.AddScoped<SiteSwitchService>();
// The alert pipeline pins its scope to an event's originating site through this seam.
builder.Services.AddScoped<NetworkOptimizer.Alerts.Interfaces.IAlertSiteScope>(sp =>
    sp.GetRequiredService<SiteContextService>());
// Resolves a site's display name so delivered alerts name their originating site.
builder.Services.AddSingleton<NetworkOptimizer.Alerts.Interfaces.IAlertSiteNameResolver, AlertSiteNameResolver>();
builder.Services.AddSingleton<AgentEnrollmentService>();
// The agent tunnel keeps using the concrete singleton (it authenticates with the agent scheme and
// runs as system); the admin-facing enrollment surface is gated.
builder.Services.AddMutatingService<IAgentEnrollmentService>(sp => sp.GetRequiredService<AgentEnrollmentService>());
// Detects agents running on the site's UniFi gateway itself (monitoring-only
// installs) so speed-test surfaces can gate accordingly.
builder.Services.AddSingleton<AgentOnGatewayDetector>();

// Licensing: singleton state machine, activation and phone-home loop. All
// licensing data is instance-wide registry data in the main database.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient("LicenseServer", client => client.Timeout = TimeSpan.FromSeconds(10));

// Ubiquiti's public release feed: publish dates, changelog links, and prior-version firmware URLs
// the console's latest-only catalog cannot supply. Read-only and anonymous, so a plain singleton.
builder.Services.AddHttpClient(
    NetworkOptimizer.Web.Services.Firmware.UbiquitiReleaseFeedClient.HttpClientName,
    client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<NetworkOptimizer.Web.Services.Firmware.UbiquitiReleaseFeedClient>();
// Publish dates (autopilot's release-ripeness gate) and changelog links (the soak report) off that feed.
builder.Services.AddSingleton<NetworkOptimizer.Web.Services.Firmware.IReleaseMetadataSource,
    NetworkOptimizer.Web.Services.Firmware.ReleaseFeedMetadataSource>();
builder.Services.AddSingleton<LicenseServerClient>();
builder.Services.AddSingleton<LicenseStateService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LicenseStateService>());
// The one gate every per-site background loop consults. Registered against the same singleton so
// Alerts and Threats - which cannot see the Web project - read exactly the state Web enforces.
builder.Services.AddSingleton<NetworkOptimizer.Core.ISiteWorkGate>(
    sp => sp.GetRequiredService<LicenseStateService>());
builder.Services.AddSingleton<LicenseActivationService>();
builder.Services.AddMutatingService<NetworkOptimizer.Web.Services.Licensing.ILicenseActivationService>(sp => sp.GetRequiredService<LicenseActivationService>());
builder.Services.AddSingleton<LicensePhoneHomeService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LicensePhoneHomeService>());
builder.Services.AddHostedService<LicenseEnforcementCoordinator>();

// Shared site-local speed-test target resolution (Client Dashboard, LAN Speed
// Test page, WAN speed test link) - scoped, follows the current site context.
builder.Services.AddScoped<SiteSpeedTestTargetResolver>();

// Register SSH client service (singleton - cross-platform SSH.NET wrapper)
builder.Services.AddSingleton<SshClientService>();

// Gateway SSH per site: the registry owns one GatewaySshService per site (settings
// from that site's DB, host fallback from that site's console). Scoped resolution of
// IGatewaySshService forwards to the current site's instance; singleton consumers
// inject the registry and pin GetDefault() or GetFor(slug).
// Shared per-site tunnel routing for device endpoints (SSH, modem/ONT status
// pages): consults the site's devices.via_agent flag and rewrites host:port to
// an agent tunnel proxy endpoint when enabled.
builder.Services.AddSingleton<SiteTunnelRouting>();
// Whether a site's agent collects instead of this server. Singleton: consulted by the
// per-site collection loops and the probe executor factory, and it caches per slug.
builder.Services.AddSiteScopedRegistry<SiteAgentCoverage>();
builder.Services.AddSiteScopedRegistry<GatewaySshRegistry>();
builder.Services.AddScoped<IGatewaySshService>(sp => sp.GetRequiredService<GatewaySshRegistry>()
    .GetFor(sp.GetRequiredService<SiteContextService>().Slug));

// Register udm-boot installer (scoped - shared gateway boot-script infrastructure
// used by Adaptive SQM, Monitoring Interfaces, etc.; follows the current site's
// gateway SSH so deployments persist on the right gateway)
builder.Services.AddScoped<IUdmBootService, UdmBootService>();

// Device SSH per site: the registry owns one UniFiSshService per site (shared
// device credentials + per-device configs from that site's DB). Scoped resolution
// forwards to the current site's instance; singleton consumers inject the
// registry and pin GetDefault() or GetFor(slug).
builder.Services.AddSiteScopedRegistry<UniFiSshRegistry>();
builder.Services.AddScoped(sp => sp.GetRequiredService<UniFiSshRegistry>()
    .GetFor(sp.GetRequiredService<SiteContextService>().Slug));

// Cellular modem monitoring is per site through ModemMonitorRegistry, which
// builds each site's provider set (qmicli with the site's device SSH; HTTP and
// per-modem-SSH providers are context-driven). Scoped resolution forwards to
// the current site's monitor.

// Register Cable Modem providers (stateless scrapers, shared across sites).
// The monitor itself is per site through ModemMonitorRegistry: configurations,
// stats, and alerts belong to each site's own database and buckets, and the
// registry activates instances as sites are enabled. Scoped resolution
// forwards to the current site's monitor.
builder.Services.AddSingleton<ICableModemProvider, NetgearCmProvider>();
builder.Services.AddSingleton<ICableModemProvider, ArrisSurfboardHttpProvider>();
builder.Services.AddSingleton<ICableModemProvider, ArrisSurfboardHnapProvider>();
builder.Services.AddSingleton<ICableModemProvider, MotorolaHnapProvider>();
builder.Services.AddSingleton<ICableModemProvider, XfinityGatewayProvider>();
builder.Services.AddSingleton<ICableModemProvider, TechnicolorCgaProvider>();
builder.Services.AddSingleton<ICableModemProvider, VodafoneStationProvider>();
builder.Services.AddSiteScopedRegistry<ModemMonitorRegistry>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ModemMonitorRegistry>());

// Register External ONT providers (stateless scrapers, shared across sites).
// The monitor itself is per site through ModemMonitorRegistry, like the cable
// modem monitor above.
builder.Services.AddSingleton<IOntProvider, AttGatewayOntProvider>();
builder.Services.AddSingleton<IOntProvider, RealtekOntProvider>();
builder.Services.AddSingleton<IOntProvider, Lantiq8311OntProvider>();
builder.Services.AddSingleton<IOntProvider, QuantumQ1000kOntProvider>();
builder.Services.AddSingleton<IOntProvider, GenericHttpOntProvider>();
builder.Services.AddSingleton<IOntProvider, TelekomModem2OntProvider>();
builder.Services.AddSingleton<IOntProvider, NokiaXs010xOntProvider>();
builder.Services.AddSingleton<IOntProvider, ZyxelGponSfpOntProvider>();
builder.Services.AddSingleton<IOntProvider, NetOptCustomPonOntProvider>();

// Starlink terminal monitoring is per site through ModemMonitorRegistry, which
// builds each site's provider set (the gRPC provider keeps per-config history
// state, so instances are per site like the cellular providers). Scoped
// resolution forwards to the current site's monitor.

// LAN iperf3 speed test per site (registry-owned): devices, credentials, and
// results live in that site's database; tests run against that site's devices.
// Scoped resolution forwards to the current site's instance.
// The cellular service is owned by the per-site modem registry, so it is gated through the
// factory overload: callers resolve the interface and get a proxy over the current site's instance.
builder.Services.AddMutatingService<ICableModemService>(sp => sp.GetRequiredService<ModemMonitorRegistry>()
    .GetFor(sp.GetRequiredService<SiteContextService>().Slug).CableModem);

builder.Services.AddMutatingService<IStarlinkMonitorService>(sp => sp.GetRequiredService<ModemMonitorRegistry>()
    .GetFor(sp.GetRequiredService<SiteContextService>().Slug).Starlink);

builder.Services.AddMutatingService<IOntMonitorService>(sp => sp.GetRequiredService<ModemMonitorRegistry>()
    .GetFor(sp.GetRequiredService<SiteContextService>().Slug).Ont);

builder.Services.AddMutatingService<ICellularModemService>(sp => sp.GetRequiredService<ModemMonitorRegistry>()
    .GetFor(sp.GetRequiredService<SiteContextService>().Slug).Cellular);

builder.Services.AddMutatingService<IIperf3SpeedTestService>(sp => sp.GetRequiredService<SpeedTestServiceRegistry>()
    .GetFor(sp.GetRequiredService<SiteContextService>().Slug).LanSpeedTest);

// Register Gateway Speed Test service (scoped - forwards to the current site's
// gateway SSH and database; gateway iperf3 tests with separate SSH creds)
builder.Services.AddMutatingService<IGatewaySpeedTestService, GatewaySpeedTestService>();

// Client Speed Test per site: the registry owns one enrichment bundle per site
// (path analyzer + topology snapshots + client speed test service). Scoped
// resolution forwards to the current site's instance so pages show that site's
// results; the public results endpoint routes by slug parameter.
builder.Services.AddSiteScopedRegistry<SpeedTestServiceRegistry>();
builder.Services.AddMutatingService<IClientSpeedTestService>(sp => sp.GetRequiredService<SpeedTestServiceRegistry>()
    .GetFor(sp.GetRequiredService<SiteContextService>().Slug).ClientSpeedTest);

// Register Client Dashboard service (scoped - forwards to the current site's
// connection, speed test service, and database; signal polling, trace tracking)
builder.Services.AddScoped<ClientDashboardService>();

// Register WAN Speed Test services. Cloudflare stays a default-site singleton
// (legacy history only) - if reactivated it must be moved into SpeedTestServiceRegistry
// and resolved per-site (see the note on CloudflareSpeedTestService). UWN is per site
// through the registry: non-default instances serve that site's result history; runs
// stay default-only (the local binary measures this server's own WAN).
builder.Services.AddSingleton<CloudflareSpeedTestService>();
builder.Services.AddMutatingService<IUwnSpeedTestService>(sp => sp.GetRequiredService<SpeedTestServiceRegistry>()
    .GetFor(sp.GetRequiredService<SiteContextService>().Slug).Uwn);

// Gateway WAN Speed Test per site (registry-owned): the test runs on that site's
// gateway via its own SSH settings and stores to that site's database. Scoped
// resolution forwards to the current site's instance; the schedule executor
// resolves by site key through the registry.
builder.Services.AddMutatingService<IGatewayWanSpeedTestService>(sp => sp.GetRequiredService<SpeedTestServiceRegistry>()
    .GetFor(sp.GetRequiredService<SiteContextService>().Slug).GatewayWan);

// Topology Snapshot service: default site's instance comes from the speed test
// registry (per-site instances capture against their own site's console).
builder.Services.AddSingleton<TopologySnapshotService>(sp =>
    sp.GetRequiredService<SpeedTestServiceRegistry>().GetDefault().Snapshots);
builder.Services.AddSingleton<ITopologySnapshotService>(sp => sp.GetRequiredService<TopologySnapshotService>());

// Register iperf3 Server service (hosted - runs iperf3 in server mode, monitors for client tests)
// Enable via environment variable: Iperf3Server__Enabled=true
// Registered as singleton so it can be injected to check status (e.g., startup failure)
builder.Services.AddSingleton<Iperf3ServerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Iperf3ServerService>());

// Register nginx hosted service (Windows only - manages nginx for OpenSpeedTest)
builder.Services.AddHostedService<NginxHostedService>();

// Register Traefik hosted service (Windows only - manages Traefik for HTTPS reverse proxying)
builder.Services.AddHostedService<TraefikHostedService>();

// Register Alert Engine services (Vigilance)
builder.Services.AddSingleton<NetworkOptimizer.Alerts.Events.IAlertEventBus, NetworkOptimizer.Alerts.Events.AlertEventBus>();
builder.Services.AddSingleton<NetworkOptimizer.Alerts.AlertCooldownTracker>();
builder.Services.AddSingleton<NetworkOptimizer.Alerts.AlertRuleEvaluator>();
builder.Services.AddSingleton<NetworkOptimizer.Alerts.AlertCorrelationService>();
builder.Services.AddSingleton<NetworkOptimizer.Alerts.AlertProcessingService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NetworkOptimizer.Alerts.AlertProcessingService>());
builder.Services.AddSingleton<NetworkOptimizer.Alerts.DigestService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NetworkOptimizer.Alerts.DigestService>());
builder.Services.AddHostedService<AgentConnectionAlertMonitor>();
// IDigestStateStore adapter: persists digest "last sent" timestamps via SystemSettings
builder.Services.AddScoped<NetworkOptimizer.Alerts.Interfaces.IDigestStateStore, DigestStateStoreAdapter>();
// ISecretDecryptor adapter: bridges Alerts project's interface to existing credential protection
builder.Services.AddSingleton<NetworkOptimizer.Alerts.Delivery.ISecretDecryptor>(sp =>
{
    var credService = sp.GetRequiredService<NetworkOptimizer.Storage.Services.ICredentialProtectionService>();
    return new SecretDecryptorAdapter(credService);
});
// Delivery channels (singleton - stateless, use HttpClient)
builder.Services.AddSingleton<NetworkOptimizer.Alerts.Delivery.IAlertDeliveryChannel, NetworkOptimizer.Alerts.Delivery.EmailDeliveryChannel>();
builder.Services.AddSingleton<NetworkOptimizer.Alerts.Delivery.IAlertDeliveryChannel>(sp =>
    new NetworkOptimizer.Alerts.Delivery.WebhookDeliveryChannel(
        sp.GetRequiredService<ILogger<NetworkOptimizer.Alerts.Delivery.WebhookDeliveryChannel>>(),
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
        sp.GetRequiredService<NetworkOptimizer.Alerts.Delivery.ISecretDecryptor>()));
builder.Services.AddSingleton<NetworkOptimizer.Alerts.Delivery.IAlertDeliveryChannel>(sp =>
    new NetworkOptimizer.Alerts.Delivery.SlackDeliveryChannel(
        sp.GetRequiredService<ILogger<NetworkOptimizer.Alerts.Delivery.SlackDeliveryChannel>>(),
        sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
builder.Services.AddSingleton<NetworkOptimizer.Alerts.Delivery.IAlertDeliveryChannel>(sp =>
    new NetworkOptimizer.Alerts.Delivery.DiscordDeliveryChannel(
        sp.GetRequiredService<ILogger<NetworkOptimizer.Alerts.Delivery.DiscordDeliveryChannel>>(),
        sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
builder.Services.AddSingleton<NetworkOptimizer.Alerts.Delivery.IAlertDeliveryChannel>(sp =>
    new NetworkOptimizer.Alerts.Delivery.TeamsDeliveryChannel(
        sp.GetRequiredService<ILogger<NetworkOptimizer.Alerts.Delivery.TeamsDeliveryChannel>>(),
        sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
builder.Services.AddSingleton<NetworkOptimizer.Alerts.Delivery.IAlertDeliveryChannel>(sp =>
    new NetworkOptimizer.Alerts.Delivery.NtfyDeliveryChannel(
        sp.GetRequiredService<ILogger<NetworkOptimizer.Alerts.Delivery.NtfyDeliveryChannel>>(),
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
        sp.GetRequiredService<NetworkOptimizer.Alerts.Delivery.ISecretDecryptor>()));

// Register Threat Intelligence services
builder.Services.AddSingleton<NetworkOptimizer.Threats.Enrichment.GeoEnrichmentService>();
builder.Services.AddSingleton<NetworkOptimizer.Threats.CrowdSec.CrowdSecClient>();
builder.Services.AddSingleton<NetworkOptimizer.Threats.CrowdSec.CrowdSecEnrichmentService>();
builder.Services.AddSingleton<NetworkOptimizer.Threats.ThreatEventNormalizer>();
builder.Services.AddSingleton<NetworkOptimizer.Threats.Analysis.KillChainClassifier>();
builder.Services.AddSingleton<NetworkOptimizer.Threats.Analysis.ThreatPatternAnalyzer>();
builder.Services.AddSingleton<NetworkOptimizer.Threats.Analysis.ExposureValidator>();
builder.Services.AddSingleton<NetworkOptimizer.Threats.ThreatCollectionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NetworkOptimizer.Threats.ThreatCollectionService>());
builder.Services.AddScoped<NetworkOptimizer.Threats.Interfaces.IThreatRepository, NetworkOptimizer.Storage.Repositories.ThreatRepository>();
builder.Services.AddScoped<NetworkOptimizer.Web.Services.ThreatDashboardService>();
// Reads stay on the concrete service (the dashboard is a Viewer surface); the noise-filter writes
// go through the gate over the same instance.
builder.Services.AddMutatingService<NetworkOptimizer.Web.Services.IThreatFilterAdminService>(
    sp => sp.GetRequiredService<NetworkOptimizer.Web.Services.ThreatDashboardService>());
builder.Services.AddScoped<NetworkOptimizer.Threats.Interfaces.IThreatSettingsAccessor, NetworkOptimizer.Web.Services.ThreatSettingsAccessor>();
builder.Services.AddSingleton<NetworkOptimizer.Threats.Interfaces.IUniFiClientAccessor, NetworkOptimizer.Web.Services.UniFiClientAccessor>();

// Register Schedule services (scheduling engine for periodic audits, speed tests)
builder.Services.AddScoped<NetworkOptimizer.Alerts.Interfaces.IScheduleRepository, NetworkOptimizer.Storage.Repositories.ScheduleRepository>();
builder.Services.AddMutatingService<IAlertConfigService, AlertConfigService>();
// Site fan-out for the schedule loop: each enabled site's schedules run in a
// scope pinned to that site's database and console connection.
builder.Services.AddSingleton<NetworkOptimizer.Alerts.Interfaces.IScheduleSiteContext, ScheduleSiteContext>();
builder.Services.AddSingleton<NetworkOptimizer.Alerts.ScheduleService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NetworkOptimizer.Alerts.ScheduleService>());

// Register WAN Data Usage tracking service (singleton - polls WAN counters, calculates billing cycle usage)
// WAN data usage tracking is per site: the registry owns one WanDataUsageService per
// site (its console + its DB), the default starting with the app and non-default sites
// reconciled in. Scoped resolution forwards to the current site's collector.
builder.Services.AddSiteScopedRegistry<WanDataUsageRegistry>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WanDataUsageRegistry>());
builder.Services.AddScoped(sp => sp.GetRequiredService<WanDataUsageRegistry>()
    .GetFor(sp.GetRequiredService<SiteContextService>().Slug));

// Register System Settings service (singleton - system-wide configuration)
builder.Services.AddSingleton<SystemSettingsService>();
builder.Services.AddSingleton<ISystemSettingsService>(sp => sp.GetRequiredService<SystemSettingsService>());
// Reads stay ungated (pollers and collectors read settings on every cycle); the UI's writes go
// through the gated admin interface so each one is authorized and audited.
builder.Services.AddMutatingService<ISystemSettingsAdmin>(sp => sp.GetRequiredService<SystemSettingsService>());

// Register Sponsorship service (singleton - reads from DB, limited state)
builder.Services.AddSingleton<ISponsorshipService, SponsorshipService>();

// Guided tours: definitions and state are instance-wide (main DB); the orchestrator is
// scoped because predicate resolution and ?site= stamping depend on the circuit's site.
builder.Services.AddSingleton<NetworkOptimizer.Web.Services.Tours.TourDefinitionService>();
builder.Services.AddSingleton<NetworkOptimizer.Web.Services.Tours.TourStateService>();
builder.Services.AddScoped<NetworkOptimizer.Web.Services.Tours.TourPredicateResolver>();
builder.Services.AddScoped<NetworkOptimizer.Web.Services.Tours.TourService>();
builder.Services.AddHostedService<NetworkOptimizer.Web.Services.Tours.TourStartupService>();

// Register password hasher (singleton - stateless)
builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();

// Register Admin Auth service (scoped - depends on ISettingsRepository)
builder.Services.AddSingleton<NetworkOptimizer.Web.Services.AdminAuthCache>();
builder.Services.AddScoped<IAdminAuthService, AdminAuthService>();

// Register JWT service (singleton - caches secret key)
builder.Services.AddSingleton<IJwtService, JwtService>();

// Add HttpContextAccessor for accessing cookies in Blazor
builder.Services.AddHttpContextAccessor();

// ASP.NET Core Identity on the dedicated main-DB AuthDbContext (users, roles, RBAC, audit),
// plus the interactive cookie authentication pipeline that REPLACES the legacy self-issued
// JWT-in-cookie scheme. The Identity application cookie gains server-side revocation via security
// stamps (which the JWT cookie fundamentally lacked). Sessions from before the upgrade survive via
// the LegacyJwtBridgeMiddleware (added below). JwtService is retained only for that bridge and is
// removed one release after the cutover (design docs 02, 06).
builder.Services.AddNetOptIdentityCore(dbPath);
builder.Services.AddNetOptIdentityAuthentication();
builder.Services.AddCascadingAuthenticationState();

// RBAC policies (global + site-scoped), the single SiteRoleHandler, and the effective-role resolver.
builder.Services.AddNetOptAuthorization();

// Declarative service-layer gate: Castle DynamicProxy interceptor doing authz + audit envelope.
builder.Services.AddNetOptGates();

// Monitoring subsystem
builder.Services.AddScoped<SnmpDetectionService>();
builder.Services.AddScoped<MonitoringReadinessService>();
// Per-site Influx clients (D1: bucket-per-site) live in the registry - the
// default site's included. Scoped resolution forwards to the current site's
// client so chart endpoints and pages read that site's buckets; singleton
// consumers inject the registry and pin GetDefault().
builder.Services.AddSiteScopedRegistry<MonitoringInfluxRegistry>();
builder.Services.AddScoped(sp => sp.GetRequiredService<MonitoringInfluxRegistry>()
    .GetFor(sp.GetRequiredService<SiteContextService>().Slug));
// Per-site live monitoring caches, same forwarding shape: pages/endpoints get
// the current site's instance, singleton collectors pin the default, and the
// agent result sink records into the owning site's instance.
builder.Services.AddSiteScopedRegistry<MonitoringLiveStatsRegistry>();
builder.Services.AddScoped(sp => sp.GetRequiredService<MonitoringLiveStatsRegistry>()
    .GetFor(sp.GetRequiredService<SiteContextService>().Slug));
builder.Services.AddSingleton<NetworkOptimizer.Web.Services.Monitoring.WanSummaryCache>();
// Devices UniFi reports as upgrading/provisioning, so the offline path can stay quiet for a
// restart that was asked for. Site-keyed internally, hence one singleton rather than a registry.
builder.Services.AddSingleton<NetworkOptimizer.Web.Services.Monitoring.DeviceTransitionTracker>();
// Per-site device reboot trackers: uptime samples go in - from the collection tier where it is
// running, and from DeviceRebootObserver regardless - and the dashboard reads the reason behind
// each device's current boot back out.
builder.Services.AddSiteScopedRegistry<NetworkOptimizer.Web.Services.Monitoring.RebootReason.DeviceRebootRegistry>();
// Observes device uptime from the console alone, so reboot reasons resolve whether or not
// monitoring is collecting anything.
builder.Services.AddHostedService<NetworkOptimizer.Web.Services.Monitoring.RebootReason.DeviceRebootObserver>();
builder.Services.AddScoped(sp => sp.GetRequiredService<NetworkOptimizer.Web.Services.Monitoring.RebootReason.DeviceRebootRegistry>()
    .GetFor(sp.GetRequiredService<SiteContextService>().Slug));
// ISP Health is per site: the registry owns one IspHealthService (with its own
// PhysicalLinkResolver, report cache, and compute state) per site; scoped
// resolution forwards to the current site's instance.
builder.Services.AddSiteScopedRegistry<NetworkOptimizer.Web.Services.Monitoring.IspHealth.IspHealthRegistry>();
builder.Services.AddScoped(sp => sp.GetRequiredService<NetworkOptimizer.Web.Services.Monitoring.IspHealth.IspHealthRegistry>()
    .GetFor(sp.GetRequiredService<SiteContextService>().Slug));
// Scoped - forwards to the current site's Influx client and database.
builder.Services.AddScoped<NetworkOptimizer.Web.Services.Monitoring.FlakyTargetService>();
builder.Services.AddScoped<NetworkOptimizer.Web.Services.Monitoring.MonitoringPathView>();
// Transient: every live-tile surface keeps its own selection state and re-render callback.
builder.Services.AddTransient<NetworkOptimizer.Web.Services.Monitoring.LiveWanScope>();
// Per-user teaching hints that retire once seen (UiHintKeys).
builder.Services.AddScoped<NetworkOptimizer.Web.Services.UiHintService>();
builder.Services.AddSingleton<NetworkOptimizer.Web.Services.Monitoring.AsnResolutionService>();
// Per-site monitoring alert evaluators (target offline / device health / SFP DDM):
// in-memory state machines keyed by target id / MAC, which repeat across sites, so
// each site gets its own bundle. Local collection loops and the agent tunnel sink
// both evaluate through the owning site's instances.
builder.Services.AddSiteScopedRegistry<MonitoringAlertRegistry>();
// (The cable modem, ONT, and cellular alert evaluators are per site via
// MonitoringAlertRegistry.)
// Loads the per-site WAN context (roles, labels, trace map) the WAN outage evaluator
// classifies against; the evaluator instances themselves live in MonitoringAlertRegistry.
builder.Services.AddSingleton<NetworkOptimizer.Web.Services.Monitoring.WanOutageContextSource>();
// Upstream tracer is per site (isolated discovery state in each site's DB, traceroute
// from the site's own vantage). Scoped resolution forwards to the current site's tracer;
// the background re-discovery iterates sites via the registry.
builder.Services.AddSiteScopedRegistry<NetworkOptimizer.Web.Services.Monitoring.UpstreamTracerRegistry>();
builder.Services.AddScoped(sp => sp.GetRequiredService<NetworkOptimizer.Web.Services.Monitoring.UpstreamTracerRegistry>()
    .GetFor(sp.GetRequiredService<SiteContextService>().Slug));
builder.Services.AddMutatingService<IInfluxDbProvisioningService, InfluxDbProvisioningService>();
// The same implementation behind a site-scoped gate, for the one thing a Site Admin should be able
// to finish alone: adding their own site's buckets to a shared connection that already exists.
builder.Services.AddMutatingService<ISiteInfluxProvisioningService>(
    sp => ActivatorUtilities.CreateInstance<InfluxDbProvisioningService>(sp));
// Probe-execution layer: the server-side LocalProbeExecutor is the default vantage. SSH
// vantages (gateway/switch/AP) are constructed per-device via SshProbeExecutor later.
builder.Services.AddSingleton<NetworkOptimizer.Monitoring.Probes.LocalProbeExecutor>();
builder.Services.AddSingleton<NetworkOptimizer.Monitoring.Probes.IProbeExecutor>(
    sp => sp.GetRequiredService<NetworkOptimizer.Monitoring.Probes.LocalProbeExecutor>());
builder.Services.AddScoped<NetworkOptimizer.Web.Services.Monitoring.ProbeExecutorFactory>();
// Read-only gateway interface diagnostics (Network Tools). Scoped because it runs through
// the current site's gateway SSH service.
builder.Services.AddScoped<NetworkOptimizer.Web.Services.Monitoring.GatewayDiagnosticsService>();
builder.Services.AddScoped<NetworkOptimizer.Web.Services.Monitoring.DmesgDiagnosticsService>();
builder.Services.AddMutatingService<ISupportFileService, SupportFileService>();
// Collection agents — drive SNMP polling on the three-tier cadence, write to InfluxDB.
// Idle while monitoring is disabled or unconfigured; activate once both SNMP detection
// succeeds and InfluxDB is reachable. One instance per site, owned by the registry
// (default always runs; non-default sites start/stop on site enable/disable). Scoped
// resolution forwards to the current site's instance so the Setup dashboard reads
// per-device SNMP status for the site it is showing.
builder.Services.AddSiteScopedRegistry<MonitoringCollectionRegistry>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MonitoringCollectionRegistry>());
builder.Services.AddScoped(sp => sp.GetRequiredService<MonitoringCollectionRegistry>()
    .GetFor(sp.GetRequiredService<SiteContextService>().Slug));
// Firmware Rollout executors — the per-device upgrade state machine, canary holds, channel
// group switches and rollout alerts. One instance per site, owned by the registry on the same
// terms as monitoring collection (default always runs; non-default sites start/stop on site
// enable/disable), and its reconcile tick also starts plans whose scheduled time has come.
builder.Services.AddSingleton<NetworkOptimizer.Web.Services.Firmware.RolloutSuppressionRegistry>();
builder.Services.AddSiteScopedRegistry<NetworkOptimizer.Web.Services.Firmware.FirmwareRolloutRegistry>();
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<NetworkOptimizer.Web.Services.Firmware.FirmwareRolloutRegistry>());
// The page's whole surface (settings, preview, controls) goes through the gate. Built per request
// against the site in context: the executor comes from the registry that owns it, and the command
// client is site-pinned the same way the registry pins it for the executor.
builder.Services.AddScoped<NetworkOptimizer.Web.Services.Firmware.IRolloutPlanningSource,
    NetworkOptimizer.Web.Services.Firmware.RolloutPlanningSource>();
builder.Services.AddMutatingService<NetworkOptimizer.Web.Services.Firmware.IFirmwareRolloutService>(sp =>
{
    var slug = sp.GetRequiredService<SiteContextService>().Slug;
    return ActivatorUtilities.CreateInstance<NetworkOptimizer.Web.Services.Firmware.FirmwareRolloutService>(
        sp,
        sp.GetRequiredService<NetworkOptimizer.Web.Services.Firmware.FirmwareRolloutRegistry>().GetFor(slug),
        ActivatorUtilities.CreateInstance<NetworkOptimizer.Web.Services.Firmware.FirmwareCommandClient>(sp, slug));
});
// Re-runs upstream tracer discovery every 7 days; flips a review flag on diff.
builder.Services.AddHostedService<NetworkOptimizer.Web.Services.Monitoring.UpstreamRediscoveryService>();
// 3D LAN flow map (spec 5.7) - composes topology + live + historic feeds for the JS layer.
// Per-site map cache: the registry owns one LanFlowMapCache per site and scoped
// forwarding hands each request the current site's instance, so a secondary
// site's rebuild can no longer overwrite the main site's map snapshot. Service
// is Scoped so it can consume scoped deps.
builder.Services.AddSiteScopedRegistry<NetworkOptimizer.Web.Services.LanFlowMap.LanFlowMapCacheRegistry>();
builder.Services.AddScoped(sp => sp.GetRequiredService<NetworkOptimizer.Web.Services.LanFlowMap.LanFlowMapCacheRegistry>()
    .GetFor(sp.GetRequiredService<SiteContextService>().Slug));
builder.Services.AddScoped<NetworkOptimizer.Web.Services.LanFlowMap.LanFlowMapService>();

// Register application services (scoped per request/circuit)
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<DashboardLayoutService>();
builder.Services.AddMutatingService<IDashboardLayoutAdminService>(
    sp => sp.GetRequiredService<DashboardLayoutService>());
builder.Services.AddScoped<PullToRefreshState>();
builder.Services.AddSingleton<FingerprintDatabaseService>(); // Singleton to cache fingerprint data
builder.Services.AddSingleton<IeeeOuiDatabase>(); // IEEE OUI database for MAC vendor lookup
builder.Services.AddScoped<PdfStorageService>(); // Scoped - namespaces PDF storage by the current site's slug
builder.Services.AddScoped<AuditService>(); // Scoped - uses IMemoryCache for cross-request state
// Running a scan and curating findings are gated separately from the audit read surface.
builder.Services.AddMutatingService<IAuditScanService>(sp => sp.GetRequiredService<AuditService>());
builder.Services.AddScoped<DiagnosticsService>(); // Scoped - network diagnostics (trunk consistency, AP lock, etc.)
// Scoped - reads the gateway's traffic control over SSH for the Smart Queues shaper check
builder.Services.AddScoped<NetworkOptimizer.Web.Services.Ssh.GatewayShaperProbeService>();
// Mutating product services go through the declarative gate (design doc 06, gate 9): the
// interface is proxied by MethodSecurityInterceptor, which authorizes the ambient caller against
// the method's [RequireRole] and writes its [AuditAction] envelope.
builder.Services.AddMutatingService<ISqmService, SqmService>();
builder.Services.AddMutatingService<ISqmDeploymentService, SqmDeploymentService>();
builder.Services.AddMutatingService<IWanSteerDeploymentService, WanSteerDeploymentService>();
builder.Services.AddMutatingService<IWanSteerRuleService, WanSteerRuleService>();
builder.Services.AddMutatingService<ISiteConfigurationService, SiteConfigurationService>();
builder.Services.AddMutatingService<IPerfTweaksDeploymentService, PerfTweaksDeploymentService>();
// The site's stored SSH key. Scoped, so its DbContext is the site in context's.
builder.Services.AddMutatingService<ISshKeyService, SshKeyService>();
// The SSH settings as the edit forms see them. Separate from IGatewaySshService/IUniFiSshService on
// purpose: those are on the connection path, which monitoring calls with no caller established.
builder.Services.AddMutatingService<ISshSettingsAdminService, SshSettingsAdminService>();
// One-click placement of the site's public key on a Cloud Gateway, via the shared udm-boot mechanism.
builder.Services.AddMutatingService<ISshKeyDeploymentService, SshKeyDeploymentService>();
// Per site: the update banner reflects the current site's gateway module deployment
// state. Scoped so each site's Perf Tweaks / WAN Steering status is its own; a circuit
// is session-lived, so the compute still runs about once per session.
builder.Services.AddScoped<ModuleUpdateNotificationService>();
builder.Services.AddMutatingService<IMonitoringInterfaceDeploymentService, MonitoringInterfaceDeploymentService>();
// Curating the site's monitoring configuration from the Monitoring page: the Latency targets card's
// target edits, the Setup tab's enable and alert thresholds, and Upstream path discovery. These ran
// as direct DbContext writes in the components, so none of them were audited.
builder.Services.AddMutatingService<IMonitoringTargetService, MonitoringTargetService>();
builder.Services.AddMutatingService<IMonitoringSettingsService, MonitoringSettingsService>();
builder.Services.AddMutatingService<IUpstreamDiscoveryService, UpstreamDiscoveryService>();

// Register WiFi Optimizer rules and engine
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.IWiFiOptimizerRule, NetworkOptimizer.WiFi.Rules.IoTSsidSeparationRule>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.IWiFiOptimizerRule, NetworkOptimizer.WiFi.Rules.BandSteeringRule>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.IWiFiOptimizerRule, NetworkOptimizer.WiFi.Rules.High2GHzConcentrationRule>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.IWiFiOptimizerRule, NetworkOptimizer.WiFi.Rules.MinRssiRule>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.IWiFiOptimizerRule, NetworkOptimizer.WiFi.Rules.MinRssiEnabledRule>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.IWiFiOptimizerRule, NetworkOptimizer.WiFi.Rules.HighPowerRule>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.IWiFiOptimizerRule, NetworkOptimizer.WiFi.Rules.CoverageGapRule>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.IWiFiOptimizerRule, NetworkOptimizer.WiFi.Rules.WeakSignalPopulationRule>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.IWiFiOptimizerRule, NetworkOptimizer.WiFi.Rules.RoamingAssistantRule>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.IWiFiOptimizerRule, NetworkOptimizer.WiFi.Rules.TxPowerVariationRule>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.IWiFiOptimizerRule, NetworkOptimizer.WiFi.Rules.HighRadioUtilizationRule>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.IWiFiOptimizerRule, NetworkOptimizer.WiFi.Rules.LegacyClientAirtimeRule>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.IWiFiOptimizerRule, NetworkOptimizer.WiFi.Rules.HighTxRetryRule>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.IWiFiOptimizerRule, NetworkOptimizer.WiFi.Rules.MinimumDataRatesRule>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.IWiFiOptimizerRule, NetworkOptimizer.WiFi.Rules.LoadImbalanceRule>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.IWiFiOptimizerRule, NetworkOptimizer.WiFi.Rules.HighApLoadRule>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.IWiFiOptimizerRule, NetworkOptimizer.WiFi.Rules.DhcpIssuesRule>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.IWiFiOptimizerRule, NetworkOptimizer.WiFi.Rules.CoChannelInterferenceRule>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.IWiFiOptimizerRule, NetworkOptimizer.WiFi.Rules.NonStandardChannelRule>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.IWiFiOptimizerRule, NetworkOptimizer.WiFi.Rules.HighPowerOverlapRule>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.IWiFiOptimizerRule, NetworkOptimizer.WiFi.Rules.WideChannelWidthRule>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.WiFiOptimizerEngine>();
builder.Services.AddSingleton<ChannelPlanCache>();
builder.Services.AddScoped<WiFiOptimizerService>();
builder.Services.AddMutatingService<IWiFiScanService>(sp => sp.GetRequiredService<WiFiOptimizerService>());
// Mesh backhaul re-scan (Optimize Mesh button); scoped - forwards to the current
// site's UniFiSshService.
builder.Services.AddMutatingService<IMeshOptimizationService, MeshOptimizationService>();
builder.Services.AddScoped<ApMapService>();
// GetApMapMarkersAsync stays ungated - every map draws AP markers from it, including a Viewer's.
builder.Services.AddMutatingService<IApMapAdminService>(sp => sp.GetRequiredService<ApMapService>());
// Per-site annotations and monitoring setup that used to be written straight from the page and
// the API: the service is the gate (UPnP notes Site Operator; custom OIDs Operator to add, Admin
// to remove, matching the card).
builder.Services.AddScoped<UpnpNoteService>();
builder.Services.AddMutatingService<IUpnpNoteService>(sp => sp.GetRequiredService<UpnpNoteService>());
builder.Services.AddScoped<CustomOidService>();
builder.Services.AddMutatingService<ICustomOidService>(sp => sp.GetRequiredService<CustomOidService>());
// Per-site: buildings, floor plans, planned APs, and their heatmap cache are
// per-site data. Scoped so each site's WiFi optimizer / floor plan / heatmap reads
// its own data (consumers - WiFiOptimizerService, floor-plan endpoints - are scoped).
builder.Services.AddScoped<FloorPlanService>();
builder.Services.AddMutatingService<IFloorPlanAdminService>(sp => sp.GetRequiredService<FloorPlanService>());
builder.Services.AddScoped<HeatmapDataCache>();
builder.Services.AddScoped<PlannedApService>();
builder.Services.AddMutatingService<IPlannedApAdminService>(sp => sp.GetRequiredService<PlannedApService>());
builder.Services.AddSingleton<ConfigTransferService>();
builder.Services.AddMutatingService<IConfigTransferService>(sp => sp.GetRequiredService<ConfigTransferService>());
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Data.AntennaPatternLoader>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Services.PropagationService>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Services.ChannelRecommendationService>();

// Channel recommendation outcome memory: persistent store (factory-based, shared by the
// singleton collector and scoped services) + background collector that attributes UniFi
// radio metrics to the channel config that was live and maintains the change log.
// Channel memory (channel history + neighbor sightings) is per site. The repository is
// scoped so web consumers (WiFiOptimizerService) read the current site's data; the
// collector runs one per-site instance via ChannelMemoryRegistry.
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.IChannelMemoryRepository>(sp =>
    ActivatorUtilities.CreateInstance<NetworkOptimizer.Storage.Repositories.ChannelMemoryRepository>(
        sp,
        sp.GetRequiredService<SiteContextService>().Slug,
        sp.GetRequiredService<SiteContextService>().IsDefault));
builder.Services.AddSiteScopedRegistry<ChannelMemoryRegistry>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChannelMemoryRegistry>());

// Add ApexCharts for Wi-Fi Optimizer visualizations
builder.Services.AddApexCharts();

// Configure HTTP client for API calls
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("TcMonitor", client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});

// CORS for client speed test endpoint (OpenSpeedTest sends results from browser)
// Auto-construct allowed origins from HOST_IP/HOST_NAME, or use CORS_ORIGINS if set
var corsOriginsList = new List<string>();
var corsOriginsConfig = builder.Configuration["CORS_ORIGINS"];

// Add origins from config
if (!string.IsNullOrEmpty(corsOriginsConfig))
{
    corsOriginsList.AddRange(corsOriginsConfig.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

// Auto-add origins from HOST_IP and HOST_NAME (OpenSpeedTest port). The host ladder and ports come
// from OpenSpeedTestSettings, shared with the Client Speed Test and Client Performance pages, so the
// allowed origins and the links those pages hand out can't drift apart. An install without the
// speed test's own origin allowed here has its results silently blocked by CORS.
var openSpeedTest = OpenSpeedTestSettings.Load(builder.Configuration);

// HTTP origins (direct access via IP or hostname) - always added
if (!string.IsNullOrEmpty(openSpeedTest.FallbackIp))
{
    corsOriginsList.Add($"http://{openSpeedTest.FallbackIp}:{openSpeedTest.Port}");
}
if (!string.IsNullOrEmpty(openSpeedTest.Host))
{
    corsOriginsList.Add($"http://{openSpeedTest.Host}:{openSpeedTest.Port}");
}

// HTTPS proxy origin (when OPENSPEEDTEST_HTTPS=true)
if (openSpeedTest.HttpsEnabled && !string.IsNullOrEmpty(openSpeedTest.Host))
{
    corsOriginsList.Add($"https://{NetworkUtilities.ComposeAuthority(openSpeedTest.Host, openSpeedTest.HttpsPort, defaultPort: 443)}");
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("SpeedTestCors", policy =>
    {
        if (corsOriginsList.Count > 0)
        {
            policy.WithOrigins(corsOriginsList.ToArray())
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
        // If no origins configured, CORS is effectively disabled (no origins allowed)
        // Configure HOST_IP or HOST_NAME in .env to enable OpenSpeedTest result reporting
    });
});

// Basic anti-flood limiter for the anonymous public speed-test result endpoints (OpenSpeedTest
// and agent-relayed iperf3). Partitioned by remote address; a real speed test takes ~30s, so a
// generous per-minute cap never affects legitimate use but stops a flood of forged posts.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Credential-verification endpoints. Identity lockout already caps attempts per ACCOUNT (5 in 5
    // minutes), which stops brute force against a known user but does nothing about one source
    // spraying a password across many usernames - and lockout is itself a denial-of-service lever,
    // since anyone can lock a known user out on demand. This caps the source instead.
    //
    // 30 a minute is deliberately generous: a human sign-in is one or two requests, and a whole
    // office behind one NAT address arriving at 9am must not lock itself out. Any spraying tool
    // exceeds it immediately.
    //
    // Partitioned on the remote address, so behind a proxy this collapses to one bucket unless
    // TRUSTED_PROXIES is configured and the real client address is recovered from forwarded headers.
    options.AddPolicy("Authentication", httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    options.AddPolicy("PublicSpeedTest", httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

var app = builder.Build();

// Forwarded headers are OPT-IN, and deliberately so. The common install is a single site reached
// directly on host:8042 with no proxy in front, where trusting X-Forwarded-* would mean trusting
// whatever the client sends. Existing reverse-proxied installs already declare themselves through
// REVERSE_PROXIED_HOST_NAME rather than through headers, so they are unaffected too.
//
// It matters now because a per-tenant hostname selects which tenant a request is FOR. An
// unvalidated forwarded host would therefore be attacker-chosen tenant selection, so the proxies
// allowed to assert one must be named explicitly: TRUSTED_PROXIES=10.0.0.1,10.0.0.0/24
var trustedProxies = builder.Configuration["TRUSTED_PROXIES"];
if (!string.IsNullOrWhiteSpace(trustedProxies))
{
    var options = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
    {
        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
            | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
            | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost,
        // One hop per proxy in the chain. Cloudflare in front of Traefik is TWO, and the default of
        // one would stop unwrapping at Cloudflare's address - so every client would share one
        // rate-limit bucket and appear to come from Cloudflare. Every proxy in the chain must ALSO
        // appear in TRUSTED_PROXIES, or unwrapping stops at the first unrecognised hop.
        ForwardLimit = int.TryParse(builder.Configuration["TRUSTED_PROXY_HOPS"], out var hops) && hops > 0
            ? hops
            : 1,
    };
    // Clear the loopback defaults: the entries below are the whole allowlist.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();

    foreach (var entry in trustedProxies.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        var value = entry.Trim();
        if (value.Contains('/'))
        {
            var parts = value.Split('/');
            if (System.Net.IPAddress.TryParse(parts[0], out var network) && int.TryParse(parts[1], out var prefix))
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(network, prefix));
        }
        else if (System.Net.IPAddress.TryParse(value, out var proxy))
        {
            options.KnownProxies.Add(proxy);
        }
    }

    app.UseForwardedHeaders(options);

    // Cloudflare sets CF-Connecting-IP to the true client address, which is more reliable than
    // walking X-Forwarded-For - it is a single value Cloudflare controls rather than a list any
    // intermediate can append to. Opt-in on top of TRUSTED_PROXIES rather than automatic: the header
    // is only meaningful if the request genuinely came through Cloudflare, and a client posting it
    // directly to an origin that merely has SOME trusted proxy configured would otherwise be
    // choosing its own source address.
    //
    // This still assumes the origin refuses non-Cloudflare connections. If it does not, anyone can
    // point their own Cloudflare account at it and the header becomes attacker-supplied.
    if (bool.TryParse(builder.Configuration["TRUST_CF_CONNECTING_IP"], out var trustCf) && trustCf)
    {
        app.Use(async (context, next) =>
        {
            var header = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
            if (!string.IsNullOrEmpty(header) && System.Net.IPAddress.TryParse(header, out var clientIp))
                context.Connection.RemoteIpAddress = clientIp;
            await next();
        });
        app.Logger.LogInformation("Trusting CF-Connecting-IP for the real client address");
    }

    app.Logger.LogInformation(
        "Trusting forwarded headers from {ProxyCount} proxy address(es) and {NetworkCount} network(s)",
        options.KnownProxies.Count, options.KnownIPNetworks.Count);
}

// Apply database migrations
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
    var conn = db.Database.GetDbConnection();
    conn.Open();
    using var cmd = conn.CreateCommand();

    // Check if database has any tables (existing install) or is brand new
    cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
    var tableCount = Convert.ToInt32(cmd.ExecuteScalar());

    if (tableCount > 0)
    {
        // Existing database - ensure migration history table exists
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (
                MigrationId TEXT PRIMARY KEY,
                ProductVersion TEXT NOT NULL
            )";
        cmd.ExecuteNonQuery();

        // For each migration that created tables which already exist, mark as applied
        // Using INSERT OR IGNORE so this works regardless of current history state
        var migrationsToCheck = new[]
        {
            ("20251208000000_InitialCreate", "AuditResults"),
            ("20251210000000_AddModemAndSpeedTables", "ModemConfigurations"),
            ("20251216000000_AddUniFiSshSettings", "UniFiSshSettings")
        };

        foreach (var (migrationId, tableName) in migrationsToCheck)
        {
            // Check if the table created by this migration exists
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@tableName";
            cmd.Parameters.Clear();
            var tableParam = cmd.CreateParameter();
            tableParam.ParameterName = "@tableName";
            tableParam.Value = tableName;
            cmd.Parameters.Add(tableParam);

            if (cmd.ExecuteScalar() != null)
            {
                // Table exists, mark migration as applied
                cmd.CommandText = "INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@migrationId, '9.0.0')";
                cmd.Parameters.Clear();
                var migrationParam = cmd.CreateParameter();
                migrationParam.ParameterName = "@migrationId";
                migrationParam.Value = migrationId;
                cmd.Parameters.Add(migrationParam);
                cmd.ExecuteNonQuery();
            }
        }
    }

    // Clear stale migration locks left by a previous interrupted startup (#624).
    // At app startup no other process can be migrating this SQLite DB, so any lock is stale.
    cmd.Parameters.Clear();
    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='__EFMigrationsLock'";
    if (cmd.ExecuteScalar() != null)
    {
        cmd.CommandText = "DELETE FROM __EFMigrationsLock";
        var cleared = cmd.ExecuteNonQuery();
        if (cleared > 0)
            app.Logger.LogWarning("Cleared stale migration lock (likely from a previous interrupted startup)");
    }

    conn.Close();

    // Apply any pending migrations (creates DB for new installs, or applies new migrations for existing)
    app.Logger.LogInformation("Applying database migrations...");
    NetworkOptimizer.Storage.MigrationSafety.MigrateWithFriendlyErrors(db);
    app.Logger.LogInformation("Database migrations complete");

    // Migrate every provisioned non-default site database: app upgrades can add
    // migrations after a site DB was provisioned. This scope has no HTTP context,
    // so `db` above is always the main database holding the site registry.
    try
    {
        var sitePaths = scope.ServiceProvider.GetRequiredService<NetworkOptimizer.Storage.Services.SiteDatabasePaths>();
        var siteRows = db.Sites.Where(s => !s.IsDefault).ToList();
        foreach (var site in siteRows)
        {
            var siteDbPath = sitePaths.GetSiteDbPath(site.Slug, isDefault: false);
            if (!File.Exists(siteDbPath))
            {
                app.Logger.LogWarning("Site {Slug} database missing at {Path}, skipping migration", site.Slug, siteDbPath);
                continue;
            }
            var siteOptions = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
                .UseSqlite($"Data Source={siteDbPath}")
                .Options;
            using var siteDb = new NetworkOptimizerDbContext(siteOptions);
            NetworkOptimizer.Storage.MigrationSafety.MigrateWithFriendlyErrors(siteDb);

            // Seed the Alerts & Schedule defaults into each site's DB too, so secondary
            // sites match the main site instead of showing blank lists. The main-DB seed
            // below only covers the default site.
            var siteSeededPatterns = StartupHelpers.SeedAlertRules(
                siteDb, NetworkOptimizer.Alerts.DefaultAlertRules.GetDefaults());
            if (siteSeededPatterns.Count > 0)
            {
                app.Logger.LogInformation("Seeded {Count} alert rule(s) for site {Slug}", siteSeededPatterns.Count, site.Slug);

                // Enable any freshly seeded modem/ONT rules for a secondary site that already
                // has the matching monitoring configured (mirrors the main-site seed below,
                // which secondary sites otherwise never got - a new ONT rule landed disabled).
                //
                // Same one-time Device Offline enable as the main site, so managed sites match.
                AlertRuleAutoEnable.EnableNowThatItHasAPublisher(
                    siteDb, "device.offline", "device.recovered", siteSeededPatterns, app.Logger);

                AlertRuleAutoEnable.EnableFreshlySeeded(siteDb, "cable_modem", siteSeededPatterns, () => siteDb.CmConfigurations.Any());
                AlertRuleAutoEnable.EnableFreshlySeeded(siteDb, "ont", siteSeededPatterns, () => siteDb.OntConfigurations.Any());
                AlertRuleAutoEnable.EnableFreshlySeeded(siteDb, "cellular", siteSeededPatterns, () => siteDb.ModemConfigurations.Any());
                AlertRuleAutoEnable.EnableFreshlySeeded(siteDb, "starlink", siteSeededPatterns, () => siteDb.StarlinkConfigurations.Any());
            }

            if (NetworkOptimizer.Core.FeatureFlags.SchedulingEnabled && !siteDb.ScheduledTasks.Any())
            {
                siteDb.ScheduledTasks.Add(new NetworkOptimizer.Alerts.Models.ScheduledTask
                {
                    TaskType = "audit",
                    Name = "Security Audit",
                    Enabled = true,
                    FrequencyMinutes = 720, // 12 hours
                    NextRunAt = NetworkOptimizer.Alerts.ScheduleService.CalculateNextRun(720),
                    CreatedAt = DateTime.UtcNow
                });
                siteDb.SaveChanges();
                app.Logger.LogInformation("Seeded default scheduled tasks for site {Slug}", site.Slug);
            }
        }
        if (siteRows.Count > 0)
            app.Logger.LogInformation("Applied migrations to {Count} site database(s)", siteRows.Count);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Failed to migrate site databases");
    }

    // FUSE/network filesystems (Unraid shfs, mergerfs, NFS, SMB) don't support the shared-memory
    // mmap that WAL mode requires, causing silent database corruption. Use DELETE mode instead.
    var (isFuseFs, detectedFsType) = StartupHelpers.DetectFilesystem(dbPath);
    if (isFuseFs)
    {
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=DELETE;");
        app.Logger.LogWarning(
            "FUSE/network filesystem detected ({FilesystemType}) - using DELETE journal mode to prevent database corruption. " +
            "To use WAL mode (better performance), store the database on a direct filesystem " +
            "(e.g., /mnt/cache instead of /mnt/user on Unraid)", detectedFsType);
    }
    else
    {
        // Ensure WAL mode - config imports replace the DB with a DELETE-mode copy
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        app.Logger.LogInformation("Database journal mode: WAL (filesystem: {FilesystemType})", detectedFsType);
    }

    // Seed default alert rules - insert any rule this database has never been seeded before
    {
        var defaults = NetworkOptimizer.Alerts.DefaultAlertRules.GetDefaults();

        // On-Site Agent rules only make sense on the main site when the default site
        // itself has an agent (secondary sites get theirs from the per-site seed above).
        // Skipping them keeps a single-site install's Rules list free of agent entries;
        // enrolling a default-site agent later seeds them at that point.
        var defaultSiteId = db.Sites
            .Where(s => s.Slug == SiteManagementService.DefaultSiteSlug)
            .Select(s => (int?)s.Id)
            .FirstOrDefault();
        if (defaultSiteId == null || !db.SiteAgents.Any(a => a.SiteId == defaultSiteId))
            defaults = defaults.Where(d => d.Source != "agent").ToList();

        var seededPatterns = StartupHelpers.SeedAlertRules(db, defaults);
        if (seededPatterns.Count > 0)
            app.Logger.LogInformation("Seeded {Count} new alert rules", seededPatterns.Count);

        // Auto-enable freshly seeded modem/ONT rules for users who already have
        // configs. Only touches rules we just inserted - never re-enables rules
        // the user has manually disabled.
        if (seededPatterns.Count > 0)
        {
            // Device Offline shipped disabled because nothing published device.offline until this
            // release. Enable that ONE rule as its publisher lands - keyed off the paired
            // device.recovered rule arriving, so it happens once and overrides no later choice.
            AlertRuleAutoEnable.EnableNowThatItHasAPublisher(
                db, "device.offline", "device.recovered", seededPatterns, app.Logger);

            AlertRuleAutoEnable.EnableFreshlySeeded(db, "cable_modem", seededPatterns, () => db.CmConfigurations.Any());
            AlertRuleAutoEnable.EnableFreshlySeeded(db, "ont", seededPatterns, () => db.OntConfigurations.Any());
            AlertRuleAutoEnable.EnableFreshlySeeded(db, "cellular", seededPatterns, () => db.ModemConfigurations.Any());
            AlertRuleAutoEnable.EnableFreshlySeeded(db, "starlink", seededPatterns, () => db.StarlinkConfigurations.Any());
        }
    }

    // Seed default scheduled tasks if none exist
    if (NetworkOptimizer.Core.FeatureFlags.SchedulingEnabled && !db.ScheduledTasks.Any())
    {
        db.ScheduledTasks.Add(new NetworkOptimizer.Alerts.Models.ScheduledTask
        {
            TaskType = "audit",
            Name = "Security Audit",
            Enabled = true,
            FrequencyMinutes = 720, // 12 hours
            NextRunAt = NetworkOptimizer.Alerts.ScheduleService.CalculateNextRun(720), // Clean minute boundary, no immediate fire
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
        app.Logger.LogInformation("Seeded default scheduled tasks");
    }
}

// Load external speed test server origins into CORS cache
{
    var sysSettings = app.Services.GetRequiredService<SystemSettingsService>();
    var servers = await sysSettings.GetExternalSpeedTestServersAsync();
    sysSettings.UpdateCachedExternalOrigins(servers);
    var configured = servers.Where(s => s.IsConfigured).ToList();
    if (configured.Count > 0)
    {
        app.Logger.LogInformation("External speed test servers configured: {Count} ({Urls})",
            configured.Count, string.Join(", ", configured.Select(s => s.Url)));
    }
}

// Pre-generate the credential encryption key (resolves singleton, triggering key creation)
app.Services.GetRequiredService<NetworkOptimizer.Storage.Services.ICredentialProtectionService>().EnsureKeyExists();

// Initialize GeoLite2 enrichment (looks for .mmdb files in data directory)
var geoDataPath = Path.GetDirectoryName(dbPath)!;
app.Services.GetRequiredService<NetworkOptimizer.Threats.Enrichment.GeoEnrichmentService>().Initialize(geoDataPath);

// Load CrowdSec daily quota from settings
{
    var sysSettings = app.Services.GetRequiredService<ISystemSettingsService>();
    var csQuota = await sysSettings.GetGlobalAsync("crowdsec.daily_quota");
    var dailyLimit = 30;
    if (!string.IsNullOrEmpty(csQuota) && int.TryParse(csQuota, out var q) && q >= 1)
        dailyLimit = q;
    app.Services.GetRequiredService<NetworkOptimizer.Threats.CrowdSec.CrowdSecClient>()
        .LoadRateLimitState(0, DateOnly.FromDateTime(DateTime.UtcNow), dailyLimit);
}

// Register schedule executor delegates (bridges Alerts project to Web project services)
app.RegisterScheduleExecutors();

// Clean up any leftover config transfer temp files from previous sessions
app.Services.GetRequiredService<ConfigTransferService>().CleanupTempFiles();

// Device monitor poll timers (cable modem, ONT, cellular) start at app launch
// via the ModemMonitorRegistry hosted service - no eager resolution needed.

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

// Host enforcement: redirect to canonical host if configured
// Only REVERSE_PROXIED_HOST_NAME or HOST_NAME trigger redirects
// HOST_IP alone does NOT redirect (allows users to access via any hostname)
// The two tiers, and the HOST_IP exclusion, now live in CanonicalBaseUrlProvider so that anything
// needing this install's external address agrees with this redirect - OIDC's redirect_uri being the
// case that found the gap.
var canonicalBase = app.Services.GetRequiredService<CanonicalBaseUrlProvider>();
var canonicalHost = canonicalBase.Url is null ? null : new Uri(canonicalBase.Url).Host;

if (!string.IsNullOrEmpty(canonicalHost))
{
    app.Use(async (context, next) =>
    {
        // Machine-to-machine requests that cannot or must not be redirected. Which ones, and why
        // each earns it, is documented on the predicate.
        if (CanonicalBaseUrlProvider.ShouldBypassRedirect(context.Request))
        {
            await next();
            return;
        }

        var requestHost = context.Request.Host.Host;

        // Check if host matches (case-insensitive)
        if (!string.Equals(requestHost, canonicalHost, StringComparison.OrdinalIgnoreCase))
        {
            // Build redirect URL
            var redirectUrl = $"{canonicalBase.Url}{context.Request.Path}{context.Request.QueryString}";

            // 302 redirect (not 301 to avoid browser caching)
            context.Response.Redirect(redirectUrl, permanent: false);
            return;
        }

        await next();
    });
}

// Only use HTTPS redirection if not in Docker/container (check for DOTNET_RUNNING_IN_CONTAINER)
if (!string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
{
    app.UseHttpsRedirection();
    app.UseHsts();
}

// Initialize IEEE OUI database (downloads from IEEE on first startup, then caches)
var ieeeOuiDb = app.Services.GetRequiredService<IeeeOuiDatabase>();
await ieeeOuiDb.InitializeAsync();

// Warm the agent-coverage flags before collection starts, so no synchronous gate answers
// "not covered" for a site that is while the cache fills.
await app.Services.GetRequiredService<SiteAgentCoverage>().WarmAsync();

// Log admin auth startup configuration
using (var startupScope = app.Services.CreateScope())
{
    var adminAuthService = startupScope.ServiceProvider.GetRequiredService<IAdminAuthService>();
    await adminAuthService.LogStartupConfigurationAsync();

    // Apply the auth schema and seed/reconcile the local admin account from the install's current
    // credential (runs after the first-run auto-generated password exists to transcode). Additive:
    // the JWT session artifact is unchanged until the cutover wires cookie auth in.
    var identityBootstrap = startupScope.ServiceProvider.GetRequiredService<IIdentityBootstrapService>();
    await identityBootstrap.RunAsync();

    // Upsert IaC-declared federation providers (mounted JSON + env), then register their schemes.
    await startupScope.ServiceProvider.GetRequiredService<IIdentityConfigLoader>().ApplyAsync();
}

// Register authentication schemes for any enabled OIDC federation providers (runtime-dynamic).
await app.Services.GetRequiredService<DynamicSchemeManager>().SyncAsync();

// Standard ASP.NET Core authentication middleware (must come before auth check).
// Authentication is now the Identity application cookie (see AddNetOptIdentityAuthentication).
app.UseAuthentication();

// Transitional bridge: upgrade a still-valid legacy auth_token JWT into an Identity cookie so
// sessions from before the upgrade are not forced to re-login. Runs after UseAuthentication (so an
// existing Identity cookie short-circuits it) and before authorization/the auth-required gate.
// SUNSET: remove with JwtService one release after the cutover (30-day legacy token lifetime).
app.UseMiddleware<NetworkOptimizer.Web.Services.Identity.LegacyJwtBridgeMiddleware>();

// Populate the ambient caller context (actor/IP/UA/correlation) for HTTP requests, after auth so the
// principal is resolved. Circuit calls are populated separately by CallerContextCircuitHandler.
app.UseMiddleware<NetworkOptimizer.Web.Services.Identity.CallerContextMiddleware>();

// Auth middleware that checks if authentication is required and protects all endpoints
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLower() ?? "";

    // Only these paths are public (no auth required)
    // "/error" is public so the exception handler's re-execute can actually render: if it fell to the
    // gate below, every unhandled exception would redirect to /login with no message.
    var publicPaths = new[] { "/login", "/login/2fa", "/error", "/api/auth/login", "/api/auth/2fa", "/api/auth/logout", "/api/health", "/api/passkey/request-options", "/api/passkey/assert" };
    // /api/public/*, plus the federation login/callback surface (OIDC challenge, IdP callbacks, SAML ACS).
    var publicPrefixes = new[] { "/api/public/", "/login/external", "/login/saml/", "/signin-oidc/", "/signout-oidc/", "/signout-callback-oidc/", "/saml/" };
    var staticPaths = new[] { "/_blazor", "/_framework", "/css", "/js", "/images", "/_content", "/downloads" };

    // Allow public endpoints
    if (publicPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)))
    {
        await next();
        return;
    }

    // Allow public API prefixes (e.g., /api/public/*)
    if (publicPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
    {
        await next();
        return;
    }

    // Allow static files and Blazor framework
    if (staticPaths.Any(p => path.StartsWith(p)) || (path.Contains('.') && !path.EndsWith(".razor")))
    {
        await next();
        return;
    }

    // Check if authentication is required (admin may have disabled it)
    var adminAuth = context.RequestServices.GetRequiredService<IAdminAuthService>();
    var isAuthRequired = await adminAuth.IsAuthenticationRequiredAsync();

    if (!isAuthRequired)
    {
        await next();
        return;
    }

    // If auth is required but user is not authenticated
    if (context.User.Identity?.IsAuthenticated != true)
    {
        // API endpoints return 401
        if (path.StartsWith("/api/"))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
            return;
        }

        // Web pages redirect to login. Carry the tab's ?site= pin through so the
        // post-login redirect lands back on the site the tab was on.
        var loginSite = context.Request.Query[SiteContextService.SiteQueryParam].ToString();
        context.Response.Redirect(string.IsNullOrEmpty(loginSite)
            ? "/login"
            : $"/login?{SiteContextService.SiteQueryParam}={Uri.EscapeDataString(loginSite)}");
        return;
    }

    await next();
});

// Endpoint/page authorization runs after the auth-required gate above, so an anonymous user on an
// auth-enabled install still gets that gate's redirect to /login (carrying the tab's ?site= pin)
// instead of a bare 401 from a policy failure. With authentication disabled the policies short-
// circuit to success (GlobalRoleHandler), so nothing changes for those installs.
app.UseAuthorization();

// API endpoints carry authorization metadata; the gated service behind them is what actually
// decides, and it refuses by throwing. Without this that lands as a 500 - the caller was told
// nothing, and the log reads like a fault rather than a refusal. Blazor has the same translation
// in GateRefusalBoundary.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (NetworkOptimizer.Web.Services.Gates.AuthorizationDeniedException)
        when (context.Request.Path.StartsWithSegments("/api") && !context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "You do not have permission to do that on this site." });
    }
});


// Site selection via ?site=<slug> is per browser tab: it wins over the site cookie on
// every request (SiteContextService.Resolve), the circuit pins itself from the tab URL
// (Routes.razor), and SiteTabSync keeps the selector in the address bar. It is never
// persisted to the cookie - following an alert "View" link pins only that tab, and the
// cookie remains the browser default written solely by an explicit switch in the UI.

// Configure static files with custom MIME types for package downloads
var contentTypeProvider = new FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".ipk"] = "application/octet-stream";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypeProvider
});
app.UseAntiforgery();
app.UseCors(); // Required for OpenSpeedTest to POST results
app.UseRateLimiter(); // Enforces the PublicSpeedTest policy on the anonymous result endpoints

// Dynamic CORS for external speed test servers (configured via Settings UI, not env vars)
// Adds Access-Control-Allow-Origin for the external server origin on public speed test endpoints
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (path.StartsWith("/api/public/speedtest/", StringComparison.OrdinalIgnoreCase))
    {
        var origin = context.Request.Headers.Origin.FirstOrDefault();
        if (!string.IsNullOrEmpty(origin))
        {
            var sysSettings = context.RequestServices.GetRequiredService<SystemSettingsService>();
            if (sysSettings.IsExternalSpeedTestOrigin(origin))
            {
                context.Response.Headers["Access-Control-Allow-Origin"] = origin;
                context.Response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
                context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";

                if (context.Request.Method == "OPTIONS")
                {
                    context.Response.StatusCode = 204;
                    return;
                }
            }
        }
    }
    await next();
});

// Narrows the request's site to one the caller may actually see. Placed after UseStaticFiles so
// asset requests never pay for it, and before the components/endpoints that read the site context.
app.UseMiddleware<NetworkOptimizer.Web.Services.Authorization.SiteAccessMiddleware>();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Every minimal-API endpoint is mapped in one place so architecture test A1 can walk the whole
// surface and prove each endpoint is authorized or explicitly public (design doc 06, gate 2/3).
ApiEndpoints.MapAll(app);

// Agent tunnel (gRPC). Mapped unconditionally; it is only reachable when the
// dedicated HTTP/2 listener is bound (multi-site enabled at startup). It authenticates with the
// agent enrollment-token/agent-key scheme, separate from user identity (design doc 06, gate 11).
app.MapGrpcService<AgentTunnelService>();

app.Run();

// Helper function to load configuration from Windows Registry (set by MSI installer)
// Returns empty collection on non-Windows or if registry key doesn't exist
static Dictionary<string, string?> LoadWindowsRegistrySettings()
{
    if (!OperatingSystem.IsWindows())
        return [];

    var settings = new Dictionary<string, string?>();

    try
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Ozark Connect\Network Optimizer");
        if (key == null)
            return [];

        // Map registry keys to configuration paths
        // Some keys map directly, others need to be transformed to match .NET configuration format
        var keyMappings = new Dictionary<string, string>
        {
            ["HOST_IP"] = "HOST_IP",
            ["HOST_NAME"] = "HOST_NAME",
            ["REVERSE_PROXIED_HOST_NAME"] = "REVERSE_PROXIED_HOST_NAME",
            ["REVERSE_PROXIED_PORT"] = "REVERSE_PROXIED_PORT",
            ["IPERF3_SERVER_ENABLED"] = "Iperf3Server:Enabled",  // Maps to Iperf3Server:Enabled
            ["OPENSPEEDTEST_PORT"] = "OPENSPEEDTEST_PORT",
            ["OPENSPEEDTEST_HOST"] = "OPENSPEEDTEST_HOST",
            ["OPENSPEEDTEST_HTTPS"] = "OPENSPEEDTEST_HTTPS",
            ["OPENSPEEDTEST_HTTPS_PORT"] = "OPENSPEEDTEST_HTTPS_PORT",
            // Traefik settings (optional HTTPS reverse proxy feature)
            ["TRAEFIK_ACME_EMAIL"] = "TRAEFIK_ACME_EMAIL",
            ["TRAEFIK_CF_DNS_API_TOKEN"] = "TRAEFIK_CF_DNS_API_TOKEN",
            ["TRAEFIK_OPTIMIZER_HOSTNAME"] = "TRAEFIK_OPTIMIZER_HOSTNAME",
            ["TRAEFIK_SPEEDTEST_HOSTNAME"] = "TRAEFIK_SPEEDTEST_HOSTNAME",
            ["TRAEFIK_LISTEN_IP"] = "TRAEFIK_LISTEN_IP",
            ["TRAEFIK_LOG_LEVEL"] = "TRAEFIK_LOG_LEVEL"
        };

        foreach (var mapping in keyMappings)
        {
            var value = key.GetValue(mapping.Key) as string;
            if (!string.IsNullOrEmpty(value))
            {
                settings[mapping.Value] = value;
            }
        }
    }
    catch
    {
        // Silently ignore registry access errors (permissions, etc.)
    }

    return settings;
}




// Request DTO for UPnP notes
record UpnpNoteRequest(string HostIp, string Port, string Protocol, string? Note);

// Request DTO for AP location upsert
record ApLocationRequest(double Latitude, double Longitude, int? Floor = 1);

// Request DTOs for building/floor plan API
record BuildingRequest(string Name, double CenterLatitude, double CenterLongitude);
record FloorRequest(int FloorNumber, string Label, double SwLatitude, double SwLongitude, double NeLatitude, double NeLongitude);
record FloorUpdateRequest(double? SwLatitude = null, double? SwLongitude = null, double? NeLatitude = null,
    double? NeLongitude = null, double? Opacity = null, string? WallsJson = null, string? Label = null,
    string? FloorMaterial = null);
record FloorImageUpdateRequest(double? SwLatitude = null, double? SwLongitude = null, double? NeLatitude = null,
    double? NeLongitude = null, double? Opacity = null, double? RotationDeg = null, string? CropJson = null,
    string? Label = null);

// Adapter to bridge ISecretDecryptor (Alerts project) to ICredentialProtectionService (Storage project)
class SecretDecryptorAdapter(NetworkOptimizer.Storage.Services.ICredentialProtectionService inner) : NetworkOptimizer.Alerts.Delivery.ISecretDecryptor
{
    public string Decrypt(string encrypted) => inner.Decrypt(encrypted);
    public string Encrypt(string plaintext) => inner.Encrypt(plaintext);
}

// Adapter to bridge IDigestStateStore (Alerts project) to SystemSettings (Storage project)
class DigestStateStoreAdapter(NetworkOptimizer.Storage.Interfaces.ISettingsRepository settings) : NetworkOptimizer.Alerts.Interfaces.IDigestStateStore
{
    private static string Key(int channelId) => $"digest.last_sent.{channelId}";

    public async Task<DateTime?> GetLastSentAsync(int channelId, CancellationToken cancellationToken)
    {
        var value = await settings.GetSystemSettingAsync(Key(channelId), cancellationToken);
        return value != null && DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : null;
    }

    public async Task SetLastSentAsync(int channelId, DateTime sentAt, CancellationToken cancellationToken)
    {
        await settings.SaveSystemSettingAsync(Key(channelId), sentAt.ToString("O"), cancellationToken);
    }
}

static partial class StartupHelpers
{
    /// <summary>
    /// Resolves the plain-HTTP bindings the app would use today from
    /// ASPNETCORE_URLS / ASPNETCORE_HTTP_PORTS (default http://*:8042), so the
    /// agent tunnel listener can re-bind them explicitly alongside its own
    /// HTTP/2 port. Returns null when the configuration contains anything we
    /// cannot faithfully reproduce with Kestrel Listen calls (HTTPS, unix
    /// sockets, malformed entries) - callers then leave binding untouched.
    /// </summary>
    internal static List<(string Host, int Port)>? ResolveHttpBindings()
    {
        var bindings = new List<(string Host, int Port)>();
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        var httpPorts = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");

        if (!string.IsNullOrWhiteSpace(urls))
        {
            foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    return null;
                var hostPort = url["http://".Length..].TrimEnd('/');
                var colon = hostPort.LastIndexOf(':');
                if (colon < 0 || !int.TryParse(hostPort[(colon + 1)..], out var port))
                    return null;
                var host = hostPort[..colon];
                bindings.Add((host is "*" or "+" or "0.0.0.0" ? "*" : host, port));
            }
        }
        else if (!string.IsNullOrWhiteSpace(httpPorts))
        {
            foreach (var part in httpPorts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(part, out var port))
                    return null;
                bindings.Add(("*", port));
            }
        }
        else
        {
            bindings.Add(("*", 8042));
        }

        return bindings.Count > 0 ? bindings : null;
    }

    /// <summary>
    /// Builds an ephemeral self-signed certificate for the agent-tunnel TLS
    /// listener. The tunnel port sits behind the reverse proxy fronting the
    /// server, which is configured to skip verification, so this cert only
    /// provides transport encryption for the proxy-to-app hop - it is never
    /// validated, never persisted, and regenerated on every start. Encrypting
    /// that hop matters when the proxy is a separate box: the agent enrollment
    /// key and the SNMP credentials pushed over the tunnel would otherwise
    /// traverse the LAN in cleartext. On Linux the freshly created cert holds an
    /// ephemeral key handle Kestrel cannot use directly, so it is round-tripped
    /// through PKCS#12 to bind the private key to the returned instance.
    /// </summary>
    internal static System.Security.Cryptography.X509Certificates.X509Certificate2 CreateSelfSignedTunnelCert()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=networkoptimizer-agent-tunnel",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
                new System.Security.Cryptography.OidCollection
                {
                    new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"), // serverAuth
                },
                false));

        var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(100));

        return System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx),
            password: null);
    }

    /// <summary>
    /// Inserts the default alert rules a database has never been given, and records every default
    /// pattern it holds in SeededAlertRules so each one is seeded at most once per database. The
    /// record is what makes a deletion stick: seeding used to key off AlertRules alone, so a rule
    /// the user deleted (or a whole source they cleared out) came straight back on the next start.
    ///
    /// Patterns already present in AlertRules but not yet recorded are backfilled, including when
    /// nothing was inserted, so existing installs stop resurrecting rules from here on.
    /// <paramref name="defaults"/> is the caller's already-filtered list and is the only source of
    /// recorded patterns - a default held back because the site lacks its capability (agent rules)
    /// stays unrecorded and can still seed once that capability arrives.
    /// </summary>
    /// <param name="db">Database to seed (main or a site's).</param>
    /// <param name="defaults">Default rules this database should have.</param>
    /// <returns>The patterns inserted by this pass, for the auto-enable helpers to act on.</returns>
    internal static HashSet<string> SeedAlertRules(
        NetworkOptimizerDbContext db, List<NetworkOptimizer.Alerts.Models.AlertRule> defaults)
    {
        var existingPatterns = db.AlertRules.Select(r => r.EventTypePattern).ToHashSet();
        var recordedPatterns = db.SeededAlertRules.Select(s => s.EventTypePattern).ToHashSet();

        var missing = defaults
            .Where(d => !existingPatterns.Contains(d.EventTypePattern) && !recordedPatterns.Contains(d.EventTypePattern))
            .ToList();
        if (missing.Count > 0)
        {
            db.AlertRules.AddRange(missing);
            db.SaveChanges();
        }

        var seededPatterns = missing.Select(m => m.EventTypePattern).ToHashSet();

        var toRecord = defaults
            .Select(d => d.EventTypePattern)
            .Distinct()
            .Where(p => !recordedPatterns.Contains(p) && (existingPatterns.Contains(p) || seededPatterns.Contains(p)))
            .ToList();
        if (toRecord.Count > 0)
        {
            db.SeededAlertRules.AddRange(toRecord.Select(p => new NetworkOptimizer.Alerts.Models.SeededAlertRule
            {
                EventTypePattern = p
            }));
            db.SaveChanges();
        }

        return seededPatterns;
    }

    internal static (bool isFuse, string filesystemType) DetectFilesystem(string filePath)
    {
        if (!OperatingSystem.IsLinux())
            return (false, "n/a");

        try
        {
            var resolvedPath = Path.GetFullPath(filePath);
            var bestMatch = string.Empty;
            var bestFsType = string.Empty;

            foreach (var line in File.ReadLines("/proc/mounts"))
            {
                var parts = line.Split(' ');
                if (parts.Length < 3) continue;

                var mountPoint = parts[1];
                if (mountPoint.Length > bestMatch.Length
                    && resolvedPath.StartsWith(mountPoint, StringComparison.Ordinal)
                    && (mountPoint == "/" || resolvedPath.Length == mountPoint.Length || resolvedPath[mountPoint.Length] == '/'))
                {
                    bestMatch = mountPoint;
                    bestFsType = parts[2];
                }
            }

            if (string.IsNullOrEmpty(bestFsType))
                return (false, "unknown");

            var isFuse = bestFsType.StartsWith("fuse", StringComparison.OrdinalIgnoreCase)
                || bestFsType.Equals("nfs", StringComparison.OrdinalIgnoreCase)
                || bestFsType.Equals("nfs4", StringComparison.OrdinalIgnoreCase)
                || bestFsType.Equals("cifs", StringComparison.OrdinalIgnoreCase)
                || bestFsType.Equals("smb", StringComparison.OrdinalIgnoreCase)
                || bestFsType.Equals("9p", StringComparison.OrdinalIgnoreCase);

            return (isFuse, bestFsType);
        }
        catch
        {
            return (false, "unknown");
        }
    }
}
