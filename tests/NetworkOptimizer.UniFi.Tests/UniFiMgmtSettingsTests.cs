using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Reading UniFi's own nightly auto-upgrade flag out of rest/setting. It races a firmware rollout,
/// so the wizard warns about it, and the section itself is never written back.
/// </summary>
public class UniFiMgmtSettingsTests
{
    private const string SettingsJson = """
    {
      "meta": { "rc": "ok" },
      "data": [
        { "key": "super_fwupdate", "firmware_channel": "release" },
        { "key": "mgmt", "_id": "000000000000000000000001", "auto_upgrade": true, "x_ssh_enabled": true }
      ]
    }
    """;

    [Fact]
    public void FromSettingsResponse_ReadsTheAutoUpgradeFlag()
    {
        using var document = JsonDocument.Parse(SettingsJson);

        var mgmt = UniFiMgmtSettings.FromSettingsResponse(document);

        mgmt.Should().NotBeNull();
        mgmt!.AutoUpgrade.Should().BeTrue();
    }

    [Fact]
    public void FromSettingsResponse_ReturnsNullWhenTheSectionIsAbsent()
    {
        using var document = JsonDocument.Parse("""{"meta":{"rc":"ok"},"data":[{"key":"super_fwupdate"}]}""");

        UniFiMgmtSettings.FromSettingsResponse(document).Should().BeNull();
    }

    [Fact]
    public void FromSettingsResponse_TreatsAMissingFlagAsUnknown()
    {
        using var document = JsonDocument.Parse("""{"meta":{"rc":"ok"},"data":[{"key":"mgmt"}]}""");

        UniFiMgmtSettings.FromSettingsResponse(document)!.AutoUpgrade.Should().BeNull();
    }
}
