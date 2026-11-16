using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class HopLocationOverrideCacheTests
{
    private static readonly HopLocationSetting Tulsa = new("192.0.2.1", "Tulsa", null, "US", 36.15, -95.99, "Admin");

    [Fact]
    public void Set_HoldsARead_UntilInvalidated()
    {
        var cache = new HopLocationOverrideCache();
        cache.Set([Tulsa], cache.Version);

        cache.Current.Should().ContainKey("192.0.2.1");
        cache.Invalidate();
        cache.Current.Should().BeNull();
    }

    [Fact]
    public void Set_AReadThatBeganBeforeAChange_IsReturnedButNotHeld()
    {
        var cache = new HopLocationOverrideCache();
        var version = cache.Version;
        cache.Invalidate();

        cache.Set([Tulsa], version).Should().ContainKey("192.0.2.1");
        cache.Current.Should().BeNull();
    }
}
