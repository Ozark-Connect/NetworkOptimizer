using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.Threats.Blocking;
using NetworkOptimizer.Threats.Waf;
using NetworkOptimizer.Threats.WanFlood;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>A removed site's threat settings in the main database, which a reused slug must not inherit.</summary>
public class ThreatSiteSettingsTests : IDisposable
{
    private readonly SqliteConnection _main = new("DataSource=:memory:");
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _factory;

    public ThreatSiteSettingsTests()
    {
        _main.Open();
        _factory = new NetworkOptimizerDbContextFactory(
            new DbContextOptionsBuilder<NetworkOptimizerDbContext>().UseSqlite(_main).Options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _main.Dispose();

    private void Seed(params (string Key, string Value)[] settings)
    {
        using var db = _factory.CreateDbContext();
        foreach (var (key, value) in settings)
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        db.SaveChanges();
    }

    private Dictionary<string, string?> All()
    {
        using var db = _factory.CreateDbContext();
        return db.SystemSettings.AsNoTracking().ToDictionary(s => s.Key, s => s.Value);
    }

    private async Task<int> RemoveAsync(string slug)
    {
        await using var db = _factory.CreateDbContext();
        return await ThreatSiteSettings.RemoveForSiteAsync(db, slug);
    }

    [Fact]
    public async Task Removes_every_family_of_the_sites_own_settings()
    {
        Seed(
            (BlockingSettingKeys.ForSite(BlockingSettingKeys.CloudflareToken, "lab"), "ENC:token"),
            (BlockingSettingKeys.ForSite(BlockingSettingKeys.FeedToken("threatfox-ip"), "lab"), "ENC:key"),
            (BlockingSettingKeys.ForSite(TrustedSourcesKeys.Addresses, "lab"), "[]"),
            (BlockingSettingKeys.ForSite(TrustedSourcesKeys.Sites, "lab"), "[\"lab\"]"),
            (WafSettingKeys.ForSite(WafSettingKeys.Url, "lab"), "http://192.0.2.10:8044"),
            (WafSettingKeys.ForSite(WafSettingKeys.Token, "lab"), "ENC:waf"),
            (WanFloodSettingKeys.ForSite(WanFloodSettingKeys.Mode, "lab"), "block"));

        (await RemoveAsync("lab")).Should().Be(7);

        All().Should().BeEmpty();
    }

    [Fact]
    public async Task Keeps_other_sites_the_default_sites_and_other_features_keys()
    {
        Seed(
            (WafSettingKeys.ForSite(WafSettingKeys.Token, "lab-annex"), "ENC:annex"),
            (WafSettingKeys.ForSite(WafSettingKeys.Token, "east-lab"), "ENC:east"),
            (WafSettingKeys.Token, "ENC:main"),
            ("crowdsec.api_key@tenant:lab", "ENC:other-feature"),
            ("other.setting:lab", "kept"));

        (await RemoveAsync("lab")).Should().Be(0);

        All().Should().HaveCount(5);
    }

    [Fact]
    public async Task Other_sites_stop_trusting_the_removed_site()
    {
        Seed(
            (TrustedSourcesKeys.Sites, "[null,\"lab\",\"branch\"]"),
            (BlockingSettingKeys.ForSite(TrustedSourcesKeys.Sites, "branch"), "[\"branch\",\"lab\"]"),
            (BlockingSettingKeys.ForSite(TrustedSourcesKeys.Sites, "annex"), "[\"annex\",null]"));

        (await RemoveAsync("lab")).Should().Be(2);

        var all = All();
        all[TrustedSourcesKeys.Sites].Should().Be("[null,\"branch\"]");
        all[BlockingSettingKeys.ForSite(TrustedSourcesKeys.Sites, "branch")].Should().Be("[\"branch\"]");
        all[BlockingSettingKeys.ForSite(TrustedSourcesKeys.Sites, "annex")].Should().Be("[\"annex\",null]");
    }

    [Fact]
    public async Task An_unreadable_trusted_list_is_left_as_it_is()
    {
        Seed((TrustedSourcesKeys.Sites, "not json"));

        (await RemoveAsync("lab")).Should().Be(0);

        All()[TrustedSourcesKeys.Sites].Should().Be("not json");
    }
}
