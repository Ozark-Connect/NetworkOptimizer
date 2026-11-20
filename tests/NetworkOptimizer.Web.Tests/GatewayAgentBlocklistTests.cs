using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class GatewayAgentBlocklistTests
{
    private const string NotInPlace = "its rules are not in place";

    [Fact]
    public void OnStatus_SiteSentOff_NoHooks_DoesNotWarn()
    {
        var (blocklist, logger, agent) = Build(enabled: false);

        blocklist.OnStatus(agent, new GatewayBlocklistStatus { Fingerprint = "off", Ok = true, HookPresent = false });

        VerifyWarning(logger, NotInPlace, Times.Never());
    }

    [Fact]
    public void OnStatus_SiteSentOn_NoHooks_Warns()
    {
        var (blocklist, logger, agent) = Build(enabled: true);

        blocklist.OnStatus(agent, new GatewayBlocklistStatus { Fingerprint = "on", Ok = true, HookPresent = false });

        VerifyWarning(logger, NotInPlace, Times.Once());
    }

    [Fact]
    public void OnStatus_SiteSentOff_Failed_StillWarns()
    {
        var (blocklist, logger, agent) = Build(enabled: false);

        blocklist.OnStatus(agent, new GatewayBlocklistStatus { Fingerprint = "off", Ok = false, Error = "teardown failed" });

        VerifyWarning(logger, "failed", Times.Once());
    }

    private static (GatewayAgentBlocklist, Mock<ILogger<GatewayAgentBlocklist>>, AgentTunnelConnection) Build(bool enabled)
    {
        var registry = new AgentTunnelRegistry(new AgentTunnelOptions(Enabled: true, Port: 0));
        var logger = new Mock<ILogger<GatewayAgentBlocklist>>();
        var blocklist = new GatewayAgentBlocklist(registry, logger.Object, Mock.Of<IServiceProvider>());
        blocklist.Push("site", enabled, [], [], enabled ? "on" : "off");
        return (blocklist, logger, new AgentTunnelConnection(1, "site", "gateway") { OnGateway = true });
    }

    private static void VerifyWarning(Mock<ILogger<GatewayAgentBlocklist>> logger, string message, Times times) =>
        logger.Verify(log => log.Log(
            LogLevel.Warning, It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((value, _) => value.ToString()!.Contains(message)),
            It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), times);
}
