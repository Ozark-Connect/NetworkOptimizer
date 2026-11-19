using FluentAssertions;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Connection History draws the stretches a client spent on each AP, with gaps where it was offline.
/// </summary>
public class ClientConnectionTimelineTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = Start.AddHours(24);

    private static ClientConnectionEvent Connected(int hour, string ap, string band = "na", int signal = -55) => new()
    {
        Type = ClientConnectionEventType.Connected, Timestamp = Start.AddHours(hour), ApName = ap, RadioBand = band, Signal = signal,
    };

    private static ClientConnectionEvent Disconnected(int hour, string ap, string band = "na") => new()
    {
        Type = ClientConnectionEventType.Disconnected, Timestamp = Start.AddHours(hour), ApName = ap, RadioBand = band,
    };

    private static ClientConnectionEvent Roamed(int hour, string from, string to, string fromBand = "na", string toBand = "na") => new()
    {
        Type = ClientConnectionEventType.Roamed, Timestamp = Start.AddHours(hour),
        FromApName = from, ToApName = to, PreviousRadioBand = fromBand, RadioBand = toBand, PreviousSignal = -72, Signal = -50,
    };

    [Fact]
    public void A_disconnect_and_reconnect_leave_a_gap_and_the_open_stretch_runs_to_the_end()
    {
        var r = ClientConnectionTimeline.Build(
            new[] { Connected(2, "Office"), Disconnected(5, "Office"), Connected(8, "Office") }, Start, End, connectedNow: true);

        r.Segments.Select(s => (s.Ap, s.Start, s.End)).Should().Equal(
            ("Office", Start.AddHours(2), Start.AddHours(5)),
            ("Office", Start.AddHours(8), End));
        r.Disconnects.Should().Be(1);
        r.LongestOffline.Should().Be(TimeSpan.FromHours(3));
    }

    [Fact]
    public void A_stretch_open_before_the_range_starts_at_the_range_start()
    {
        var r = ClientConnectionTimeline.Build(new[] { Disconnected(4, "Office", "ng") }, Start, End, connectedNow: false);

        r.Segments.Should().ContainSingle().Which.Should().Match<ClientConnectionTimeline.Segment>(
            s => s.Ap == "Office" && s.Band == "ng" && s.Start == Start && s.End == Start.AddHours(4));
        r.LongestOffline.Should().Be(TimeSpan.FromHours(20), "still offline at the end of the range");
    }

    [Fact]
    public void A_roam_ends_one_stretch_and_starts_the_next_on_the_new_ap_and_band()
    {
        var r = ClientConnectionTimeline.Build(
            new[] { Connected(1, "Office", "ng"), Roamed(6, "Office", "Kitchen", "ng", "6e") }, Start, End, connectedNow: true);

        r.Segments.Should().HaveCount(2);
        r.Segments[0].Should().Match<ClientConnectionTimeline.Segment>(
            s => s.Ap == "Office" && s.Band == "ng" && s.EndedBy == ClientConnectionEventType.Roamed && s.EndSignal == -72);
        r.Segments[1].Should().Match<ClientConnectionTimeline.Segment>(
            s => s.Ap == "Kitchen" && s.Band == "6e" && s.Start == Start.AddHours(6) && s.End == End && s.EndedBy == null);
        r.Roams.Should().Be(1);
        r.LongestOffline.Should().BeNull();
    }

    [Fact]
    public void A_first_event_roam_opens_on_the_ap_it_left()
    {
        var r = ClientConnectionTimeline.Build(new[] { Roamed(3, "Office", "Kitchen") }, Start, End, connectedNow: true);

        r.Segments.Select(s => (s.Ap, s.Start)).Should().Equal(("Office", Start), ("Kitchen", Start.AddHours(3)));
    }

    [Fact]
    public void A_last_connect_with_the_client_offline_now_is_not_drawn_to_the_end()
    {
        var r = ClientConnectionTimeline.Build(new[] { Connected(20, "Office") }, Start, End, connectedNow: false);

        r.Segments.Should().BeEmpty("its disconnect is not in the events, so the stretch's end is unknown");
    }
}
