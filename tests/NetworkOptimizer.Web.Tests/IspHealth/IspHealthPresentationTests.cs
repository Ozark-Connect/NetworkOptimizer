using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>How the Path &amp; Congestion Events feed names the path an event happened on.</summary>
public class IspHealthPresentationTests
{
    private static readonly AccessProfile Gpon = IspHealthProfiles.GetProfile(AccessTechnology.Gpon)!;
    private static readonly DateTime WindowEnd = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    private static IspHealthReport Report() => new()
    {
        OverallScore = 88,
        ComputedAt = WindowEnd,
        WindowStart = WindowEnd.AddHours(-48),
        WindowEnd = WindowEnd,
        Profile = Gpon,
        AccessTechnology = AccessTechnology.Gpon,
        AccessDimension = new IspScoreDimension { Name = "Access Layer", Score = 90, Weight = 0.5 },
        TransitDimension = new IspScoreDimension { Name = "Transit", Score = 85, Weight = 0.25 },
        IspAsnDimension = new IspScoreDimension { Name = "ISP Network", Score = 87, Weight = 0.25 },
        TargetAddresses = new Dictionary<string, string> { ["t1"] = "198.51.100.9", ["t2"] = "203.0.113.4" },
    };

    private static PathShiftEvent Unreachable(string? asnName, params string[] targetIds) => new()
    {
        Time = WindowEnd.AddHours(-8),
        AsnName = asnName,
        IsUnreachable = true,
        UnreachableEnd = WindowEnd.AddHours(-7.5),
        TargetIds = targetIds.ToList(),
    };

    private static string TextOf(IspHealthReport r) =>
        IspHealthPresentation.EventTimeline(r).Single().Text;

    [Fact]
    public void An_unreachable_path_is_named_by_its_network()
    {
        var r = Report();
        r.PathShifts.Add(Unreachable("Example Transit", "t1"));
        TextOf(r).Should().StartWith("Example Transit went fully unreachable");
    }

    [Fact]
    public void A_path_with_no_network_is_named_by_its_target()
    {
        var r = Report();
        r.IspTargets.Add(new IspTargetHealth { TargetId = "t1", Name = "ISP hop 1" });
        r.PathShifts.Add(Unreachable(null, "t1"));
        TextOf(r).Should().StartWith("ISP hop 1 went fully unreachable");
    }

    [Fact]
    public void The_grade_it_counts_against_is_the_targets_network()
    {
        var r = Report();
        r.IspTargets.Add(new IspTargetHealth { TargetId = "t1", Name = "ISP hop 1" });
        r.IspAsns.Add(new IspAsnHealth { AsnName = "Example ISP", TargetIds = { "t1" } });
        r.PathShifts.Add(Unreachable(null, "t1"));
        var text = TextOf(r);
        text.Should().StartWith("ISP hop 1 went fully unreachable");
        text.Should().EndWith("still counted against Example ISP's own network grade.");
    }

    [Fact]
    public void A_target_with_no_network_anywhere_counts_against_its_own_grade()
    {
        var r = Report();
        r.PathShifts.Add(Unreachable(null, "t2"));
        TextOf(r).Should().EndWith("still counted against its own network grade.");
    }

    [Fact]
    public void A_host_outage_is_told_as_the_hosts_and_spares_the_network()
    {
        var r = Report();
        r.IspTargets.Add(new IspTargetHealth { TargetId = "t1", Name = "ISP speedtest" });
        r.IspAsns.Add(new IspAsnHealth { AsnName = "Example ISP", TargetIds = { "t1" } });
        r.PathShifts.Add(new PathShiftEvent
        {
            Time = WindowEnd.AddHours(-8),
            IsUnreachable = true,
            IsHostOutage = true,
            UnreachableEnd = WindowEnd.AddHours(-7.5),
            TargetId = "t1",
            TargetIds = { "t1" },
        });
        var entry = IspHealthPresentation.EventTimeline(r).Single();
        entry.Badge.Should().Be("Target unreachable");
        entry.Text.Should().StartWith("ISP speedtest went unreachable for 30 min - a host you monitor directly");
        entry.Text.Should().EndWith("Excluded from the Packet Loss factor and from Example ISP's grade.");
    }

    [Fact]
    public void A_target_with_no_name_is_named_by_its_address()
    {
        var r = Report();
        r.PathShifts.Add(Unreachable(null, "t2"));
        TextOf(r).Should().StartWith("203.0.113.4 went fully unreachable");
    }

    [Fact]
    public void An_event_with_no_target_at_all_still_reads()
    {
        var r = Report();
        r.PathShifts.Add(Unreachable(null));
        TextOf(r).Should().StartWith("path went fully unreachable");
    }
}
