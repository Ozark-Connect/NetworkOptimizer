using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.OntProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.RoutedDevices;

/// <summary>
/// Device clients address a device by its own address and dial through the site's dialer, so every
/// connection - redirect hops included - is opened inside the device's network. Runs the real tunnel
/// proxy against a fake agent whose LAN is a map of addresses to local test servers.
/// </summary>
public class RoutedDeviceDialingTests
{
    private const string Slug = "lake-house";
    private const string Device = "192.0.2.1";

    private static ServiceProvider BuildServices(bool viaAgent)
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        if (viaAgent)
        {
            using var db = new NetworkOptimizerDbContext(options);
            db.SystemSettings.Add(new SystemSetting { Key = SiteTunnelRouting.DevicesViaAgentKey, Value = "true" });
            db.SaveChanges();
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => new NetworkOptimizerDbContext(options));
        services.AddScoped(_ => new SiteContextService(new HttpContextAccessor(), new SiteDatabasePaths("test.db")));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(new AgentTunnelRegistry(new AgentTunnelOptions(Enabled: true, Port: 0)));
        services.AddSingleton<SiteConnectionRegistry>();
        services.AddSingleton<AgentTunnelProxyService>();
        return services.BuildServiceProvider();
    }

    private static IDeviceDialer DialerFor(IServiceProvider services) =>
        new SiteTunnelRouting(services, new SiteAgentCoverage(services), NullLogger<SiteTunnelRouting>.Instance)
            .DialerFor(Slug);

    private static FakeSiteAgent Agent(IServiceProvider services, Dictionary<(string, int), int> lan) =>
        new(services.GetRequiredService<AgentTunnelRegistry>(), services.GetRequiredService<AgentTunnelProxyService>(), Slug, lan);

    private static HttpClient DeviceClient(IDeviceDialer dialer) =>
        new(new SocketsHttpHandler { ConnectCallback = DeviceHttp.Via(dialer) }) { Timeout = TimeSpan.FromSeconds(15) };

    [Fact]
    public async Task AgentSite_DeviceIsReachedAtItsOwnAddress_AndSeesThatAddress()
    {
        await using var services = BuildServices(viaAgent: true);
        await using var device = new FakeDeviceHttpServer(_ => FakeDeviceHttpServer.Ok("up"));
        await using var agent = Agent(services, new() { [(Device, 80)] = device.Port });
        using var client = DeviceClient(DialerFor(services));

        var body = await client.GetStringAsync($"http://{Device}/status");

        body.Should().Be("up");
        device.HostHeaders.Should().Equal(Device);
        agent.Opens.Should().Equal((Device, 80));
    }

    [Fact]
    public async Task AgentSite_RedirectNamingTheDevicesOwnAddressOnAnotherPort_IsFollowedInsideTheSite()
    {
        // The CM2050V's login redirect, and the shape of an HTTP-to-HTTPS upgrade.
        await using var services = BuildServices(viaAgent: true);
        await using var index = new FakeDeviceHttpServer(_ => FakeDeviceHttpServer.Ok("index"));
        await using var login = new FakeDeviceHttpServer(_ => FakeDeviceHttpServer.Redirect($"http://{Device}:8080/index.htm"));
        await using var agent = Agent(services, new() { [(Device, 80)] = login.Port, [(Device, 8080)] = index.Port });
        using var client = DeviceClient(DialerFor(services));

        var body = await client.GetStringAsync($"http://{Device}/goform/Login");

        body.Should().Be("index");
        agent.Opens.Should().Equal((Device, 80), (Device, 8080));
    }

    [Fact]
    public async Task AgentSite_RedirectToALoopbackPort_IsOpenedInsideTheSite_NotOnThisServer()
    {
        // A loopback port on this server could be another site's tunnel listener.
        await using var services = BuildServices(viaAgent: true);
        await using var thisServer = new FakeDeviceHttpServer(_ => FakeDeviceHttpServer.Ok("this server"));
        await using var device = new FakeDeviceHttpServer(_ => FakeDeviceHttpServer.Redirect($"http://127.0.0.1:{thisServer.Port}/"));
        await using var agent = Agent(services, new() { [(Device, 80)] = device.Port });
        using var client = DeviceClient(DialerFor(services));

        var act = () => client.GetStringAsync($"http://{Device}/");

        await act.Should().ThrowAsync<HttpRequestException>();
        thisServer.Accepted.Should().Be(0);
        agent.Opens.Should().Equal((Device, 80), ("127.0.0.1", thisServer.Port));
    }

    [Fact]
    public async Task AgentSite_Provider_FollowsTheDevicesRedirect_ToStats()
    {
        await using var services = BuildServices(viaAgent: true);
        await using var stats = new FakeDeviceHttpServer(_ =>
            FakeDeviceHttpServer.Ok("{\"ploam\":{\"curr_state\":5}}", "application/json"));
        await using var device = new FakeDeviceHttpServer(_ => FakeDeviceHttpServer.Redirect($"http://{Device}:10013/"));
        await using var agent = Agent(services, new() { [(Device, 10012)] = device.Port, [(Device, 10013)] = stats.Port });
        var provider = new NetOptCustomPonOntProvider(NullLogger<NetOptCustomPonOntProvider>.Instance);
        var context = new OntPollContext
        {
            Id = 1, SiteSlug = Slug, Name = "ONT", Host = Device, Port = 10012,
            Dialer = DialerFor(services),
        };

        var (success, message) = await provider.TestConnectionAsync(context);

        success.Should().BeTrue(message);
        stats.Accepted.Should().Be(1);
    }

    [Fact]
    public async Task AgentSite_WithNoAgentOnline_FailsRatherThanDialingFromThisServer()
    {
        await using var services = BuildServices(viaAgent: true);
        await using var thisServer = new FakeDeviceHttpServer(_ => FakeDeviceHttpServer.Ok("this server"));
        using var client = DeviceClient(DialerFor(services));

        var act = () => client.GetStringAsync($"http://127.0.0.1:{thisServer.Port}/");

        await act.Should().ThrowAsync<HttpRequestException>();
        thisServer.Accepted.Should().Be(0);
    }

    [Fact]
    public async Task TunnelStream_ClosedByTheCaller_ClosesTheAgentsConnection()
    {
        await using var services = BuildServices(viaAgent: true);
        await using var device = new FakeDeviceHttpServer(_ => FakeDeviceHttpServer.Ok("up"));
        await using var agent = Agent(services, new() { [(Device, 80)] = device.Port });

        var stream = await DialerFor(services).DialAsync(Device, 80, CancellationToken.None);
        await stream.DisposeAsync();

        await WaitUntilAsync(() => agent.ClosesFromServer == 1);
    }

    [Fact]
    public async Task TunnelOpen_CancelledByTheCaller_DoesNotTripTheSitesOpenBreaker()
    {
        // A disposed client or cancelled poll gives up on a pending open. That says nothing about the
        // tunnel, so the next dial on the site must still go through.
        await using var services = BuildServices(viaAgent: true);
        await using var device = new FakeDeviceHttpServer(_ => FakeDeviceHttpServer.Ok("up"));
        await using var agent = new FakeSiteAgent(
            services.GetRequiredService<AgentTunnelRegistry>(), services.GetRequiredService<AgentTunnelProxyService>(), Slug,
            new Dictionary<(string, int), int> { [(Device, 80)] = device.Port }, silent: new HashSet<(string, int)> { (Device, 81) });
        var dialer = DialerFor(services);

        // Cancel only once the agent holds the open, so it is the pending open that gets abandoned.
        using var giveUp = new CancellationTokenSource();
        var pending = dialer.DialAsync(Device, 81, giveUp.Token).AsTask();
        await WaitUntilAsync(() => agent.Opens.Contains((Device, 81)));
        giveUp.Cancel();
        await pending.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        await WaitUntilAsync(() => agent.ClosesFromServer == 1);

        using var client = DeviceClient(dialer);
        (await client.GetStringAsync($"http://{Device}/")).Should().Be("up");
    }

    [Fact]
    public async Task TunnelStream_ClosedByTheDevice_EndsTheCallersStream()
    {
        await using var services = BuildServices(viaAgent: true);
        var hangUp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        hangUp.Start();
        var hungUp = Task.Run(async () => (await hangUp.AcceptTcpClientAsync()).Dispose());
        await using var agent = Agent(services, new() { [(Device, 80)] = ((System.Net.IPEndPoint)hangUp.LocalEndpoint).Port });

        await using var stream = await DialerFor(services).DialAsync(Device, 80, CancellationToken.None);
        await hungUp;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var read = await stream.ReadAsync(new byte[16], timeout.Token);

        read.Should().Be(0);
        hangUp.Stop();
    }
    [Fact]
    public async Task DirectSite_DialsFromThisServer()
    {
        await using var services = BuildServices(viaAgent: false);
        await using var device = new FakeDeviceHttpServer(_ => FakeDeviceHttpServer.Ok("up"));
        using var client = DeviceClient(DialerFor(services));

        var body = await client.GetStringAsync($"http://127.0.0.1:{device.Port}/");

        body.Should().Be("up");
    }

    [Fact]
    public void EveryDeviceClient_TakesTheContextsDialerAsItsConnectCallback()
    {
        var servicesDir = Path.Combine(FindRepositoryRoot(), "src", "NetworkOptimizer.Web", "Services");
        var files = new[] { "CableModemProviders", "CellularModemProviders", "OntProviders", "StarlinkProviders" }
            .SelectMany(dir => Directory.GetFiles(Path.Combine(servicesDir, dir), "*.cs", SearchOption.AllDirectories))
            .ToList();
        files.Should().NotBeEmpty();

        var problems = new List<string>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var source = File.ReadAllText(file);

            if (source.Contains("new HttpClientHandler"))
                problems.Add($"{name}: HttpClientHandler takes no ConnectCallback; use SocketsHttpHandler");
            if (System.Text.RegularExpressions.Regex.IsMatch(source, @"new HttpClient\s*(\(\s*\)|\{)"))
                problems.Add($"{name}: an HttpClient on the default handler dials from this server");
            if (source.Contains("new TcpClient") || source.Contains("new Socket("))
                problems.Add($"{name}: open sockets with context.Dialer.DialAsync");

            if (source.Contains("DirectDeviceDialer"))
                problems.Add($"{name}: a provider dials through its context's dialer, never a direct one");
            if (source.Contains("GrpcChannel.ForAddress") && !source.Contains("HttpHandler = new SocketsHttpHandler"))
                problems.Add($"{name}: a gRPC channel needs a SocketsHttpHandler with the dialer");

            var handlers = System.Text.RegularExpressions.Regex.Matches(source, @"new SocketsHttpHandler\b").Count;
            var dialed = System.Text.RegularExpressions.Regex.Matches(source, @"new SocketsHttpHandler\s*\{\s*ConnectCallback = DeviceHttp\.Via\(").Count;
            if (dialed != handlers)
                problems.Add($"{name}: {handlers - dialed} SocketsHttpHandler(s) without ConnectCallback = DeviceHttp.Via(...) first");
        }

        problems.Should().BeEmpty();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met within 10 s.");
            await Task.Delay(20);
        }
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NetworkOptimizer.sln")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
