using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Licensing;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Licensing;

public class LicenseStateServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Dictionary<string, DateTime> NoHistory = new();

    private static Site MakeSite(int id, string slug, bool isDefault = false, int ageDays = 100) => new()
    {
        Id = id,
        Slug = slug,
        Name = slug,
        IsDefault = isDefault,
        Enabled = true,
        CreatedAt = Now.AddDays(-ageDays),
    };

    private static LicenseKeyRecord MakeTermKey(int id, int allowance, DateTime paidThrough, string org = "Example Networks LLC") => new()
    {
        Id = id,
        LicenseKey = $"NO-KEY{id:D2}",
        Org = org,
        Model = LicenseKeyModels.Term,
        SiteAllowance = allowance,
        Status = LicenseKeyStatuses.Active,
        PaidThrough = paidThrough,
        ActivatedAt = Now.AddDays(-50),
        CreatedAt = Now.AddDays(-50),
    };

    private static LicenseKeyRecord MakePerpetualKey(int id, int allowance, bool confirmed = false) => new()
    {
        Id = id,
        LicenseKey = $"NO-KEY{id:D2}",
        Org = "Example Networks LLC",
        Model = LicenseKeyModels.Perpetual,
        SiteAllowance = allowance,
        Status = LicenseKeyStatuses.Active,
        PerpetualConfirmed = confirmed,
        ActivatedAt = Now.AddDays(-50),
        CreatedAt = Now.AddDays(-50),
    };

    private static SiteLicenseAssignment Assign(int siteId, int keyId, int ageDays = 10) => new()
    {
        Id = siteId * 100 + keyId,
        SiteId = siteId,
        LicenseKeyRecordId = keyId,
        CreatedAt = Now.AddDays(-ageDays),
    };

    [Fact]
    public void NoKeys_AtOrUnderFreeLimit_AllFreeTier()
    {
        var sites = new[] { MakeSite(1, "main", isDefault: true), MakeSite(2, "branch"), MakeSite(3, "lake") };

        var states = LicenseStateService.ComputeStates(sites, [], [], NoHistory, Now);

        states.Values.Should().OnlyContain(s => s.State == SiteLicenseState.FreeTier);
    }

    [Fact]
    public void ActiveKey_CoveredSitesLicensed_UncoveredFallToFloorUnderFreeLimit()
    {
        var sites = new[] { MakeSite(1, "main", isDefault: true), MakeSite(2, "branch") };
        var keys = new[] { MakeTermKey(1, 1, Now.AddDays(60)) };
        var assignments = new[] { Assign(1, 1) };

        var states = LicenseStateService.ComputeStates(sites, keys, assignments, NoHistory, Now);

        states["main"].State.Should().Be(SiteLicenseState.Licensed);
        states["main"].CoveringKeyOrg.Should().Be("Example Networks LLC");
        states["branch"].State.Should().Be(SiteLicenseState.FreeTier);
    }

    [Fact]
    public void StackedKeys_CoverAllSites_AllLicensed()
    {
        var sites = new[]
        {
            MakeSite(1, "main", isDefault: true), MakeSite(2, "s2"), MakeSite(3, "s3"),
            MakeSite(4, "s4"), MakeSite(5, "s5"),
        };
        var keys = new[] { MakeTermKey(1, 2, Now.AddDays(60)), MakePerpetualKey(2, 3) };
        var assignments = new[] { Assign(1, 1), Assign(2, 1), Assign(3, 2), Assign(4, 2), Assign(5, 2) };

        var states = LicenseStateService.ComputeStates(sites, keys, assignments, NoHistory, Now);

        states.Values.Should().OnlyContain(s => s.State == SiteLicenseState.Licensed);
    }

    [Fact]
    public void UnassignedSite_OverFreeLimit_GetsGraceCountdownThenRestricted()
    {
        var sites = new[]
        {
            MakeSite(1, "main", isDefault: true), MakeSite(2, "s2"), MakeSite(3, "s3"), MakeSite(4, "s4"),
        };
        var keys = new[] { MakeTermKey(1, 3, Now.AddDays(60)) };
        var assignments = new[] { Assign(1, 1), Assign(2, 1), Assign(3, 1) };

        // Newly uncovered: countdown starts now.
        var states = LicenseStateService.ComputeStates(sites, keys, assignments, NoHistory, Now);
        states["s4"].State.Should().Be(SiteLicenseState.Grace);
        states["s4"].Reason.Should().Be(LicenseRestrictionReason.Unassigned);
        states["s4"].GraceDeadline.Should().Be(Now + LicenseStateService.GracePeriod);

        // Eleven days into the countdown: restricted.
        var history = new Dictionary<string, DateTime> { ["s4"] = Now.AddDays(-11) };
        states = LicenseStateService.ComputeStates(sites, keys, assignments, history, Now);
        states["s4"].State.Should().Be(SiteLicenseState.Restricted);
        states["s4"].Reason.Should().Be(LicenseRestrictionReason.Unassigned);
    }

    private static Site[] FourSites() =>
    [
        MakeSite(1, "main", isDefault: true, ageDays: 400), MakeSite(2, "s2", ageDays: 300),
        MakeSite(3, "s3", ageDays: 200), MakeSite(4, "s4", ageDays: 100),
    ];

    [Fact]
    public void PersonalUse_FreeTierSlotsPlusOneKey_CoverFourSites()
    {
        var keys = new[] { MakeTermKey(1, 1, Now.AddDays(60)) };
        var assignments = new[] { Assign(4, 1) };

        var states = LicenseStateService.ComputeStates(FourSites(), keys, assignments, NoHistory, Now, [1, 2, 3]);

        states["main"].State.Should().Be(SiteLicenseState.FreeTier);
        states["s2"].State.Should().Be(SiteLicenseState.FreeTier);
        states["s3"].State.Should().Be(SiteLicenseState.FreeTier);
        states["s4"].State.Should().Be(SiteLicenseState.Licensed);
    }

    [Fact]
    public void NoPersonalUse_SameSitesAndKey_UncoveredSitesGetGrace()
    {
        var keys = new[] { MakeTermKey(1, 1, Now.AddDays(60)) };
        var assignments = new[] { Assign(4, 1) };

        var states = LicenseStateService.ComputeStates(FourSites(), keys, assignments, NoHistory, Now);

        states["s4"].State.Should().Be(SiteLicenseState.Licensed);
        new[] { "main", "s2", "s3" }.Should().OnlyContain(slug =>
            states[slug].State == SiteLicenseState.Grace && states[slug].Reason == LicenseRestrictionReason.Unassigned);
    }

    [Fact]
    public void PersonalUse_StaleListOverLimit_CapsAtThreeDefaultThenOldest()
    {
        var sites = FourSites().Append(MakeSite(5, "s5", ageDays: 50)).ToArray();
        var keys = new[] { MakeTermKey(1, 1, Now.AddDays(60)) };

        var states = LicenseStateService.ComputeStates(sites, keys, [], NoHistory, Now, [5, 4, 3, 2, 1]);

        new[] { "main", "s2", "s3" }.Should().OnlyContain(slug => states[slug].State == SiteLicenseState.FreeTier);
        states["s4"].State.Should().Be(SiteLicenseState.Grace);
        states["s5"].State.Should().Be(SiteLicenseState.Grace);
    }

    [Fact]
    public void PersonalUse_KeyExpiredPastGrace_FreeTierSitesKeepWorking()
    {
        var keys = new[] { MakeTermKey(1, 1, Now.AddDays(-20)) };
        var assignments = new[] { Assign(4, 1) };

        var states = LicenseStateService.ComputeStates(FourSites(), keys, assignments, NoHistory, Now, [1, 2, 3]);

        new[] { "main", "s2", "s3" }.Should().OnlyContain(slug => states[slug].State == SiteLicenseState.FreeTier);
        states["s4"].State.Should().Be(SiteLicenseState.Restricted);
    }

    [Fact]
    public void PersonalUse_NoKeys_FloorFollowsFreeTierSlots_NeverMoreThanThree()
    {
        // The newest site holds a slot, so the default-plus-oldest floor alone would pick a different three.
        var states = LicenseStateService.ComputeStates(FourSites(), [], [], NoHistory, Now, [1, 2, 4]);

        new[] { "main", "s2", "s4" }.Should().OnlyContain(slug => states[slug].State == SiteLicenseState.FreeTier);
        states["s3"].State.Should().Be(SiteLicenseState.Restricted);
        states["s3"].Reason.Should().Be(LicenseRestrictionReason.OverFreeLimit);
    }

    [Fact]
    public void ExpiredTermKey_InsideGrace_SitesGraceWithPaidThroughAnchor()
    {
        var paidThrough = Now.AddDays(-5);
        var sites = new[]
        {
            MakeSite(1, "main", isDefault: true), MakeSite(2, "s2"), MakeSite(3, "s3"), MakeSite(4, "s4"),
        };
        var keys = new[] { MakeTermKey(1, 4, paidThrough) };
        var assignments = new[] { Assign(1, 1), Assign(2, 1), Assign(3, 1), Assign(4, 1) };

        var states = LicenseStateService.ComputeStates(sites, keys, assignments, NoHistory, Now);

        states.Values.Should().OnlyContain(s =>
            s.State == SiteLicenseState.Grace
            && s.Reason == LicenseRestrictionReason.KeyExpired
            && s.GraceDeadline == paidThrough + LicenseStateService.GracePeriod);
    }

    [Fact]
    public void ExpiredTermKey_PastGrace_NoOtherKeys_FloorKeepsDefaultAndTwoOldest()
    {
        var sites = new[]
        {
            MakeSite(1, "main", isDefault: true, ageDays: 400),
            MakeSite(2, "oldest", ageDays: 300),
            MakeSite(3, "older", ageDays: 200),
            MakeSite(4, "newest", ageDays: 50),
        };
        var keys = new[] { MakeTermKey(1, 4, Now.AddDays(-11)) };
        var assignments = new[] { Assign(1, 1), Assign(2, 1), Assign(3, 1), Assign(4, 1) };

        var states = LicenseStateService.ComputeStates(sites, keys, assignments, NoHistory, Now);

        states["main"].State.Should().Be(SiteLicenseState.FreeTier);
        states["oldest"].State.Should().Be(SiteLicenseState.FreeTier);
        states["older"].State.Should().Be(SiteLicenseState.FreeTier);
        states["newest"].State.Should().Be(SiteLicenseState.Restricted);
        states["newest"].Reason.Should().Be(LicenseRestrictionReason.OverFreeLimit);
    }

    [Fact]
    public void ExpiredTermKey_PastGrace_WithAnotherCurrentKey_RestrictsImmediatelyWithoutSecondCountdown()
    {
        var sites = new[]
        {
            MakeSite(1, "main", isDefault: true), MakeSite(2, "s2"), MakeSite(3, "s3"), MakeSite(4, "s4"),
        };
        var keys = new[] { MakeTermKey(1, 3, Now.AddDays(60)), MakeTermKey(2, 1, Now.AddDays(-11)) };
        var assignments = new[] { Assign(1, 1), Assign(2, 1), Assign(3, 1), Assign(4, 2) };

        var states = LicenseStateService.ComputeStates(sites, keys, assignments, NoHistory, Now);

        states["s4"].State.Should().Be(SiteLicenseState.Restricted);
        states["s4"].Reason.Should().Be(LicenseRestrictionReason.KeyExpired);
    }

    [Fact]
    public void RevokedKey_OverFreeLimit_RestrictsAssignedSites()
    {
        var sites = new[]
        {
            MakeSite(1, "main", isDefault: true), MakeSite(2, "s2"), MakeSite(3, "s3"), MakeSite(4, "s4"),
        };
        var revoked = MakeTermKey(1, 1, Now.AddDays(60));
        revoked.Status = LicenseKeyStatuses.Revoked;
        var keys = new[] { revoked, MakeTermKey(2, 3, Now.AddDays(60)) };
        var assignments = new[] { Assign(1, 2), Assign(2, 2), Assign(3, 2), Assign(4, 1) };

        var states = LicenseStateService.ComputeStates(sites, keys, assignments, NoHistory, Now);

        states["s4"].State.Should().Be(SiteLicenseState.Restricted);
        states["s4"].Reason.Should().Be(LicenseRestrictionReason.KeyRevoked);
    }

    [Fact]
    public void RevokedKey_UnderFreeLimit_FloorStillApplies()
    {
        var sites = new[] { MakeSite(1, "main", isDefault: true), MakeSite(2, "s2") };
        var revoked = MakePerpetualKey(1, 2);
        revoked.Status = LicenseKeyStatuses.Revoked;
        var assignments = new[] { Assign(1, 1), Assign(2, 1) };

        var states = LicenseStateService.ComputeStates(sites, [revoked], assignments, NoHistory, Now);

        states.Values.Should().OnlyContain(s => s.State == SiteLicenseState.FreeTier);
    }

    [Fact]
    public void AllowanceCap_OldestAssignmentsWin()
    {
        var sites = new[]
        {
            MakeSite(1, "main", isDefault: true), MakeSite(2, "s2"), MakeSite(3, "s3"), MakeSite(4, "s4"),
        };
        var keys = new[] { MakeTermKey(1, 2, Now.AddDays(60)) };
        var assignments = new[]
        {
            Assign(1, 1, ageDays: 30), Assign(2, 1, ageDays: 20), Assign(3, 1, ageDays: 10), Assign(4, 1, ageDays: 5),
        };

        var states = LicenseStateService.ComputeStates(sites, keys, assignments, NoHistory, Now);

        states["main"].State.Should().Be(SiteLicenseState.Licensed);
        states["s2"].State.Should().Be(SiteLicenseState.Licensed);
        states["s3"].State.Should().Be(SiteLicenseState.Grace);
        states["s4"].State.Should().Be(SiteLicenseState.Grace);
    }

    [Fact]
    public void PerpetualKey_CurrentRegardlessOfConfirmation()
    {
        LicenseStateService.IsActiveCurrent(MakePerpetualKey(1, 1, confirmed: false), Now).Should().BeTrue();
        LicenseStateService.IsActiveCurrent(MakePerpetualKey(1, 1, confirmed: true), Now).Should().BeTrue();
    }

    [Fact]
    public void PendingKey_GrantsNothing()
    {
        var pending = new LicenseKeyRecord
        {
            Id = 1,
            LicenseKey = "NO-KEY01",
            Status = LicenseKeyStatuses.Pending,
            CreatedAt = Now,
        };

        LicenseStateService.IsActiveCurrent(pending, Now).Should().BeFalse();
        LicenseStateService.IsInGrace(pending, Now).Should().BeFalse();
    }

    [Fact]
    public void GraceBoundary_ExactDeadlineStillInGrace()
    {
        var key = MakeTermKey(1, 1, Now - LicenseStateService.GracePeriod);

        LicenseStateService.IsInGrace(key, Now).Should().BeTrue();
        LicenseStateService.IsInGrace(key, Now.AddSeconds(1)).Should().BeFalse();
    }
}

public class LicenseNextCheckTests
{
    private static readonly DateTime Now = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ConfirmedPerpetual_NeverChecksAgain()
    {
        var key = new LicenseKeyRecord
        {
            Model = LicenseKeyModels.Perpetual,
            Status = LicenseKeyStatuses.Active,
            PerpetualConfirmed = true,
            ActivatedAt = Now.AddDays(-60),
        };

        LicenseActivationService.ComputeNextCheck(key, Now).Should().BeNull();
    }

    [Fact]
    public void UnconfirmedPerpetual_ChecksAtFraudWindowEnd()
    {
        var key = new LicenseKeyRecord
        {
            Model = LicenseKeyModels.Perpetual,
            Status = LicenseKeyStatuses.Active,
            ActivatedAt = Now.AddDays(-10),
        };

        LicenseActivationService.ComputeNextCheck(key, Now)
            .Should().Be(key.ActivatedAt!.Value + LicenseActivationService.PerpetualConfirmWindow);
    }

    [Fact]
    public void UnconfirmedPerpetual_PastFraudWindow_RetriesDaily()
    {
        var key = new LicenseKeyRecord
        {
            Model = LicenseKeyModels.Perpetual,
            Status = LicenseKeyStatuses.Active,
            ActivatedAt = Now.AddDays(-45),
        };

        LicenseActivationService.ComputeNextCheck(key, Now).Should().Be(Now.AddDays(1));
    }

    [Fact]
    public void TermKey_FarFromExpiry_SleepsUntilRefreshWindow()
    {
        var key = new LicenseKeyRecord
        {
            Model = LicenseKeyModels.Term,
            Status = LicenseKeyStatuses.Active,
            PaidThrough = Now.AddDays(90),
        };

        LicenseActivationService.ComputeNextCheck(key, Now)
            .Should().Be(key.PaidThrough!.Value - LicenseActivationService.TermRefreshWindow);
    }

    [Fact]
    public void TermKey_InsideRefreshWindow_ChecksDaily()
    {
        var key = new LicenseKeyRecord
        {
            Model = LicenseKeyModels.Term,
            Status = LicenseKeyStatuses.Active,
            PaidThrough = Now.AddDays(10),
        };

        LicenseActivationService.ComputeNextCheck(key, Now).Should().Be(Now.AddDays(1));
    }

    [Fact]
    public void TermKey_ExpiredInGrace_KeepsCheckingDaily()
    {
        var key = new LicenseKeyRecord
        {
            Model = LicenseKeyModels.Term,
            Status = LicenseKeyStatuses.Active,
            PaidThrough = Now.AddDays(-5),
        };

        LicenseActivationService.ComputeNextCheck(key, Now).Should().Be(Now.AddDays(1));
    }

    [Fact]
    public void RevokedKey_NeverChecksAgain()
    {
        var key = new LicenseKeyRecord
        {
            Model = LicenseKeyModels.Term,
            Status = LicenseKeyStatuses.Revoked,
            PaidThrough = Now.AddDays(10),
        };

        LicenseActivationService.ComputeNextCheck(key, Now).Should().BeNull();
    }

    [Fact]
    public void PendingKey_RetriesHourly()
    {
        var key = new LicenseKeyRecord { Status = LicenseKeyStatuses.Pending };

        LicenseActivationService.ComputeNextCheck(key, Now).Should().Be(Now.AddHours(1));
    }
}
