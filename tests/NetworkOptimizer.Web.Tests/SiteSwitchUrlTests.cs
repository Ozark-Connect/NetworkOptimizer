using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class SiteSwitchUrlTests
{
    private const string Base = "https://example.com";

    [Theory]
    // A WAN key belongs to the site being left, on every page.
    [InlineData("/monitoring?tab=isp-health&wan=wan2", "/monitoring?tab=isp-health")]
    [InlineData("/wan-speedtest?wan=wan2", "/wan-speedtest")]
    // Monitoring: devices, clients, modules, and target IDs go; the time window and category stay.
    [InlineData("/monitoring?tab=performance&category=Transit&at=1700000000000&span=3600000&target=custom-abc&wan=wan1",
        "/monitoring?tab=performance&category=Transit&at=1700000000000&span=3600000")]
    [InlineData("/monitoring?tab=performance&investigate=packet-loss&wan=wan1", "/monitoring?tab=performance&investigate=packet-loss")]
    [InlineData("/monitoring?tab=devices&device=aa:bb:cc:dd:ee:ff&at=1700000000000", "/monitoring?tab=devices&at=1700000000000")]
    [InlineData("/monitoring?tab=sfp&sfp=m1", "/monitoring?tab=sfp")]
    [InlineData("/monitoring?tab=setup&mifip=192.0.2.1&mitype=cm", "/monitoring?tab=setup&mitype=cm")]
    // Network Tools: the probe host is the same everywhere, the vantage is not.
    [InlineData("/network-tools?target=198.51.100.7&from=wan2", "/network-tools?target=198.51.100.7")]
    // Wi-Fi Optimizer: APs, clients, and roaming edges go; band and channel filters stay.
    [InlineData("/wifi-optimizer?tab=roaming&edge=ap1-ap2", "/wifi-optimizer?tab=roaming")]
    [InlineData("/wifi-optimizer?tab=channels&ap=aa:bb:cc:dd:ee:ff&band=5&channel=36", "/wifi-optimizer?tab=channels&band=5&channel=36")]
    [InlineData("/wifi-optimizer?tab=client&ap=aa:bb:cc:dd:ee:ff&band=5&client=aa:bb:cc:dd:ee:01", "/wifi-optimizer?tab=client&band=5")]
    [InlineData("/client-dashboard?ip=192.0.2.10&range=24h", "/client-dashboard?range=24h")]
    [InlineData("/client-dashboard?ip=192.0.2.10&wan=wan2#speed", "/client-dashboard#speed")]
    [InlineData("/alerts?tab=active&alert=12", "/alerts?tab=active")]
    [InlineData("/speedtest?result=5", "/speedtest")]
    // Live Traffic: clients, routes, resolvers, and LAN-device destinations go; apps and internet destinations stay.
    [InlineData("/monitoring/live-traffic?not-app=ICMP&client=NAS&route=Fiber%20WAN&not-resolver=Gateway",
        "/monitoring/live-traffic?not-app=ICMP")]
    [InlineData("/monitoring/live-traffic?dest=192.168.1.20&dest=youtube.com&not-dest=10.0.0.5",
        "/monitoring/live-traffic?dest=youtube.com")]
    [InlineData("/monitoring/live-traffic?dest=203.0.113.9&app=YouTube", "/monitoring/live-traffic?dest=203.0.113.9&app=YouTube")]
    [InlineData("/monitoring/live-traffic?route=LAN&not-route=Fiber%20WAN", "/monitoring/live-traffic?route=LAN")]   // LAN is every site's
    [InlineData("/monitoring/live-traffic?not-app=ICMP&route=LAN&dest=%5BAP%5D%20Living%20Room",
        "/monitoring/live-traffic?not-app=ICMP&route=LAN")]                                                       // a UniFi device by name
    [InlineData("/monitoring/live-traffic?dest=nas.lan&dest=printer.local&dest=rr4---sn-abc.googlevideo.com",
        "/monitoring/live-traffic?dest=rr4---sn-abc.googlevideo.com")]
    // Threat Intelligence filters name an outside source, which every site shares.
    [InlineData("/threats?ip=203.0.113.5&port=22", "/threats?ip=203.0.113.5&port=22")]
    public void StripSiteBoundParams_DropsOnlyWhatBelongsToTheOldSite(string url, string expected)
    {
        SiteSwitchService.StripSiteBoundParams(Base + url).Should().Be(Base + expected);
    }

    [Fact]
    public void StripSiteBoundParams_ParamNamedForAnotherPageIsKept()
    {
        // ?ip= is a client on Client Performance but an outside source on Threat Intelligence.
        SiteSwitchService.StripSiteBoundParams(Base + "/monitoring?tab=live&ip=192.0.2.10")
            .Should().Be(Base + "/monitoring?tab=live&ip=192.0.2.10");
    }

    [Theory]
    [InlineData("/change-control/plans/42")]
    [InlineData("/change-control/plans/42?site=branch-office")]
    public void StripSiteBoundParams_ReviewPlan_LandsOnChangeControl(string url)
    {
        // A plan ID is the old site's row; the new site opens Change Control instead.
        SiteSwitchService.StripSiteBoundParams(Base + url).Should().Be(Base + "/change-control");
    }

    [Fact]
    public void StripSiteBoundParams_ChangeControlTabIsKept()
    {
        SiteSwitchService.StripSiteBoundParams(Base + "/change-control/snapshots").Should().Be(Base + "/change-control/snapshots");
    }

    [Fact]
    public void StripSiteBoundParams_PreservesFragment()
    {
        SiteSwitchService.StripSiteBoundParams(Base + "/settings?tab=monitoring&wan=wan2#thresholds")
            .Should().Be(Base + "/settings?tab=monitoring#thresholds");
    }
}
