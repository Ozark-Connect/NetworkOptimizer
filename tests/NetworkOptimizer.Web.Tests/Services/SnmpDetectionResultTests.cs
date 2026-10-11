using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// How a UniFi SNMP settings read maps to a detection state, including the credentials UniFi
/// withholds from View accounts: the v3 password on every version, the community on Network 11.
/// </summary>
public class SnmpDetectionResultTests
{
    private static SnmpDetectionResult Result(bool v2c, bool v3, string? community = null) => new()
    {
        Success = true,
        SnmpEnabled = v2c,
        SnmpV3Enabled = v3,
        Community = community,
        V3Username = "snmp-user",
    };

    [Fact]
    public void V2c_with_a_community_is_EnabledV2c()
        => Result(v2c: true, v3: false, community: "c0mmunity").DetectionState.Should().Be(SnmpDetectionState.EnabledV2c);

    [Fact]
    public void V2c_with_the_community_withheld_is_EnabledV2c_not_Disabled()
        => Result(v2c: true, v3: false).DetectionState.Should().Be(SnmpDetectionState.EnabledV2c);

    [Fact]
    public void V2c_and_v3_with_the_community_withheld_prefers_v3()
        => Result(v2c: true, v3: true).DetectionState.Should().Be(SnmpDetectionState.EnabledV3Only);

    [Fact]
    public void Neither_version_on_is_Disabled()
        => Result(v2c: false, v3: false).DetectionState.Should().Be(SnmpDetectionState.Disabled);

    [Fact]
    public void A_withheld_community_reads_as_missing_on_the_saved_settings()
    {
        var settings = new MonitoringSettings { SnmpDetectionState = SnmpDetectionState.EnabledV2c, SnmpCommunity = null };

        settings.SnmpCommunityMissing.Should().BeTrue();
        settings.SnmpV3PasswordMissing.Should().BeFalse();
    }

    [Fact]
    public void A_withheld_v3_password_reads_as_missing_on_the_saved_settings()
    {
        var settings = new MonitoringSettings
        {
            SnmpDetectionState = SnmpDetectionState.EnabledV3Only,
            SnmpV3Username = "snmp-user",
            SnmpV3AuthPassword = null,
        };

        settings.SnmpV3PasswordMissing.Should().BeTrue();
        settings.SnmpCommunityMissing.Should().BeFalse();
    }
}
