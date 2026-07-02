using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using NetworkOptimizer.Agent;

// On-site agent skeleton: enrolls against the central Network Optimizer server
// with a one-time token, persists its agent key and site slug, then heartbeats.
// Monitoring, speed test serving, and the gRPC tunnel build on this loop.

var configPath = args.Length > 0
    ? args[0]
    : Environment.GetEnvironmentVariable("NO_AGENT_CONFIG") ?? "agent.json";

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Config not found: {configPath}");
    Console.Error.WriteLine("""
        Create agent.json:
        {
          "serverUrl": "https://your-network-optimizer:8042",
          "enrollmentToken": "noa_...",
          "ignoreSslErrors": false
        }
        Generate the enrollment token in Settings > Multi-Site > Agents.
        """);
    return 1;
}

var config = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(configPath), AgentJson.Options)
    ?? throw new InvalidOperationException("Invalid agent config");
if (string.IsNullOrWhiteSpace(config.ServerUrl))
{
    Console.Error.WriteLine("serverUrl is required");
    return 1;
}

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

var handler = new HttpClientHandler();
if (config.IgnoreSslErrors)
    handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
using var http = new HttpClient(handler) { BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/") };

// Enroll once: exchange the one-time token for a persistent agent key
if (string.IsNullOrEmpty(config.AgentKey))
{
    if (string.IsNullOrWhiteSpace(config.EnrollmentToken))
    {
        Console.Error.WriteLine("No agentKey and no enrollmentToken in config - nothing to do");
        return 1;
    }

    Console.WriteLine("Enrolling with central server...");
    var response = await http.PostAsJsonAsync("api/public/agents/enrollments",
        new { token = config.EnrollmentToken, version }, AgentJson.Options);
    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"Enrollment failed: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return 1;
    }

    var enrollment = await response.Content.ReadFromJsonAsync<EnrollmentResponse>(AgentJson.Options)
        ?? throw new InvalidOperationException("Empty enrollment response");
    // The tunnel is a dedicated cleartext HTTP/2 port on the same host as the
    // web UI, so derive its address from the server URL unless the config
    // already pins one explicitly.
    var tunnelUrl = config.TunnelUrl;
    if (tunnelUrl == null && enrollment.TunnelPort is int tunnelPort)
        tunnelUrl = $"http://{new Uri(config.ServerUrl).Host}:{tunnelPort}";
    config = config with { AgentKey = enrollment.AgentKey, SiteSlug = enrollment.SiteSlug, EnrollmentToken = null, TunnelUrl = tunnelUrl };
    File.WriteAllText(configPath, JsonSerializer.Serialize(config, AgentJson.WriteOptions));
    Console.WriteLine($"Enrolled for site '{config.SiteSlug}'. Agent key saved to {configPath}.");
}

Console.WriteLine($"Agent v{version} running for site '{config.SiteSlug}' against {config.ServerUrl}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Prefer the persistent gRPC tunnel; REST heartbeats keep the agent visible as
// Online whenever the tunnel is unavailable (tunnel disabled server-side, an
// older server, or a network drop between reconnect attempts).
while (!cts.IsCancellationRequested)
{
    if (!string.IsNullOrEmpty(config.TunnelUrl))
    {
        try
        {
            var tunnel = new TunnelClient();
            await tunnel.RunAsync(config.TunnelUrl, config.AgentKey!, version, config.IgnoreSslErrors, cts.Token);
            Console.Error.WriteLine("Tunnel closed by server, reconnecting...");
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Tunnel error: {ex.Message}");
        }
    }

    try
    {
        var response = await http.PostAsJsonAsync("api/public/agents/heartbeats",
            new { agentKey = config.AgentKey, version }, AgentJson.Options, cts.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            Console.Error.WriteLine("Heartbeat rejected: agent disabled or key revoked");
        else if (!response.IsSuccessStatusCode)
            Console.Error.WriteLine($"Heartbeat failed: {response.StatusCode}");
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Heartbeat error: {ex.Message}");
    }

    try { await Task.Delay(TimeSpan.FromSeconds(30), cts.Token); }
    catch (OperationCanceledException) { break; }
}

Console.WriteLine("Agent stopped");
return 0;

namespace NetworkOptimizer.Agent
{
    /// <summary>Agent configuration file contents (agent.json).</summary>
    public record AgentConfig(
        string ServerUrl,
        string? EnrollmentToken,
        string? AgentKey,
        string? SiteSlug,
        bool IgnoreSslErrors,
        string? TunnelUrl = null);

    public record EnrollmentResponse(string AgentKey, string SiteSlug, int? TunnelPort = null);

    public static class AgentJson
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
        public static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    }
}
