using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.Audit.Tests;

public class PortLockSupportTests
{
    [Theory]
    [InlineData("10.6.106", "7.6.2.17186", true)]
    [InlineData("10.6.101", "7.6.2", true)]
    [InlineData("11.0.5", "8.0.1.100", true)]
    [InlineData("10.6.100", "7.6.2.17186", false)]   // app below 10.6.101
    [InlineData("10.6.97", "7.6.2.17186", false)]
    [InlineData("10.5.120", "7.6.2.17186", false)]
    [InlineData("10.6.106", "7.6.1.17000", false)]   // firmware below 7.6.2
    [InlineData("10.6.106", "7.5.15.17146", false)]
    [InlineData("10.6.106", "2.1.6.762", false)]     // USW-Flex-Mini line
    [InlineData(null, "7.6.2.17186", false)]
    [InlineData("10.6.106", null, false)]
    [InlineData("", "", false)]
    [InlineData("garbage", "7.6.2.17186", false)]
    public void IsAvailable_GatesOnBothVersions(string? app, string? firmware, bool expected)
    {
        PortLockSupport.IsAvailable(app, firmware).Should().Be(expected);
    }

    [Theory]
    [InlineData("10.6.106", "10.6.106.0")]
    [InlineData("7.6.2.17186", "7.6.2.17186")]
    [InlineData("v10.6.101-beta", "10.6.101.0")]
    [InlineData("10.6", "10.6.0.0")]
    [InlineData(" 10.6.101 ", "10.6.101.0")]
    [InlineData("1.2.3.4.5", "1.2.3.4")]
    public void ParseVersion_ReadsLeadingNumericPart(string raw, string expected)
    {
        PortLockSupport.ParseVersion(raw).Should().Be(Version.Parse(expected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("10")]
    public void ParseVersion_Unparseable_ReturnsNull(string? raw)
    {
        PortLockSupport.ParseVersion(raw).Should().BeNull();
    }

    [Theory]
    [InlineData("10.5.120", false)]
    [InlineData("10.5", false)]
    [InlineData("10.6.0", true)]
    [InlineData("10.6", true)]
    [InlineData("10.6.106", true)]
    [InlineData("11.0.1", true)]
    [InlineData(null, true)]      // unknown: the current UI's names
    [InlineData("garbage", true)]
    public void UsesPortSecurityNames_FromUniFiNetwork106(string? appVersion, bool expected)
    {
        PortLockSupport.UsesPortSecurityNames(appVersion).Should().Be(expected);
    }

    [Theory]
    [InlineData("usw", true)]
    [InlineData("USW", true)]
    [InlineData("uxg", false)]
    [InlineData("ucg", false)]
    [InlineData("udm", false)]
    [InlineData("uap", false)]
    [InlineData("usp", false)]
    [InlineData(null, false)]
    public void SupportsDeviceType_SwitchesOnly(string? deviceType, bool expected)
    {
        PortLockSupport.SupportsDeviceType(deviceType).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("prof-1", false)]
    public void CanLockWithProfile_FollowsCoexistenceFlag(string? profileId, bool expectedWhileExclusive)
    {
        // While UniFi keeps the lock and profiles exclusive, only a profile-free port can be locked
        var expected = PortLockSupport.LockCoexistsWithPortProfile || expectedWhileExclusive;
        PortLockSupport.CanLockWithProfile(profileId).Should().Be(expected);
    }

    [Theory]
    [InlineData("US-16-XG", false)]
    [InlineData("USW-Mission-Critical", false)]
    [InlineData("US-8", false)]
    [InlineData("US-8-60W", false)]
    [InlineData("US-8-150W", false)]
    [InlineData("USW-Flex", true)]
    [InlineData("USW-Pro-24", true)]
    [InlineData(null, true)]      // unknown model: the general gate decides
    [InlineData("", true)]
    public void SupportsModel_ExcludesSwitchesWithoutTheLock(string? productName, bool expected)
    {
        PortLockSupport.SupportsModel(productName).Should().Be(expected);
        PortLockSupport.IsAvailable("11.0.5", "7.6.7", productName).Should().Be(expected);
    }

    [Theory]
    [InlineData("11.0.5", "7.6.7.18000", true)]
    [InlineData("11.0.0", "7.6.7", true)]
    [InlineData("11.0.5", "7.6.6.17900", false)]   // firmware before the Flex gained the lock
    [InlineData("10.6.106", "7.6.7.18000", false)] // Flex support needs UniFi Network 11
    [InlineData("10.6.106", "7.6.2.17186", false)]
    public void IsAvailable_UswFlex_NeedsFirmware767AndNetwork11(string app, string firmware, bool expected)
    {
        PortLockSupport.IsAvailable(app, firmware, "USW-Flex").Should().Be(expected);
    }

    [Fact]
    public void IsAvailable_OtherFlexModels_KeepTheGeneralGate()
    {
        PortLockSupport.IsAvailable("10.6.106", "7.6.2.17186", "USW-Flex-2.5G-8").Should().BeTrue();
        PortLockSupport.IsAvailable("10.6.106", "7.6.2.17186", "USW-Flex-XG").Should().BeTrue();
    }

    [Fact]
    public void ParseVersion_TwoPartVersion_ComparesAgainstThreePart()
    {
        // "10.6" normalizes to 10.6.0.0, which is below the 10.6.101 minimum
        PortLockSupport.IsAvailable("10.6", "7.6.2").Should().BeFalse();
        PortLockSupport.IsAvailable("10.7", "7.6.2").Should().BeTrue();
    }
}
