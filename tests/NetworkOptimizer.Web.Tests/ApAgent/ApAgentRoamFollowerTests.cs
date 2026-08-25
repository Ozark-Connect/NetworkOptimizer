using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// The roam follow. A walk test freezes today because the console keeps answering for the access
/// point the client already left, so what matters here is that leaving is recognized, that the
/// search is bounded, and that an unreachable agent is never mistaken for a roam.
/// </summary>
public class ApAgentRoamFollowerTests
{
    private const string ApOne = "aa:bb:cc:11:22:01";
    private const string ApTwo = "aa:bb:cc:33:44:02";
    private const string ApThree = "aa:bb:cc:55:66:03";
    private const string ApFour = "aa:bb:cc:77:88:04";

    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Fleet = { ApOne, ApTwo, ApThree, ApFour };

    [Fact]
    public void A_new_follower_has_nothing_to_poll()
    {
        var follower = new ApAgentRoamFollower();

        follower.State.Should().Be(ApAgentFollowState.Idle);
        follower.CurrentAp.Should().BeNull();
        follower.NextProbes(Fleet, Now).Should().BeEmpty();
    }

    [Fact]
    public void Seeing_the_client_attaches_to_that_access_point()
    {
        var follower = new ApAgentRoamFollower();
        follower.Seen("AA:BB:CC:11:22:01");

        follower.State.Should().Be(ApAgentFollowState.Attached);
        follower.CurrentAp.Should().Be(ApOne, "the MAC is normalized so case cannot split the follow");
        follower.NextProbes(Fleet, Now).Should().BeEmpty("nothing is searched while the client is where we think it is");
    }

    [Fact]
    public void An_access_point_reporting_the_client_gone_starts_a_search()
    {
        var follower = new ApAgentRoamFollower();
        follower.Seen(ApOne);
        follower.Left(Now);

        follower.State.Should().Be(ApAgentFollowState.Searching);
        follower.CurrentAp.Should().BeNull();
        follower.PreviousAp.Should().Be(ApOne);
    }

    [Fact]
    public void The_search_never_probes_the_access_point_the_client_left()
    {
        var follower = new ApAgentRoamFollower();
        follower.Seen(ApOne);
        follower.Left(Now);

        var probed = new List<string>();
        for (var i = 0; i < 6; i++) probed.AddRange(follower.NextProbes(Fleet, Now));

        probed.Should().NotContain(ApOne);
        probed.Should().Contain(new[] { ApTwo, ApThree, ApFour });
    }

    [Fact]
    public void The_fan_out_is_a_few_access_points_per_tick_not_the_whole_site()
    {
        var follower = new ApAgentRoamFollower();
        follower.Seen(ApOne);
        follower.Left(Now);

        var fleet = Enumerable.Range(1, 40).Select(i => $"aa:bb:cc:dd:ee:{i:x2}").ToList();
        follower.NextProbes(fleet, Now).Should().HaveCount(ApAgentRoamFollower.MaxProbesPerTick);
    }

    [Fact]
    public void A_large_site_is_capped_rather_than_swept_end_to_end()
    {
        var follower = new ApAgentRoamFollower();
        follower.Seen(ApOne);
        follower.Left(Now);

        var fleet = Enumerable.Range(1, 90).Select(i => $"aa:bb:cc:dd:ff:{i:x2}").ToList();

        var probed = new HashSet<string>();
        for (var i = 0; i < 200; i++) probed.UnionWith(follower.NextProbes(fleet, Now));

        probed.Should().HaveCount(ApAgentRoamFollower.MaxCandidates);
    }

    [Fact]
    public void The_announced_peer_is_probed_first()
    {
        var follower = new ApAgentRoamFollower();
        follower.Seen(ApOne);
        // A BSSID is derived from the access point's MAC and differs in the last octet only.
        follower.Left(Now, "aa:bb:cc:55:66:13");

        follower.NextProbes(Fleet, Now).First().Should().Be(ApThree);
    }

    [Fact]
    public void An_unusable_peer_hint_only_costs_the_ordering()
    {
        var follower = new ApAgentRoamFollower();
        follower.Seen(ApOne);
        follower.Left(Now, "00:11:22:33:44:55");

        follower.NextProbes(Fleet, Now).Should().StartWith(ApTwo, "the fleet order stands when nothing matches");
    }

    [Fact]
    public void Finding_the_client_elsewhere_ends_the_search()
    {
        var follower = new ApAgentRoamFollower();
        follower.Seen(ApOne);
        follower.Left(Now);
        follower.NextProbes(Fleet, Now).Should().NotBeEmpty();

        follower.Seen(ApTwo);

        follower.State.Should().Be(ApAgentFollowState.Attached);
        follower.CurrentAp.Should().Be(ApTwo);
        follower.NextProbes(Fleet, Now).Should().BeEmpty();
    }

    [Fact]
    public void A_client_that_never_reappears_closes_the_window_and_stops_probing()
    {
        var follower = new ApAgentRoamFollower();
        follower.Seen(ApOne);
        follower.Left(Now);

        var inWindow = Now + ApAgentRoamFollower.SearchWindow;
        follower.NextProbes(Fleet, inWindow).Should().NotBeEmpty("the window is still open");

        var past = inWindow + TimeSpan.FromSeconds(1);
        follower.NextProbes(Fleet, past).Should().BeEmpty();
        follower.State.Should().Be(ApAgentFollowState.Lost);
        follower.NextProbes(Fleet, past).Should().BeEmpty("a closed window does not reopen on the next tick");
    }

    [Fact]
    public void An_unreachable_agent_is_not_a_roam()
    {
        var follower = new ApAgentRoamFollower();
        follower.Seen(ApOne);
        follower.Stalled();

        follower.State.Should().Be(ApAgentFollowState.Lost);
        follower.IsSearching.Should().BeFalse();
        follower.NextProbes(Fleet, Now).Should().BeEmpty(
            "one access point going quiet must not fan requests out over the fleet");
    }

    [Fact]
    public void A_stalled_follow_picks_up_again_when_the_agent_comes_back()
    {
        var follower = new ApAgentRoamFollower();
        follower.Seen(ApOne);
        follower.Stalled();
        follower.Seen(ApOne);

        follower.State.Should().Be(ApAgentFollowState.Attached);
        follower.CurrentAp.Should().Be(ApOne);
    }
}
