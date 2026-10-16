using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.Threats.Models;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>Attack sequences: which events can form a kill chain.</summary>
public class ThreatRepositorySequenceTests : IDisposable
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ThreatRepository _repository;
    private readonly DateTime _now = DateTime.UtcNow;

    public ThreatRepositorySequenceTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new NetworkOptimizerDbContext(options);
        _repository = new ThreatRepository(_context, new Mock<ILogger<ThreatRepository>>().Object);
    }

    public void Dispose() => _context.Dispose();

    private void Add(string ip, string protocol, int sourcePort, int destPort, KillChainStage stage)
    {
        _context.ThreatEvents.Add(new ThreatEvent
        {
            Timestamp = _now.AddMinutes(-5),
            SourceIp = ip,
            SourcePort = sourcePort,
            DestIp = "203.0.113.10",
            DestPort = destPort,
            Protocol = protocol,
            SignatureName = "test",
            Category = "test",
            Severity = 2,
            Action = ThreatAction.Blocked,
            InnerAlertId = Guid.NewGuid().ToString(),
            KillChainStage = stage,
        });
    }

    [Fact]
    public async Task UdpRepliesFromServerPorts_FormNoChain()
    {
        Add("198.51.100.1", "UDP", 443, 61635, KillChainStage.Reconnaissance);
        Add("198.51.100.1", "UDP", 443, 54976, KillChainStage.Monitored);
        await _context.SaveChangesAsync();

        (await _repository.GetAttackSequencesAsync(_now.AddHours(-1), _now)).Should().BeEmpty();
    }

    [Fact]
    public async Task TcpFromPort443_StillFormsAChain()
    {
        Add("198.51.100.2", "TCP", 443, 59469, KillChainStage.Reconnaissance);
        Add("198.51.100.2", "TCP", 443, 59469, KillChainStage.Monitored);
        await _context.SaveChangesAsync();

        (await _repository.GetAttackSequencesAsync(_now.AddHours(-1), _now)).Should().ContainSingle()
            .Which.Stages.Should().HaveCount(2);
    }

    [Fact]
    public async Task UdpToAServicePort_StillFormsAChain()
    {
        Add("198.51.100.3", "UDP", 443, 161, KillChainStage.Reconnaissance);
        Add("198.51.100.3", "UDP", 40000, 1900, KillChainStage.Monitored);
        await _context.SaveChangesAsync();

        (await _repository.GetAttackSequencesAsync(_now.AddHours(-1), _now)).Should().ContainSingle();
    }
}
