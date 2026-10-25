using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;
using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Web.Tests.Services;

public class AuditFindingDeltaTests
{
    private static AuditIssue Issue(string title, AuditSeverity severity = AuditSeverity.Informational,
        string? device = "Port 3 on Switch", string? port = "3", string category = "Port Security") => new()
    {
        Title = title, Severity = severity, DeviceName = device, Port = port, Category = category
    };

    private static bool NoneAcknowledged(AuditIssue _) => false;

    [Fact]
    public void Compare_CountRose_ReportsIncreaseAndTheNewFindings()
    {
        var previous = new[] { Issue("Port Lock Available") };
        var current = new[]
        {
            Issue("Port Lock Available"),
            Issue("Port Lock Available", device: "[AP] Back Yard on Switch", port: "5")
        };

        var result = AuditFindingDelta.Compare(previous, current, AuditSeverity.Informational, NoneAcknowledged);

        result.Increased.Should().BeTrue();
        result.Added.Should().Be(1);
        result.PreviousCount.Should().Be(1);
        result.CurrentCount.Should().Be(2);
        result.NewIssues.Should().ContainSingle().Which.Port.Should().Be("5");
    }

    [Fact]
    public void Compare_SameCount_NotAnIncrease()
    {
        var previous = new[] { Issue("A", port: "1") };
        var current = new[] { Issue("A", port: "1") };

        AuditFindingDelta.Compare(previous, current, AuditSeverity.Informational, NoneAcknowledged).Increased.Should().BeFalse();
    }

    [Fact]
    public void Compare_OneResolvedOneAppeared_NotAnIncrease()
    {
        // A + delta only: a swap nets to no change
        var previous = new[] { Issue("A", port: "1") };
        var current = new[] { Issue("B", port: "2") };

        var result = AuditFindingDelta.Compare(previous, current, AuditSeverity.Informational, NoneAcknowledged);

        result.Increased.Should().BeFalse();
        result.NewIssues.Should().ContainSingle();
    }

    [Fact]
    public void Compare_CountFell_NotAnIncrease()
    {
        var previous = new[] { Issue("A", port: "1"), Issue("B", port: "2") };
        var current = new[] { Issue("A", port: "1") };

        AuditFindingDelta.Compare(previous, current, AuditSeverity.Informational, NoneAcknowledged).Increased.Should().BeFalse();
    }

    [Fact]
    public void Compare_OnlyCountsTheRequestedSeverity()
    {
        var previous = Array.Empty<AuditIssue>();
        var current = new[] { Issue("Missing Port Lock", AuditSeverity.Recommended), Issue("Critical", AuditSeverity.Critical) };

        AuditFindingDelta.Compare(previous, current, AuditSeverity.Informational, NoneAcknowledged).Increased.Should().BeFalse();
        AuditFindingDelta.Compare(previous, current, AuditSeverity.Recommended, NoneAcknowledged).Added.Should().Be(1);
    }

    [Fact]
    public void Compare_AcknowledgedFindings_CountInNeitherRun()
    {
        var acknowledged = Issue("Acknowledged", port: "9");
        var previous = Array.Empty<AuditIssue>();
        var current = new[] { acknowledged };

        var result = AuditFindingDelta.Compare(previous, current, AuditSeverity.Informational,
            i => AuditService.GetIssueKey(i) == AuditService.GetIssueKey(acknowledged));

        result.Increased.Should().BeFalse();
        result.CurrentCount.Should().Be(0);
    }

    [Fact]
    public void GetIssueKey_PortlessFindingsWithSameTitle_DifferByMessage()
    {
        var plex = new AuditIssue { Title = "Firewall: Rule Order Issue", Description = "Allow rule 'Plex' may subvert deny rule 'Block IoT'" };
        var other = new AuditIssue { Title = "Firewall: Rule Order Issue", Description = "Allow rule 'Any' may subvert deny rule 'Block Guest'" };

        AuditService.GetIssueKey(plex).Should().NotBe(AuditService.GetIssueKey(other));
        AuditService.GetIssueKey(plex).Should().Be(AuditService.GetIssueKey(
            new AuditIssue { Title = plex.Title, Description = plex.Description }));
    }

    [Fact]
    public void GetIssueKey_DismissalScope_IgnoresLiveCountsInMessage()
    {
        var before = new AuditIssue { Title = "UPnP: Ports Exposed", DeviceName = "Gateway", DismissalScope = "", Description = "UPnP has 3 active port mapping(s)" };
        var after = new AuditIssue { Title = "UPnP: Ports Exposed", DeviceName = "Gateway", DismissalScope = "", Description = "UPnP has 5 active port mapping(s)" };

        AuditService.GetIssueKey(before).Should().Be(AuditService.GetIssueKey(after));
    }

    [Fact]
    public void GetIssueKey_PortAndClientFindings_IgnoreMessage()
    {
        var port = Issue("Port Lock Available");
        port.Description = "changed";
        var client = new AuditIssue { Title = "IoT Device on Wrong VLAN", DeviceName = "Plug", ClientMac = "aa:bb:cc:dd:ee:ff", Description = "x" };

        AuditService.GetIssueKey(port).Should().Be("Port Lock Available|Port 3 on Switch|3");
        AuditService.GetIssueKey(client).Should().Be("IoT Device on Wrong VLAN|Plug|");
    }

    [Fact]
    public void Compare_SystemFindings_Ignored()
    {
        var current = new[] { Issue("Fingerprint database unavailable", category: "System") };

        AuditFindingDelta.Compare([], current, AuditSeverity.Informational, NoneAcknowledged).Increased.Should().BeFalse();
    }

    [Theory]
    [InlineData(1, "recommendation", "recommendations", "1 new recommendation in the scheduled Security Audit (2 → 3)")]
    [InlineData(2, "recommendation", "recommendations", "2 new recommendations in the scheduled Security Audit (2 → 4)")]
    [InlineData(1, "Info finding", "Info findings", "1 new Info finding in the scheduled Security Audit (2 → 3)")]
    [InlineData(2, "Info finding", "Info findings", "2 new Info findings in the scheduled Security Audit (2 → 4)")]
    public void BuildTitle_CountsAndPlural(int added, string singular, string plural, string expected)
    {
        var delta = new AuditFindingDelta.Result(2, 2 + added, []);

        AuditFindingDelta.BuildTitle(singular, plural, delta).Should().Be(expected);
    }

    [Fact]
    public void BuildMessage_ListsUpToFiveThenCountsTheRest()
    {
        var issues = Enumerable.Range(1, 7).Select(n => Issue($"Finding {n}", device: $"Port {n} on Switch", port: n.ToString())).ToList();
        var delta = new AuditFindingDelta.Result(0, 7, issues);

        AuditFindingDelta.BuildMessage(delta).Should().Be(
            "Finding 1: Port 1 on Switch; Finding 2: Port 2 on Switch; Finding 3: Port 3 on Switch; " +
            "Finding 4: Port 4 on Switch; Finding 5: Port 5 on Switch; and 2 more");
    }

    [Fact]
    public void BuildMessage_FindingWithoutDevice_TitleOnly()
    {
        var delta = new AuditFindingDelta.Result(0, 1, [Issue("DNS: No DoH", device: null, port: null)]);

        AuditFindingDelta.BuildMessage(delta).Should().Be("DNS: No DoH");
    }
}
