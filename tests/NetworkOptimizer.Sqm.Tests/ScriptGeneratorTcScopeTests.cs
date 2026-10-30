using System.Text.RegularExpressions;
using FluentAssertions;
using NetworkOptimizer.Sqm.Models;
using Xunit;

namespace NetworkOptimizer.Sqm.Tests;

/// <summary>
/// WAN Flood Protection puts tc filters on the WAN's ingress qdisc and trusts that Adaptive SQM never
/// touches filters or creates and deletes qdiscs (it only changes UniFi's existing classes). This fails if
/// that ever stops being true, before the two can undo each other on a gateway.
///
/// The one exception is the opt-in CAKE download shaper: it replaces the root qdisc of the IFB, which is
/// where UniFi's mirred redirect delivers ingress traffic. The WAN device, its ingress qdisc and the
/// redirect filter stay untouched.
/// </summary>
public class ScriptGeneratorTcScopeTests
{
    /// <summary>The only qdisc replacement Adaptive SQM is allowed to emit.</summary>
    private const string CakeRootReplace = "tc qdisc replace dev $device root handle $CAKE_HANDLE cake";

    [Theory]
    [InlineData(ConnectionType.CellularHome, false)]
    [InlineData(ConnectionType.Starlink, false)]
    [InlineData(ConnectionType.CellularHome, true)]
    [InlineData(ConnectionType.Starlink, true)]
    public void AdaptiveSqm_NeverAddsOrRemovesFiltersOrQdiscs(ConnectionType type, bool cakeDownload)
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
            CakeDownload = cakeDownload,
        };
        config.ApplyProfileSettings(1000);
        var baseline = config.GetProfile().GetHourlyBaseline(config.CongestionSeverity);

        foreach (var script in new ScriptGenerator(config).GenerateAllScripts(baseline).Values)
            AssertTcScope(script);
    }

    [Fact]
    public void ShaperLift_NeverAddsOrRemovesFiltersOrQdiscs()
    {
        AssertTcScope(SqmShaperLiftScript.Wrap("true", "ppp0", 1000, 500, false));
    }

    [Fact]
    public void CakeRootReplace_IsOnlyReachedForTheIfbDevice()
    {
        // apply_cake_root is the only function that replaces a root, and update_all_tc_classes is
        // the only caller; it calls it only inside the IFB branch.
        var functions = ScriptGenerator.TcFunctionsText;
        var update = functions[functions.IndexOf("update_all_tc_classes() {", StringComparison.Ordinal)..];
        var ifbBranch = update[..update.IndexOf("# Burst mode", StringComparison.Ordinal)];

        ifbBranch.Should().Contain("if [ \"$device\" = \"$IFB_DEVICE\" ]; then");
        Regex.Matches(functions, @"apply_cake_root \$device").Count
            .Should().Be(Regex.Matches(ifbBranch, @"apply_cake_root \$device").Count);
    }

    private static void AssertTcScope(string script)
    {
        script.Should().NotContain("tc filter");
        script.Should().NotMatchRegex(@"tc qdisc (add|del)");
        script.Should().NotContain("ffff:");
        script.Should().NotMatchRegex(@"tc qdisc \w+ dev \$INTERFACE");

        var replaces = Regex.Matches(script, @"tc qdisc replace[^\n]*").Select(m => m.Value).ToList();
        replaces.Should().OnlyContain(line => line.StartsWith(CakeRootReplace, StringComparison.Ordinal));
    }
}
