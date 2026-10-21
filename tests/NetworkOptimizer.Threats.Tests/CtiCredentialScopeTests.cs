using FluentAssertions;
using Moq;
using NetworkOptimizer.Threats.CrowdSec;
using NetworkOptimizer.Threats.Interfaces;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

/// <summary>The public build's one CrowdSec CTI key and quota for every site.</summary>
public class CtiCredentialScopeTests
{
    private static InstanceCtiCredentialScope Scope(string? key, string? quota)
    {
        var settings = new Mock<IThreatSettingsAccessor>();
        settings.Setup(s => s.GetDecryptedSettingAsync(CrowdSecSettingKeys.ApiKey, It.IsAny<CancellationToken>())).ReturnsAsync(key);
        settings.Setup(s => s.GetSettingAsync(CrowdSecSettingKeys.DailyQuota, It.IsAny<CancellationToken>())).ReturnsAsync(quota);
        return new InstanceCtiCredentialScope(settings.Object);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task NoKey_IsNoCredential(string? key)
    {
        (await Scope(key, "100").GetAsync(null)).Should().BeNull();
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData("abc", 30)]
    [InlineData("0", 30)]
    [InlineData("500", 500)]
    public async Task Quota_FallsBackToTheFreeTier(string? quota, int expected)
    {
        (await Scope("key", quota).GetAsync(null))!.DailyQuota.Should().Be(expected);
    }

    [Fact]
    public async Task EverySite_GetsTheSameKey()
    {
        var scope = Scope("key", "100");
        (await scope.GetAsync(null)).Should().Be(await scope.GetAsync("branch-office"));
    }
}
