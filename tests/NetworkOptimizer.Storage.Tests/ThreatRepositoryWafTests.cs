using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.Threats.Models;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class ThreatRepositoryWafTests : IDisposable
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ThreatRepository _repository;
    private readonly DateTime _now = DateTime.UtcNow;

    public ThreatRepositoryWafTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new NetworkOptimizerDbContext(options);
        _repository = new ThreatRepository(_context, new Mock<ILogger<ThreatRepository>>().Object);
    }

    public void Dispose() => _context.Dispose();

    private ThreatEvent Waf(string sourceIp, long ruleId, string host, ThreatAction action, EventSource source = EventSource.Waf) => new()
    {
        Timestamp = _now,
        SourceIp = sourceIp,
        DestIp = "",
        DestPort = 443,
        Protocol = "HTTP",
        SignatureId = ruleId,
        SignatureName = $"Rule {ruleId}",
        Category = "attack-sqli",
        Severity = 4,
        Action = action,
        EventSource = source,
        Domain = host,
        InnerAlertId = Guid.NewGuid().ToString()
    };

    [Fact]
    public async Task Summary_CountsOnlyWafEvents_AndRanksRulesAndHosts()
    {
        _context.ThreatEvents.AddRange(
            Waf("203.0.113.1", 942100, "a.example.com", ThreatAction.Blocked),
            Waf("203.0.113.2", 942100, "a.example.com", ThreatAction.Detected),
            Waf("203.0.113.3", 930120, "b.example.com", ThreatAction.Detected),
            Waf("203.0.113.4", 2_000_001, "ignored.example.com", ThreatAction.Blocked, EventSource.Ips));
        await _context.SaveChangesAsync();

        var summary = await _repository.GetWafSummaryAsync(_now.AddHours(-1), _now.AddHours(1));

        summary.Total.Should().Be(3);
        summary.Blocked.Should().Be(1);
        summary.Detected.Should().Be(2);
        summary.TopRules.Should().HaveCount(2);
        summary.TopRules[0].RuleId.Should().Be(942100);
        summary.TopRules[0].Count.Should().Be(2);
        summary.TopHosts[0].Host.Should().Be("a.example.com");
        summary.TopHosts.Should().NotContain(h => h.Host == "ignored.example.com");
    }

    [Fact]
    public async Task Summary_HonorsNoiseFilters()
    {
        _context.ThreatEvents.AddRange(
            Waf("203.0.113.1", 942100, "a.example.com", ThreatAction.Blocked),
            Waf("198.51.100.7", 942100, "a.example.com", ThreatAction.Blocked));
        await _context.SaveChangesAsync();
        _repository.SetNoiseFilters([new ThreatNoiseFilter { SourceIp = "198.51.100.7", Enabled = true }]);

        var summary = await _repository.GetWafSummaryAsync(_now.AddHours(-1), _now.AddHours(1));

        summary.Total.Should().Be(1);
    }
}
