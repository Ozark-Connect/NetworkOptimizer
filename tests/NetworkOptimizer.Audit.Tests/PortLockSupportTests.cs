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
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("prof-1", false)]
    public void CanLockWithProfile_FollowsCoexistenceFlag(string? profileId, bool expectedWhileExclusive)
    {
        // While UniFi keeps the lock and profiles exclusive, only a profile-free port can be locked
        var expected = PortLockSupport.LockCoexistsWithPortProfile || expectedWhileExclusive;
        PortLockSupport.CanLockWithProfile(profileId).Should().Be(expected);
    }

    [Fact]
    public void ParseVersion_TwoPartVersion_ComparesAgainstThreePart()
    {
        // "10.6" normalizes to 10.6.0.0, which is below the 10.6.101 minimum
        PortLockSupport.IsAvailable("10.6", "7.6.2").Should().BeFalse();
        PortLockSupport.IsAvailable("10.7", "7.6.2").Should().BeTrue();
    }
}
