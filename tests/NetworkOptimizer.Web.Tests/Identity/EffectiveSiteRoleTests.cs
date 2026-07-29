using FluentAssertions;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Authorization;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// The effective-site-role matrix (design doc 04): effective role = max(global-implied, direct,
/// group-derived, AllSites), with the restriction toggle gating the global-implied contribution.
/// Global Admin is SiteAdmin everywhere and bypasses restriction. Exercised against the pure
/// computation so the whole matrix is covered without a database.
/// </summary>
public class EffectiveSiteRoleTests
{
    private static readonly Func<string, bool> NoGroups = _ => false;

    [Fact]
    public void GlobalAdmin_IsSiteAdmin_Everywhere_EvenWhenRestricted()
    {
        EffectiveSiteRole.Compute(isGlobalAdmin: true, globalImplied: null,
            memberships: [], slug: "anything", NoGroups, restrictSitesToMembers: true)
            .Should().Be(SiteRole.SiteAdmin);
    }

    [Theory]
    [InlineData(SiteRole.SiteOperator)]
    [InlineData(SiteRole.SiteViewer)]
    public void GlobalImplied_AppliesEverywhere_WhenUnrestricted(SiteRole implied)
    {
        EffectiveSiteRole.Compute(isGlobalAdmin: false, globalImplied: implied,
            memberships: [], slug: "site-a", NoGroups, restrictSitesToMembers: false)
            .Should().Be(implied);
    }

    [Fact]
    public void GlobalImplied_DoesNotApply_WhenRestricted_AndNoMembership()
    {
        EffectiveSiteRole.Compute(isGlobalAdmin: false, globalImplied: SiteRole.SiteOperator,
            memberships: [], slug: "site-a", NoGroups, restrictSitesToMembers: true)
            .Should().BeNull("a restricted install grants access only via membership");
    }

    [Fact]
    public void DirectMembership_ElevatesAboveGlobalImplied()
    {
        // Global Viewer everywhere, but SiteAdmin on "home" via direct membership.
        var memberships = new[] { new MembershipGrant(MembershipTargetType.Site, "home", SiteRole.SiteAdmin) };

        EffectiveSiteRole.Compute(false, SiteRole.SiteViewer, memberships, "home", NoGroups, restrictSitesToMembers: false)
            .Should().Be(SiteRole.SiteAdmin);
        EffectiveSiteRole.Compute(false, SiteRole.SiteViewer, memberships, "other", NoGroups, restrictSitesToMembers: false)
            .Should().Be(SiteRole.SiteViewer, "the direct grant is site-specific");
    }

    [Fact]
    public void AllSitesMembership_AppliesToEverySite_EvenWhenRestricted()
    {
        var memberships = new[] { new MembershipGrant(MembershipTargetType.AllSites, null, SiteRole.SiteOperator) };

        EffectiveSiteRole.Compute(false, null, memberships, "site-x", NoGroups, restrictSitesToMembers: true)
            .Should().Be(SiteRole.SiteOperator);
    }

    [Fact]
    public void GroupMembership_AppliesOnlyToGroupSites()
    {
        var memberships = new[] { new MembershipGrant(MembershipTargetType.Group, "7", SiteRole.SiteOperator) };

        // The predicate receives the GROUP id and answers whether the call's slug belongs to it.
        // Group 7 contains west-1/west-2 but not east-9.
        EffectiveSiteRole.Compute(false, null, memberships, "west-1", groupId => groupId == "7", restrictSitesToMembers: true)
            .Should().Be(SiteRole.SiteOperator);
        EffectiveSiteRole.Compute(false, null, memberships, "east-9", groupId => false, restrictSitesToMembers: true)
            .Should().BeNull();
    }

    [Fact]
    public void Max_WinsAcrossMultipleApplicableGrants()
    {
        var memberships = new[]
        {
            new MembershipGrant(MembershipTargetType.AllSites, null, SiteRole.SiteViewer),
            new MembershipGrant(MembershipTargetType.Site, "home", SiteRole.SiteOperator),
            new MembershipGrant(MembershipTargetType.Group, "3", SiteRole.SiteAdmin),
        };
        // Group 3 also contains "home", so all three grants apply on that slug; SiteAdmin wins.
        EffectiveSiteRole.Compute(false, null, memberships, "home", groupId => groupId == "3", restrictSitesToMembers: true)
            .Should().Be(SiteRole.SiteAdmin, "the highest applicable grant wins");
    }

    [Fact]
    public void NoRolesNoMemberships_IsNull()
    {
        EffectiveSiteRole.Compute(false, null, [], "site-a", NoGroups, restrictSitesToMembers: false)
            .Should().BeNull();
    }
}
