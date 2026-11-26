using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Licensing;

public class SiteLimitTests
{
    [Fact]
    public void NoActiveKey_FreeTierLimit()
    {
        SiteManagementService.ComputeSiteLimit(anyKeysActive: false, totalAllowance: 0, personalUse: false).Should().Be(3);
    }

    [Fact]
    public void NoActiveKey_PersonalUseDeclared_StillFreeTierLimit()
    {
        // A key in grace grants no headroom; the declaration alone does not raise the free limit.
        SiteManagementService.ComputeSiteLimit(anyKeysActive: false, totalAllowance: 0, personalUse: true).Should().Be(3);
    }

    [Fact]
    public void ActiveKey_NoPersonalUse_KeyAllowanceOnly()
    {
        SiteManagementService.ComputeSiteLimit(anyKeysActive: true, totalAllowance: 5, personalUse: false).Should().Be(5);
    }

    [Fact]
    public void ActiveOneSiteKey_PersonalUse_AllowsAFourthSite()
    {
        SiteManagementService.ComputeSiteLimit(anyKeysActive: true, totalAllowance: 1, personalUse: true).Should().Be(4);
    }
}
