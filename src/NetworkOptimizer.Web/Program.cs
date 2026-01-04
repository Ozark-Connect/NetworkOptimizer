using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NetworkOptimizer.Web;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Audit;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

// TODO(i18n): Add internationalization/localization support. Community volunteers available for translations.
// See: https://learn.microsoft.com/en-us/aspnet/core/blazor/globalization-localization

var builder = WebApplication.CreateBuilder(args);

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

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Add memory cache for path analysis caching
builder.Services.AddMemoryCache();

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
// In Docker, use /app/data; otherwise use LocalApplicationData
var dbPath = isDocker
    ? "/app/data/network_optimizer.db"
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetworkOptimizer", "network_optimizer.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
builder.Services.AddDbContext<NetworkOptimizerDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}")
           .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

// Also register DbContextFactory for singletons that need database access (ClientSpeedTestService)
builder.Services.AddDbContextFactory<NetworkOptimizerDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}")
           .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

// Register repository pattern (scoped - same lifetime as DbContext)
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.IAuditRepository, NetworkOptimizer.Storage.Repositories.AuditRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.ISettingsRepository, NetworkOptimizer.Storage.Repositories.SettingsRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.IUniFiRepository, NetworkOptimizer.Storage.Repositories.UniFiRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.IModemRepository, NetworkOptimizer.Storage.Repositories.ModemRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.ISpeedTestRepository, NetworkOptimizer.Storage.Repositories.SpeedTestRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.ISqmRepository, NetworkOptimizer.Storage.Repositories.SqmRepository>();
builder.Services.AddScoped<NetworkOptimizer.Storage.Interfaces.IAgentRepository, NetworkOptimizer.Storage.Repositories.AgentRepository>();

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

// Register iperf3 Server service (hosted - runs iperf3 in server mode, monitors for client tests)
// Enable via environment variable: Iperf3Server__Enabled=true
builder.Services.AddHostedService<Iperf3ServerService>();

// Register System Settings service (singleton - system-wide configuration)
builder.Services.AddSingleton<SystemSettingsService>();

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
builder.Services.AddScoped<AuditService>(); // Scoped - uses IMemoryCache for cross-request state
builder.Services.AddScoped<SqmService>();
builder.Services.AddScoped<SqmDeploymentService>();
builder.Services.AddScoped<AgentService>();

// Configure HTTP client for API calls
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("TcMonitor", client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});

// TODO(agent-infrastructure): CORS for agent API endpoints - enable when agents are implemented
// builder.Services.AddCors(options =>
// {
//     options.AddDefaultPolicy(policy =>
//     {
//         policy.AllowAnyOrigin()
//               .AllowAnyMethod()
//               .AllowAnyHeader();
//     });
// });

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
}

// Pre-generate the credential encryption key (resolves singleton, triggering key creation)
app.Services.GetRequiredService<NetworkOptimizer.Storage.Services.ICredentialProtectionService>().EnsureKeyExists();

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
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
    var staticPaths = new[] { "/_blazor", "/_framework", "/css", "/js", "/images", "/_content", "/downloads" };

    // Allow public endpoints
    if (publicPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)))
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
// app.UseCors(); // TODO(agent-infrastructure): Enable when agents are implemented

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

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

// iperf3 Speed Test API endpoints
app.MapGet("/api/iperf3/devices", async (Iperf3SpeedTestService service) =>
{
    var devices = await service.GetDevicesAsync();
    return Results.Ok(devices);
});

app.MapPost("/api/iperf3/test/{deviceId:int}", async (int deviceId, Iperf3SpeedTestService service) =>
{
    var devices = await service.GetDevicesAsync();
    var device = devices.FirstOrDefault(d => d.Id == deviceId);
    if (device == null)
        return Results.NotFound(new { error = "Device not found" });

    var result = await service.RunSpeedTestAsync(device);
    return Results.Ok(result);
});

app.MapGet("/api/iperf3/results", async (Iperf3SpeedTestService service, int count = 50) =>
{
    // Validate count parameter is within reasonable bounds
    if (count < 1) count = 1;
    if (count > 1000) count = 1000;

    var results = await service.GetRecentResultsAsync(count);
    return Results.Ok(results);
});

app.MapGet("/api/iperf3/results/{deviceHost}", async (string deviceHost, Iperf3SpeedTestService service, int count = 20) =>
{
    // Validate deviceHost format (IP address or hostname, no path traversal)
    if (string.IsNullOrWhiteSpace(deviceHost) ||
        deviceHost.Contains("..") ||
        deviceHost.Contains('/') ||
        deviceHost.Contains('\\'))
    {
        return Results.BadRequest(new { error = "Invalid device host format" });
    }

    // Validate count parameter
    if (count < 1) count = 1;
    if (count > 1000) count = 1000;

    var results = await service.GetResultsForDeviceAsync(deviceHost, count);
    return Results.Ok(results);
});

// Client Speed Test API endpoints (for browser-based and iperf3 client tests)
app.MapPost("/api/speedtest/result", async (HttpContext context, ClientSpeedTestService service) =>
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

    // Get client IP (handle proxies)
    var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrEmpty(forwardedFor))
    {
        clientIp = forwardedFor.Split(',')[0].Trim();
    }

    var result = await service.RecordOpenSpeedTestResultAsync(
        clientIp, download, upload, ping, jitter, downloadData, uploadData, userAgent);

    return Results.Ok(new {
        success = true,
        id = result.Id,
        clientIp = result.ClientIp,
        clientName = result.ClientName,
        download = result.DownloadMbps,
        upload = result.UploadMbps
    });
});

app.MapGet("/api/speedtest/results", async (ClientSpeedTestService service, int count = 50) =>
{
    if (count < 1) count = 1;
    if (count > 1000) count = 1000;
    return Results.Ok(await service.GetResultsAsync(count));
});

app.MapGet("/api/speedtest/results/ip/{clientIp}", async (string clientIp, ClientSpeedTestService service, int count = 20) =>
{
    if (string.IsNullOrWhiteSpace(clientIp))
        return Results.BadRequest(new { error = "Invalid client IP" });

    if (count < 1) count = 1;
    if (count > 1000) count = 1000;

    return Results.Ok(await service.GetResultsByIpAsync(clientIp, count));
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
        Expires = DateTimeOffset.UtcNow.AddDays(1),
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

// Demo mode masking endpoint (returns mappings from DEMO_MODE_MAPPINGS env var)
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

app.Run();
