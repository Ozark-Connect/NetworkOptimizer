using FluentAssertions;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>The rules that read a pasted firmware link, on the layouts Ubiquiti actually serves.</summary>
public class FirmwareUrlParserTests
{
    [Fact]
    public void Parse_ReadsAUniFiOsImage()
    {
        var parsed = FirmwareUrlParser.Parse(
            "https://fw-download.ubnt.com/data/unifi-dream/6a7a-UCGF-6.0.11-847f967b-d464-428f-8b51-96dbc35b0f8b.bin", out var error);

        error.Should().BeNull();
        parsed!.Kind.Should().Be(FirmwareUrlKind.UniFiOs);
        parsed.Token.Should().Be("UCGF");
        parsed.Version.Should().Be("6.0.11");
        parsed.Directory.Should().Be("/data/unifi-dream");
    }

    [Fact]
    public void Parse_ReadsADeviceImage()
    {
        var parsed = FirmwareUrlParser.Parse(
            "https://fw-download.ubnt.com/data/unifi-firmware/b4a0-UAPA6A5-8.7.11-367ab339-3771-4c2d-84e3-b94266eac0a6.bin", out _);

        parsed!.Kind.Should().Be(FirmwareUrlKind.Device);
        parsed.Token.Should().Be("UAPA6A5");
        parsed.Version.Should().Be("8.7.11");
    }

    [Fact]
    public void Parse_ReadsAnUndashedImageId()
    {
        // Some images end in a bare hex id rather than a dashed GUID.
        var parsed = FirmwareUrlParser.Parse(
            "https://fw-download.ubnt.com/data/unifi-firmware/8d6c-UXGPRO-1.13.8-fb5c61711e234434bce3ccb909cea1b8.bin", out _);

        parsed!.Token.Should().Be("UXGPRO");
        parsed.Version.Should().Be("1.13.8");
    }

    [Fact]
    public void Parse_ReadsADeviceDownloadLink()
    {
        // dl.ui.com puts the model and the full version in folders rather than the file name.
        var parsed = FirmwareUrlParser.Parse(
            "https://dl.ui.com/unifi/firmware/U7PRO/8.8.8.20113/BZ.ipq53xx_8.8.8+20113.260910.1347.bin", out var error);

        error.Should().BeNull();
        parsed!.Kind.Should().Be(FirmwareUrlKind.Device);
        parsed.Token.Should().Be("U7PRO");
        parsed.Version.Should().Be("8.8.8.20113");
    }

    [Theory]
    [InlineData("https://dl.ui.com/unifi/10.6.97/unifi-native_sysvinit.deb")]
    [InlineData("https://dl.ui.com/unifi/10.6.97/unifi_sysvinit_all.deb")]
    public void Parse_ReadsTheNetworkApplicationPackage(string url)
    {
        var parsed = FirmwareUrlParser.Parse(url, out _);

        parsed!.Kind.Should().Be(FirmwareUrlKind.NetworkApp);
        parsed.Version.Should().Be("10.6.97");
        parsed.Token.Should().BeNull();
    }

    [Fact]
    public void Parse_LeavesAnUnknownDirectoryForTheCatalogToMatch()
    {
        var parsed = FirmwareUrlParser.Parse(
            "https://fw-download.ubnt.com/data/unifi-other/1a2b-ABC123-1.2.3-00112233.bin", out var error);

        error.Should().BeNull();
        parsed!.Kind.Should().Be(FirmwareUrlKind.Unknown);
        parsed.Token.Should().Be("ABC123");
    }

    [Theory]
    [InlineData("https://fw-download.ui.com/data/unifi-firmware/b4a0-UAPA6A5-8.7.11-367ab339.bin")]
    [InlineData("https://downloads.ubnt.com/unifi/firmware/U7PRO/8.8.8.20113/BZ.ipq53xx_8.8.8+20113.bin")]
    public void Parse_AcceptsAnyUbiquitiSubdomainOnAKnownLayout(string url)
    {
        // The layout is the real check; the host only has to be Ubiquiti's.
        FirmwareUrlParser.Parse(url, out var error).Should().NotBeNull();
        error.Should().BeNull();
    }

    [Theory]
    [InlineData("https://evil-ui.com/unifi/firmware/U7PRO/8.8.8.20113/BZ.bin")]
    [InlineData("https://dl.ui.com.example.net/unifi/firmware/U7PRO/8.8.8.20113/BZ.bin")]
    [InlineData("https://dl.ui.com:8443/unifi/firmware/U7PRO/8.8.8.20113/BZ.bin")]
    [InlineData("https://community.ui.com/uploads/attachment.bin")]
    [InlineData("http://fw-download.ubnt.com/data/unifi-dream/6a7a-UCGF-6.0.11-847f967b.bin")]
    [InlineData("https://example.com/data/unifi-dream/6a7a-UCGF-6.0.11-847f967b.bin")]
    [InlineData("https://fw-download.ubnt.com/data/unifi-dream/6a7a-UCGF-6.0.11-847f967b.bin?x=1")]
    [InlineData("https://fw-download.ubnt.com/data/unifi-dream/firmware.bin")]
    [InlineData("https://fw-download.ubnt.com/data/unifi-dream/6a7a-UCGF-6.0.11-847f'967b.bin")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Parse_RefusesAnythingThatIsNotAPlainUbiquitiImage(string url)
    {
        FirmwareUrlParser.Parse(url, out var error).Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void TokenIn_ReadsOnlyUrlsOnTheSamePath()
    {
        const string url = "https://fw-download.ubnt.com/data/unifi-firmware/99f0-UXGPROV2-5.1.26-f0594436-4f08-4d3d-a852-e9004f3e7d43.bin";

        FirmwareUrlParser.TokenIn(url, "/data/unifi-firmware").Should().Be("UXGPROV2");
        FirmwareUrlParser.TokenIn(url, "/data/unifi-dream").Should().BeNull();
    }
}
