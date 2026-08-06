using System.Net;
using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests;

/// <summary>
/// A name can resolve to more than one address, and not all of them are hosts. Picking the wrong
/// one decides the wrong answer about where the thing lives.
/// </summary>
public class SelectUsableAddressTests
{
    private static IPAddress[] Ips(params string[] values) =>
        values.Select(IPAddress.Parse).ToArray();

    [Fact]
    public void Discards_the_unspecified_answer_and_takes_the_real_one()
    {
        // A gateway whose name carries a good A record behind a junk AAAA - which is what a
        // search-domain suffix that exists but answers nothing looks like.
        NetworkUtilities.SelectUsableAddress(Ips("::", "192.168.1.1"))
            .Should().Be(IPAddress.Parse("192.168.1.1"));
    }

    [Fact]
    public void Discards_an_unspecified_v4_answer_in_favour_of_a_real_v6_one()
    {
        // Rejecting has to happen BEFORE preferring, or a junk A record outranks a good AAAA.
        NetworkUtilities.SelectUsableAddress(Ips("0.0.0.0", "2001:db8::1"))
            .Should().Be(IPAddress.Parse("2001:db8::1"));
    }

    [Fact]
    public void Prefers_ipv4_when_both_are_real()
    {
        NetworkUtilities.SelectUsableAddress(Ips("2001:db8::1", "203.0.113.10"))
            .Should().Be(IPAddress.Parse("203.0.113.10"));
    }

    [Fact]
    public void Takes_ipv6_when_that_is_all_there_is()
    {
        NetworkUtilities.SelectUsableAddress(Ips("2001:db8::1"))
            .Should().Be(IPAddress.Parse("2001:db8::1"));
    }

    [Fact]
    public void Finds_nothing_when_no_answer_is_a_host()
    {
        NetworkUtilities.SelectUsableAddress(Ips("::", "0.0.0.0")).Should().BeNull();
        NetworkUtilities.SelectUsableAddress(Array.Empty<IPAddress>()).Should().BeNull();
    }
}
