using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>Stored change rows for a field that is read-only today are not shown.</summary>
public sealed class StoredReadOnlyRowsTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public StoredReadOnlyRowsTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private NetworkOptimizerDbContext Context() =>
        new(new DbContextOptionsBuilder<NetworkOptimizerDbContext>().UseSqlite(_connection).Options);

    private static StoredField Lte() => new()
    {
        Path = "lte_lan_enabled_devices", Label = "Lte LAN Enabled Devices", Old = "none", New = "aa:bb:cc:dd:ee:ff",
    };

    private static ConfigChangeObject Network(string name, params StoredField[] fields) => new()
    {
        Collection = "networkconf", ObjectId = name, ObjectName = name, Action = ConfigChangeAction.Updated,
        FieldsJson = JsonSerializer.Serialize(fields.ToList()),
    };

    [Fact]
    public void DisplayFields_DropsARowForAReadOnlyField()
    {
        ChangeControlService.DisplayFields(Network("IoT", Lte())).Fields.Should().BeEmpty();
    }

    [Fact]
    public void DisplayFields_KeepsTheOtherRowsOfAMixedObject()
    {
        var mdns = new StoredField { Path = "mdns_enabled", Label = "mDNS", Old = "off", New = "on" };

        var fields = ChangeControlService.DisplayFields(Network("IoT", Lte(), mdns)).Fields;

        fields.Should().ContainSingle().Which.Path.Should().Be("mdns_enabled");
    }

    [Fact]
    public async Task SummariesAsync_IsEmpty_WhenTheOnlyRowWasAReadOnlyField()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        ConfigChange change;
        await using (var ctx = Context())
        {
            ctx.Database.EnsureCreated();
            var repo = new ChangeControlRepository(ctx, NullLogger<ChangeControlRepository>.Instance);
            change = await repo.AddChangeAsync(new ConfigChange
            {
                OccurredAt = t, DetectedAt = t, Kind = ConfigChangeKind.Config, ObjectCount = 1,
                Summary = "Network IoT: Lte LAN Enabled Devices set to aa:bb:cc:dd:ee:ff",
            }, [Network("IoT", Lte())], CancellationToken.None);
        }

        await using var read = Context();
        var summaries = await ChangeControlService.SummariesAsync(
            new ChangeControlRepository(read, NullLogger<ChangeControlRepository>.Instance), [change], CancellationToken.None);

        summaries[change.Id].Should().BeEmpty();
    }
}
