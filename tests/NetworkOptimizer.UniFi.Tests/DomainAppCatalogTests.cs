using FluentAssertions;
using NetworkOptimizer.UniFi;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class DomainAppCatalogTests
{
    [Theory]
    [InlineData("rr3---sn-abc.googlevideo.com", "Youtube")]
    [InlineData("ipv4-c001-ord001-ix.1.oca.nflxvideo.net.", "Netflix")]
    [InlineData("steamcontent.com", "Valve Steam")]
    [InlineData("teams.microsoft.com", "Microsoft Teams")]
    [InlineData("claude.ai", "Claude")]
    [InlineData("api.anthropic.com", "Claude")]
    [InlineData("chatgpt.com", "ChatGPT")]
    [InlineData("gsa.apple.com", "Apple")]
    [InlineData("17.courier.push.apple.com", "Apple")]
    [InlineData("a1234.dscb.akamai.aaplimg.com", "Apple")]
    [InlineData("music.apple.com", "Apple Music")]   // a listed app under apple.com still wins
    [InlineData("p42-caldav.icloud.com", "iCloud")]
    [InlineData("imap.mail.me.com", "iCloud")]
    [InlineData("www.microsoft.com", null)]
    [InlineData("com", null)]
    [InlineData("example.net", null)]
    [InlineData(null, null)]
    public void NamesTheAppByDomainOrParent(string? domain, string? app) =>
        DomainAppCatalog.AppFor(domain).Should().Be(app);

    [Fact]
    public void EveryAppItNamesHasACatalogIconOrTheInitial()
    {
        // A name the DPI catalog spells differently would lose its icon without failing anything else.
        foreach (var app in new[] { "Youtube", "Netflix", "Valve Steam", "Discord", "Spotify" })
        {
            var (domain, cls) = DpiCatalog.IconsForName(app);
            (domain != null || cls != null).Should().BeTrue($"{app} should have an icon");
        }
    }

    [Theory]
    [InlineData("Valve")]          // a flow named by the network that owns the address
    [InlineData("Valve Steam")]
    public void SteamMarkForValve(string app) =>
        DpiCatalog.IconsForName(app).Class.Should().Be("fa-brands fa-steam");

    [Theory]
    [InlineData("udp", 4500, "AT&T Enterprises", "Wi-Fi Calling")]
    [InlineData("udp", 500, "T-Mobile USA, Inc.", "Wi-Fi Calling")]
    [InlineData("udp", 4500, "Verizon Business", "Wi-Fi Calling")]
    [InlineData("udp", 4500, "Example Hosting", null)]   // IPsec to anyone else stays a VPN
    [InlineData("tcp", 4500, "AT&T Enterprises", null)]
    [InlineData("udp", 443, "AT&T Enterprises", null)]
    [InlineData("udp", 4500, "Orangeville Networks", null)] // a carrier name only as a whole word
    [InlineData("udp", 4500, null, null)]
    public void WifiCallingIsIpsecToAMobileCarrier(string proto, int port, string? owner, string? expected) =>
        DomainAppCatalog.WifiCalling(proto, port, owner).Should().Be(expected);

    [Theory]
    [InlineData("tcp", 8009, "Google Cast")]
    [InlineData("tcp", 6444, "Midea")]
    [InlineData("udp", 8009, null)]
    [InlineData("tcp", 443, null)]
    public void LanProtocolsByPort(string proto, int port, string? expected) =>
        DomainAppCatalog.LanApp(proto, port).Should().Be(expected);

    [Theory]
    [InlineData("tcp", 5201, "iperf3")]
    [InlineData("udp", 5201, "iperf3")]   // its UDP mode
    [InlineData("tcp", 5202, null)]
    public void Iperf3ByPort(string proto, int port, string? expected) =>
        DomainAppCatalog.PortApp(proto, port).Should().Be(expected);

    [Theory]
    [InlineData("iperf3", "fa-solid fa-gauge-high")]
    [InlineData("Google Cast", "fa-brands fa-chromecast")]
    [InlineData("Midea", "fa-solid fa-house-signal")]
    public void LanProtocolMarks(string app, string cls) =>
        DpiCatalog.IconsForName(app).Class.Should().Be(cls);

    [Fact]
    public void WifiCallingHasThePhoneMark() =>
        DpiCatalog.IconsForName("Wi-Fi Calling").Class.Should().NotBeNull();

    [Theory]
    [InlineData("WireGuard", "no-icon no-icon-wireguard")]   // the port name: not in the DPI catalog
    [InlineData("OpenVPN", "no-icon no-icon-openvpn")]
    [InlineData("MQTT", "no-icon no-icon-mqtt")]
    [InlineData("MQTT-TLS", "no-icon no-icon-mqtt")]
    [InlineData("Cloudflare", "fa-brands fa-cloudflare")]    // an address's owner
    [InlineData("IPSec NAT-T", "fa-solid fa-shield-halved")] // the port name
    [InlineData("IMAPS", "fa-solid fa-envelope")]
    [InlineData("SMTP Submit", "fa-solid fa-envelope")]
    [InlineData("POP3S", "fa-solid fa-envelope")]
    [InlineData("ChatGPT", "no-icon no-icon-openai")]        // in the DPI catalog, without a mark
    [InlineData("Claude", "no-icon no-icon-claude")]
    public void MarksFontAwesomeLacksOrTheCatalogMisses(string app, string cls) =>
        DpiCatalog.IconsForName(app).Class.Should().Be(cls);

    [Theory]
    [InlineData("TCP", "fa-solid fa-right-left")]
    [InlineData("TCP 8443", "fa-solid fa-right-left")]
    [InlineData("UDP 3478", "fa-solid fa-paper-plane")]
    [InlineData("TCP Ventures", null)]   // a name that only starts with the word
    public void TransportMarksForFlowsNamedByProtocol(string app, string? cls) =>
        DpiCatalog.IconsForName(app).Class.Should().Be(cls);
}
