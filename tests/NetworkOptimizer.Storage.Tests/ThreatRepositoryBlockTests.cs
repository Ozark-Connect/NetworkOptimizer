using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.Threats.Models;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class ThreatRepositoryBlockTests : IDisposable
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ThreatRepository _repository;
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    public ThreatRepositoryBlockTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new NetworkOptimizerDbContext(options);
        _repository = new ThreatRepository(_context, new Mock<ILogger<ThreatRepository>>().Object);
    }

    public void Dispose() => _context.Dispose();

    private static ThreatBlock Block(string ip, DateTime created, int minutes, bool enforced = true) => new()
    {
        SourceIp = ip,
        Reason = "test",
        Enforced = enforced,
        CreatedAt = created,
        ExpiresAt = created.AddMinutes(minutes),
    };

    [Fact]
    public async Task WafCounts_CountOnlyWafEventsInTheWindow_IgnoringNoiseFilters()
    {
        ThreatEvent E(string ip, DateTime at, EventSource source) => new()
        {
            Timestamp = at, SourceIp = ip, DestIp = "", Protocol = "HTTP", SignatureName = "", Category = "",
            InnerAlertId = Guid.NewGuid().ToString(), EventSource = source,
        };
        _context.ThreatEvents.AddRange(
            E("203.0.113.7", Now.AddMinutes(-1), EventSource.Waf),
            E("203.0.113.7", Now.AddMinutes(-2), EventSource.Waf),
            E("203.0.113.7", Now.AddMinutes(-30), EventSource.Waf),        // outside the window
            E("203.0.113.8", Now.AddMinutes(-1), EventSource.Ips));       // not a WAF event
        await _context.SaveChangesAsync();
        _repository.SetNoiseFilters([new ThreatNoiseFilter { SourceIp = "203.0.113.7", Enabled = true }]);

        var counts = await _repository.GetWafEventCountsBySourceAsync(Now.AddMinutes(-10), Now);

        counts.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, int>("203.0.113.7", 2));
    }

    [Fact]
    public async Task WafEvents_NewestFirst_SearchedAcrossFields_NoiseFiltered()
    {
        ThreatEvent W(string ip, int minutesAgo, string host, string service, long rule) => new()
        {
            Timestamp = Now.AddMinutes(-minutesAgo), SourceIp = ip, DestIp = "", Protocol = "HTTP",
            SignatureId = rule, SignatureName = $"Rule {rule}", Category = "", Domain = host, Service = service,
            InnerAlertId = Guid.NewGuid().ToString(), EventSource = EventSource.Waf,
        };
        _context.ThreatEvents.AddRange(
            W("203.0.113.1", 5, "app.example.com", "GET /search", 942100),
            W("2001:db8::1", 1, "wiki.example.com", "POST /login", 930100),
            W("203.0.113.9", 2, "app.example.com", "GET /", 942100));                 // hidden by a noise filter
        await _context.SaveChangesAsync();
        _repository.SetNoiseFilters([new ThreatNoiseFilter { SourceIp = "203.0.113.9", Enabled = true }]);

        var all = await _repository.GetWafEventsAsync(Now.AddHours(-1), Now, null, 50);
        all.Select(e => e.SourceIp).Should().Equal("2001:db8::1", "203.0.113.1");

        (await _repository.GetWafEventsAsync(Now.AddHours(-1), Now, "wiki", 50)).Should().ContainSingle();
        (await _repository.GetWafEventsAsync(Now.AddHours(-1), Now, "/search", 50)).Should().ContainSingle();
        (await _repository.GetWafEventsAsync(Now.AddHours(-1), Now, "930100", 50)).Should().ContainSingle()
            .Which.SourceIp.Should().Be("2001:db8::1");
    }

    [Fact]
    public async Task GetBlocks_ReturnsActivePlusRecentlyCreated()
    {
        await _repository.AddBlocksAsync(
        [
            Block("203.0.113.1", Now.AddMinutes(-10), 60),                 // active
            Block("203.0.113.2", Now.AddHours(-3), 60),                    // ended, created before the cutoff
            Block("203.0.113.3", Now.AddMinutes(-30), 10),                 // ended, created after the cutoff
        ]);

        var blocks = await _repository.GetBlocksAsync(Now, Now.AddHours(-1));

        blocks.Select(b => b.SourceIp).Should().BeEquivalentTo(["203.0.113.1", "203.0.113.3"]);
    }

    [Fact]
    public async Task Release_EndsAnActiveBlockOnce()
    {
        await _repository.AddBlocksAsync([Block("203.0.113.1", Now.AddMinutes(-10), 60)]);
        var id = (await _repository.GetBlocksAsync(Now, Now)).Single().Id;

        (await _repository.ReleaseBlockAsync(id, "admin", Now)).Should().BeTrue();
        (await _repository.ReleaseBlockAsync(id, "admin", Now)).Should().BeFalse();

        var block = await _repository.GetBlockAsync(id);
        block!.ReleasedBy.Should().Be("admin");
        block.IsActive(Now).Should().BeFalse();
    }

    [Fact]
    public async Task StrikeCounts_CountBlocksPerSourceSince()
    {
        await _repository.AddBlocksAsync(
        [
            Block("203.0.113.1", Now.AddDays(-1), 60),
            Block("203.0.113.1", Now.AddDays(-5), 60),
            Block("203.0.113.1", Now.AddDays(-40), 60),                    // outside the strike window
        ]);

        var counts = await _repository.GetBlockCountsBySourceAsync(Now.AddDays(-30));

        counts["203.0.113.1"].Should().Be(2);
    }

    [Fact]
    public async Task Prune_RemovesOnlyBlocksThatEndedBeforeTheCutoff()
    {
        await _repository.AddBlocksAsync(
        [
            Block("203.0.113.1", Now.AddDays(-100), 60),                   // ended long ago
            Block("203.0.113.2", Now.AddMinutes(-10), 60),                 // active
        ]);

        (await _repository.PruneBlocksAsync(Now.AddDays(-90))).Should().Be(1);
        (await _repository.GetBlocksAsync(Now, Now.AddYears(-1))).Should().ContainSingle().Which.SourceIp.Should().Be("203.0.113.2");
    }
}
