using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Which console-reported WANs a WAN selector offers (#1183). A WAN with no link and no address is
/// a spare port with a WAN assigned, unless something says it is real: the primary role, a WAN
/// context, or a profile from a time it was live. Those three keep a WAN in an outage on screen.
/// </summary>
public class WanSelectabilityTests
{
    private static WanSummary Wan(string key, bool up, string? ip, bool primary = false) => new()
    {
        WanInterface = key,
        IsPrimary = primary,
        Up = up,
        IpAddress = ip,
    };

    private static readonly string[] None = Array.Empty<string>();

    [Fact]
    public void Up_with_address_is_offered()
    {
        var wans = new[] { Wan("wan", true, "198.51.100.2") };
        MonitoringPathView.SelectableWans(wans, None, None).Should().ContainSingle();
    }

    [Fact]
    public void Down_without_address_and_without_evidence_is_dropped()
    {
        var wans = new[] { Wan("wan", true, "198.51.100.2"), Wan("wan2", false, null) };
        MonitoringPathView.SelectableWans(wans, None, None)
            .Select(w => w.WanInterface).Should().Equal("wan");
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "198.51.100.9")]
    public void Either_link_or_address_counts_as_active(bool up, string? ip)
    {
        var wans = new[] { Wan("wan2", up, ip) };
        MonitoringPathView.SelectableWans(wans, None, None).Should().ContainSingle();
    }

    [Fact]
    public void Primary_role_is_kept_even_when_dark()
    {
        var wans = new[] { Wan("wan", false, null, primary: true), Wan("wan2", true, "198.51.100.9") };
        MonitoringPathView.SelectableWans(wans, None, None).Should().HaveCount(2);
    }

    [Theory]
    [InlineData("wan2")]
    [InlineData("WAN2")]
    public void Wan_context_keeps_a_dark_wan(string contextKey)
    {
        var wans = new[] { Wan("wan2", false, null) };
        MonitoringPathView.SelectableWans(wans, new[] { contextKey }, None).Should().ContainSingle();
    }

    [Fact]
    public void Legacy_wan1_context_key_matches_the_wan_summary()
    {
        var wans = new[] { Wan("wan", false, null) };
        MonitoringPathView.SelectableWans(wans, new[] { "wan1" }, None).Should().ContainSingle();
    }

    [Theory]
    [InlineData("WAN2")]
    [InlineData("wan2")]
    public void Wan_profile_keeps_a_dark_wan(string profileGroup)
    {
        var wans = new[] { Wan("wan2", false, null) };
        MonitoringPathView.SelectableWans(wans, None, new[] { profileGroup }).Should().ContainSingle();
    }

    [Fact]
    public void Evidence_for_one_wan_does_not_keep_another()
    {
        var wans = new[] { Wan("wan2", false, null), Wan("wan3", false, null) };
        MonitoringPathView.SelectableWans(wans, new[] { "wan3" }, new[] { "WAN" })
            .Select(w => w.WanInterface).Should().Equal("wan3");
    }

    [Fact]
    public void Order_is_preserved()
    {
        var wans = new[] { Wan("wan3", true, null), Wan("wan2", false, null), Wan("wan", true, "198.51.100.2") };
        MonitoringPathView.SelectableWans(wans, None, None)
            .Select(w => w.WanInterface).Should().Equal("wan3", "wan");
    }
}
