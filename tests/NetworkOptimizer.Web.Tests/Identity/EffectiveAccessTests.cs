using FluentAssertions;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Authorization;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// The Access list as effective access: who appears for a site, at what role, and what the row says
/// it comes from. Exercised against the pure computation, like the role matrix it is built on.
/// </summary>
public class EffectiveAccessTests
{
    private static readonly Func<string, bool> NoGroups = _ => false;

    private static AccessUser User(string name, string? globalRole, params SiteMembership[] grants)
        => new(name, name, globalRole, grants);

    private static SiteMembership Grant(int id, string slug, SiteRole role)
        => new() { Id = id, UserId = "", TargetType = MembershipTargetType.Site, TargetId = slug, SiteRole = role };

    private static SiteMembership AllSites(int id, SiteRole role)
        => new() { Id = id, UserId = "", TargetType = MembershipTargetType.AllSites, SiteRole = role };

    [Fact]
    public void Admin_IsListed_OnEverySite_WithoutAGrant()
    {
        var rows = EffectiveAccess.ForSite([User("root", Roles.Admin)], "site-a", NoGroups, restrictSitesToMembers: true);

        var row = rows.Should().ContainSingle().Subject;
        row.EffectiveRole.Should().Be(SiteRole.SiteAdmin);
        row.Source.Should().Be(AccessSource.GlobalAdmin);
        row.MembershipId.Should().BeNull();
    }

    [Fact]
    public void GlobalRole_WithNoGrant_IsListed_OnlyWhileUnrestricted()
    {
        var users = new[] { User("op", Roles.Operator) };

        EffectiveAccess.ForSite(users, "site-a", NoGroups, restrictSitesToMembers: true).Should().BeEmpty();

        var row = EffectiveAccess.ForSite(users, "site-a", NoGroups, restrictSitesToMembers: false)
            .Should().ContainSingle().Subject;
        row.EffectiveRole.Should().Be(SiteRole.SiteOperator);
        row.Source.Should().Be(AccessSource.GlobalRole);
        row.MembershipId.Should().BeNull();
    }

    [Fact]
    public void GrantBelowGlobalRole_ShowsTheGlobalRole_AndKeepsTheGrant()
    {
        var users = new[] { User("op", Roles.Operator, Grant(7, "site-a", SiteRole.SiteViewer)) };

        var row = EffectiveAccess.ForSite(users, "site-a", NoGroups, restrictSitesToMembers: false)
            .Should().ContainSingle().Subject;
        row.EffectiveRole.Should().Be(SiteRole.SiteOperator);
        row.Source.Should().Be(AccessSource.GlobalRole);
        row.MembershipId.Should().Be(7, "the grant is still there to revoke");
        row.GrantRole.Should().Be(SiteRole.SiteViewer);
    }

    [Fact]
    public void GrantBelowGlobalRole_IsWhatApplies_WhileRestricted()
    {
        var users = new[] { User("op", Roles.Operator, Grant(7, "site-a", SiteRole.SiteViewer)) };

        var row = EffectiveAccess.ForSite(users, "site-a", NoGroups, restrictSitesToMembers: true)
            .Should().ContainSingle().Subject;
        row.EffectiveRole.Should().Be(SiteRole.SiteViewer);
        row.Source.Should().Be(AccessSource.Grant);
    }

    [Fact]
    public void GrantAboveGlobalRole_IsTheSource()
    {
        var users = new[] { User("v", Roles.Viewer, Grant(3, "site-a", SiteRole.SiteAdmin)) };

        var row = EffectiveAccess.ForSite(users, "site-a", NoGroups, restrictSitesToMembers: false)
            .Should().ContainSingle().Subject;
        row.EffectiveRole.Should().Be(SiteRole.SiteAdmin);
        row.Source.Should().Be(AccessSource.Grant);
    }

    [Fact]
    public void DirectGrant_CarriesTheRow_OverAnAllSitesGrantAtTheSameRole()
    {
        var users = new[] { User("v", Roles.Viewer, AllSites(1, SiteRole.SiteOperator), Grant(2, "site-a", SiteRole.SiteOperator)) };

        EffectiveAccess.ForSite(users, "site-a", NoGroups, restrictSitesToMembers: true)
            .Should().ContainSingle().Which.MembershipId.Should().Be(2);
    }

    [Fact]
    public void Overview_AddsAnAllSitesRow_ForGlobalRolesWithoutOne()
    {
        var users = new[]
        {
            User("root", Roles.Admin),
            User("op", Roles.Operator, Grant(5, "site-a", SiteRole.SiteViewer)),
            User("v", Roles.Viewer, AllSites(6, SiteRole.SiteViewer)),
        };

        var rows = EffectiveAccess.Overview(users, restrictSitesToMembers: false);

        rows.Should().ContainSingle(r => r.UserName == "root").Which.Should().BeEquivalentTo(new
        {
            TargetType = MembershipTargetType.AllSites, EffectiveRole = SiteRole.SiteAdmin, Source = AccessSource.GlobalAdmin, MembershipId = (int?)null,
        });

        var op = rows.Where(r => r.UserName == "op").ToList();
        op.Should().HaveCount(2);
        op.Single(r => r.TargetType == MembershipTargetType.Site).Should().BeEquivalentTo(new
        {
            EffectiveRole = SiteRole.SiteOperator, Source = AccessSource.GlobalRole, MembershipId = (int?)5, GrantRole = (SiteRole?)SiteRole.SiteViewer,
        });
        op.Single(r => r.TargetType == MembershipTargetType.AllSites).MembershipId.Should().BeNull();

        rows.Where(r => r.UserName == "v").Should().ContainSingle("an All sites grant already covers every site")
            .Which.MembershipId.Should().Be(6);
    }

    [Fact]
    public void Overview_WhileRestricted_ListsGrantsOnly_PlusAdmins()
    {
        var users = new[] { User("root", Roles.Admin), User("op", Roles.Operator, Grant(5, "site-a", SiteRole.SiteViewer)) };

        var rows = EffectiveAccess.Overview(users, restrictSitesToMembers: true);

        rows.Should().HaveCount(2);
        rows.Single(r => r.UserName == "op").Should().BeEquivalentTo(new { EffectiveRole = SiteRole.SiteViewer, Source = AccessSource.Grant });
        rows.Single(r => r.UserName == "root").Source.Should().Be(AccessSource.GlobalAdmin);
    }
}
