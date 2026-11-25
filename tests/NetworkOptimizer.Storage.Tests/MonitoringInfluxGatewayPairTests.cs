using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>
/// A Shadow Mode pair's MACs read as one gateway: the device_mac predicate matches every member and
/// merges their series. Any other MAC, and a site with no pair, keep the single-MAC query.
/// </summary>
public class MonitoringInfluxGatewayPairTests
{
    private const string A = "aa:bb:cc:00:00:01";
    private const string B = "aa:bb:cc:00:00:02";

    private static MonitoringInfluxClient Client()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new MonitoringInfluxClient(new Factory(options), new NoopCredentials(), NullLogger<MonitoringInfluxClient>.Instance);
    }

    [Fact]
    public void NoPair_SingleMacPredicate_NoRegroup()
    {
        var (filter, regroup) = Client().DeviceMacFilter("AA-BB-CC-00-00-01", "_field");

        filter.Should().Be($"r.device_mac == \"{A}\"");
        regroup.Should().BeEmpty();
    }

    [Fact]
    public void PairMember_MatchesEveryMember_AndRegroups()
    {
        var client = Client();
        client.SetGatewayPairMacs([A, "AA:BB:CC:00:00:02"]);

        var (filter, regroup) = client.DeviceMacFilter(B, "if_name", "_field");

        filter.Should().Be($"r.device_mac == \"{A}\" or r.device_mac == \"{B}\"");
        regroup.Should().Contain("group(columns: [\"if_name\", \"_field\"])").And.Contain("sort(columns: [\"_time\"])");
    }

    [Fact]
    public void OtherMac_OnAPairSite_Unchanged()
    {
        var client = Client();
        client.SetGatewayPairMacs([A, B]);

        client.DeviceMacFilter("aa:bb:cc:00:00:09", "_field").Regroup.Should().BeEmpty();
    }

    [Fact]
    public void OneMac_IsNotAPair()
    {
        var client = Client();
        client.SetGatewayPairMacs([A]);

        client.GatewayPairMacs.Should().BeEmpty();
        client.DeviceMacFilter(A, "_field").Regroup.Should().BeEmpty();
    }

    private sealed class Factory(DbContextOptions<NetworkOptimizerDbContext> options) : IDbContextFactory<NetworkOptimizerDbContext>
    {
        public NetworkOptimizerDbContext CreateDbContext() => new(options);
    }

    private sealed class NoopCredentials : ICredentialProtectionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string encrypted) => encrypted;
        public bool IsEncrypted(string? value) => false;
        public void EnsureKeyExists() { }
    }
}
