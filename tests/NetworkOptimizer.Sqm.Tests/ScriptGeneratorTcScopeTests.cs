using FluentAssertions;
using NetworkOptimizer.Sqm.Models;
using Xunit;

namespace NetworkOptimizer.Sqm.Tests;

/// <summary>
/// WAN Flood Protection puts tc filters on the WAN's ingress qdisc and trusts that Adaptive SQM never
/// touches filters or creates and deletes qdiscs (it only changes UniFi's existing classes). This fails if
/// that ever stops being true, before the two can undo each other on a gateway.
/// </summary>
public class ScriptGeneratorTcScopeTests
{
    [Theory]
    [InlineData(ConnectionType.CellularHome)]
    [InlineData(ConnectionType.Starlink)]
    public void AdaptiveSqm_NeverAddsOrRemovesFiltersOrQdiscs(ConnectionType type)
    {
        var config = new SqmConfiguration
        {
            ConnectionType = type,
            ConnectionName = "Test WAN",
            Interface = "eth4",
            NominalDownloadSpeed = 200,
            NominalUploadSpeed = 30,
            ShapeUpload = true,
            PingHost = "1.1.1.1",
        };
        config.ApplyProfileSettings(1000);
        var baseline = config.GetProfile().GetHourlyBaseline(config.CongestionSeverity);

        foreach (var script in new ScriptGenerator(config).GenerateAllScripts(baseline).Values)
        {
            script.Should().NotContain("tc filter");
            script.Should().NotMatchRegex(@"tc qdisc (add|del|replace)");
        }
    }
}
