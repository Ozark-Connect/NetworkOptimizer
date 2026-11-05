using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Threats.Blocking;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Ssh;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.Ssh;

public class GatewayFirewallReaderTests
{
    private const string Output = """
        ###NO_UDAPI
        [{"config":{"name":"LAN_WAN"},"rules":[]}]

        ###NO_IPT4
        *filter
        -A INPUT -j NETOPT_BLOCK
        COMMIT
        ###NO_IPT6
        ###NO_IPSET
        Name: netopt_block_v4
        Number of entries: 3
        ###NO_END
        """;

    [Fact]
    public void Parse_SplitsEachSection_AndLeavesEmptyOnesNull()
    {
        var snapshot = GatewayFirewallReader.Parse(Output);

        snapshot.Should().NotBeNull();
        snapshot!.UdapiFilterJson.Should().StartWith("[{\"config\"");
        snapshot.Iptables.Should().Contain("-A INPUT -j NETOPT_BLOCK");
        snapshot.Ip6tables.Should().BeNull();
        snapshot.IpsetHeaders.Should().Contain("Number of entries: 3");
    }

    [Fact]
    public void Parse_ReadsCrlfOutput()
    {
        GatewayFirewallReader.Parse(Output.Replace("\n", "\r\n"))!.Iptables.Should().NotContain("\r\r");
    }

    [Fact]
    public void Parse_WithoutTheEndMarker_IsNull()
    {
        // A dropped session must never read as rules missing from the gateway.
        GatewayFirewallReader.Parse(Output.Replace("###NO_END", "")).Should().BeNull();
    }

    [Theory]
    [InlineData("PUT ")]
    [InlineData("POST ")]
    [InlineData("DELETE ")]
    [InlineData("INTERNAL ")]
    [InlineData("ipset list -n")]
    public void Command_OnlyReads(string forbidden)
    {
        GatewayFirewallReader.Command.Should().NotContain(forbidden);
        GatewayFirewallReader.Command.Should().Contain("GET /firewall/filter").And.Contain("ipset list -t");
    }

    private static Mock<IGatewaySshService> Ssh(bool awaitingTunnel = false, (bool, string)? run = null)
    {
        var ssh = new Mock<IGatewaySshService>();
        ssh.Setup(s => s.GetSettingsAsync(It.IsAny<bool>())).ReturnsAsync(
            new NetworkOptimizer.Storage.Models.GatewaySshSettings { Enabled = true, Host = "192.0.2.1", Password = "x" });
        ssh.Setup(s => s.IsAwaitingAgentTunnelAsync()).ReturnsAsync(awaitingTunnel);
        ssh.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(run ?? (true, Output));
        return ssh;
    }

    [Fact]
    public async Task ReadAsync_WhileTheAgentTunnelIsDown_SkipsWithoutRunning()
    {
        // Restarting the gateway's agent drops the tunnel; the next audit skips the check rather than failing.
        var ssh = Ssh(awaitingTunnel: true);

        (await new GatewayFirewallReader(ssh.Object, NullLogger.Instance).ReadAsync()).Should().BeNull();
        ssh.Verify(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReadAsync_WhenTheCommandFails_IsNull()
    {
        var ssh = Ssh(run: (false, "Connection refused"));
        (await new GatewayFirewallReader(ssh.Object, NullLogger.Instance).ReadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task ReadAsync_WithoutGatewaySsh_SkipsWithoutRunning()
    {
        var ssh = Ssh();
        ssh.Setup(s => s.GetSettingsAsync(It.IsAny<bool>())).ReturnsAsync(new NetworkOptimizer.Storage.Models.GatewaySshSettings { Enabled = false });

        (await new GatewayFirewallReader(ssh.Object, NullLogger.Instance).ReadAsync()).Should().BeNull();
        ssh.Verify(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReadAsync_ReturnsTheSnapshot()
    {
        (await new GatewayFirewallReader(Ssh().Object, NullLogger.Instance).ReadAsync())!.Iptables.Should().Contain("NETOPT_BLOCK");
    }

    private sealed class FakeBlocklist(GatewayAgentSent? sent) : IGatewayAgentBlocklist
    {
        public bool Push(string? site, bool enabled, IReadOnlyList<GatewayAgentEntry> entries, IReadOnlyList<GatewayAgentEntry> outbound,
            string fingerprint, IReadOnlyList<GatewayAgentEntry>? feeds = null, IReadOnlyList<BlockingException>? exceptions = null) => true;
        public bool IsEnforcing(string? site, string fingerprint) => true;
        public string? Problem(string? site, string fingerprint) => null;
        public bool HasGatewayAgent(string? site) => sent != null;
        public bool EnforcesExceptions(string? site) => sent?.FeedsSplit ?? true;
        public GatewayAgentSent? LastSent(string? site) => sent;
    }

    [Fact]
    public void Expectation_NullWithoutAGatewayAgent()
    {
        GatewayFirewallReader.Expectation(new FakeBlocklist(null), "site").Should().BeNull();
        GatewayFirewallReader.Expectation(null, "site").Should().BeNull();
    }

    [Fact]
    public void Expectation_CarriesTheAgentsChainAndSentCounts()
    {
        var expected = GatewayFirewallReader.Expectation(new FakeBlocklist(new GatewayAgentSent(true, 5, 1, 0, 0, 900, 40)), "site")!;

        expected.ChainName.Should().Be(GatewayBlocklistRules.ChainName);
        expected.CoreRulesV4.Should().Equal(GatewayBlocklistRules.CoreChain(true, v6: false));
        expected.SentEntries[GatewayBlocklistRules.BlockV4].Should().Be(5);
        expected.SentEntries[GatewayBlocklistRules.FeedV4].Should().Be(900);
    }

    [Fact]
    public void Expectation_WithoutFeedsSplit_ExpectsNoFeedSets()
    {
        var expected = GatewayFirewallReader.Expectation(new FakeBlocklist(new GatewayAgentSent(false, 5, 0, 0, 0, 0, 0)), "site")!;

        expected.SentEntries.Should().NotContainKey(GatewayBlocklistRules.FeedV4);
        expected.CoreRulesV4.Should().NotContain(r => r.Contains(GatewayBlocklistRules.FeedV4));
    }

    [Fact]
    public void GatewayWanPaths_PairsEachWanWithItsDataPath()
    {
        List<UniFiNetworkConfig> networks =
        [
            new() { Id = "wan1", Name = "Internet 1", Purpose = "wan", WanNetworkgroup = "WAN" },
            new() { Id = "wan2", Name = "Backup", Purpose = "wan", WanNetworkgroup = "WAN2" },
            new() { Id = "wan3", Name = "Unplugged", Purpose = "wan", WanNetworkgroup = "WAN3" },
            new() { Id = "lan", Name = "Home", Purpose = "corporate" },
        ];
        List<PrimaryWanInterfaces> interfaces = [new("WAN", "eth6", "eth6.7", "eth6"), new("WAN2", "eth4", null, "eth4")];

        AuditService.GatewayWanPaths(networks, interfaces).Should().BeEquivalentTo(
        [
            new NetworkOptimizer.Audit.Models.GatewayWanPath("wan1", "Internet 1", "eth6.7"),
            new NetworkOptimizer.Audit.Models.GatewayWanPath("wan2", "Backup", "eth4"),
        ]);
    }
}
