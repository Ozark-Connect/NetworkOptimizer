using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Ssh;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Ssh;

/// <summary>
/// The preconditions around the Smart Queues shaper read. Everything here is about NOT asking:
/// a site with nothing to check, or a gateway we cannot see, must cost no SSH and produce no
/// state - a finding raised from a failed read would accuse a healthy install.
/// </summary>
public class GatewayShaperProbeServiceTests
{
    private readonly Mock<ISqmService> _sqm = new();
    private readonly Mock<IGatewaySshService> _ssh = new();

    private GatewayShaperProbeService CreateService() =>
        new(_sqm.Object, _ssh.Object, NullLogger<GatewayShaperProbeService>.Instance);

    [Fact]
    public async Task RunAsync_NoWanWithSmartQueues_NeverTouchesSsh()
    {
        SetWans(CreateWan("Fiber", "eth6", smartqEnabled: false));

        var states = await CreateService().RunAsync();

        states.Should().BeEmpty();
        _ssh.Verify(s => s.GetSettingsAsync(It.IsAny<bool>()), Times.Never);
        _ssh.Verify(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_GatewaySshDisabled_ReturnsNothing()
    {
        SetWans(CreateWan("Fiber", "eth6"));
        SetSshSettings(enabled: false);

        var states = await CreateService().RunAsync();

        states.Should().BeEmpty();
        _ssh.Verify(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_NoCredentials_ReturnsNothing()
    {
        SetWans(CreateWan("Fiber", "eth6"));
        SetSshSettings(password: null);

        var states = await CreateService().RunAsync();

        states.Should().BeEmpty();
        _ssh.Verify(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_AgentTunnelNotUp_ReturnsNothing()
    {
        SetWans(CreateWan("Fiber", "eth6"));
        SetSshSettings();
        _ssh.Setup(s => s.IsAwaitingAgentTunnelAsync()).ReturnsAsync(true);

        var states = await CreateService().RunAsync();

        states.Should().BeEmpty();
        _ssh.Verify(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_CommandFails_ReturnsNothing()
    {
        SetWans(CreateWan("Fiber", "eth6"));
        SetSshSettings();
        SetCommandResult(success: false, output: "Connection refused");

        var states = await CreateService().RunAsync();

        states.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ReadsEveryEnabledWanInOneCommand()
    {
        SetWans(
            CreateWan("Fiber", "ppp0"),
            CreateWan("Cable", "eth7"),
            CreateWan("Backup", "eth8", smartqEnabled: false));
        SetSshSettings();

        string? issued = null;
        _ssh.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback<string, TimeSpan?, CancellationToken>((cmd, _, _) => issued = cmd)
            .ReturnsAsync(() => (true, """
                ###TC ppp0
                class htb 1:1 root rate 550Mbit ceil 550Mbit
                ###TC ifbppp0
                class htb 1:1 root rate 894Mbit ceil 894Mbit
                ###TC eth7
                class mq :1 root
                ###TC ifbeth7
                Cannot find device "ifbeth7"
                """));

        var states = await CreateService().RunAsync();

        _ssh.Verify(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Once);
        issued.Should().Contain("ppp0").And.Contain("ifbppp0").And.Contain("eth7").And.Contain("ifbeth7");
        issued.Should().NotContain("eth8");

        states.Should().HaveCount(2);
        states[0].WanName.Should().Be("Fiber");
        states[0].Egress.HasRootHtb.Should().BeTrue();
        states[1].WanName.Should().Be("Cable");
        states[1].Ingress.DeviceFound.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_UnusableInterfaceName_SkipsThatWan()
    {
        SetWans(CreateWan("Odd", "eth6; reboot"));
        SetSshSettings();

        var states = await CreateService().RunAsync();

        states.Should().BeEmpty();
        _ssh.Verify(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private void SetWans(params WanInterfaceInfo[] wans) =>
        _sqm.Setup(s => s.GetWanInterfacesFromControllerAsync()).ReturnsAsync(wans.ToList());

    private void SetSshSettings(bool enabled = true, string? host = "192.0.2.1", string? password = "secret") =>
        _ssh.Setup(s => s.GetSettingsAsync(It.IsAny<bool>())).ReturnsAsync(new GatewaySshSettings
        {
            Enabled = enabled,
            Host = host,
            Username = "root",
            Password = password
        });

    private void SetCommandResult(bool success, string output) =>
        _ssh.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((success, output));

    private static WanInterfaceInfo CreateWan(string name, string ifName, bool smartqEnabled = true) =>
        new()
        {
            Name = name,
            Interface = ifName,
            TcInterface = $"ifb{ifName}",
            SmartqEnabled = smartqEnabled,
            SmartqDownRateMbps = 900,
            SmartqUpRateMbps = 500
        };
}
