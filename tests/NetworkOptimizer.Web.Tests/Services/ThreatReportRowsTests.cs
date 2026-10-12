using FluentAssertions;
using NetworkOptimizer.Reports;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class ThreatReportRowsTests
{
    [Fact]
    public void FromSettings_Unset_UsesDefaults()
    {
        var rows = ThreatReportRows.FromSettings(null, null);

        rows.TopSources.Should().Be(ThreatSummaryData.DefaultTopSourcesRows);
        rows.CategoryRows.Should().Be(ThreatSummaryData.DefaultCategoryRows);
        rows.IsDefault.Should().BeTrue();
    }

    [Fact]
    public void FromSettings_SavedValues_Used()
    {
        var rows = ThreatReportRows.FromSettings("12", "20");

        rows.Should().Be(new ThreatReportRows(12, 20));
        rows.IsDefault.Should().BeFalse();
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("-5", 1)]
    [InlineData("100000", ThreatSummaryData.MaxRows)]
    public void FromSettings_OutOfRange_Clamped(string value, int expected)
    {
        var rows = ThreatReportRows.FromSettings(value, value);

        rows.TopSources.Should().Be(expected);
        rows.CategoryRows.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("7.5")]
    public void FromSettings_NonNumeric_UsesDefaults(string value)
    {
        ThreatReportRows.FromSettings(value, value).IsDefault.Should().BeTrue();
    }

    [Fact]
    public void FromSettings_OneSetOneUnset_MixesSavedAndDefault()
    {
        var rows = ThreatReportRows.FromSettings("8", null);

        rows.Should().Be(new ThreatReportRows(8, ThreatSummaryData.DefaultCategoryRows));
    }
}
