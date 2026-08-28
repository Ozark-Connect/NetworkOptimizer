using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// Per-AP source selection. The console writes wifi_client for every access point on the site, so a
/// claim here is what stops an access point being written twice - and losing the claim is what
/// stops it going dark.
/// </summary>
public class ApAgentCoverageLedgerTests
{
    private const string ApOne = "aa:bb:cc:dd:ee:01";
    private const string ApTwo = "aa:bb:cc:dd:ee:02";

    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void An_unclaimed_access_point_stays_on_the_console_path()
        => new ApAgentCoverageLedger().Covers(ApOne, Now).Should().BeFalse();

    [Fact]
    public void Coverage_is_per_access_point_not_per_site()
    {
        var ledger = new ApAgentCoverageLedger();
        ledger.Claim(ApOne, Now);

        ledger.Covers(ApOne, Now).Should().BeTrue();
        ledger.Covers(ApTwo, Now).Should().BeFalse("an access point without an AP Agent keeps its console-sourced data");
    }

    [Fact]
    public void Mac_case_and_spacing_do_not_split_a_claim()
    {
        var ledger = new ApAgentCoverageLedger();
        ledger.Claim("AA:BB:CC:DD:EE:01", Now);

        ledger.Covers(" aa:bb:cc:dd:ee:01 ", Now).Should().BeTrue();
    }

    [Fact]
    public void A_failed_poll_hands_the_access_point_straight_back()
    {
        var ledger = new ApAgentCoverageLedger();
        ledger.Claim(ApOne, Now);
        ledger.Release(ApOne);

        ledger.Covers(ApOne, Now).Should().BeFalse();
    }

    [Fact]
    public void A_claim_that_stops_being_renewed_expires()
    {
        var ledger = new ApAgentCoverageLedger();
        ledger.Claim(ApOne, Now);

        ledger.Covers(ApOne, Now + ApAgentCoverageLedger.ClaimTtl).Should().BeTrue();
        ledger.Covers(ApOne, Now + ApAgentCoverageLedger.ClaimTtl + TimeSpan.FromSeconds(1)).Should().BeFalse();
    }

    [Fact]
    public void An_access_point_removed_from_the_site_stops_being_tracked()
    {
        var ledger = new ApAgentCoverageLedger();
        ledger.Claim(ApOne, Now);
        ledger.Claim(ApTwo, Now);

        ledger.RetainOnly(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ApTwo });

        ledger.Covers(ApOne, Now).Should().BeFalse();
        ledger.Covers(ApTwo, Now).Should().BeTrue();
        ledger.ActiveClaims(Now).Should().Be(1);
    }

    [Fact]
    public void Switching_the_site_off_hands_every_access_point_back()
    {
        var ledger = new ApAgentCoverageLedger();
        ledger.Claim(ApOne, Now);
        ledger.Claim(ApTwo, Now);

        ledger.ReleaseAll();

        ledger.ActiveClaims(Now).Should().Be(0);
    }
}
