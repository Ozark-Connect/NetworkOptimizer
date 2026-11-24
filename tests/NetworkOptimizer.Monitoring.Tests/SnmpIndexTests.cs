using FluentAssertions;
using Lextm.SharpSnmpLib;
using NetworkOptimizer.Monitoring;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

/// <summary>An SNMP walk indexed by ifIndex: TimeTicks (ifLastChange) come through as raw hundredths, not a formatted duration.</summary>
public class SnmpIndexTests
{
    [Fact]
    public void IndexByIfIndex_TimeTicksAsRawHundredths_OtherValuesAsText()
    {
        const string ifLastChange = "1.3.6.1.2.1.2.2.1.9";
        var vars = new List<Variable>
        {
            new(new ObjectIdentifier(ifLastChange + ".3"), new TimeTicks(123456)),
            new(new ObjectIdentifier(ifLastChange + ".4"), new Integer32(7)),
            new(new ObjectIdentifier("1.3.6.1.2.1.2.2.1.8.3"), new Integer32(1)),
        };
        var byIndex = SnmpPoller.IndexByIfIndex(vars, ifLastChange);
        byIndex.Should().HaveCount(2);
        byIndex["3"].Should().Be("123456");
        byIndex["4"].Should().Be("7");
    }
}