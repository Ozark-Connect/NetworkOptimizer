using FluentAssertions;
using NetworkOptimizer.UniFi;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class DomainAppCatalogTests
{
    [Theory]
    [InlineData("rr3---sn-abc.googlevideo.com", "YouTube")]
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
    [InlineData("s3.us-west-004.backblazeb2.com", "Backblaze")]   // B2's S3 endpoint
    [InlineData("f004.backblazeb2.com", "Backblaze")]
    [InlineData("api.backblaze.com", "Backblaze")]
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
        foreach (var app in new[] { "YouTube", "Netflix", "Valve Steam", "Discord", "Spotify", "FaceTime", "Xbox" })
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
    [InlineData("APPLE-ENGINEERING", "Apple")]
    [InlineData("APPLE-AUSTIN", "Apple")]
    [InlineData("Apple Inc.", "Apple")]
    [InlineData("Applebee's Hosting", null)]   // the owner only as a whole word
    [InlineData("BACKBLAZE", "Backblaze")]
    [InlineData("Backblaze Inc", "Backblaze")]
    [InlineData("Example Hosting", null)]
    [InlineData(null, null)]
    public void OwnerNamesTheAppForAddressesWithNoDomain(string? owner, string? app) =>
        DomainAppCatalog.OwnerApp(owner).Should().Be(app);

    [Theory]
    [InlineData("icmp", "ICMP")]
    [InlineData("icmpv6", "ICMP")]
    [InlineData("ICMP", "ICMP")]   // History's upper-cased protocol
    [InlineData("tcp", null)]
    [InlineData(null, null)]
    public void IcmpIsIcmpWhoeverAnswers(string? proto, string? app) =>
        DomainAppCatalog.ProtocolApp(proto).Should().Be(app);

    [Theory]
    [InlineData("tcp", 8009, "Google Cast")]
    [InlineData("tcp", 6444, "Midea")]
    [InlineData("udp", 8009, null)]
    [InlineData("tcp", 443, null)]
    public void LanProtocolsByPort(string proto, int port, string? expected) =>
        DomainAppCatalog.LanApp(proto, port).Should().Be(expected);

    [Theory]
    [InlineData("tcp", 3001, "LgElectro", "LG webOS TV")]
    [InlineData("tcp", 3000, "LG Electronics", "LG webOS TV")]
    [InlineData("tcp", 3001, "Apple", null)]        // a dev server on a laptop: Rails, Next.js, BrowserSync
    [InlineData("tcp", 3001, null, null)]           // vendor unknown: never by port alone
    [InlineData("udp", 3001, "LgElectro", null)]
    public void LgWebOsOnlyToAnLgDevice(string proto, int port, string? vendor, string? expected) =>
        DomainAppCatalog.LanApp(proto, port, vendor).Should().Be(expected);

    [Theory]
    [InlineData("rr3---sn-abc.googlevideo.com", 64496, "Example ISP", "Google Global Cache")] // embedded GGC
    [InlineData("rr3---sn-abc.googlevideo.com", 15169, "Google LLC", null)]          // Google's own network
    [InlineData("rr3---sn-abc.googlevideo.com", 396982, "Google LLC", null)]         // an owner ASN not listed: its name says Google
    [InlineData("rr3---sn-abc.googlevideo.com", null, null, null)]                   // no ASN: never a guess
    [InlineData("ipv4-c001-ord001-isp.1.oca.nflxvideo.net", null, null, "Netflix Open Connect")] // names itself embedded
    [InlineData("ipv4-c001-ord001-ix.1.oca.nflxvideo.net", 2906, "Netflix Streaming Services", null)]
    [InlineData("scontent.fxyz1-1.fna.fbcdn.net", 64497, "Example Telecom", "Meta FNA")]
    [InlineData("a123.akamaized.net", 20940, "Akamai International B.V.", null)]
    [InlineData("www.example.com", 64496, "Example ISP", null)]
    public void EmbeddedCacheIsAnOwnersHostOnSomeoneElsesAsn(string? host, int? asn, string? org, string? expected) =>
        DomainAppCatalog.EmbeddedCache(host, asn, org).Should().Be(expected);

    [Theory]
    [InlineData("YouTube", 7029, "Windstream Communications", "Google Global Cache")]   // DPI-named, node resolved out of view
    [InlineData("YouTube", 15169, "Google LLC", null)]
    [InlineData("Netflix", 7029, "Windstream Communications", "Netflix Open Connect")]
    [InlineData("Instagram", 7029, "Windstream Communications", "Meta FNA")]
    [InlineData("Microsoft Teams", 7029, "Windstream Communications", null)]          // not a content owner with caches
    [InlineData("YouTube", null, null, null)]                                         // no ASN: never a guess
    public void EmbeddedCacheByTheDpiNameWhenNoHostnameIsKnown(string app, int? asn, string? org, string? expected) =>
        DomainAppCatalog.EmbeddedCache(null, asn, org, app).Should().Be(expected);

    [Theory]
    [InlineData("speedtest.example.net", "tcp", 8080, "Speedtest.net")]
    [InlineData("speed1.example.com", "tcp", 8080, "Speedtest.net")]
    [InlineData("Speedtest-dfw.example.net", "tcp", 8080, "Speedtest.net")]
    [InlineData("speedtest.example.net", "tcp", 443, null)]     // 8080 only
    [InlineData("api.speedtest.example.net", "tcp", 8080, null)] // "speed" leads the name
    [InlineData("speedtest", "tcp", 8080, null)]                 // a bare label is not a server name
    [InlineData(null, "tcp", 8080, null)]
    public void OoklaServersBySpeedNameOn8080(string? host, string proto, int port, string? expected) =>
        DomainAppCatalog.SpeedtestApp(host, proto, port).Should().Be(expected);

    [Theory]
    [InlineData("udp", 41641, 3478, "Tailscale")]    // source 41641, to a STUN server
    [InlineData("udp", 51234, 41641, "Tailscale")]   // a peer's 41641 as the destination
    [InlineData("udp", 41641, 443, "Tailscale")]     // whatever the other port
    [InlineData("tcp", 41641, 443, null)]
    [InlineData("udp", 51234, 3478, null)]
    public void TailscaleByUdp41641EitherEnd(string proto, int clientPort, int remotePort, string? expected) =>
        DomainAppCatalog.TailscaleApp(proto, clientPort, remotePort).Should().Be(expected);

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
    [InlineData("Blizzard Entertainment", "fa-brands fa-battle-net")]
    [InlineData("Blizzard Entertainment, Inc.", "fa-brands fa-battle-net")]   // an address's owner
    [InlineData("Overwatch", "fa-brands fa-battle-net")]
    [InlineData("ICMP", "no-icon no-icon-icmp")]
    [InlineData("Outlook.com", "fa-brands fa-microsoft")]
    [InlineData("Roku", "no-icon no-icon-roku")]
    [InlineData("Elasticsearch", "no-icon no-icon-elasticsearch")]   // the port name (9200, 9300)
    [InlineData("Tailscale", "no-icon no-icon-tailscale")]
    [InlineData("Backblaze", "no-icon no-icon-backblaze")]          // in the DPI catalog, without a mark
    [InlineData("Akamai Technologies, Inc.", "no-icon no-icon-akamai")]   // an address's owner
    [InlineData("AKAMAI-AS", "no-icon no-icon-akamai")]
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
