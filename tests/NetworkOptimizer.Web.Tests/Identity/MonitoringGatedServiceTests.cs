using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Gates;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// The monitoring edits that used to be direct DbContext writes inside the Latency targets card and
/// the Monitoring page. What matters here is what reaches the audit envelope: an edit that changed
/// something must describe it, and an edit that changed nothing must stay out of the log entirely.
/// </summary>
public class MonitoringGatedServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly SiteDbContextFactory _factory;
    private readonly SiteContextService _siteContext;
    private readonly AuditContext _audit = new();

    public MonitoringGatedServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "no-gated-monitoring-" + Guid.NewGuid().ToString("N")[..8]);
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

    private MonitoringTargetService Targets() => new(
        _factory, _siteContext, asnResolution: null!, executorFactory: null!, _audit,
        NullLogger<MonitoringTargetService>.Instance);

    private MonitoringSettingsService Settings() => new(_factory, _siteContext, _audit);

    private async Task<int> SeedTargetAsync(bool enabled = true, int interval = 10)
    {
        await using var db = _factory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        var target = new MonitoringTarget
        {
            TargetId = "custom-seed",
            Name = "Seed target",
            Address = "192.0.2.10",
            TargetType = MonitoringTargetType.Custom,
            ProbeMode = ProbeMode.Icmp,
            PollIntervalSeconds = interval,
            PingCount = 5,
            Enabled = enabled,
            VantagePoint = "server",
            CreatedAt = DateTime.UtcNow
        };
        db.MonitoringTargets.Add(target);
        await db.SaveChangesAsync();
        return target.Id;
    }

    [Fact]
    public async Task Pausing_a_target_records_what_changed()
    {
        var id = await SeedTargetAsync(enabled: true);

        (await Targets().SetEnabledAsync(id, false)).Should().BeTrue();

        var (details, targetId, targetName, suppressed) = _audit.Drain();
        details.Should().NotBeNull();
        suppressed.Should().BeFalse();
        targetId.Should().Be("custom-seed");
        targetName.Should().Be("Seed target");

        await using var db = _factory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        (await db.MonitoringTargets.FindAsync(id))!.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Pausing_an_already_paused_target_writes_no_event()
    {
        var id = await SeedTargetAsync(enabled: false);

        (await Targets().SetEnabledAsync(id, false)).Should().BeTrue();

        _audit.Drain().Suppressed.Should().BeTrue("an edit that changed nothing writes no audit event at all");
    }

    [Fact]
    public async Task Editing_a_target_that_is_gone_reports_it_rather_than_throwing()
    {
        (await Targets().SetEnabledAsync(4242, false)).Should().BeFalse();
        _audit.Drain().Suppressed.Should().BeTrue("nothing was there to change");

        (await Targets().DeleteAsync(4242)).Should().BeFalse();
        _audit.Drain().Suppressed.Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_a_target_names_it_before_it_disappears()
    {
        var id = await SeedTargetAsync();

        (await Targets().DeleteAsync(id)).Should().BeTrue();

        var (details, targetId, targetName, suppressed) = _audit.Drain();
        details.Should().NotBeNull();
        targetId.Should().Be("custom-seed");
        targetName.Should().Be("Seed target", "the row is gone by the time the envelope is written");

        await using var db = _factory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        (await db.MonitoringTargets.FindAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task Changing_the_poll_interval_records_both_ends()
    {
        var id = await SeedTargetAsync(interval: 10);

        await Targets().SetPollIntervalAsync(id, 30);

        _audit.Drain().Details.Should().NotBeNull();
        await using var db = _factory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        (await db.MonitoringTargets.FindAsync(id))!.PollIntervalSeconds.Should().Be(30);
    }

    [Theory]
    [InlineData("", "192.0.2.1")]
    [InlineData("Name", "")]
    [InlineData("Name", "not a hostname!")]
    public async Task A_target_that_fails_validation_is_rejected_before_anything_is_written(string name, string address)
    {
        var act = () => Targets().AddAsync(new NewMonitoringTarget { Name = name, Address = address });

        await act.Should().ThrowAsync<MonitoringTargetValidationException>();

        await using var db = _factory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        (await db.MonitoringTargets.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_over_long_target_name_is_rejected()
    {
        var act = () => Targets().AddAsync(new NewMonitoringTarget
        {
            Name = new string('n', 201),
            Address = "192.0.2.1"
        });

        await act.Should().ThrowAsync<MonitoringTargetValidationException>();
    }

    [Fact]
    public async Task Saving_temperature_thresholds_records_the_change_and_normalizes_non_positive_values()
    {
        var settings = await Settings().SaveTempThresholdsAsync(switchHighC: 65, gatewayHighC: 0);

        settings.SwitchTempHighC.Should().Be(65);
        settings.GatewayTempHighC.Should().BeNull("a non-positive threshold means use the default");
        _audit.Drain().Details.Should().NotBeNull();
    }

    [Fact]
    public async Task Re_saving_the_same_thresholds_writes_no_event()
    {
        await Settings().SaveTempThresholdsAsync(switchHighC: 65, gatewayHighC: 70);
        _audit.Drain();

        await Settings().SaveTempThresholdsAsync(switchHighC: 65, gatewayHighC: 70);

        _audit.Drain().Suppressed.Should().BeTrue("saving a form whose values are already stored changed nothing");
    }

    [Fact]
    public async Task Resetting_the_influx_setup_clears_it_without_putting_the_token_in_the_log()
    {
        await Settings().SetEnabledAsync(true);
        await using (var db = _factory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault))
        {
            var row = await db.MonitoringSettings.FirstAsync();
            row.InfluxDbToken = "a-secret-token";
            row.InfluxDbUrl = "http://192.0.2.50:8086";
            await db.SaveChangesAsync();
        }
        _audit.Drain();

        await Settings().ResetInfluxSetupAsync();

        await using var check = _factory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        var settings = await check.MonitoringSettings.FirstAsync();
        settings.InfluxDbToken.Should().BeEmpty();
        settings.Enabled.Should().BeFalse();

        var details = _audit.Drain().Details;
        details.Should().NotBeNull();
        System.Text.Json.JsonSerializer.Serialize(details)
            .Should().NotContain("a-secret-token", "the audit detail must never carry the cleared credentials");
    }
}
