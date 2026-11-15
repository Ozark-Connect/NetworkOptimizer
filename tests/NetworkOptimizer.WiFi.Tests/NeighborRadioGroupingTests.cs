using FluentAssertions;
using NetworkOptimizer.WiFi.Helpers;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class NeighborRadioGroupingTests
{
    private static Dictionary<string, List<NeighborSighting>> Sightings(params (string Bssid, int Observer, int Channel, int Signal)[] rows) =>
        rows.GroupBy(r => r.Bssid)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => new NeighborSighting(r.Observer, r.Channel, 80, r.Signal, 1.0)).ToList(),
                StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("00:11:22:33:44:50", "02:11:22:33:44:50")] // locally administered variant of the base
    [InlineData("00:11:22:33:44:50", "06:11:22:33:44:50")]
    [InlineData("02:11:22:33:44:50", "0a:11:22:33:44:50")] // two locally administered variants
    [InlineData("00:11:22:33:44:50", "00:11:22:33:44:53")] // last octet incremented
    [InlineData("00:11:22:33:44:50", "02:11:22:33:44:5f")] // both
    public void AreAddressVariants_OneBaseAddress_True(string a, string b)
    {
        NeighborRadioGrouping.AreAddressVariants(a, b).Should().BeTrue();
        NeighborRadioGrouping.AreAddressVariants(b, a).Should().BeTrue();
    }

    [Theory]
    [InlineData("00:11:22:33:44:50", "00:11:22:33:45:50")] // a middle octet differs
    [InlineData("00:11:22:33:44:50", "00:11:22:33:44:60")] // last octet too far
    [InlineData("00:11:22:33:44:50", "04:11:22:33:44:50")] // two different universal addresses
    [InlineData("00:11:22:33:44:50", "not-a-mac")]
    public void AreAddressVariants_DifferentDevices_False(string a, string b)
    {
        NeighborRadioGrouping.AreAddressVariants(a, b).Should().BeFalse();
    }

    [Fact]
    public void Group_SsidsOfOneRadio_JoinIncludingAHiddenOne()
    {
        var radios = NeighborRadioGrouping.Group(Sightings(
            ("00:11:22:33:44:50", 0, 36, -60),
            ("02:11:22:33:44:50", 0, 36, -61),
            ("06:11:22:33:44:50", 0, 36, -59),
            ("aa:bb:cc:dd:ee:ff", 0, 36, -60)));

        radios.Should().HaveCount(2);
        radios.Should().ContainSingle(r => r.Count == 3);
    }

    [Fact]
    public void Group_SameAddressFamilyOnAnotherChannel_StaysApart()
    {
        // A dual-radio device: same base address, one BSSID per band-channel.
        var radios = NeighborRadioGrouping.Group(Sightings(
            ("00:11:22:33:44:50", 0, 36, -60),
            ("02:11:22:33:44:51", 0, 149, -60)));

        radios.Should().HaveCount(2);
    }

    [Fact]
    public void Group_SignalsFarApartAtOneObserver_StaysApart()
    {
        // Two nodes of a mesh kit with adjacent addresses on one channel.
        var radios = NeighborRadioGrouping.Group(Sightings(
            ("00:11:22:33:44:50", 0, 36, -50),
            ("00:11:22:33:44:58", 0, 36, -75)));

        radios.Should().HaveCount(2);
    }

    [Fact]
    public void Group_OneObserverDisagrees_StaysApart()
    {
        var radios = NeighborRadioGrouping.Group(Sightings(
            ("00:11:22:33:44:50", 0, 36, -60),
            ("02:11:22:33:44:50", 0, 36, -60),
            ("00:11:22:33:44:50", 1, 36, -55),
            ("02:11:22:33:44:50", 1, 36, -80)));

        radios.Should().HaveCount(2);
    }

    [Fact]
    public void Group_NoCommonObserver_StaysApart()
    {
        var radios = NeighborRadioGrouping.Group(Sightings(
            ("00:11:22:33:44:50", 0, 36, -60),
            ("02:11:22:33:44:50", 1, 36, -60)));

        radios.Should().HaveCount(2);
    }

    [Fact]
    public void Group_UnparseableBssid_IsItsOwnRadio()
    {
        var radios = NeighborRadioGrouping.Group(Sightings(
            ("garbage", 0, 36, -60),
            ("00:11:22:33:44:50", 0, 36, -60)));

        radios.Should().HaveCount(2);
    }
}
