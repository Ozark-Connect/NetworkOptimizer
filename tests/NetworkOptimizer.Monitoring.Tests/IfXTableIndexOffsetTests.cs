using NetworkOptimizer.Monitoring;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

/// <summary>
/// ifXTable is required to share ifTable's ifIndex, but some UniFi switches publish it on its
/// own index space - a US-8 returns ifDescr on 1..8 with every ifXTable row at 1000001..1000008
/// (#1067). These cover the detection that joins the two back up, and the far more important
/// property that a conforming device is never touched.
/// </summary>
public class IfXTableIndexOffsetTests
{
    private static string[] Range(int first, int count) =>
        Enumerable.Range(first, count).Select(i => i.ToString()).ToArray();

    [Fact]
    public void DetectsTheOffsetAUsEightPublishes()
    {
        var offset = SnmpPoller.DetectIfXTableIndexOffset(Range(1, 8), Range(1_000_001, 8));

        Assert.Equal(1_000_000, offset);
    }

    [Fact]
    public void ReturnsZeroWhenTheTablesAlreadyAgree()
    {
        var offset = SnmpPoller.DetectIfXTableIndexOffset(Range(1, 8), Range(1, 8));

        Assert.Equal(0, offset);
    }

    [Fact]
    public void ReturnsZeroWhenAnyIndexIsSharedBetweenTheTables()
    {
        // 1..8 against 5..12 is a uniform +4 shift, but the two overlap on 5..8. Rebasing that
        // would move real rows onto other ports, so a uniform offset alone must not be enough.
        var offset = SnmpPoller.DetectIfXTableIndexOffset(Range(1, 8), Range(5, 8));

        Assert.Equal(0, offset);
    }

    [Fact]
    public void ReturnsZeroWhenTheShiftIsNotUniform()
    {
        var offset = SnmpPoller.DetectIfXTableIndexOffset(
            new[] { "1", "2", "3" },
            new[] { "1000001", "1000002", "2000003" });

        Assert.Equal(0, offset);
    }

    [Fact]
    public void ReturnsZeroWhenTheTablesDifferInSize()
    {
        // A device that simply omits ifXTable rows for some interfaces is sparse, not offset,
        // and the per-index lookups already handle a miss.
        var offset = SnmpPoller.DetectIfXTableIndexOffset(Range(1, 8), Range(1_000_001, 6));

        Assert.Equal(0, offset);
    }

    [Fact]
    public void ReturnsZeroWhenEitherTableIsEmpty()
    {
        Assert.Equal(0, SnmpPoller.DetectIfXTableIndexOffset(Array.Empty<string>(), Range(1_000_001, 8)));
        Assert.Equal(0, SnmpPoller.DetectIfXTableIndexOffset(Range(1, 8), Array.Empty<string>()));
    }

    [Fact]
    public void IgnoresIndexesThatAreNotNumeric()
    {
        // A malformed walk should fall out as "no offset" rather than throw mid-poll.
        var offset = SnmpPoller.DetectIfXTableIndexOffset(
            new[] { "1", "not-an-index", "3" },
            new[] { "1000001", "1000003" });

        Assert.Equal(1_000_000, offset);
    }

    [Fact]
    public void HandlesUnsortedWalkOrder()
    {
        // BulkWalk order is the device's business, and the offset must not depend on it.
        var offset = SnmpPoller.DetectIfXTableIndexOffset(
            new[] { "3", "1", "2" },
            new[] { "1000002", "1000003", "1000001" });

        Assert.Equal(1_000_000, offset);
    }

    [Fact]
    public void DetectsANegativeOffset()
    {
        // Nothing says the extension table has to sit above ifTable; the join is the same.
        var offset = SnmpPoller.DetectIfXTableIndexOffset(Range(1_000_001, 8), Range(1, 8));

        Assert.Equal(-1_000_000, offset);
    }
}
