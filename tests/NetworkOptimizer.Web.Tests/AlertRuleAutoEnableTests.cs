using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// The opt-in ONT rules (management unresponsive, rebooted) must stay out of the source-wide
/// enables: configuring any ONT may not turn them on.
/// </summary>
public class AlertRuleAutoEnableTests
{
    private const string Mgmt = "ont.management_unresponsive";
    private const string Reboot = "ont.rebooted";

    private static (ServiceProvider Sp, NetworkOptimizerDbContext Db) Create()
    {
        var name = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<NetworkOptimizerDbContext>(o => o.UseInMemoryDatabase(name));
        var sp = services.BuildServiceProvider();
        var db = sp.CreateScope().ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
        foreach (var pattern in new[] { "ont.rx_power_low", "ont.pon_link_down", Mgmt, Reboot })
            db.AlertRules.Add(new AlertRule { Name = pattern, EventTypePattern = pattern, Source = "ont", IsEnabled = false });
        db.SaveChanges();
        return (sp, db);
    }

    private static bool Enabled(NetworkOptimizerDbContext db, string pattern) =>
        db.AlertRules.AsNoTracking().Single(r => r.EventTypePattern == pattern).IsEnabled;

    [Fact]
    public async Task EnableBySource_SkipsOptInRules()
    {
        var (sp, db) = Create();
        using var scope = sp.CreateScope();

        await AlertRuleAutoEnable.EnableBySourceAsync(scope, "ont", NullLogger.Instance);

        Enabled(db, "ont.pon_link_down").Should().BeTrue();
        Enabled(db, Mgmt).Should().BeFalse();
        Enabled(db, Reboot).Should().BeFalse();
    }

    [Fact]
    public void EnableFreshlySeeded_SkipsOptInRules()
    {
        var (_, db) = Create();
        var seeded = new HashSet<string> { "ont.rx_power_low", Mgmt, Reboot };

        AlertRuleAutoEnable.EnableFreshlySeeded(db, "ont", seeded, () => true);

        Enabled(db, "ont.rx_power_low").Should().BeTrue();
        Enabled(db, Mgmt).Should().BeFalse();
        Enabled(db, Reboot).Should().BeFalse();
    }

    [Fact]
    public void EnableFreshlySeededWhen_EnablesOnlyWithPrerequisiteAndOnlyOnSeed()
    {
        var (_, db) = Create();

        AlertRuleAutoEnable.EnableFreshlySeededWhen(db, Mgmt, new HashSet<string> { Mgmt }, () => false);
        Enabled(db, Mgmt).Should().BeFalse("no ONT is attached to an SFP module");

        AlertRuleAutoEnable.EnableFreshlySeededWhen(db, Mgmt, new HashSet<string>(), () => true);
        Enabled(db, Mgmt).Should().BeFalse("a pass that did not seed the rule never touches it");

        AlertRuleAutoEnable.EnableFreshlySeededWhen(db, Mgmt, new HashSet<string> { Mgmt }, () => true);
        Enabled(db, Mgmt).Should().BeTrue();
        Enabled(db, Reboot).Should().BeFalse();
    }
}
