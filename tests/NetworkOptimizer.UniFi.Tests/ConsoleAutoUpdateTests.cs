using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// The two places /api/system records that something updates itself. They are set independently,
/// so reading only the console's rider misses an application on its own schedule.
/// </summary>
public class ConsoleAutoUpdateTests
{
    private static UniFiConsoleSystemInfo Parse(string json) =>
        JsonSerializer.Deserialize<UniFiConsoleSystemInfo>(json)!;

    [Fact]
    public void AnApplicationOnItsOwnSchedule_AutoUpdates_EvenWhenTheConsoleRiderIsOff()
    {
        var info = Parse("""
            {
              "firmware": {
                "autoUpdate": {
                  "schedule": { "frequency": "daily", "hour": 0 },
                  "includeApplications": false
                }
              },
              "apps": {
                "controllers": [
                  { "name": "network", "type": "controller", "version": "10.6.94",
                    "updateSchedule": { "frequency": "daily", "hour": 0 } }
                ]
              }
            }
            """);

        info.Firmware!.AutoUpdate!.IncludeApplications.Should().BeFalse();
        info.Apps!.Controllers.Single(c => c.Name == UniFiConsoleController.NetworkName)
            .AutoUpdates.Should().BeTrue();
    }

    [Fact]
    public void AnApplicationWithNoSchedule_DoesNotAutoUpdate()
    {
        var info = Parse("""
            {
              "apps": {
                "controllers": [
                  { "name": "network", "type": "controller", "updateSchedule": null },
                  { "name": "innerspace", "type": "controller" }
                ]
              }
            }
            """);

        info.Apps!.Controllers.Should().OnlyContain(c => !c.AutoUpdates);
    }

    [Fact]
    public void TheConsoleSchedulePresenceIsWhatCounts_NotItsShape()
    {
        Parse("""{"firmware":{"autoUpdate":{"schedule":{"frequency":"weekly","hour":0,"day":0}}}}""")
            .Firmware!.AutoUpdate!.IsScheduled.Should().BeTrue();

        Parse("""{"firmware":{"autoUpdate":{"schedule":null}}}""")
            .Firmware!.AutoUpdate!.IsScheduled.Should().BeFalse();
    }
}
