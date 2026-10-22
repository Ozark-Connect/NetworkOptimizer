using FluentAssertions;
using NetworkOptimizer.Web.Services.CellularModemProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class AtDeviceShellTests
{
    [Fact]
    public void BuildTwoOpenCommand_WithFallback_OnlySwapsTheTimeoutWord()
    {
        var gl = AtDeviceShell.BuildTwoOpenCommand("/dev/mhi_DUN", "AT+CGMR", "timeout");
        var generic = AtDeviceShell.BuildTwoOpenCommand("/dev/mhi_DUN", "AT+CGMR", "_to");

        generic.Should().Be(
            """echo -ne 'AT+CGMR\r' > /dev/mhi_DUN; _to 3 sh -c 'while IFS= read -r l; do echo "$l"; case "$l" in OK*|ERROR*|+CME\ ERROR*|+CMS\ ERROR*) break;; esac; done < /dev/mhi_DUN'""");
        generic.Should().Be(gl.Replace("; timeout 3 ", "; _to 3 "));
    }

    [Fact]
    public void BuildSingleOpenCommand_OpensOnceAndPassesDeviceAndCommandAsArguments()
    {
        var command = AtDeviceShell.BuildSingleOpenCommand("/dev/ttyUSB2", "AT+QENG=\"servingcell\"");

        command.Should().Be(
            """_to 3 sh -c 'exec 3<>"$1"; printf "%s\r" "$2" >&3; while IFS= read -r l <&3; do echo "$l"; case "$l" in OK*|ERROR*|+CME\ ERROR*|+CMS\ ERROR*) break;; esac; done' sh /dev/ttyUSB2 'AT+QENG="servingcell"'""");
    }

    [Fact]
    public void TimeoutPrelude_ProbesATimeoutThatWorks_AndFallsBackToAWatchdog()
    {
        AtDeviceShell.TimeoutPrelude.Should().Be(
            """if timeout 1 true >/dev/null 2>&1; then _to() { timeout "$@"; }; else _to() { t=$1; shift; "$@" & p=$!; (sleep "$t"; kill $p 2>/dev/null) >/dev/null 2>&1 & w=$!; wait $p; r=$?; kill $w 2>/dev/null; return $r; }; fi""");
    }

    [Theory]
    [InlineData("/dev/ttyUSB2; reboot")]
    [InlineData("/dev/../etc/passwd")]
    [InlineData("$(id)")]
    [InlineData("")]
    public void Builders_RejectDeviceInjection(string device)
    {
        var single = () => AtDeviceShell.BuildSingleOpenCommand(device, "AT");
        var two = () => AtDeviceShell.BuildTwoOpenCommand(device, "AT", "_to");

        single.Should().Throw<ArgumentException>();
        two.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("AT'; reboot; '")]
    [InlineData("AT\nreboot")]
    [InlineData("AT\rreboot")]
    public void Builders_RejectAtCommandsThatBreakTheQuoting(string at)
    {
        var single = () => AtDeviceShell.BuildSingleOpenCommand("/dev/ttyUSB2", at);
        var two = () => AtDeviceShell.BuildTwoOpenCommand("/dev/mhi_DUN", at, "_to");

        single.Should().Throw<ArgumentException>();
        two.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SplitSections_KeysByMarker_AndDropsTextBeforeTheFirst()
    {
        var output = "banner\n===A===\none\n===B===\ntwo\nthree\n";

        var sections = AtDeviceShell.SplitSections(output, new[] { ("a", "===A==="), ("b", "===B===") });

        sections.Should().HaveCount(2);
        sections["a"].Trim().Should().Be("one");
        sections["b"].Trim().Should().Be("two" + Environment.NewLine + "three");
    }
}
