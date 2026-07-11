using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
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

// Proxy dial policy: hardcoded site-local fence (RFC1918 / IPv6 local) unless the
// operator pins an explicit CIDR list in agent.json, which fully replaces it. The
// policy is agent-owned on purpose - nothing the server sends over the tunnel can
// widen it. An invalid entry aborts startup: failing loud beats silently running
// with a partial pin.
var proxyPolicy = NetworkOptimizer.Core.Helpers.ProxyDialPolicy.SiteLocal;
if (config.ProxyAllowedCidrs != null)
{
    var pinned = NetworkOptimizer.Core.Helpers.ProxyDialPolicy.FromPinnedCidrs(config.ProxyAllowedCidrs, out var policyError);
    if (pinned == null)
    {
        Console.Error.WriteLine($"Invalid proxyAllowedCidrs in {configPath}: {policyError}");
        return 1;
    }
    proxyPolicy = pinned;
    Console.WriteLine($"Proxy dials pinned to {pinned.PinnedCount} operator-configured CIDR(s)");
}
else
{
    Console.WriteLine("Proxy dials restricted to site-local addresses (RFC1918 / IPv6 local)");
}

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

// The agent's primary LAN IPv4 on the site network. The central server points
// site clients at this address for LAN speed tests, since its own address is
// unreachable from a remote site's LAN. Reported on enrollment and each heartbeat.
// NO_AGENT_LAN_IP overrides auto-detection for deployments where it can't see the
// real LAN address (e.g. Docker bridge mode instead of host, or multi-NIC hosts).
var lanIpOverride = Environment.GetEnvironmentVariable("NO_AGENT_LAN_IP");
var lanIp = !string.IsNullOrWhiteSpace(lanIpOverride)
    ? lanIpOverride.Trim()
    : NetworkOptimizer.Core.Helpers.NetworkUtilities.DetectLocalIpFromInterfaces();

var handler = new HttpClientHandler();
if (config.IgnoreSslErrors)
    handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
// Short timeout: this client only carries enrollment and heartbeats. The
// default 100s meant a heartbeat against an offline server host (SYN black
// hole, e.g. host down for maintenance) hung for the full 100s per attempt.
using var http = new HttpClient(handler)
{
    BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/"),
    Timeout = TimeSpan.FromSeconds(15)
};

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

// systemd stops deliver SIGTERM to the whole control group: the agent itself AND
// its child processes (in-flight ping probes, iperf3). Without this handler the
// agent ignored SIGTERM, kept its loops and tunnel alive for systemd's full 90s
// stop timeout (then got SIGKILLed), and - worse - parsed its killed ping
// children as real packet loss and reported it upstream, planting a false loss
// spike on every agent stop/update. Cancelling here makes the shutdown immediate
// and flips the cancellation guards so in-flight probe results are discarded.
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    ctx.Cancel = true;
    cts.Cancel();
});

// LAN speed test (independent of the tunnel - site clients hit the agent
// directly). nginx serves the OpenSpeedTest page + the throughput-critical
// transfer legs on config.LanSpeedTestPort; this loopback relay only forwards
// result posts to the central server with the site slug.
SpeedTestServer? speedTestServer = null;
Task? iperf3Task = null;
if (config.LanSpeedTest)
{
    try
    {
        speedTestServer = SpeedTestServer.Create(config.ServerUrl, config.SiteSlug ?? "", config.IgnoreSslErrors);
        speedTestServer.Start();
        Console.WriteLine($"LAN speed test results relay on 127.0.0.1:{SpeedTestServer.RelayPort} (nginx fronts the page + transfer legs on port {config.LanSpeedTestPort})");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Speed test server failed to start: {ex.Message}");
    }
    // Relay client-initiated iperf3 results (captured off iperf3 -s -J) to the central server,
    // tagged with the site slug - the iperf3 analog of the OpenSpeedTest result relay above.
    iperf3Task = Iperf3Runner.RunAsync(
        speedTestServer is { } relayServer ? relayServer.PostIperf3ResultAsync : null, cts.Token);
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
        var proxyHandler = new ProxyHandler(tunnel, proxyPolicy);
        var iperf3ClientRunner = new Iperf3ClientRunner(tunnel);
        var uwnClientRunner = new UwnClientRunner(tunnel);
        var probeRequestRunner = new ProbeRequestRunner(tunnel);
        tunnel.OnProbeConfig = probeRunner.UpdateConfig;
        tunnel.OnSnmpConfig = snmpRunner.UpdateConfig;
        tunnel.OnWanSpeedTestConfig = wanConfig => speedTestServer?.UpdateWanServers(
            wanConfig.Servers.Select(s => new SpeedTestServer.WanServerEntry(s.ServerId, s.Url)).ToList(),
            wanConfig.DefaultServerId);
        tunnel.OnSnmpOidQuery = snmpRunner.HandleOidQueryAsync;
        tunnel.OnProxyOpen = proxyHandler.HandleOpenAsync;
        tunnel.OnProxyData = proxyHandler.HandleDataAsync;
        tunnel.OnProxyClose = proxyHandler.HandleClose;
        tunnel.OnIperf3Request = iperf3ClientRunner.HandleAsync;
        tunnel.OnUwnRequest = uwnClientRunner.HandleAsync;
        tunnel.OnProbeRequest = probeRequestRunner.HandleAsync;
        var probeTask = probeRunner.RunAsync(connectionCts.Token);
        var snmpTask = snmpRunner.RunAsync(connectionCts.Token);
        try
        {
            await tunnel.RunAsync(config.TunnelUrl, config.AgentKey!, version, lanIp, config.IgnoreSslErrors, cts.Token);
            Console.Error.WriteLine("Tunnel closed by server, reconnecting...");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
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
    // The when-guard matters: HttpClient's timeout surfaces as TaskCanceledException
    // (an OperationCanceledException), so an unguarded catch here mistook a timed-out
    // heartbeat for shutdown and exited the reconnect loop - permanently, since the
    // shutdown path then awaited the still-running iperf3 task. That zombied every
    // agent whose server host was powered off long enough to black-hole a heartbeat.
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
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
    /// <remarks>
    /// ProxyAllowedCidrs: operator pin for tunnel proxy dial targets (IPs or CIDRs).
    /// When present it fully replaces the built-in site-local fence - both the
    /// narrowing knob (pin to a management VLAN) and the only escape hatch for a
    /// public-IP target. Include every subnet holding the UniFi Console, gateway,
    /// devices used for SSH/speed tests, and modem/ONT/hotspot status pages.
    /// </remarks>
    public record AgentConfig(
        string ServerUrl,
        string? EnrollmentToken,
        string? AgentKey,
        string? SiteSlug,
        bool IgnoreSslErrors,
        string? TunnelUrl = null,
        string? ProbeSourceIp = null,
        bool LanSpeedTest = false,
        int LanSpeedTestPort = 3000,
        IReadOnlyList<string>? ProxyAllowedCidrs = null);

    public record EnrollmentResponse(string AgentKey, string SiteSlug, int? TunnelPort = null);

    public static class AgentJson
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
        public static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    }
}
