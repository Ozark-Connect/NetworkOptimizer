using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// A remote client (Tailscale, Teleport, a remote-user VPN, an address outside the site) is traced to the gateway, whose
/// MAC stands in as the path's destination. Backfilled as the client's MAC, it merged every remote client's history with
/// the gateway's. A local client's path still gives its own MAC, Client WAN Test paths (all marked external) included.
/// </summary>
public class ClientSpeedTestRemoteMacTests : IDisposable
{
    private const string GatewayMac = "aa:bb:cc:00:00:01";
    private const string ClientMac = "aa:bb:cc:00:00:99";

    private readonly SqliteConnection _connection;
    private readonly NetworkOptimizerDbContext _db;

    public ClientSpeedTestRemoteMacTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new NetworkOptimizerDbContext(new DbContextOptionsBuilder<NetworkOptimizerDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static NetworkHop Hop(HopType type, string? mac = null) => new() { Type = type, DeviceMac = mac ?? "" };

    private static NetworkPath RemoteThrough(HopType vpn) => new()
    {
        DestinationMac = GatewayMac,
        IsExternalPath = true,
        Hops = [Hop(vpn), Hop(HopType.Gateway, GatewayMac)],
    };

    [Theory]
    [InlineData(HopType.Tailscale)]
    [InlineData(HopType.Teleport)]
    [InlineData(HopType.Vpn)]
    public void A_remote_client_path_gives_no_mac(HopType vpn)
    {
        ClientSpeedTestService.BackfillableClientMac(RemoteThrough(vpn)).Should().BeNull();
    }

    [Fact]
    public void An_outside_address_traced_to_the_gateway_gives_no_mac()
    {
        var path = new NetworkPath
        {
            DestinationMac = GatewayMac,
            IsExternalPath = true,
            Hops = [Hop(HopType.Wan), Hop(HopType.Gateway, GatewayMac)],
        };

        ClientSpeedTestService.BackfillableClientMac(path).Should().BeNull();
    }

    [Fact]
    public void A_local_client_path_gives_its_mac()
    {
        var path = new NetworkPath
        {
            DestinationMac = ClientMac,
            Hops = [Hop(HopType.Client, ClientMac), Hop(HopType.Switch, "aa:bb:cc:00:00:02"), Hop(HopType.Gateway, GatewayMac)],
        };

        ClientSpeedTestService.BackfillableClientMac(path).Should().Be(ClientMac);
    }

    [Fact]
    public void A_local_client_wan_test_path_still_gives_its_mac()
    {
        // Every Client WAN Test path is marked external, local clients included.
        var path = new NetworkPath
        {
            DestinationMac = ClientMac,
            IsExternalPath = true,
            Hops = [Hop(HopType.Wan), Hop(HopType.Gateway, GatewayMac), Hop(HopType.Client, ClientMac)],
        };

        ClientSpeedTestService.BackfillableClientMac(path).Should().Be(ClientMac);
    }

    [Fact]
    public void A_gateway_run_test_to_the_gateway_itself_keeps_its_mac()
    {
        var path = new NetworkPath { DestinationMac = GatewayMac, Hops = [Hop(HopType.Gateway, GatewayMac)] };

        ClientSpeedTestService.BackfillableClientMac(path).Should().Be(GatewayMac);
    }

    private Iperf3Result Row(string host, string? mac, NetworkPath? path) => new()
    {
        DeviceHost = host,
        ClientMac = mac,
        Direction = SpeedTestDirection.BrowserToServer,
        Success = true,
        TestTime = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc),
        PathAnalysis = path == null ? null : new PathAnalysisResult { Path = path },
    };

    [Fact]
    public void High_score_history_by_mac_skips_old_remote_rows_that_carry_the_gateway_mac()
    {
        _db.Iperf3Results.AddRange(
            Row("100.64.5.6", GatewayMac, RemoteThrough(HopType.Tailscale)),
            Row("192.168.2.3", GatewayMac, RemoteThrough(HopType.Teleport)),
            Row("192.0.2.1", GatewayMac, new NetworkPath { DestinationMac = GatewayMac, Hops = [Hop(HopType.Gateway, GatewayMac)] }),
            Row("192.0.2.50", ClientMac, null));
        _db.SaveChanges();

        var byGatewayMac = ClientSpeedTestService.HighScoreHistory(
                _db.Iperf3Results.AsNoTracking(), SpeedTestDirection.BrowserToServer, excludeId: 0, ip: "192.0.2.77", lowerMac: GatewayMac)
            .Select(r => r.DeviceHost).ToList();

        byGatewayMac.Should().Equal("192.0.2.1");
    }

    [Fact]
    public void High_score_history_by_address_still_takes_a_remote_clients_own_rows()
    {
        _db.Iperf3Results.AddRange(
            Row("100.64.5.6", GatewayMac, RemoteThrough(HopType.Tailscale)),
            Row("100.64.5.7", GatewayMac, RemoteThrough(HopType.Tailscale)));
        _db.SaveChanges();

        ClientSpeedTestService.HighScoreHistory(
                _db.Iperf3Results.AsNoTracking(), SpeedTestDirection.BrowserToServer, excludeId: 0, ip: "100.64.5.6", lowerMac: null)
            .Select(r => r.DeviceHost).ToList()
            .Should().Equal("100.64.5.6");
    }
}
