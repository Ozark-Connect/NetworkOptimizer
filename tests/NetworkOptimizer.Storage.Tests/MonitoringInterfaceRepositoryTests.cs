using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>
/// Uses SQLite in-memory (not the EF InMemory provider) because the repository's uniqueness
/// check runs inside a real transaction (BEGIN IMMEDIATE) and relies on real SQLite constraint
/// and query semantics - the EF InMemory provider models neither, so it can't exercise this.
/// </summary>
public class MonitoringInterfaceRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NetworkOptimizerDbContext _context;
    private readonly MonitoringInterfaceRepository _repository;

    public MonitoringInterfaceRepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new NetworkOptimizerDbContext(options);
        _context.Database.EnsureCreated();

        var logger = new Mock<ILogger<MonitoringInterfaceRepository>>();
        _repository = new MonitoringInterfaceRepository(_context, logger.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private static MonitoringInterface Valid(string name, string wanIf, string targetIp,
        string localIp, string? aliasIp = null) => new()
    {
        Name = name,
        WanIfName = wanIf,
        TargetIp = targetIp,
        GatewayLocalIp = localIp,
        AliasIp = aliasIp,
        SubnetPrefix = 24,
        WatchdogIntervalMinutes = 5,
    };

    [Fact]
    public async Task SaveMonitoringInterfaceAsync_TwoRowsSameTargetDifferentAlias_BothSucceed()
    {
        // This is the canonical duplicate-IP-WAN scenario: two rows sharing TargetIp,
        // distinguished by AliasIp - must NOT be rejected.
        await _repository.SaveMonitoringInterfaceAsync(Valid("ont0", "eth4", "192.168.100.1", "192.168.100.2"));
        var act = () => _repository.SaveMonitoringInterfaceAsync(
            Valid("starlink0", "eth2", "192.168.100.1", "192.168.100.3", aliasIp: "192.168.101.1"));

        await act.Should().NotThrowAsync();

        var all = await _repository.GetMonitoringInterfacesAsync();
        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task SaveMonitoringInterfaceAsync_DuplicateEffectiveIp_Throws()
    {
        await _repository.SaveMonitoringInterfaceAsync(Valid("ont0", "eth4", "192.168.100.1", "192.168.100.2"));

        // Second row's AliasIp equals the first row's effective IP (TargetIp, since it has
        // no alias) - this IS a real collision and must be rejected.
        var act = () => _repository.SaveMonitoringInterfaceAsync(
            Valid("dup0", "eth2", "10.0.0.1", "10.0.0.2", aliasIp: "192.168.100.1"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SaveMonitoringInterfaceAsync_DuplicateGatewayLocalIp_Throws()
    {
        await _repository.SaveMonitoringInterfaceAsync(Valid("ont0", "eth4", "192.168.100.1", "192.168.100.2"));

        var act = () => _repository.SaveMonitoringInterfaceAsync(
            Valid("starlink0", "eth2", "10.0.0.1", "192.168.100.2"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SaveMonitoringInterfaceAsync_DuplicateName_Throws()
    {
        // Two different WANs' devices, no IP overlap at all - only the Name collides. This is
        // the default-form-value collision a user hits every time they add a second interface
        // without renaming it from "modem0" - must get a friendly message, not a raw SQLite
        // UNIQUE-constraint exception.
        await _repository.SaveMonitoringInterfaceAsync(Valid("modem0", "eth4", "192.168.100.1", "192.168.100.2"));

        var act = () => _repository.SaveMonitoringInterfaceAsync(
            Valid("modem0", "eth2", "10.0.0.1", "10.0.0.2"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*modem0*");
    }

    [Fact]
    public async Task SaveMonitoringInterfaceAsync_EditingExistingRowToSameValues_DoesNotThrow()
    {
        // The uniqueness check must self-exclude the row's own Id, or simply re-saving an
        // already-valid row (e.g. toggling an unrelated field) would falsely trip the gate.
        await _repository.SaveMonitoringInterfaceAsync(Valid("ont0", "eth4", "192.168.100.1", "192.168.100.2"));
        var saved = (await _repository.GetMonitoringInterfacesAsync()).Single();

        saved.WatchdogIntervalMinutes = 10;
        var act = () => _repository.SaveMonitoringInterfaceAsync(saved);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SaveMonitoringInterfaceAsync_NewGatewayLocalIpEqualsExistingTargetIp_Throws()
    {
        // Existing row polls 192.168.100.1 (its TargetIp). A new row whose gateway-local
        // (macvlan) address is that same 192.168.100.1 must be rejected: local addresses win
        // over routes, so the existing row would poll this gateway macvlan instead of its device.
        await _repository.SaveMonitoringInterfaceAsync(Valid("ont0", "eth4", "192.168.100.1", "192.168.100.2"));

        var act = () => _repository.SaveMonitoringInterfaceAsync(
            Valid("starlink0", "eth2", "10.0.0.1", "192.168.100.1"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SaveMonitoringInterfaceAsync_NewTargetIpEqualsExistingGatewayLocalIp_Throws()
    {
        // Existing row's gateway-local (macvlan) address is 192.168.100.2. A new row that tries
        // to poll 192.168.100.2 as its TargetIp must be rejected: it would poll the other
        // interface's gateway macvlan instead of a real remote device.
        await _repository.SaveMonitoringInterfaceAsync(Valid("ont0", "eth4", "192.168.100.1", "192.168.100.2"));

        var act = () => _repository.SaveMonitoringInterfaceAsync(
            Valid("starlink0", "eth2", "192.168.100.2", "10.0.0.2"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetByEffectiveIpAsync_MatchesAliasRowByItsAlias_NotByTargetIp()
    {
        await _repository.SaveMonitoringInterfaceAsync(Valid("ont0", "eth4", "192.168.100.1", "192.168.100.2"));
        await _repository.SaveMonitoringInterfaceAsync(
            Valid("starlink0", "eth2", "192.168.100.1", "192.168.100.3", aliasIp: "192.168.101.1"));

        var byTarget = await _repository.GetByEffectiveIpAsync("192.168.100.1");
        var byAlias = await _repository.GetByEffectiveIpAsync("192.168.101.1");

        byTarget!.Name.Should().Be("ont0");
        byAlias!.Name.Should().Be("starlink0");
    }
}
