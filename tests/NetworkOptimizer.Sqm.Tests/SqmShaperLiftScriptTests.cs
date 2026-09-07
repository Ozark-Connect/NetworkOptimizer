using System.Text;
using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.Sqm.Tests;

public class SqmShaperLiftScriptTests
{
    [Fact]
    public void Wrap_ReadsRates_LiftsBothSides_AndRestoresFromAnExitTrap()
    {
        var script = SqmShaperLiftScript.Wrap("/data/uwnspeedtest --interface eth4 -duration 4", "eth4", 400, 60, rateProportionalDownloadBurst: true);

        script.Should().StartWith("#!/bin/bash\n");
        script.Should().Contain("IFB_DEVICE=\"ifbeth4\"");
        script.Should().Contain("DOWN_PROBE=\"400\"");
        script.Should().Contain("UP_PROBE=\"60\"");
        script.Should().Contain("BURST_MODE=1");
        script.Should().Contain("update_all_tc_classes() {");
        script.Should().Contain("read_root_rate_mbps() {");
        script.Should().Contain("trap restore_rates EXIT");
        script.Should().Contain("update_all_tc_classes \"$IFB_DEVICE\" \"$DOWN_PROBE\" \"$BURST_MODE\"");
        script.Should().Contain("update_all_tc_classes \"$INTERFACE\" \"$UP_PROBE\"");
        // The restore runs before the lift in file order (a trap handler), and the test command is last.
        script.IndexOf("restore_rates() {", StringComparison.Ordinal).Should().BeLessThan(script.IndexOf("\"$DOWN_PROBE\"", StringComparison.Ordinal));
        script.TrimEnd('\n').Should().EndWith("/data/uwnspeedtest --interface eth4 -duration 4");
        script.Should().NotContain("\r\n");
    }

    [Fact]
    public void Wrap_OnlyTouchesRootsThatExist()
    {
        var script = SqmShaperLiftScript.Wrap("true", "eth4", 400, 60, false);

        // Every tc write is guarded on a saved (non-empty, positive) rate for that device.
        script.Should().Contain("if [ -n \"$saved_down\" ] && [ \"$saved_down\" -gt 0 ] 2>/dev/null; then");
        script.Should().Contain("if [ -n \"$saved_up\" ] && [ \"$saved_up\" -gt 0 ] 2>/dev/null; then");
    }

    [Theory]
    [InlineData("eth4; rm -rf /")]
    [InlineData("")]
    [InlineData("eth 4")]
    public void Wrap_RejectsUnsafeInterfaceNames(string iface)
    {
        var act = () => SqmShaperLiftScript.Wrap("true", iface, 400, 60, false);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Wrap_RejectsNonPositiveProbeRates()
    {
        var down = () => SqmShaperLiftScript.Wrap("true", "eth4", 0, 60, false);
        var up = () => SqmShaperLiftScript.Wrap("true", "eth4", 400, 0, false);
        down.Should().Throw<ArgumentOutOfRangeException>();
        up.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ToRemoteCommand_CarriesTheScriptIntact_AndReturnsItsExitCode()
    {
        var script = "#!/bin/bash\necho hi\n";
        var command = SqmShaperLiftScript.ToRemoteCommand(script, "eth4");

        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        command.Should().Contain(b64);
        command.Should().Contain("/tmp/netopt-sqm-probe-eth4.sh");
        command.Should().EndWith("exit $rc");
        // No shell metacharacters from the script leak into the command line: it travels base64.
        command.Should().NotContain("echo hi");
    }
}
