using FluentAssertions;
using NetworkOptimizer.Threats.Blocking;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class BlockingProfilesTests
{
    [Theory]
    [InlineData(0, false, BlockingProfile.Home)]
    [InlineData(3, false, BlockingProfile.SelfHosting)]
    [InlineData(0, true, BlockingProfile.SelfHosting)]
    [InlineData(null, false, BlockingProfile.SelfHosting)]
    public void Recommend_SelfHostingWheneverAnythingIsExposed(int? forwards, bool waf, BlockingProfile expected)
    {
        BlockingProfiles.Recommend(forwards, waf).Profile.Should().Be(expected);
    }

    [Fact]
    public void Home_IsOutboundCoverage_WithTokenListsOnlyWhenTheirTokenIsSet()
    {
        BlockingProfiles.Lists(BlockingProfile.Home, _ => false).Should().Equal(PublicBlockLists.SpamhausDrop);
        BlockingProfiles.Lists(BlockingProfile.Home, _ => true)
            .Should().Equal(PublicBlockLists.SpamhausDrop, PublicBlockLists.ThreatFox, PublicBlockLists.QFeeds);
    }

    [Fact]
    public void Business_LeavesOutListsWhoseTermsAreNonCommercialOrUnclear()
    {
        BlockingProfiles.Lists(BlockingProfile.Business, _ => true).Should().NotContain(new[]
        {
            PublicBlockLists.ThreatFox, PublicBlockLists.Feodo, PublicBlockLists.CinsArmy, PublicBlockLists.IpsumLevel3,
        });
    }

    [Fact]
    public void Lockdown_IsSelfHostingPlusTorExits()
    {
        BlockingProfiles.Lists(BlockingProfile.Lockdown, _ => true)
            .Should().BeEquivalentTo(BlockingProfiles.Lists(BlockingProfile.SelfHosting, _ => true).Append(PublicBlockLists.TorExits));
    }
}
