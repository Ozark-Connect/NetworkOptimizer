using ApexCharts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Audit;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web;
using NetworkOptimizer.Web.Endpoints;
using NetworkOptimizer.Web.Services;
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
        builder.WebHost.UseUrls("http://0.0.0.0:8042");
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

// Register UniFi connection service (singleton - maintains connection state)
builder.Services.AddSingleton<UniFiConnectionService>();
builder.Services.AddSingleton<IUniFiClientProvider>(sp => sp.GetRequiredService<UniFiConnectionService>());

// Register Network Path Analyzer (singleton - uses caching)
builder.Services.AddSingleton<INetworkPathAnalyzer, NetworkPathAnalyzer>();

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
builder.Services.AddDbContext<NetworkOptimizerDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}")
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

// Register repository pattern (scoped - same lifetime as DbContext)
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.IAuditRepository, NetworkOptimizer.Storage.Repositories.AuditRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.ISettingsRepository, NetworkOptimizer.Storage.Repositories.SettingsRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.IUniFiRepository, NetworkOptimizer.Storage.Repositories.UniFiRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.IModemRepository, NetworkOptimizer.Storage.Repositories.ModemRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.ISpeedTestRepository, NetworkOptimizer.Storage.Repositories.SpeedTestRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.ISqmRepository, NetworkOptimizer.Storage.Repositories.SqmRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.IAgentRepository, NetworkOptimizer.Storage.Repositories.AgentRepository>();
builder.Services.AddScoped<NetworkOptimizer.Alerts.Interfaces.IAlertRepository, NetworkOptimizer.Storage.Repositories.AlertRepository>();

// Register SSH client service (singleton - cross-platform SSH.NET wrapper)
builder.Services.AddSingleton<SshClientService>();

// Register Gateway SSH service (singleton - SSH access to UniFi gateway/UDM)
builder.Services.AddSingleton<IGatewaySshService, GatewaySshService>();

// Register UniFi SSH service (singleton - shared SSH credentials for all UniFi devices)
builder.Services.AddSingleton<UniFiSshService>();

// Register Cellular Modem service (singleton - maintains polling timer, uses UniFiSshService)
builder.Services.AddSingleton<CellularModemService>();

// Register iperf3 Speed Test service (singleton - tracks running tests, uses UniFiSshService)
builder.Services.AddSingleton<Iperf3SpeedTestService>();

// Register Gateway Speed Test service (singleton - gateway iperf3 tests with separate SSH creds)
builder.Services.AddSingleton<GatewaySpeedTestService>();

// Register Client Speed Test service (singleton - receives browser/iperf3 client results)
builder.Services.AddSingleton<ClientSpeedTestService>();

// Register Client Dashboard service (singleton - signal polling, trace tracking)
builder.Services.AddSingleton<ClientDashboardService>();

// Register WAN Speed Test services (singletons - server-side and gateway-direct WAN speed tests)
builder.Services.AddSingleton<CloudflareSpeedTestService>();
builder.Services.AddSingleton<UwnSpeedTestService>();

// Register Gateway WAN Speed Test service (singleton - gateway-direct WAN speed tests via SSH)
builder.Services.AddSingleton<GatewayWanSpeedTestService>();

// Register Topology Snapshot service (singleton - captures wireless rate snapshots during speed tests)
builder.Services.AddSingleton<TopologySnapshotService>();
builder.Services.AddSingleton<ITopologySnapshotService>(sp => sp.GetRequiredService<TopologySnapshotService>());

// Register iperf3 Server service (hosted - runs iperf3 in server mode, monitors for client tests)
// Enable via environment variable: Iperf3Server__Enabled=true
// Registered as singleton so it can be injected to check status (e.g., startup failure)
builder.Services.AddSingleton<Iperf3ServerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Iperf3ServerService>());

// Register nginx hosted service (Windows only - manages nginx for OpenSpeedTest)
builder.Services.AddHostedService<NginxHostedService>();

// Register Alert Engine services (Vigilance)
builder.Services.AddSingleton<NetworkOptimizer.Alerts.Events.IAlertEventBus, NetworkOptimizer.Alerts.Events.AlertEventBus>();
builder.Services.AddSingleton<NetworkOptimizer.Alerts.AlertCooldownTracker>();
builder.Services.AddSingleton<NetworkOptimizer.Alerts.AlertRuleEvaluator>();
builder.Services.AddSingleton<NetworkOptimizer.Alerts.AlertCorrelationService>();
builder.Services.AddSingleton<NetworkOptimizer.Alerts.AlertProcessingService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NetworkOptimizer.Alerts.AlertProcessingService>());
builder.Services.AddSingleton<NetworkOptimizer.Alerts.DigestService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NetworkOptimizer.Alerts.DigestService>());
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

// Register Threat Intelligence services
builder.Services.AddSingleton<NetworkOptimizer.Threats.Enrichment.GeoEnrichmentService>();
builder.Services.AddSingleton<NetworkOptimizer.Threats.CrowdSec.CrowdSecClient>();
builder.Services.AddSingleton<NetworkOptimizer.Threats.CrowdSec.CrowdSecEnrichmentService>();
builder.Services.AddSingleton<NetworkOptimizer.Threats.ThreatEventNormalizer>();
builder.Services.AddSingleton<NetworkOptimizer.Threats.Analysis.KillChainClassifier>();
builder.Services.AddSingleton<NetworkOptimizer.Threats.Analysis.ScanSweepDetector>();
builder.Services.AddSingleton<NetworkOptimizer.Threats.Analysis.BruteForceDetector>();
builder.Services.AddSingleton<NetworkOptimizer.Threats.Analysis.ExploitCampaignDetector>();
builder.Services.AddSingleton<NetworkOptimizer.Threats.Analysis.DDoSDetector>();
builder.Services.AddSingleton<NetworkOptimizer.Threats.Analysis.ThreatPatternAnalyzer>();
builder.Services.AddSingleton<NetworkOptimizer.Threats.Analysis.ExposureValidator>();
builder.Services.AddSingleton<NetworkOptimizer.Threats.ThreatCollectionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NetworkOptimizer.Threats.ThreatCollectionService>());
builder.Services.AddScoped<NetworkOptimizer.Threats.Interfaces.IThreatRepository, NetworkOptimizer.Storage.Repositories.ThreatRepository>();
builder.Services.AddScoped<NetworkOptimizer.Web.Services.ThreatDashboardService>();
builder.Services.AddScoped<NetworkOptimizer.Threats.Interfaces.IThreatSettingsAccessor, NetworkOptimizer.Web.Services.ThreatSettingsAccessor>();
builder.Services.AddSingleton<NetworkOptimizer.Threats.Interfaces.IUniFiClientAccessor, NetworkOptimizer.Web.Services.UniFiClientAccessor>();

// Register System Settings service (singleton - system-wide configuration)
builder.Services.AddSingleton<SystemSettingsService>();
builder.Services.AddSingleton<ISystemSettingsService>(sp => sp.GetRequiredService<SystemSettingsService>());

// Register Sponsorship service (singleton - reads from DB, limited state)
builder.Services.AddSingleton<ISponsorshipService, SponsorshipService>();

// Register password hasher (singleton - stateless)
builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();

// Register Admin Auth service (scoped - depends on ISettingsRepository)
builder.Services.AddScoped<IAdminAuthService, AdminAuthService>();

// Register JWT service (singleton - caches secret key)
builder.Services.AddSingleton<IJwtService, JwtService>();

// Add HttpContextAccessor for accessing cookies in Blazor
builder.Services.AddHttpContextAccessor();

// Configure JWT Authentication using standard ASP.NET Core pattern
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Token validation will be configured after app build (needs JwtService)
        options.Events = new JwtBearerEvents
        {
            // Read JWT from cookie instead of Authorization header
            OnMessageReceived = context =>
            {
                if (context.Request.Cookies.TryGetValue("auth_token", out var token))
                {
                    context.Token = token;
                }
                return Task.CompletedTask;
            },
            // Redirect to login page instead of 401 for web requests
            OnChallenge = context =>
            {
                // Skip default behavior for API requests
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    return Task.CompletedTask;
                }

                context.HandleResponse();
                context.Response.Redirect("/login");
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// Register application services (scoped per request/circuit)
builder.Services.AddScoped<DashboardService>();
builder.Services.AddSingleton<FingerprintDatabaseService>(); // Singleton to cache fingerprint data
builder.Services.AddSingleton<IeeeOuiDatabase>(); // IEEE OUI database for MAC vendor lookup
builder.Services.AddSingleton<PdfStorageService>(); // Singleton - manages PDF report file storage
builder.Services.AddScoped<AuditService>(); // Scoped - uses IMemoryCache for cross-request state
builder.Services.AddScoped<DiagnosticsService>(); // Scoped - network diagnostics (trunk consistency, AP lock, etc.)
builder.Services.AddScoped<SqmService>();
builder.Services.AddScoped<SqmDeploymentService>();
builder.Services.AddScoped<AgentService>();

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
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Rules.WiFiOptimizerEngine>();
builder.Services.AddScoped<WiFiOptimizerService>();
builder.Services.AddScoped<ApMapService>();
builder.Services.AddSingleton<FloorPlanService>();
builder.Services.AddSingleton<HeatmapDataCache>();
builder.Services.AddSingleton<PlannedApService>();
builder.Services.AddSingleton<ConfigTransferService>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Data.AntennaPatternLoader>();
builder.Services.AddSingleton<NetworkOptimizer.WiFi.Services.PropagationService>();

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
var hostIp = builder.Configuration["HOST_IP"];
var hostName = builder.Configuration["HOST_NAME"];
var reverseProxiedHostName = builder.Configuration["REVERSE_PROXIED_HOST_NAME"];
var corsOriginsConfig = builder.Configuration["CORS_ORIGINS"];

// Add origins from config
if (!string.IsNullOrEmpty(corsOriginsConfig))
{
    corsOriginsList.AddRange(corsOriginsConfig.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

// Auto-add origins from HOST_IP and HOST_NAME (OpenSpeedTest port)
var openSpeedTestPortConfig = builder.Configuration["OPENSPEEDTEST_PORT"];
var openSpeedTestPort = !string.IsNullOrEmpty(openSpeedTestPortConfig) ? openSpeedTestPortConfig : "3005";
var openSpeedTestHostConfig = builder.Configuration["OPENSPEEDTEST_HOST"];
var openSpeedTestHost = !string.IsNullOrEmpty(openSpeedTestHostConfig) ? openSpeedTestHostConfig : hostName;
var openSpeedTestHttps = builder.Configuration["OPENSPEEDTEST_HTTPS"]?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
var openSpeedTestHttpsPortConfig = builder.Configuration["OPENSPEEDTEST_HTTPS_PORT"];
var openSpeedTestHttpsPort = !string.IsNullOrEmpty(openSpeedTestHttpsPortConfig) ? openSpeedTestHttpsPortConfig : "443";

// HTTP origins (direct access via IP or hostname)
// Use HOST_IP if set, otherwise auto-detect from network interfaces
var corsIp = !string.IsNullOrEmpty(hostIp) ? hostIp : NetworkUtilities.DetectLocalIpFromInterfaces();
if (!string.IsNullOrEmpty(corsIp))
{
    corsOriginsList.Add($"http://{corsIp}:{openSpeedTestPort}");
}
if (!string.IsNullOrEmpty(openSpeedTestHost))
{
    corsOriginsList.Add($"http://{openSpeedTestHost}:{openSpeedTestPort}");
}

// HTTPS origins (when proxied with TLS)
if (openSpeedTestHttps && !string.IsNullOrEmpty(openSpeedTestHost))
{
    var httpsOrigin = openSpeedTestHttpsPort == "443"
        ? $"https://{openSpeedTestHost}"
        : $"https://{openSpeedTestHost}:{openSpeedTestHttpsPort}";
    corsOriginsList.Add(httpsOrigin);
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

var app = builder.Build();

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
    conn.Close();

    // Apply any pending migrations (creates DB for new installs, or applies new migrations for existing)
    db.Database.Migrate();

    // Seed default alert rules if none exist
    if (!db.AlertRules.Any())
    {
        var defaults = NetworkOptimizer.Alerts.DefaultAlertRules.GetDefaults();
        db.AlertRules.AddRange(defaults);
        db.SaveChanges();
        app.Logger.LogInformation("Seeded {Count} default alert rules", defaults.Count);
    }
}

// Pre-generate the credential encryption key (resolves singleton, triggering key creation)
app.Services.GetRequiredService<NetworkOptimizer.Storage.Services.ICredentialProtectionService>().EnsureKeyExists();

// Initialize GeoLite2 enrichment (looks for .mmdb files in data directory)
var geoDataPath = Path.GetDirectoryName(dbPath)!;
app.Services.GetRequiredService<NetworkOptimizer.Threats.Enrichment.GeoEnrichmentService>().Initialize(geoDataPath);

// Clean up any leftover config transfer temp files from previous sessions
app.Services.GetRequiredService<ConfigTransferService>().CleanupTempFiles();

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

// Host enforcement: redirect to canonical host if configured
// Only REVERSE_PROXIED_HOST_NAME or HOST_NAME trigger redirects
// HOST_IP alone does NOT redirect (allows users to access via any hostname)
var canonicalHost = builder.Configuration["REVERSE_PROXIED_HOST_NAME"];
var canonicalScheme = "https";
var canonicalPort = (string?)null; // No port for reverse proxy (443 implied)

if (string.IsNullOrEmpty(canonicalHost))
{
    canonicalHost = builder.Configuration["HOST_NAME"];
    canonicalScheme = "http";
    canonicalPort = "8042";
}
// Note: HOST_IP intentionally NOT used for redirects

if (!string.IsNullOrEmpty(canonicalHost))
{
    app.Use(async (context, next) =>
    {
        var requestHost = context.Request.Host.Host;

        // Check if host matches (case-insensitive)
        if (!string.Equals(requestHost, canonicalHost, StringComparison.OrdinalIgnoreCase))
        {
            // Build redirect URL
            var port = canonicalPort != null ? $":{canonicalPort}" : "";
            var redirectUrl = $"{canonicalScheme}://{canonicalHost}{port}{context.Request.Path}{context.Request.QueryString}";

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

// Log admin auth startup configuration
using (var startupScope = app.Services.CreateScope())
{
    var adminAuthService = startupScope.ServiceProvider.GetRequiredService<IAdminAuthService>();
    await adminAuthService.LogStartupConfigurationAsync();
}

// Configure JWT Bearer token validation parameters (requires JwtService from DI)
var jwtService = app.Services.GetRequiredService<IJwtService>();
var tokenValidationParams = await jwtService.GetTokenValidationParametersAsync();

// Get the JwtBearerOptions and set the token validation parameters
var jwtBearerOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<JwtBearerOptions>>();
jwtBearerOptions.Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters = tokenValidationParams;

// Standard ASP.NET Core authentication middleware (must come before auth check)
app.UseAuthentication();
app.UseAuthorization();

// Auth middleware that checks if authentication is required and protects all endpoints
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLower() ?? "";

    // Only these paths are public (no auth required)
    var publicPaths = new[] { "/login", "/api/auth/set-cookie", "/api/auth/logout", "/api/health" };
    var publicPrefixes = new[] { "/api/public/" };  // All /api/public/* endpoints are anonymous
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

        // Web pages redirect to login
        context.Response.Redirect("/login");
        return;
    }

    await next();
});

// Configure static files with custom MIME types for package downloads
var contentTypeProvider = new FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".ipk"] = "application/octet-stream";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypeProvider
});
app.UseAntiforgery();
app.UseCors(); // Required for OpenSpeedTest to POST results

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Alert Engine API endpoints
app.MapAlertEndpoints();

// API endpoints for agent metrics ingestion
app.MapPost("/api/metrics", async (HttpContext context) =>
{
    // TODO(agent-infrastructure): Implement metrics ingestion from agents.
    // Requires: NetworkOptimizer.Agents package with gateway agent that pushes
    // latency, bandwidth, and SQM stats. Metrics should be stored in SQLite
    // time-series tables or optionally forwarded to external TSDB.
    var metrics = await context.Request.ReadFromJsonAsync<Dictionary<string, object>>();
    return Results.Ok(new { status = "accepted" });
});

app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// Audit Report PDF download endpoints (serves pre-generated PDFs)
// Auth handled by middleware for all /api/* paths
// Uses strongly-typed int to prevent path traversal attacks
app.MapGet("/api/reports/{auditId:int}/pdf", async (int auditId, AuditService auditService) =>
{
    var (pdfBytes, fileName) = await auditService.GetAuditPdfAsync(auditId);
    return pdfBytes != null ? Results.File(pdfBytes, "application/pdf", fileName) : Results.NotFound(new { error = "PDF not found" });
});

// Get the latest audit report PDF (works across restarts since it queries database)
app.MapGet("/api/reports/latest/pdf", async (AuditService auditService) =>
{
    var (pdfBytes, fileName) = await auditService.GetLatestAuditPdfAsync();
    return pdfBytes != null ? Results.File(pdfBytes, "application/pdf", fileName) : Results.NotFound(new { error = "PDF not found" });
});

// Speed Test API endpoints (LAN iperf3)
app.MapGet("/api/speedtest/devices", async (Iperf3SpeedTestService service) =>
{
    var devices = await service.GetDevicesAsync();
    return Results.Ok(devices);
});

app.MapPost("/api/speedtest/devices/{deviceId:int}/results", async (int deviceId, Iperf3SpeedTestService service) =>
{
    var devices = await service.GetDevicesAsync();
    var device = devices.FirstOrDefault(d => d.Id == deviceId);
    if (device == null)
        return Results.NotFound(new { error = "Device not found" });

    var result = await service.RunSpeedTestAsync(device);
    return Results.Ok(result);
});

app.MapGet("/api/speedtest/results", async (Iperf3SpeedTestService service, string? deviceHost = null, int count = 50) =>
{
    // Validate count parameter is within reasonable bounds
    if (count < 1) count = 1;
    if (count > 1000) count = 1000;

    // Filter by device host if provided
    if (!string.IsNullOrWhiteSpace(deviceHost))
    {
        // Validate deviceHost format (IP address or hostname, no path traversal)
        if (deviceHost.Contains("..") || deviceHost.Contains('/') || deviceHost.Contains('\\'))
            return Results.BadRequest(new { error = "Invalid device host format" });

        return Results.Ok(await service.GetResultsForDeviceAsync(deviceHost, count));
    }

    var results = await service.GetRecentResultsAsync(count);
    return Results.Ok(results);
});

// Public endpoint for external clients (OpenSpeedTest, iperf3) to submit results
app.MapPost("/api/public/speedtest/results", async (HttpContext context, ClientSpeedTestService service) =>
{
    // OpenSpeedTest sends data as URL query params: d, u, p, j, dd, ud, ua
    var query = context.Request.Query;

    // Also check form data for POST body
    IFormCollection? form = null;
    if (context.Request.HasFormContentType)
    {
        form = await context.Request.ReadFormAsync();
    }

    // Helper to get value from query or form
    string? GetValue(string key) =>
        query.TryGetValue(key, out var qv) ? qv.ToString() :
        form?.TryGetValue(key, out var fv) == true ? fv.ToString() : null;

    var downloadStr = GetValue("d");
    var uploadStr = GetValue("u");

    if (string.IsNullOrEmpty(downloadStr) || string.IsNullOrEmpty(uploadStr))
    {
        return Results.BadRequest(new { error = "Missing required parameters: d (download) and u (upload)" });
    }

    if (!double.TryParse(downloadStr, out var download) || !double.TryParse(uploadStr, out var upload))
    {
        return Results.BadRequest(new { error = "Invalid speed values" });
    }

    double? ping = double.TryParse(GetValue("p"), out var p) ? p : null;
    double? jitter = double.TryParse(GetValue("j"), out var j) ? j : null;
    double? downloadData = double.TryParse(GetValue("dd"), out var dd) ? dd : null;
    double? uploadData = double.TryParse(GetValue("ud"), out var ud) ? ud : null;
    var userAgent = GetValue("ua") ?? context.Request.Headers.UserAgent.ToString();

    // Geolocation (optional)
    double? latitude = double.TryParse(GetValue("lat"), out var lat) ? lat : null;
    double? longitude = double.TryParse(GetValue("lng"), out var lng) ? lng : null;
    int? locationAccuracy = int.TryParse(GetValue("acc"), out var acc) ? acc : null;

    // Test duration per direction (seconds)
    int? duration = int.TryParse(GetValue("dur"), out var dur) ? dur : null;

    var clientIp = GetClientIp(context);

    var result = await service.RecordOpenSpeedTestResultAsync(
        clientIp, download, upload, ping, jitter, downloadData, uploadData, userAgent,
        latitude, longitude, locationAccuracy, duration);

    return Results.Ok(new
    {
        success = true,
        id = result.Id,
        clientIp = result.DeviceHost,
        clientName = result.DeviceName,
        download = result.DownloadMbps,
        upload = result.UploadMbps
    });
}).RequireCors("SpeedTestCors");

// Public endpoint for capturing topology snapshots during speed tests
// Called by OpenSpeedTest ~3 seconds into a test to capture wireless rates mid-test
app.MapPost("/api/public/speedtest/topology-snapshots", async (HttpContext context, ITopologySnapshotService snapshotService) =>
{
    var clientIp = GetClientIp(context);

    // Fire-and-forget - capture snapshot asynchronously, don't block response
    _ = snapshotService.CaptureSnapshotAsync(clientIp);

    return Results.Ok(new { success = true });
}).RequireCors("SpeedTestCors");

// Authenticated endpoint for viewing client speed test results
app.MapGet("/api/speedtest/client-results", async (ClientSpeedTestService service, string? ip = null, string? mac = null, int count = 50) =>
{
    if (count < 1) count = 1;
    if (count > 1000) count = 1000;

    // Filter by IP if provided
    if (!string.IsNullOrWhiteSpace(ip))
        return Results.Ok(await service.GetResultsByIpAsync(ip, count));

    // Filter by MAC if provided
    if (!string.IsNullOrWhiteSpace(mac))
        return Results.Ok(await service.GetResultsByMacAsync(mac, count));

    // Return all results
    return Results.Ok(await service.GetResultsAsync(count));
});

// Authenticated endpoint for deleting a client speed test result
app.MapDelete("/api/speedtest/client-results/{id:int}", async (int id, ClientSpeedTestService service) =>
{
    var deleted = await service.DeleteResultAsync(id);
    return deleted ? Results.NoContent() : Results.NotFound();
});

// Auth API endpoints
app.MapGet("/api/auth/set-cookie", (HttpContext context, string token, string returnUrl = "/") =>
{
    // Validate returnUrl to prevent open redirect attacks
    // Only allow relative URLs that start with /
    if (string.IsNullOrEmpty(returnUrl) ||
        !returnUrl.StartsWith('/') ||
        returnUrl.StartsWith("//") ||
        returnUrl.Contains(':'))
    {
        returnUrl = "/";
    }

    // Only set Secure flag if actually using HTTPS
    // (localhost/127.0.0.1 check was causing issues when accessed via IP over HTTP)
    var isSecure = context.Request.IsHttps;

    // Set HttpOnly cookie with the JWT token
    context.Response.Cookies.Append("auth_token", token, new CookieOptions
    {
        HttpOnly = true,
        Secure = isSecure,
        SameSite = isSecure ? SameSiteMode.Strict : SameSiteMode.Lax,
        Expires = DateTimeOffset.UtcNow.AddDays(30), // Match JWT expiration
        Path = "/"
    });

    return Results.Redirect(returnUrl);
});

app.MapGet("/api/auth/logout", (HttpContext context) =>
{
    context.Response.Cookies.Delete("auth_token", new CookieOptions
    {
        Path = "/"
    });

    return Results.Redirect("/login");
});

app.MapGet("/api/auth/check", async (HttpContext context, IJwtService jwt) =>
{
    if (context.Request.Cookies.TryGetValue("auth_token", out var token))
    {
        var principal = await jwt.ValidateTokenAsync(token);
        if (principal != null)
        {
            return Results.Ok(new { authenticated = true, user = principal.Identity?.Name });
        }
    }
    return Results.Unauthorized();
});

// UPnP Notes API endpoints
app.MapGet("/api/upnp/notes", async (NetworkOptimizerDbContext db) =>
{
    var notes = await db.UpnpNotes.ToListAsync();
    return Results.Ok(notes);
});

app.MapPut("/api/upnp/notes", async (HttpContext context, NetworkOptimizerDbContext db) =>
{
    var request = await context.Request.ReadFromJsonAsync<UpnpNoteRequest>();
    if (request == null || string.IsNullOrWhiteSpace(request.HostIp) ||
        string.IsNullOrWhiteSpace(request.Port) || string.IsNullOrWhiteSpace(request.Protocol))
    {
        return Results.BadRequest(new { error = "HostIp, Port, and Protocol are required" });
    }

    // Normalize protocol to lowercase
    var protocol = request.Protocol.ToLowerInvariant();

    // Find existing note or create new
    var existing = await db.UpnpNotes.FirstOrDefaultAsync(n =>
        n.HostIp == request.HostIp &&
        n.Port == request.Port &&
        n.Protocol == protocol);

    if (existing != null)
    {
        // Update or delete if note is empty
        if (string.IsNullOrWhiteSpace(request.Note))
        {
            db.UpnpNotes.Remove(existing);
        }
        else
        {
            existing.Note = request.Note;
            existing.UpdatedAt = DateTime.UtcNow;
        }
    }
    else if (!string.IsNullOrWhiteSpace(request.Note))
    {
        // Create new note
        var note = new UpnpNote
        {
            HostIp = request.HostIp,
            Port = request.Port,
            Protocol = protocol,
            Note = request.Note,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.UpnpNotes.Add(note);
    }

    await db.SaveChangesAsync();
    return Results.Ok(new { success = true });
});

// AP Location API endpoints
app.MapGet("/api/ap-locations", async (NetworkOptimizerDbContext db) =>
{
    var locations = await db.ApLocations.ToListAsync();
    return Results.Ok(locations);
});

app.MapPut("/api/ap-locations/{mac}", async (string mac, HttpContext context, NetworkOptimizerDbContext db) =>
{
    var request = await context.Request.ReadFromJsonAsync<ApLocationRequest>();
    if (request == null)
    {
        return Results.BadRequest(new { error = "Request body is required" });
    }

    // Normalize MAC to lowercase for consistent matching
    var normalizedMac = mac.ToLowerInvariant();

    var existing = await db.ApLocations.FirstOrDefaultAsync(a => a.ApMac == normalizedMac);
    if (existing != null)
    {
        existing.Latitude = request.Latitude;
        existing.Longitude = request.Longitude;
        existing.Floor = request.Floor ?? 1;
        existing.UpdatedAt = DateTime.UtcNow;
    }
    else
    {
        var location = new ApLocation
        {
            ApMac = normalizedMac,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            Floor = request.Floor ?? 1,
            UpdatedAt = DateTime.UtcNow
        };
        db.ApLocations.Add(location);
    }

    await db.SaveChangesAsync();
    return Results.Ok(new { success = true });
});

app.MapDelete("/api/ap-locations/{mac}", async (string mac, NetworkOptimizerDbContext db) =>
{
    var normalizedMac = mac.ToLowerInvariant();
    var existing = await db.ApLocations.FirstOrDefaultAsync(a => a.ApMac == normalizedMac);
    if (existing == null)
    {
        return Results.NotFound();
    }

    db.ApLocations.Remove(existing);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// --- Building & Floor Plan API ---

app.MapGet("/api/floor-plan/buildings", async (FloorPlanService svc) =>
{
    var buildings = await svc.GetBuildingsAsync();
    return Results.Ok(buildings.Select(b => new
    {
        b.Id, b.Name, b.CenterLatitude, b.CenterLongitude, b.CreatedAt,
        Floors = b.Floors.Select(f => new
        {
            f.Id, f.BuildingId, f.FloorNumber, f.Label, f.SwLatitude, f.SwLongitude,
            f.NeLatitude, f.NeLongitude, f.Opacity, f.WallsJson, f.FloorMaterial,
            HasImage = !string.IsNullOrEmpty(f.ImagePath), f.CreatedAt, f.UpdatedAt
        })
    }));
});

app.MapPost("/api/floor-plan/buildings", async (HttpContext context, FloorPlanService svc, ApMapService apMapSvc, PlannedApService plannedApSvc, HeatmapDataCache heatmapCache) =>
{
    var request = await context.Request.ReadFromJsonAsync<BuildingRequest>();
    if (request == null) return Results.BadRequest(new { error = "Request body is required" });
    var building = await svc.CreateBuildingAsync(request.Name?.Trim() ?? "", request.CenterLatitude, request.CenterLongitude);
    await heatmapCache.InvalidateAndReloadAsync(svc, apMapSvc, plannedApSvc);
    return Results.Ok(new { building.Id, building.Name, building.CenterLatitude, building.CenterLongitude });
});

app.MapPut("/api/floor-plan/buildings/{id:int}", async (int id, HttpContext context, FloorPlanService svc, ApMapService apMapSvc, PlannedApService plannedApSvc, HeatmapDataCache heatmapCache) =>
{
    var request = await context.Request.ReadFromJsonAsync<BuildingRequest>();
    if (request == null) return Results.BadRequest(new { error = "Request body is required" });
    var building = await svc.UpdateBuildingAsync(id, request.Name?.Trim() ?? "", request.CenterLatitude, request.CenterLongitude);
    await heatmapCache.InvalidateAndReloadAsync(svc, apMapSvc, plannedApSvc);
    return building != null ? Results.Ok(new { success = true }) : Results.NotFound();
});

app.MapDelete("/api/floor-plan/buildings/{id:int}", async (int id, FloorPlanService svc, ApMapService apMapSvc, PlannedApService plannedApSvc, HeatmapDataCache heatmapCache) =>
{
    await svc.DeleteBuildingAsync(id);
    await heatmapCache.InvalidateAndReloadAsync(svc, apMapSvc, plannedApSvc);
    return Results.NoContent();
});

app.MapGet("/api/floor-plan/buildings/{id:int}/floors", async (int id, FloorPlanService svc) =>
{
    var floors = await svc.GetFloorsAsync(id);
    return Results.Ok(floors.Select(f => new
    {
        f.Id, f.BuildingId, f.FloorNumber, f.Label, f.SwLatitude, f.SwLongitude,
        f.NeLatitude, f.NeLongitude, f.Opacity, f.WallsJson, f.FloorMaterial,
        HasImage = !string.IsNullOrEmpty(f.ImagePath), f.CreatedAt, f.UpdatedAt
    }));
});

app.MapPost("/api/floor-plan/buildings/{id:int}/floors", async (int id, HttpContext context, FloorPlanService svc, ApMapService apMapSvc, PlannedApService plannedApSvc, HeatmapDataCache heatmapCache) =>
{
    var request = await context.Request.ReadFromJsonAsync<FloorRequest>();
    if (request == null) return Results.BadRequest(new { error = "Request body is required" });
    var floor = await svc.CreateFloorAsync(id, request.FloorNumber, request.Label,
        request.SwLatitude, request.SwLongitude, request.NeLatitude, request.NeLongitude);
    await heatmapCache.InvalidateAndReloadAsync(svc, apMapSvc, plannedApSvc);
    return Results.Ok(new { floor.Id, floor.BuildingId, floor.FloorNumber, floor.Label });
});

app.MapPut("/api/floor-plan/floors/{id:int}", async (int id, HttpContext context, FloorPlanService svc, ApMapService apMapSvc, PlannedApService plannedApSvc, HeatmapDataCache heatmapCache) =>
{
    var request = await context.Request.ReadFromJsonAsync<FloorUpdateRequest>();
    if (request == null) return Results.BadRequest(new { error = "Request body is required" });
    var floor = await svc.UpdateFloorAsync(id,
        request.SwLatitude, request.SwLongitude, request.NeLatitude, request.NeLongitude,
        request.Opacity, request.WallsJson, request.Label, floorMaterial: request.FloorMaterial);
    await heatmapCache.InvalidateAndReloadAsync(svc, apMapSvc, plannedApSvc);
    return floor != null ? Results.Ok(new { success = true }) : Results.NotFound();
});

app.MapDelete("/api/floor-plan/floors/{id:int}", async (int id, FloorPlanService svc, ApMapService apMapSvc, PlannedApService plannedApSvc, HeatmapDataCache heatmapCache) =>
{
    await svc.DeleteFloorAsync(id);
    await heatmapCache.InvalidateAndReloadAsync(svc, apMapSvc, plannedApSvc);
    return Results.NoContent();
});

app.MapGet("/api/floor-plan/floors/{id:int}/image", async (int id, FloorPlanService svc) =>
{
    var floor = await svc.GetFloorAsync(id);
    if (floor == null) return Results.NotFound();
    var imagePath = svc.GetFloorImagePath(floor);
    if (imagePath == null) return Results.NotFound();
    var mimeType = DetectImageMimeType(imagePath);
    return Results.File(imagePath, mimeType);
});

app.MapPost("/api/floor-plan/floors/{id:int}/image", async (int id, HttpContext context, FloorPlanService svc) =>
{
    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("image");
    if (file == null || file.Length == 0)
        return Results.BadRequest(new { error = "No image file provided" });

    using var stream = file.OpenReadStream();
    await svc.SaveFloorImageAsync(id, stream);
    return Results.Ok(new { success = true });
});

// --- FloorPlanImage (multi-image per floor) ---

app.MapGet("/api/floor-plan/floors/{floorId:int}/images", async (int floorId, FloorPlanService svc) =>
{
    var images = await svc.GetFloorImagesAsync(floorId);
    return Results.Ok(images.Select(i => new
    {
        i.Id, i.FloorPlanId, i.Label, i.SwLatitude, i.SwLongitude,
        i.NeLatitude, i.NeLongitude, i.Opacity, i.RotationDeg, i.CropJson,
        i.SortOrder, HasFile = !string.IsNullOrEmpty(i.ImagePath)
    }));
});

app.MapPost("/api/floor-plan/floors/{floorId:int}/images", async (int floorId, HttpContext context, FloorPlanService svc) =>
{
    const long maxFileSize = 50 * 1024 * 1024; // 50 MB
    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("image");
    if (file == null || file.Length == 0)
        return Results.BadRequest(new { error = "No image file provided" });
    if (file.Length > maxFileSize)
        return Results.BadRequest(new { error = "File exceeds 50 MB limit" });

    double.TryParse(form["swLat"], System.Globalization.CultureInfo.InvariantCulture, out var swLat);
    double.TryParse(form["swLng"], System.Globalization.CultureInfo.InvariantCulture, out var swLng);
    double.TryParse(form["neLat"], System.Globalization.CultureInfo.InvariantCulture, out var neLat);
    double.TryParse(form["neLng"], System.Globalization.CultureInfo.InvariantCulture, out var neLng);
    var label = form["label"].FirstOrDefault() ?? "";

    using var stream = file.OpenReadStream();
    var image = await svc.CreateFloorImageAsync(floorId, stream, swLat, swLng, neLat, neLng, label);
    return Results.Ok(new
    {
        image.Id, image.FloorPlanId, image.Label, image.SwLatitude, image.SwLongitude,
        image.NeLatitude, image.NeLongitude, image.Opacity, image.RotationDeg, image.CropJson,
        image.SortOrder, HasFile = true
    });
});

app.MapGet("/api/floor-plan/images/{imageId:int}/file", async (int imageId, FloorPlanService svc) =>
{
    var image = await svc.GetFloorImageAsync(imageId);
    if (image == null) return Results.NotFound();
    var filePath = svc.GetFloorImageFilePath(image);
    if (filePath == null) return Results.NotFound();
    var mimeType = DetectImageMimeType(filePath);
    return Results.File(filePath, mimeType);
});

app.MapPut("/api/floor-plan/images/{imageId:int}", async (int imageId, FloorImageUpdateRequest req, FloorPlanService svc) =>
{
    var image = await svc.UpdateFloorImageAsync(imageId, req.SwLatitude, req.SwLongitude,
        req.NeLatitude, req.NeLongitude, req.Opacity, req.RotationDeg, req.CropJson, req.Label);
    if (image == null) return Results.NotFound();
    return Results.Ok(new
    {
        image.Id, image.FloorPlanId, image.Label, image.SwLatitude, image.SwLongitude,
        image.NeLatitude, image.NeLongitude, image.Opacity, image.RotationDeg, image.CropJson, image.SortOrder
    });
});

app.MapDelete("/api/floor-plan/images/{imageId:int}", async (int imageId, FloorPlanService svc) =>
{
    return await svc.DeleteFloorImageAsync(imageId) ? Results.NoContent() : Results.NotFound();
});

app.MapPost("/api/floor-plan/heatmap", async (HttpContext context,
    FloorPlanService floorSvc, ApMapService apMapSvc,
    PlannedApService plannedApSvc,
    NetworkOptimizer.WiFi.Services.PropagationService propagationSvc,
    HeatmapDataCache heatmapCache) =>
{
    var request = await context.Request.ReadFromJsonAsync<NetworkOptimizer.WiFi.Models.HeatmapRequest>();
    if (request == null) return Results.BadRequest(new { error = "Request body is required" });

    if (!request.SwLat.HasValue || !request.SwLng.HasValue || !request.NeLat.HasValue || !request.NeLng.HasValue)
        return Results.BadRequest(new { error = "Viewport bounds are required" });

    var activeFloor = request.ActiveFloor;

    // Load from cache (only hits DB when data has been invalidated)
    var cached = await heatmapCache.GetOrLoadAsync(floorSvc, apMapSvc, plannedApSvc);

    // Build placed APs list from cached markers
    var bandFilter = request.Band == "2.4" ? "2.4" : request.Band == "6" ? "6" : "5";
    var placedAps = cached.ApMarkers
        .Where(a => a.Latitude.HasValue && a.Longitude.HasValue)
        .Where(a => a.Radios.Any(r => r.Band.Contains(bandFilter)))
        .Select(a =>
        {
            var radio = a.Radios.First(r => r.Band.Contains(bandFilter));
            return new NetworkOptimizer.WiFi.Models.PropagationAp
            {
                Mac = a.Mac,
                Model = a.Model,
                Latitude = a.Latitude!.Value,
                Longitude = a.Longitude!.Value,
                Floor = a.Floor ?? 1,
                OrientationDeg = a.OrientationDeg,
                MountType = a.MountType,
                AntennaMode = radio.AntennaMode,
                TxPowerDbm = radio.TxPowerDbm ?? 20,
                AntennaGainDbi = (radio.Eirp ?? 23) - (radio.TxPowerDbm ?? 20)
            };
        }).ToList();

    // Add planned APs to the propagation computation (unless excluded by toggle)
    if (!request.ExcludePlannedAps)
    {
        var patternLoader = context.RequestServices.GetRequiredService<NetworkOptimizer.WiFi.Data.AntennaPatternLoader>();
        foreach (var pa in cached.PlannedAps)
        {
            var bandDefaults = NetworkOptimizer.WiFi.Data.ApModelCatalog.GetBandDefaults(pa.Model, bandFilter);
            var (modeGain, modeMaxTx, modeDefaultTx) = NetworkOptimizer.WiFi.Data.ApModelCatalog.ResolveForMode(bandDefaults, pa.AntennaMode);
            var txPowerStored = bandFilter switch { "2.4" => pa.TxPower24Dbm, "6" => pa.TxPower6Dbm, _ => pa.TxPower5Dbm };
            var txPower = txPowerStored ?? modeDefaultTx;
            var supportedBands = patternLoader.GetSupportedBands(pa.Model);
            if (!supportedBands.Contains(bandFilter)) continue;

            placedAps.Add(new NetworkOptimizer.WiFi.Models.PropagationAp
            {
                Mac = $"planned-{pa.Id}",
                Model = pa.Model,
                Latitude = pa.Latitude,
                Longitude = pa.Longitude,
                Floor = pa.Floor,
                OrientationDeg = pa.OrientationDeg,
                MountType = pa.MountType,
                AntennaMode = pa.AntennaMode,
                TxPowerDbm = txPower,
                AntennaGainDbi = modeGain
            });
        }
    }

    // Apply TX power overrides from simulation slider
    if (request.TxPowerOverrides is { Count: > 0 })
    {
        foreach (var ap in placedAps)
        {
            if (request.TxPowerOverrides.TryGetValue(ap.Mac.ToLowerInvariant(), out var overridePower))
                ap.TxPowerDbm = overridePower;
        }
    }

    // Apply antenna mode overrides from simulation toggle (also updates gain)
    if (request.AntennaModeOverrides is { Count: > 0 })
    {
        foreach (var ap in placedAps)
        {
            if (request.AntennaModeOverrides.TryGetValue(ap.Mac.ToLowerInvariant(), out var overrideMode))
            {
                ap.AntennaMode = overrideMode;
                var bd = NetworkOptimizer.WiFi.Data.ApModelCatalog.GetBandDefaults(ap.Model, bandFilter);
                var (gain, maxTx, _) = NetworkOptimizer.WiFi.Data.ApModelCatalog.ResolveForMode(bd, overrideMode);
                ap.AntennaGainDbi = gain;
                ap.TxPowerDbm = Math.Min(ap.TxPowerDbm, maxTx);
            }
        }
    }

    // Remove disabled APs from simulation
    if (request.DisabledMacs is { Count: > 0 })
    {
        var disabled = new HashSet<string>(request.DisabledMacs, StringComparer.OrdinalIgnoreCase);
        placedAps.RemoveAll(ap => disabled.Contains(ap.Mac));
    }

    var result = propagationSvc.ComputeHeatmap(
        request.SwLat.Value, request.SwLng.Value, request.NeLat.Value, request.NeLng.Value,
        request.Band, placedAps, cached.WallsByFloor, activeFloor, request.GridResolutionMeters, cached.BuildingFloorInfos);

    return Results.Ok(result);
});

// ── Planned APs ─────────────────────────────────────────────────────

app.MapGet("/api/floor-plan/planned-aps", async (PlannedApService svc) =>
{
    var aps = await svc.GetAllAsync();
    return Results.Ok(aps);
});

app.MapPost("/api/floor-plan/planned-aps", async (HttpContext context, FloorPlanService floorSvc, ApMapService apMapSvc, PlannedApService svc, HeatmapDataCache heatmapCache) =>
{
    var ap = await context.Request.ReadFromJsonAsync<NetworkOptimizer.Storage.Models.PlannedAp>();
    if (ap == null) return Results.BadRequest(new { error = "Request body is required" });
    var created = await svc.CreateAsync(ap);
    await heatmapCache.InvalidateAndReloadAsync(floorSvc, apMapSvc, svc);
    return Results.Ok(created);
});

app.MapPut("/api/floor-plan/planned-aps/{id:int}", async (int id, HttpContext context, FloorPlanService floorSvc, ApMapService apMapSvc, PlannedApService svc, HeatmapDataCache heatmapCache) =>
{
    var body = await context.Request.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
    if (body == null) return Results.BadRequest(new { error = "Request body is required" });

    if (body.TryGetValue("latitude", out var lat) && body.TryGetValue("longitude", out var lng))
        await svc.UpdateLocationAsync(id, lat.GetDouble(), lng.GetDouble());
    if (body.TryGetValue("floor", out var floor))
        await svc.UpdateFloorAsync(id, floor.GetInt32());
    if (body.TryGetValue("orientationDeg", out var deg))
        await svc.UpdateOrientationAsync(id, deg.GetInt32());
    if (body.TryGetValue("mountType", out var mt))
        await svc.UpdateMountTypeAsync(id, mt.GetString() ?? "ceiling");
    if (body.TryGetValue("txPowerDbm", out var tx) && body.TryGetValue("band", out var band))
        await svc.UpdateTxPowerAsync(id, band.GetString() ?? "5", tx.ValueKind == System.Text.Json.JsonValueKind.Null ? null : tx.GetInt32());
    if (body.TryGetValue("antennaMode", out var am))
        await svc.UpdateAntennaModeAsync(id, am.ValueKind == System.Text.Json.JsonValueKind.Null ? null : am.GetString());
    if (body.TryGetValue("name", out var name))
        await svc.UpdateNameAsync(id, (name.GetString() ?? "").Trim());

    await heatmapCache.InvalidateAndReloadAsync(floorSvc, apMapSvc, svc);
    return Results.Ok(new { success = true });
});

app.MapDelete("/api/floor-plan/planned-aps/{id:int}", async (int id, FloorPlanService floorSvc, ApMapService apMapSvc, PlannedApService svc, HeatmapDataCache heatmapCache) =>
{
    var deleted = await svc.DeleteAsync(id);
    await heatmapCache.InvalidateAndReloadAsync(floorSvc, apMapSvc, svc);
    return deleted ? Results.Ok(new { success = true }) : Results.NotFound();
});

app.MapGet("/api/floor-plan/ap-catalog", (NetworkOptimizer.WiFi.Data.AntennaPatternLoader patternLoader) =>
{
    var catalog = NetworkOptimizer.WiFi.Data.ApModelCatalog.BuildCatalog(patternLoader);
    return Results.Ok(catalog.Select(c => new
    {
        model = c.Model,
        bands = c.Bands.ToDictionary(b => b.Key, b => new
        {
            defaultTxPowerDbm = b.Value.DefaultTxPowerDbm,
            minTxPowerDbm = b.Value.MinTxPowerDbm,
            maxTxPowerDbm = b.Value.MaxTxPowerDbm,
            antennaGainDbi = b.Value.AntennaGainDbi,
            modeOverrides = b.Value.ModeOverrides?.ToDictionary(m => m.Key, m => new
            {
                antennaGainDbi = m.Value.AntennaGainDbi,
                maxTxPowerDbm = m.Value.MaxTxPowerDbm,
                defaultTxPowerDbm = m.Value.DefaultTxPowerDbm,
            })
        }),
        defaultMountType = c.DefaultMountType,
        hasOmniVariant = c.HasOmniVariant,
        antennaVariants = c.AntennaVariants,
        iconPath = NetworkOptimizer.Web.Components.Shared.DeviceIcon.GetIconPath(c.Model) ?? "/images/devices/default-ap.png"
    }));
});

// Demo mode masking endpoint (returns mappings from DEMO_MODE_MAPPINGS env var)
// --- Client Dashboard API ---

app.MapGet("/api/client-dashboard/client", async (HttpContext context, ClientDashboardService service) =>
{
    var clientIp = GetClientIp(context);
    var identity = await service.IdentifyClientAsync(clientIp);
    return identity != null ? Results.Ok(identity) : Results.NotFound(new { error = "Client not found" });
});

app.MapGet("/api/client-dashboard/signal-detail", async (HttpContext context, ClientDashboardService service,
    double? lat = null, double? lng = null, int? acc = null) =>
{
    var clientIp = GetClientIp(context);
    var result = await service.PollSignalAsync(clientIp, lat, lng, acc);
    return result != null ? Results.Ok(result) : Results.NotFound(new { error = "Client not found" });
});

app.MapPost("/api/client-dashboard/gps-locations", async (HttpContext context, ClientDashboardService service) =>
{
    var request = await context.Request.ReadFromJsonAsync<NetworkOptimizer.Web.Models.GpsUpdateRequest>();
    if (request == null)
        return Results.BadRequest(new { error = "Request body is required" });

    // Identify client by IP to get MAC
    var clientIp = GetClientIp(context);
    var identity = await service.IdentifyClientAsync(clientIp);
    if (identity == null)
        return Results.NotFound(new { error = "Client not found" });

    await service.SubmitGpsAsync(identity.Mac, request.Latitude, request.Longitude, request.AccuracyMeters);
    return Results.Ok(new { success = true });
});

app.MapGet("/api/client-dashboard/signal-history", async (ClientDashboardService service,
    string mac, DateTime? from = null, DateTime? to = null, int? skip = null, int? take = null) =>
{
    var fromDate = from ?? DateTime.UtcNow.AddHours(-24);
    var toDate = to ?? DateTime.UtcNow;
    var history = await service.GetSignalHistoryAsync(mac, fromDate, toDate, skip ?? 0, take ?? 500);
    return Results.Ok(history);
});

app.MapGet("/api/client-dashboard/trace-history", async (ClientDashboardService service,
    string mac, DateTime? from = null, DateTime? to = null) =>
{
    var fromDate = from ?? DateTime.UtcNow.AddHours(-24);
    var toDate = to ?? DateTime.UtcNow;
    var history = await service.GetTraceHistoryAsync(mac, fromDate, toDate);
    return Results.Ok(history);
});

app.MapGet("/api/client-dashboard/speed-results", async (ClientDashboardService service,
    string mac, DateTime? from = null, DateTime? to = null) =>
{
    var fromDate = from ?? DateTime.UtcNow.AddHours(-24);
    var toDate = to ?? DateTime.UtcNow;
    var results = await service.GetSpeedResultsAsync(mac, fromDate, toDate);
    return Results.Ok(results);
});

app.MapGet("/api/demo-mappings", () =>
{
    var mappingsEnv = Environment.GetEnvironmentVariable("DEMO_MODE_MAPPINGS");
    if (string.IsNullOrWhiteSpace(mappingsEnv))
    {
        return Results.Ok(new { mappings = Array.Empty<object>() });
    }

    // Parse format: "key1:value1,key2:value2"
    var mappings = mappingsEnv
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair =>
        {
            var parts = pair.Split(':', 2);
            if (parts.Length == 2)
            {
                return new { from = parts[0].Trim(), to = parts[1].Trim() };
            }
            return null;
        })
        .Where(m => m != null)
        .ToArray();

    return Results.Ok(new { mappings });
});

// --- Config Backup/Restore API ---

app.MapGet("/api/config/backups", async (string type, ConfigTransferService service) =>
{
    var exportType = type?.Equals("settings", StringComparison.OrdinalIgnoreCase) == true
        ? ExportType.SettingsOnly
        : ExportType.Full;

    var bytes = await service.ExportAsync(exportType);
    var label = exportType == ExportType.Full ? "full" : "settings";
    var fileName = $"NetworkOptimizer-{label}-{DateTime.UtcNow:yyyyMMdd}.nopt";
    return Results.File(bytes, "application/octet-stream", fileName);
});

app.MapPost("/api/config/backups", async (HttpContext context, ConfigTransferService service) =>
{
    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null || file.Length == 0)
        return Results.BadRequest(new { error = "No file provided" });

    try
    {
        using var stream = file.OpenReadStream();
        var preview = await service.ValidateImportAsync(stream);
        return Results.Ok(preview);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Invalid file: {ex.Message}" });
    }
});

app.MapPut("/api/config", async (ConfigTransferService service) =>
{
    try
    {
        await service.ApplyImportAsync();
        return Results.Ok(new { message = "Config restored. Restarting..." });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapDelete("/api/config/backups/pending", (ConfigTransferService service) =>
{
    service.CancelPendingImport();
    return Results.Ok(new { message = "Pending backup cancelled" });
});

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
            ["IPERF3_SERVER_ENABLED"] = "Iperf3Server:Enabled",  // Maps to Iperf3Server:Enabled
            ["OPENSPEEDTEST_PORT"] = "OPENSPEEDTEST_PORT",
            ["OPENSPEEDTEST_HOST"] = "OPENSPEEDTEST_HOST",
            ["OPENSPEEDTEST_HTTPS"] = "OPENSPEEDTEST_HTTPS"
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

// Helper to extract client IP from request (handles proxies)
static string GetClientIp(HttpContext context)
{
    var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrEmpty(forwardedFor))
    {
        clientIp = forwardedFor.Split(',')[0].Trim();
    }
    return clientIp;
}

static string DetectImageMimeType(string filePath)
{
    try
    {
        var header = new byte[12];
        using var fs = File.OpenRead(filePath);
        var bytesRead = fs.Read(header, 0, header.Length);
        if (bytesRead >= 4)
        {
            // PNG: 89 50 4E 47
            if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                return "image/png";
            // JPEG: FF D8 FF
            if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                return "image/jpeg";
            // WebP: RIFF + 4 byte size + WEBP
            if (bytesRead >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
                && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
                return "image/webp";
        }
    }
    catch { /* fall through */ }

    // Fallback by extension
    var ext = Path.GetExtension(filePath).ToLowerInvariant();
    return ext switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "image/png"
    };
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
