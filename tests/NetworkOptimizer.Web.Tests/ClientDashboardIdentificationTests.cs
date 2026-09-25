using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Ssh;
using NetworkOptimizer.Web.Services.WiredPortPresence;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class ClientDashboardIdentificationTests
{
    private const string Owner = "00:11:22:33:44:55";
    private const string Previous = "00:11:22:33:44:66";
    private const string Ip = "192.0.2.42";

    [Fact]
    public async Task V2OnlyAddressIdentifiesOnlineIpv4Client()
    {
        using var fixture = new Fixture();
        fixture.Api.Active.Add(Detail(Owner, Ip, "Current owner"));

        var identity = await fixture.Service.IdentifyClientAsync(Ip);

        Assert.NotNull(identity);
        Assert.Equal(Owner, identity.Mac);
        Assert.Equal(Ip, identity.Ip);
        Assert.Equal("Current owner", identity.Name);
        Assert.False(identity.IsOffline);
    }

    [Fact]
    public async Task ExpandedIpv6JoinsCanonicalNeighborAndCachesGatewayReadAcrossPolls()
    {
        using var fixture = new Fixture();
        fixture.Api.Active.Add(Detail(Owner, "192.0.2.10", "IPv6 device"));
        fixture.Ssh.Setup(s => s.RunCommandAsync("ip -6 neigh show", It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "fd00::42 dev br0 lladdr 00:11:22:33:44:55 REACHABLE"));
        const string expanded = "fd00:0000:0000:0000:0000:0000:0000:0042";

        var first = await fixture.Service.IdentifyClientAsync(expanded);
        var second = await fixture.Service.IdentifyClientAsync(expanded);

        Assert.Equal(Owner, first?.Mac);
        Assert.Equal(Owner, second?.Mac);
        Assert.Equal(IPAddress.Parse(expanded).ToString(), first?.Ip);
        fixture.Ssh.Verify(s => s.RunCommandAsync("ip -6 neigh show", It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CurrentV2OwnerBeatsAnotherClientsLastIpAndHistory()
    {
        using var fixture = new Fixture();
        fixture.Api.Stats.Add(new UniFiClientResponse { Mac = Previous, LastIp = Ip, Name = "Old owner" });
        fixture.Api.Active.Add(Detail(Owner, Ip, "New owner"));
        fixture.Api.History.Add(Detail(Previous, null, "Old owner", lastIp: Ip));

        var identity = await fixture.Service.IdentifyClientAsync(Ip);

        Assert.NotNull(identity);
        Assert.Equal(Owner, identity.Mac);
        Assert.Equal("New owner", identity.Name);
        Assert.False(identity.IsOffline);
    }

    [Fact]
    public async Task CachedMacIsRecheckedWhenCurrentAddressMoves()
    {
        using var fixture = new Fixture();
        fixture.Api.Stats.Add(new UniFiClientResponse { Mac = Previous, Ip = Ip, Name = "First" });
        Assert.Equal(Previous, (await fixture.Service.IdentifyClientAsync(Ip))?.Mac);

        fixture.Api.Stats[0].Ip = "192.0.2.99";
        fixture.Api.Active.Add(Detail(Owner, Ip, "Second"));
        fixture.Api.History.Add(Detail(Previous, null, "First", lastIp: Ip));

        var identity = await fixture.Service.IdentifyClientAsync(Ip);

        Assert.NotNull(identity);
        Assert.Equal(Owner, identity.Mac);
        Assert.Contains(fixture.Api.Requests, p => p.EndsWith($"/stat/sta/{Previous}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task V2AddressJoinsAddresslessStatStaAndRetainsSignalAndAp()
    {
        using var fixture = new Fixture();
        const string apMac = "aa:bb:cc:dd:ee:ff";
        fixture.Api.Stats.Add(new UniFiClientResponse
        {
            Mac = Owner, Ip = "", Name = "Raw name", Signal = -57, Channel = 149,
            ApMac = apMac, Network = "Trusted"
        });
        fixture.Api.Active.Add(Detail(Owner, Ip, "Console name"));

        var identity = await fixture.Service.IdentifyClientAsync(Ip);

        Assert.NotNull(identity);
        Assert.Equal(Owner, identity.Mac);
        Assert.Equal("Console name", identity.Name);
        Assert.Equal(-57, identity.SignalDbm);
        Assert.Equal(149, identity.Channel);
        Assert.Equal(apMac, identity.ApMac);
        Assert.Equal("Trusted", identity.NetworkName);
    }

    [Fact]
    public async Task V2AddressJoinedToWiredStatStaHonorsLinkDown()
    {
        using var fixture = new Fixture();
        fixture.Api.Stats.Add(new UniFiClientResponse
        {
            Mac = Owner, IsWired = true, SwMac = "aa:bb:cc:dd:ee:ff", SwPort = 8
        });
        fixture.Api.Active.Add(Detail(Owner, Ip, "Wired", wired: true));
        fixture.Ports.Setup(p => p.ListLinkDownAsync())
            .ReturnsAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Owner });

        var identity = await fixture.Service.IdentifyClientAsync(Ip);

        Assert.NotNull(identity);
        Assert.Equal(Owner, identity.Mac);
        Assert.True(identity.IsOffline);
        Assert.Equal(8, identity.SwitchPort);
    }

    [Fact]
    public async Task V2OnlyWiredClientHonorsLinkDown()
    {
        using var fixture = new Fixture();
        fixture.Api.Active.Add(Detail(Owner, Ip, "V2 wired", wired: true));
        fixture.Ports.Setup(p => p.ListLinkDownAsync())
            .ReturnsAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Owner });

        var identity = await fixture.Service.IdentifyClientAsync(Ip);

        Assert.NotNull(identity);
        Assert.Equal(Owner, identity.Mac);
        Assert.True(identity.IsWired);
        Assert.True(identity.IsOffline);
    }

    [Fact]
    public async Task OfflineCacheDoesNotResurrectMacNowAtAnotherCurrentAddress()
    {
        using var fixture = new Fixture();
        fixture.Api.History.Add(Detail(Previous, null, "Departed", lastIp: Ip));
        var old = await fixture.Service.IdentifyClientAsync(Ip);
        Assert.NotNull(old);
        Assert.True(old.IsOffline);

        fixture.Api.Stats.Add(new UniFiClientResponse { Mac = Previous, Ip = "192.0.2.99", LastIp = Ip });

        Assert.Null(await fixture.Service.IdentifyClientAsync(Ip));
    }

    [Fact]
    public async Task UnavailableV2StillUsesLegacyAddressAndOfflineHistory()
    {
        using var fixture = new Fixture();
        fixture.Api.ThrowActive = true;
        fixture.Api.Stats.Add(new UniFiClientResponse { Mac = Owner, LastIp = Ip, Name = "Legacy" });
        fixture.Api.History.Add(Detail(Previous, null, "Departed", lastIp: "192.0.2.88"));

        var legacy = await fixture.Service.IdentifyClientAsync(Ip);
        var offline = await fixture.Service.IdentifyClientAsync("192.0.2.88");

        Assert.NotNull(legacy);
        Assert.Equal(Owner, legacy.Mac);
        Assert.False(legacy.IsOffline);
        Assert.NotNull(offline);
        Assert.Equal(Previous, offline.Mac);
        Assert.True(offline.IsOffline);
    }

    [Fact]
    public async Task FailedNeighborLookupStillFallsThroughToIpv6History()
    {
        using var fixture = new Fixture();
        const string ipv6 = "fd00::88";
        fixture.Api.Active.Add(Detail(Owner, "192.0.2.10", "Other active"));
        fixture.Api.History.Add(Detail(Previous, null, "Historical", lastIp: ipv6));
        fixture.Ssh.Setup(s => s.RunCommandAsync("ip -6 neigh show", It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "unavailable"));

        var identity = await fixture.Service.IdentifyClientAsync(ipv6);

        Assert.NotNull(identity);
        Assert.Equal(Previous, identity.Mac);
        Assert.True(identity.IsOffline);
    }

    private static UniFiClientDetailResponse Detail(string mac, string? ip, string name,
        string? lastIp = null, bool wired = false) => new()
    {
        Mac = mac, Ip = ip, LastIp = lastIp, DisplayName = name,
        Status = "online", Type = wired ? "WIRED" : "WIRELESS"
    };

    private sealed class Fixture : IDisposable
    {
        public FakeApi Api { get; } = new();
        public Mock<IGatewaySshService> Ssh { get; } = new(MockBehavior.Strict);
        public Mock<IWiredPortPresenceService> Ports { get; } = new(MockBehavior.Strict);
        public ClientDashboardService Service { get; }
        private readonly UniFiApiClient _client;

        public Fixture()
        {
            Ssh.Setup(s => s.RunCommandAsync("ip -6 neigh show", It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((false, ""));
            Ports.Setup(p => p.ListLinkDownAsync())
                .ReturnsAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            _client = new UniFiApiClient(NullLogger<UniFiApiClient>.Instance,
                "https://console.invalid", "", "", apiKey: "fixture-key");
            var oldTransport = (HttpClient?)GetField(_client, "_httpClient");
            SetField(_client, "_httpClient", new HttpClient(Api));
            oldTransport?.Dispose();
            SetField(_client, "_isAuthenticated", true);

            // The concrete connection starts a background initialization in its constructor.
            // Only its already-connected read path is needed by IdentifyClientAsync.
            var connection = (UniFiConnectionService)RuntimeHelpers.GetUninitializedObject(typeof(UniFiConnectionService));
            SetField(connection, "_logger", NullLogger<UniFiConnectionService>.Instance);
            SetField(connection, "_client", _client);
            SetField(connection, "_isConnected", true);
            SetField(connection, "_cachedDevices", new List<DiscoveredDevice>());
            SetField(connection, "_deviceCacheTime", DateTime.UtcNow);

            var site = new SiteContextService(new HttpContextAccessor(), new SiteDatabasePaths("/tmp/test.sqlite"));
            site.OverrideSite("default");
            var registry = new SpeedTestServiceRegistry(null!, null!);
            var instances = (ConcurrentDictionary<string, SpeedTestServiceRegistry.SiteSpeedTestServices>)GetField(registry, "_instances")!;
            instances[site.Slug] = new(null!, null!, null!, null!, null!, null!);
            Service = new ClientDashboardService(NullLogger<ClientDashboardService>.Instance,
                null!, connection, registry, new ConfigurationBuilder().Build(), null!, site,
                new MonitoringLiveStatsRegistry(null!), Ssh.Object, portPresence: Ports.Object);
        }

        public void Dispose() => _client.Dispose();
    }

    private sealed class FakeApi : HttpMessageHandler
    {
        public List<UniFiClientResponse> Stats { get; } = new();
        public List<UniFiClientDetailResponse> Active { get; } = new();
        public List<UniFiClientDetailResponse> History { get; } = new();
        public List<string> Requests { get; } = new();
        public bool ThrowActive { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? throw new InvalidOperationException("Missing request URI");
            Requests.Add(path);
            object body;
            if (path == "/api/s/default/stat/sta")
                body = new { meta = new { rc = "ok" }, data = Stats };
            else if (path.StartsWith("/api/s/default/stat/sta/", StringComparison.Ordinal))
                body = new { meta = new { rc = "ok" }, data = Stats.Where(c => path.EndsWith(c.Mac, StringComparison.OrdinalIgnoreCase)).ToList() };
            else if (path == "/v2/api/site/default/clients/active")
            {
                if (ThrowActive) throw new InvalidOperationException("v2 unavailable");
                body = Active;
            }
            else if (path == "/v2/api/site/default/clients/history?withinHours=720")
                body = History;
            else if (path.StartsWith("/v2/api/site/default/wifiman/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            else
                throw new InvalidOperationException($"Unexpected console route: {path}");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            });
        }
    }

    private static object? GetField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
