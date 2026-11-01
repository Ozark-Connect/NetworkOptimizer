using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring.PathLocalization;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring.PathLocalization;

public class PathEpisodeRoleTests
{
    private static MonitoringTarget Target(MonitoringTargetType type, string address, bool? isLocal = null, string? wan = null) => new()
    {
        TargetId = "t",
        Name = "T",
        TargetType = type,
        Address = address,
        IsLocal = isLocal,
        WanInterface = wan
    };

    [Fact]
    public void LocalCustomTarget_HasNoRole()
    {
        PathEpisodeWatcher.RoleOf(Target(MonitoringTargetType.Custom, "192.168.1.10")).Should().BeNull();
        PathEpisodeWatcher.RoleOf(Target(MonitoringTargetType.Custom, "nas.local", isLocal: true)).Should().BeNull();
    }

    [Fact]
    public void PublicOrWanPinnedCustomTarget_IsAPathTarget()
    {
        PathEpisodeWatcher.RoleOf(Target(MonitoringTargetType.Custom, "198.51.100.20")).Should().Be(GateRole.Path);
        PathEpisodeWatcher.RoleOf(Target(MonitoringTargetType.Custom, "speedtest.example.net", isLocal: false)).Should().Be(GateRole.Path);
        // Measured over a WAN, so upstream whatever its address reads.
        PathEpisodeWatcher.RoleOf(Target(MonitoringTargetType.Custom, "192.168.100.1", wan: "wan2")).Should().Be(GateRole.Path);
    }

    [Fact]
    public void InternetServiceTarget_IsAPathTargetWhateverItsAddress()
    {
        PathEpisodeWatcher.RoleOf(Target(MonitoringTargetType.InternetService, "203.0.113.5")).Should().Be(GateRole.Path);
    }
}
