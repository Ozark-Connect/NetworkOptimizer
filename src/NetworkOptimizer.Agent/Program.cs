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

// The agent refuses cleartext transport outright: serverUrl and tunnelUrl must
// be https. TLS is terminated by the reverse proxy fronting the central server,
// which re-encrypts to the tunnel listener's own self-signed TLS behind it;
// self-signed certificates work with ignoreSslErrors, but plain http never does
// - the tunnel carries SNMP credentials and proxied console traffic.
static bool IsHttpsUrl(string url) =>
    Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

if (!IsHttpsUrl(config.ServerUrl))
{
    Console.Error.WriteLine(
        $"Refusing non-HTTPS serverUrl '{config.ServerUrl}'. The agent only connects over HTTPS; " +
        "use the https:// address of the reverse proxy fronting the central server " +
        "(self-signed certificates: set \"ignoreSslErrors\": true).");
    return 1;
}
if (!string.IsNullOrEmpty(config.TunnelUrl) && !IsHttpsUrl(config.TunnelUrl))
{
    Console.Error.WriteLine(
        $"Refusing non-HTTPS tunnelUrl '{config.TunnelUrl}'. Publish the agent tunnel through the " +
        "gRPC-capable reverse proxy (see the agent README) and use its https:// address.");
    return 1;
}

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

// The agent's primary LAN IPv4 on the site network. The central server points
// site clients at this address for LAN speed tests, since its own address is
// unreachable from a remote site's LAN. Reported on enrollment and each heartbeat.
var lanIp = NetworkOptimizer.Core.Helpers.NetworkUtilities.DetectLocalIpFromInterfaces();

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
        new { token = config.EnrollmentToken, version, lanIp }, AgentJson.Options);
    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"Enrollment failed: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return 1;
    }

    var enrollment = await response.Content.ReadFromJsonAsync<EnrollmentResponse>(AgentJson.Options)
        ?? throw new InvalidOperationException("Empty enrollment response");
    // The server's dedicated tunnel port isn't publicly addressable on its own,
    // and the agent only speaks HTTPS to the reverse proxy - so the tunnel
    // address is never derived automatically. It must be pinned explicitly to
    // the https address the reverse proxy publishes for the tunnel; until then
    // the agent stays on REST heartbeats.
    if (config.TunnelUrl == null && enrollment.TunnelPort is int tunnelPort)
    {
        Console.WriteLine(
            $"The server offers an agent tunnel (port {tunnelPort}), but the agent connects over HTTPS only. " +
            "Publish the tunnel through the gRPC-capable reverse proxy (see the agent README) and set " +
            "\"tunnelUrl\" to its https:// address in agent.json.");
    }
    config = config with { AgentKey = enrollment.AgentKey, SiteSlug = enrollment.SiteSlug, EnrollmentToken = null };
    File.WriteAllText(configPath, JsonSerializer.Serialize(config, AgentJson.WriteOptions));
    Console.WriteLine($"Enrolled for site '{config.SiteSlug}'. Agent key saved to {configPath}.");
}

Console.WriteLine($"Agent v{version} running for site '{config.SiteSlug}' against {config.ServerUrl}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// LAN speed test serving (independent of the tunnel - site clients hit the
// agent directly; results relay to the central server with the site slug).
SpeedTestServer? speedTestServer = null;
Task? iperf3Task = null;
if (config.LanSpeedTest)
{
    try
    {
        speedTestServer = SpeedTestServer.Create(config.ServerUrl, config.SiteSlug ?? "", config.LanSpeedTestPort, config.IgnoreSslErrors);
        speedTestServer.Start();
        Console.WriteLine($"LAN speed test page listening on port {config.LanSpeedTestPort}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Speed test server failed to start: {ex.Message}");
    }
    iperf3Task = Iperf3Runner.RunAsync(cts.Token);
}

// Prefer the persistent gRPC tunnel; REST heartbeats keep the agent visible as
// Online whenever the tunnel is unavailable (tunnel disabled server-side, an
// older server, or a network drop between reconnect attempts).
while (!cts.IsCancellationRequested)
{
    if (!string.IsNullOrEmpty(config.TunnelUrl))
    {
        // The probe runner lives and dies with its tunnel connection: the
        // server re-pushes probe config on every connect, and results have
        // nowhere to go while the tunnel is down.
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var tunnel = new TunnelClient();
        var probeRunner = new ProbeRunner(tunnel.TrySend, config.ProbeSourceIp);
        var snmpRunner = new SnmpRunner(tunnel);
        var proxyHandler = new ProxyHandler(tunnel);
        var iperf3ClientRunner = new Iperf3ClientRunner(tunnel);
        var uwnClientRunner = new UwnClientRunner(tunnel);
        tunnel.OnProbeConfig = probeRunner.UpdateConfig;
        tunnel.OnSnmpConfig = snmpRunner.UpdateConfig;
        tunnel.OnProxyOpen = proxyHandler.HandleOpenAsync;
        tunnel.OnProxyData = proxyHandler.HandleDataAsync;
        tunnel.OnProxyClose = proxyHandler.HandleClose;
        tunnel.OnIperf3Request = iperf3ClientRunner.HandleAsync;
        tunnel.OnUwnRequest = uwnClientRunner.HandleAsync;
        var probeTask = probeRunner.RunAsync(connectionCts.Token);
        var snmpTask = snmpRunner.RunAsync(connectionCts.Token);
        try
        {
            await tunnel.RunAsync(config.TunnelUrl, config.AgentKey!, version, lanIp, config.IgnoreSslErrors, cts.Token);
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
        finally
        {
            connectionCts.Cancel();
            try { await Task.WhenAll(probeTask, snmpTask); } catch (OperationCanceledException) { }
        }
    }

    try
    {
        var response = await http.PostAsJsonAsync("api/public/agents/heartbeats",
            new { agentKey = config.AgentKey, version, lanIp }, AgentJson.Options, cts.Token);
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

if (speedTestServer != null)
    await speedTestServer.DisposeAsync();
if (iperf3Task != null)
{
    try { await iperf3Task; } catch (OperationCanceledException) { }
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
        string? TunnelUrl = null,
        string? ProbeSourceIp = null,
        bool LanSpeedTest = false,
        int LanSpeedTestPort = 3000);

    public record EnrollmentResponse(string AgentKey, string SiteSlug, int? TunnelPort = null);

    public static class AgentJson
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
        public static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    }
}
