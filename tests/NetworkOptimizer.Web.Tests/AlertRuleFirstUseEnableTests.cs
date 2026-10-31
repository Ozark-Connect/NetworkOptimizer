using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Alerts;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>The WAN degradation rule turns on at first use only while it still has its shipped state.</summary>
public class AlertRuleFirstUseEnableTests
{
    private const string Pattern = "wan.speed_degradation";
    private const string Name = DefaultAlertRules.WanSpeedDegradationRuleName;

    private static NetworkOptimizerDbContext Db(params AlertRule[] rules)
    {
        var db = new NetworkOptimizerDbContext(new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.AlertRules.AddRange(rules);
        db.SaveChanges();
        return db;
    }

    private static AlertRule Rule(bool enabled = false, DateTime? updatedAt = null, string name = Name) =>
        new() { Name = name, EventTypePattern = Pattern, Source = "wan", IsEnabled = enabled, UpdatedAt = updatedAt };

    private static Task<bool> Enable(NetworkOptimizerDbContext db) =>
        AlertRuleAutoEnable.EnableUntouchedDefaultAsync(db, Pattern, Name, NullLogger.Instance);

    [Fact]
    public async Task ShippedRule_IsEnabled()
    {
        await using var db = Db(Rule());

        (await Enable(db)).Should().BeTrue();

        db.AlertRules.AsNoTracking().Single().IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task ARuleTheUserTurnedOff_StaysOff()
    {
        await using var db = Db(Rule(updatedAt: DateTime.UtcNow.AddDays(-1)));

        (await Enable(db)).Should().BeFalse();

        db.AlertRules.AsNoTracking().Single().IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task AUserRuleForTheSameEvent_IsLeftAlone()
    {
        await using var db = Db(Rule(name: "My WAN check"));

        (await Enable(db)).Should().BeFalse();

        db.AlertRules.AsNoTracking().Single().IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task ADeletedRule_StaysDeleted()
    {
        await using var db = Db();

        (await Enable(db)).Should().BeFalse();

        db.AlertRules.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEnabledRule_IsNotTouched()
    {
        await using var db = Db(Rule(enabled: true));

        (await Enable(db)).Should().BeFalse();
    }

    [Fact]
    public void TheShippedRule_HasTheNameItIsMatchedBy()
    {
        DefaultAlertRules.GetDefaults().Should().ContainSingle(r => r.EventTypePattern == Pattern)
            .Which.Should().Match<AlertRule>(r => r.Name == Name && !r.IsEnabled);
    }
}
