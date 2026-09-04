using FluentAssertions;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.CellularModemProviders;
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
    [InlineData("$(id)")]
    public void QmiDevice_RefusesAnythingElse(string configured)
    {
        var act = () => QmicliModemProvider.QmiDevice(Context(configured));
        act.Should().Throw<ArgumentException>().WithMessage("Invalid QMI device path:*");
    }
}
