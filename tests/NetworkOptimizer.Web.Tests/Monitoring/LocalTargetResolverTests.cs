using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Whether a target is on this network decides where it appears, whether a vantage may adopt it,
/// and whether a metered WAN may slow it - so a wrong answer is worse than no answer.
/// </summary>
public class LocalTargetResolverTests
{
    private static MonitoringTarget Target(string address, bool? isLocal = null,
        MonitoringTargetType type = MonitoringTargetType.Custom) => new()
        {
            TargetId = "t",
            Name = "t",
            Address = address,
            TargetType = type,
            IsLocal = isLocal,
        };

    [Fact]
    public void Fabric_is_local_whatever_address_it_wears()
    {
        LocalTargetResolver.IsLocal(Target("203.0.113.10", isLocal: false, type: MonitoringTargetType.Fabric))
            .Should().BeTrue();
    }

    [Fact]
    public void Prefers_the_resolved_answer_over_the_address()
    {
        // A name that resolved onto this network, and one that resolved off it. Neither could be
        // told from the other by looking at the text.
        LocalTargetResolver.IsLocal(Target("cloudkey.example", isLocal: true)).Should().BeTrue();
        LocalTargetResolver.IsLocal(Target("speedtest.example", isLocal: false)).Should().BeFalse();
    }

    [Theory]
    [InlineData("192.168.1.5", true)]
    [InlineData("10.0.0.9", true)]
    [InlineData("172.20.4.1", true)]
    [InlineData("203.0.113.10", false)]
    [InlineData("cloudkey.example", false)]
    public void Falls_back_to_the_address_when_nothing_has_resolved_it(string address, bool expected)
    {
        // A literal answers itself; a name cannot, and reads as not-local until DNS says otherwise.
        LocalTargetResolver.IsLocal(Target(address)).Should().Be(expected);
    }

    [Theory]
    [InlineData("192.168.1.5", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("203.0.113.10", false)]
    public async Task Resolves_a_literal_without_asking_dns(string address, bool expected)
    {
        var (isLocal, ip) = await LocalTargetResolver.ResolveAsync(address);

        isLocal.Should().Be(expected);
        ip.Should().Be(address);
    }

    /// <summary>
    /// A search domain that exists but answers nothing hands back the unspecified address. It
    /// parses as an address and is not any host, so settling on it would file a LAN device as
    /// reached over a WAN. Which of several answers gets picked is NetworkUtilities'
    /// SelectUsableAddress; this only pins that an unusable one never settles anything.
    /// </summary>
    [Theory]
    [InlineData("::")]
    [InlineData("0.0.0.0")]
    public async Task Refuses_to_settle_on_an_unspecified_address(string address)
    {
        var (isLocal, _) = await LocalTargetResolver.ResolveAsync(address);

        isLocal.Should().BeNull();
    }

    [Fact]
    public async Task Leaves_an_empty_address_unresolved()
    {
        (await LocalTargetResolver.ResolveAsync("")).IsLocal.Should().BeNull();
        (await LocalTargetResolver.ResolveAsync(null)).IsLocal.Should().BeNull();
    }
}
