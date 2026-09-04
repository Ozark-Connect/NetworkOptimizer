using Xunit;
using NetworkOptimizer.Web.Services.ApAgent;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// Neighbor report elements are forwarded to the client untouched, so a misread of one is invisible
/// until a device lands on the wrong band. The operating classes here are ones production access
/// points actually emit: 81 (2.4 GHz), 128 (5 GHz), 134 (6 GHz).
/// </summary>
public class ApAgentRoamCandidatesTests
{
    /// <summary>Builds an element: BSSID(6) + BSSID info(4) + operating class(1) + channel(1).</summary>
    private static string Element(string bssidHex, int opClass, int channel)
        => bssidHex + "00000000" + opClass.ToString("x2") + channel.ToString("x2") + "00";

    private static readonly string Band24 = Element("aabbccddee01", 81, 6);
    private static readonly string Band5 = Element("aabbccddee02", 128, 100);
    private static readonly string Band6 = Element("aabbccddee03", 134, 101);

    [Theory]
    [InlineData("6", 3)]
    [InlineData("6e", 3)]
    [InlineData("5", 2)]
    [InlineData("na", 2)]
    [InlineData("2.4", 1)]
    [InlineData("ng", 1)]
    public void BandRank_accepts_both_spellings(string band, int expected)
        => Assert.Equal(expected, ApAgentRoamCandidates.BandRank(band));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("60")]
    [InlineData("5ghz")]
    public void BandRank_is_zero_for_anything_unrecognized(string? band)
        => Assert.Equal(0, ApAgentRoamCandidates.BandRank(band));

    [Fact]
    public void BandRank_orders_bands_best_first()
    {
        Assert.True(ApAgentRoamCandidates.BandRank("6") > ApAgentRoamCandidates.BandRank("5"));
        Assert.True(ApAgentRoamCandidates.BandRank("5") > ApAgentRoamCandidates.BandRank("2.4"));
        Assert.True(ApAgentRoamCandidates.BandRank("2.4") > ApAgentRoamCandidates.BandRank("wat"));
    }

    [Theory]
    [InlineData(81, "2.4")]
    [InlineData(84, "2.4")]
    [InlineData(115, "5")]
    [InlineData(128, "5")]
    [InlineData(130, "5")]
    [InlineData(131, "6")]
    [InlineData(136, "6")]
    public void BandOf_reads_the_operating_class(int opClass, string expected)
        => Assert.Equal(expected, ApAgentRoamCandidates.BandOf(Element("aabbccddee01", opClass, 1)));

    [Theory]
    [InlineData(80)]
    [InlineData(114)]
    [InlineData(137)]
    public void BandOf_is_null_for_an_operating_class_outside_the_known_ranges(int opClass)
        => Assert.Null(ApAgentRoamCandidates.BandOf(Element("aabbccddee01", opClass, 1)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("aabbccddee01000000")]
    public void BandOf_is_null_for_a_truncated_element(string? element)
        => Assert.Null(ApAgentRoamCandidates.BandOf(element));

    [Fact]
    public void BandOf_is_null_rather_than_throwing_on_non_hex()
        => Assert.Null(ApAgentRoamCandidates.BandOf("aabbccddee0100000000" + "zz" + "01"));

    [Fact]
    public void Describe_renders_bssid_band_and_channel()
        => Assert.Equal("aa:bb:cc:dd:ee:03/6GHz ch101", ApAgentRoamCandidates.Describe(Band6));

    [Fact]
    public void Describe_returns_a_short_element_unchanged_rather_than_throwing()
        => Assert.Equal("aabb", ApAgentRoamCandidates.Describe("aabb"));

    [Fact]
    public void OtherBands_excludes_the_band_the_client_is_on()
    {
        var result = ApAgentRoamCandidates.OtherBands(new[] { Band6, Band24, Band5 }, new[] { "5" });

        Assert.DoesNotContain(Band5, result);
        Assert.Equal(new[] { Band6, Band24 }, result);
    }

    [Fact]
    public void OtherBands_orders_best_first_so_an_upgrade_is_the_first_choice()
        => Assert.Equal(new[] { Band6, Band5 }, ApAgentRoamCandidates.OtherBands(new[] { Band24, Band5, Band6 }, new[] { "2.4" }));

    /// <summary>An MLO client holds several bands at once, so all of them are excluded.</summary>
    [Fact]
    public void OtherBands_excludes_every_band_a_multi_link_client_holds()
        => Assert.Equal(new[] { Band24 }, ApAgentRoamCandidates.OtherBands(new[] { Band6, Band5, Band24 }, new[] { "5", "6" }));

    [Fact]
    public void OtherBands_is_empty_when_the_client_holds_every_band_offered()
        => Assert.Empty(ApAgentRoamCandidates.OtherBands(new[] { Band6, Band5, Band24 }, new[] { "2.4", "5", "6" }));

    /// <summary>
    /// An element whose band cannot be read is dropped rather than offered: it would be sent to the
    /// client as a candidate we cannot reason about, on an intent that is entirely about band.
    /// </summary>
    [Fact]
    public void OtherBands_drops_an_element_with_an_unreadable_band()
        => Assert.Equal(new[] { Band6 }, ApAgentRoamCandidates.OtherBands(new[] { "aabb", Band6 }, new[] { "5" }));

    [Fact]
    public void OtherBands_returns_everything_when_the_current_band_is_unknown()
        => Assert.Equal(3, ApAgentRoamCandidates.OtherBands(new[] { Band6, Band5, Band24 }, Array.Empty<string>()).Count);
}
