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

/// <summary>
/// Gateway-role SSH on HA sites. The configured Gateway SSH host is the LAN gateway address, which
/// lands on the active unit, so a command aimed at the Managed Shadow pair's backup is refused
/// rather than run on the master, and a per-network VRRP unit is dialed at its own address.
/// </summary>
public class DeviceSshRouterHaTests
{
    private const string PrimaryMac = "aa:bb:cc:00:00:01";
    private const string ShadowMac = "aa:bb:cc:00:00:02";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Mock<IUniFiSshService> _device = new();
    private readonly Mock<IGatewaySshService> _gateway = new();
    private readonly List<DiscoveredDevice> _devices = new();

    public DeviceSshRouterHaTests()
    {
        _gateway.Setup(g => g.GetSettingsAsync(It.IsAny<bool>()))
            .ReturnsAsync(new GatewaySshSettings { Enabled = true, Username = "root", Password = "encrypted" });
        _gateway.Setup(g => g.RunCommandAsync("cmd", Timeout, It.IsAny<CancellationToken>())).ReturnsAsync((true, "site gateway"));
    }

    private DeviceSshRouter Router() => new(
        _device.Object, _gateway.Object, new NoRouteStore(),
        _ => Task.FromResult<IReadOnlyList<DiscoveredDevice>>(_devices),
        c => Task.FromResult<SshConnectionInfo?>(c),
        p => p,
        NullLogger.Instance);

    private static DiscoveredDevice Unit(string mac, string? mode = null, string? vrrp = null, string? vrrpMode = null,
        string? lanIp = "192.0.2.1") => new()
    {
        Mac = mac,
        Type = DeviceType.Gateway,
        HardwareType = DeviceType.Gateway,
        DeviceModeOverride = mode,
        VrrpState = vrrp,
        GatewayVrrpMode = vrrpMode,
        State = 1,
        IpAddress = "203.0.113.10",
        LanIpAddress = lanIp,
    };

    [Fact]
    public async Task TheMaster_goes_through_the_gateway_service()
    {
        var primary = Unit(PrimaryMac);
        _devices.AddRange([primary, Unit(ShadowMac, GatewayHa.ShadowModeOverride, "backup", lanIp: null)]);

        var result = await Router().RunAsync(DeviceSshTarget.From(primary), "cmd", Timeout);

        result.Should().Be((true, "site gateway"));
    }

    [Fact]
    public async Task TheShadowPairBackup_is_refused_never_run_on_the_master()
    {
        var shadow = Unit(ShadowMac, GatewayHa.ShadowModeOverride, "backup", lanIp: null);
        _devices.AddRange([Unit(PrimaryMac), shadow]);

        var router = Router();
        var result = await router.RunAsync(DeviceSshTarget.From(shadow), "cmd", Timeout);

        result.success.Should().BeFalse();
        result.output.Should().Be(DeviceSshRouter.StandbyUnreachable);
        _gateway.Verify(g => g.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never());
        (await router.GetConnectionAsync(DeviceSshTarget.From(shadow))).Should().BeNull();
        (await router.DescribeMissingConnectionAsync(DeviceSshTarget.From(shadow))).Should().Be(DeviceSshRouter.StandbyUnreachable);
    }

    [Fact]
    public async Task APerNetworkVrrpUnit_is_dialed_at_its_own_address()
    {
        var secondary = Unit(ShadowMac, vrrpMode: "secondary", lanIp: "192.0.2.3");
        _devices.AddRange([Unit(PrimaryMac, vrrpMode: "primary", lanIp: "192.0.2.2"), secondary]);
        _device.Setup(d => d.RunCommandAsync("192.0.2.3", "cmd", "root", "encrypted", It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "secondary"));

        var result = await Router().RunAsync(DeviceSshTarget.From(secondary), "cmd", Timeout);

        result.Should().Be((true, "secondary"));
        _gateway.Verify(g => g.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task AnUnreadableDeviceList_still_reaches_the_site_gateway()
    {
        var router = new DeviceSshRouter(
            _device.Object, _gateway.Object, new NoRouteStore(),
            _ => throw new HttpRequestException("console unreachable"),
            c => Task.FromResult<SshConnectionInfo?>(c),
            p => p,
            NullLogger.Instance);

        var result = await router.RunAsync(new DeviceSshTarget(PrimaryMac, "192.0.2.1", DeviceType.Gateway, DeviceType.Gateway), "cmd", Timeout);

        result.Should().Be((true, "site gateway"));
    }

    private sealed class NoRouteStore : IDeviceSshRouteStore
    {
        public Task<bool> UsesGatewayCredentialsAsync(string mac, CancellationToken ct = default) => Task.FromResult(false);
        public Task SetUsesGatewayCredentialsAsync(string mac, bool value, CancellationToken ct = default) => Task.CompletedTask;
    }
}
