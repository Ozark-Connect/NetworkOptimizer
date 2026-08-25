using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// The witness only earns its place if it stays quiet. Access points are polled concurrently and
/// most clients are reported by exactly one, so a false positive here would be a warning per client
/// per pass forever.
/// </summary>
public class ApAgentPassWitnessTests
{
    private const string ClientA = "aa:bb:cc:dd:ee:01";
    private const string ApOne = "84:78:48:c8:48:f1";
    private const string ApTwo = "1c:0b:8b:32:62:38";

    [Fact]
    public void A_client_on_one_access_point_is_not_reported()
    {
        var witness = new ApAgentPassWitness();
        witness.Claimed(ClientA, ApOne, 3, -55, true, "5");

        Assert.Empty(witness.Contested());
    }

    /// <summary>An MLO client is several links on one access point, claimed once per pass each.</summary>
    [Fact]
    public void The_same_access_point_claiming_twice_is_not_a_contest()
    {
        var witness = new ApAgentPassWitness();
        witness.Claimed(ClientA, ApOne, 3, -55, true, "5");
        witness.Claimed(ClientA, ApOne, 4, -57, true, "6");

        Assert.Empty(witness.Contested());
    }

    [Fact]
    public void Two_access_points_claiming_one_client_is_reported_once()
    {
        var witness = new ApAgentPassWitness();
        witness.Claimed(ClientA, ApOne, 3, -55, true, "5");
        witness.Claimed(ClientA, ApTwo, 900, -87, false, "2.4");

        var line = Assert.Single(witness.Contested());
        Assert.Contains(ClientA, line);
        Assert.Contains(ApOne, line);
        Assert.Contains(ApTwo, line);
    }

    /// <summary>The whole point is telling a real association from a phantom, so both must show.</summary>
    [Fact]
    public void The_report_carries_each_access_points_evidence()
    {
        var witness = new ApAgentPassWitness();
        witness.Claimed(ClientA, ApOne, 3, -55.4, true, "5");
        witness.Claimed(ClientA, ApTwo, 900, -87, false, "2.4");

        var line = Assert.Single(witness.Contested());
        Assert.Contains("rssi=-55.4", line);
        Assert.Contains("idle=3", line);
        Assert.Contains("auth=True", line);
        Assert.Contains("rssi=-87", line);
        Assert.Contains("idle=900", line);
        Assert.Contains("auth=False", line);
    }

    [Fact]
    public void Missing_values_render_rather_than_throwing()
    {
        var witness = new ApAgentPassWitness();
        witness.Claimed(ClientA, ApOne, null, null, true, null);
        witness.Claimed(ClientA, ApTwo, null, null, false, null);

        var line = Assert.Single(witness.Contested());
        Assert.Contains("rssi=?", line);
        Assert.Contains("idle=?", line);
    }

    [Fact]
    public void Access_point_MACs_differing_only_in_case_are_one_access_point()
    {
        var witness = new ApAgentPassWitness();
        witness.Claimed(ClientA, ApOne.ToUpperInvariant(), 3, -55, true, "5");
        witness.Claimed(ClientA, ApOne, 3, -55, true, "5");

        Assert.Empty(witness.Contested());
    }

    [Fact]
    public void An_empty_client_mac_is_ignored()
    {
        var witness = new ApAgentPassWitness();
        witness.Claimed("", ApOne, 3, -55, true, "5");
        witness.Claimed("", ApTwo, 3, -55, true, "5");

        Assert.Empty(witness.Contested());
    }

    [Fact]
    public void Reset_clears_the_previous_pass()
    {
        var witness = new ApAgentPassWitness();
        witness.Claimed(ClientA, ApOne, 3, -55, true, "5");
        witness.Claimed(ClientA, ApTwo, 3, -87, true, "2.4");
        Assert.Single(witness.Contested());

        witness.Reset();
        witness.Claimed(ClientA, ApOne, 3, -55, true, "5");

        Assert.Empty(witness.Contested());
    }

    /// <summary>Access points are polled with Task.WhenAll, so claims arrive from several threads.</summary>
    [Fact]
    public void Concurrent_claims_are_all_recorded()
    {
        var witness = new ApAgentPassWitness();
        var aps = Enumerable.Range(0, 8).Select(i => $"ap-{i}").ToArray();

        Parallel.ForEach(aps, ap =>
        {
            for (var c = 0; c < 50; c++)
                witness.Claimed($"client-{c}", ap, 1, -60, true, "5");
        });

        Assert.Equal(50, witness.Contested().Count);
    }
}
