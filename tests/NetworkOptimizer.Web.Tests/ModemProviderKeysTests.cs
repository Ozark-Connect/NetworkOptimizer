using FluentAssertions;
using NetworkOptimizer.Web.Services.CellularModemProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// The one transport check Settings uses for every cellular provider. Each existing key must keep
/// the class it had before the check was shared.
/// </summary>
public class ModemProviderKeysTests
{
    [Theory]
    [InlineData(null, true, false, false)]
    [InlineData("", true, false, false)]
    [InlineData("qmicli", true, false, false)]
    [InlineData("unifi-gateway", false, true, false)]
    [InlineData("quectel-at", false, false, true)]
    [InlineData("quectel-ssh", false, false, true)]
    [InlineData("mikrotik-routeros", false, false, true)]
    [InlineData("netgear-nighthawk-hotspot", false, false, false)]
    [InlineData("inseego-fx", false, false, false)]
    [InlineData("zyxel-cpe", false, false, false)]
    public void ClassifiesTheTransport(string? provider, bool sharedSsh, bool gatewaySsh, bool perModemSsh)
    {
        ModemProviderKeys.UsesSharedSsh(provider).Should().Be(sharedSsh);
        ModemProviderKeys.UsesGatewaySsh(provider).Should().Be(gatewaySsh);
        ModemProviderKeys.UsesPerModemSsh(provider).Should().Be(perModemSsh);
        ModemProviderKeys.IsUniFi(provider).Should().Be(sharedSsh || gatewaySsh);
        ModemProviderKeys.IsSsh(provider).Should().Be(sharedSsh || gatewaySsh || perModemSsh,
            "Settings forces port 80/443 and clears the username for every provider that is not SSH");
    }
}
