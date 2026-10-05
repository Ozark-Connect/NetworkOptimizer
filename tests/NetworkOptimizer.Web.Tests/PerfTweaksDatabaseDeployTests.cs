using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Licensing;
using NetworkOptimizer.Web.Services.Ssh;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// The PostgreSQL SSD deploy and the MongoDB SSD remove against a scripted gateway: a failed step
/// must stop the next one and must not record the tweak as deployed or removed.
/// </summary>
public class PerfTweaksDatabaseDeployTests : IDisposable
{
    private readonly string _dir;
    private readonly SiteDbContextFactory _factory;
    private readonly SiteContextService _siteContext;
    private readonly List<string> _commands = new();

    public PerfTweaksDatabaseDeployTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "no-perftweaks-db-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        var paths = new SiteDatabasePaths(Path.Combine(_dir, "network_optimizer.db"));
        _factory = new SiteDbContextFactory(paths);
        _siteContext = new SiteContextService(new HttpContextAccessor(), paths);
        using var db = _factory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        db.Database.Migrate();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir; a leftover is harmless */ }
        GC.SuppressFinalize(this);
    }

    // A UCG-Fiber on Network 11 in PostgreSQL mode with MongoDB on SSD still deployed.
    private const string PostgresProbe = """
        ---UDM_BOOT_CHECK---
        installed
        ---GATEWAY_MODEL---
        UCGF
        ---FIRMWARE_VERSION---
        6.0.10
        ---SSD_VOLUME---
        /volume/00000000-0000-0000-0000-000000000001
        ---MONGO_BOOT_SCRIPT---
        exists
        ---MONGO_MOUNTPOINT---
        mounted
        ---NETWORK_VERSION---
        11.0.81-37038-1
        ---UNIFI_ACTIVE---
        active
        ---DB_MODE---
        postgresql
        ---PG_APPS_UNIT---
        active
        ---PG_NETWORK_TABLES---
        114
        ---PG_OTHER_DBS---
        0
        ---PG_BOOT_SCRIPT---
        missing
        ---PG_FINDMNT---
        N/A
        ---PG_MARKER---
        missing
        """;

    // A MongoDB-mode gateway with MongoDB on SSD deployed.
    private const string MongoProbe = """
        ---UDM_BOOT_CHECK---
        installed
        ---GATEWAY_MODEL---
        UCGF
        ---SSD_VOLUME---
        /volume1
        ---MONGO_BOOT_SCRIPT---
        exists
        ---MONGO_MOUNTPOINT---
        mounted
        ---NETWORK_VERSION---
        10.0.162
        """;

    private PerfTweaksDeploymentService Build(Func<string, (bool, string)> gateway)
    {
        var ssh = new Mock<IGatewaySshService>();
        ssh.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string c, TimeSpan? _, CancellationToken _) =>
            {
                _commands.Add(c);
                return gateway(c);
            });
        var license = new LicenseStateService(
            Mock.Of<IDbContextFactory<NetworkOptimizerDbContext>>(), TimeProvider.System,
            NullLogger<LicenseStateService>.Instance);
        return new PerfTweaksDeploymentService(
            NullLogger<PerfTweaksDeploymentService>.Instance, ssh.Object, _factory, _siteContext, null!, license);
    }

    private async Task<List<PerfTweakSetting>> SettingsAsync()
    {
        await using var db = _factory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        return await db.PerfTweakSettings.ToListAsync();
    }

    private async Task SeedSettingAsync(string tweakId)
    {
        await using var db = _factory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        db.PerfTweakSettings.Add(new PerfTweakSetting { TweakId = tweakId });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Deploy_DecommissionError_StopsBefore08()
    {
        var service = Build(c =>
            c.Contains("---UDM_BOOT_CHECK---") ? (true, PostgresProbe)
            : c.Contains("mongodb-ssd-decommission.sh --decommission") ? (false, "RESULT=error mode=decommission step=4 reason=\"umount failed (busy?).\"")
            : (true, "deployed"));

        var (success, message, _) = await service.DeployTweakAsync("postgresql-ssd");

        success.Should().BeFalse();
        message.Should().Contain("step 4");
        // The status probe names 08 too, so look for the write and the run specifically.
        _commands.Should().NotContain(c => c.Contains("> /data/on_boot.d/08-") || c.StartsWith("/data/on_boot.d/08-"));
        (await SettingsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Deploy_08NonzeroExit_FailsAndPersistsNoState()
    {
        var service = Build(c =>
            c.Contains("---UDM_BOOT_CHECK---") ? (true, PostgresProbe)
            : c.Contains("mongodb-ssd-decommission.sh --decommission") ? (true, "RESULT=ok mode=decommission copied=ssd bytes=10 unclean=0 cleanup=done noop=0")
            : c.StartsWith("/data/on_boot.d/08-postgresql-ssd-offload.sh") ? (false, "ERROR: PostgreSQL on SSD failed readiness or identity checks.")
            : (true, "deployed"));

        var (success, message, _) = await service.DeployTweakAsync("postgresql-ssd");

        success.Should().BeFalse();
        message.Should().Contain("readiness");
        _commands.Should().NotContain(c => c.StartsWith("/data/on_boot.d/09-postgresql-ssd-backup.sh"));
        (await SettingsAsync()).Should().NotContain(s => s.TweakId == "postgresql-ssd");
    }

    [Fact]
    public async Task RemoveMongo_ResultError_FailsAndKeepsTheSettingRow()
    {
        await SeedSettingAsync("mongodb-ssd");
        var service = Build(c =>
            c.Contains("mongodb-ssd-decommission.sh --remove") ? (false, "RESULT=error mode=remove step=3 reason=\"mongod still running after 30s. Not touching MongoDB data.\"")
            : c.Contains("---UDM_BOOT_CHECK---") ? (true, MongoProbe)
            : (true, "deployed"));

        var (success, message) = await service.RemoveTweakAsync("mongodb-ssd");

        success.Should().BeFalse();
        message.Should().Contain("step 3");
        (await SettingsAsync()).Should().Contain(s => s.TweakId == "mongodb-ssd");
    }
}
