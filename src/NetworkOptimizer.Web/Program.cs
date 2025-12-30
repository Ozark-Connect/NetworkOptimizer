using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NetworkOptimizer.Web;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Audit;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<NetworkPathAnalyzer>();

// Register audit engine and analyzers
builder.Services.AddTransient<VlanAnalyzer>();
builder.Services.AddTransient<SecurityAuditEngine>();
builder.Services.AddTransient<FirewallRuleAnalyzer>();
builder.Services.AddTransient<AuditScorer>();
builder.Services.AddTransient<ConfigAuditEngine>();

// Register TC Monitor client (singleton - shared HTTP client)
builder.Services.AddSingleton<TcMonitorClient>();

// Register SQLite database context
// In Docker, use /app/data; otherwise use LocalApplicationData
var isDocker = string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);
var dbPath = isDocker
    ? "/app/data/network_optimizer.db"
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetworkOptimizer", "network_optimizer.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
builder.Services.AddDbContext<NetworkOptimizerDbContext>(options =>
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

// Register System Settings service (singleton - system-wide configuration)
builder.Services.AddSingleton<SystemSettingsService>();

// Register password hasher (singleton - stateless)
builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();

// Register Admin Auth service (scoped - depends on ISettingsRepository)
builder.Services.AddScoped<AdminAuthService>();

// Register JWT service (singleton - caches secret key)
builder.Services.AddSingleton<JwtService>();

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
builder.Services.AddSingleton<AuditService>(); // Singleton to persist dismissed alerts across refreshes
builder.Services.AddScoped<SqmService>();
builder.Services.AddScoped<SqmDeploymentService>();
builder.Services.AddScoped<AgentService>();

// Configure HTTP client for API calls
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("TcMonitor", client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});

// Add CORS for API endpoints (if needed for agents)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
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
            cmd.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name='{tableName}'";
            if (cmd.ExecuteScalar() != null)
            {
                // Table exists, mark migration as applied
                cmd.CommandText = $"INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('{migrationId}', '9.0.0')";
                cmd.ExecuteNonQuery();
            }
        }
    }
    conn.Close();

    // Apply any pending migrations (creates DB for new installs, or applies new migrations for existing)
    db.Database.Migrate();
}

// Pre-generate the credential encryption key
NetworkOptimizer.Storage.Services.CredentialProtectionService.EnsureKeyExists();

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

// Log admin auth startup configuration
using (var startupScope = app.Services.CreateScope())
{
    var adminAuthService = startupScope.ServiceProvider.GetRequiredService<AdminAuthService>();
    await adminAuthService.LogStartupConfigurationAsync();
}

// Configure JWT Bearer token validation parameters (requires JwtService from DI)
var jwtService = app.Services.GetRequiredService<JwtService>();
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
    var adminAuth = context.RequestServices.GetRequiredService<AdminAuthService>();
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
app.UseCors();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// API endpoints for agent metrics ingestion
app.MapPost("/api/metrics", async (HttpContext context) =>
{
    // TODO: Implement metrics ingestion from agents
    var metrics = await context.Request.ReadFromJsonAsync<Dictionary<string, object>>();
    // Store in InfluxDB or queue for processing
    return Results.Ok(new { status = "accepted" });
});

app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// Debug endpoint to see raw device JSON (temporary)
app.MapGet("/api/debug/devices", async (UniFiConnectionService connectionService) =>
{
    if (!connectionService.IsConnected || connectionService.Client == null)
        return Results.BadRequest(new { error = "Not connected to controller" });

    var rawJson = await connectionService.Client.GetDevicesRawJsonAsync();
    if (rawJson == null)
        return Results.NotFound(new { error = "Could not fetch devices" });

    return Results.Content(rawJson, "application/json");
});

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
    var results = await service.GetRecentResultsAsync(count);
    return Results.Ok(results);
});

app.MapGet("/api/iperf3/results/{deviceHost}", async (string deviceHost, Iperf3SpeedTestService service, int count = 20) =>
{
    var results = await service.GetResultsForDeviceAsync(deviceHost, count);
    return Results.Ok(results);
});

// Auth API endpoints
app.MapGet("/api/auth/set-cookie", (HttpContext context, string token, string returnUrl = "/") =>
{
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

app.MapGet("/api/auth/check", async (HttpContext context, JwtService jwt) =>
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

app.Run();
