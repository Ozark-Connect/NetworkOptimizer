using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class ConsoleWatchTests
{
    [Fact]
    public void ParseBackups_SkipsCloudAndUnsafeNames()
    {
        const string body = """
            {"meta":{"rc":"ok"},"data":[
              {"filename":"autobackup_10.6.106_20261020_0700_1760943600000.unf","datetime":"2026-10-20T07:00:00Z","version":"10.6.106","type":"primary"},
              {"filename":"autobackup_cloud.unf","datetime":"2026-10-20T07:00:00Z","version":"10.6.106","type":"cloud"},
              {"filename":"../etc/passwd","datetime":"2026-10-20T07:00:00Z"}
            ]}
            """;

        var entries = ConsoleWatch.ParseBackups(body);

        entries.Should().ContainSingle();
        entries[0].Version.Should().Be("10.6.106");
        entries[0].TakenAt.Should().Be(new DateTime(2026, 10, 20, 7, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Thin_KeepsNewestAndEveryRolloutBackup()
    {
        var t = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var records = Enumerable.Range(1, 10).Select(i => new ConsoleBackupRecord { Id = i, FileName = $"b{i}", TakenAt = t.AddDays(i), FirmwareRollout = i == 2 }).ToList();

        var thin = ConsoleWatch.Thin(records, 3);

        thin.Select(r => r.Id).Should().BeEquivalentTo([1, 3, 4, 5, 6, 7]);
    }

    [Fact]
    public void Describe_NamesAdminAndAutoUpdateChanges()
    {
        const string system1 = """{"autoBackupEnabled":true,"firmware":{"releaseChannel":"release","autoUpdate":{"schedule":{"frequency":"weekly"}}}}""";
        const string system2 = """{"autoBackupEnabled":false,"firmware":{"releaseChannel":"release","autoUpdate":{"schedule":{"frequency":"weekly"}}}}""";
        const string users1 = """{"data":[{"id":"u1","full_name":"Admin1","status":"ACTIVE","roles":[{"system_key":"owner"}]}]}""";
        const string users2 = """{"data":[{"id":"u1","full_name":"Admin1","status":"ACTIVE","roles":[{"system_key":"owner"}]},{"id":"u2","full_name":"User2","status":"ACTIVE","roles":[{"system_key":"super_administrator"}]},{"id":"u3","full_name":"NoRole","roles":[]}]}""";

        var lines = ConsoleWatch.Describe(ConsoleWatch.ConsoleSettings(system1, users1), ConsoleWatch.ConsoleSettings(system2, users2));

        lines.Should().Contain("Console admin added: User2 (super_administrator)");
        lines.Should().Contain(l => l.StartsWith("Console auto-backup: true to false"));
        lines.Should().NotContain(l => l.Contains("NoRole"));
        lines.Should().NotContain(l => l.StartsWith("UniFi OS auto-update"));
    }
}
