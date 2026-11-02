using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class SpeedTestHighScoreTests
{
    private const string Ip = "192.0.2.70";
    private const string OldIp = "198.51.100.70";
    private const string Mac = "aa:bb:cc:dd:ee:ff";
    private const string OtherMac = "00:11:22:33:44:55";

    private static HighScoreHistoryEntry Entry(string ip, string? mac, double mbps)
        => new(ip, mac, mbps * 1_000_000);

    private static bool Check(double mbps, string? mac, params HighScoreHistoryEntry[] history)
        => SpeedTestHighScore.IsNewHighScore(mbps * 1_000_000, Ip, mac, history);

    [Fact]
    public void Beating_the_address_history_is_a_high_score()
        => Assert.True(Check(1500, null, Entry(Ip, null, 1200), Entry(Ip, null, 1400)));

    [Fact]
    public void Matching_the_best_is_not_a_high_score()
        => Assert.False(Check(1400, null, Entry(Ip, null, 1200), Entry(Ip, null, 1400)));

    [Fact]
    public void Fewer_than_two_earlier_results_is_not_a_high_score()
        => Assert.False(Check(1500, Mac, Entry(Ip, Mac, 1200)));

    [Fact]
    public void An_all_zero_history_is_not_a_high_score()
        => Assert.False(Check(1500, null, Entry(Ip, null, 0), Entry(Ip, null, 0)));

    /// <summary>The reported case: the device's best was set at its old address before an IP change.</summary>
    [Fact]
    public void A_best_from_an_earlier_address_still_counts()
        => Assert.False(Check(1544.6, Mac,
            Entry(OldIp, Mac, 1636.5),
            Entry(Ip, Mac, 1518.5),
            Entry(Ip, Mac, 1236.8)));

    [Fact]
    public void Results_from_an_earlier_address_count_toward_the_minimum()
        => Assert.True(Check(1700, Mac, Entry(OldIp, Mac, 1636.5), Entry(OldIp, Mac, 1500)));

    /// <summary>With no MAC the rule is the address-only rule, so another address's results are ignored.</summary>
    [Fact]
    public void Without_a_mac_only_the_address_counts()
        => Assert.True(Check(1500, null,
            Entry(OldIp, Mac, 1900),
            Entry(Ip, null, 1200),
            Entry(Ip, Mac, 1400)));

    /// <summary>Never fewer results than the address-only rule: rows at this address count whatever their MAC.</summary>
    [Fact]
    public void Address_results_count_even_under_another_mac()
        => Assert.False(Check(1500, Mac,
            Entry(Ip, OtherMac, 1600),
            Entry(Ip, Mac, 1200),
            Entry(Ip, null, 1300)));

    [Fact]
    public void An_unrelated_device_is_ignored()
        => Assert.True(Check(1500, Mac,
            Entry(OldIp, OtherMac, 1900),
            Entry(Ip, Mac, 1200),
            Entry(Ip, Mac, 1400)));

    [Fact]
    public void Mac_matching_ignores_case()
        => Assert.False(Check(1500, Mac.ToUpperInvariant(),
            Entry(OldIp, Mac, 1600),
            Entry(Ip, null, 1200)));

    [Theory]
    [InlineData(SpeedTestDirection.BrowserToServer, 100, 900, 900)]
    [InlineData(SpeedTestDirection.OpenSpeedTestWan, 100, 900, 100)]
    public void Client_download_follows_the_stored_perspective(
        SpeedTestDirection direction, double downloadBps, double uploadBps, double expected)
        => Assert.Equal(expected, SpeedTestHighScore.ClientDownloadBps(direction, downloadBps, uploadBps));
}
