using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Body composition and the acceptance predicate for the two cmd/devmgr firmware commands.
/// Shapes reconstructed from the sanitized sample ledger.
/// </summary>
public class UniFiDeviceUpgradeCommandTests
{
    [Fact]
    public void UpgradeBody_CarriesTheMacAndCommandOnly()
    {
        var body = UniFiDeviceUpgradeCommand.BuildUpgradeBody("aa:bb:cc:dd:ee:ff");

        body.Keys.Should().BeEquivalentTo("mac", "cmd");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(body));
        doc.RootElement.GetProperty("mac").GetString().Should().Be("aa:bb:cc:dd:ee:ff");
        doc.RootElement.GetProperty("cmd").GetString().Should().Be("upgrade");

        // No URL: the target is the console's pending build for the device.
        doc.RootElement.TryGetProperty("url", out _).Should().BeFalse();
    }

    [Fact]
    public void ExternalUpgradeBody_CarriesTheMacUrlAndCommand()
    {
        const string url = "https://fw-download.ubnt.com/data/unifi-firmware/0000-U6M-6.7.35-00000000-0000-0000-0000-000000000001.bin";

        var body = UniFiDeviceUpgradeCommand.BuildExternalUpgradeBody("00:11:22:33:44:55", url);

        body.Keys.Should().BeEquivalentTo("mac", "url", "cmd");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(body));
        doc.RootElement.GetProperty("mac").GetString().Should().Be("00:11:22:33:44:55");
        doc.RootElement.GetProperty("url").GetString().Should().Be(url);
        doc.RootElement.GetProperty("cmd").GetString().Should().Be("upgrade-external");
    }

    [Fact]
    public void UpgradeBody_NormalizesAHyphenatedUppercaseMac()
    {
        var body = UniFiDeviceUpgradeCommand.BuildUpgradeBody("AA-BB-CC-DD-EE-FF");

        body["mac"].Should().Be("aa:bb:cc:dd:ee:ff");
    }

    [Fact]
    public void ExternalUpgradeBody_NormalizesAHyphenatedUppercaseMac()
    {
        var body = UniFiDeviceUpgradeCommand.BuildExternalUpgradeBody("AA-BB-CC-DD-EE-FF", "https://example.test/fw.bin");

        body["mac"].Should().Be("aa:bb:cc:dd:ee:ff");
    }

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff", "aa:bb:cc:dd:ee:ff")]
    [InlineData("AA:BB:CC:DD:EE:FF", "aa:bb:cc:dd:ee:ff")]
    [InlineData("AA-BB-CC-DD-EE-FF", "aa:bb:cc:dd:ee:ff")]
    [InlineData("aabbccddeeff", "aa:bb:cc:dd:ee:ff")]
    [InlineData("AABBCCDDEEFF", "aa:bb:cc:dd:ee:ff")]
    [InlineData("aabb.ccdd.eeff", "aa:bb:cc:dd:ee:ff")]
    [InlineData("  00-11-22-33-44-55  ", "00:11:22:33:44:55")]
    public void NormalizeMac_ProducesLowercaseColonForm(string input, string expected)
    {
        UniFiDeviceUpgradeCommand.NormalizeMac(input).Should().Be(expected);
    }

    [Fact]
    public void NormalizeMac_LeavesSomethingThatIsNotAMacAlone()
    {
        // Not twelve hex digits, so it is passed through rather than reshaped into a wrong MAC.
        UniFiDeviceUpgradeCommand.NormalizeMac("NOT-A-MAC").Should().Be("not:a:mac");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildBodies_WithoutAMac_Throw(string? mac)
    {
        var upgrade = () => UniFiDeviceUpgradeCommand.BuildUpgradeBody(mac!);
        var external = () => UniFiDeviceUpgradeCommand.BuildExternalUpgradeBody(mac!, "https://example.test/fw.bin");

        upgrade.Should().Throw<ArgumentException>();
        external.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ExternalUpgradeBody_WithoutAUrl_Throws()
    {
        var act = () => UniFiDeviceUpgradeCommand.BuildExternalUpgradeBody("aa:bb:cc:dd:ee:ff", "  ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IsAccepted_TrueForTheDocumentedOkEnvelope()
    {
        var response = JsonSerializer.Deserialize<UniFiApiResponse<object>>(
            """{"meta":{"rc":"ok"},"data":[]}""");

        UniFiDeviceUpgradeCommand.IsAccepted(response).Should().BeTrue();
    }

    [Fact]
    public void IsAccepted_FalseForAnErrorEnvelope()
    {
        var response = JsonSerializer.Deserialize<UniFiApiResponse<object>>(
            """{"meta":{"rc":"error","msg":"api.err.UnknownDevice"},"data":[]}""");

        UniFiDeviceUpgradeCommand.IsAccepted(response).Should().BeFalse();
    }

    [Fact]
    public void IsAccepted_FalseWhenTheCallDidNotComplete()
    {
        UniFiDeviceUpgradeCommand.IsAccepted(null).Should().BeFalse();
    }
}
