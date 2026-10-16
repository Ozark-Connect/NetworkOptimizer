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
        Add("198.51.100.2", "TCP", 443, 54321, KillChainStage.Reconnaissance);
        Add("198.51.100.2", "TCP", 443, 54321, KillChainStage.Monitored);
        await _context.SaveChangesAsync();

        (await _repository.GetAttackSequencesAsync(_now.AddHours(-1), _now)).Should().ContainSingle()
            .Which.Stages.Should().HaveCount(2);
    }

    [Fact]
    public async Task PublicListContact_FormsNoChainWithTheDevicesOtherTraffic()
    {
        Add("192.0.2.10", "TCP", 40000, 443, KillChainStage.PostExploitation);
        _context.ThreatEvents.Local.Last().Category = NetworkOptimizer.Threats.Blocking.PublicListContact.Category;
        Add("192.0.2.10", "UDP", 40500, 55000, KillChainStage.Monitored);
        Add("192.0.2.10", "TCP", 40001, 22, KillChainStage.Monitored);
        await _context.SaveChangesAsync();

        (await _repository.GetAttackSequencesAsync(_now.AddHours(-1), _now)).Should().BeEmpty();
    }

    [Fact]
    public async Task PublicListContacts_GroupedPerDeviceAndAddress_ConnectedFirst()
    {
        void Contact(string device, string address, ThreatAction action, string list)
        {
            Add(device, "TCP", 40000, 443, KillChainStage.PostExploitation);
            var e = _context.ThreatEvents.Local.Last();
            e.DestIp = address;
            e.Action = action;
            e.Category = NetworkOptimizer.Threats.Blocking.PublicListContact.Category;
            e.SignatureName = (action == ThreatAction.Blocked ? "Tried to reach an address on " : "Reached an address on ") + list;
        }
        Contact("192.0.2.10", "198.51.100.7", ThreatAction.Blocked, "Spamhaus DROP");
        Contact("192.0.2.10", "198.51.100.7", ThreatAction.Blocked, "Spamhaus DROP");
        Contact("192.0.2.11", "203.0.113.9", ThreatAction.Detected, "abuse.ch Feodo Tracker");
        Add("192.0.2.12", "TCP", 40000, 22, KillChainStage.Monitored);
        await _context.SaveChangesAsync();

        var contacts = await _repository.GetPublicListContactsAsync(_now.AddHours(-1), _now);

        contacts.Should().HaveCount(2);
        contacts[0].Should().Match<PublicListContactSummary>(c => c.Connected && c.DeviceIp == "192.0.2.11" && c.List == "abuse.ch Feodo Tracker");
        contacts[1].Should().Match<PublicListContactSummary>(c => !c.Connected && c.Events == 2 && c.List == "Spamhaus DROP");
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
