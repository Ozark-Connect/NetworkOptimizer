using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>Which alert incidents Change Control places next to config changes.</summary>
public sealed class ChangeIncidentsTests : IDisposable
{
    private static AlertHistoryEntry Alert(string type, string? context = null, string? ip = null, string message = "") =>
        new() { EventType = type, ContextJson = context, DeviceIp = ip, Message = message };

    [Theory]
    [InlineData("device.offline")]
    [InlineData("device.gateway_high_cpu")]
    [InlineData("monitoring.health_check_failed")]
    [InlineData("speedtest.regression")]
    [InlineData("wifi.congestion")]
    public void DeviceAndLanAlerts_AreLinked(string type) =>
        ChangeIncidents.Classify(Alert(type)).Should().Be(IncidentRelevance.Linked);

    [Theory]
    [InlineData("monitoring.wan_outage")]
    [InlineData("monitoring.wan_outage_partial")]
    [InlineData("wan.speed_degradation")]
    [InlineData("wan.link_speed_degraded")]
    public void WanAlerts_AreListedNotLinked(string type) =>
        ChangeIncidents.Classify(Alert(type)).Should().Be(IncidentRelevance.Wan);

    [Theory]
    [InlineData("starlink.dish_alert")]
    [InlineData("cable_modem.uncorrectable_errors")]
    [InlineData("ont.pon_link_down")]
    [InlineData("cellular.network_downgrade")]
    [InlineData("monitoring.sfp_rx_power")]
    [InlineData("threats.attack_chain_attempt")]
    [InlineData("audit.critical_findings")]
    [InlineData("rollout.started")]
    [InlineData("schedule.task_failed")]
    [InlineData("agent.offline")]
    [InlineData("console.connection_failed")]
    [InlineData("device.high_temperature")]
    [InlineData("device.recovered")]
    [InlineData("monitoring.target_recovered")]
    [InlineData("monitoring.wan_recovered")]
    [InlineData("some.future_type")]
    public void OtherAlerts_AreUnrelated(string type) =>
        ChangeIncidents.Classify(Alert(type)).Should().Be(IncidentRelevance.Unrelated);

    [Theory]
    [InlineData("Fabric", "192.0.2.10", IncidentRelevance.Linked)]
    [InlineData("Custom", "10.0.0.5", IncidentRelevance.Linked)]
    [InlineData("Custom", "vps.example.com", IncidentRelevance.Unrelated)]
    [InlineData("Custom", "203.0.113.9", IncidentRelevance.Unrelated)]
    [InlineData("Transit", "198.51.100.1", IncidentRelevance.Unrelated)]
    [InlineData("AccessIsp", "198.51.100.2", IncidentRelevance.Unrelated)]
    [InlineData("InternetService", "9.9.9.9", IncidentRelevance.Unrelated)]
    public void TargetAlerts_FollowTheTargetType(string targetType, string address, IncidentRelevance expected)
    {
        var context = $$"""{"target_id":"t1","target_type":"{{targetType}}"}""";
        ChangeIncidents.Classify(Alert("monitoring.target_offline", context, address)).Should().Be(expected);
        ChangeIncidents.Classify(Alert("monitoring.target_sustained_loss", context, address)).Should().Be(expected);
    }

    [Theory]
    [InlineData("CommandedReboot", IncidentRelevance.Linked)]
    [InlineData("KernelPanic", IncidentRelevance.Linked)]
    [InlineData("Watchdog", IncidentRelevance.Linked)]
    [InlineData("PowerLoss", IncidentRelevance.Unrelated)]
    [InlineData("PowerCycle", IncidentRelevance.Unrelated)]
    [InlineData("FirmwareUpgrade", IncidentRelevance.Unrelated)]
    public void Reboots_FollowTheCategory(string category, IncidentRelevance expected) =>
        ChangeIncidents.Classify(Alert("device.rebooted", $$"""{"reboot_category":"{{category}}"}"""))
            .Should().Be(expected);

    [Theory]
    [InlineData("Switch restarted: Power loss. The device lost power.", IncidentRelevance.Unrelated)]
    [InlineData("Switch restarted: Power cycled.", IncidentRelevance.Unrelated)]
    [InlineData("Switch restarted: Firmware upgrade. 7.2.1.", IncidentRelevance.Unrelated)]
    [InlineData("AP restarted: Kernel panic. Oops#1 Part1.", IncidentRelevance.Linked)]
    [InlineData("Gateway restarted: Unexpected stop.", IncidentRelevance.Linked)]
    public void LegacyReboots_FallBackToTheMessage(string message, IncidentRelevance expected) =>
        ChangeIncidents.Classify(Alert("device.rebooted", message: message)).Should().Be(expected);

    [Theory]
    [InlineData("Starlink", IncidentRelevance.Unrelated)]
    [InlineData("WAN2 starlink backup", IncidentRelevance.Unrelated)]
    [InlineData("Fiber", IncidentRelevance.Wan)]
    public void SpeedDegradation_OnStarlinkIsUnrelated(string wanName, IncidentRelevance expected) =>
        ChangeIncidents.Classify(Alert("wan.speed_degradation", $$"""{"wan_name":"{{wanName}}"}""")).Should().Be(expected);

    [Fact]
    public void End_OneShotAlert_EndsShortlyAfterItFired_NotWhenSomeoneResolvedIt()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var incident = new AlertIncident { FirstTriggeredAt = t, LastTriggeredAt = t, ResolvedAt = t.AddDays(2) };
        ChangeIncidents.End(incident, [Alert("device.gateway_high_cpu")], t.AddDays(3)).Should().Be(t + ChangeIncidents.OneShotSpan);
    }

    [Fact]
    public void ConditionEnd_IsTheRecovery_NotTheLaterHandResolution()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var offline = new AlertHistoryEntry { EventType = "device.offline", DeviceId = "aa:bb:cc:00:00:01", TriggeredAt = t, ResolvedAt = t.AddHours(12) };
        var other = new AlertHistoryEntry { EventType = "device.recovered", DeviceId = "aa:bb:cc:00:00:02", TriggeredAt = t.AddMinutes(1) };
        var recovered = new AlertHistoryEntry { EventType = "device.recovered", DeviceId = "AABBCC000001", TriggeredAt = t.AddMinutes(2) };
        ChangeIncidents.ConditionEnd(offline, [offline, other, recovered]).Should().Be(t.AddMinutes(2));
        ChangeIncidents.ConditionEnd(offline, [offline, other]).Should().Be(t.AddHours(12));
        ChangeIncidents.End(new AlertIncident { FirstTriggeredAt = t, LastTriggeredAt = t, ResolvedAt = t.AddHours(12) }, [offline, recovered], t.AddDays(1))
            .Should().Be(t.AddMinutes(2));
    }

    [Fact]
    public void End_RecoveringAlert_RunsToItsResolution_OrNow()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        ChangeIncidents.End(new AlertIncident { FirstTriggeredAt = t, LastTriggeredAt = t, ResolvedAt = t.AddHours(5) }, [Alert("device.offline")], t.AddDays(1))
            .Should().Be(t.AddHours(5));
        ChangeIncidents.End(new AlertIncident { FirstTriggeredAt = t, LastTriggeredAt = t }, [Alert("device.offline")], t.AddHours(2))
            .Should().Be(t.AddHours(2));
    }

    [Fact]
    public void Incident_TakesItsStrongestAlert() =>
        ChangeIncidents.Classify([Alert("device.recovered"), Alert("monitoring.wan_outage"), Alert("device.offline")])
            .Should().Be(IncidentRelevance.Linked);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public ChangeIncidentsTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private NetworkOptimizerDbContext Context() =>
        new(new DbContextOptionsBuilder<NetworkOptimizerDbContext>().UseSqlite(_connection).Options);

    [Fact]
    public async Task StartedAsync_DropsUnrelatedIncidentsInTheDatabase()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await using (var ctx = Context())
        {
            ctx.Database.EnsureCreated();
            AlertIncident Add(string title, params AlertHistoryEntry[] alerts)
            {
                var incident = new AlertIncident { Title = title, CorrelationKey = "k", FirstTriggeredAt = t, LastTriggeredAt = t };
                ctx.AlertIncidents.Add(incident);
                ctx.SaveChanges();
                foreach (var a in alerts) { a.IncidentId = incident.Id; a.Source = "s"; a.Title = title; ctx.AlertHistory.Add(a); }
                ctx.SaveChanges();
                return incident;
            }
            Add("Starlink dish", Alert("starlink.dish_alert"));
            Add("Switch offline", Alert("device.offline"), Alert("device.recovered"));
            Add("WAN outage", Alert("monitoring.wan_outage"));
            Add("Transit hop", Alert("monitoring.target_offline", """{"target_type":"Transit"}""", "198.51.100.1"));
        }

        await using var read = Context();
        var repo = new ChangeControlRepository(read, NullLogger<ChangeControlRepository>.Instance);
        var result = await ChangeIncidents.StartedAsync(repo, t.AddMinutes(-1), t.AddMinutes(1), CancellationToken.None);

        result.Select(r => (r.Incident.Title, r.Relevance)).Should().BeEquivalentTo(new[]
        {
            ("Switch offline", IncidentRelevance.Linked),
            ("WAN outage", IncidentRelevance.Wan),
        });
    }
}
