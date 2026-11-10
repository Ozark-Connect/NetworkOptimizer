using System.Globalization;
using FluentAssertions;
using NetworkOptimizer.Sqm;
using NetworkOptimizer.Sqm.Models;
using Xunit;

namespace NetworkOptimizer.Sqm.Tests;

/// <summary>ping's rtt line reads the same on a comma-decimal host (#1373).</summary>
public class LatencyMonitorCommaCultureTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public LatencyMonitorCommaCultureTests() => CultureInfo.CurrentCulture = new CultureInfo("de-DE");

    public void Dispose() => CultureInfo.CurrentCulture = _culture;

    [Fact]
    public void ParsePingOutput_ReadsTheAverageUnderACommaCulture()
    {
        var monitor = new LatencyMonitor(new SqmConfiguration { PingHost = "192.0.2.1", Interface = "eth2" });
        const string output = @"20 packets transmitted, 20 received, 0% packet loss, time 4847ms
rtt min/avg/max/mdev = 10.123/12.456/15.789/2.345 ms";

        monitor.ParsePingOutput(output).Should().Be(12.456);
    }
}
