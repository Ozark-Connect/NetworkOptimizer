using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Only a vendor whose access gear is one technology may propose that technology. The
/// negative cases matter more than the positive ones: a wrong guess sets a value the user
/// has to notice and correct, which is worse than leaving the selector empty.
/// </summary>
public class UpstreamTracerVendorTechnologyTests
{
    [Theory]
    [InlineData("CADANT INC.")]
    [InlineData("Cadant Inc")]
    [InlineData("cadant")]
    [InlineData("Vecima Networks")]
    [InlineData("Harmonic Inc")]
    public void Cmts_only_vendors_propose_docsis(string vendor)
    {
        UpstreamTracerService.TechnologyFromVendor(vendor).Should().Be(AccessTechnology.Docsis);
    }

    [Theory]
    [InlineData("ARRIS Group, Inc.")]        // CMTS and PON OLT both
    [InlineData("CommScope Inc")]            // same line after the merger
    [InlineData("Casa Systems")]             // CMTS, PON OLT, and 5G core
    [InlineData("Nokia")]                    // PON and DSL from one chassis
    [InlineData("Calix, Inc.")]
    [InlineData("Huawei Technologies Co.,Ltd")]
    [InlineData("ZTE Corporation")]
    [InlineData("Alcatel-Lucent")]
    [InlineData("ADTRAN Inc")]
    [InlineData("DZS Inc")]
    [InlineData("Dasan Networks")]
    [InlineData("Ubiquiti Inc")]             // fiber, fixed wireless, and plain routers
    public void Multi_technology_vendors_propose_nothing(string vendor)
    {
        UpstreamTracerService.TechnologyFromVendor(vendor).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Some Unknown Vendor LLC")]
    public void Unknown_or_missing_vendors_propose_nothing(string? vendor)
    {
        UpstreamTracerService.TechnologyFromVendor(vendor).Should().BeNull();
    }
}
