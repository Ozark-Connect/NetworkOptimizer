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
    [InlineData(null, true, false)]
    [InlineData("", true, false)]
    [InlineData("qmicli", true, false)]
    [InlineData("quectel-at", false, true)]
    [InlineData("quectel-ssh", false, true)]
    [InlineData("netgear-nighthawk-hotspot", false, false)]
    [InlineData("inseego-fx", false, false)]
    [InlineData("zyxel-cpe", false, false)]
    public void ClassifiesTheTransport(string? provider, bool sharedSsh, bool perModemSsh)
    {
        ModemProviderKeys.UsesSharedSsh(provider).Should().Be(sharedSsh);
        ModemProviderKeys.UsesPerModemSsh(provider).Should().Be(perModemSsh);
        ModemProviderKeys.IsSsh(provider).Should().Be(sharedSsh || perModemSsh,
            "Settings forces port 80/443 and clears the username for every provider that is not SSH");
    }
}
