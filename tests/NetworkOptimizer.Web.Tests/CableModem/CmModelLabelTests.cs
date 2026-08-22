using FluentAssertions;
using NetworkOptimizer.Web.Components.Shared;
using Xunit;

namespace NetworkOptimizer.Web.Tests.CableModem;

/// <summary>
/// How a modem's model splits across the two lines on the Cable Modem Stats card.
/// </summary>
public class CmModelLabelTests
{
    [Theory]
    [InlineData("CBR (CGA4332COM)", "CBR", "CGA4332COM")]
    [InlineData("XB10 (SG417DBCT)", "XB10", "SG417DBCT")]
    [InlineData("Motorola (8612)", "Motorola", "8612")]
    public void ProductTypeAndModelNumberStack(string model, string name, string number)
    {
        SplitModel(model).Should().Be((name, number));
    }

    [Theory]
    [InlineData("Netgear", "Netgear")]
    [InlineData("Motorola", "Motorola")]
    public void ABareVendorGetsLabelledCm(string model, string name)
    {
        // The provider could not read a model and named the maker instead, so the
        // second line has to say what the thing is.
        SplitModel(model).Should().Be((name, "CM"));
    }

    [Theory]
    [InlineData("CM600 - MyISP", "CM600")]
    [InlineData("Living Room SB8200", "SB8200")]
    [InlineData("MB8611 Primary", "MB8611")]
    [InlineData("Basement CGA4332COM", "CGA4332COM")]
    public void ABareVendorTakesTheModelNumberFromTheDeviceName(string deviceName, string expected)
    {
        SplitModel("Netgear", deviceName).Should().Be(("Netgear", expected));
    }

    [Theory]
    [InlineData("Modem")]
    [InlineData("Rack 4B")]
    [InlineData("Upstairs modem 2")]
    [InlineData("")]
    [InlineData(null)]
    public void ANameWithoutAModelNumberFallsBackToCm(string? deviceName)
    {
        SplitModel("Netgear", deviceName).Should().Be(("Netgear", "CM"));
    }

    [Theory]
    [InlineData("CM600")]
    [InlineData("cm1000")]
    [InlineData("SB8200")]
    public void AModelNumberOnItsOwnHasNoVendorToStackAboveIt(string model)
    {
        SplitModel(model).Should().Be((model, null));
    }

    [Theory]
    [InlineData("ARRIS SB8200", "ARRIS", "SB8200")]
    [InlineData("Motorola MB8611", "Motorola", "MB8611")]
    [InlineData("Netgear CM1000", "Netgear", "CM1000")]
    public void AnUnparenthesizedModelNumberStacksTheSameWay(string model, string name, string number)
    {
        SplitModel(model).Should().Be((name, number));
    }

    [Theory]
    [InlineData("Xfinity Gateway")]
    [InlineData("ARRIS Surfboard HNAP")]
    public void AModelWithNoNumberInItStaysOnOneLine(string model)
    {
        SplitModel(model).Should().Be((model, null));
    }

    private static (string Name, string? Number) SplitModel(string model, string? deviceName = null) =>
        CmStatsPanel.SplitModel(model, deviceName);
}
