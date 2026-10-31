using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.CellularModemProviders;
using NetworkOptimizer.Web.Services.Ssh;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.CellularModemProviders;

public class QmicliModemProviderTests
{
    private static ModemPollContext Context(string transportPath) => new()
    {
        Id = 1,
        Name = "Modem",
        Host = "192.0.2.1",
        TransportPath = transportPath,
    };

    // The three values UniFiProductDatabase.GetDefaultQmiDevicePath issues, plus the shapes an
    // operator types into the QMI Device Path field.
    [Theory]
    [InlineData("/dev/cdc-wdm0", "/dev/cdc-wdm0")]
    [InlineData("/dev/wwan0qmi0", "/dev/wwan0qmi0")]
    [InlineData("qrtr://3", "qrtr://3")]
    [InlineData("qrtr://12", "qrtr://12")]
    [InlineData("/dev/mhi_0308_02.01.00_pipe_12", "/dev/mhi_0308_02.01.00_pipe_12")]
    [InlineData(" /dev/wwan0qmi0 ", "/dev/wwan0qmi0")]
    [InlineData("", "/dev/wwan0qmi0")]
    [InlineData("   ", "/dev/wwan0qmi0")]
    public void QmiDevice_AcceptsEveryShapeTheProductUses(string configured, string expected)
    {
        QmicliModemProvider.QmiDevice(Context(configured)).Should().Be(expected);
    }

    [Theory]
    [InlineData("asdf")]
    [InlineData("/dev/wwan0qmi0; reboot")]
    [InlineData("/dev/wwan0qmi0 --device-open-proxy")]
    [InlineData("qrtr://3;id")]
    [InlineData("qrtr://x")]
    [InlineData("/dev/../etc/passwd")]
    [InlineData("/dev/..")]
    [InlineData("$(id)")]
    public void QmiDevice_RefusesAnythingElse(string configured)
    {
        var act = () => QmicliModemProvider.QmiDevice(Context(configured));
        act.Should().Throw<ArgumentException>().WithMessage("Invalid QMI device path:*");
    }

    [Fact]
    public async Task ForGateway_RunsEveryCommandOverGatewaySsh()
    {
        var gateway = new Mock<IGatewaySshService>();
        gateway.Setup(g => g.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Gateway SSH access is disabled"));
        gateway.Setup(g => g.TestConnectionAsync()).ReturnsAsync((true, "SSH connection successful"));

        var provider = QmicliModemProvider.ForGateway(NullLogger<QmicliModemProvider>.Instance, gateway.Object);

        provider.ProviderKey.Should().Be(ModemProviderKeys.UniFiGateway);
        (await provider.TestConnectionAsync(Context(""))).success.Should().BeTrue();
        (await provider.PollAsync(Context(""))).Success.Should().BeFalse();
        gateway.Verify(g => g.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        gateway.Verify(g => g.TestConnectionAsync(), Times.Once);
    }

    // U-LTE, U5G-Max and U5G-Backup keep the exact commands they had before the gateway dialect
    // existed; the device paths come from the configured TransportPath, unchanged.
    [Fact]
    public void ModemDialect_IsUnchanged()
    {
        var modem = QmicliModemProvider.Dialect.Modem;

        modem.Uiwwand("get-radio-status").Should().Be("ubus call uiwwand call '{\"method\":\"get-radio-status\",\"params\":{}}'");
        modem.Uiwwand("get-cell-tower-info").Should().Be("ubus call uiwwand call '{\"method\":\"get-cell-tower-info\",\"params\":{}}'");
        modem.QmiOpen.Should().Be("--device-open-proxy");
        modem.DefaultDevice.Should().Be("/dev/wwan0qmi0");
    }

    // UniFi OS on the UDR-5G-Max ships no ubus: uiwwand answers through uiwwand-ctl, and the
    // modem's QMI device is an MHI pipe opened over MBIM (the firmware's /etc/mbbcfg/functions.sh).
    [Fact]
    public async Task ForGateway_SpeaksUniFiOsDialect()
    {
        var commands = new List<string>();
        var gateway = new Mock<IGatewaySshService>();
        gateway.Setup(g => g.RunCommandAsync(Capture.In(commands), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, ""));

        var provider = QmicliModemProvider.ForGateway(NullLogger<QmicliModemProvider>.Instance, gateway.Object);
        await provider.PollAsync(Context(""));
        await provider.ResetRadioAsync(Context(""));

        commands.Should().Contain(c => c.Contains("echo '{\"method\":\"get-radio-status\"}' | uiwwand-ctl"));
        commands.Should().NotContain(c => c.Contains("ubus"));
        commands.Where(c => c.Contains("qmicli")).Should().NotBeEmpty()
            .And.OnlyContain(c => c.Contains($"-d {QmicliModemProvider.GatewayQmiDevice} --device-open-proxy --device-open-mbim"));
    }
}
