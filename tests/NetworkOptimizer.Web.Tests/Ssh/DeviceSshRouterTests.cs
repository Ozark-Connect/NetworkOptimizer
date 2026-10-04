using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Ssh;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Ssh;

public class DeviceSshRouterTests
{
    private const string Host = "192.0.2.20";
    private const string Mac = "aa:bb:cc:dd:ee:01";
    private const string Refused = "Authentication failed: Permission denied (password).";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Mock<IUniFiSshService> _device = new();
    private readonly Mock<IGatewaySshService> _gateway = new();
    private readonly FakeStore _store = new();
    private readonly List<DiscoveredDevice> _devices = new();

    public DeviceSshRouterTests()
    {
        _gateway.Setup(g => g.GetSettingsAsync(It.IsAny<bool>()))
            .ReturnsAsync(new GatewaySshSettings { Enabled = true, Username = "root", Password = "encrypted" });
    }

    private DeviceSshRouter Router() => new(
        _device.Object, _gateway.Object, _store,
        _ => Task.FromResult<IReadOnlyList<DiscoveredDevice>>(_devices),
        c => Task.FromResult<SshConnectionInfo?>(c),
        p => p,
        NullLogger.Instance);

    private static DeviceSshTarget ApModeUx7 => new(Mac, Host, DeviceType.AccessPoint, DeviceType.Gateway);
    private static DeviceSshTarget PlainAp => new(Mac, Host, DeviceType.AccessPoint, DeviceType.AccessPoint);

    private void DeviceCredentialsReturn(bool success, string output) =>
        _device.Setup(d => d.RunCommandAsync(Host, "cmd", null, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((success, output));

    private void GatewayCredentialsReturn(bool success, string output) =>
        _device.Setup(d => d.RunCommandAsync(Host, "cmd", "root", "encrypted", It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((success, output));

    private void VerifyGatewayCredentialsTried(Times times) =>
        _device.Verify(d => d.RunCommandAsync(Host, "cmd", "root", "encrypted", It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task TheSiteGateway_goes_through_the_gateway_service()
    {
        _gateway.Setup(g => g.RunCommandAsync("cmd", Timeout, It.IsAny<CancellationToken>())).ReturnsAsync((true, "ok"));

        var result = await Router().RunAsync(new DeviceSshTarget(Mac, Host, DeviceType.Gateway, DeviceType.Gateway), "cmd", Timeout);

        result.Should().Be((true, "ok"));
        _device.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task APlainAccessPoint_never_falls_back()
    {
        DeviceCredentialsReturn(false, Refused);

        var result = await Router().RunAsync(PlainAp, "cmd", Timeout);

        result.Should().Be((false, Refused));
        VerifyGatewayCredentialsTried(Times.Never());
        (await _store.UsesGatewayCredentialsAsync(Mac)).Should().BeFalse();
    }

    [Fact]
    public async Task AnApModeUx7_refusing_Device_SSH_falls_back_and_remembers()
    {
        DeviceCredentialsReturn(false, Refused);
        GatewayCredentialsReturn(true, "ok");

        var result = await Router().RunAsync(ApModeUx7, "cmd", Timeout);

        result.Should().Be((true, "ok"));
        (await _store.UsesGatewayCredentialsAsync(Mac)).Should().BeTrue();
    }

    [Fact]
    public async Task ARememberedRoute_skips_Device_SSH()
    {
        await _store.SetUsesGatewayCredentialsAsync(Mac, true);
        GatewayCredentialsReturn(true, "ok");

        var result = await Router().RunAsync(ApModeUx7, "cmd", Timeout);

        result.Should().Be((true, "ok"));
        _device.Verify(d => d.RunCommandAsync(Host, "cmd", null, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task ARememberedRoute_whose_Gateway_SSH_is_refused_goes_back_to_Device_SSH()
    {
        await _store.SetUsesGatewayCredentialsAsync(Mac, true);
        GatewayCredentialsReturn(false, Refused);
        DeviceCredentialsReturn(true, "ok");

        var result = await Router().RunAsync(ApModeUx7, "cmd", Timeout);

        result.Should().Be((true, "ok"));
        (await _store.UsesGatewayCredentialsAsync(Mac)).Should().BeFalse();
    }

    [Fact]
    public async Task AnUnreachableApModeUx7_does_not_fall_back()
    {
        DeviceCredentialsReturn(false, "Connection failed: No route to host");

        var result = await Router().RunAsync(ApModeUx7, "cmd", Timeout);

        result.success.Should().BeFalse();
        VerifyGatewayCredentialsTried(Times.Never());
    }

    [Fact]
    public async Task BothRefused_returns_the_Device_SSH_error_and_remembers_nothing()
    {
        DeviceCredentialsReturn(false, Refused);
        GatewayCredentialsReturn(false, "Authentication failed: root");

        var result = await Router().RunAsync(ApModeUx7, "cmd", Timeout);

        result.Should().Be((false, Refused));
        (await _store.UsesGatewayCredentialsAsync(Mac)).Should().BeFalse();
    }

    [Fact]
    public async Task AnUnknownHardwareType_is_looked_up_by_MAC()
    {
        _devices.Add(new DiscoveredDevice { Mac = "AA:BB:CC:DD:EE:01", Type = DeviceType.AccessPoint, HardwareType = DeviceType.Gateway });
        DeviceCredentialsReturn(false, Refused);
        GatewayCredentialsReturn(true, "ok");

        var result = await Router().RunAsync(new DeviceSshTarget(Mac, Host, DeviceType.AccessPoint), "cmd", Timeout);

        result.Should().Be((true, "ok"));
    }

    [Fact]
    public async Task APlainAccessPoint_gets_no_overrides_without_dialing()
    {
        var overrides = await Router().GetCredentialOverridesAsync(PlainAp);

        overrides.Should().BeNull();
        _device.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ARememberedApModeUx7_gets_the_Gateway_SSH_credentials_as_overrides()
    {
        await _store.SetUsesGatewayCredentialsAsync(Mac, true);

        var overrides = await Router().GetCredentialOverridesAsync(ApModeUx7);

        overrides.Should().Be(new DeviceSshCredentialOverrides("root", "encrypted", null));
    }

    [Theory]
    [InlineData("Authentication failed: Permission denied (password).", true)]
    [InlineData("SSH credentials not configured", true)]
    [InlineData(DeviceSshRouter.GatewayCredentialsMissing, true)]
    [InlineData("Connection failed: No route to host", false)]
    [InlineData("Command timed out: operation timed out", false)]
    [InlineData("mkdir: can't create directory '/tmp/netopt-apagent': Read-only file system", false)]
    [InlineData("", false)]
    public void OnlyARefusedLogin_counts_as_a_refusal(string output, bool refused)
    {
        DeviceSshRouter.IsCredentialRefusal(output).Should().Be(refused);
    }

    private sealed class FakeStore : IDeviceSshRouteStore
    {
        private readonly HashSet<string> _macs = new();

        public Task<bool> UsesGatewayCredentialsAsync(string mac, CancellationToken ct = default) =>
            Task.FromResult(_macs.Contains(mac));

        public Task SetUsesGatewayCredentialsAsync(string mac, bool usesGatewayCredentials, CancellationToken ct = default)
        {
            if (usesGatewayCredentials) _macs.Add(mac);
            else _macs.Remove(mac);
            return Task.CompletedTask;
        }
    }
}
