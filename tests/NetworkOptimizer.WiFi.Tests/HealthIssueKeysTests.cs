using FluentAssertions;
using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

/// <summary>
/// A key is what an acknowledgment is stored against, so the same subject must produce the
/// same key whatever order or casing it arrives in, and never carry a number that changes.
/// </summary>
public class HealthIssueKeysTests
{
    [Fact]
    public void Site_scope_is_the_rule_alone()
    {
        HealthIssueKeys.For("WIFI-X-001").Should().Be("WIFI-X-001|site");
    }

    [Fact]
    public void Mac_sets_are_order_and_case_independent()
    {
        var a = HealthIssueKeys.Macs(new[] { "AA:BB:CC:DD:EE:02", "aa:bb:cc:dd:ee:01" });
        var b = HealthIssueKeys.Macs(new[] { "aa:bb:cc:dd:ee:01", "aa:bb:cc:dd:ee:02", "aa:bb:cc:dd:ee:01" });
        a.Should().Be(b).And.Be("aa:bb:cc:dd:ee:01+aa:bb:cc:dd:ee:02");
    }

    [Fact]
    public void Radio_scope_names_the_band_by_its_code()
    {
        HealthIssueKeys.Radio("AA:BB:CC:DD:EE:01", RadioBand.Band6GHz).Should().Be("aa:bb:cc:dd:ee:01/6e");
    }

    [Fact]
    public void Scope_parts_keep_their_order()
    {
        HealthIssueKeys.For("R", "na", "36", "x").Should().Be("R|na|36|x");
    }
}
