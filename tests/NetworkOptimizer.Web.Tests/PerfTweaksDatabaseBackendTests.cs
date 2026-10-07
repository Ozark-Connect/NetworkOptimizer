using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Database backend detection, the PostgreSQL SSD status, and the decommission RESULT= parser
/// for the Network 11 move from MongoDB to PostgreSQL.
/// </summary>
public class PerfTweaksDatabaseBackendTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 16, 0, 0, DateTimeKind.Utc);
    private const string Ssd = "/volume/00000000-0000-0000-0000-000000000001";

    // Captured from a UCG-Fiber on Network 11.0.81 after the PostgreSQL migration, with the
    // MongoDB SSD tweak still deployed and the PostgreSQL SSD tweak not yet deployed.
    private static Dictionary<string, string> PostgresGatewayWithMongoTweak() => new()
    {
        ["UDM_BOOT_CHECK"] = "installed",
        ["GATEWAY_MODEL"] = "UCGF",
        ["FIRMWARE_VERSION"] = "6.0.10",
        ["SSD_VOLUME"] = Ssd,
        ["MONGO_BOOT_SCRIPT"] = "exists",
        ["MONGO_MOUNTPOINT"] = "mounted",
        ["MONGO_FINDMNT"] = "/dev/md3[/unifi-db]",
        ["MONGO_SERVICE"] = "inactive",
        ["MONGO_BACKUP_SCRIPT"] = "exists",
        ["MONGO_BACKUP_CRON"] = "exists",
        ["MONGO_HELPER_DIR"] = "exists",
        ["MONGO_SSD_COPY"] = "exists",
        ["NETWORK_VERSION"] = "11.0.81-37038-1",
        ["UNIFI_ACTIVE"] = "active",
        ["DB_MODE"] = "postgresql\n",
        ["MONGOD_RUNNING"] = "no",
        ["PG_APPS_UNIT"] = "active",
        ["PG_NETWORK_TABLES"] = "114",
        ["PG_OTHER_DBS"] = "0",
        ["PG_BOOT_SCRIPT"] = "missing",
        ["PG_FINDMNT"] = "N/A",
        ["PG_MARKER"] = "missing",
        ["PG_BACKUP_SCRIPT"] = "missing",
        ["PG_BACKUP_CRON"] = "missing",
        ["PG_BACKUP_AT"] = "N/A",
    };

    // A pre-11 gateway: no unifi-native, MongoDB mode, no PostgreSQL apps cluster.
    private static Dictionary<string, string> MongoGateway() => new()
    {
        ["UDM_BOOT_CHECK"] = "installed",
        ["GATEWAY_MODEL"] = "UCGF",
        ["FIRMWARE_VERSION"] = "6.0.10",
        ["SSD_VOLUME"] = Ssd,
        ["MONGO_BOOT_SCRIPT"] = "missing",
        ["MONGO_MOUNTPOINT"] = "not-mounted",
        ["MONGO_BACKUP_SCRIPT"] = "missing",
        ["MONGO_BACKUP_CRON"] = "missing",
        ["MONGO_HELPER_DIR"] = "missing",
        ["MONGO_SSD_COPY"] = "missing",
        ["NETWORK_VERSION"] = "10.0.162",
        ["UNIFI_ACTIVE"] = "active",
        ["DB_MODE"] = "\n",
        ["MONGOD_RUNNING"] = "yes",
        ["PG_APPS_UNIT"] = "inactive\ninactive",
        ["PG_NETWORK_TABLES"] = "N/A",
        ["PG_OTHER_DBS"] = "N/A",
        ["PG_BOOT_SCRIPT"] = "missing",
        ["PG_FINDMNT"] = "N/A",
        ["PG_MARKER"] = "missing",
    };

    // A gateway where the PostgreSQL SSD tweak is deployed, by NO or by hand per the script docs.
    private static Dictionary<string, string> OffloadedPostgresGateway()
    {
        var s = PostgresGatewayWithMongoTweak();
        s["MONGO_BOOT_SCRIPT"] = "missing";
        s["MONGO_MOUNTPOINT"] = "not-mounted";
        s["MONGO_BACKUP_SCRIPT"] = "missing";
        s["MONGO_BACKUP_CRON"] = "missing";
        s["MONGO_HELPER_DIR"] = "missing";
        s["MONGO_SSD_COPY"] = "missing";
        s["PG_BOOT_SCRIPT"] = "exists";
        s["PG_FINDMNT"] = "/dev/md3[/postgresql-14-apps]";
        s["PG_MARKER"] = $"2026-10-05T14:00:00Z {Ssd}/postgresql-14-apps";
        s["PG_SSD_SIZE"] = "83M";
        s["PG_BACKUP_SCRIPT"] = "exists";
        s["PG_BACKUP_CRON"] = "exists";
        s["PG_BACKUP_AT"] = "2026-10-05T03:00:00Z";
        return s;
    }

    private static PerfTweaksStatus Parse(Dictionary<string, string> sections)
    {
        var output = string.Join("\n", sections.Select(kv => $"---{kv.Key}---\n{kv.Value}"));
        var status = new PerfTweaksStatus();
        PerfTweaksDeploymentService.ParseStatusOutput(output, status, Now);
        return status;
    }

    [Fact]
    public void PostgresGateway_IsPostgreSql_WithMongoArtifactsAndReplacementAvailable()
    {
        var status = Parse(PostgresGatewayWithMongoTweak());

        status.DatabaseBackend.Should().Be(DatabaseBackend.PostgreSql);
        status.NetworkVersion.Should().Be("11.0.81-37038-1");
        status.PgOtherDatabases.Should().Be(0);
        status.MongoArtifactsPresent.Should().BeTrue();
        status.PostgresReplacementAvailable.Should().BeTrue();
        status.Tweaks["postgresql-ssd"].IsActive.Should().BeFalse();
        status.Tweaks["postgresql-ssd"].HealthChecks.Should().BeEmpty();
    }

    [Fact]
    public void MongoGateway_IsMongoDb_AndOffersNoReplacement()
    {
        var status = Parse(MongoGateway());

        status.DatabaseBackend.Should().Be(DatabaseBackend.MongoDb);
        status.MongoArtifactsPresent.Should().BeFalse();
        status.PgOtherDatabases.Should().BeNull();
        status.PostgresReplacementAvailable.Should().BeFalse();
    }

    [Theory]
    // PostgreSQL needs every signal.
    [InlineData("11.0.81-37038-1", "active", "postgresql", "active", "114", DatabaseBackend.PostgreSql)]
    // Mid-upgrade: Network 11 installed but the app is not running.
    [InlineData("11.0.81-37038-1", "inactive", "", "active", "114", DatabaseBackend.Indeterminate)]
    // PostgreSQL mode but psql gave no answer, or the database is empty.
    [InlineData("11.0.81-37038-1", "active", "postgresql", "active", "N/A", DatabaseBackend.Indeterminate)]
    [InlineData("11.0.81-37038-1", "active", "postgresql", "active", "0", DatabaseBackend.Indeterminate)]
    [InlineData("11.0.81-37038-1", "active", "postgresql", "inactive", "114", DatabaseBackend.Indeterminate)]
    // Pre-11 Network is MongoDB whatever else reads.
    [InlineData("10.0.162", "active", "", "inactive", "N/A", DatabaseBackend.MongoDb)]
    [InlineData("9.5.21", "inactive", "", "inactive", "N/A", DatabaseBackend.MongoDb)]
    // Any other mode value is MongoDB.
    [InlineData("11.0.81-37038-1", "active", "mongodb", "inactive", "N/A", DatabaseBackend.MongoDb)]
    // Nothing readable.
    [InlineData("N/A", "inactive", "", "inactive", "N/A", DatabaseBackend.Indeterminate)]
    public void ClassifyBackend_FollowsTheRuleTable(
        string version, string unifiActive, string dbMode, string pgUnit, string tables, DatabaseBackend expected)
    {
        PerfTweaksDeploymentService.ClassifyBackend(version, unifiActive, dbMode, pgUnit, tables)
            .Should().Be(expected);
    }

    [Fact]
    public void OnDemandMongod_DoesNotChangeThePostgreSqlBackend()
    {
        var sections = OffloadedPostgresGateway();
        sections["MONGOD_RUNNING"] = "yes";

        var status = Parse(sections);

        status.DatabaseBackend.Should().Be(DatabaseBackend.PostgreSql);
        status.MongodRunning.Should().BeTrue();
        status.Tweaks["postgresql-ssd"].HealthChecks.Should().Contain(h => h.Label == "On-demand mongod");
    }

    [Theory]
    [InlineData("MONGO_BOOT_SCRIPT", "exists")]
    [InlineData("MONGO_BACKUP_SCRIPT", "exists")]
    [InlineData("MONGO_BACKUP_CRON", "exists")]
    [InlineData("MONGO_MOUNTPOINT", "mounted")]
    [InlineData("MONGO_HELPER_DIR", "exists")]
    [InlineData("MONGO_SSD_COPY", "exists")]
    [InlineData("MONGO_EMMC_BACKUP", "exists")]
    public void MongoArtifactsPresent_IsTrueForEachArtifactOnItsOwn(string section, string value)
    {
        var sections = OffloadedPostgresGateway();
        Parse(sections).MongoArtifactsPresent.Should().BeFalse();

        sections[section] = value;

        Parse(sections).MongoArtifactsPresent.Should().BeTrue();
    }

    [Fact]
    public void HandInstalledPostgresTweak_ShowsAsDeployed()
    {
        // No PerfTweakSetting row is involved: deployed state comes from the hook, bind and marker.
        var status = Parse(OffloadedPostgresGateway());
        var pg = status.Tweaks["postgresql-ssd"];

        pg.BootScriptDeployed.Should().BeTrue();
        pg.IsActive.Should().BeTrue();
        pg.IssueDescription.Should().BeNull();
        pg.HealthChecks.Should().OnlyContain(h => h.Status == HealthCheckStatus.Ok);
        status.PostgresReplacementAvailable.Should().BeFalse();
    }

    [Fact]
    public void BindWithoutMarker_IsAnIssue()
    {
        var sections = OffloadedPostgresGateway();
        sections["PG_MARKER"] = "missing";

        var pg = Parse(sections).Tweaks["postgresql-ssd"];

        pg.IsActive.Should().BeFalse();
        pg.IssueDescription.Should().NotBeNull();
    }

    [Fact]
    public void MarkerWithoutBind_IsAnIssue()
    {
        var sections = OffloadedPostgresGateway();
        sections["PG_FINDMNT"] = "N/A";

        var pg = Parse(sections).Tweaks["postgresql-ssd"];

        pg.IsActive.Should().BeFalse();
        pg.IssueDescription.Should().NotBeNull();
    }

    [Fact]
    public void StaleBackup_IsAWarning()
    {
        var sections = OffloadedPostgresGateway();
        sections["PG_BACKUP_AT"] = "2026-10-02T03:00:00Z";

        var backup = Parse(sections).Tweaks["postgresql-ssd"].HealthChecks.Single(h => h.Label == "Backup");

        backup.Status.Should().Be(HealthCheckStatus.Warning);
    }

    [Fact]
    public void OutdatedMongoScripts_AreNotFlaggedOnAPostgresGateway()
    {
        var sections = PostgresGatewayWithMongoTweak();
        sections["SCRIPT_HASHES"] = "06-mongodb-ssd-offload.sh:00000000000000000000000000000000";

        Parse(sections).Tweaks["mongodb-ssd"].ScriptOutdated.Should().BeFalse();

        var mongo = MongoGateway();
        mongo["MONGO_BOOT_SCRIPT"] = "exists";
        mongo["MONGO_MOUNTPOINT"] = "mounted";
        mongo["SCRIPT_HASHES"] = "06-mongodb-ssd-offload.sh:00000000000000000000000000000000";

        Parse(mongo).Tweaks["mongodb-ssd"].ScriptOutdated.Should().BeTrue();
    }

    [Fact]
    public void ParseDecommissionResult_Ok()
    {
        var r = PerfTweaksDeploymentService.ParseDecommissionResult(
            "log line\nRESULT=ok mode=decommission copied=ssd bytes=576716800 unclean=0 cleanup=done noop=0\n", true);

        r.Ok.Should().BeTrue();
        r.Copied.Should().Be("ssd");
        r.Bytes.Should().Be(576716800);
        r.Noop.Should().BeFalse();
        PerfTweaksDeploymentService.DecommissionWarnings(r).Should().BeEmpty();
    }

    [Fact]
    public void ParseDecommissionResult_Error()
    {
        var r = PerfTweaksDeploymentService.ParseDecommissionResult(
            "RESULT=error mode=decommission step=3 reason=\"mongod still running after 30s. Not touching MongoDB data.\"", false);

        r.Ok.Should().BeFalse();
        r.Step.Should().Be(3);
        r.Reason.Should().Be("mongod still running after 30s. Not touching MongoDB data.");
    }

    [Fact]
    public void ParseDecommissionResult_NoopUncleanAndIncompleteCleanup()
    {
        PerfTweaksDeploymentService.ParseDecommissionResult(
            "RESULT=ok mode=remove copied=none bytes=0 unclean=0 cleanup=skipped noop=1", true).Noop.Should().BeTrue();

        var r = PerfTweaksDeploymentService.ParseDecommissionResult(
            "RESULT=ok mode=decommission copied=ssd bytes=10 unclean=1 cleanup=incomplete noop=0", true);

        r.Ok.Should().BeTrue();
        r.Unclean.Should().BeTrue();
        PerfTweaksDeploymentService.DecommissionWarnings(r).Should().HaveCount(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Copying /volume1/unifi-db to the eMMC (550 MiB)...\n")]
    public void ParseDecommissionResult_MissingResultLine_IsAnError(string output)
    {
        PerfTweaksDeploymentService.ParseDecommissionResult(output, true).Ok.Should().BeFalse();
    }

    [Fact]
    public void ParseDecommissionResult_OkLineWithNonzeroExit_IsAnError()
    {
        PerfTweaksDeploymentService.ParseDecommissionResult(
            "RESULT=ok mode=remove copied=none bytes=0 unclean=0 cleanup=done noop=0", false).Ok.Should().BeFalse();
    }

    [Fact]
    public void ReplacementFlag_SetsAndClears()
    {
        var state = new SiteModuleUpdateState();
        var changes = 0;
        state.OnStateChanged += () => changes++;

        state.NotifyPerfTweaksStatus(Parse(PostgresGatewayWithMongoTweak()));
        state.PerfTweaksReplacementAvailable.Should().BeTrue();

        state.NotifyPerfTweaksStatus(Parse(OffloadedPostgresGateway()));
        state.PerfTweaksReplacementAvailable.Should().BeFalse();
        changes.Should().Be(2);

        state.NotifyPerfTweaksStatus(Parse(MongoGateway()));
        state.PerfTweaksReplacementAvailable.Should().BeFalse();
    }
}
