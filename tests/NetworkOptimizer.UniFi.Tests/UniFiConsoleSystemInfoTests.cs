using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Console-level /api/system parsing and the PATCH /api/system/updates/channels body.
/// JSON reconstructed from the sanitized sample ledger with generic ids.
/// </summary>
public class UniFiConsoleSystemInfoTests
{
    private const string SystemResponseJson = """
    {
      "name": "Test Console",
      "autoBackupEnabled": true,
      "hostname": "console.example.test",
      "ip": "192.0.2.10",
      "mac": "aa:bb:cc:dd:ee:ff",
      "firmware": {
        "latest": {
          "channel": "beta",
          "created": "2026-08-13T09:21:47Z",
          "file_size": 881518708,
          "id": "00000000-0000-0000-0000-000000000001",
          "md5": "00000000000000000000000000000000",
          "platform": "linux-x64",
          "product": "unifi-os-server",
          "version": "v5.1.34",
          "_links": {
            "self": { "href": "https://fw-update.ui.com/api/firmware/00000000-0000-0000-0000-000000000001" },
            "data": { "href": "https://fw-download.ubnt.com/data/unifi-os-server/0000-linux-x64-5.1.34-x64" },
            "upload": [
              { "name": "data", "href": "https://fw-update.ui.com/api/firmware/00000000-0000-0000-0000-000000000001/data" },
              { "name": "changelog", "href": "https://fw-update.ui.com/api/firmware/00000000-0000-0000-0000-000000000001/changelog" }
            ]
          }
        },
        "latestByChannel": {
          "release": {
            "channel": "release",
            "created": "2025-12-11T14:08:41Z",
            "id": "00000000-0000-0000-0000-000000000002",
            "platform": "linux-x64",
            "product": "unifi-os-server",
            "version": "v5.0.6",
            "_links": {
              "data": { "href": "https://fw-download.ubnt.com/data/unifi-os-server/0000-linux-x64-5.0.6-x64" },
              "upload": [
                { "name": "changelog", "href": "https://fw-update.ui.com/api/firmware/00000000-0000-0000-0000-000000000002/changelog" }
              ]
            }
          }
        },
        "progress": { "state": "none" },
        "channels": ["release", "release-candidate", "beta"],
        "releaseChannel": "beta",
        "update": { "state": "NOT_STARTED", "failedReason": "NONE" },
        "autoUpdate": { "schedule": null, "includeApplications": false }
      }
    }
    """;

    [Fact]
    public void Deserialize_ReadsTheFieldsTheRolloutNeeds()
    {
        var info = JsonSerializer.Deserialize<UniFiConsoleSystemInfo>(SystemResponseJson);

        info.Should().NotBeNull();
        info!.Name.Should().Be("Test Console");
        info.AutoBackupEnabled.Should().BeTrue();

        var firmware = info.Firmware;
        firmware.Should().NotBeNull();
        firmware!.ReleaseChannel.Should().Be("beta");
        firmware.Channels.Should().Equal("release", "release-candidate", "beta");
        firmware.Progress!.State.Should().Be("none");
        firmware.Update!.State.Should().Be("NOT_STARTED");
        firmware.Update.FailedReason.Should().Be("NONE");
    }

    [Fact]
    public void Deserialize_ReadsLatestWithItsPublishDateAndLinks()
    {
        var info = JsonSerializer.Deserialize<UniFiConsoleSystemInfo>(SystemResponseJson);

        var latest = info!.Firmware!.Latest;
        latest.Should().NotBeNull();
        latest!.Channel.Should().Be("beta");
        latest.Version.Should().Be("v5.1.34");
        latest.Product.Should().Be("unifi-os-server");
        latest.Created.Should().Be(new DateTime(2026, 8, 13, 9, 21, 47, DateTimeKind.Utc));
        latest.DownloadUrl.Should().Be("https://fw-download.ubnt.com/data/unifi-os-server/0000-linux-x64-5.1.34-x64");
        latest.ChangelogUrl.Should().EndWith("/changelog");
    }

    [Fact]
    public void Deserialize_ReadsLatestByChannel()
    {
        var info = JsonSerializer.Deserialize<UniFiConsoleSystemInfo>(SystemResponseJson);

        var byChannel = info!.Firmware!.LatestByChannel;
        byChannel.Should().ContainKey("release");
        byChannel["release"].Version.Should().Be("v5.0.6");
        byChannel["release"].Created.Should().Be(new DateTime(2025, 12, 11, 14, 8, 41, DateTimeKind.Utc));
        byChannel["release"].DownloadUrl.Should().Contain("5.0.6");
    }

    [Fact]
    public void IsStandaloneConsole_TrueForUniFiOsServer()
    {
        var info = JsonSerializer.Deserialize<UniFiConsoleSystemInfo>(SystemResponseJson);

        info!.IsStandaloneConsole.Should().BeTrue();
    }

    [Fact]
    public void IsStandaloneConsole_FalseForACloudGateway()
    {
        var json = """
        {
          "name": "Test Gateway",
          "firmware": {
            "releaseChannel": "release",
            "latest": { "channel": "release", "version": "v4.3.6", "product": "unifi-dream" },
            "latestByChannel": {
              "release": { "channel": "release", "version": "v4.3.6", "product": "unifi-dream" }
            }
          }
        }
        """;

        var info = JsonSerializer.Deserialize<UniFiConsoleSystemInfo>(json);

        info!.IsStandaloneConsole.Should().BeFalse();
    }

    [Fact]
    public void Deserialize_ReadsTheNetworkApplicationsChannelAndVersion()
    {
        var json = """
        {
          "name": "Test Gateway",
          "apps": {
            "controllers": [
              { "name": "protect", "type": "controller", "version": "6.2.10", "releaseChannel": "release" },
              {
                "name": "network", "type": "controller", "version": "10.6.94",
                "releaseChannel": "release-candidate", "updateAvailable": "10.7.10",
                "rollback": { "availableBackup": { "version": "10.5.20", "releaseChannel": "release" } }
              }
            ]
          }
        }
        """;

        var info = JsonSerializer.Deserialize<UniFiConsoleSystemInfo>(json);

        var network = info!.NetworkApplication;
        network.Should().NotBeNull();
        network!.Version.Should().Be("10.6.94");
        network.ReleaseChannel.Should().Be("release-candidate");
        network.UpdateAvailable.Should().Be("10.7.10");
        network.HasUpdate.Should().BeTrue();
        network.Rollback!.AvailableBackup!.Version.Should().Be("10.5.20");
    }

    [Fact]
    public void NetworkApplication_IsNullWhenTheConsoleDoesNotListIt()
    {
        var info = JsonSerializer.Deserialize<UniFiConsoleSystemInfo>(
            """{"name":"Test Console","apps":{"controllers":[{"name":"talk","version":"2.0.0"}]}}""");

        info!.NetworkApplication.Should().BeNull();
    }

    [Fact]
    public void ANetworkApplicationWithNoUpdate_HasNoneOnOffer()
    {
        var info = JsonSerializer.Deserialize<UniFiConsoleSystemInfo>(
            """{"apps":{"controllers":[{"name":"Network","version":"10.6.94","updateAvailable":null}]}}""");

        info!.NetworkApplication!.HasUpdate.Should().BeFalse("the name match is case-insensitive");
    }

    [Fact]
    public void IsStandaloneConsole_FalseWhenTheFirmwareBlockIsAbsent()
    {
        var info = JsonSerializer.Deserialize<UniFiConsoleSystemInfo>("""{"name":"Test Console"}""");

        info!.IsStandaloneConsole.Should().BeFalse();
    }

    [Fact]
    public void ChannelsRequest_NetworkApplicationOnly()
    {
        var request = UniFiConsoleUpdateChannelsRequest.Build("release-candidate", null);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(request));
        doc.RootElement.GetProperty("applications").GetProperty("network").GetString()
            .Should().Be("release-candidate");
        doc.RootElement.TryGetProperty("firmware", out _).Should().BeFalse();
    }

    [Fact]
    public void ChannelsRequest_UniFiOsOnly()
    {
        var request = UniFiConsoleUpdateChannelsRequest.Build(null, "beta");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(request));
        doc.RootElement.GetProperty("firmware").GetString().Should().Be("beta");
        doc.RootElement.TryGetProperty("applications", out _).Should().BeFalse();
    }

    [Fact]
    public void ChannelsRequest_BothInOnePatch()
    {
        var request = UniFiConsoleUpdateChannelsRequest.Build("release", "release-candidate");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(request));
        doc.RootElement.GetProperty("applications").GetProperty("network").GetString().Should().Be("release");
        doc.RootElement.GetProperty("firmware").GetString().Should().Be("release-candidate");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "  ")]
    public void ChannelsRequest_WithNothingToSet_IsNull(string? networkChannel, string? osChannel)
    {
        UniFiConsoleUpdateChannelsRequest.Build(networkChannel, osChannel).Should().BeNull();
    }
}
