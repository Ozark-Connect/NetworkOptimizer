using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Licensing;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Licensing;

public class LicenseBillingTests
{
    private static LicenseKeyRecord MakeKey(string model, string status = LicenseKeyStatuses.Active) => new()
    {
        LicenseKey = "NO-KEY01",
        Model = model,
        Status = status,
        SiteAllowance = 5,
    };

    private static LicenseSnapshot MakeSnapshot(params SiteLicenseStatus[] states) =>
        new(states.ToDictionary(s => s.Slug), AnyKeysActive: true, TotalAllowance: 5, ComputedAt: DateTime.UtcNow);

    private static SiteLicenseStatus Status(string slug, SiteLicenseState state, LicenseRestrictionReason reason) =>
        new(slug, state, state == SiteLicenseState.Grace ? DateTime.UtcNow.AddDays(3) : null, reason, null);

    [Fact]
    public void HasSubscriptionKey_ActiveTermKey_True()
    {
        LicenseBilling.HasSubscriptionKey([MakeKey(LicenseKeyModels.Perpetual), MakeKey(LicenseKeyModels.Term)])
            .Should().BeTrue();
    }

    [Fact]
    public void HasSubscriptionKey_PerpetualOnly_False()
    {
        LicenseBilling.HasSubscriptionKey([MakeKey(LicenseKeyModels.Perpetual)]).Should().BeFalse();
    }

    [Theory]
    [InlineData(LicenseKeyStatuses.Pending)]
    [InlineData(LicenseKeyStatuses.Revoked)]
    public void HasSubscriptionKey_InactiveTermKey_False(string status)
    {
        LicenseBilling.HasSubscriptionKey([MakeKey(LicenseKeyModels.Term, status)]).Should().BeFalse();
    }

    [Fact]
    public void HasSubscriptionKey_NoKeys_False()
    {
        LicenseBilling.HasSubscriptionKey([]).Should().BeFalse();
    }

    [Fact]
    public void AnySubscriptionInGrace_ExpiredTermKeyGrace_True()
    {
        var snapshot = MakeSnapshot(
            Status("main", SiteLicenseState.Licensed, LicenseRestrictionReason.None),
            Status("branch", SiteLicenseState.Grace, LicenseRestrictionReason.KeyExpired));

        LicenseBilling.AnySubscriptionInGrace(snapshot).Should().BeTrue();
    }

    [Fact]
    public void AnySubscriptionInGrace_UncoveredSiteGraceOnly_False()
    {
        var snapshot = MakeSnapshot(Status("branch", SiteLicenseState.Grace, LicenseRestrictionReason.Unassigned));

        LicenseBilling.AnySubscriptionInGrace(snapshot).Should().BeFalse();
    }

    [Fact]
    public void AnySubscriptionInGrace_RestrictedAfterExpiry_False()
    {
        var snapshot = MakeSnapshot(Status("branch", SiteLicenseState.Restricted, LicenseRestrictionReason.KeyExpired));

        LicenseBilling.AnySubscriptionInGrace(snapshot).Should().BeFalse();
    }

    [Fact]
    public void AnySubscriptionInGrace_NoSnapshot_False()
    {
        LicenseBilling.AnySubscriptionInGrace(null).Should().BeFalse();
    }

    [Fact]
    public void AllGraceIsSubscription_OnlyExpiredTermGrace_True()
    {
        var snapshot = MakeSnapshot(
            Status("main", SiteLicenseState.Licensed, LicenseRestrictionReason.None),
            Status("branch", SiteLicenseState.Grace, LicenseRestrictionReason.KeyExpired));

        LicenseBilling.AllGraceIsSubscription(snapshot).Should().BeTrue();
    }

    [Fact]
    public void AllGraceIsSubscription_MixedWithUncoveredGrace_False()
    {
        var snapshot = MakeSnapshot(
            Status("main", SiteLicenseState.Grace, LicenseRestrictionReason.KeyExpired),
            Status("branch", SiteLicenseState.Grace, LicenseRestrictionReason.Unassigned));

        LicenseBilling.AllGraceIsSubscription(snapshot).Should().BeFalse();
    }

    [Fact]
    public void AllGraceIsSubscription_NoGrace_False()
    {
        LicenseBilling.AllGraceIsSubscription(MakeSnapshot(Status("main", SiteLicenseState.Licensed, LicenseRestrictionReason.None)))
            .Should().BeFalse();
        LicenseBilling.AllGraceIsSubscription(null).Should().BeFalse();
    }

    [Fact]
    public void CoverageGraceSentence_KeepsTheShippedWording()
    {
        LicenseBilling.CoverageGraceSentence(2, 5).Should().Be(
            "2 sites lose operations in 5 days - renew or reassign in Settings > Application > Licensing.");
        LicenseBilling.CoverageGraceSentence(1, 1).Should().Be(
            "1 site loses operations in 1 day - renew or reassign in Settings > Application > Licensing.");
    }

    [Fact]
    public void GraceSentence_OneSiteSevenDays_Singular()
    {
        LicenseBilling.GraceSentence(1, 7).Should().Be(
            "In 7 days, operations stop on 1 Site because its license is past due. " +
            "Pay the open invoice or renew the subscription to keep it running, " +
            "or move it to another license key in Settings > Application > Licensing.");
    }

    [Fact]
    public void GraceSentence_ThreeSitesOneDay_PluralSitesSingularDay()
    {
        LicenseBilling.GraceSentence(3, 1).Should().Be(
            "In 1 day, operations stop on 3 Sites because their license is past due. " +
            "Pay the open invoice or renew the subscription to keep them running, " +
            "or move them to another license key in Settings > Application > Licensing.");
    }

    [Fact]
    public void GraceSentence_ManySitesManyDays_MatchesApprovedCopy()
    {
        LicenseBilling.GraceSentence(4, 10).Should().Be(
            "In 10 days, operations stop on 4 Sites because their license is past due. " +
            "Pay the open invoice or renew the subscription to keep them running, " +
            "or move them to another license key in Settings > Application > Licensing.");
    }
}
