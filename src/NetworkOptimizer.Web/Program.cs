using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Web;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Audit;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Storage.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Register UniFi connection service (singleton - maintains connection state)
builder.Services.AddSingleton<UniFiConnectionService>();

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

// Register UniFi SSH service (singleton - shared SSH credentials for all UniFi devices)
builder.Services.AddSingleton<UniFiSshService>();

// Register Cellular Modem service (singleton - maintains polling timer, uses UniFiSshService)
builder.Services.AddSingleton<CellularModemService>();

// Register iperf3 Speed Test service (singleton - tracks running tests, uses UniFiSshService)
builder.Services.AddSingleton<Iperf3SpeedTestService>();

// Register application services (scoped per request/circuit)
builder.Services.AddScoped<DashboardService>();
builder.Services.AddSingleton<AuditService>(); // Singleton to persist dismissed alerts across refreshes
builder.Services.AddScoped<SqmService>();
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

// Simple basic auth middleware (optional - set APP_PASSWORD env var to enable)
var appPassword = Environment.GetEnvironmentVariable("APP_PASSWORD");
if (!string.IsNullOrEmpty(appPassword))
{
    app.Use(async (context, next) =>
    {
        // Skip auth for health endpoint and static files
        if (context.Request.Path.StartsWithSegments("/api/health") ||
            context.Request.Path.StartsWithSegments("/downloads"))
        {
            await next();
            return;
        }

        // Check for basic auth header
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            var encoded = authHeader["Basic ".Length..].Trim();
            var credentials = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var parts = credentials.Split(':', 2);
            if (parts.Length == 2 && parts[1] == appPassword)
            {
                await next();
                return;
            }
        }

        // Check for session cookie (set after successful auth)
        if (context.Request.Cookies.TryGetValue("auth", out var cookie) && cookie == ComputeHash(appPassword))
        {
            await next();
            return;
        }

        // Return 401 with basic auth challenge
        context.Response.StatusCode = 401;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"Network Optimizer\"";
        await context.Response.WriteAsync("Unauthorized");
    });
}

static string ComputeHash(string input)
{
    var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
    return Convert.ToBase64String(bytes)[..16];
}

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

app.Run();
