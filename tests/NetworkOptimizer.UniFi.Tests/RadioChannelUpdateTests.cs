using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// The channel PUT carries only the radios being moved, four fields each. Anything more would write
/// back values read earlier and could undo an edit made in UniFi Network since.
/// </summary>
public class RadioChannelUpdateTests
{
    [Fact]
    public void ToRequestJson_OneRadio_IsTheMinimalBody()
    {
        var json = RadioChannelUpdate.ToRequestJson([new RadioChannelUpdate("wifi2", "6e", 133, 160)]);

        json.Should().Be("""{"radio_table":[{"name":"wifi2","radio":"6e","channel":133,"ht":160}]}""");
    }

    [Fact]
    public void ToRequestJson_TwoRadios_ListsOnlyThoseRadios()
    {
        var json = RadioChannelUpdate.ToRequestJson(
        [
            new RadioChannelUpdate("wifi0", "ng", 6, 20),
            new RadioChannelUpdate("wifi1", "na", 36, 80)
        ]);

        json.Should().Be(
            """{"radio_table":[{"name":"wifi0","radio":"ng","channel":6,"ht":20},{"name":"wifi1","radio":"na","channel":36,"ht":80}]}""");
    }
}
