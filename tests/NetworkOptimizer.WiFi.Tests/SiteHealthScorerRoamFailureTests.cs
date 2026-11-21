using FluentAssertions;
using NetworkOptimizer.WiFi.Analyzers;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

/// <summary>
/// Roaming failures between an AP pair: one failed roam is no issue, and failures that all come from one device are a
/// Warning about that device, never a Critical about the APs.
/// </summary>
public class SiteHealthScorerRoamFailureTests
{
    private const string Ap1 = "aa:bb:cc:00:00:01";
    private const string Ap2 = "aa:bb:cc:00:00:02";
    private const string ClientA = "00:11:22:33:44:01";
    private const string ClientB = "00:11:22:33:44:02";

    [Fact]
    public void OneFailedRoam_NoIssue()
    {
        // 1 of 9 is 89% success: under the severe rate, but one roam is noise
        Issues(Edge(9, 8, Client(ClientA, 5, 4), Client(ClientB, 4, 4))).Should().BeEmpty();
    }

    [Fact]
    public void FailuresFromOneDevice_WarningNamingTheDevice()
    {
        var issue = Issues(Edge(9, 7, Client(ClientA, 5, 3), Client(ClientB, 4, 4))).Should().ContainSingle().Subject;

        issue.Severity.Should().Be(HealthIssueSeverity.Warning);
        issue.ScoreImpact.Should().Be(-5);
        issue.Description.Should().Be("2 of 9 roams failed between these APs, all from one client: Phone");
        issue.Recommendation.Should().Be(SiteHealthScorer.SingleDeviceRoamFailureRecommendation);
    }

    [Fact]
    public void FailuresFromTwoDevices_UnderSevereRate_Critical()
    {
        var issue = Issues(Edge(9, 7, Client(ClientA, 5, 4), Client(ClientB, 4, 3))).Should().ContainSingle().Subject;

        issue.Severity.Should().Be(HealthIssueSeverity.Critical);
        issue.ScoreImpact.Should().Be(-10);
        issue.Description.Should().StartWith("22% roam failure rate (2 of 9 roams)");
    }

    [Fact]
    public void FailuresTheClientListMisses_CountAsAnotherDevice()
    {
        // The list accounts for 1 of the 2 failures, so a second device may have made the other
        var edge = Edge(9, 7, Client(ClientA, 5, 4));

        SiteHealthScorer.SingleFailingRoamClient(edge).Should().BeNull();
        Issues(edge).Should().ContainSingle().Which.Severity.Should().Be(HealthIssueSeverity.Critical);
    }

    [Fact]
    public void SingleFailingRoamClient_UnnamedDevice_FallsBackToMac()
    {
        var topology = Topology(Edge(9, 7, Client(ClientB, 9, 7)));

        new SiteHealthScorer().Calculate([], [], topology).Issues
            .Should().ContainSingle(i => i.RuleId == "WIFI-ROAM-FAILURES-001")
            .Which.Description.Should().EndWith($"one client: {ClientB}");
    }

    private static List<HealthIssue> Issues(RoamingEdge edge) =>
        new SiteHealthScorer().Calculate([], [], Topology(edge)).Issues
            .Where(i => i.RuleId == "WIFI-ROAM-FAILURES-001").ToList();

    private static RoamingTopology Topology(RoamingEdge edge) => new()
    {
        Vertices =
        [
            new RoamingVertex { Mac = Ap1, Name = "AP-1" },
            new RoamingVertex { Mac = Ap2, Name = "AP-2" }
        ],
        Clients = [new RoamingClient { Mac = ClientA, Name = "Phone" }],
        Edges = [edge]
    };

    private static RoamingEdge Edge(int attempts, int successes, params ClientRoamingStats[] clients) => new()
    {
        Endpoint1Mac = Ap1,
        Endpoint2Mac = Ap2,
        TotalRoamAttempts = attempts,
        TotalSuccessfulRoams = successes,
        TopRoamingClients = [.. clients]
    };

    private static ClientRoamingStats Client(string mac, int attempts, int successes) =>
        new() { Mac = mac, RoamAttempts = attempts, SuccessfulRoams = successes };
}
