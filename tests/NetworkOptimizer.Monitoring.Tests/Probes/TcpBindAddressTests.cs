using System.Net;
using FluentAssertions;
using NetworkOptimizer.Monitoring.Probes;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests.Probes;

/// <summary>
/// A TCP probe binds an address, so a WAN context that names an interface has to be resolved to
/// that interface's current address at probe time. Doing it at probe time rather than at push time
/// is what keeps a DHCP or PPPoE WAN working: its address moves, and a stale one binds nothing.
/// </summary>
public class TcpBindAddressTests
{
    private static IReadOnlyList<IPAddress> NoAddresses(string _) => Array.Empty<IPAddress>();

    [Fact]
    public void IpLiteral_IsUsedDirectly()
    {
        var (address, error) = LocalProbeExecutor.ResolveTcpBindAddress("192.0.2.10", NoAddresses);

        address.Should().Be(IPAddress.Parse("192.0.2.10"));
        error.Should().BeNull();
    }

    [Fact]
    public void InterfaceName_ResolvesToItsCurrentIPv4Address()
    {
        var (address, error) = LocalProbeExecutor.ResolveTcpBindAddress(
            "eth8", _ => new[] { IPAddress.Parse("198.51.100.7") });

        address.Should().Be(IPAddress.Parse("198.51.100.7"));
        error.Should().BeNull();
    }

    [Fact]
    public void InterfaceName_SkipsIPv6AndTakesTheIPv4Address()
    {
        var (address, error) = LocalProbeExecutor.ResolveTcpBindAddress(
            "ppp0", _ => new[] { IPAddress.Parse("2001:db8::1"), IPAddress.Parse("198.51.100.7") });

        address.Should().Be(IPAddress.Parse("198.51.100.7"));
        error.Should().BeNull();
    }

    [Fact]
    public void InterfaceWithNoIPv4Address_FailsLoudlyRatherThanProbingUnbound()
    {
        // An unbound probe leaves by the default route and records another WAN's latency under
        // this one's name, which reads as data rather than as a failure.
        var (address, error) = LocalProbeExecutor.ResolveTcpBindAddress(
            "ppp0", _ => new[] { IPAddress.Parse("2001:db8::1") });

        address.Should().BeNull();
        error.Should().Contain("ppp0").And.Contain("IPv4");
    }

    [Fact]
    public void UnknownInterface_Fails()
    {
        var (address, error) = LocalProbeExecutor.ResolveTcpBindAddress("eth9", NoAddresses);

        address.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void UnsafeSourceValue_IsRejectedBeforeAnyLookup()
    {
        var looked = false;
        var (address, error) = LocalProbeExecutor.ResolveTcpBindAddress(
            "eth0; rm -rf /", _ => { looked = true; return Array.Empty<IPAddress>(); });

        address.Should().BeNull();
        error.Should().Contain("Invalid probe source");
        looked.Should().BeFalse();
    }
}
