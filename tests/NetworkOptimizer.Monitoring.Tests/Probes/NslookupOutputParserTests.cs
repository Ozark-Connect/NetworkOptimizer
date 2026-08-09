using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Probes;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests.Probes;

/// <summary>
/// Each fixture keeps the exact output SHAPE captured from that device class - spacing,
/// ordering, stray lines and all - with the addresses swapped for documentation ranges.
/// The dialects differ enough that inventing their shapes would prove nothing.
/// </summary>
public class NslookupOutputParserTests
{
    private static readonly ProbeTarget Host = new("example.com", ProbeMode.Icmp);
    private static readonly ProbeTarget Addr = new("1.1.1.1", ProbeMode.Icmp);
    private static readonly ProbeVantage Vantage = ProbeVantage.Server;

    // ---- Forward lookups ----

    [Fact]
    public void Bind_gateway_forward()
    {
        var output = """
            Server:		127.0.0.1
            Address:	127.0.0.1#53

            Non-authoritative answer:
            Name:	example.com
            Address: 192.0.2.10
            Name:	example.com
            Address: 192.0.2.11
            Name:	example.com
            Address: 2001:db8::2
            """;

        var r = NslookupOutputParser.Parse(output, Host, Vantage);

        r.Resolver.Should().Be("127.0.0.1");
        r.Addresses.Should().BeEquivalentTo(
            new[] { "192.0.2.10", "192.0.2.11", "2001:db8::2" });
        r.NotFound.Should().BeFalse();
        r.Kind.Should().Be(NslookupOutputParser.ResultKind);
    }

    [Fact]
    public void Busybox_ap_forward_with_two_answer_blocks()
    {
        // The AP splits A and AAAA into separate Non-authoritative answer blocks, and reports
        // the resolver port with a colon rather than a hash.
        var output = """
            Server:		192.168.99.1
            Address:	192.168.99.1:53

            Non-authoritative answer:
            Name:	example.com
            Address: 192.0.2.11
            Name:	example.com
            Address: 192.0.2.10

            Non-authoritative answer:
            Name:	example.com
            Address: 2001:db8::1
            """;

        var r = NslookupOutputParser.Parse(output, Host, Vantage);

        r.Resolver.Should().Be("192.168.99.1");
        r.Addresses.Should().BeEquivalentTo(
            new[] { "192.0.2.11", "192.0.2.10", "2001:db8::1" });
        r.Addresses.Should().NotContain("192.168.99.1", "the header address is the resolver, not an answer");
    }

    [Fact]
    public void Busybox_switch_forward_numbered_with_noise_line()
    {
        // No server line at all, numbered answers, and a can't-resolve line printed on success.
        var output = """
            nslookup: can't resolve '(null)': Name does not resolve

            Name:      example.com
            Address 1: 192.0.2.11
            Address 2: 192.0.2.10
            Address 3: 2001:db8::1
            """;

        var r = NslookupOutputParser.Parse(output, Host, Vantage);

        r.Resolver.Should().BeNull();
        r.Addresses.Should().BeEquivalentTo(
            new[] { "192.0.2.11", "192.0.2.10", "2001:db8::1" });
        r.NotFound.Should().BeFalse("the (null) line prints on successful lookups too");
    }

    [Fact]
    public void Busybox_xg_switch_forward_server_plus_numbered()
    {
        var output = """
            Server:		192.168.99.1
            Address:	192.168.99.1#53

            Name:      example.com
            Address 1: 192.0.2.10
            Address 2: 192.0.2.11
            """;

        var r = NslookupOutputParser.Parse(output, Host, Vantage);

        r.Resolver.Should().Be("192.168.99.1");
        r.Addresses.Should().BeEquivalentTo(new[] { "192.0.2.10", "192.0.2.11" });
    }

    // ---- Not found ----

    [Fact]
    public void Bind_style_nxdomain()
    {
        var output = """
            Server:		192.168.99.1
            Address:	192.168.99.1:53

            ** server can't find no-such-host-xyz.invalid: NXDOMAIN
            """;

        var r = NslookupOutputParser.Parse(output, Host, Vantage);

        r.NotFound.Should().BeTrue();
        r.Addresses.Should().BeEmpty();
        r.Success.Should().BeFalse();
    }

    [Fact]
    public void Busybox_switch_nxdomain_is_told_apart_from_its_own_noise()
    {
        // Both lines start "can't resolve"; only the second is the real answer.
        var output = """
            nslookup: can't resolve '(null)': Name does not resolve

            nslookup: can't resolve 'no-such-host-xyz.invalid': Name does not resolve
            """;

        NslookupOutputParser.Parse(output, Host, Vantage).NotFound.Should().BeTrue();
    }

    [Fact]
    public void Busybox_noise_alone_is_not_a_not_found()
    {
        var output = """
            nslookup: can't resolve '(null)': Name does not resolve

            Name:      example.com
            Address 1: 192.0.2.11
            """;

        var r = NslookupOutputParser.Parse(output, Host, Vantage);
        r.NotFound.Should().BeFalse();
        r.Success.Should().BeTrue();
    }

    // ---- Reverse lookups ----

    [Fact]
    public void Bind_reverse()
    {
        var output = """
            1.1.1.1.in-addr.arpa	name = one.one.one.one.
            """;

        var r = NslookupOutputParser.Parse(output, Addr, Vantage, reverse: true);

        r.CanonicalName.Should().Be("one.one.one.one");
        r.Addresses.Should().BeEmpty("the address is the question on a reverse lookup");
        r.Success.Should().BeTrue();
    }

    [Fact]
    public void Busybox_reverse_hangs_the_name_off_the_address_line()
    {
        var output = """
            nslookup: can't resolve '(null)': Name does not resolve

            Name:      1.1.1.1
            Address 1: 1.1.1.1 one.one.one.one
            """;

        var r = NslookupOutputParser.Parse(output, Addr, Vantage, reverse: true);

        r.CanonicalName.Should().Be("one.one.one.one");
        r.Addresses.Should().BeEmpty();
    }

    // ---- Degenerate input ----

    [Fact]
    public void Empty_output_reports_an_error_not_a_not_found()
    {
        var r = NslookupOutputParser.Parse("", Host, Vantage);

        r.ErrorMessage.Should().NotBeNull();
        r.NotFound.Should().BeFalse();
        r.Success.Should().BeFalse();
    }

    [Fact]
    public void Unrecognized_output_yields_nothing_rather_than_guessing()
    {
        var r = NslookupOutputParser.Parse("something entirely unexpected", Host, Vantage);

        r.Addresses.Should().BeEmpty();
        r.CanonicalName.Should().BeNull();
        r.Resolver.Should().BeNull();
        r.NotFound.Should().BeFalse();
    }
}
