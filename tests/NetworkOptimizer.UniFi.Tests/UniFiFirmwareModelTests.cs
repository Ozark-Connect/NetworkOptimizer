using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Deserialization and request-body composition for the firmware rollout API shapes.
/// JSON here is reconstructed from the sanitized sample ledger with generic ids.
/// </summary>
public class UniFiFirmwareModelTests
{
    private const string SettingsResponseJson = """
    {
      "meta": { "rc": "ok" },
      "data": [
        {
          "key": "mgmt",
          "_id": "000000000000000000000001",
          "auto_upgrade": true,
          "auto_upgrade_hour": 3
        },
        {
          "key": "super_fwupdate",
          "_id": "000000000000000000000002",
          "sso_enabled": true,
          "x_sso_token": "not-a-real-token",
          "firmware_channel": "beta",
          "available_firmware_channels": ["release", "release-candidate", "beta"],
          "available_controller_channels": ["release", "release-candidate", "beta"]
        }
      ]
    }
    """;

    [Fact]
    public void FromSettingsResponse_ReadsTheSuperFwupdateSection()
    {
        using var doc = JsonDocument.Parse(SettingsResponseJson);

        var settings = UniFiFirmwareUpdateSettings.FromSettingsResponse(doc);

        settings.Should().NotBeNull();
        settings!.Id.Should().Be("000000000000000000000002");
        settings.Key.Should().Be("super_fwupdate");
        settings.SsoEnabled.Should().BeTrue();
        settings.FirmwareChannel.Should().Be("beta");
        settings.AvailableFirmwareChannels.Should().Equal("release", "release-candidate", "beta");
        settings.AvailableControllerChannels.Should().Equal("release", "release-candidate", "beta");
    }

    [Fact]
    public void FromSettingsResponse_WithoutTheSection_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("""{"meta":{"rc":"ok"},"data":[{"key":"usg"}]}""");

        UniFiFirmwareUpdateSettings.FromSettingsResponse(doc).Should().BeNull();
    }

    [Fact]
    public void FromSettingsResponse_WithoutADataArray_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("""{"meta":{"rc":"ok"}}""");

        UniFiFirmwareUpdateSettings.FromSettingsResponse(doc).Should().BeNull();
    }

    [Fact]
    public void BuildChannelWriteBody_PreservesIdAndSsoEnabled()
    {
        using var doc = JsonDocument.Parse(SettingsResponseJson);
        var current = UniFiFirmwareUpdateSettings.FromSettingsResponse(doc)!;

        var body = UniFiFirmwareUpdateSettings.BuildChannelWriteBody(current, "release-candidate");

        using var written = JsonDocument.Parse(JsonSerializer.Serialize(body));
        var root = written.RootElement;

        root.GetProperty("key").GetString().Should().Be("super_fwupdate");
        root.GetProperty("_id").GetString().Should().Be("000000000000000000000002");
        root.GetProperty("sso_enabled").GetBoolean().Should().BeTrue();
        root.GetProperty("firmware_channel").GetString().Should().Be("release-candidate");
    }

    [Fact]
    public void BuildChannelWriteBody_CarriesNothingElse()
    {
        using var doc = JsonDocument.Parse(SettingsResponseJson);
        var current = UniFiFirmwareUpdateSettings.FromSettingsResponse(doc)!;

        var body = UniFiFirmwareUpdateSettings.BuildChannelWriteBody(current, "release");

        // The mgmt section carries SSH credentials; the token is credential material too. Neither
        // may ever ride along on a channel write.
        body.Keys.Should().BeEquivalentTo("key", "sso_enabled", "firmware_channel", "_id");
        body.Should().NotContainKey("mgmt");
        body.Should().NotContainKey("x_ssh_password");
        body.Should().NotContainKey("x_sso_token");
    }

    [Fact]
    public void BuildChannelWriteBody_WithoutAnExistingId_OmitsIt()
    {
        var current = new UniFiFirmwareUpdateSettings { FirmwareChannel = "release" };

        var body = UniFiFirmwareUpdateSettings.BuildChannelWriteBody(current, "beta");

        body.Should().NotContainKey("_id");
        body.Should().NotContainKey("sso_enabled");
        body["firmware_channel"].Should().Be("beta");
    }

    [Fact]
    public void BuildChannelWriteBody_WithoutAChannel_Throws()
    {
        var current = new UniFiFirmwareUpdateSettings();

        var act = () => UniFiFirmwareUpdateSettings.BuildChannelWriteBody(current, "  ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FirmwareCatalog_DeserializesTheListAvailableEnvelope()
    {
        var json = """
        {
          "meta": { "rc": "ok" },
          "data": [
            {
              "base_model": "UP1",
              "device": "UP1",
              "knownDevice": false,
              "siteDevice": true,
              "version": "2.2.6.532",
              "url": "https://fw-download.ubnt.com/data/unifi-firmware/0000-UP1-2.2.6-00000000-0000-0000-0000-000000000000.bin",
              "md5sum": "00000000000000000000000000000000",
              "bundled": false
            }
          ]
        }
        """;

        var response = JsonSerializer.Deserialize<UniFiApiResponse<UniFiFirmwareCatalogEntry>>(json);

        response.Should().NotBeNull();
        response!.Meta.Rc.Should().Be("ok");
        response.Data.Should().HaveCount(1);

        var entry = response.Data[0];
        entry.BaseModel.Should().Be("UP1");
        entry.Device.Should().Be("UP1");
        entry.KnownDevice.Should().BeFalse();
        entry.SiteDevice.Should().BeTrue();
        entry.Version.Should().Be("2.2.6.532");
        entry.Url.Should().StartWith("https://fw-download.ubnt.com/");
        entry.Md5Sum.Should().Be("00000000000000000000000000000000");
        entry.Bundled.Should().BeFalse();
    }
}
