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
    [InlineData("CM600")]
    [InlineData("cm1000")]
    public void AModelAlreadyCarryingCmIsNotMadeToSayItTwice(string model)
    {
        SplitModel(model).Should().Be((model, null));
    }

    [Theory]
    [InlineData("ARRIS SB8200")]
    [InlineData("Motorola MB8611")]
    [InlineData("Xfinity Gateway")]
    public void AMultiWordModelStaysOnOneLine(string model)
    {
        SplitModel(model).Should().Be((model, null));
    }

    private static (string Name, string? Number) SplitModel(string model) =>
        CmStatsPanel.SplitModel(model);
}
