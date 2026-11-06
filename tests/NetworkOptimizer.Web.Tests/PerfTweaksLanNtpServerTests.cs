using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
/// The LAN NTP server tweak's status, read from the boot script, the systemd unit and chronyc.
/// </summary>
public class PerfTweaksLanNtpServerTests
{
    private const long GatewayNow = 1_793_552_100;
    private const string Id = "lan-ntp-server";
    private const string SyncedTracking =
        "C0A80A62,192.168.10.98,2,1793552015.911648089,-0.001030638,-0.000008039,0.000008039,2.490,0.000,223.479,0.000682662,0.001340078,1.8,Normal";
    private const string UnsyncedTracking =
        "00000000,,0,0.000000000,0.000000000,0.000000000,0.000000000,0.000,0.000,2000.000,1.000000000,1.000000000,0.0,Not synchronised";

    private static Dictionary<string, string> Deployed(string tracking = SyncedTracking, string stats = "12,0,8,0,0,0,0,0,0,0,0,0,12,12,0,0,0",
        string subState = "running") => new()
    {
        ["GATEWAY_MODEL"] = "UCGF",
        ["FIRMWARE_VERSION"] = "6.0.16",
        ["NTP_SRV_BOOT_SCRIPT"] = "exists",
        ["NTP_SRV_SERVICE"] = subState,
        ["NTP_SRV_TRACKING"] = tracking,
        ["NTP_SRV_STATS"] = stats,
        ["NTP_SRV_SOCKETS"] = "ok",
        ["NTP_SRV_TIMER"] = "active",
        ["NTP_SRV_LOG"] = "",
        ["NTP_SRV_NOW"] = GatewayNow.ToString(),
    };

    private static TweakDeploymentStatus Parse(Dictionary<string, string> sections)
    {
        var output = string.Join("\n", sections.Select(kv => $"---{kv.Key}---\n{kv.Value}"));
        var status = new PerfTweaksStatus();
        PerfTweaksDeploymentService.ParseStatusOutput(output, status, DateTime.UtcNow);
        return status.Tweaks[Id];
    }

    [Fact]
    public void NotDeployed_HasNoHealthChecks()
    {
        var tweak = Parse(new()
        {
            ["NTP_SRV_BOOT_SCRIPT"] = "missing",
            ["NTP_SRV_SERVICE"] = "dead",
            ["NTP_SRV_TRACKING"] = "failed",
            ["NTP_SRV_STATS"] = "failed",
            ["NTP_SRV_NOW"] = GatewayNow.ToString(),
        });

        tweak.BootScriptDeployed.Should().BeFalse();
        tweak.IsActive.Should().BeFalse();
        tweak.HealthChecks.Should().BeEmpty();
    }

    [Fact]
    public void Synchronized_IsActive()
    {
        var tweak = Parse(Deployed());

        tweak.IsActive.Should().BeTrue();
        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("Clock", "Serving stratum 2 from 192.168.10.98", HealthCheckStatus.Ok));
        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("Requests", "12 received", HealthCheckStatus.Ok));
        tweak.HealthChecks.Should().NotContain(h => h.Label == "Rate Limited");
        tweak.HealthChecks.Should().NotContain(h => h.Status != HealthCheckStatus.Ok);
    }

    [Fact]
    public void DroppedRequests_AreShown()
    {
        var tweak = Parse(Deployed(stats: "40,7,8,0,0,0,0,0,0,0,0,0,33,33,0,0,0"));

        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("Rate Limited", "7 dropped", HealthCheckStatus.Ok));
    }

    [Fact]
    public void Unsynchronized_IsAWarningButStillActive()
    {
        var tweak = Parse(Deployed(UnsyncedTracking));

        tweak.IsActive.Should().BeTrue();
        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("Clock", "Not synchronized yet", HealthCheckStatus.Warning));
    }

    [Fact]
    public void NoSourceUpdateForAnHour_IsAWarning()
    {
        var tracking = SyncedTracking.Replace("1793552015.911648089", (GatewayNow - 4000).ToString() + ".0");

        var tweak = Parse(Deployed(tracking));

        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("Clock", "Stratum 2, no source update for over an hour", HealthCheckStatus.Warning));
    }

    [Fact]
    public void Port123TakenForOneFamily_IsAnError()
    {
        var sections = Deployed();
        sections["NTP_SRV_SOCKETS"] = "chronyd is not listening on UDP 123 for IPv4: UNCONN 0 0 0.0.0.0:123 0.0.0.0:* users:((\"ntpd\",pid=1,fd=3))";

        var tweak = Parse(sections);

        tweak.IsActive.Should().BeFalse();
        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("Port 123", "Not listening", HealthCheckStatus.Error));
        tweak.IssueDescription.Should().StartWith("chronyd is not listening on UDP 123 for IPv4");
    }

    [Fact]
    public void StoppedRefreshTimer_IsAWarning()
    {
        var sections = Deployed();
        sections["NTP_SRV_TIMER"] = "inactive";

        var tweak = Parse(sections);

        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("Refresh Timer", "Not running", HealthCheckStatus.Warning));
    }

    [Fact]
    public void WaitingForTheFirstTimeSync_IsAWarning()
    {
        var tweak = Parse(Deployed(subState: "start-pre"));

        tweak.IsActive.Should().BeFalse();
        tweak.HealthChecks.Should().ContainSingle().Which.Should().Be(
            new HealthCheckResult("Service", "Waiting for the gateway's first time sync", HealthCheckStatus.Warning));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("506 Cannot talk to daemon")]
    [InlineData("C0A80A62,192.168.10.98,two,1793552015.9,0,0,0,0,0,0,0,0,0,Normal")]
    public void ChronydNotAnswering_IsAnError(string tracking)
    {
        var tweak = Parse(Deployed(tracking));

        tweak.IsActive.Should().BeFalse();
        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("Status", "chronyd not answering", HealthCheckStatus.Error));
        tweak.HealthChecks.Should().NotContain(h => h.Label == "Requests");
    }

    [Theory]
    [InlineData("auto-restart")]
    [InlineData("failed")]
    [InlineData("dead")]
    public void ServiceNotRunning_WithBootScript_IsAnErrorWithTheJournal(string subState)
    {
        var sections = Deployed(tracking: "failed", stats: "failed", subState: subState);
        sections["NTP_SRV_LOG"] = "Fatal error : Could not open NTP sockets";

        var tweak = Parse(sections);

        tweak.IsActive.Should().BeFalse();
        tweak.IssueDescription.Should().Be("Fatal error : Could not open NTP sockets");
        tweak.HealthChecks.Should().ContainSingle().Which.Should().Be(new HealthCheckResult("Service", "Not running", HealthCheckStatus.Error));
    }

    [Fact]
    public void ChangedBootScript_IsOutdated()
    {
        var sections = Deployed();
        sections["SCRIPT_HASHES"] = "35-lan-ntp-server.sh:00000000000000000000000000000000";

        Parse(sections).ScriptOutdated.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAllStatusAsync_HashesTheBootScript()
    {
        string? command = null;
        var ssh = new Mock<IGatewaySshService>();
        ssh.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback<string, TimeSpan?, CancellationToken>((c, _, _) => command = c)
            .ReturnsAsync((false, "unreachable"));

        await new PerfTweaksDeploymentService(Mock.Of<ILogger<PerfTweaksDeploymentService>>(), ssh.Object, null!, null!, null!, null!,
            new NetworkOptimizer.Web.Services.Firmware.RolloutSuppressionRegistry()).CheckAllStatusAsync();

        var hashLoop = command!.Split("---SCRIPT_HASHES---")[1].Split("done;")[0];
        hashLoop.Should().Contain(" 35-lan-ntp-server.sh;");
        command.Should().Contain("chronyc -n -c -h /run/lan-ntp-server/chronyd.sock tracking");
    }
}

/// <summary>Deploy and remove of the LAN NTP server tweak against a scripted gateway.</summary>
public class PerfTweaksLanNtpServerDeployTests : IDisposable
{
    private readonly string _dir;
    private readonly SiteDbContextFactory _factory;
    private readonly SiteContextService _siteContext;
    private readonly List<string> _commands = new();

    public PerfTweaksLanNtpServerDeployTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "no-perftweaks-ntp-" + Guid.NewGuid().ToString("N")[..8]);
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
            NullLogger<PerfTweaksDeploymentService>.Instance, ssh.Object, _factory, _siteContext, null!, license,
            new NetworkOptimizer.Web.Services.Firmware.RolloutSuppressionRegistry());
    }

    private const string RunBootScript = "/data/on_boot.d/35-lan-ntp-server.sh 2>&1";

    [Fact]
    public async Task Deploy_ServiceNotStarting_FailsAndRemovesTheTweak()
    {
        var service = Build(c => c switch
        {
            _ when c.Contains("base64 -d >") => (true, "deployed"),
            RunBootScript => (false, "[lan-ntp-server] UDP 123 is already in use"),
            _ when c.Contains("disable --now lan-ntp-server-refresh.timer lan-ntp-server") => (true, "removed"),
            _ => (true, ""),
        });

        var (success, message, _) = await service.DeployTweakAsync("lan-ntp-server");

        success.Should().BeFalse();
        message.Should().Contain("Service did not start").And.Contain("UDP 123 is already in use");
        message.Should().Contain("tweak removed again");
        _commands.Should().Contain(c => c.Contains("disable --now lan-ntp-server-refresh.timer lan-ntp-server"));
    }

    [Fact]
    public async Task Deploy_ServiceNotStarting_ReportsAFailedCleanup()
    {
        var service = Build(c => c switch
        {
            _ when c.Contains("base64 -d >") => (true, "deployed"),
            RunBootScript => (false, "Service failed to start"),
            _ => (true, ""),
        });

        var (success, message, _) = await service.DeployTweakAsync("lan-ntp-server");

        success.Should().BeFalse();
        message.Should().Contain("removing it failed");
    }

    [Fact]
    public async Task Remove_StopsTheServiceBeforeDeletingIt()
    {
        var service = Build(_ => (true, "removed"));

        var (success, _) = await service.RemoveTweakAsync("lan-ntp-server");

        success.Should().BeTrue();
        var command = _commands.Single();
        command.IndexOf("disable --now lan-ntp-server-refresh.timer lan-ntp-server", StringComparison.Ordinal)
            .Should().BeLessThan(command.IndexOf("rm -rf /data/lan-ntp-server", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remove_LeftoversAreAFailure()
    {
        var service = Build(_ => (true, ""));

        var (success, _) = await service.RemoveTweakAsync("lan-ntp-server");

        success.Should().BeFalse();
    }
}
