using FluentAssertions;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Threats.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class InfrastructureFiltersTests
{
    private static Func<string, string?> Env(Dictionary<string, string> values) =>
        name => values.TryGetValue(name, out var v) ? v : null;

    [Fact]
    public void NotInContainer_UsesDetectedAddress()
    {
        var ip = InfrastructureFilters.DetectSelfIpv4(
            Env(new()), () => ["192.0.2.10"], () => "192.0.2.10");

        ip.Should().Be("192.0.2.10");
    }

    [Fact]
    public void BridgeContainer_WithoutHostIp_ReturnsNull()
    {
        var ip = InfrastructureFilters.DetectSelfIpv4(
            Env(new() { ["DOTNET_RUNNING_IN_CONTAINER"] = "true" }),
            () => ["172.18.0.5"],
            () => "172.18.0.5");

        ip.Should().BeNull();
    }

    [Fact]
    public void BridgeContainer_WithHostIp_UsesHostIp()
    {
        var ip = InfrastructureFilters.DetectSelfIpv4(
            Env(new() { ["DOTNET_RUNNING_IN_CONTAINER"] = "true", ["HOST_IP"] = "192.0.2.20" }),
            () => ["172.18.0.5"],
            () => "192.0.2.20");

        ip.Should().Be("192.0.2.20");
    }

    [Fact]
    public void HostNetworkContainer_SeesHostInterfaces_UsesDetectedAddress()
    {
        var ip = InfrastructureFilters.DetectSelfIpv4(
            Env(new() { ["DOTNET_RUNNING_IN_CONTAINER"] = "true" }),
            () => ["192.0.2.30", "172.17.0.1"],
            () => "192.0.2.30");

        ip.Should().Be("192.0.2.30");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("2001:db8::1")]
    public void UnusableDetection_ReturnsNull(string? detected)
    {
        var ip = InfrastructureFilters.DetectSelfIpv4(
            Env(new()), () => ["192.0.2.10"], () => detected);

        ip.Should().BeNull();
    }

    private static ThreatNoiseFilter SystemFilter(string ip, int? port) => new()
    {
        SourceIp = ip, DestPort = port, Category = ThreatFilterCategory.Infrastructure, IsSystem = true, Enabled = true,
        Label = $"Network Optimizer {InfrastructureFilters.LabelSuffix}",
    };

    [Fact]
    public void Plan_AddsOneFilterPerManagementPort_NeverSourceWide()
    {
        var changes = InfrastructureFilters.Plan([], [new InfrastructureSource("192.0.2.10", "Network Optimizer")]);

        changes.Add.Select(f => f.DestPort).Should().BeEquivalentTo(new int?[] { 22, 8899, 161, 5201 });
        changes.Add.Should().OnlyContain(f => f.SourceIp == "192.0.2.10" && f.DestIp == null && f.IsSystem &&
                                              f.Category == ThreatFilterCategory.Infrastructure);
        changes.Retire.Should().BeEmpty();
    }

    [Fact]
    public void Plan_RetiresTheOldSourceWideFilter_AndKeepsCurrentOnes()
    {
        var existing = new List<ThreatNoiseFilter> { SystemFilter("192.0.2.10", null), SystemFilter("192.0.2.10", 22) };

        var changes = InfrastructureFilters.Plan(existing, [new InfrastructureSource("192.0.2.10", "Network Optimizer")]);

        changes.Retire.Should().ContainSingle().Which.DestPort.Should().BeNull();
        changes.Add.Select(f => f.DestPort).Should().BeEquivalentTo(new int?[] { 8899, 161, 5201 });
    }

    [Fact]
    public void Plan_RetiresAHostThatLeft_AndPromotesOneThatReturned()
    {
        var returned = SystemFilter("192.0.2.30", 22);
        returned.IsSystem = false;
        returned.Enabled = false;
        var existing = new List<ThreatNoiseFilter> { SystemFilter("192.0.2.20", 22), returned };

        var changes = InfrastructureFilters.Plan(existing, [new InfrastructureSource("192.0.2.30", "On-Site Agent")]);

        changes.Retire.Should().ContainSingle().Which.SourceIp.Should().Be("192.0.2.20");
        changes.Promote.Should().ContainSingle().Which.Should().BeSameAs(returned);
        changes.Add.Select(f => f.DestPort).Should().BeEquivalentTo(new int?[] { 8899, 161, 5201 });
    }

    [Fact]
    public void Plan_LeavesUserFiltersAlone()
    {
        var mine = new ThreatNoiseFilter { SourceIp = "192.0.2.40", Category = ThreatFilterCategory.Infrastructure, Enabled = true };

        var changes = InfrastructureFilters.Plan([mine], []);

        changes.Retire.Should().BeEmpty();
        changes.Promote.Should().BeEmpty();
        changes.Add.Should().BeEmpty();
    }
}
