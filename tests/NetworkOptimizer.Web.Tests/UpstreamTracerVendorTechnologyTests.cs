using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// A vendor's OUI proposes a technology only for a WAN that has none. Cable vendors lean
/// DOCSIS, PON/DSL vendors lean GPON (an OLT is far likelier than a DSLAM on a modern WAN),
/// and vendors whose OUI spans well beyond access gear propose nothing at all.
/// </summary>
public class UpstreamTracerVendorTechnologyTests
{
    [Theory]
    [InlineData("CADANT INC.")]
    [InlineData("Cadant Inc")]
    [InlineData("cadant")]
    [InlineData("Vecima Networks")]
    [InlineData("Harmonic Inc")]
    [InlineData("Teleste Corporation")]
    [InlineData("ARRIS Group, Inc.")]
    [InlineData("CommScope Inc")]
    [InlineData("Casa Systems")]
    public void Cable_vendors_propose_docsis(string vendor)
    {
        UpstreamTracerService.TechnologyFromVendor(vendor).Should().Be(AccessTechnology.Docsis);
    }

    [Theory]
    [InlineData("Nokia")]
    [InlineData("Calix, Inc.")]
    [InlineData("Huawei Technologies Co.,Ltd")]
    [InlineData("ZTE Corporation")]
    [InlineData("Alcatel-Lucent")]
    [InlineData("ADTRAN Inc")]
    [InlineData("DZS Inc")]
    [InlineData("Dasan Networks")]
    public void Pon_vendors_propose_gpon(string vendor)
    {
        // These ship DSL too, but a DSL line rarely presents the DSLAM as the L2 neighbor.
        UpstreamTracerService.TechnologyFromVendor(vendor).Should().Be(AccessTechnology.Gpon);
    }

    [Theory]
    [InlineData("Ubiquiti Inc")]             // fiber, fixed wireless, and plain routers
    [InlineData("Cisco Systems, Inc")]       // uBR/cBR CMTS shares its OUI with everything else
    public void Vendors_beyond_access_gear_propose_nothing(string vendor)
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
