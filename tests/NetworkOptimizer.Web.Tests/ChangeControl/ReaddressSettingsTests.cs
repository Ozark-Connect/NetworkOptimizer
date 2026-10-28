using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Threats.Waf;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>Network Optimizer's own settings that a re-address moves with the site.</summary>
public sealed class ReaddressSettingsTests : IDisposable
{
    private readonly SqliteConnection _mainConnection = new("DataSource=:memory:");
    private readonly SqliteConnection _siteConnection = new("DataSource=:memory:");
    private static readonly List<SubnetMove> Moves = [new SubnetMove("n1", "Default", null, "192.0.2.0/24", "198.51.100.0/24")];

    public ReaddressSettingsTests()
    {
        _mainConnection.Open();
        _siteConnection.Open();
        using var main = Db(_mainConnection);
        main.Database.EnsureCreated();
        main.MonitoringSettings.Add(new MonitoringSettings { InfluxDbUrl = "http://192.0.2.5:8086" });
        main.SystemSettings.Add(new SystemSetting { Key = WafSettingKeys.Url, Value = "http://192.0.2.7:8044" });
        main.SystemSettings.Add(new SystemSetting { Key = WafSettingKeys.ForSite(WafSettingKeys.Url, "branch"), Value = "http://192.0.2.8:8044" });
        main.MonitoringTargets.Add(new MonitoringTarget { TargetId = "t1", Name = "NAS", Address = "192.0.2.5" });
        main.SaveChanges();
        using var site = Db(_siteConnection);
        site.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _mainConnection.Dispose();
        _siteConnection.Dispose();
    }

    private static NetworkOptimizerDbContext Db(SqliteConnection c) =>
        new(new DbContextOptionsBuilder<NetworkOptimizerDbContext>().UseSqlite(c).Options);

    [Fact]
    public async Task DefaultSite_MovesItsWafUrlAndTheInfluxDbUrl()
    {
        await using var main = Db(_mainConnection);

        var moved = await ReaddressSettings.RunAsync(main, Moves, [], apply: true, CancellationToken.None, main, site: null);

        moved.Should().Contain(m => m.Setting == ReaddressSettings.InfluxSetting && m.New == "http://198.51.100.5:8086");
        await using var check = Db(_mainConnection);
        (await check.MonitoringSettings.SingleAsync()).InfluxDbUrl.Should().Be("http://198.51.100.5:8086");
        (await check.SystemSettings.FindAsync(WafSettingKeys.Url))!.Value.Should().Be("http://198.51.100.7:8044");
        (await check.SystemSettings.FindAsync(WafSettingKeys.ForSite(WafSettingKeys.Url, "branch")))!.Value.Should().Be("http://192.0.2.8:8044");
        (await check.MonitoringTargets.SingleAsync()).Address.Should().Be("198.51.100.5");
    }

    [Fact]
    public async Task OtherSite_MovesOnlyItsOwnWafUrl_NeverTheSharedInfluxDbUrl()
    {
        await using var site = Db(_siteConnection);
        await using var main = Db(_mainConnection);

        var moved = await ReaddressSettings.RunAsync(site, Moves, [], apply: true, CancellationToken.None, main, site: "branch");

        moved.Should().NotContain(m => m.Setting == ReaddressSettings.InfluxSetting);
        await using var check = Db(_mainConnection);
        (await check.MonitoringSettings.SingleAsync()).InfluxDbUrl.Should().Be("http://192.0.2.5:8086");
        (await check.SystemSettings.FindAsync(WafSettingKeys.Url))!.Value.Should().Be("http://192.0.2.7:8044");
        (await check.SystemSettings.FindAsync(WafSettingKeys.ForSite(WafSettingKeys.Url, "branch")))!.Value.Should().Be("http://198.51.100.8:8044");
        (await check.MonitoringTargets.SingleAsync()).Address.Should().Be("192.0.2.5", "the main site's own targets are not this site's");
    }

    [Fact]
    public async Task DryRun_ChangesNothing()
    {
        await using var main = Db(_mainConnection);

        var moved = await ReaddressSettings.RunAsync(main, Moves, [], apply: false, CancellationToken.None, main, site: null);

        moved.Should().HaveCount(3);
        await using var check = Db(_mainConnection);
        (await check.MonitoringSettings.SingleAsync()).InfluxDbUrl.Should().Be("http://192.0.2.5:8086");
        (await check.SystemSettings.FindAsync(WafSettingKeys.Url))!.Value.Should().Be("http://192.0.2.7:8044");
    }
}
