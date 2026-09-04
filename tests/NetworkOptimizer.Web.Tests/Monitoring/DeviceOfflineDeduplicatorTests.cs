using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

public class DeviceOfflineDeduplicatorTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstCaller_Wins()
    {
        var dedup = new DeviceOfflineDeduplicator();

        Assert.True(dedup.TryClaimSlot("aa:bb:cc:dd:ee:ff", isRecovery: false, Now));
    }

    [Fact]
    public void SecondCaller_WithinWindow_IsSuppressed()
    {
        var dedup = new DeviceOfflineDeduplicator();

        Assert.True(dedup.TryClaimSlot("aa:bb:cc:dd:ee:ff", isRecovery: false, Now));
        Assert.False(dedup.TryClaimSlot("aa:bb:cc:dd:ee:ff", isRecovery: false, Now.AddSeconds(8)));
    }

    [Fact]
    public void SecondCaller_AfterWindow_IsNotSuppressed()
    {
        var dedup = new DeviceOfflineDeduplicator();

        Assert.True(dedup.TryClaimSlot("aa:bb:cc:dd:ee:ff", isRecovery: false, Now));
        Assert.True(dedup.TryClaimSlot("aa:bb:cc:dd:ee:ff", isRecovery: false,
            Now + DeviceOfflineDeduplicator.Window + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void OfflineAndRecovery_AreIndependent()
    {
        var dedup = new DeviceOfflineDeduplicator();

        Assert.True(dedup.TryClaimSlot("aa:bb:cc:dd:ee:ff", isRecovery: false, Now));
        Assert.True(dedup.TryClaimSlot("aa:bb:cc:dd:ee:ff", isRecovery: true, Now.AddSeconds(30)));
    }

    [Fact]
    public void DifferentDevices_AreIndependent()
    {
        var dedup = new DeviceOfflineDeduplicator();

        Assert.True(dedup.TryClaimSlot("aa:bb:cc:dd:ee:ff", isRecovery: false, Now));
        Assert.True(dedup.TryClaimSlot("11:22:33:44:55:66", isRecovery: false, Now.AddSeconds(5)));
    }

    [Fact]
    public void MacNormalization_MatchesDifferentFormats()
    {
        var dedup = new DeviceOfflineDeduplicator();

        Assert.True(dedup.TryClaimSlot("AA:BB:CC:DD:EE:FF", isRecovery: false, Now));
        Assert.False(dedup.TryClaimSlot("aabbccddeeff", isRecovery: false, Now.AddSeconds(5)));
    }

    [Fact]
    public void NullMac_AlwaysAllowed()
    {
        var dedup = new DeviceOfflineDeduplicator();

        Assert.True(dedup.TryClaimSlot(null, isRecovery: false, Now));
        Assert.True(dedup.TryClaimSlot(null, isRecovery: false, Now.AddSeconds(5)));
    }

    [Fact]
    public void EmptyMac_AlwaysAllowed()
    {
        var dedup = new DeviceOfflineDeduplicator();

        Assert.True(dedup.TryClaimSlot("", isRecovery: false, Now));
        Assert.True(dedup.TryClaimSlot("", isRecovery: false, Now.AddSeconds(5)));
    }
}
