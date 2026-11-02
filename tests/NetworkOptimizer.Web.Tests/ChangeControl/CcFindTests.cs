using FluentAssertions;
using NetworkOptimizer.Web.Components.Shared.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>The Edit tab's Collections search count.</summary>
public class CcFindTests
{
    private const string Yaml = "wlans:\n- name: IoT\n  x_passphrase: <secret:wlanconf/w1/x_passphrase>\n  networkconf_id: IoT Network\n";

    [Fact]
    public void Count_MatchesFieldNamesAndValues_IgnoringCase()
    {
        CcFind.Count(Yaml, "iot").Should().Be(2);
        CcFind.Count(Yaml, "NETWORKCONF").Should().Be(1);
    }

    [Fact]
    public void Count_SkipsMaskedSecretTokens()
    {
        // "x_passphrase" is the field once; the token's path holds it again and is not counted.
        CcFind.Count(Yaml, "x_passphrase").Should().Be(1);
        CcFind.Count(Yaml, "secret").Should().Be(0);
    }

    [Fact]
    public void Count_TreatsQueryAsLiteralText()
    {
        CcFind.Count("a: 10.0.0.1\nb: 1000\n", "0.0").Should().Be(1);
        CcFind.Count("a: [x]\n", "[x]").Should().Be(1);
    }

    [Fact]
    public void Count_IsNonOverlapping()
    {
        CcFind.Count("aaaa", "aa").Should().Be(2);
    }

    [Fact]
    public void Count_EmptyQuery_IsZero()
    {
        CcFind.Count(Yaml, "").Should().Be(0);
    }
}
