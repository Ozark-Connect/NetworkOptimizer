using FluentAssertions;
using NetworkOptimizer.UniFi;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Whether a gateway is its own UniFi OS console. Firmware rollouts size a reboot budget off
/// this and decide whether a console update belongs to the device at all, so a wrong answer
/// either declares a healthy gateway stuck or waits half an hour on one that rebooted in five
/// minutes. The two predicates are deliberately not each other's negation - unrecognized
/// hardware answers false to both.
/// </summary>
public class CloudGatewayClassificationTests
{
    [Theory]
    [InlineData("UDMPRO", "UDM-Pro")]
    [InlineData("UDMPROMAX", "UDM-Pro-Max")]
    [InlineData("UDMPROSE", "UDM-SE")]
    [InlineData("UDW", "UDW")]
    [InlineData("UDMENT", "EFG")]
    [InlineData("UCGMAX", "UCG-Max")]
    [InlineData("UDMA6A8", "UCG-Fiber")]
    [InlineData("UDMA6AD", "UCG-Industrial")]
    [InlineData("UDRULT", "UCG-Ultra")]
    [InlineData("UDR", "UDR")]
    [InlineData("UDMA67A", "UDR7")]
    [InlineData("UX", "UX")]
    [InlineData("UDMA69B", "UX7")]
    public void IsCloudGateway_ConsolesOfTheirOwn_AreTrue(string model, string shortname)
    {
        UniFiProductDatabase.IsCloudGateway(model, shortname).Should().BeTrue();
        UniFiProductDatabase.IsNetworkOnlyGateway(model, shortname).Should().BeFalse();
    }

    [Theory]
    [InlineData("UXGPRO", "UXG-Pro")]
    [InlineData("UXG", "UXG-Lite")]
    [InlineData("UXGB", "UXG-Max")]
    [InlineData("UXGENT", "UXG-Enterprise")]
    [InlineData("UXGA6AA", "UXG-Fiber")]
    [InlineData("UGW3", "USG-3P")]
    [InlineData("UGW4", "USG-Pro-4")]
    [InlineData("UGWHD4", "USG")]
    [InlineData("UGWXG", "USG-XG-8")]
    public void IsNetworkOnlyGateway_GatewaysManagedElsewhere_AreTrue(string model, string shortname)
    {
        UniFiProductDatabase.IsNetworkOnlyGateway(model, shortname).Should().BeTrue();
        UniFiProductDatabase.IsCloudGateway(model, shortname).Should().BeFalse();
    }

    [Theory]
    [InlineData("UDMEA4C", "UDM-Beast")]
    [InlineData("UDMA6B9", "UDR-5G-Max")]
    [InlineData("UDMA6AD", "UCG-Industrial")]
    public void IsCloudGateway_ConsoleLineMembers_NeedNoEntryOfTheirOwn(string model, string shortname)
    {
        // These match on the UCG-/UDR/UDM name prefix alone, so adding a model code to the
        // catalog is the only edit a new console in one of those lines needs.
        UniFiProductDatabase.IsCloudGateway(model, shortname).Should().BeTrue();
    }

    [Fact]
    public void IsCloudGateway_UnrecognizedHardware_AnswersNeither()
    {
        // Callers pick the predicate whose "no" is the safe direction rather than negating one.
        UniFiProductDatabase.IsCloudGateway("SOMEGW", "Some Unlisted Gateway").Should().BeFalse();
        UniFiProductDatabase.IsNetworkOnlyGateway("SOMEGW", "Some Unlisted Gateway").Should().BeFalse();
    }

    [Fact]
    public void IsCloudGateway_ExpressPrefix_DoesNotSwallowTheUxgLine()
    {
        UniFiProductDatabase.IsCloudGateway("UX", "UX").Should().BeTrue();
        UniFiProductDatabase.IsCloudGateway("UXGPRO", "UXG-Pro").Should().BeFalse();
    }

    [Theory]
    [InlineData("UDMENT", true)]    // EFG
    [InlineData("UDMEA4B", true)]   // EF-Core
    [InlineData("UDMA6A8", true)]   // UCG-Fiber
    [InlineData("UDMA69B", true)]   // UX7
    [InlineData("UDRULT", true)]    // UCG-Ultra
    [InlineData("UXGA6AA", false)]  // UXG-Fiber
    [InlineData("UGWHD4", false)]   // USG
    public void IsCloudGateway_ModelCodeAlone_IsResolvedThroughTheCatalogFirst(string model, bool expected)
    {
        // A code on its own has to be translated before anything can be decided from it:
        // UDMENT is EFG, UDMA69B is UX7, UDRULT is UCG-Ultra. Note this cannot prove the
        // prefixes run on the NAME - every UDMxxxx code in the catalog happens to be a console,
        // so code-prefixing would agree here. That invariant is a comment, not a test.
        UniFiProductDatabase.IsCloudGateway(model, null).Should().Be(expected);
    }

    [Fact]
    public void CloudGatewayPrefixes_MatchNothingOutsideTheConsoleLines()
    {
        // The prefixes are only safe while no non-console product is named UCG-*, UDR* or UDM*.
        // If Ubiquiti ever ships one, this fails and the prefix has to become an exact list.
        var matched = UniFiProductDatabase.AllProductNames
            .Where(n => n.StartsWith("UCG-", StringComparison.OrdinalIgnoreCase)
                     || n.StartsWith("UDR", StringComparison.OrdinalIgnoreCase)
                     || n.StartsWith("UDM", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        matched.Should().Equal(
            "UCG-Fiber", "UCG-Industrial", "UCG-Max", "UCG-Ultra",
            "UDM", "UDM-Beast", "UDM-Pro", "UDM-Pro-Max", "UDM-SE",
            "UDR", "UDR-5G-Max", "UDR7");
    }
}
